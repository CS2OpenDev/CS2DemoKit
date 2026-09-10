#region

using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using CS2DemoKit.Analysis.Plugins;
using CS2DemoKit.Parser;
using CS2DemoKit.Parser.EntityTracking;

#endregion

namespace CS2DemoKit.Analysis.Visibility;

/// <summary>
///     Computes the engine-fidelity <b>"time enemy was visible"</b> stat: for every directed enemy pair
///     (viewer A → enemy E) it accumulates two durations — <b>exposed</b> (A's eye had a clear 3D line of
///     sight to some point on E's body) and <b>could-see</b> (exposed AND E was inside A's view frustum, i.e.
///     on A's screen). LOS is recomputed from baked map collision via <see cref="VisibilityEngine" /> — NOT
///     the demo's <c>spotted</c> bit (Guiding Principle 2). Multi-anchor hitbox sampling (any anchor clear ⇒
///     exposed) is our addition over awpy's point-to-point.
///     <para>
///         <b>Layering:</b> the caller injects a position resolver (<c>PositionUtil.CellToWorld</c>
///         is the packaged one) so the oracle-pinned cell constant stays in a single place, and injects the
///         geometry as a built <see cref="VisibilityEngine" /> — this type does no file I/O and knows
///         nothing about where bakes live (see <see cref="CollisionAssetLocator" />). Runs a standalone sequential
///         replay (the main eval pass is digest-precomputed, not live-positioned per tick — see the
///         architecture map) sampling every Nth tick; decode cost is one extra pass (a future lever is
///         capturing vantage in the existing digest walk).
///     </para>
///     <para>
///         <b>Smoke model:</b> active smoke clouds occlude <b>could-see</b> (vision) but never <b>exposed</b>
///         (geometric line of fire) — a smoked-off enemy reads <i>exposed but not seen</i>, which is faithful: it's
///         exactly why through-smoke kills happen. See <see cref="SmokeVolumes" />.
///     </para>
///     <para>
///         <b>Known gaps (deferred):</b> flashbangs aren't modelled (a flashed viewer still "sees");
///         non-gameplay tick gaps are dt-capped rather than round-gated; hitbox anchors approximate the animated
///         hitboxes.
///     </para>
/// </summary>
public static class VisibilityAnalyzer
{
    /// <summary>The smoke-projectile entity class whose active clouds occlude vision.</summary>
    private const string SmokeClass = "CSmokeGrenadeProjectile";

    /// <summary>Extracts a live pawn's vantage. Null when position can't be reconstructed (dormant/pre-spawn).</summary>
    public static Vantage? TryVantage(int slot, EntityState pawn, Func<EntityState, Vector3?> resolvePosition)
    {
        if (resolvePosition(pawn) is not { } feet)
        {
            return null;
        }

        int team = CoerceInt(pawn["m_iTeamNum"], -1);
        float duck = CoerceFloat(pawn["m_pMovementServices.m_flDuckAmount"], 0f);
        Vector3 eye = PlayerVantage.Eye(feet, duck);

        Vector3 fwd = default;
        bool hasFwd = false;
        if (pawn.TryGet<Vector3>("m_angEyeAngles") is { } ang)
        {
            fwd = PlayerVantage.Forward(ang.X, ang.Y); // QAngle: pitch=X, yaw=Y (degrees)
            hasFwd = true;
        }

        return new Vantage(slot, team, feet, eye, fwd, hasFwd, duck);
    }

    /// <summary>Are viewer and target on opposing live teams (both team ids &gt; 1 and unequal)?</summary>
    public static bool AreEnemies(in Vantage viewer, in Vantage target) =>
        viewer.Team > 1 && target.Team > 1 && viewer.Team != target.Team;

    /// <summary>Overload without dynamic smoke occluders (equivalent to no active smokes).</summary>
    public static (bool Exposed, bool CouldSee) EvaluatePair(
        VisibilityEngine engine, in Vantage viewer, in Vantage target, float yawHalfDeg, float pitchHalfDeg)
        => EvaluatePair(engine, viewer, target, yawHalfDeg, pitchHalfDeg, ReadOnlySpan<Vector4>.Empty);

