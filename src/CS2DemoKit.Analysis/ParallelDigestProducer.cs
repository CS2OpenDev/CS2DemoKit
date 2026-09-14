#region

using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Analysis.Plugins;
using CS2DemoKit.Parser;

#endregion

namespace CS2DemoKit.Analysis;

/// <summary>
///     The parallel chunk decoder. Produces the per-frame
///     <see cref="EntityFrameDigest" /> array the scanner consumes, but decodes the entity stream in
///     parallel by splitting at <c>DEM_FullPacket</c> frames (each carries a complete entity snapshot, so a
///     worker can start there with no prior deltas). Every worker drives the SAME
///     <see cref="EntityStateLayer.SeekToTick" /> mechanism + the SAME <see cref="EntityDigestExtractor" />
///     the sequential scanner uses, just pre-positioned at its chunk's checkpoint via
///     <see cref="EntityStateLayer.PrimeFromCheckpoint" />, so the digest stream folds to the same
///     per-pawn snapshot a sequential decode produces, with singletons and molotovs identical frame for
///     frame (proven by <c>ParallelDigestEquivalenceTests</c>; sequential→golden is proven by Step 1, so
///     parallel→golden follows by composition). Per-pawn rows are deltas and each worker re-emits every
///     live cell on its chunk's first frame, so they match after the fold rather than row for row.
///     <para>
///         <b>Thread safety:</b> each worker owns its own <see cref="EntityStateLayer" /> (hence its own
///         <c>EntityTracker</c> + entity set) and its own provider instances (via the factories), and writes
///         only its chunk's disjoint slice of the shared <see cref="EntityFrameDigest" />[] — so there is no
///         shared mutable state across workers. The decode path itself was audited race-free; the providers
///         are created per-worker because <see cref="FreezePeriodProvider" /> caches a mutable entity index.
///     </para>
/// </summary>
internal static class ParallelDigestProducer
{
    // ── Parallel-decode alloc accounting (opt-in at RUNTIME via Profiling.Enabled) ─────────────────
    // The decode runs on Parallel.For worker threads, so a caller that brackets
    // GC.GetAllocatedBytesForCurrentThread() on its own (orchestrator) thread misses every worker
    // but the one it happens to run. Each worker brackets its chunk's allocation and folds it
    // into the box its own Produce call published. chunks.Count ≈ the chunk target (see
    // ResolveTargetChunks), so this is ≈ core-count Interlocked.Add total — no contention. The scanner
    // reads the sum via ReadWorkerAllocBytes() after Produce returns. The per-worker brackets are
    // guarded at runtime so a default decode pays nothing.
    // The box hangs off an AsyncLocal rather than a plain static because MaxDegreeOfParallelism exists
    // precisely so several demos can decode at once in one process, and a static accumulator cannot
    // survive that: the second Produce's zeroing discards the first's total, and from then on the two
    // runs add into each other. The box is published before the fork and read after the join within one
    // synchronous call, so every Produce reports its own decode and nobody else's.
    private static readonly AsyncLocal<StrongBox<long>?> _profWorkerAlloc = new();

    /// <summary>
    ///     Total per-worker decode allocation (bytes) accumulated by the <c>Produce</c> call this
    ///     execution context most recently made. Zero when that run had <see cref="Profiling.Enabled" /> off.
    /// </summary>
    internal static long ReadWorkerAllocBytes() =>
        _profWorkerAlloc.Value is { } box ? Interlocked.Read(ref box.Value) : 0;

