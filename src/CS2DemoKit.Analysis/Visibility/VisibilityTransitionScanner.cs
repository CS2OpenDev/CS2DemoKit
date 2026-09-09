#region

using System.Numerics;
using CS2DemoKit.Analysis.Events;

#endregion

namespace CS2DemoKit.Analysis.Visibility;

/// <summary>
///     The rising-edge half of visibility, which the engine could not express before:
///     <see cref="VisibilityAnalyzer.Analyze" /> computes per-tick pairwise visibility and then throws
///     the booleans away, accumulating only seconds, so nothing in its report carries a tick. This
///     holds the previous tick's per-pair booleans instead and synthesizes an
///     <see cref="EnemySpottedEvent" /> on every not-visible to visible transition of a DIRECTED
///     (viewer, target) enemy pair.
///     <para>
///         <b>The geometry is not reimplemented.</b> <see cref="VisibilityAnalyzer.AreEnemies" />,
///         <c>VisibilityAnalyzer.EvaluatePair</c> and
///         <see cref="PlayerVantage.BuildAnchors" /> are the same primitives the accumulating analyzer
///         uses, so the two agree by construction; what is added here is edge detection and an event
///         to hang it on.
///     </para>
///     <para>
///         <b>Vantage is not recomputed either.</b> The scanner consumes an
///         <see cref="AimVantage" /> list built once per tick by <see cref="AimVantageScanner" /> from
///         the existing digest walk. It never touches entity state, never resolves a pawn, and never
///         triggers a second decode. Per pair it does raycasting and nothing else.
///     </para>
///     <para>
///         <b>An absent pair reads as not-visible.</b> The visible set is rebuilt from scratch each
///         sample, so a target who dies, disconnects, or drops out of the vantage set for any other
///         reason falls to false, and their reappearance is a genuine rising edge rather than a
///         suppressed one.
///     </para>
/// </summary>
public sealed class VisibilityTransitionScanner
{
    /// <summary>
    ///     Half-width of a player used to decide whether the crosshair is ON them, in world units.
    ///     Matches the lateral offset <c>PlayerVantage.BuildAnchors</c> puts its shoulder anchors at,
    ///     so "on target" means the same body this scanner already tests visibility against.
    /// </summary>
    public const float OnTargetHalfWidthUnits = 16f;

    private readonly List<EnemySpottedEvent> _spots = new(4);
    private readonly VisibilityEngine _engine;
    private readonly Options _options;
    private HashSet<(int Viewer, int Target)> _current = new();
    private int _lastSampledTick = int.MinValue;
    private HashSet<(int Viewer, int Target)> _visible = new();

    // Per viewer, the tick their crosshair most recently ARRIVED on an enemy, and the pairs it is
    // on THIS sample. Re-armed when the crosshair leaves, so a held angle reports the moment of
    // arrival rather than the start of the hold.
    //
    // The dictionary doubles as the "was on someone last sample" record: the re-arm sweep removes a
    // viewer key exactly when the crosshair has left every enemy, so a key still present when the
    // next sample starts means the hold is unbroken and the arrival tick stands.
    private readonly Dictionary<int, int> _onTargetSince = [];
    private readonly HashSet<(int Viewer, int Target)> _onTarget = [];

    /// <param name="engine">The loaded collision geometry every sightline is cast against.</param>
    /// <param name="options">Sampling stride and frustum half-angles; defaults are the ones the metrics need.</param>
    public VisibilityTransitionScanner(VisibilityEngine engine, Options? options = null)
    {
        ArgumentNullException.ThrowIfNull(engine);
        _engine = engine;
        _options = options ?? new Options();
    }