    /// <summary>
    ///     Evaluates one directed pair: <c>exposed</c> = any body anchor has clear LOS from the viewer's eye;
    ///     <c>couldSee</c> = some anchor is clear AND inside the viewer's frustum AND not blocked by smoke.
    ///     could-see ⊆ exposed by construction. Smoke occludes <b>could-see only</b> (vision), never exposed
    ///     (geometric line of fire) — see <see cref="SmokeVolumes" />. Each smoke is a sphere
    ///     <c>(centre.xyz, radius)</c>. Raycasts only the anchors that could change a result (early-exit).
    /// </summary>
    public static (bool Exposed, bool CouldSee) EvaluatePair(
        VisibilityEngine engine, in Vantage viewer, in Vantage target,
        float yawHalfDeg, float pitchHalfDeg, ReadOnlySpan<Vector4> smokes) =>
        Evaluate(engine, viewer, target, yawHalfDeg, pitchHalfDeg, smokes, Span<int>.Empty, needExposed: true);

    /// <summary>
    ///     <see cref="EvaluatePair(VisibilityEngine, in Vantage, in Vantage, float, float, ReadOnlySpan{Vector4})" />
    ///     with last-occluder hints, one per body anchor; see
    ///     <see cref="CouldSee(VisibilityEngine, in Vantage, in Vantage, float, float, ReadOnlySpan{Vector4}, Span{int})" />
    ///     for the contract.
    /// </summary>
    /// <param name="engine">The loaded collision geometry.</param>
    /// <param name="viewer">The player whose view is being asked about.</param>
    /// <param name="target">The enemy being looked for.</param>
    /// <param name="yawHalfDeg">Half the horizontal FOV in degrees.</param>
    /// <param name="pitchHalfDeg">Half the vertical FOV in degrees.</param>
    /// <param name="smokes">Active smoke spheres <c>(centre.xyz, radius)</c>.</param>
    /// <param name="occluderHints">
    ///     Per-anchor hint slots for this directed pair, at least <see cref="PlayerVantage.MaxAnchors" />
    ///     long, or empty for no hints.
    /// </param>
    public static (bool Exposed, bool CouldSee) EvaluatePair(
        VisibilityEngine engine, in Vantage viewer, in Vantage target,
        float yawHalfDeg, float pitchHalfDeg, ReadOnlySpan<Vector4> smokes, Span<int> occluderHints)
    {
        RequireHintSpan(occluderHints);
        return Evaluate(engine, viewer, target, yawHalfDeg, pitchHalfDeg, smokes, occluderHints, needExposed: true);
    }

    /// <summary>
    ///     The could-see half of <see cref="EvaluatePair(VisibilityEngine, in Vantage, in Vantage, float, float, ReadOnlySpan{Vector4})" />
    ///     alone, for a caller that never reads <c>exposed</c>: true iff some body anchor is inside
    ///     the viewer's frustum, not behind smoke, and has clear line of sight from the viewer's eye.
    ///     <para>
    ///         Same anchors, same frustum, same smoke test, same ray, same loop. What differs is what
    ///         the loop is allowed to skip: an anchor outside the frustum or behind smoke cannot make
    ///         could-see true, so no ray is cast for it here, whereas <c>EvaluatePair</c> still has to
    ///         cast it while <c>exposed</c> is unknown. On a real demo roughly half of all anchors
    ///         fall outside the viewer's frustum and nearly every ray cast for one is occluded, which
    ///         is why this exists. The answer is identical by construction: could-see is an OR over
    ///         anchors of three independent predicates, and this evaluates the two cheap ones first.
    ///     </para>
    /// </summary>
    /// <param name="engine">The loaded collision geometry.</param>
    /// <param name="viewer">The player whose view is being asked about.</param>
    /// <param name="target">The enemy being looked for.</param>
    /// <param name="yawHalfDeg">Half the horizontal FOV in degrees.</param>
    /// <param name="pitchHalfDeg">Half the vertical FOV in degrees.</param>
    /// <param name="smokes">Active smoke spheres <c>(centre.xyz, radius)</c>.</param>
    public static bool CouldSee(
        VisibilityEngine engine, in Vantage viewer, in Vantage target,
        float yawHalfDeg, float pitchHalfDeg, ReadOnlySpan<Vector4> smokes) =>
        Evaluate(engine, viewer, target, yawHalfDeg, pitchHalfDeg, smokes, Span<int>.Empty, needExposed: false).CouldSee;

