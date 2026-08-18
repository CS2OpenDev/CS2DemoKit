#region

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
///     <see cref="EntityStateLayer.PrimeFromCheckpoint" /> — so the parallel digest array is element-wise
///     identical to a sequential one (proven by <c>ParallelDigestEquivalenceTests</c>; sequential→golden is
///     proven by Step 1, so parallel→golden follows by composition).
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
    // but the one it happens to run. Each worker brackets its chunk's allocation and folds it in
    // here. chunks.Count ≈ Environment.ProcessorCount (see ResolveTargetChunks), so this is
    // ≈ core-count Interlocked.Add total — no contention. The scanner reads the sum via
    // ReadWorkerAllocBytes() after Produce returns. The per-worker brackets are guarded at runtime so a
    // default decode pays nothing. Mirrors ParseProfiler's static-accumulator pattern.
    private static long _profWorkerAllocSum;

    /// <summary>Total per-worker decode allocation (bytes) accumulated by the last <see cref="Produce" /> run.</summary>
    internal static long ReadWorkerAllocBytes() => Interlocked.Read(ref _profWorkerAllocSum);

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
    internal static EntityFrameDigest[] Produce(
        IReadOnlyList<DemoFrame> frames,
        Func<IReadOnlyList<IPerPlayerEntityValueProvider>> perPlayerFactory,
        Func<IReadOnlyList<IEntityValueProvider>> singletonFactory,
        bool emitMolotov,
        int? maxDegreeOfParallelism = null,
        Action<double>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        EntityFrameDigest[] digests = new EntityFrameDigest[frames.Count];
        if (frames.Count == 0)
        {
            return digests;
        }

        IReadOnlyList<Chunk> chunks = PlanChunks(frames, out int schemaPrefixEnd);

        // Bootstrap each worker's layer single-threaded before fan-out — the layer ctor runs BootstrapTracker
        // (lens registry + entity-factory registry). Priming and the per-frame decode then run in parallel.
        EntityStateLayer[] layers = new EntityStateLayer[chunks.Count];
        for (int i = 0; i < chunks.Count; i++)
        {
            layers[i] = new EntityStateLayer(frames);
        }

        ParallelOptions options = BuildParallelOptions(maxDegreeOfParallelism, cancellationToken);

        // Snapshot the flag into a local before forking so every worker closes over one consistent value
        // (the single-run contract on Profiling). The fork is a full memory barrier.
        bool prof = Profiling.Enabled;
        if (prof)
        {
            Interlocked.Exchange(ref _profWorkerAllocSum, 0);
        }

        int chunksDone = 0;
        Parallel.For(0, chunks.Count, options, ci =>
        {
            long workerAllocStart = prof ? GC.GetAllocatedBytesForCurrentThread() : 0;
            Chunk chunk = chunks[ci];
            EntityStateLayer layer = layers[ci];

            // Each worker its OWN provider instances (FreezePeriodProvider caches a mutable entity index;
            // sharing would race). The factory ordering contract keeps the digest indices aligned.
            IReadOnlyList<IPerPlayerEntityValueProvider> perPlayer = perPlayerFactory();
            IReadOnlyList<IEntityValueProvider> singletons = singletonFactory();

            if (chunk.CheckpointFrameIndex >= 0)
            {
                layer.PrimeFromCheckpoint(chunk.CheckpointFrameIndex, schemaPrefixEnd);
            }

            for (int n = chunk.Start; n < chunk.End; n++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                layer.SeekToTick(frames[n].ServerTick);
                digests[n] = EntityDigestExtractor.Build(layer, perPlayer, singletons, emitMolotov);
            }

            if (prof)
            {
                // This worker's decode allocation (provider clones + prime + seek loop + digest build).
                Interlocked.Add(ref _profWorkerAllocSum,
                    GC.GetAllocatedBytesForCurrentThread() - workerAllocStart);
            }

            if (onProgress is not null)
            {
                onProgress((double)Interlocked.Increment(ref chunksDone) / chunks.Count);
            }
        });

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
    /// </summary>
    internal static IReadOnlyList<Chunk> PlanChunks(IReadOnlyList<DemoFrame> frames, out int schemaPrefixEnd)
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
        int target = ResolveTargetChunks();
        int candidates = fullIdx.Count - 1; // F_0 is never a checkpoint (schema-bootstrap region)
        int wantCheckpoints = Math.Max(1, Math.Min(candidates, target - 1));
        int stride = (candidates + wantCheckpoints - 1) / wantCheckpoints; // ceil

        List<int> checkpoints = [];
        for (int k = 1; k < fullIdx.Count; k += stride)
        {
            checkpoints.Add(fullIdx[k]);
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
    private static int ResolveTargetChunks() => Environment.ProcessorCount;

    /// <summary>
    ///     A contiguous frame range assigned to one worker. <see cref="CheckpointFrameIndex" /> is the
    ///     <c>DEM_FullPacket</c> the worker primes from, or <c>-1</c> for a from-scratch decode (chunk 0).
    /// </summary>
    internal readonly record struct Chunk(int Start, int End, int CheckpointFrameIndex);
}
