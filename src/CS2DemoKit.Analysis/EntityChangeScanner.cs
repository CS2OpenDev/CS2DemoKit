#region

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Reflection;
using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Analysis.Diagnostics;
using CS2DemoKit.Analysis.Events;
using CS2DemoKit.Analysis.Plugins;
using CS2DemoKit.Analysis.Visibility;
using CS2DemoKit.Parser;
using CS2DemoKit.Parser.Entities;
using CS2DemoKit.Parser.EntityTracking;
using CS2DemoKit.Parser.GameEvents;

#endregion

namespace CS2DemoKit.Analysis;

/// <summary>
///     Per-evaluator orchestrator for entity-state consumption. Owns a single
///     <see cref="EntityStateLayer" />, tracks the last-observed value of each registered
///     <see cref="IEntityValueProvider" />, and on every frame advance returns synthesized
///     <see cref="EntityChangeMessage" />s for fields that crossed an emission edge.
///     <para>
///         Lazy-activation invariant: the scanner is only constructed when at least one rule
///         actually references a provider's <see cref="IEntityValueProvider.ContextName" />.
///         A null <c>BuildResult.EntityScanner</c> guarantees zero per-frame work.
///     </para>
///     <para>
///         Each scanner instance is single-threaded. Parallel rule-chain evaluators each get
///         their own scanner and layer (one layer = one scanner). The scanner also writes the provider's <see cref="ValueNode{T}" /> on every
///         observed change regardless of <see cref="IEntityValueProvider.EmitOn" /> direction,
///         so condition expressions referencing the context name see the live value even when no
///         edge is synthesized.
///     </para>
/// </summary>
public sealed class EntityChangeScanner
{
    // Molotov-throw synthesis. Molotov/incendiary detonation has no usable single-fire
    // game event in GOTV, so a `molotov_used` rule needs entity attribution: each newly-created
    // CMolotovProjectile is counted once (keyed by entity index + serial to survive index reuse)
    // and attributed to its thrower via m_hThrower → pawn → m_hController → slot. Only enabled
    // when a rule references the synthesized `molotov_thrown` event, so the per-frame entity walk
    // it adds is skipped otherwise.
    private readonly bool _emitMolotovThrows;


    private readonly Dictionary<IPerPlayerEntityValueProvider, int> _perPlayerProviderIndex =
        new(ReferenceEqualityComparer.Instance);

    // ── Per-player pre-frame snapshot ─────────────────────────────────────────
    // For each registered per-player provider, the most-recently-captured value PER PLAYER
    // SLOT. Captured at the START of each AdvanceAndPollAt call, BEFORE the layer advances,
    // so when consumers read inside that frame's inner-message processing they see the
    // PREVIOUS frame's value. This is the right input for "compute pre-event state from
    // entity ground truth" because the current frame's PacketEntities update arrives
    // concurrently with the event we're handling.
    private readonly List<IPerPlayerEntityValueProvider> _perPlayerProviders;

    // The column plan of every digest this scanner consumes, and the typed snapshot indexed by
    // it. A digest from a chunk worker carries its own layout instance over provider clones;
    // MergePreFrameSnapshot checks it is compatible (same kinds, same names) before folding.
    private readonly DigestColumnLayout _layout;
    private PreFrameSnapshot _preFrameSnapshot;
    private DigestColumnLayout? _lastCompatibleLayout;

    // This scanner's own cell memory, used only on the sequential path. The pipelined path
    // never reaches BuildDigest, so each chunk worker keeps its own instead.
    private PerPawnDeltaState _delta;

    // Sequential path driven by the evaluator's tick runs. Frames whose tick is not past the
    // layer's current tick are held back and applied with the first run that is, which is the
    // tick-gated seek's behaviour over a list; the signon prefix is the case that needs it.
    // Outside an evaluation nothing feeds the layer, so the poll seeks it itself.
    private readonly List<DemoFrame> _pendingFrames = [];
    private bool _runDriven;
    private bool _layerAdvanced;

    // Each slot's controller team as last observed in a digest, -1 until seen. The evaluator seeds
    // a materialising player from it and a change to a real team synthesizes PlayerTeamObservedEvent.
    private readonly int[] _lastTeam = new int[64];

    // Decode-integrity latch (hardening that landed with the EnemyDmg-overcount investigation, but
    // NOT that fix — the fix lives in HurtTeamEnrichmentEdge's same-frame guard): once ANY consumed
    // digest reports DecodeCompromised — the producing tracker had hit an entity-decode error (the
    // bit-misalignment shape, KNOWN-AND-SUSPECTED-ISSUES.md) — stop folding per-pawn values into the
    // pre-frame snapshot for the rest of the run. Without the latch, the parallel digest path keeps re-priming
    // fresh trackers at DEM_FullPacket checkpoints, so on a decode-broken demo the snapshot would
    // be periodically refreshed with values that then freeze mid-chunk as the decode breaks —
    // silently feeding stale per-pawn state to every snapshot consumer. With the latch, the
    // entity path goes quiet at the first error and event-tracked fallbacks take over. All 5
    // current bench demos decode cleanly (probed 2026-08-12), so this never engages there; it
    // protects future demos that hit real decode errors.
    private bool _preFrameSnapshotFrozen;
    private readonly List<NetMessage> _scratch = new(8);
    private readonly HashSet<(int Index, int Serial)> _seenMolotovs = [];

    // The singleton providers in _tracked order — the digest's Singletons[] aligns with these. Held
    // separately so the shared EntityDigestExtractor (used by the pipelined producer too) reads the
    // same providers in the same order.
    private readonly List<IEntityValueProvider> _singletonProviders;
    private readonly List<TrackedProvider> _tracked;

    // Hand-built digests a test injects in place of a producer; AdvanceAndPollAt consumes
    // digest[frameIdx] and BeginEvaluation starts no producer while they are set.
    private EntityFrameDigest?[]? _injected;

    // Digests PrecomputeParallelDigests folded ahead of an evaluation, one per frame. They serve
    // one evaluation: AdvanceAndPollAt releases each as it is consumed and EndEvaluation drops
    // the rest, so a run cut short leaves nothing half-consumed for the next.
    private EntityFrameDigest?[]? _precomputed;

    // The previous frame's digest. The pre-frame snapshot consumed inside frame N
    // is the per-pawn state from frame N-1 — which is exactly _prevDigest.PerPawn (built last frame from
    // N-1's post-seek state == N's pre-seek state). Holding one frame back lets the digest carry the
    // "previous values", so a chunk worker needn't preserve the layer's pre-seek state.
    private EntityFrameDigest? _prevDigest;