    /// <summary>
    ///     Is any enemy visible to <paramref name="viewerSlot" /> as of the most recent
    ///     <c>Sample</c>? This is the LEVEL the rising edges are edges of, and it answers a different
    ///     question: a shot fired ten seconds into a held angle is a shot at a visible enemy but no
    ///     transition at all, so a "was an enemy spotted" gate built on the event alone would drop it.
    ///     <para>
    ///         Reads the same could-see set the edge detection maintains, so level and edges cannot
    ///         disagree. False, not unknown, before the first sample or for a viewer with no vantage:
    ///         the set is rebuilt from scratch every sample (see the class doc), so absence from it
    ///         always means not-visible.
    ///     </para>
    /// </summary>
    /// <param name="viewerSlot">The player whose view is being asked about.</param>
    public bool IsAnyEnemyVisibleTo(int viewerSlot)
    {
        foreach ((int viewer, int _) in _visible)
        {
            if (viewer == viewerSlot)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    ///     The tick <paramref name="viewerSlot" />'s crosshair arrived on an enemy, or -1 when it is
    ///     not on one.
    ///     <para>
    ///         This is the anchor an AIMED REACTION is measured from, and it is deliberately not the
    ///         spot: time-to-shoot spans the whole act of turning onto a target, while aimed reaction
    ///         is only what happens once the crosshair is already there. Separating them is the
    ///         difference between measuring aim travel and measuring the player.
    ///     </para>
    ///     <para>
    ///         "On target" is range-corrected: a player subtends a smaller angle further away, so a
    ///         fixed degree tolerance would call a distant miss an acquisition and a close one a
    ///         miss. See <see cref="OnTargetHalfWidthUnits" />.
    ///     </para>
    /// </summary>
    /// <param name="viewerSlot">The player whose crosshair is being asked about.</param>
    public int OnTargetSince(int viewerSlot) => _onTargetSince.GetValueOrDefault(viewerSlot, -1);

    /// <summary>Overload without dynamic smoke occluders (equivalent to no active smokes).</summary>
    /// <param name="tick">The absolute server tick being sampled.</param>
    /// <param name="gameTick">The same instant on the frame clock.</param>
    /// <param name="vantages">This tick's vantage set, one entry per eligible player.</param>
    public IReadOnlyList<EnemySpottedEvent> Sample(
        int tick, int gameTick, IReadOnlyList<AimVantage> vantages) =>
        Sample(tick, gameTick, vantages, ReadOnlySpan<Vector4>.Empty);

    /// <summary>
    ///     Evaluates every directed enemy pair in <paramref name="vantages" /> and returns the pairs
    ///     that became visible on this tick. The returned list is REUSED across calls (cleared at
    ///     entry), so a caller that needs it beyond the next <c>Sample</c> must copy it.
    ///     Returns empty without judging anything when the tick falls inside the sampling stride, so a
    ///     caller may hand it every frame and let it decide which are sample points.
    /// </summary>
    /// <param name="tick">
    ///     The ABSOLUTE server tick being sampled; stamped on every event this call emits and used
    ///     for the stride decision.
    /// </param>
    /// <param name="gameTick">
    ///     The same instant on the FRAME clock (<c>ServerTick - ServerStartTick</c>), stamped as the
    ///     event's <c>GameTick</c>. Passed in rather than derived because the offset belongs to the
    ///     demo, not to this scanner. It matters: every parsed <c>GameEvent</c> carries a frame-clock
    ///     GameTick, so stamping the absolute tick here would put synthesized spots in a different
    ///     clock from the shots they get paired against, and the pairing would measure the offset.
    /// </param>
    /// <param name="vantages">This tick's vantage set, one entry per eligible player.</param>
    /// <param name="smokes">
    ///     Active smoke spheres <c>(centre.xyz, radius)</c>, from
    ///     <see cref="VisibilityAnalyzer.CollectActiveSmokes" />. Smoke occludes vision, so a
    ///     smoked-off enemy is not spotted. Passing none over-reports every through-smoke sightline as
    ///     a spot, which biases hardest in exactly the post-plant and execute situations where
    ///     engagements cluster.
    /// </param>
    public IReadOnlyList<EnemySpottedEvent> Sample(
        int tick, int gameTick, IReadOnlyList<AimVantage> vantages, ReadOnlySpan<Vector4> smokes)
    {
        ArgumentNullException.ThrowIfNull(vantages);
        _spots.Clear();

        // Stride gate. Frames repeat and skip ticks, so the caller cannot know which frames are
        // sample points; this decides. A backward tick (a re-evaluation or a seek) resamples rather
        // than being swallowed by the gap test.
        if (_lastSampledTick != int.MinValue
            && tick >= _lastSampledTick
            && tick - _lastSampledTick < _options.SampleStrideTicks)
        {
            return _spots;
        }

        _lastSampledTick = tick;
        _current.Clear();
        _onTarget.Clear();

        for (int v = 0; v < vantages.Count; v++)
        {
            AimVantage viewerAim = vantages[v];
            VisibilityAnalyzer.Vantage viewer = viewerAim.Vantage;
            for (int t = 0; t < vantages.Count; t++)
            {
                VisibilityAnalyzer.Vantage target = vantages[t].Vantage;
                if (viewer.Slot == target.Slot || !VisibilityAnalyzer.AreEnemies(viewer, target))
                {
                    continue;
                }

                (bool _, bool couldSee) = VisibilityAnalyzer.EvaluatePair(
                    _engine, viewer, target, _options.YawHalfDeg, _options.PitchHalfDeg, smokes);
                if (!couldSee)
                {
                    continue;
                }

                (int Viewer, int Target) pair = (viewer.Slot, target.Slot);
                _current.Add(pair);

                // Range-corrected acquisition test. A player 300 units away subtends about 3 degrees
                // of half-width and one 1500 away about 0.6, so a fixed tolerance would call a
                // distant miss an acquisition and a close one a miss. The range is the one to the
                // CHEST ANCHOR, which is the point the angle was measured to; the feet are further
                // off by the eye-to-chest height difference, which is nothing at 500 units and
                // nearly a degree out of nine inside 100.
                (float chest, float range) = ChestAim(viewer, target);
                float halfWidth = range <= 1f
                    ? 90f
                    : (float)(Math.Atan2(OnTargetHalfWidthUnits, range) * 180.0 / Math.PI);
                if (chest <= halfWidth)
                {
                    _onTarget.Add(pair);

                    // Arrival, not continuation, and per VIEWER rather than per pair: the crosshair
                    // is on an enemy or it is not, and the re-arm below drops a viewer only once it
                    // has left them ALL. Stamping per pair restarts the clock when a second enemy
                    // walks into a held crosshair, on a player who never moved their aim, and every
                    // aimed reaction measured through that instant then reads short.
                    if (!_onTargetSince.ContainsKey(viewer.Slot))
                    {
                        _onTargetSince[viewer.Slot] = tick;
                    }
                }

                if (_visible.Contains(pair))
                {
                    continue; // still visible, not an edge
                }

                // FrameNumber has no meaningful value here (this is synthesized between frames, not
                // decoded from one), so it carries the server tick like the other two positional
                // slots. GameTick is the one that has to be the frame clock: it is what every edge
                // pairing a spot against a shot compares.
                _spots.Add(new EnemySpottedEvent(
                    tick, tick, gameTick, viewer.Slot, target.Slot,
                    chest,
                    viewerAim.EyePitchDeg, viewerAim.EyeYawDeg));
            }
        }

        // A viewer whose crosshair left every enemy re-arms, so the next arrival is a fresh one
        // rather than the stale tick of an acquisition they have since turned away from.
        foreach (int viewer in _onTargetSince.Keys.ToList())
        {
            bool stillOn = false;
            foreach ((int v, int _) in _onTarget)
            {
                if (v == viewer)
                {
                    stillOn = true;
                    break;
                }
            }

            if (!stillOn)
            {
                _onTargetSince.Remove(viewer);
            }
        }

        // Swap rather than copy: the outgoing set becomes next tick's scratch and is cleared above.
        (_visible, _current) = (_current, _visible);
        return _spots;
    }

    /// <summary>
    ///     Angle between the viewer's eye ray and the target's chest anchor, and the range to that
    ///     same anchor. Both come back together because the range is what turns the angle into an
    ///     acquisition verdict, and measuring the two against different points is what makes a
    ///     tolerance that is right at 500 units wrong at 100.
    ///     <para>
    ///         Rebuilds the anchor set rather than threading one out of
    ///         <c>VisibilityAnalyzer.EvaluatePair</c>: taking the anchor from the same builder is what
    ///         keeps the measured point identical to the one the visibility test cleared. It runs for
    ///         every pair the viewer could see, not only on a rising edge.
    ///     </para>
    /// </summary>
    /// <param name="viewer">The player whose crosshair is being measured.</param>
    /// <param name="target">The enemy being measured against.</param>
    private static (float AngleDeg, float RangeUnits) ChestAim(
        in VisibilityAnalyzer.Vantage viewer, in VisibilityAnalyzer.Vantage target)
    {
        Span<Vector3> anchors = stackalloc Vector3[PlayerVantage.MaxAnchors];
        _ = PlayerVantage.BuildAnchors(target.Feet, target.Duck, viewer.Eye, anchors);

        // Anchor 0 is chest (48 units, duck-scaled). Chest is the most stable body point across a
        // crouch transition, so a preaim number taken against it is comparable between engagements
        // in a way one taken against the head is not.
        return (
            PlayerVantage.AngleToPointDegrees(viewer.Eye, viewer.Forward, anchors[0]),
            Vector3.Distance(viewer.Eye, anchors[0]));
    }

    /// <summary>Sampling and frustum knobs for <c>Sample</c>.</summary>
    /// <param name="SampleStrideTicks">
    ///     Ticks between sampled positions. <b>1, not the analyzer's 4.</b> Stride 4 is 62 ms of
    ///     resolution at 64-tick, and the metrics that consume these edges (time-to-shoot,
    ///     time-to-damage, aimed reaction) have a total dynamic range of roughly 150 ms across the
    ///     whole skill ladder, so a stride-4 edge quantises away most of the signal it is meant to
    ///     carry. The cost is about four times the raycasting.
    /// </param>
    /// <param name="YawHalfDeg">Half the horizontal FOV in degrees (16:9 Hor+ of the 90 degree base).</param>
    /// <param name="PitchHalfDeg">Half the vertical FOV in degrees.</param>
    public sealed record Options(
        int SampleStrideTicks = 1,
        float YawHalfDeg = 53f,
        float PitchHalfDeg = 37f);
}
