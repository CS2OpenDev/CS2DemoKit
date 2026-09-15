#region

using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.ExceptionServices;
using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Analysis.Plugins;
using CS2DemoKit.Parser;
using CS2DemoKit.Parser.Entities;
using CS2OpenSchema.Protos;

#endregion

namespace CS2DemoKit.Analysis;

/// <summary>
///     The checkpoint-parallel digest producer for a stream. Sits between a forward source and the
///     evaluator: a reader thread pulls frames from the source into chunks that start at a
///     <c>DEM_FullPacket</c>, hands each chunk to a worker that primes its own tracker from that
///     checkpoint and folds the chunk's digests, and queues the chunk for the evaluator, which
///     takes them in order. What <see cref="ParallelDigestProducer" /> does up front over a
///     retained list, done over a bounded window of a stream: the same prime, the same tick-gated
///     seek, the same extractor, so the digests fold to the same values. The read runs on its own
///     thread so decoding overlaps the evaluator's dispatch instead of taking turns with it.
///     <para>
///         Live memory is the unconsumed part of the current chunk plus up to the worker count of
///         chunks queued ahead, the chunk being assembled, and one tracker per worker. A tracker is
///         primed with the schema once and re-primed from each later checkpoint with its entities
///         cleared, which is what keeps a chunk small enough to hold several. The consumer's
///         roster view is pinned to the frame it has read, or the one it has peeked, exactly as
///         the sequential reader exposes it, so a materialisation reads the same name on both
///         producers.
///     </para>
///     <para>
///         Threads: the inner source is touched by the reader thread alone once reading starts;
///         the queue is guarded by one lock; chunk contents are published to the consumer through
///         that lock and to the fold worker through the task start; a fold releases only frames
///         the consumer has not reached, which the queue order guarantees.
///     </para>
/// </summary>
internal sealed class PipelinedDigestSource : IDemoFrameSource
{
    // A chunk closes at the first candidate full packet after this many frames. Smaller chunks
    // hold less ahead of the loop; each costs one checkpoint prime on a tracker that already
    // carries the schema.
    internal const int MinChunkFrames = 1024;

    private readonly IDemoFrameSource _inner;
    private readonly Func<IReadOnlyList<IPerPlayerEntityValueProvider>> _perPlayerFactory;
    private readonly Func<IReadOnlyList<IEntityValueProvider>> _singletonFactory;
    private readonly bool _emitMolotov;
    private readonly bool _captureSmokes;
    private readonly int _workers;
    private readonly Action<IReadOnlyList<DemoFrame>>? _onFirstChunk;
    private readonly CancellationTokenSource _cts;
    private readonly CancellationToken _token;
    private readonly ConcurrentBag<Worker> _pool = [];
    private readonly List<DemoFrame> _signonPrefix = [];
    private readonly IReadOnlyDictionary<int, PlayerInfo> _initialPlayers;
    private readonly View _view;

    // Consumer thread only.
    private Chunk? _current;
    private int _currentPos;
    private int _yieldedIndex = -1;
    private int _peekedIndex = -1;

    // Reader thread only.
    private Chunk? _pending;
    private int _nextReadIndex;
    private bool _prefixDone;
    private bool _firstFullPacketSeen;

    // Shared, under _gate.
    private readonly object _gate = new();
    private readonly Queue<Chunk> _ahead = new();
    private Thread? _reader;
    private bool _readerDone;
    private ExceptionDispatchInfo? _readerError;
    private bool _disposed;