    private int _profFramesPolled;

    // Whether this scanner captured profiling data — latched the first time a profiled per-frame seam runs.
    // Reported as ScannerProfilingSnapshot.Enabled, decoupled from the live flag.
    private bool _profiled;

    // Allocated-bytes deltas for the same per-frame sub-phases (cheap: GetAllocatedBytes is a
    // non-allocating intrinsic). Attributes the eval allocation total to its per-frame source.
    private long _profSeekAlloc;

    // Scanner-level profiling (opt-in at RUNTIME via Profiling.Enabled). These bracket the per-frame
    // sub-phases of AdvanceAndPollAt; SeekTicks ⊇ the EntityTracker-internal decode captured by
    // EntityTracker.GetProfilingSnapshot(). The fold call-sites are guarded by `if (Profiling.Enabled)`,
    // so a default run touches none of them. Read once post-run via GetProfilingSnapshot().
    private long _profSeekTicks;
    private long _profSnapshotAlloc;
    private long _profSnapshotTicks;

    // The pipelined producer's fold, summed over its workers when it closes: worker time and
    // allocation, since the calling thread's counter sees none of theirs. Under the evaluation
    // or up front in PrecomputeParallelDigests alike.
    private long _profFoldTicks;
    private long _profFoldAlloc;

    // Provider schema validation: latched once every provider's target class has
    // descriptors and every declared type matched the wire schema. Loud on drift — see
    // TryValidateProviderSchema.
    private volatile bool _schemaValidated;

    // Visibility rising edges. Both are null unless a rule actually subscribes to enemy_spotted AND
    // the caller supplied map geometry, so the per-frame consume pays nothing by default. They are
    // driven off the SAME digest the rest of the consume reads: the vantage scanner folds the
    // per-pawn delta rows the pre-frame snapshot is already folding, so no second entity decode,
    // no second entity-set walk, and one Vantage per (player, tick) shared by every pair.
    private readonly AimVantageScanner? _vantageScanner;
    private readonly VisibilityTransitionScanner? _transitionScanner;

    // The projectile slots the per-frame digest reads instead of walking every live entity. Bound
    // to the layer's tracker on first use and rebound if the layer is reset under it.
    private readonly ProjectileSlotIndex _projectiles = new();
    private readonly PawnSlotIndex _pawns = new();

    /// <param name="layer">The entity-state layer the scanner reads from; advanced one frame at a time.</param>
    /// <param name="providers">Singleton-entity providers paired with their backing value nodes (push model).</param>
    /// <param name="perPlayerProviders">Per-player providers polled into the pre-frame snapshot (pull model).</param>
    /// <param name="emitMolotovThrows">
    ///     When true, synthesize a <c>molotov_thrown</c> event per newly-created
    ///     CMolotovProjectile.
    /// </param>
    /// <param name="vantageScanner">
    ///     When supplied, the per-frame consume folds the digest's per-pawn deltas into it and
    ///     samples a vantage set (eye ray, duck, derived 2D speed) that shot-anchored enrichment
    ///     edges read. Null (the default) leaves the whole vantage fold out of the per-frame path.
    /// </param>
    /// <param name="transitionScanner">
    ///     When supplied ALONGSIDE <paramref name="vantageScanner" />, consumes that vantage set and
    ///     synthesizes an <c>enemy_spotted</c> event per rising edge. It cannot run without a vantage
    ///     source (it would be a silent no-op), but the vantage source runs perfectly well without
    ///     it: visibility needs baked map geometry, and the aim columns do not.
    /// </param>
    public EntityChangeScanner(
        EntityStateLayer layer,
        IReadOnlyList<(IEntityValueProvider Provider, StateNode ValueNode)> providers,
        IReadOnlyList<IPerPlayerEntityValueProvider>? perPlayerProviders = null,
        bool emitMolotovThrows = false,
        AimVantageScanner? vantageScanner = null,
        VisibilityTransitionScanner? transitionScanner = null)
    {
        Layer = layer;
        _emitMolotovThrows = emitMolotovThrows;
        _vantageScanner = vantageScanner;
        _transitionScanner = transitionScanner;
        _tracked = new List<TrackedProvider>(providers.Count);
        _singletonProviders = new List<IEntityValueProvider>(providers.Count);
        foreach ((IEntityValueProvider p, StateNode node) in providers)
        {
            _tracked.Add(new TrackedProvider(p, node, p.DefaultValue));
            _singletonProviders.Add(p);
        }

        _perPlayerProviders = perPlayerProviders is not null
            ? new List<IPerPlayerEntityValueProvider>(perPlayerProviders)
            : new List<IPerPlayerEntityValueProvider>();
        for (int i = 0; i < _perPlayerProviders.Count; i++)
        {
            _perPlayerProviderIndex[_perPlayerProviders[i]] = i;
        }

        // In this order: the delta state and the snapshot are both indexed by the layout.
        _layout = DigestColumnLayout.For(_perPlayerProviders);
        _delta = new PerPawnDeltaState(_layout);
        _preFrameSnapshot = new PreFrameSnapshot(_layout);
    }

    /// <summary>
    ///     Entity-state layer owned by this scanner. Exposed for per-event reads in edges that
    ///     follow the pre-frame pull model. The scanner advances it: through
    ///     <see cref="BeginTickRun" /> on the sequential path, by seeking it when
    ///     <see cref="AdvanceAndPollAt" /> is called outside an evaluation, and never on the
    ///     pipelined path, where each chunk worker folds on a layer of its own. Do not seek it
    ///     directly.
    /// </summary>
    public EntityStateLayer Layer { get; }

    /// <summary>
    ///     Test seam: hand-built digests the scanner consumes in <see cref="AdvanceAndPollAt" /> in
    ///     place of a producer's, one per frame index.
    /// </summary>
    internal void InjectDigests(EntityFrameDigest?[] digests) => _injected = digests;