    /// <summary>
    ///     Decodes the whole demo's entity stream in parallel and returns <c>digest[N]</c> for every frame
    ///     <c>N</c> — the post-seek <see cref="EntityFrameDigest" /> the sequential scanner would build at
    ///     frame <c>N</c>.
    /// </summary>
    /// <param name="frames">The demo's frame list.</param>
    /// <param name="perPlayerFactory">
    ///     Creates a fresh per-player provider list for one worker. MUST return the providers in the same
    ///     order on every call (the digest's per-pawn value arrays are indexed by this order).
    /// </param>
    /// <param name="singletonFactory">
    ///     Creates a fresh singleton provider list for one worker (same ordering contract as
    ///     <paramref name="perPlayerFactory" />). Fresh per worker because some singletons cache mutable
    ///     state.
    /// </param>
    /// <param name="emitMolotov">When true, each digest includes live <c>CMolotovProjectile</c>s.</param>
    /// <param name="captureSmokes">
    ///     When true, each digest carries the frame's active smoke clouds. The consumer that needs
    ///     them runs sequentially after this returns, by which point a worker's entity set is gone,
    ///     so they have to be collected here or not at all.
    /// </param>
    /// <param name="maxDegreeOfParallelism">Optional cap on concurrent workers (default: unbounded).</param>
    /// <param name="onProgress">
    ///     Optional fraction-complete callback (0..1), invoked once per chunk as it finishes. Called from
    ///     worker threads — the eval bridges it to its UI <c>IProgress</c>. ~core-count calls total.
    /// </param>
    /// <param name="cancellationToken">
    ///     Aborts the decode: workers observe it per frame, so cancel latency is one seek+digest. A
    ///     canceled produce throws <see cref="OperationCanceledException" /> — no partial digest array
    ///     is ever returned.
    /// </param>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken" /> was observed.</exception>
    internal static EntityFrameDigest[] Produce(
        IReadOnlyList<DemoFrame> frames,
        Func<IReadOnlyList<IPerPlayerEntityValueProvider>> perPlayerFactory,
        Func<IReadOnlyList<IEntityValueProvider>> singletonFactory,
        bool emitMolotov,
        bool captureSmokes = false,
        int? maxDegreeOfParallelism = null,
        Action<double>? onProgress = null,
        CancellationToken cancellationToken = default) =>
        Produce(frames, perPlayerFactory, singletonFactory, emitMolotov, null,
            captureSmokes, maxDegreeOfParallelism, onProgress, cancellationToken);

