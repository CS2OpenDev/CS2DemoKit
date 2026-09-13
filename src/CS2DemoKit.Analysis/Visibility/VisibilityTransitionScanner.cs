#region

using System.Diagnostics;
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
///         <see cref="VisibilityAnalyzer.CouldSee(VisibilityEngine, in VisibilityAnalyzer.Vantage, in VisibilityAnalyzer.Vantage, float, float, ReadOnlySpan{Vector4}, Span{int})" /> (the same pair loop
///         <c>VisibilityAnalyzer.EvaluatePair</c> runs, told that <c>exposed</c> is not wanted) and
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
///     <para>
///         <b>What survives a sample is the caller's to end.</b> Everything carried BETWEEN samples
///         — the visible set, the on-target record and its arrival stamps, the stride anchor — is
///         state about a stretch of play the scanner assumes is continuous, and it has no way to
///         tell that a round ended or that the vantage feed stopped. <see cref="Reset" /> is how a
///         caller says so; see its remarks for the two places that have to.
///     </para>
///     <para>
///         <b>Pair state is a bit per (viewer, target).</b> Slots are player slots, which a
///         well-formed demo keeps in 0..63 (controller entity index minus one, and controllers
///         occupy indices 1..64), so the visible set is one <c>ulong</c> row per viewer and the
///         on-target record one <c>ulong</c> of viewers plus a stamp per slot. A slot outside that
///         range is a malformed demo and <c>Sample</c> throws rather than reporting on it: no
///         partial report on a malformed demo, the same posture as every other analyzer here.
///     </para>
///     <para>
///         <b>Rays remember their last occluder.</b> Per (viewer, target, anchor) the scanner keeps
///         the triangle that blocked the sightline last sample and hands it to the pair loop as a
///         hint (<see cref="OccluderHintTable" />). At stride 1 consecutive samples of a sightline
///         are nearly always stopped by the same surface, so most rays are answered by one
///         triangle test instead of a BVH traversal. The hints change nothing about a verdict.
///         They are per-scanner mutable state, which is the one thing a parallel evaluation would
///         have to give each worker its own copy of.
///     </para>
/// </summary>
public sealed class VisibilityTransitionScanner
{
    /// <summary>
    ///     Half-width of a player used to decide whether the crosshair is ON them, in world units.
    ///     Matches the lateral offset <c>PlayerVantage.BuildAnchors</c> puts its shoulder anchors at,
    ///     so "on target" means the same body this scanner already tests visibility against.
    ///     <para>
    ///         <b>Known limit: the acceptance region is a disc, and a player is not.</b> The test is
    ///         <c>angleToChest &lt;= atan(16 / rangeToChest)</c>, which accepts a cone 16 units wide
    ///         centred on the CHEST anchor — isotropic, while a player is 32 wide and 72 tall with
    ///         the head at 64 and the chest at 48. It is therefore tight vertically by construction,
    ///         and tightest in the most ordinary geometry in the game. Two standing players on the
    ///         same floor, crosshair level: the eye (feet + 64) sits 16 above the chest (feet + 48),
    ///         so at horizontal distance D the measured angle is <c>atan(16 / D)</c> while the
    ///         tolerance is <c>atan(16 / sqrt(D² + 256))</c> — strictly the smaller of the two at
    ///         every D, because the range to the chest is the hypotenuse of the same triangle the
    ///         angle was taken from. A level crosshair on a same-elevation standing enemy therefore
    ///         fails the test at every range, by a hair: 9.0903 degrees against an 8.9780 tolerance
    ///         at D = 100, and 1.8328 against 1.8319 at D = 500. Against a crouched target it is not
    ///         a hair — <c>HeightScale</c> pulls the chest to 34.5 while the tolerance stays 16 wide,
    ///         so at D = 100 the level crosshair measures 16.44 against an 8.72 tolerance.
    ///     </para>
    ///     <para>
    ///         The verdict in that geometry is thus decided by sub-tenth-degree differences, exactly
    ///         where players hold their crosshair, and a player aiming a little low (chest rather
    ///         than head) is admitted where one holding head level is not. Correcting it means an
    ///         ANISOTROPIC acceptance — the chest point's perpendicular offset from the eye ray
    ///         against a half-width laterally and a half-HEIGHT vertically, or the nearest point on a
    ///         capsule rather than one anchor — which moves <c>enemy_spotted</c> and
    ///         <c>ticks_since_on_target</c> and so moves both the committed visibility golden and the
    ///         frozen reference scanner the parity suite measures against.
    ///         <c>OnTarget_IsRefusedForALevelCrosshairOnASameFloorEnemy</c> pins the behaviour as it
    ///         stands so that change is a deliberate one.
    ///     </para>
    /// </summary>
    public const float OnTargetHalfWidthUnits = 16f;