    /// <summary>
    ///     Consumes frame <paramref name="frameIndex" />'s digest and returns any synthesized change
    ///     messages produced by the providers whose values transitioned this frame. The digest is
    ///     the pipelined producer's, one <see cref="PrecomputeParallelDigests" /> held, or built
    ///     from this scanner's own layer after <see cref="BeginTickRun" /> applied the frame's tick
    ///     run. Called outside an evaluation, it seeks a list-backed layer to
    ///     <paramref name="tick" /> itself, so a caller can walk a scanner frame by frame; a layer
    ///     built without frames throws, since nothing else can advance it. Returns an empty list
    ///     (not null) when nothing changed; callers may skip iteration by checking the count first.
    /// </summary>
    /// <param name="frameIndex">Index of the frame in the evaluation's read order, from zero.</param>
    /// <param name="tick">The frame's server tick.</param>
    public IReadOnlyList<NetMessage> AdvanceAndPollAt(int frameIndex, int tick)
    {
        if (Profiling.Enabled)
        {
            _profiled = true;
            _profFramesPolled++;
        }

        if (_pipeline is not null)
        {
            return Consume(_pipeline.Take(frameIndex), tick);
        }

        if (_precomputed is not null)
        {
            EntityFrameDigest? held = frameIndex < _precomputed.Length ? _precomputed[frameIndex] : null;
            if (held is null)
            {
                throw new InvalidOperationException(
                    $"No precomputed digest for frame {frameIndex}: an evaluation over precomputed digests "
                    + "must read the frames they were folded from, once each, in order.");
            }

            _precomputed[frameIndex] = null;
            return Consume(held, tick);
        }

        if (_injected is not null && _injected[frameIndex] is { } injected)
        {
            return Consume(injected, tick);
        }

        if (!_runDriven)
        {
            SeekLayer(tick);
        }

        ValidateSchema(Layer.Tracker);
        return Consume(BuildDigest(), tick);
    }

    // One tick-gated seek per poll, which is what the evaluator's tick runs reproduce.
    private void SeekLayer(int tick)
    {
        bool prof = Profiling.Enabled;
        long seekStart = 0, seekStartA = 0;
        if (prof)
        {
            seekStart = Stopwatch.GetTimestamp();
            seekStartA = GC.GetAllocatedBytesForCurrentThread();
        }

        Layer.SeekToTick(tick);
        _layerAdvanced = true;
        if (prof)
        {
            _profSeekTicks += Stopwatch.GetTimestamp() - seekStart;
            _profSeekAlloc += GC.GetAllocatedBytesForCurrentThread() - seekStartA;
        }
    }

    /// <summary>How this evaluation's digests are being produced; <see cref="DigestProducerKind.None" /> before one starts.</summary>
    public DigestProducerKind ProducerKind { get; private set; }

    // Workers the pipelined producer runs over a stream when the caller expresses no cap. Each
    // one holds a tracker and a chunk of frames ahead of the loop, about 20 MB live on a full
    // match, so this bounds memory as much as it bounds parallelism. Three is where the fold
    // disappears behind the read and the dispatch on a ten-core machine; a fourth was measured
    // to buy nothing.
    internal const int DefaultPipelineWorkers = 3;

    // Over a retained list the walk is free and the frames are already resident, so the fold is
    // the only thing left to hide and memory is not what bounds the count. Measured in
    // docs/perf/baseline.md, "One producer".
    internal static int DefaultListWorkers => Math.Max(DefaultPipelineWorkers, Environment.ProcessorCount - 2);

    private PipelinedDigestSource? _pipeline;
    private bool _releaseFolded;
    private bool _pastSignonPrefix;

    /// <summary>The per-player providers this scanner snapshots, in gate order.</summary>
    internal IReadOnlyList<IPerPlayerEntityValueProvider> PerPlayerProviders => _perPlayerProviders;

    /// <summary>
    ///     The digest workers an evaluation runs: the caller's cap when it is positive, else the
    ///     default for the source kind. One selects the sequential producer. Zero and negatives
    ///     are treated as no cap rather than thrown on, since the knob rides an options record a
    ///     service may fill from config.
    /// </summary>
    internal static int ResolveWorkers(int? maxDegreeOfParallelism, bool randomAccess) =>
        maxDegreeOfParallelism is int dop and > 0
            ? dop
            : Math.Min(Environment.ProcessorCount, randomAccess ? DefaultListWorkers : DefaultPipelineWorkers);

    /// <summary>
    ///     Starts an evaluation: clears every per-evaluation accumulator (the previous digest, the
    ///     pre-frame snapshot, the delta memory, the molotov de-dup set, the singleton last-values,
    ///     the decode-compromise latch, a layer the sequential path advanced), then chooses the
    ///     producer: the pipelined checkpoint-parallel producer, which reads ahead on the
    ///     evaluator's behalf and is what the evaluator must then read from, or one layer driven
    ///     in step with the loop by <see cref="BeginTickRun" /> when the parallelism cap is one.
    ///     Digests <see cref="PrecomputeParallelDigests" /> holds are consumed instead, and no
    ///     producer starts. Without this a second evaluation over one <c>BuildResult</c> would
    ///     start from the prior run's terminal values.
    /// </summary>
    /// <returns>The source the evaluator reads frames from for this evaluation.</returns>
    internal IDemoFrameSource BeginEvaluation(IDemoFrameSource source, int? maxDegreeOfParallelism,
        CancellationToken cancellationToken)
    {
        ClosePipeline();
        _releaseFolded = !source.SupportsRandomAccess;
        _pastSignonPrefix = false;
        _prevDigest = null;
        _seenMolotovs.Clear();
        _preFrameSnapshotFrozen = false;
        _lastCompatibleLayout = null;
        _pendingFrames.Clear();
        _runDriven = false;
        Array.Fill(_lastTeam, -1);
        _delta = new PerPawnDeltaState(_layout);
        _preFrameSnapshot = new PreFrameSnapshot(_layout);
        Span<TrackedProvider> tracked = CollectionsMarshal.AsSpan(_tracked);
        for (int i = 0; i < tracked.Length; i++)
        {
            tracked[i].LastValue = tracked[i].Provider.DefaultValue;
        }

        if (_layerAdvanced)
        {
            Layer.Reset();
            _layerAdvanced = false;
        }

        if (_precomputed is not null)
        {
            ProducerKind = DigestProducerKind.Pipelined;
            return source;
        }

        // A test's hand-built digests stand in for a producer.
        if (_injected is not null)
        {
            ProducerKind = DigestProducerKind.Sequential;
            return source;
        }

        int workers = ResolveWorkers(maxDegreeOfParallelism, source.SupportsRandomAccess);
        if (workers <= 1)
        {
            _runDriven = true;
            ProducerKind = DigestProducerKind.Sequential;
            return source;
        }

        _pipeline = StartPipeline(source, workers, _releaseFolded, cancellationToken);
        ProducerKind = DigestProducerKind.Pipelined;
        return _pipeline;
    }