    /// <summary>
    ///     <see cref="CouldSee(VisibilityEngine, in Vantage, in Vantage, float, float, ReadOnlySpan{Vector4})" />
    ///     with last-occluder hints. <paramref name="occluderHints" /> holds one slot per body
    ///     anchor for THIS directed pair: the triangle that blocked that anchor's sightline the last
    ///     time this pair was evaluated, or -1. Each slot is handed to
    ///     <see cref="VisibilityEngine.IsVisible(Vector3, Vector3, ref int)" /> for its anchor and
    ///     updated by it, so a caller that keeps one span per (viewer, target) across samples gets
    ///     most of its rays answered by a single triangle test.
    ///     <para>
    ///         The verdict is identical with hints and without, for any hint contents: the hinted
    ///         test answers occluded only where the traversal would have reached and hit that
    ///         triangle, and otherwise runs the traversal unchanged (see
    ///         <see cref="TriangleBvh.AnyHit(Vector3, Vector3, float, float, ref int)" />). The
    ///         anchors are built in a fixed order, so slot <c>i</c> is the same body point from one
    ///         sample to the next.
    ///     </para>
    /// </summary>
    /// <param name="engine">The loaded collision geometry.</param>
    /// <param name="viewer">The player whose view is being asked about.</param>
    /// <param name="target">The enemy being looked for.</param>
    /// <param name="yawHalfDeg">Half the horizontal FOV in degrees.</param>
    /// <param name="pitchHalfDeg">Half the vertical FOV in degrees.</param>
    /// <param name="smokes">Active smoke spheres <c>(centre.xyz, radius)</c>.</param>
    /// <param name="occluderHints">
    ///     Per-anchor hint slots for this directed pair, at least <see cref="PlayerVantage.MaxAnchors" />
    ///     long, or empty for no hints.
    /// </param>
    public static bool CouldSee(
        VisibilityEngine engine, in Vantage viewer, in Vantage target,
        float yawHalfDeg, float pitchHalfDeg, ReadOnlySpan<Vector4> smokes, Span<int> occluderHints)
    {
        RequireHintSpan(occluderHints);
        return Evaluate(engine, viewer, target, yawHalfDeg, pitchHalfDeg, smokes, occluderHints, needExposed: false).CouldSee;
    }

    private static void RequireHintSpan(Span<int> occluderHints)
    {
        if (!occluderHints.IsEmpty && occluderHints.Length < PlayerVantage.MaxAnchors)
        {
            throw new ArgumentException(
                $"occluder hints must be empty or hold at least {PlayerVantage.MaxAnchors} slots, one per body anchor",
                nameof(occluderHints));
        }
    }