    /// <summary>Player slots the scanner can hold state for: 0 to <c>MaxSlots - 1</c>.</summary>
    public const int MaxSlots = 64;

    private readonly List<EnemySpottedEvent> _spots = new(4);
    private readonly VisibilityEngine _engine;
    private readonly OccluderHintTable _hints = new();
    private readonly Options _options;
    private ulong[] _current = new ulong[MaxSlots];
    private int _lastSampledTick = int.MinValue;
    private ulong[] _visible = new ulong[MaxSlots];

    // Per viewer, the tick their crosshair most recently ARRIVED on an enemy. Re-armed when the
    // crosshair leaves, so a held angle reports the moment of arrival rather than the start of the
    // hold.
    //
    // _onTargetViewers is the "was on someone last sample" record: the re-arm sweep clears a
    // viewer's bit exactly when the crosshair has left every enemy, so a bit still set when the next
    // sample starts means the hold is unbroken and the arrival tick in _onTargetSince stands.
    private readonly int[] _onTargetSince = new int[MaxSlots];
    private ulong _onTargetViewers;

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
    public bool IsAnyEnemyVisibleTo(int viewerSlot) =>
        (uint)viewerSlot < MaxSlots && _visible[viewerSlot] != 0;

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
    public int OnTargetSince(int viewerSlot) =>
        (uint)viewerSlot < MaxSlots && (_onTargetViewers & (1UL << viewerSlot)) != 0
            ? _onTargetSince[viewerSlot]
            : -1;

    /// <summary>
    ///     Drops everything the scanner carries between samples: the visible set, the on-target
    ///     record and its arrival stamps, and the stride anchor (so the next <c>Sample</c> always
    ///     judges). Reported values fall back to their no-data answers — nothing visible to anyone,
    ///     no acquisition — and the next contact of every pair is a fresh rising edge.
    ///     <para>
    ///         <b>Call it at a round boundary.</b> Every other per-round anchor these metrics read
    ///         lives on <c>PlayerContextIndex.PlayerContext</c> and is cleared by
    ///         <c>ResetRoundState</c>; the visible set and the acquisition stamp are the two that do
    ///         not, so without this they are the only state in the feature that spans rounds. A pair
    ///         left visible from the previous round's end suppresses its own first contact of the new
    ///         one, which is the event this whole path exists to produce, and it does so silently:
    ///         <c>SpottedEnrichmentEdge</c> then hands <c>spot_index</c> 1 (and
    ///         <c>is_first_contact</c>) to whichever contact came second.
    ///     </para>
    ///     <para>
    ///         <b>Call it when the vantage feed stops.</b> The re-arm sweep that expires an
    ///         acquisition runs inside <c>Sample</c>, so a caller that stops sampling freezes the
    ///         stamps rather than clearing them, and a frozen stamp does not read as missing data:
    ///         it reads as an acquisition that is still live, and every aimed reaction measured
    ///         against it comes back as however long the feed has been down. That is not a large
    ///         number a gate would reject — it is a plausible-looking one, under the sentinel, in a
    ///         column of milliseconds.
    ///     </para>
    /// </summary>
    public void Reset()
    {
        Array.Clear(_visible);
        Array.Clear(_current);
        Array.Clear(_onTargetSince);
        _onTargetViewers = 0;
        _lastSampledTick = int.MinValue;
        _spots.Clear();
    }

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