    /// <param name="inner">The stream to read; consumed by this source alone from here on.</param>
    /// <param name="perPlayerFactory">Fresh per-player providers for one worker, in the layout's order.</param>
    /// <param name="singletonFactory">Fresh singleton providers for one worker, in the same order contract.</param>
    /// <param name="emitMolotov">Whether digests carry live molotov projectiles.</param>
    /// <param name="captureSmokes">Whether digests carry the frame's active smoke clouds.</param>
    /// <param name="workers">Chunks queued ahead of the consumer, and the worker count.</param>
    /// <param name="onFirstChunk">Runs on the reader thread with the first chunk's frames once it closes, before its fold starts.</param>
    /// <param name="cancellationToken">Cancels the read-ahead and every worker.</param>
    public PipelinedDigestSource(
        IDemoFrameSource inner,
        Func<IReadOnlyList<IPerPlayerEntityValueProvider>> perPlayerFactory,
        Func<IReadOnlyList<IEntityValueProvider>> singletonFactory,
        bool emitMolotov,
        bool captureSmokes,
        int workers,
        Action<IReadOnlyList<DemoFrame>>? onFirstChunk,
        CancellationToken cancellationToken)
    {
        _inner = inner;
        _perPlayerFactory = perPlayerFactory;
        _singletonFactory = singletonFactory;
        _emitMolotov = emitMolotov;
        _captureSmokes = captureSmokes;
        _workers = Math.Max(1, workers);
        _onFirstChunk = onFirstChunk;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _token = _cts.Token;
        _initialPlayers = inner.Enrichment.Players;
        _view = new View(this);

        // Bootstrapped here, on the consumer's thread, as the list producer does before it fans
        // out. A fold that finds the pool empty makes its own; the registries are initialised
        // by then.
        for (int i = 0; i < _workers; i++)
        {
            _pool.Add(new Worker());
        }
    }

    public IDemoEnrichmentView Enrichment => _view;

    public int? FrameCount => null;

    public double? Progress => _inner.Progress;

    public bool SupportsRandomAccess => false;

    public IReadOnlyList<DemoFrame>? Frames => null;

    public IReadOnlyList<DemoFrame> SignonPrefix => _signonPrefix;

    public DemoFrame? LastInstanceBaselineFullPacket => _inner.LastInstanceBaselineFullPacket;

    public bool TryReadNext([NotNullWhen(true)] out DemoFrame? frame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_current is null || _currentPos >= _current.Frames.Count)
        {
            _current = TakeChunk();
            _currentPos = 0;
            if (_current is null)
            {
                frame = null;
                return false;
            }
        }