    /// <summary>
    ///     <see cref="Produce(IReadOnlyList{DemoFrame}, Func{IReadOnlyList{IPerPlayerEntityValueProvider}}, Func{IReadOnlyList{IEntityValueProvider}}, bool, bool, int?, Action{double}?, CancellationToken)" />
    ///     with the chunk plan's target made explicit. Only the gates pass it. Left to
    ///     <see cref="ResolveTargetChunks" />'s default the boundaries are a function of the runner's core
    ///     count, and a boundary is the one position a checkpoint reconstruction can diverge at — so
    ///     without this seam which boundaries the suite exercises is whatever hardware it happens to run
    ///     on, and a bug that only bites when a chunk starts on an entity's creation frame is reachable on
    ///     one machine and unreachable on the next.
    /// </summary>
    /// <param name="frames">The demo's frame list.</param>
    /// <param name="perPlayerFactory">Creates a fresh per-player provider list for one worker.</param>
    /// <param name="singletonFactory">Creates a fresh singleton provider list for one worker.</param>
    /// <param name="emitMolotov">When true, each digest includes live <c>CMolotovProjectile</c>s.</param>
    /// <param name="targetChunks">Chunks to aim for, or null for <see cref="Environment.ProcessorCount" />.</param>
    /// <param name="captureSmokes">When true, each digest carries the frame's active smoke clouds.</param>
    /// <param name="maxDegreeOfParallelism">Optional cap on concurrent workers (default: unbounded).</param>
    /// <param name="onProgress">Optional fraction-complete callback (0..1), invoked once per chunk.</param>
    /// <param name="cancellationToken">Aborts the decode; no partial digest array is ever returned.</param>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken" /> was observed.</exception>
    internal static EntityFrameDigest[] Produce(
        IReadOnlyList<DemoFrame> frames,
        Func<IReadOnlyList<IPerPlayerEntityValueProvider>> perPlayerFactory,
        Func<IReadOnlyList<IEntityValueProvider>> singletonFactory,
        bool emitMolotov,
        int? targetChunks,
        bool captureSmokes = false,
        int? maxDegreeOfParallelism = null,
        Action<double>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        EntityFrameDigest[] digests = new EntityFrameDigest[frames.Count];
        if (frames.Count == 0)
        {
            return digests;
        }

        IReadOnlyList<Chunk> chunks = PlanChunks(frames, out int schemaPrefixEnd, targetChunks);

        // Bootstrap each worker's layer single-threaded before fan-out — the layer ctor runs BootstrapTracker
        // (lens registry + entity-factory registry). Priming and the per-frame decode then run in parallel.
        EntityStateLayer[] layers = new EntityStateLayer[chunks.Count];
        for (int i = 0; i < chunks.Count; i++)
        {
            layers[i] = new EntityStateLayer(frames);
        }

        ParallelOptions options = BuildParallelOptions(maxDegreeOfParallelism, cancellationToken);

        // Snapshot the flag into a local before forking so every worker closes over one consistent value.
        // The workers add into the box directly rather than reading the AsyncLocal back, so a caller that
        // starts another Produce while this one runs cannot redirect these workers' accounting.
        // The fork is a full memory barrier.
        bool prof = Profiling.Enabled;
        StrongBox<long>? allocSum = prof ? new StrongBox<long>(0L) : null;
        _profWorkerAlloc.Value = allocSum;

        int chunksDone = 0;
        try
        {
            Parallel.For(0, chunks.Count, options, ci =>
            {
                long workerAllocStart = prof ? GC.GetAllocatedBytesForCurrentThread() : 0;
                Chunk chunk = chunks[ci];
                EntityStateLayer layer = layers[ci];

                // Each worker its OWN provider instances (FreezePeriodProvider caches a mutable entity index;
                // sharing would race). The factory ordering contract keeps the digest indices aligned.
                IReadOnlyList<IPerPlayerEntityValueProvider> perPlayer = perPlayerFactory();
                IReadOnlyList<IEntityValueProvider> singletons = singletonFactory();

                // Per chunk, never shared. This worker has no history before its checkpoint, so its first
                // frame re-emits every live cell; the consumer's fold makes that redundant, not wrong. The
                // layout is this worker's own too, over its own clones; the consumer judges it by kinds
                // and names, not by reference.
                PerPawnDeltaState delta = new(DigestColumnLayout.For(perPlayer));

                // Per chunk like the delta state: it subscribes to this worker's tracker, and its
                // first sync seeds from whatever the checkpoint primed, so a smoke already billowing
                // when the chunk starts is read from frame one.
                ProjectileSlotIndex projectiles = new();

                if (chunk.CheckpointFrameIndex >= 0)
                {
                    layer.PrimeFromCheckpoint(chunk.CheckpointFrameIndex, schemaPrefixEnd);
                }

                for (int n = chunk.Start; n < chunk.End; n++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    layer.SeekToTick(frames[n].ServerTick);
                    digests[n] = EntityDigestExtractor.Build(
                        layer, delta, singletons, emitMolotov, captureSmokes, projectiles);
                }

                if (allocSum is not null)
                {
                    // This worker's decode allocation (provider clones + prime + seek loop + digest build).
                    Interlocked.Add(ref allocSum.Value,
                        GC.GetAllocatedBytesForCurrentThread() - workerAllocStart);
                }

                if (onProgress is not null)
                {
                    onProgress((double)Interlocked.Increment(ref chunksDone) / chunks.Count);
                }
            });
        }
        catch (AggregateException e)
        {
            // Same contract as TriangleBvh.Build, which forks the other half of this work: one worker's
            // failure reaches the caller as the exception it threw rather than as the AggregateException
            // Parallel.For delivers it in. It has to, because the failures worth catching here are typed —
            // EntityDigestExtractor throws InvalidOperationException on provider schema drift, and drift is
            // meant to be loud rather than something a caller has to unwrap to recognise. Only several
            // distinct failures at once stay aggregated; a cancellation never arrives this way at all.
            AggregateException flat = e.Flatten();
            if (flat.InnerExceptions.Count == 1)
            {
                ExceptionDispatchInfo.Throw(flat.InnerExceptions[0]);
            }

            throw flat;
        }

        return digests;
    }