    private PipelinedDigestSource StartPipeline(IDemoFrameSource source, int workers, bool releaseFolded,
        CancellationToken cancellationToken) =>
        new(source,
            () => _perPlayerProviders.Select(CloneProvider).ToList(),
            () => _singletonProviders.Select(CloneProvider).ToList(),
            _emitMolotovThrows,
            _transitionScanner is not null,
            workers,
            releaseFolded,
            ValidateSchema,
            cancellationToken,
            MinChunkFrames(source.FrameCount, workers));

    /// <summary>
    ///     Folds every frame's digest on the pipelined producer up front: the next evaluation over
    ///     these frames consumes the held digests and starts no producer. The evaluator never calls
    ///     this; it is for a host that times or schedules the fold apart from the loop, at the cost
    ///     of holding every digest at once, which the producer run live never does. The digests
    ///     serve one evaluation, which must read the same frames in the same order; a run cut
    ///     short drops the rest.
    /// </summary>
    /// <param name="frames">The frames the evaluation will read, in order.</param>
    /// <param name="onProgress">
    ///     Fraction complete in [0, 1], reported on the calling thread every 2048 frames and once
    ///     at the end.
    /// </param>
    /// <param name="maxDegreeOfParallelism">
    ///     The worker count, mapped as <see cref="AnalysisOptions.MaxDegreeOfParallelism" /> is over
    ///     a retained demo; one folds on a single worker.
    /// </param>
    /// <param name="cancellationToken">Cancels every worker. A cancelled fold holds nothing.</param>
    public void PrecomputeParallelDigests(IReadOnlyList<DemoFrame> frames, Action<double>? onProgress = null,
        int? maxDegreeOfParallelism = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(frames);
        using Activity? span = AnalysisDiagnostics.ActivitySource.StartActivity("analysis.precompute");
        int workers = ResolveWorkers(maxDegreeOfParallelism, randomAccess: true);
        PipelinedDigestSource pipeline = StartPipeline(new FrameListSource(frames, null), workers, false, cancellationToken);
        EntityFrameDigest?[] digests = new EntityFrameDigest?[frames.Count];
        try
        {
            for (int i = 0; pipeline.TryReadNext(out _); i++)
            {
                digests[i] = pipeline.Take(i);
                if (onProgress is not null && (i & 2047) == 0)
                {
                    onProgress(i / (double)frames.Count);
                }
            }
        }
        finally
        {
            ClosePipeline(pipeline);
        }

        _precomputed = digests;
        onProgress?.Invoke(1.0);
    }

    // Over a stream a chunk is memory held ahead of the loop, so it stays at the producer's
    // minimum. Over a list the frames are resident either way and every chunk costs a checkpoint
    // prime, so the chunks are sized to about twice the worker count. Measured in
    // docs/perf/baseline.md, "One producer".
    internal static int MinChunkFrames(int? frameCount, int workers) =>
        frameCount is int frames
            ? Math.Max(PipelinedDigestSource.MinChunkFrames, frames / (2 * Math.Max(1, workers)))
            : PipelinedDigestSource.MinChunkFrames;

    /// <summary>
    ///     Releases the pipelined producer, cancelling any chunk still decoding ahead, and drops
    ///     any precomputed digests the evaluation did not consume.
    /// </summary>
    internal void EndEvaluation()
    {
        ClosePipeline();
        _precomputed = null;
        _runDriven = false;
    }

    private void ClosePipeline()
    {
        if (_pipeline is { } pipeline)
        {
            _pipeline = null;
            ClosePipeline(pipeline);
        }
    }

    // Close joins every fold, so the counters are final once it returns.
    private void ClosePipeline(PipelinedDigestSource pipeline)
    {
        pipeline.Close();
        if (Profiling.Enabled)
        {
            _profiled = true;
            _profFoldTicks += pipeline.FoldTicks;
            _profFoldAlloc += pipeline.FoldAllocBytes;
        }
    }

    // Runs on whichever thread applied the frame: the loop's on the sequential path, a fold
    // worker's on the pipelined one. The latch is idempotent, so two workers judging the same
    // schema at once reach the same answer.
    private void ValidateSchema(EntityTracker tracker)
    {
        if (!_schemaValidated)
        {
            TryValidateProviderSchema(tracker);
        }
    }

    /// <summary>
    ///     Hands the evaluator's next tick run (a frame plus its same-tick successors) to the
    ///     sequential producer, which applies it before any frame in the run is polled. A run whose
    ///     tick is not past the layer's current tick is held back, exactly as the tick-gated seek
    ///     over a list would skip it, and applied with the first run that is. A no-op while the
    ///     pipelined producer runs, while precomputed digests are held, or while a test's digests
    ///     stand in for a producer.
    /// </summary>
    internal void BeginTickRun(IReadOnlyList<DemoFrame> run)
    {
        if (_injected is not null || _precomputed is not null || _pipeline is not null || run.Count == 0)
        {
            return;
        }

        if (Layer.CurrentTick >= run[0].ServerTick)
        {
            _pendingFrames.AddRange(run);
            return;
        }

        bool prof = Profiling.Enabled;
        long seekStart = 0, seekStartA = 0;
        if (prof)
        {
            seekStart = Stopwatch.GetTimestamp();
            seekStartA = GC.GetAllocatedBytesForCurrentThread();
        }

        for (int i = 0; i < _pendingFrames.Count; i++)
        {
            ApplyToLayer(_pendingFrames[i]);
        }

        _pendingFrames.Clear();
        for (int i = 0; i < run.Count; i++)
        {
            ApplyToLayer(run[i]);
        }

        _layerAdvanced = true;
        if (prof)
        {
            _profSeekTicks += Stopwatch.GetTimestamp() - seekStart;
            _profSeekAlloc += GC.GetAllocatedBytesForCurrentThread() - seekStartA;
        }
    }

    // Over a stream the frame is nobody's but the evaluator's once applied, and the evaluator
    // dispatches no entity message, so the payload goes with the fold. The signon prefix is
    // kept, as the pipelined producer keeps it, so the two dispatch the same messages.
    private void ApplyToLayer(DemoFrame frame)
    {
        Layer.Apply(frame);
        _pastSignonPrefix |= frame.CommandKind == EDemoCommands.DemPacket;
        if (_releaseFolded && _pastSignonPrefix)
        {
            frame.Release(PipelinedDigestSource.FoldedCategories(frame));
        }
    }

