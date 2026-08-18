#region

using System.Diagnostics;
using System.Reflection;
using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Analysis.Diagnostics;
using CS2DemoKit.Analysis.Events;
using CS2DemoKit.Analysis.Plugins;
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
///         their own scanner via <see cref="IDemoContext.CreateEntityLayer" /> (one layer = one
///         scanner). The scanner also writes the provider's <see cref="ValueNode{T}" /> on every
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
    // SLOT. Captured at the START of each AdvanceAndPoll call — BEFORE the layer advances —
    // so when consumers read inside that frame's inner-message processing they see the
    // PREVIOUS frame's value. This is the right input for "compute pre-event state from
    // entity ground truth" because the current frame's PacketEntities update arrives
    // concurrently with the event we're handling.
    private readonly List<IPerPlayerEntityValueProvider> _perPlayerProviders;
    private readonly Dictionary<(int ProviderIdx, int Slot), object?> _preFrameSnapshot = [];

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
    // separately so the shared EntityDigestExtractor (used by the parallel producer too) reads the
    // same providers in the same order.
    private readonly List<IEntityValueProvider> _singletonProviders;
    private readonly List<TrackedProvider> _tracked;

    // When set (via PrecomputeParallelDigests), AdvanceAndPollAt consumes digest[frameIdx]
    // instead of driving the layer (SeekToTick + BuildDigest) sequentially. These are proven element-wise
    // identical to the sequential digests (ParallelDigestEquivalenceTests), so golden output is preserved.

    // The previous frame's digest. The pre-frame snapshot consumed inside frame N
    // is the per-pawn state from frame N-1 — which is exactly _prevDigest.PerPawn (built last frame from
    // N-1's post-seek state == N's pre-seek state). Holding one frame back lets the digest carry the
    // "previous values", so a parallel producer needn't preserve the layer's pre-seek state.
    private EntityFrameDigest? _prevDigest;

    private int _profFramesPolled;

    // Whether this scanner captured profiling data — latched the first time a profiled per-frame seam runs.
    // Reported as ScannerProfilingSnapshot.Enabled, decoupled from the live flag.
    private bool _profiled;

    private long _profPrecomputeAlloc;

    // The up-front parallel decode (PrecomputeParallelDigests). When digests are
    // precomputed, the per-frame seek/snapshot accumulators above stay ~0 and this holds the moved cost.
    private long _profPrecomputeTicks;

    // Allocated-bytes deltas for the same per-frame sub-phases (cheap: GetAllocatedBytes is a
    // non-allocating intrinsic). Attributes the eval allocation total to its per-frame source.
    private long _profSeekAlloc;

    // Scanner-level profiling (opt-in at RUNTIME via Profiling.Enabled). These bracket the per-frame
    // sub-phases of AdvanceAndPoll; SeekTicks ⊇ the EntityTracker-internal decode captured by
    // EntityTracker.GetProfilingSnapshot(). The fold call-sites are guarded by `if (Profiling.Enabled)`,
    // so a default run touches none of them. Read once post-run via GetProfilingSnapshot().
    private long _profSeekTicks;
    private long _profSnapshotAlloc;
    private long _profSnapshotTicks;

    // Provider schema validation: latched once every provider's target class has
    // descriptors and every declared type matched the wire schema. Loud on drift — see
    // TryValidateProviderSchema.
    private bool _schemaValidated;

    /// <param name="layer">The entity-state layer the scanner reads from; advanced one frame at a time.</param>
    /// <param name="providers">Singleton-entity providers paired with their backing value nodes (push model).</param>
    /// <param name="perPlayerProviders">Per-player providers polled into the pre-frame snapshot (pull model).</param>
    /// <param name="emitMolotovThrows">
    ///     When true, synthesize a <c>molotov_thrown</c> event per newly-created
    ///     CMolotovProjectile.
    /// </param>
    public EntityChangeScanner(
        EntityStateLayer layer,
        IReadOnlyList<(IEntityValueProvider Provider, StateNode ValueNode)> providers,
        IReadOnlyList<IPerPlayerEntityValueProvider>? perPlayerProviders = null,
        bool emitMolotovThrows = false)
    {
        Layer = layer;
        _emitMolotovThrows = emitMolotovThrows;
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
    }

    /// <summary>Test seam: the precomputed digests (ProviderDigestParityTests compares two scanners').</summary>
    internal EntityFrameDigest?[]? PrecomputedDigests { get; private set; }

    /// <summary>
    ///     Entity-state layer owned by this scanner. Exposed for per-event reads
    ///     in edges that follow the pre-frame pull model. Layer is advanced once per frame by
    ///     <see cref="AdvanceAndPoll" />; do not call <c>SeekToTick</c> on it directly.
    /// </summary>
    public EntityStateLayer Layer { get; }

    /// <summary>
    ///     Test seam: inject hand-built digests. PrecomputeParallelDigests' idempotence guard
    ///     makes EvaluateCore's unconditional call a no-op, so AdvanceAndPollAt consumes these.
    /// </summary>
    internal void SetPrecomputedDigests(EntityFrameDigest?[] digests) => PrecomputedDigests = digests;

    /// <summary>
    ///     Seeks the layer to <paramref name="tick" /> and returns any synthesized change
    ///     messages produced by the providers whose values transitioned this frame.
    ///     Returns an empty list (not null) when nothing changed; callers may skip iteration
    ///     by checking the count first.
    /// </summary>
    public IReadOnlyList<NetMessage> AdvanceAndPoll(int tick)
    {
        bool prof = Profiling.Enabled;
        long seekStart = 0, seekStartA = 0;
        if (prof)
        {
            _profiled = true;
            _profFramesPolled++;
            seekStart = Stopwatch.GetTimestamp();
            seekStartA = GC.GetAllocatedBytesForCurrentThread();
        }

        Layer.SeekToTick(tick);
        if (prof)
        {
            _profSeekTicks += Stopwatch.GetTimestamp() - seekStart;
            _profSeekAlloc += GC.GetAllocatedBytesForCurrentThread() - seekStartA;
        }

        // Build this frame's digest from the POST-seek entity state, then consume it.
        return Consume(BuildDigest(), tick);
    }

    /// <summary>
    ///     Frame-indexed variant of <see cref="AdvanceAndPoll" /> used by the eval loop. When digests were
    ///     precomputed in parallel (<see cref="PrecomputeParallelDigests" />), consumes
    ///     <c>digest[frameIndex]</c> without driving the layer — the parallel digests are proven
    ///     element-wise identical to the sequential ones, so the consumed output is unchanged. Falls back to
    ///     the sequential layer-driven path when nothing was precomputed (e.g. direct test callers, or a
    ///     config the producer can't chunk).
    /// </summary>
    public IReadOnlyList<NetMessage> AdvanceAndPollAt(int frameIndex, int tick)
    {
        bool prof = Profiling.Enabled;
        if (prof)
        {
            _profiled = true;
            _profFramesPolled++;
        }

        if (PrecomputedDigests is not null)
        {
            EntityFrameDigest? precomputed = PrecomputedDigests[frameIndex];
            if (precomputed is not null)
            {
                // Release-after-consume: the digest stream holds every frame's boxed
                // provider values; dropping each entry once consumed caps resident memory at
                // O(1) digests instead of O(frames). The LAST frame's consume clears the array
                // so a re-evaluation's PrecomputeParallelDigests re-produces (its idempotence
                // guard keys on null).
                PrecomputedDigests[frameIndex] = null;
                if (frameIndex == PrecomputedDigests.Length - 1)
                {
                    PrecomputedDigests = null;
                }

                return Consume(precomputed, tick);
            }

            // Already-consumed entry (a restart after a partial run, e.g. cancellation):
            // fall back to the sequential path for correctness; the next full precompute
            // re-establishes the fast path.
        }

        long seekStart = 0, seekStartA = 0;
        if (prof)
        {
            seekStart = Stopwatch.GetTimestamp();
            seekStartA = GC.GetAllocatedBytesForCurrentThread();
        }

        Layer.SeekToTick(tick);
        if (prof)
        {
            _profSeekTicks += Stopwatch.GetTimestamp() - seekStart;
            _profSeekAlloc += GC.GetAllocatedBytesForCurrentThread() - seekStartA;
        }

        // Sequential-path schema validation — latches once all provider classes
        // have descriptors (first FullPacket); throws on drift. Zero cost once latched.
        if (!_schemaValidated)
        {
            TryValidateProviderSchema(Layer.Tracker);
        }

        return Consume(BuildDigest(), tick);
    }

    /// <summary>
    ///     Decodes the whole demo's entity stream in parallel up front (chunked at
    ///     <c>DEM_FullPacket</c> boundaries by <see cref="ParallelDigestProducer" />), so the eval loop's
    ///     <see cref="AdvanceAndPollAt" /> consumes a precomputed digest per frame instead of driving the
    ///     layer sequentially. Each worker is handed its OWN provider instances (the producer's factories)
    ///     because some providers cache mutable state (e.g. <c>FreezePeriodProvider</c>'s cached entity
    ///     index); the per-frame consume that follows still runs sequentially on this scanner.
    /// </summary>
    /// <param name="frames">The demo's frame list.</param>
    /// <param name="onProgress">Fraction-complete in [0, 1], invoked once per chunk from worker threads.</param>
    /// <param name="maxDegreeOfParallelism">
    ///     Caps the decode's concurrent worker count; <c>null</c> (the default) leaves it unbounded.
    ///     This is the knob <see cref="AnalysisOptions.MaxDegreeOfParallelism" /> plumbs through — set
    ///     it when several demos decode concurrently in one process, so they don't each fan out to
    ///     every core.
    /// </param>
    /// <param name="cancellationToken">Observed per frame inside each worker.</param>
    public void PrecomputeParallelDigests(IReadOnlyList<DemoFrame> frames, Action<double>? onProgress = null,
        int? maxDegreeOfParallelism = null,
        CancellationToken cancellationToken = default)
    {
        // Idempotent: digests are deterministic over the scanner's demo, produced once per
        // scanner. EvaluateCore calls this unconditionally per evaluation — re-runs reuse the
        // existing digests (Consume never mutates them), and injected test digests
        // (SetPrecomputedDigests) survive instead of being silently overwritten.
        if (PrecomputedDigests is not null)
        {
            return;
        }

        // On the parallel path the scanner's own layer never advances, so schema
        // validation primes a THROWAWAY layer over the first frames (the initial FullPacket
        // decodes every entity class within a few frames) and validates against its tracker.
        // The scanner's own layer stays cold — the sequential fallback path's forward-only
        // seek contract is untouched.
        if (!_schemaValidated && frames.Count > 0)
        {
            EntityStateLayer probe = new(frames);
            for (int f = 32; f <= Math.Min(frames.Count, 608) && !_schemaValidated; f += 96)
            {
                probe.SeekBeforeFrame(f);
                TryValidateProviderSchema(probe.Tracker);
            }
        }

        using Activity? span =
            AnalysisDiagnostics.ActivitySource.StartActivity("analysis.precompute");
        bool prof = Profiling.Enabled;
        long s = 0;
        if (prof)
        {
            _profiled = true;
            s = Stopwatch.GetTimestamp();
        }

        PrecomputedDigests = ParallelDigestProducer.Produce(
            frames,
            () => _perPlayerProviders.Select(CloneProvider).ToList(),
            () => _singletonProviders.Select(CloneProvider).ToList(),
            _emitMolotovThrows,
            maxDegreeOfParallelism,
            onProgress,
            cancellationToken);
        if (prof)
        {
            _profPrecomputeTicks += Stopwatch.GetTimestamp() - s;
            // The decode allocates on Parallel.For worker threads, so the calling-thread
            // GetAllocatedBytesForCurrentThread delta used here previously missed every worker but this
            // one (under-count that worsened with core count). The ticks bracket above is wall-clock of
            // the whole parallel phase and stays correct; for alloc, read the producer's per-worker sum.
            _profPrecomputeAlloc += ParallelDigestProducer.ReadWorkerAllocBytes();
        }
    }

    // Fresh provider instance of the same concrete type for a parallel worker (all current providers have
    // parameterless ctors). If a future provider takes constructor state, give it a clone hook instead.
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
    ///     string providers project a followed handle's ClassName (the active-weapon shape).
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
            return wireType is "float32" or "CNetworkedQuantizedFloat" or "float64";
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
    ///     half of the scan, shared by the sequential and precomputed entry points. Order matches the
    ///     pre-digest poll-loop → DetectMolotovThrows sequence.
    /// </summary>
    private List<NetMessage> Consume(EntityFrameDigest digest, int tick)
    {
        _scratch.Clear();

        // Pre-frame snapshot consumed inside THIS frame = the previous frame's per-pawn values (N-1 state).
        // Before _prevDigest exists (frame 0) the snapshot stays empty.
        MergePreFrameSnapshot(_prevDigest);

        ConsumeSingletons(digest, tick);
        if (_emitMolotovThrows)
        {
            ConsumeMolotovs(digest, tick);
        }

        _prevDigest = digest;
        return _scratch;
    }

    /// <summary>
    ///     Extracts the per-frame <see cref="EntityFrameDigest" /> from the layer's current (post-seek)
    ///     entity state via the shared <see cref="EntityDigestExtractor" /> — the single source of truth
    ///     reused by the parallel chunk decoder, so a precomputed parallel digest is
    ///     byte-identical to this one. This is the only part of the per-frame loop that touches the entity
    ///     set; the (sequential, stateful) consume path below reads only the digest.
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
            Layer, _perPlayerProviders, _singletonProviders, _emitMolotovThrows);
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

        foreach ((int slot, object?[] values) in prev.PerPawn)
        {
            for (int p = 0; p < values.Length; p++)
            {
                if (values[p] is not null)
                {
                    _preFrameSnapshot[(p, slot)] = values[p];
                }
            }
        }
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
    ///     Synthesizes one <c>molotov_thrown</c> event per newly-seen <c>CMolotovProjectile</c> in the
    ///     digest, deduped by (index, serial) across the run. Identical to the pre-digest
    ///     <c>DetectMolotovThrows</c>: every live molotov is recorded as seen, but only those with a
    ///     resolvable thrower slot emit.
    /// </summary>
    private void ConsumeMolotovs(EntityFrameDigest digest, int tick)
    {
        foreach ((int idx, int serial, int slot) in digest.Molotovs)
        {
            if (!_seenMolotovs.Add((idx, serial)))
            {
                continue;
            }

            if (slot < 0)
            {
                continue;
            }

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
        // seam (always 0 now); the up-front parallel decode is reported as PrecomputeTicks/Alloc.
        _profiled
            ? new ScannerProfilingSnapshot(true, _profSeekTicks, 0L, 0L,
                _profSnapshotTicks, _profSeekAlloc, 0L, 0L,
                _profSnapshotAlloc, _profFramesPolled, _profPrecomputeTicks, _profPrecomputeAlloc)
            : default;

    /// <summary>
    ///     Reads the snapshot of <paramref name="provider" />'s value for
    ///     <paramref name="playerSlot" /> as captured at the START of the most recent
    ///     <see cref="AdvanceAndPoll" /> call. Returns <c>null</c> if no snapshot exists
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

        return _preFrameSnapshot.GetValueOrDefault((idx, playerSlot));
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