    /// <summary>
    ///     The one pair loop behind both public entry points. <paramref name="needExposed" /> decides
    ///     whether a ray that can only decide <c>exposed</c> is worth casting; nothing else depends
    ///     on it, so the two callers cannot disagree on a verdict they both compute.
    ///     <para>
    ///         Per anchor, the three predicates are ordered cheapest first: frustum (a few dot
    ///         products), smoke (a few more per active cloud), then the ray. An anchor that fails
    ///         either cheap test can only ever contribute to <c>exposed</c>, so its ray is cast only
    ///         while <c>exposed</c> is both wanted and still false. The result is the same in every
    ///         order because each anchor's contribution is a pure function of its own three tests.
    ///     </para>
    ///     <para>
    ///         Counter bookkeeping: every anchor lands in exactly one of cast, skipped-by-gate or
    ///         skipped-by-early-exit, so <c>RaysCast + RaysSkippedByGate + RaysSkippedByEarlyExit</c>
    ///         equals <c>AnchorsTotal</c> in both modes. Gate skips are the frustum and smoke rejects
    ///         above; early-exit skips are the anchors never visited once the pair is decided.
    ///     </para>
    ///     <para>
    ///         <paramref name="occluderHints" /> is empty or one slot per anchor; when present, anchor
    ///         <c>i</c>'s ray is cast through slot <c>i</c>. It changes which triangle is tested
    ///         first, never the verdict.
    ///     </para>
    /// </summary>
    private static (bool Exposed, bool CouldSee) Evaluate(
        VisibilityEngine engine, in Vantage viewer, in Vantage target,
        float yawHalfDeg, float pitchHalfDeg, ReadOnlySpan<Vector4> smokes, Span<int> occluderHints,
        bool needExposed)
    {
        Span<Vector3> anchors = stackalloc Vector3[PlayerVantage.MaxAnchors];
        int n = PlayerVantage.BuildAnchors(target.Feet, target.Duck, viewer.Eye, anchors);
        ViewFrustum frustum = viewer.HasForward
            ? new ViewFrustum(viewer.Eye, viewer.Forward, yawHalfDeg, pitchHalfDeg)
            : default;

        // Ray budget instrumentation. Off by default; when off, `count` is false and every block
        // below is a predicted-not-taken branch. The counting never feeds back into the result.
        bool count = VisibilityCounters.Enabled;
        int anchorsInFov = 0, raysCast = 0, raysClear = 0, raysOutsideFov = 0;
        int raysSkippedByEarlyExit = 0, raysSkippedByGate = 0, raysSkippedBySmoke = 0, raysShortCircuited = 0;
        long rayTicks = 0;

        bool exposed = false, couldSee = false;
        for (int i = 0; i < n; i++)
        {
            bool inFov = viewer.HasForward && frustum.Contains(anchors[i]);
            if (count && inFov)
            {
                anchorsInFov++;
            }

            // Smoke is only asked about anchors the frustum admits; outside it the answer is moot.
            bool smoked = inFov && SmokeVolumes.SegmentBlocked(viewer.Eye, anchors[i], smokes);

            // An anchor outside the frustum or behind smoke can newly-expose but never newly
            // could-see, so its ray is cast only while exposed is wanted and still unknown.
            if ((!inFov || smoked) && (exposed || !needExposed))
            {
                if (count)
                {
                    raysSkippedByGate++;
                    if (smoked)
                    {
                        raysSkippedBySmoke++;
                    }
                }

                continue;
            }

            long rayStart = count ? Stopwatch.GetTimestamp() : 0;
            bool clear;
            bool shortCircuited = false;
            if (occluderHints.IsEmpty)
            {
                clear = engine.IsVisible(viewer.Eye, anchors[i]);
            }
            else
            {
                clear = engine.IsVisible(viewer.Eye, anchors[i], ref occluderHints[i], out shortCircuited);
            }

            if (count)
            {
                rayTicks += Stopwatch.GetTimestamp() - rayStart;
                raysCast++;
                if (clear)
                {
                    raysClear++;
                }

                if (shortCircuited)
                {
                    raysShortCircuited++;
                }

                if (!inFov)
                {
                    raysOutsideFov++;
                }
            }

            if (clear)
            {
                exposed = true;
                // Vision additionally requires the frustum and a sightline clear of active smoke.
                if (inFov && !smoked)
                {
                    couldSee = true;
                }
            }

            if (exposed && couldSee)
            {
                if (count)
                {
                    // Anchors after this one are never visited. Count the frustum membership of
                    // the unvisited tail too, so the no-anchor-in-frustum verdict is about the
                    // pair, not about how far the loop got.
                    raysSkippedByEarlyExit += n - i - 1;
                    for (int j = i + 1; j < n; j++)
                    {
                        if (viewer.HasForward && frustum.Contains(anchors[j]))
                        {
                            anchorsInFov++;
                        }
                    }
                }

                break;
            }
        }

        if (count)
        {
            VisibilityCounters.RecordPair(
                n, anchorsInFov, raysCast, raysClear, raysOutsideFov,
                raysSkippedByEarlyExit, raysSkippedByGate, raysSkippedBySmoke, raysShortCircuited,
                rayTicks, exposed, couldSee);
        }

        return (exposed, couldSee);
    }