        frame = _current.Frames[_currentPos];
        _yieldedIndex = _current.FirstFrameIndex + _currentPos;
        _currentPos++;
        _peekedIndex = -1;
        return true;
    }

    public bool TryPeekNext([NotNullWhen(true)] out DemoFrame? frame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_current is not null && _currentPos < _current.Frames.Count)
        {
            frame = _current.Frames[_currentPos];
            _peekedIndex = _current.FirstFrameIndex + _currentPos;
            return true;
        }

        Chunk? next = PeekChunk();
        if (next is null)
        {
            frame = null;
            return false;
        }

        frame = next.Frames[0];
        _peekedIndex = next.FirstFrameIndex;
        return true;
    }

    /// <summary>
    ///     The digest for <paramref name="frameIndex" />, which must lie in the chunk the consumer is
    ///     reading. Blocks until that chunk's worker has finished and rethrows what it threw.
    /// </summary>
    public EntityFrameDigest Take(int frameIndex)
    {
        Chunk chunk = _current ?? throw new InvalidOperationException("No frame has been read from the pipelined source.");
        int i = frameIndex - chunk.FirstFrameIndex;
        if (i < 0 || i >= chunk.Frames.Count)
        {
            throw new InvalidOperationException(
                $"Frame {frameIndex} is outside the chunk being consumed [{chunk.FirstFrameIndex}, {chunk.FirstFrameIndex + chunk.Frames.Count}).");
        }

        EntityFrameDigest[] digests = chunk.Digests!.GetAwaiter().GetResult();
        EntityFrameDigest digest = digests[i];
        digests[i] = null!;
        chunk.Frames[i] = null!;
        return digest;
    }

    /// <summary>Stops the reader thread, cancels every chunk still decoding, and joins. Not reusable.</summary>
    public void Close()
    {
        Thread? reader;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            reader = _reader;
            Monitor.PulseAll(_gate);
        }

        _cts.Cancel();
        reader?.Join();
        lock (_gate)
        {
            foreach (Chunk chunk in _ahead)
            {
                Observe(chunk.Digests);
            }

            _ahead.Clear();
        }

        Observe(_current?.Digests);
        _current = null;
        _pending = null;
    }

    private static void Observe(Task? task) =>
        task?.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);

    // The reader thread starts on the first read, so a source built and never read costs no thread.
    private void EnsureReading()
    {
        lock (_gate)
        {
            if (_reader is not null || _readerDone)
            {
                return;
            }

            _reader = new Thread(ReadAll) { IsBackground = true, Name = "cs2demokit-pipeline-reader" };
            _reader.Start();
        }
    }

    private void ReadAll()
    {
        try
        {
            while (true)
            {
                Chunk? chunk = ReadChunk(out bool more);
                if (chunk is not null)
                {
                    lock (_gate)
                    {
                        while (_ahead.Count >= _workers && !_disposed)
                        {
                            Monitor.Wait(_gate);
                        }

                        if (_disposed)
                        {
                            Observe(chunk.Digests);
                            return;
                        }

                        _ahead.Enqueue(chunk);
                        Monitor.PulseAll(_gate);
                    }
                }

                if (!more)
                {
                    return;
                }
            }
        }
        catch (Exception e)
        {
            lock (_gate)
            {
                _readerError = ExceptionDispatchInfo.Capture(e);
            }
        }
        finally
        {
            lock (_gate)
            {
                _readerDone = true;
                Monitor.PulseAll(_gate);
            }
        }
    }

    private Chunk? TakeChunk()
    {
        EnsureReading();
        lock (_gate)
        {
            while (_ahead.Count == 0 && !_readerDone)
            {
                Monitor.Wait(_gate);
            }

            _readerError?.Throw();
            if (_ahead.Count == 0)
            {
                return null;
            }

            Chunk chunk = _ahead.Dequeue();
            Monitor.PulseAll(_gate);
            return chunk;
        }
    }

    private Chunk? PeekChunk()
    {
        EnsureReading();
        lock (_gate)
        {
            while (_ahead.Count == 0 && !_readerDone)
            {
                Monitor.Wait(_gate);
            }

            _readerError?.Throw();
            return _ahead.Count == 0 ? null : _ahead.Peek();
        }
    }

    // Assembles the next chunk from the inner source. Returns null with more=false when the
    // source is exhausted and nothing was assembled.
    private Chunk? ReadChunk(out bool more)
    {
        Chunk chunk = _pending ?? new Chunk { FirstFrameIndex = _nextReadIndex };
        _pending = null;
        more = true;
        while (true)
        {
            _token.ThrowIfCancellationRequested();
            if (!_inner.TryReadNext(out DemoFrame? frame))
            {
                more = false;
                break;
            }

            // Both read before any peek, which would move the inner view one frame on.
            IReadOnlyDictionary<int, PlayerInfo> players = _inner.Enrichment.Players;
            DemoFrame? instanceBaseline = _inner.LastInstanceBaselineFullPacket;
            int index = _nextReadIndex++;

            if (!_prefixDone)
            {
                if (frame.CommandKind == EDemoCommands.DemPacket)
                {
                    _prefixDone = true;
                }
                else
                {
                    _signonPrefix.Add(frame);
                    chunk.PrefixFrames++;
                }
            }

            if (frame.CommandKind == EDemoCommands.DemFullPacket)
            {
                // The signon full packet is never a checkpoint, and neither is one whose successor
                // shares its tick: a prime leaves the tracker at the checkpoint tick, so the
                // successor's delta would be skipped where a sequential fold applies it.
                if (!_firstFullPacketSeen)
                {
                    _firstFullPacketSeen = true;
                }
                else if (_prefixDone && chunk.Frames.Count >= MinChunkFrames
                         && !(_inner.TryPeekNext(out DemoFrame? next) && next.ServerTick == frame.ServerTick))
                {
                    // The new chunk opens at the start of the checkpoint's tick run, so no run
                    // straddles a boundary. Frames of that run before the checkpoint read the
                    // primed state, which is what a tick-gated seek gives them.
                    int runStart = chunk.Frames.Count;
                    while (runStart > 0 && chunk.Frames[runStart - 1].ServerTick == frame.ServerTick)
                    {
                        runStart--;
                    }

                    _pending = new Chunk
                    {
                        FirstFrameIndex = index - (chunk.Frames.Count - runStart),
                        Checkpoint = frame,
                        InstanceBaseline = instanceBaseline
                    };
                    for (int i = runStart; i < chunk.Frames.Count; i++)
                    {
                        _pending.Frames.Add(chunk.Frames[i]);
                        _pending.PlayersAfter.Add(chunk.PlayersAfter[i]);
                    }

                    chunk.Frames.RemoveRange(runStart, chunk.Frames.Count - runStart);
                    chunk.PlayersAfter.RemoveRange(runStart, chunk.PlayersAfter.Count - runStart);
                    _pending.Frames.Add(frame);
                    _pending.PlayersAfter.Add(players);
                    break;
                }
            }

            chunk.Frames.Add(frame);
            chunk.PlayersAfter.Add(players);
        }

        if (chunk.Frames.Count == 0)
        {
            return null;
        }

        // Before the fold starts, while the frames are whole and the consumer is still waiting.
        if (chunk.Checkpoint is null)
        {
            _onFirstChunk?.Invoke(chunk.Frames);
        }

        chunk.Digests = Task.Run(() => Fold(chunk), _token);
        return chunk;
    }

    private EntityFrameDigest[] Fold(Chunk chunk)
    {
        Worker worker = TakeWorker(fresh: chunk.Checkpoint is null);
        try
        {
            return Fold(chunk, worker);
        }
        finally
        {
            _pool.Add(worker);
        }
    }

    // Chunk 0 folds the signon prefix as ordinary frames, so it needs a tracker that has never
    // loaded a schema; every other chunk takes whichever worker is free.
    private Worker TakeWorker(bool fresh)
    {
        List<Worker> passed = [];
        Worker? taken = null;
        while (_pool.TryTake(out Worker? candidate))
        {
            if (!fresh || !candidate.SchemaLoaded)
            {
                taken = candidate;
                break;
            }

            passed.Add(candidate);
        }

        foreach (Worker w in passed)
        {
            _pool.Add(w);
        }

        return taken ?? new Worker();
    }

    // The tick-gated seek a list-backed layer runs, over the chunk's own frames: every frame
    // not yet applied whose tick is at most the current frame's is applied before the digest.
    private EntityFrameDigest[] Fold(Chunk chunk, Worker worker)
    {
        List<DemoFrame> frames = chunk.Frames;
        EntityStateLayer layer = worker.Layer;
        IReadOnlyList<IPerPlayerEntityValueProvider> perPlayer = _perPlayerFactory();
        IReadOnlyList<IEntityValueProvider> singletons = _singletonFactory();
        PerPawnDeltaState delta = new(DigestColumnLayout.For(perPlayer));

        int next = 0;
        if (chunk.Checkpoint is not null)
        {
            int at = 0;
            while (!ReferenceEquals(frames[at], chunk.Checkpoint))
            {
                at++;
            }

            // The schema comes from the signon prefix once; a re-primed tracker keeps it and only
            // needs its entities cleared and the checkpoint state loaded.
            layer.PrimeFromCheckpoint(worker.SchemaLoaded ? [] : _signonPrefix, chunk.InstanceBaseline, chunk.Checkpoint,
                at + 1 < frames.Count ? frames[at + 1] : null);
            next = at + 1;

            // The run's frames before the checkpoint are covered by its snapshot and never applied
            // here; the sequential producer applies and releases them, so release them too.
            for (int i = 0; i < at; i++)
            {
                frames[i].Release(FoldedCategories(frames[i]));
            }

        }

        worker.SchemaLoaded = true;
        EntityFrameDigest[] digests = new EntityFrameDigest[frames.Count];
        for (int n = 0; n < frames.Count; n++)
        {
            _token.ThrowIfCancellationRequested();
            int target = frames[n].ServerTick;
            if (layer.CurrentTick < target)
            {
                while (next < frames.Count && frames[next].ServerTick <= target)
                {
                    DemoFrame applied = frames[next];
                    layer.Apply(applied);
                    // Folded, so its entity and string-table bytes are dead weight for everyone
                    // downstream. Two exceptions keep the two producers dispatching the same
                    // messages: the signon prefix, which later workers prime from, and a frame
                    // the tick gate deferred past its own position, which the sequential producer
                    // dispatches before it has folded it.
                    if (next >= chunk.PrefixFrames && next >= n)
                    {
                        applied.Release(FoldedCategories(applied));
                    }

                    next++;
                }
            }

            digests[n] = EntityDigestExtractor.Build(layer, delta, singletons, _emitMolotov, _captureSmokes, worker.Projectiles);
        }

        return digests;
    }

    // A full packet is never released: another worker may be reading its string tables to
    // prime baselines while this one has finished with it, and RemoveAll under an enumeration
    // throws. There are a few dozen per demo and each lives only while its chunk is in flight.
    internal static MessageCategories FoldedCategories(DemoFrame frame) =>
        frame.CommandKind == EDemoCommands.DemFullPacket
            ? MessageCategories.None
            : MessageCategories.Entities | MessageCategories.StringTables;

    // Never the inner view itself: the reader thread is moving it.
    private IReadOnlyDictionary<int, PlayerInfo> PlayersAt(int index)
    {
        if (index < 0)
        {
            return _initialPlayers;
        }

        if (_current is { } current && Holds(current, index))
        {
            return current.PlayersAfter[index - current.FirstFrameIndex];
        }

        lock (_gate)
        {
            foreach (Chunk chunk in _ahead)
            {
                if (Holds(chunk, index))
                {
                    return chunk.PlayersAfter[index - chunk.FirstFrameIndex];
                }
            }
        }

        return _current is { } last && last.PlayersAfter.Count > 0 ? last.PlayersAfter[^1] : _initialPlayers;
    }

    private static bool Holds(Chunk chunk, int index) =>
        index >= chunk.FirstFrameIndex && index < chunk.FirstFrameIndex + chunk.Frames.Count;

    private sealed class Chunk
    {
        public readonly List<DemoFrame> Frames = [];
        public readonly List<IReadOnlyDictionary<int, PlayerInfo>> PlayersAfter = [];
        public DemoFrame? Checkpoint;
        public DemoFrame? InstanceBaseline;
        public int FirstFrameIndex;
        public int PrefixFrames;
        public Task<EntityFrameDigest[]>? Digests;
    }

    // One tracker and one projectile index per worker, reused chunk after chunk. The index
    // stays bound to its tracker across a re-prime: stale slots are pruned on the next sync and
    // the checkpoint's creates are inserted as they fire.
    private sealed class Worker
    {
        public readonly EntityStateLayer Layer = new();
        public readonly ProjectileSlotIndex Projectiles = new();
        public bool SchemaLoaded;
    }

    private sealed class View(PipelinedDigestSource source) : IDemoEnrichmentView
    {
        private IDemoEnrichmentView Inner => source._inner.Enrichment;

        public int TickRate => Inner.TickRate;

        public float TickInterval => Inner.TickInterval;

        public string MapName => Inner.MapName;

        public string ServerName => Inner.ServerName;

        public string ClientName => Inner.ClientName;

        public int BuildNumber => Inner.BuildNumber;

        public int ServerStartTick => Inner.ServerStartTick;

        public int TickCount => Inner.TickCount;

        public bool TickCountIsFinal => Inner.TickCountIsFinal;

        public DemoProfile Profile => Inner.Profile;

        public RuntimeSchema? Schema => Inner.Schema;

        public IReadOnlyDictionary<int, PlayerInfo> Players =>
            source.PlayersAt(source._peekedIndex >= 0 ? source._peekedIndex : source._yieldedIndex);

        public IReadOnlyList<ParseWarning> Warnings => Inner.Warnings;

        public ParseHealth Health => Inner.Health;

        public DemoDescriptor Snapshot() => Inner.Snapshot();
    }
}