    /// <summary>
    ///     Post-SendTables provider schema validation: once every registered
    ///     provider's target class has field descriptors (the parser builds them when the
    ///     first entity of the class decodes — the initial FullPacket in practice), check that
    ///     each declared field path EXISTS and its wire type is COMPATIBLE with the provider's
    ///     declared value type. Both failures throw — CS2 schema drift must be loud, not the
    ///     silent zeros/nulls the read-path coercion would otherwise produce. A class that
    ///     never appears leaves the latch unset (reads yield null legitimately; nothing to
    ///     judge).
    /// </summary>
    private void TryValidateProviderSchema(EntityTracker tracker)
    {
        // Wait until every distinct target class has descriptors — judging a field against a
        // class the parser hasn't seen yet would misreport "missing".
        foreach (IPerPlayerEntityValueProvider p in _perPlayerProviders)
        {
            if (!tracker.HasClassDescriptors(p.EntityClass))
            {
                return;
            }
        }

        foreach (IEntityValueProvider p in _singletonProviders)
        {
            if (!tracker.HasClassDescriptors(p.EntityClass))
            {
                return;
            }
        }

        foreach (IPerPlayerEntityValueProvider p in _perPlayerProviders)
        {
            if (p is GenericPerPlayerFieldProvider { Spec.ViaHandleToField: { } hop })
            {
                // Two-hop spec (ViaHandleToField): only hop 1 — the CHandle field on the
                // provider's own class — is judged here. Hop 2's target class varies per
                // resolved entity (e.g. CWeaponAK47 vs CWeaponGlock share m_iClip1 via
                // CBasePlayerWeapon), so there is no single declared class to hold
                // descriptors for; a drifted target field reads as null (slot skipped),
                // not a loud error. The declared ValueType applies to the hop-2 value and
                // must NOT be judged against the hop-1 CHandle wire type.
                ValidateHandleField(tracker, p.Name, p.EntityClass, hop.HandlePath);
                continue;
            }

            if (p is IMultiSchemaFieldProvider multi)
            {
                // A field Valve renamed between builds has more than one correct spelling, and
                // demos of both vintages are in circulation. Judging the single declared path
                // would abort the entire analysis on every demo of the other vintage.
                ValidateAnyOf(tracker, p.Name, p.EntityClass, multi.CandidateFieldNames, p.ValueType);
                continue;
            }

            ValidateOne(tracker, p.Name, p.EntityClass, p.FieldName, p.ValueType);
        }

        foreach (IEntityValueProvider p in _singletonProviders)
        {
            ValidateOne(tracker, p.ContextName, p.EntityClass, p.FieldName, p.ValueType);
        }

        _schemaValidated = true;
    }

    private static void ValidateOne(EntityTracker tracker, string providerName,
        string entityClass, string fieldPath, Type declaredType)
    {
        RuntimeField? meta = tracker.GetFieldMeta(entityClass, fieldPath);
        if (meta is null)
        {
            throw new InvalidOperationException(
                $"provider '{providerName}': field '{fieldPath}' does not exist on "
                + $"'{entityClass}' in this demo's schema — CS2 schema drift; update the "
                + "provider's field path (schema reference: cs2-opendocs).");
        }

        if (!IsWireTypeCompatible(declaredType, meta.TypeName))
        {
            throw new InvalidOperationException(
                $"provider '{providerName}': declared type '{declaredType.Name}' is not "
                + $"compatible with wire type '{meta.TypeName}' for '{entityClass}.{fieldPath}' "
                + "— CS2 schema drift; update the provider's declared type.");
        }
    }

    /// <summary>
    ///     Validation for a provider whose field was renamed between CS2 builds
    ///     (<see cref="IMultiSchemaFieldProvider" />): at least one candidate path must exist and
    ///     be type-compatible.
    ///     <para>
    ///         Still loud when it should be. A demo carrying none of the candidates throws with
    ///         every spelling listed, which is the drift signal; what it stops being loud about
    ///         is a demo that legitimately uses the older spelling.
    ///     </para>
    /// </summary>
    private static void ValidateAnyOf(EntityTracker tracker, string providerName,
        string entityClass, IReadOnlyList<string> candidates, Type declaredType)
    {
        string? incompatible = null;
        foreach (string path in candidates)
        {
            RuntimeField? meta = tracker.GetFieldMeta(entityClass, path);
            if (meta is null)
            {
                continue;
            }

            if (IsWireTypeCompatible(declaredType, meta.TypeName))
            {
                return;
            }

            // Present but the wrong shape. Remember it so the throw below can say so, which is a
            // different fault from "this demo is the other vintage" and wants a different fix.
            incompatible ??= $"'{path}' exists with wire type '{meta.TypeName}'";
        }

        string detail = incompatible is null
            ? "none of them exist on this demo"
            : incompatible + ", which is not compatible";

        throw new InvalidOperationException(
            $"provider '{providerName}': no usable field on '{entityClass}' among "
            + $"[{string.Join(", ", candidates)}] ({detail}) for declared type "
            + $"'{declaredType.Name}' — CS2 schema drift; add the new spelling to the provider's "
            + "candidate list.");
    }

    /// <summary>
    ///     Hop-1 validation for a <see cref="ProviderSpec.ViaHandleToField" /> spec: the handle
    ///     path must EXIST on the provider's class and its wire type must be a CHandle (any
    ///     schema spelling — <c>CHandle&lt;</c>, <c>CHandle &lt;</c>, <c>CHandle&amp;lt;</c> —
    ///     matching the string-provider handle arm of <see cref="IsWireTypeCompatible" />).
    /// </summary>
    private static void ValidateHandleField(EntityTracker tracker, string providerName,
        string entityClass, string handlePath)
    {
        RuntimeField? meta = tracker.GetFieldMeta(entityClass, handlePath);
        if (meta is null)
        {
            throw new InvalidOperationException(
                $"provider '{providerName}': field '{handlePath}' does not exist on "
                + $"'{entityClass}' in this demo's schema — CS2 schema drift; update the "
                + "provider's field path (schema reference: cs2-opendocs).");
        }

        if (!meta.TypeName.StartsWith("CHandle", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"provider '{providerName}': handle-hop path '{entityClass}.{handlePath}' has "
                + $"wire type '{meta.TypeName}', which is not a CHandle — CS2 schema drift; "
                + "update the provider's spec.");
        }
    }

