#region

using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Analysis.Plugins;
using CS2DemoKit.Parser;
using CS2DemoKit.Parser.Entities;
using CS2OpenSchema.Protos;

#endregion

namespace CS2DemoKit.Analysis;

/// <summary>
///     The checkpoint-parallel digest producer for a stream. Sits between a forward source and the
///     evaluator: reads frames ahead in chunks that start at a <c>DEM_FullPacket</c>, hands each
///     chunk to a worker that primes its own tracker from that checkpoint and folds the chunk's
///     digests, and yields the frames to the evaluator in order while the workers run ahead. What
///     <see cref="ParallelDigestProducer" /> does up front over a retained list, done over a
///     bounded window of a stream: the same prime, the same tick-gated seek, the same extractor,
///     so the digests fold to the same values.
///     <para>
///         Live memory is the unconsumed part of the current chunk plus up to the worker count of
///         chunks decoded ahead, and one tracker per worker. A tracker is primed with the schema
///         once and re-primed from each later checkpoint with its entities cleared, which is what
///         keeps a chunk small enough to hold several. The consumer's roster view is pinned to
///         the frame it has read, or the one it has peeked, exactly as the sequential reader
///         exposes it, so a materialisation reads the same name on both producers.
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
    private readonly Queue<Chunk> _ahead = new();
    private readonly ConcurrentBag<Worker> _pool = [];
    private readonly List<DemoFrame> _signonPrefix = [];
    private readonly View _view;
    private Chunk? _current;
    private Chunk? _pending;
    private int _currentPos;
    private int _nextReadIndex;
    private int _yieldedIndex = -1;
    private int _peekedIndex = -1;
    private bool _innerDone;
    private bool _prefixDone;
    private bool _firstFullPacketSeen;
    private bool _disposed;

    /// <param name="inner">The stream to read; consumed by this source alone from here on.</param>
    /// <param name="perPlayerFactory">Fresh per-player providers for one worker, in the layout's order.</param>
    /// <param name="singletonFactory">Fresh singleton providers for one worker, in the same order contract.</param>
    /// <param name="emitMolotov">Whether digests carry live molotov projectiles.</param>
    /// <param name="captureSmokes">Whether digests carry the frame's active smoke clouds.</param>
    /// <param name="workers">Chunks decoded ahead of the consumer, and the worker count.</param>
    /// <param name="onFirstChunk">Runs on the consumer's thread with the first chunk's frames once it closes.</param>
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
        if ((_current is null || _currentPos >= _current.Frames.Count) && !Advance())
        {
            frame = null;
            return false;
        }

        frame = _current!.Frames[_currentPos];
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

        EnsureAhead();
        if (_ahead.Count == 0)
        {
            frame = null;
            return false;
        }

        Chunk next = _ahead.Peek();
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

    /// <summary>Stops reading ahead and cancels every chunk still decoding. Not reusable.</summary>
    public void Close()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cts.Cancel();
        foreach (Chunk chunk in _ahead)
        {
            Observe(chunk.Digests);
        }

        Observe(_current?.Digests);
        _ahead.Clear();
        _current = null;
        _pending = null;
    }

    private static void Observe(Task? task) =>
        task?.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);

    private bool Advance()
    {
        EnsureAhead();
        if (_ahead.Count == 0)
        {
            return false;
        }

        _current = _ahead.Dequeue();
        _currentPos = 0;
        EnsureAhead();
        return true;
    }

    // Keeps the worker count of chunks decoding ahead of the one being consumed.
    private void EnsureAhead()
    {
        while (!_innerDone && _ahead.Count < _workers + (_current is null ? 1 : 0))
        {
            ReadChunk();
        }
    }

    private void ReadChunk()
    {
        Chunk chunk = _pending ?? new Chunk { FirstFrameIndex = _nextReadIndex };
        _pending = null;
        while (true)
        {
            _token.ThrowIfCancellationRequested();
            if (!_inner.TryReadNext(out DemoFrame? frame))
            {
                _innerDone = true;
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
            return;
        }

        if (chunk.Checkpoint is null)
        {
            _onFirstChunk?.Invoke(chunk.Frames);
        }

        chunk.Digests = Task.Run(() => Fold(chunk), _token);
        _ahead.Enqueue(chunk);
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

    private IReadOnlyDictionary<int, PlayerInfo> PlayersAt(int index)
    {
        if (index < 0)
        {
            return _inner.Enrichment.Players;
        }

        if (_current is { } current && Holds(current, index))
        {
            return current.PlayersAfter[index - current.FirstFrameIndex];
        }

        foreach (Chunk chunk in _ahead)
        {
            if (Holds(chunk, index))
            {
                return chunk.PlayersAfter[index - chunk.FirstFrameIndex];
            }
        }

        return _inner.Enrichment.Players;
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