    /// <summary>
    ///     Maps the public <c>AnalysisOptions.MaxDegreeOfParallelism</c> onto the
    ///     <see cref="ParallelOptions" /> the decode fans out with. <c>null</c> — and any value
    ///     <c>&lt;= 0</c> — leaves <see cref="ParallelOptions.MaxDegreeOfParallelism" /> at its -1
    ///     default (unbounded), so the "no opinion" and "nonsense value" cases both degrade to today's
    ///     behaviour rather than throwing out of an evaluation. Cancellation rides on the same options:
    ///     <c>Parallel.For</c> stops scheduling new chunks and throws
    ///     <see cref="OperationCanceledException" /> once running workers observe the token.
    /// </summary>
    internal static ParallelOptions BuildParallelOptions(
        int? maxDegreeOfParallelism, CancellationToken cancellationToken)
    {
        ParallelOptions options = new()
        {
            CancellationToken = cancellationToken
        };
        if (maxDegreeOfParallelism is int dop and > 0)
        {
            options.MaxDegreeOfParallelism = dop;
        }

        return options;
    }

    /// <summary>
    ///     Partitions <paramref name="frames" /> into worker chunks at <c>DEM_FullPacket</c> boundaries.
    ///     Chunk 0 is <c>[0, F_1)</c> decoded from scratch; chunk <c>k</c> is <c>[F_k, F_{k+1})</c> primed
    ///     from the checkpoint <c>F_k</c>. Note F_0 is intentionally NOT a checkpoint: it sits in the
    ///     schema-bootstrap region (gameplay starts at tick 0 and <c>CurrentTick</c> inits to 0, so the
    ///     sequential <c>SeekToTick(0)</c> early-returns and digests there are empty); a checkpoint at F_0
    ///     would reconstruct a non-empty tick-0 set and diverge. Decoding <c>[0, F_1)</c> from scratch IS
    ///     the sequential mechanism, so it matches by construction. With fewer than two full packets there
    ///     is no usable split point, so the whole demo is one from-scratch chunk.
    ///     <para>
    ///         A full packet with a same-tick successor is never chosen: see
    ///         <see cref="EntityStateLayer.PrimeFromCheckpoint" />, which cannot represent that position.
    ///         Skipping one only widens the chunk that would have started there, which is the same
    ///         coarsening the paragraph below already relies on.
    ///     </para>
    /// </summary>
    /// <param name="frames">The demo's frame list.</param>
    /// <param name="schemaPrefixEnd">Index of the first <c>DEM_Packet</c>, which bounds the schema prefix a prime replays.</param>
    /// <param name="targetChunks">
    ///     How many chunks to aim for, or null for <see cref="Environment.ProcessorCount" />. Only tests pass
    ///     it: see <see cref="ResolveTargetChunks" />.
    /// </param>
    internal static IReadOnlyList<Chunk> PlanChunks(
        IReadOnlyList<DemoFrame> frames, out int schemaPrefixEnd, int? targetChunks = null)
    {
        schemaPrefixEnd = -1;
        List<int> fullIdx = [];
        for (int i = 0; i < frames.Count; i++)
        {
            string cmd = frames[i].Command;
            if (schemaPrefixEnd < 0 && cmd == "DEM_Packet")
            {
                schemaPrefixEnd = i;
            }

            if (cmd == "DEM_FullPacket")
            {
                fullIdx.Add(i);
            }
        }

        // Degenerate demo with no gameplay packets — the prefix replay becomes a no-op.
        if (schemaPrefixEnd < 0)
        {
            schemaPrefixEnd = 0;
        }

        List<Chunk> chunks = [];
        if (fullIdx.Count < 2)
        {
            chunks.Add(new Chunk(0, frames.Count, -1));
            return chunks;
        }

        // Coarsen: use a SUBSET of full packets as checkpoints so the chunk count is ~the core count, not
        // the full-packet count (39). Each checkpoint chunk spans several full-packet intervals; the
        // intermediate full packets inside it decode via the normal SeekToTick path (their PacketEntities
        // skipped exactly as sequential playback does, their UpdateStringTable baselines applied during that
        // decode) — so coarsening is correctness-neutral (the equivalence gate confirms). Coarse chunks cut
        // both the redundant per-worker schema parse (fewer workers) and the oversubscription that serialized
        // ~39 allocating workers onto ~10 cores.
        // Candidates, in order. F_0 is never one (schema-bootstrap region), and neither is a full packet
        // that shares its tick with the frame after it: PrimeFromCheckpoint leaves CurrentTick at the
        // checkpoint tick, so the worker's first SeekToTick early-returns and that successor's delta
        // never lands, while a sequential decode folds it into the very same frame.
        List<int> candidates = [];
        for (int k = 1; k < fullIdx.Count; k++)
        {
            int f = fullIdx[k];
            if (f + 1 >= frames.Count || frames[f + 1].ServerTick != frames[f].ServerTick)
            {
                candidates.Add(f);
            }
        }

        // Every full packet after F_0 clashes. Nothing to split on, so decode the demo sequentially
        // rather than plan a chunk the prime cannot represent.
        if (candidates.Count == 0)
        {
            chunks.Add(new Chunk(0, frames.Count, -1));
            return chunks;
        }

        int target = ResolveTargetChunks(targetChunks);
        int wantCheckpoints = Math.Max(1, Math.Min(candidates.Count, target - 1));
        int stride = (candidates.Count + wantCheckpoints - 1) / wantCheckpoints; // ceil

        List<int> checkpoints = [];
        for (int k = 0; k < candidates.Count; k += stride)
        {
            checkpoints.Add(candidates[k]);
        }

        chunks.Add(new Chunk(0, checkpoints[0], -1)); // chunk 0: [0, first checkpoint) from scratch
        for (int c = 0; c < checkpoints.Count; c++)
        {
            int start = checkpoints[c];
            int end = c + 1 < checkpoints.Count ? checkpoints[c + 1] : frames.Count;
            chunks.Add(new Chunk(start, end, start));
        }

        return chunks;
    }

    // Target chunk count ≈ available cores. More chunks → finer load balance but more redundant per-worker
    // schema parses + worse oversubscription (the 39-on-N thread injection that serialized the producer);
    // fewer → coarser balance but cheaper. ~cores is the measured sweet spot.
    // An explicit target exists for the gates only. A chunk boundary is where a worker primes from a
    // checkpoint and re-emits every live cell, so it is the one position a reconstruction bug can hide at;
    // left to the core count alone, which boundaries a suite exercises is a property of the runner, and a
    // bug that only bites when a boundary lands on an entity's creation frame is reachable on one machine
    // and unreachable on the next. Zero and negatives fall back rather than plan an empty demo, matching
    // how BuildParallelOptions treats a nonsense degree.
    private static int ResolveTargetChunks(int? targetChunks) =>
        targetChunks is int t and > 0 ? t : Environment.ProcessorCount;

    /// <summary>
    ///     A contiguous frame range assigned to one worker. <see cref="CheckpointFrameIndex" /> is the
    ///     <c>DEM_FullPacket</c> the worker primes from, or <c>-1</c> for a from-scratch decode (chunk 0).
    /// </summary>
    internal readonly record struct Chunk(int Start, int End, int CheckpointFrameIndex);
}