    /// <summary>
    ///     Declared-type ↔ wire-type compatibility. Deliberately a CLOSED allowlist — an
    ///     unknown pairing throws at prime time rather than becoming a silent null at read
    ///     time (the coercion switch's fallback arm). string accepts handle types because
    ///     string providers project a followed handle's ClassName (the active-weapon shape);
    ///     float accepts QAngle because a float provider over an angle field is declaring "one
    ///     component of this angle" (see the QAngle arm below).
    /// </summary>
    private static bool IsWireTypeCompatible(Type declaredType, string wireType)
    {
        if (declaredType == typeof(int))
        {
            return wireType is "int32" or "uint32" or "int16" or "uint16" or "int8" or "uint8"
                or "int64" or "uint64";
        }

        if (declaredType == typeof(bool))
        {
            return wireType is "bool";
        }

        if (declaredType == typeof(float))
        {
            // QAngle is admitted deliberately, and only under float. The position providers dodge
            // this check by naming a scalar leaf (CBodyComponent.m_vecX) while reading the
            // composite; an angle field has NO scalar leaf to name, so a component provider must
            // declare the QAngle itself. A float provider over a QAngle therefore means "one
            // component of this angle, in degrees", which is the only shape the rules language
            // can express: it has no vector type and no member access, so the component split has
            // to happen in the provider (PawnEyeAngleProvider, PawnAimPunchProvider). Widening
            // here rather than adding a ComponentOf knob to ProviderSpec keeps the failure loud
            // for every other pairing, which is the point of the allowlist: everything not named
            // on one of these four arms still throws at prime time instead of decaying to a
            // silent column of nulls.
            return wireType is "float32" or "CNetworkedQuantizedFloat" or "float64" or "QAngle";
        }

        if (declaredType == typeof(string))
        {
            return wireType is "CUtlString" or "CUtlSymbolLarge" or "char"
                   || wireType.StartsWith("char[", StringComparison.Ordinal)
                   || wireType.StartsWith("CHandle<", StringComparison.Ordinal)
                   || wireType.StartsWith("CHandle <", StringComparison.Ordinal)
                   || wireType.StartsWith("CHandle&lt;", StringComparison.Ordinal);
        }

        return false;
    }

    private static IPerPlayerEntityValueProvider CloneProvider(IPerPlayerEntityValueProvider p) =>
        p is IWorkerCloneable<IPerPlayerEntityValueProvider> cloneable
            ? cloneable.CloneForWorker()
            : (IPerPlayerEntityValueProvider)Activator.CreateInstance(p.GetType())!;

    private static IEntityValueProvider CloneProvider(IEntityValueProvider p) =>
        p is IWorkerCloneable<IEntityValueProvider> cloneable
            ? cloneable.CloneForWorker()
            : (IEntityValueProvider)Activator.CreateInstance(p.GetType())!;

    /// <summary>
    ///     Consumes one frame's digest: folds the previous frame's per-pawn values into the pre-frame
    ///     snapshot, runs singleton change-detection, then molotov synthesis — the (sequential, stateful)
    ///     half of the scan, the same whichever producer built the digest. Order matches the
    ///     pre-digest poll-loop → DetectMolotovThrows sequence.
    /// </summary>
    private List<NetMessage> Consume(EntityFrameDigest digest, int tick)
    {
        _scratch.Clear();

        // Pre-frame snapshot consumed inside THIS frame = the previous frame's per-pawn values (N-1 state).
        // Before _prevDigest exists (frame 0) the snapshot stays empty.
        MergePreFrameSnapshot(_prevDigest);

        ConsumeSingletons(digest, tick);
        ConsumeControllerTeams(digest, tick);
        if (_emitMolotovThrows)
        {
            ConsumeMolotovs(digest, tick);
        }

        ConsumeAimVantage(digest, tick);

        _prevDigest = digest;
        return _scratch;
    }

    /// <summary>The team <paramref name="slot" />'s controller last showed in a digest, when one has.</summary>
    public bool TryGetTeam(int slot, out int team)
    {
        team = slot is >= 0 and < 64 ? _lastTeam[slot] : -1;
        return team >= 0;
    }

    private void ConsumeControllerTeams(EntityFrameDigest digest, int tick)
    {
        if (digest.ControllerTeams is not { } teams)
        {
            return;
        }

        for (int slot = 0; slot < teams.Length; slot++)
        {
            int observed = teams[slot];
            if (observed < 0 || observed == _lastTeam[slot])
            {
                continue;
            }

            int previous = _lastTeam[slot];
            _lastTeam[slot] = observed;
            if (observed >= 2)
            {
                _scratch.Add(GameEventMessage.ForSynthesizedEvent(
                    new PlayerTeamObservedEvent(tick, tick, tick, slot, previous, observed)));
            }
        }
    }

    /// <summary>
    ///     Folds this frame's per-pawn deltas into the vantage scanner and samples one vantage per
    ///     live pawn; when a transition scanner is also present, feeds that sample to it and
    ///     synthesizes an <c>enemy_spotted</c> event per directed pair that crossed into visibility.
    ///     No-op when no vantage scanner was supplied.
    ///     <para>
    ///         The two halves are separable on purpose. Visibility needs a map bake and answers
    ///         "who can see whom"; vantage needs only the digest columns and answers "where is each
    ///         player looking, and how fast are they moving", which is what the shot-anchored aim
    ///         metrics read. Requiring geometry for the second would make counter-strafing depend on
    ///         a collision file it has nothing to do with.
    ///     </para>
    ///     <para>
    ///         Reads the CURRENT digest, not <c>_prevDigest</c>: singleton and molotov consumption
    ///         above are both current-frame, and a spot is a claim about where the players are AT the
    ///         sampled tick. The pre-frame snapshot's one-frame lag exists to answer "what was true
    ///         before this frame's event", which is not the question here.
    ///     </para>
    ///     <para>
    ///         Decode integrity gates it the same way the snapshot fold is gated, and for a stronger
    ///         reason: a compromised digest freezes per-pawn values at their last decoded state, and
    ///         frozen positions do not read as missing data, they read as ten players standing
    ///         perfectly still with a plausible set of sightlines between them.
    ///     </para>
    ///     <para>
    ///         Gating it is not enough on its own, because the transition scanner carries state
    ///         BETWEEN samples and expires it only while samples keep arriving: stop feeding it and
    ///         the visible set and the crosshair-arrival stamps freeze at their last values for the
    ///         rest of the run, which a reader cannot tell from live ones. So the gate RESETS it
    ///         rather than merely skipping it — the same "an absent pair reads as not-visible"
    ///         posture the scanner already takes for a player who drops out of the vantage set,
    ///         extended to the whole set dropping out at once. The cost is that the first contact
    ///         after decode recovers is re-reported, which is the honest reading of a hole in the
    ///         feed.
    ///     </para>
    /// </summary>
    private void ConsumeAimVantage(EntityFrameDigest digest, int tick)
    {
        if (_vantageScanner is null)
        {
            return;
        }

        if (_preFrameSnapshotFrozen || digest.DecodeCompromised)
        {
            _transitionScanner?.Reset();
            return;
        }

        PerPawnColumns rows = digest.PerPawn;
        for (int r = 0; r < rows.Count; r++)
        {
            _vantageScanner.Observe(rows, r);
        }

        IReadOnlyList<AimVantage> vantages = _vantageScanner.Sample(tick);
        if (_transitionScanner is null)
        {
            return;
        }

        IReadOnlyList<EnemySpottedEvent> spots = _transitionScanner.Sample(tick, tick, vantages, digest.Smokes);
        for (int i = 0; i < spots.Count; i++)
        {
            _scratch.Add(GameEventMessage.ForSynthesizedEvent(spots[i]));
        }
    }