        // Every slot has to index the rows below; a slot outside them is a malformed demo, and the
        // check is per vantage per sample, not per pair.
        for (int i = 0; i < vantages.Count; i++)
        {
            int slot = vantages[i].Vantage.Slot;
            if ((uint)slot >= MaxSlots)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(vantages), slot, $"player slot must be in 0..{MaxSlots - 1}");
            }
        }

        _lastSampledTick = tick;
        Array.Clear(_current);
        ulong onTargetThisSample = 0;

        // Ray budget instrumentation, off by default; brackets the whole pairwise pass so the
        // per-ray figure in VisibilityCounters has a per-tick envelope to be a share of.
        bool count = VisibilityCounters.Enabled;
        long sampleStart = count ? Stopwatch.GetTimestamp() : 0;

        for (int v = 0; v < vantages.Count; v++)
        {
            AimVantage viewerAim = vantages[v];
            VisibilityAnalyzer.Vantage viewer = viewerAim.Vantage;
            ulong viewerBit = 1UL << viewer.Slot;
            for (int t = 0; t < vantages.Count; t++)
            {
                VisibilityAnalyzer.Vantage target = vantages[t].Vantage;
                if (viewer.Slot == target.Slot || !VisibilityAnalyzer.AreEnemies(viewer, target))
                {
                    continue;
                }

                // Could-see only: this scanner never reads exposed, and saying so lets the pair
                // loop skip every ray that could only have decided it. The hint slots are this
                // pair's own; the slot check above guarantees the table has a row for it.
                if (!VisibilityAnalyzer.CouldSee(
                        _engine, viewer, target, _options.YawHalfDeg, _options.PitchHalfDeg, smokes,
                        _hints.For(viewer.Slot, target.Slot)))
                {
                    continue;
                }

                ulong targetBit = 1UL << target.Slot;
                _current[viewer.Slot] |= targetBit;

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
                    onTargetThisSample |= viewerBit;

                    // Arrival, not continuation, and per VIEWER rather than per pair: the crosshair
                    // is on an enemy or it is not, and the re-arm below drops a viewer only once it
                    // has left them ALL. Stamping per pair restarts the clock when a second enemy
                    // walks into a held crosshair, on a player who never moved their aim, and every
                    // aimed reaction measured through that instant then reads short.
                    if ((_onTargetViewers & viewerBit) == 0)
                    {
                        _onTargetViewers |= viewerBit;
                        _onTargetSince[viewer.Slot] = tick;
                    }
                }

                if ((_visible[viewer.Slot] & targetBit) != 0)
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
        // rather than the stale tick of an acquisition they have since turned away from. Stamps
        // survive only for viewers on someone THIS sample.
        _onTargetViewers &= onTargetThisSample;

        // Swap rather than copy: the outgoing rows become next tick's scratch and are cleared above.
        (_visible, _current) = (_current, _visible);
        if (count)
        {
            VisibilityCounters.RecordSample(Stopwatch.GetTimestamp() - sampleStart);
        }

        return _spots;
    }

    /// <summary>
    ///     Angle between the viewer's eye ray and the target's chest anchor, and the range to that
    ///     same anchor. Both come back together because the range is what turns the angle into an
    ///     acquisition verdict, and measuring the two against different points is what makes a
    ///     tolerance that is right at 500 units wrong at 100.
    ///     <para>
    ///         The chest point comes from <see cref="PlayerVantage.ChestAnchor" />, the same
    ///         expression <see cref="PlayerVantage.BuildAnchors" /> writes as anchor 0, so the
    ///         measured point is identical to the one the visibility test cleared without rebuilding
    ///         the whole set to read one entry. It runs for every pair the viewer could see, not only
    ///         on a rising edge.
    ///     </para>
    /// </summary>
    /// <param name="viewer">The player whose crosshair is being measured.</param>
    /// <param name="target">The enemy being measured against.</param>
    private static (float AngleDeg, float RangeUnits) ChestAim(
        in VisibilityAnalyzer.Vantage viewer, in VisibilityAnalyzer.Vantage target)
    {
        // Chest (48 units, duck-scaled) is the most stable body point across a crouch transition,
        // so a preaim number taken against it is comparable between engagements in a way one taken
        // against the head is not.
        Vector3 chest = PlayerVantage.ChestAnchor(target.Feet, PlayerVantage.HeightScale(target.Duck));
        return (
            PlayerVantage.AngleToPointDegrees(viewer.Eye, viewer.Forward, chest),
            Vector3.Distance(viewer.Eye, chest));
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