    /// <summary>
    ///     Collects the active smoke spheres into <paramref name="into" /> (cleared first): billowing
    ///     <c>CSmokeGrenadeProjectile</c> clouds (<c>m_nSmokeEffectTickBegin&gt;0</c> ⇒ detonated, not the flying
    ///     projectile) centred on the networked world <c>m_vSmokeDetonationPos</c>, radius
    ///     <see cref="SmokeVolumes.DefaultRadius" />. This is exactly the 2D overlay's proven active-smoke gate
    ///     (<c>UpdateAreaEffects</c>) — the engine removes the entity when the cloud fades, so the active window
    ///     is bounded without a max-age cap (a cap can't be applied here anyway: the entity's tick fields use a
    ///     different origin than <c>DemoFrame.ServerTick</c>). The bound is regression-guarded by
    ///     <c>SmokeWindow_HasBoundedLifetime_AndDoesNotCollapseVision</c> (max concurrent stays low).
    /// </summary>
    public static void CollectActiveSmokes(EntityTracker tracker, List<Vector4> into)
    {
        into.Clear();
        foreach ((int _, EntityState ent) in tracker.CurrentEntities.AllIndexed())
        {
            if (ent.ClassName != SmokeClass)
            {
                continue;
            }

            if (CoerceInt(ent["m_nSmokeEffectTickBegin"], 0) <= 0)
            {
                continue; // still a flying projectile, not yet a billowing cloud
            }

            if (ent.TryGet<Vector3>("m_vSmokeDetonationPos") is { } pos && (pos.X != 0 || pos.Y != 0))
            {
                into.Add(new Vector4(pos.X, pos.Y, pos.Z, SmokeVolumes.DefaultRadius));
            }
        }
    }