    /// <summary>
    ///     Extracts the per-frame <see cref="EntityFrameDigest" /> from the layer's current (post-seek)
    ///     entity state via the shared <see cref="EntityDigestExtractor" /> — the single source of truth
    ///     the pipelined producer's workers reuse, so a chunk worker's digest is byte-identical to
    ///     this one. This is the only part of the per-frame loop that touches the entity set; the
    ///     (sequential, stateful) consume path below reads only the digest.
    /// </summary>
    private EntityFrameDigest BuildDigest()
    {
        bool prof = Profiling.Enabled;
        long s = 0, sA = 0;
        if (prof)
        {
            s = Stopwatch.GetTimestamp();
            sA = GC.GetAllocatedBytesForCurrentThread();
        }

        EntityFrameDigest d = EntityDigestExtractor.Build(
            Layer, _delta, _singletonProviders, _emitMolotovThrows, _transitionScanner is not null, _projectiles, _pawns);
        if (prof)
        {
            // Lumped under the historical "snapshot" sub-phase — it is the per-pawn sweep that dominated it;
            // poll/projectile now show only the (cheap) sequential consume below.
            _profSnapshotTicks += Stopwatch.GetTimestamp() - s;
            _profSnapshotAlloc += GC.GetAllocatedBytesForCurrentThread() - sA;
        }

        return d;
    }

    /// <summary>
    ///     Folds the previous frame's per-pawn values into the persistent pre-frame snapshot. Updates
    ///     live slots and RETAINS stale entries for slots not present this frame — byte-identical to the
    ///     pre-digest <c>CapturePreFrameSnapshot</c>, which wrote each live <c>(providerIdx, slot)</c>
    ///     and never cleared.
    ///     <para>
    ///         Decode-compromise guard: the fold FREEZES (sticky, for the rest of the run) at the first digest whose
    ///         producing tracker had recorded a decode error — that digest and everything after it may
    ///         carry stale or partially-decoded per-pawn state, and folding it corrupts the damage cap
    ///         (see <see cref="EntityFrameDigest.DecodeCompromised" />). Consumers then see only values
    ///         captured while decode was provably clean; on decode-broken demos that means
    ///         <see cref="GetPreFrameValue" /> returns <c>null</c> and event-tracked fallbacks take over.
    ///     </para>
    /// </summary>
    private void MergePreFrameSnapshot(EntityFrameDigest? prev)
    {
        if (prev is null || _preFrameSnapshotFrozen)
        {
            return;
        }

        if (prev.DecodeCompromised)
        {
            _preFrameSnapshotFrozen = true;
            return;
        }

        PerPawnColumns rows = prev.PerPawn;
        if (rows.Count == 0)
        {
            // Nothing to fold, and nothing to judge: an empty row set (the shared Empty instance
            // included) carries no layout worth comparing, so a hand-built digest with no rows
            // is accepted whatever providers the scanner holds.
            return;
        }

        if (!ReferenceEquals(rows.Layout, _lastCompatibleLayout))
        {
            if (!rows.Layout.IsCompatibleWith(_layout))
            {
                throw new InvalidOperationException(
                    "digest column layout does not match this scanner's per-player provider set "
                    + $"({rows.Layout.Count} columns in the digest, {_layout.Count} on the scanner); "
                    + "digests must be produced over the same providers in the same order");
            }

            _lastCompatibleLayout = rows.Layout;
        }

        _preFrameSnapshot.Fold(rows);
    }

    /// <summary>
    ///     Singleton-provider change detection over the digest's post-seek values. Identical semantics
    ///     to the pre-digest poll loop — writes the value node on every observed change and synthesizes
    ///     an event only when the transition matches <see cref="IEntityValueProvider.EmitOn" />.
    /// </summary>
    private void ConsumeSingletons(EntityFrameDigest digest, int tick)
    {
        for (int i = 0; i < _tracked.Count; i++)
        {
            TrackedProvider t = _tracked[i];
            object? newValue = digest.Singletons[i];

            // No value yet (entity not spawned) — leave cached state untouched.
            if (newValue is null)
            {
                continue;
            }

            if (ReferenceEquals(newValue, t.LastValue) || Equals(newValue, t.LastValue))
            {
                continue;
            }

            UpdateValueNode(t.ValueNode, newValue);

            if (ShouldEmit(t.Provider.EmitOn, t.LastValue, newValue))
            {
                _scratch.Add(BuildSynthesizedMessage(t.Provider, tick, t.LastValue, newValue));
            }

            t.LastValue = newValue;
            _tracked[i] = t;
        }
    }

    /// <summary>
    ///     Synthesizes one <c>molotov_thrown</c> event per <c>CMolotovProjectile</c> in the digest,
    ///     deduped by (index, serial) across the run so a projectile still alive on the next frame
    ///     does not throw again; the serial in the key is what keeps a later projectile reusing the
    ///     same entity index a separate throw.
    ///     <para>
    ///         The dedup is keyed on EMISSION, not on first sighting: a projectile whose thrower has
    ///         not resolved yet (<c>slot &lt; 0</c>) is left out of the set, so a later frame that
    ///         does resolve it still emits. <c>m_hThrower</c> is not reliably networked on the frame
    ///         the entity is created — a pawn killed on that same frame reports the 24-bit invalid
    ///         handle, which <c>EntityDigestExtractor.ResolveThrowerSlot</c> folds to -1 — and
    ///         recording the projectile as seen on that first sighting would drop the throw for the
    ///         rest of the run with no diagnostic, silently undercounting the shipped
    ///         <c>molotov_used</c> stat.
    ///     </para>
    /// </summary>
    private void ConsumeMolotovs(EntityFrameDigest digest, int tick)
    {
        foreach ((int idx, int serial, int slot) in digest.Molotovs)
        {
            if (slot < 0 || !_seenMolotovs.Add((idx, serial)))
            {
                continue;
            }

            // Frame clock in all three slots, deliberately: the scanner's tick IS the frame clock
            // and it holds no ServerStartTick to build the absolute one. Same convention as the
            // EnemySpottedEvent emitted in ConsumeAimVantage above; the event's class doc carries
            // the reasoning and the event.tick caveat that follows from it.
            _scratch.Add(GameEventMessage.ForSynthesizedEvent(
                new MolotovThrownEvent(tick, tick, tick, slot)));
        }
    }

    /// <summary>
    ///     Returns the scanner's per-frame profiling accumulators. Returns <c>default</c>
    ///     (<see cref="ScannerProfilingSnapshot.Enabled" /> = <c>false</c>) when no profiled run has driven
    ///     this scanner — see <see cref="Profiling.Enabled" />.
    /// </summary>
    public ScannerProfilingSnapshot GetProfilingSnapshot() =>
        // ProviderPoll/ProjectileScan phases were folded into the snapshot/digest build at the Track-4
        // seam (always 0 now).
        _profiled
            ? new ScannerProfilingSnapshot(true, _profSeekTicks, 0L, 0L,
                _profSnapshotTicks, _profSeekAlloc, 0L, 0L,
                _profSnapshotAlloc, _profFramesPolled, _profFoldTicks, _profFoldAlloc)
            : default;

    /// <summary>
    ///     Reads the snapshot of <paramref name="provider" />'s value for
    ///     <paramref name="playerSlot" /> as captured at the START of the most recent
    ///     <see cref="AdvanceAndPollAt" /> call. Returns <c>null</c> if no snapshot exists
    ///     (provider not registered, slot never populated, or first frame). The value is
    ///     PRE-FRAME relative to the currently-in-flight frame.
    /// </summary>
    public object? GetPreFrameValue(IPerPlayerEntityValueProvider provider, int playerSlot)
    {
        // Cached index: the linear IndexOf was fine at 4 providers but scales with
        // catalog width × per-event read volume. Reference-keyed — provider instances are
        // registration-stable for the scanner's lifetime.
        if (!_perPlayerProviderIndex.TryGetValue(provider, out int idx))
        {
            // Loud arm: with reference-gated activation, a read against a provider
            // the scanner never snapshots is a WIRING BUG (the builder gated it out while some
            // compile site still resolved it) — silently returning null here would zero every
            // such read. An unsnapshotted-but-registered provider still reads null below.
            throw new InvalidOperationException(
                $"per-player provider '{provider.Name}' is not registered on this scanner — "
                + "it was reference-gated out at build time but something still reads it");
        }

        // The one box on the per-pawn path: per event read, not per frame.
        return _preFrameSnapshot.GetBoxed(idx, playerSlot);
    }

    private static EntityChangeMessage BuildSynthesizedMessage(IEntityValueProvider provider, int tick, object? oldValue, object? newValue)
    {
        // Construct EntityValueChangedEvent<TMarker> via reflection. The marker type is only
        // known at provider-registration time; no compile-time generic dispatch is available.
        Type closed = typeof(EntityValueChangedEvent<>).MakeGenericType(provider.MarkerType);
        EntityValueChangedEvent evt = (EntityValueChangedEvent)Activator.CreateInstance(closed)!;
        SetInitProperty(closed, evt, nameof(EntityValueChangedEvent.Tick), tick);
        SetInitProperty(closed, evt, nameof(EntityValueChangedEvent.OldValue), oldValue);
        SetInitProperty(closed, evt, nameof(EntityValueChangedEvent.NewValue), newValue);
        return new EntityChangeMessage(evt);
    }

    private static void SetInitProperty(Type t, object instance, string name, object? value)
    {
        PropertyInfo prop = t.GetProperty(name, BindingFlags.Instance | BindingFlags.Public)!;
        prop.SetValue(instance, value);
    }

    private static bool ShouldEmit(ChangeDirection dir, object? oldValue, object? newValue)
    {
        // Rising/falling semantics for bool. For non-bool values, "rising" is "any change to
        // a non-default value" — only a bool provider ships today, so this branch is exercised
        // only by tests. Both fires for any change regardless.
        if (dir == ChangeDirection.Both)
        {
            return true;
        }

        if (newValue is bool b)
        {
            return dir == ChangeDirection.RisingOnly ? b : !b;
        }

        // Non-bool fallback: treat null-or-default → non-default as rising.
        bool wasDefault = oldValue is null || oldValue.Equals(Activator.CreateInstance(oldValue.GetType()));
        bool isDefault = newValue is null || newValue.Equals(Activator.CreateInstance(newValue.GetType()));
        return dir == ChangeDirection.RisingOnly ? wasDefault && !isDefault : !wasDefault && isDefault;
    }

    private static void UpdateValueNode(StateNode node, object value)
    {
        // The node is GenericValueNode<T> or GenericBoolNode at runtime. Use reflection on
        // SetValue / Activate to avoid carrying T through the registry.
        Type nodeType = node.GetType();
        MethodInfo? set = nodeType.GetMethod("SetValue", BindingFlags.Instance | BindingFlags.Public);
        if (set is not null)
        {
            set.Invoke(node, [value]);
            return;
        }

        if (node is BoolNode b)
        {
            if (value is true)
            {
                b.Activate();
            }
            else
            {
                b.Deactivate();
            }
        }
    }

    /// <param name="provider">Push-model provider to track.</param>
    /// <param name="valueNode">Backing node that mirrors the provider's value.</param>
    /// <param name="initial">Seed value for change detection (typically the provider's default).</param>
    private struct TrackedProvider(IEntityValueProvider provider, StateNode valueNode, object? initial)
    {
        /// <summary>The push-model provider being tracked.</summary>
        public readonly IEntityValueProvider Provider = provider;

        /// <summary>The state node the provider's value is mirrored into.</summary>
        public readonly StateNode ValueNode = valueNode;

        /// <summary>Last value emitted; used to detect transitions and gate change events.</summary>
        public object? LastValue = initial;
    }
}