    /// <summary>
    ///     Full-demo (or windowed) accumulation over a fresh sequential replay. Minutes of work on a
    ///     long demo, so it is cancellable and the result carries the bake identity it was computed
    ///     against when the caller supplies one (<see cref="Options.Bundle" />).
    /// </summary>
    /// <param name="frames">The demo's frame list — the replay's clock is <c>DemoFrame.ServerTick</c>.</param>
    /// <param name="engine">The loaded collision geometry every ray is cast against.</param>
    /// <param name="resolvePosition">
    ///     Reconstructs a pawn's world-space FEET position (eye height is derived from it here). The
    ///     packaged answer is <c>PositionUtil.CellToWorld</c>.
    /// </param>
    /// <param name="options">Sampling stride, tick rate, FOV, frame window, smoke, bake identity.</param>
    /// <param name="cancellationToken">
    ///     Observed once per replayed frame, so cancel latency is one frame's entity decode. A canceled
    ///     run throws <see cref="OperationCanceledException" /> — no partial report is ever returned,
    ///     matching the evaluation family's posture (<c>AnalysisOptions.CancellationToken</c>).
    /// </param>
    public static Report Analyze(
        IReadOnlyList<DemoFrame> frames, VisibilityEngine engine,
        Func<EntityState, Vector3?> resolvePosition, Options? options = null,
        CancellationToken cancellationToken = default)
    {
        Options opt = options ?? new Options();
        Dictionary<(int Viewer, int Target), double> pairExposed = new();
        Dictionary<(int Viewer, int Target), double> pairCouldSee = new();
        Dictionary<int, double> couldSeeAny = new();
        Dictionary<int, double> exposedToAny = new();

        if (frames.Count == 0)
        {
            return new Report([], couldSeeAny, exposedToAny, 0, 0, opt.Bundle);
        }

        int start = Math.Clamp(opt.StartFrame, 0, frames.Count - 1);
        int end = Math.Min(frames.Count - 1, opt.EndFrame);
        double maxDt = opt.SampleStrideTicks * 2.0 / opt.TickRate; // cap across demo-pause gaps

        EntityTracker tracker = new();
        tracker.ReplayToIndex(start, frames);
        int lastSampledTick = frames[start].ServerTick;

        List<Vantage> samples = new(12);
        List<Vector4> smokes = new(4);
        HashSet<int> exposedThisTick = new();
        int sampledTicks = 0;
        double sampledSeconds = 0;

        // Last-occluder hints per (viewer, target, anchor), carried across sampled ticks. Verdicts
        // do not depend on them (see CouldSee), so nothing here needs them cleared between rounds.
        // A slot outside the table's range gets no hints rather than an exception: this path has
        // never range-checked slots and this step does not start.
        OccluderHintTable hints = new();

        // One delegate for the run rather than one per sampled tick: everything it closes over
        // is loop-invariant, so building it inside the loop only allocated.
        Action<int, EntityState> collect = (slot, pawn) =>
        {
            if (TryVantage(slot, pawn, resolvePosition) is { } v)
            {
                samples.Add(v);
            }
        };

        for (int i = start; i <= end; i++)
        {
            // Frame granularity is the cancellation quantum — one volatile read per frame, and it
            // bounds cancel latency to a single AdvanceOneFrame + (at most) one sampled tick's
            // pairwise raycasting. Same quantum the state-graph evaluator uses.
            cancellationToken.ThrowIfCancellationRequested();

            if (i > start)
            {
                tracker.AdvanceOneFrame(frames[i]);
            }

            int tick = frames[i].ServerTick;
            if (i != start && tick - lastSampledTick < opt.SampleStrideTicks)
            {
                continue;
            }

            double dt = Math.Min((tick - lastSampledTick) / opt.TickRate, maxDt);
            lastSampledTick = tick;
            if (dt <= 0)
            {
                continue;
            }

            samples.Clear();
            PawnLookup.ForEachLivePawn(tracker, collect);

            if (samples.Count < 2)
            {
                continue;
            }

            sampledTicks++;
            sampledSeconds += dt;
            exposedThisTick.Clear();
            if (opt.IncludeSmoke)
            {
                CollectActiveSmokes(tracker, smokes);
            }
            else
            {
                smokes.Clear();
            }

            ReadOnlySpan<Vector4> smokeSpan = CollectionsMarshal.AsSpan(smokes);

            foreach (Vantage viewer in samples)
            {
                bool viewerSawAny = false;
                foreach (Vantage target in samples)
                {
                    if (viewer.Slot == target.Slot || !AreEnemies(viewer, target))
                    {
                        continue;
                    }

                    (bool exposed, bool couldSee) = Evaluate(
                        engine, viewer, target, opt.YawHalfDeg, opt.PitchHalfDeg, smokeSpan,
                        hints.For(viewer.Slot, target.Slot), needExposed: true);

                    // One hash probe per accumulate rather than a lookup and a store. The entry is
                    // added on first touch exactly as the indexer store did, so insertion order,
                    // and with it the report's pair order, is unchanged.
                    if (exposed)
                    {
                        ref double seconds = ref CollectionsMarshal.GetValueRefOrAddDefault(
                            pairExposed, (viewer.Slot, target.Slot), out _);
                        seconds += dt;
                        exposedThisTick.Add(target.Slot);
                    }

                    if (couldSee)
                    {
                        ref double seconds = ref CollectionsMarshal.GetValueRefOrAddDefault(
                            pairCouldSee, (viewer.Slot, target.Slot), out _);
                        seconds += dt;
                        viewerSawAny = true;
                    }
                }

                if (viewerSawAny)
                {
                    ref double seconds = ref CollectionsMarshal.GetValueRefOrAddDefault(couldSeeAny, viewer.Slot, out _);
                    seconds += dt;
                }
            }

            foreach (int t in exposedThisTick)
            {
                ref double seconds = ref CollectionsMarshal.GetValueRefOrAddDefault(exposedToAny, t, out _);
                seconds += dt;
            }
        }

        List<PairStat> pairs = new(pairExposed.Count);
        HashSet<(int, int)> keys = new(pairExposed.Keys);
        keys.UnionWith(pairCouldSee.Keys);
        foreach ((int v, int t) in keys)
        {
            pairs.Add(new PairStat(v, t,
                pairExposed.GetValueOrDefault((v, t)),
                pairCouldSee.GetValueOrDefault((v, t))));
        }

        return new Report(pairs, couldSeeAny, exposedToAny, sampledTicks, sampledSeconds, opt.Bundle);
    }

    internal static int CoerceInt(object? value, int fallback) => value switch
    {
        int i => i,
        uint u => (int)u,
        short s => s,
        ushort us => us,
        byte b => b,
        long l => (int)l,
        ulong ul => (int)ul,
        float f => (int)f,
        double d => (int)d,
        _ => fallback
    };

    internal static float CoerceFloat(object? value, float fallback) => value switch
    {
        float f => f,
        double d => (float)d,
        int i => i,
        uint u => u,
        _ => fallback
    };

    /// <summary>A player's per-tick viewing/exposure state, extracted once and reused for all its pairs.</summary>
    public readonly record struct Vantage(
        int Slot,
        int Team,
        Vector3 Feet,
        Vector3 Eye,
        Vector3 Forward,
        bool HasForward,
        float Duck);

    /// <summary>
    ///     Sampling and scoping knobs for <see cref="Analyze" />. <see cref="Bundle" /> is metadata
    ///     only — it changes nothing about the computation, it just travels onto the
    ///     <see cref="Report" />.
    /// </summary>
    /// <param name="SampleStrideTicks">Ticks between sampled positions (4 ⇒ 16 Hz at 64-tick).</param>
    /// <param name="TickRate">Ticks per second, used to convert sampled gaps into seconds.</param>
    /// <param name="YawHalfDeg">Half the horizontal FOV in degrees.</param>
    /// <param name="PitchHalfDeg">Half the vertical FOV in degrees.</param>
    /// <param name="StartFrame">First frame index of the replay window.</param>
    /// <param name="EndFrame">Last frame index of the replay window (inclusive).</param>
    /// <param name="IncludeSmoke">Active smoke clouds occlude could-see; false gives an A/B baseline.</param>
    /// <param name="Bundle">
    ///     Which bake the <see cref="VisibilityEngine" />'s geometry came from, for the report's audit
    ///     trail. The analyzer does no file I/O — the caller that opened the bundle supplies this (see
    ///     <see cref="MapAssetBundleReader.TryReadIdentity" />). Null when unknown, which is why every
    ///     consumer must treat <see cref="Report.Bundle" /> as optional.
    /// </param>
    public sealed record Options(
        int SampleStrideTicks = 4, // 16 Hz at 64-tick (perf-spike default)
        double TickRate = 64.0,
        float YawHalfDeg = 53f, // 16:9 Hor+ of the 90° base ⇒ ~106° horizontal FOV
        float PitchHalfDeg = 37f, // ~74° vertical
        int StartFrame = 0,
        int EndFrame = int.MaxValue,
        bool IncludeSmoke = true, // active smoke clouds occlude could-see (set false for an A/B baseline)
        MapBundleIdentity? Bundle = null);

    public sealed record PairStat(int ViewerSlot, int TargetSlot, double ExposedSeconds, double CouldSeeSeconds);

    /// <summary>
    ///     One <see cref="Analyze" /> run's accumulated durations, plus the identity of the geometry
    ///     they were computed against.
    /// </summary>
    /// <param name="Pairs">Per directed enemy pair (viewer → target) exposed/could-see seconds.</param>
    /// <param name="CouldSeeAnyEnemySeconds">Viewer → union time they could see at least one enemy.</param>
    /// <param name="ExposedToAnyEnemySeconds">Target → union time exposed to at least one enemy.</param>
    /// <param name="SampledTicks">How many ticks were actually sampled (≥ 2 live players).</param>
    /// <param name="SampledSeconds">Total wall-clock the sampled ticks covered.</param>
    /// <param name="Bundle">
    ///     The bake the geometry came from, when the caller supplied it via
    ///     <see cref="Options.Bundle" /> — otherwise null. Bundles are selected by map NAME alone, so
    ///     this is the only evidence distinguishing a result computed against current geometry from
    ///     one computed against a bake that predates a CS2 map update. Persist it alongside any
    ///     stored result.
    /// </param>
    public sealed record Report(
        IReadOnlyList<PairStat> Pairs,
        IReadOnlyDictionary<int, double> CouldSeeAnyEnemySeconds,
        IReadOnlyDictionary<int, double> ExposedToAnyEnemySeconds,
        int SampledTicks,
        double SampledSeconds,
        MapBundleIdentity? Bundle = null);
}
