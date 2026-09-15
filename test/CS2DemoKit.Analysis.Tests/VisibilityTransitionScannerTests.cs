#region

using System.Numerics;
using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Analysis.Building;
using CS2DemoKit.Analysis.Catalog;
using CS2DemoKit.Analysis.Edges;
using CS2DemoKit.Analysis.Events;
using CS2DemoKit.Analysis.Nodes;
using CS2DemoKit.Analysis.Plugins;
using CS2DemoKit.Analysis.RulesetsV2.Model;
using CS2DemoKit.Analysis.RulesetsV2.Resolve;
using CS2DemoKit.Analysis.Visibility;
using CS2DemoKit.Analysis.Yaml;
using CS2DemoKit.Parser;
using CS2DemoKit.Parser.GameEvents;

using CS2OpenSchema.Protos;

#endregion

namespace CS2DemoKit.Analysis.Tests;

/// <summary>
///     Unit pins for the visibility rising edge: <see cref="AimVantageScanner" />'s reconstruction of
///     a vantage from digest columns (and the speed it derives from them),
///     <see cref="VisibilityTransitionScanner" />'s edge detection, and
///     <see cref="SpottedEnrichmentEdge" />'s contact history. Pure in-memory, no demo file and no
///     baked map: an empty <see cref="VisibilityEngine" /> has nothing to occlude with, so every
///     sightline is clear and the assertions isolate the edge logic from the raycaster.
/// </summary>
[Category("Unit")]
public class VisibilityTransitionScannerTests
{
    /// <summary>Digest column order used by every fixture here. Order is arbitrary on purpose: the scanner resolves by name.</summary>
    private static readonly string[] _columns =
    [
        "entity.pawn.health",
        AimVantageScanner.DuckAmountProvider,
        AimVantageScanner.EyePitchProvider,
        AimVantageScanner.EyeYawProvider,
        AimVantageScanner.PosXProvider,
        AimVantageScanner.PosYProvider,
        AimVantageScanner.PosZProvider
    ];

    /// <summary>
    ///     The six aim columns as PROVIDERS, in the digest order the scanner-through-the-scanner
    ///     fixtures below hand to both halves. Separate from <see cref="_columns" /> because those
    ///     feed <see cref="AimVantageScanner" /> directly by name, while these have to build a real
    ///     <see cref="DigestColumnLayout" /> for <see cref="EntityChangeScanner" /> to consume.
    /// </summary>
    private static readonly IPerPlayerEntityValueProvider[] _aimProviders =
    [
        new PawnPositionProvider(PawnPositionAxis.X),
        new PawnPositionProvider(PawnPositionAxis.Y),
        new PawnPositionProvider(PawnPositionAxis.Z),
        new PawnEyeAngleProvider(PawnAngleAxis.Pitch),
        new PawnEyeAngleProvider(PawnAngleAxis.Yaw),
        new GenericPerPlayerFieldProvider(BuiltinProviderSpecs.PawnDuckAmount)
    ];

    private static readonly DigestColumnLayout _aimLayout = DigestColumnLayout.For(_aimProviders);

    private static AimVantageScanner Scanner(params (int Slot, int Team)[] teams)
    {
        Dictionary<int, int> byslot = teams.ToDictionary(t => t.Slot, t => t.Team);
        return new AimVantageScanner(_columns, slot => byslot.GetValueOrDefault(slot, -1));
    }

    /// <summary>One full digest row (every column populated), as the first frame of a delta stream emits.</summary>
    private static object?[] Row(float x, float y, float z, float pitch, float yaw, float duck = 0f) =>
        [null, duck, pitch, yaw, x, y, z];

    /// <summary>A delta row that carries only the X and Y columns, as a walking pawn's later frames do.</summary>
    private static object?[] MoveRow(float x, float y) => [null, null, null, null, x, y, null];

    /// <summary>One full per-pawn row in <see cref="_aimProviders" /> order.</summary>
    private static object?[] AimRow(float x, float y, float z, float pitch, float yaw, float duck = 0f) =>
        [x, y, z, pitch, yaw, duck];

    /// <summary>One hand-built digest over the six aim columns, optionally decode-compromised.</summary>
    private static EntityFrameDigest AimDigest(bool compromised, params (int Slot, object?[] Row)[] rows) =>
        new()
        {
            DecodeCompromised = compromised,
            PerPawn = PerPawnColumns.FromBoxedRows(_aimLayout, rows)
        };

    // ── AimVantageScanner ────────────────────────────────────────────────────

    [Test]
    public async Task Vantage_IsBuiltFromColumns_WithEyeHeightAndForwardRay()
    {
        AimVantageScanner scanner = Scanner((0, 2));
        scanner.Observe(0, Row(100f, 200f, 64f, 0f, 0f));

        IReadOnlyList<AimVantage> sampled = scanner.Sample(1000);

        await Assert.That(sampled.Count).IsEqualTo(1);
        VisibilityAnalyzer.Vantage v = sampled[0].Vantage;
        await Assert.That(v.Slot).IsEqualTo(0);
        await Assert.That(v.Team).IsEqualTo(2);
        await Assert.That(v.HasForward).IsTrue();
        await Assert.That(v.Feet.Z).IsEqualTo(64f);
        // Standing eye height is 64 above the feet, and yaw 0 / pitch 0 looks down world +X.
        await Assert.That(v.Eye.Z).IsEqualTo(128f);
        await Assert.That(v.Forward.X).IsGreaterThan(0.99f);
    }

    [Test]
    public async Task Vantage_IsWithheld_UntilPositionAndAngleAreBothKnown()
    {
        AimVantageScanner scanner = Scanner((0, 2));

        // Position only: no view direction, so nothing that consumes a forward ray can use it.
        scanner.Observe(0, [null, null, null, null, 100f, 200f, 64f]);
        await Assert.That(scanner.Sample(1000).Count).IsEqualTo(0);

        scanner.Observe(0, [null, null, 0f, 90f, null, null, null]);
        await Assert.That(scanner.Sample(1001).Count).IsEqualTo(1);
    }

    [Test]
    public async Task Vantage_IsWithheld_ForASlotTheTeamCallbackRejects()
    {
        // The delta columns cannot express "this pawn is a corpse": the row simply stops arriving
        // and the last position stands. Liveness is the callback's job, and this is the pin.
        AimVantageScanner scanner = Scanner((0, -1));
        scanner.Observe(0, Row(100f, 200f, 64f, 0f, 0f));

        await Assert.That(scanner.Sample(1000).Count).IsEqualTo(0);
    }

    [Test]
    public async Task Speed_IsDifferencedFromThePreviousSampledTick()
    {
        AimVantageScanner scanner = Scanner((0, 2));
        scanner.Observe(0, Row(0f, 0f, 0f, 0f, 0f));
        await Assert.That(scanner.Sample(1000)[0].Speed2D).IsEqualTo(0f)
            .Because("the first sample has no previous position to difference against");

        // 4 units in 1 tick at 64 tick/s is 256 u/s: a plausible run.
        scanner.Observe(0, MoveRow(4f, 0f));
        await Assert.That(scanner.Sample(1001)[0].Speed2D).IsEqualTo(256f).Within(0.01f);

        // Vertical movement is not horizontal speed.
        scanner.Observe(0, [null, null, null, null, 4f, 0f, 100f]);
        await Assert.That(scanner.Sample(1002)[0].Speed2D).IsEqualTo(0f);
    }

    [Test]
    public async Task Speed_IsZeroAcrossATeleport()
    {
        // A round restart relocates the player between two consecutive sampled ticks. Without the
        // gate the derivative is a five-figure spike that satisfies every "was moving" test.
        AimVantageScanner scanner = Scanner((0, 2));
        scanner.Observe(0, Row(0f, 0f, 0f, 0f, 0f));
        scanner.Sample(1000);

        scanner.Observe(0, MoveRow(3000f, 0f));

        await Assert.That(scanner.Sample(1001)[0].Speed2D).IsEqualTo(0f);
    }

    [Test]
    public async Task Speed_IsZeroAcrossAGapWiderThanTheScannerTolerates()
    {
        AimVantageScanner scanner = Scanner((0, 2));
        scanner.Observe(0, Row(0f, 0f, 0f, 0f, 0f));
        scanner.Sample(1000);

        // Same 4-unit step, but 40 ticks later: the player was absent from the sample set in
        // between, and a straight line across that hole is not a speed.
        scanner.Observe(0, MoveRow(4f, 0f));

        await Assert.That(scanner.Sample(1040)[0].Speed2D).IsEqualTo(0f);
    }

    [Test]
    public void MissingColumn_ThrowsRatherThanReadingNulls()
    {
        // A silent miss here would be a column of nulls that reads as "nobody had a position", so
        // the constructor is the loud arm.
        Assert.Throws<InvalidOperationException>(
            () => _ = new AimVantageScanner([AimVantageScanner.PosXProvider], _ => 2));
    }

    // ── VisibilityTransitionScanner ──────────────────────────────────────────

    [Test]
    public async Task RisingEdge_EmitsOncePerPair_AndNotWhileVisibilityHolds()
    {
        AimVantageScanner vantage = Scanner((0, 2), (1, 3));
        VisibilityTransitionScanner transitions = new(VisibilityEngine.FromTriangles([], 0));

        // Slot 0 at the origin looking down +X; slot 1 standing 500 units along +X, inside the
        // frustum. Slot 1 also faces +X, so it is looking AWAY and only one direction of the pair spots.
        vantage.Observe(0, Row(0f, 0f, 0f, 0f, 0f));
        vantage.Observe(1, Row(500f, 0f, 0f, 0f, 0f));

        IReadOnlyList<EnemySpottedEvent> first = transitions.Sample(1000, 1000, vantage.Sample(1000));
        await Assert.That(first.Count).IsEqualTo(1);
        await Assert.That(first[0].ViewerSlot).IsEqualTo(0);
        await Assert.That(first[0].TargetSlot).IsEqualTo(1);
        await Assert.That(first[0].ServerTick).IsEqualTo(1000);

        // Nothing moved: still visible is not an edge.
        await Assert.That(transitions.Sample(1001, 1001, vantage.Sample(1001)).Count).IsEqualTo(0);
    }

    /// <summary>
    ///     The two tick arguments land in their own slots on the emitted event, rather than one
    ///     overwriting the other.
    ///     <para>
    ///         In production the caller passes the same value twice, because the scanner is driven
    ///         off <c>DemoFrame.ServerTick</c>, which is already the frame clock a parsed event
    ///         reaches by subtracting <c>ServerStartTick</c>. This pins the plumbing anyway: the two
    ///         are separate parameters, and a change that collapsed them (or that helpfully
    ///         subtracted the start tick a second time) would push early spots negative and silently
    ///         empty every spot-to-shot population behind a <c>&gt;= 0</c> guard, with no error
    ///         anywhere to say so.
    ///     </para>
    /// </summary>
    [Test]
    public async Task Spot_CarriesTheFrameClockSeparatelyFromTheAbsoluteTick()
    {
        AimVantageScanner vantage = Scanner((0, 2), (1, 3));
        VisibilityTransitionScanner transitions = new(VisibilityEngine.FromTriangles([], 0));

        vantage.Observe(0, Row(0f, 0f, 0f, 0f, 0f));
        vantage.Observe(1, Row(500f, 0f, 0f, 0f, 0f));

        // A demo whose ServerStartTick is 600: the same instant is tick 1000 absolute and 400 on
        // the frame clock. The two must not collapse into one another.
        IReadOnlyList<EnemySpottedEvent> spots = transitions.Sample(1000, 400, vantage.Sample(1000));

        await Assert.That(spots.Count).IsEqualTo(1);
        await Assert.That(spots[0].ServerTick).IsEqualTo(1000);
        await Assert.That(spots[0].GameTick).IsEqualTo(400);
    }

    [Test]
    public async Task RisingEdge_ReArmsAfterTheTargetLeavesTheSampleSet()
    {
        // A target who dies or disconnects stops appearing, so the pair falls to false and their
        // reappearance is a genuine spot rather than a suppressed one.
        Dictionary<int, int> teams = new() { [0] = 2, [1] = 3 };
        AimVantageScanner vantage = new(_columns, slot => teams.GetValueOrDefault(slot, -1));
        VisibilityTransitionScanner transitions = new(VisibilityEngine.FromTriangles([], 0));

        vantage.Observe(0, Row(0f, 0f, 0f, 0f, 0f));
        vantage.Observe(1, Row(500f, 0f, 0f, 0f, 0f));
        await Assert.That(transitions.Sample(1000, 1000, vantage.Sample(1000)).Count).IsEqualTo(1);

        teams[1] = -1; // slot 1 dies
        await Assert.That(transitions.Sample(1001, 1001, vantage.Sample(1001)).Count).IsEqualTo(0);

        teams[1] = 3; // and respawns
        await Assert.That(transitions.Sample(1002, 1002, vantage.Sample(1002)).Count).IsEqualTo(1);
    }

    [Test]
    public async Task NoEdge_WhenTheTargetIsBehindTheViewer()
    {
        // Line of sight without the viewer looking at it is exposure, not a spot: could-see is what
        // the edge is taken on, and the frustum is what separates the two.
        AimVantageScanner vantage = Scanner((0, 2), (1, 3));
        VisibilityTransitionScanner transitions = new(VisibilityEngine.FromTriangles([], 0));

        vantage.Observe(0, Row(0f, 0f, 0f, 0f, 180f));
        vantage.Observe(1, Row(500f, 0f, 0f, 0f, 0f));

        await Assert.That(transitions.Sample(1000, 1000, vantage.Sample(1000)).Count).IsEqualTo(0);
    }

    [Test]
    public async Task NoEdge_BetweenTeammates()
    {
        AimVantageScanner vantage = Scanner((0, 2), (1, 2));
        VisibilityTransitionScanner transitions = new(VisibilityEngine.FromTriangles([], 0));

        vantage.Observe(0, Row(0f, 0f, 0f, 0f, 0f));
        vantage.Observe(1, Row(500f, 0f, 0f, 0f, 0f));

        await Assert.That(transitions.Sample(1000, 1000, vantage.Sample(1000)).Count).IsEqualTo(0);
    }

    [Test]
    public async Task SmokeBetweenThePair_SuppressesTheSpot()
    {
        AimVantageScanner vantage = Scanner((0, 2), (1, 3));
        VisibilityTransitionScanner transitions = new(VisibilityEngine.FromTriangles([], 0));

        vantage.Observe(0, Row(0f, 0f, 0f, 0f, 0f));
        vantage.Observe(1, Row(500f, 0f, 0f, 0f, 0f));

        // A cloud straddling the sightline at its midpoint, at chest height.
        Vector4[] smokes = [new Vector4(250f, 0f, 48f, SmokeVolumes.DefaultRadius)];

        await Assert.That(transitions.Sample(1000, 1000, vantage.Sample(1000), smokes).Count).IsEqualTo(0);
    }

    [Test]
    public async Task AngleToChest_IsZeroOnTargetAndGrowsAsTheCrosshairDrifts()
    {
        AimVantageScanner vantage = Scanner((0, 2), (1, 3));
        VisibilityTransitionScanner onTarget = new(VisibilityEngine.FromTriangles([], 0));

        // Viewer's eye sits 64 above its feet; putting the viewer's feet 16 units BELOW the target's
        // puts the eye exactly level with the target's 48-unit chest anchor, so a level crosshair
        // pointing straight at them is a zero-degree error.
        vantage.Observe(0, Row(0f, 0f, -16f, 0f, 0f));
        vantage.Observe(1, Row(500f, 0f, 0f, 0f, 0f));
        IReadOnlyList<EnemySpottedEvent> aimed = onTarget.Sample(1000, 1000, vantage.Sample(1000));
        await Assert.That(aimed.Count).IsEqualTo(1);
        await Assert.That(aimed[0].AngleToChestDeg).IsEqualTo(0f).Within(0.01f);

        // Same geometry, crosshair rotated 20 degrees off. Still inside the ~106 degree frustum, so
        // it is still a spot, just a badly preaimed one.
        AimVantageScanner offAim = Scanner((0, 2), (1, 3));
        VisibilityTransitionScanner drifted = new(VisibilityEngine.FromTriangles([], 0));
        offAim.Observe(0, Row(0f, 0f, -16f, 0f, 20f));
        offAim.Observe(1, Row(500f, 0f, 0f, 0f, 0f));
        IReadOnlyList<EnemySpottedEvent> wide = drifted.Sample(1000, 1000, offAim.Sample(1000));
        await Assert.That(wide.Count).IsEqualTo(1);
        await Assert.That(wide[0].AngleToChestDeg).IsEqualTo(20f).Within(0.01f);
    }

    [Test]
    public async Task StrideGate_SkipsTicksInsideTheStride()
    {
        AimVantageScanner vantage = Scanner((0, 2), (1, 3));
        VisibilityTransitionScanner transitions = new(
            VisibilityEngine.FromTriangles([], 0),
            new VisibilityTransitionScanner.Options(SampleStrideTicks: 4));

        vantage.Observe(0, Row(0f, 0f, 0f, 0f, 0f));
        vantage.Observe(1, Row(500f, 0f, 0f, 0f, 0f));

        // First call always samples; the next three ticks fall inside the stride and judge nothing.
        await Assert.That(transitions.Sample(1000, 1000, vantage.Sample(1000)).Count).IsEqualTo(1);
        await Assert.That(transitions.Sample(1001, 1001, vantage.Sample(1001)).Count).IsEqualTo(0);
        await Assert.That(transitions.Sample(1002, 1002, vantage.Sample(1002)).Count).IsEqualTo(0);
    }

    // ── On-target acquisition ────────────────────────────────────────────────

    /// <summary>
    ///     The acquisition tick is the moment the crosshair ARRIVED, and it holds for as long as the
    ///     crosshair stays there. A stamp that advanced on every sample would make every aimed
    ///     reaction measured from it read as one tick.
    /// </summary>
    [Test]
    public async Task OnTarget_StampsTheArrivalTick_AndHoldsItWhileTheCrosshairStays()
    {
        AimVantageScanner vantage = Scanner((0, 2), (1, 3));
        VisibilityTransitionScanner transitions = new(VisibilityEngine.FromTriangles([], 0));

        // Putting the viewer's feet 16 units BELOW the target's puts its eye level with the target's
        // 48-unit chest anchor, so a level crosshair down +X is a zero-degree error.
        vantage.Observe(0, Row(0f, 0f, -16f, 0f, 0f));
        vantage.Observe(1, Row(500f, 0f, 0f, 0f, 0f));

        transitions.Sample(1000, 1000, vantage.Sample(1000));
        await Assert.That(transitions.OnTargetSince(0)).IsEqualTo(1000);

        transitions.Sample(1001, 1001, vantage.Sample(1001));
        await Assert.That(transitions.OnTargetSince(0)).IsEqualTo(1000)
            .Because("holding the crosshair on an enemy is one acquisition, not one per tick");
    }

    /// <summary>
    ///     Leaving every enemy re-arms the viewer, so the next arrival anchors a fresh interval
    ///     rather than the stale one they turned away from.
    /// </summary>
    [Test]
    public async Task OnTarget_ReArms_AfterTheCrosshairLeavesEveryEnemy()
    {
        AimVantageScanner vantage = Scanner((0, 2), (1, 3));
        VisibilityTransitionScanner transitions = new(VisibilityEngine.FromTriangles([], 0));

        vantage.Observe(0, Row(0f, 0f, -16f, 0f, 0f));
        vantage.Observe(1, Row(500f, 0f, 0f, 0f, 0f));
        transitions.Sample(1000, 1000, vantage.Sample(1000));
        await Assert.That(transitions.OnTargetSince(0)).IsEqualTo(1000);

        // 30 degrees off: still inside the ~106 degree frustum, so still a spot, but nowhere near
        // the body.
        vantage.Observe(0, [null, null, null, 30f, null, null, null]);
        transitions.Sample(1001, 1001, vantage.Sample(1001));
        await Assert.That(transitions.OnTargetSince(0)).IsEqualTo(-1);

        vantage.Observe(0, [null, null, null, 0f, null, null, null]);
        transitions.Sample(1002, 1002, vantage.Sample(1002));
        await Assert.That(transitions.OnTargetSince(0)).IsEqualTo(1002);
    }

    /// <summary>
    ///     A viewer already on one enemy does not re-acquire because a SECOND enemy walks into the
    ///     same crosshair.
    ///     <para>
    ///         The stamp is per VIEWER (the crosshair is on an enemy, or it is not) and the
    ///         re-arm drops it only once the crosshair has left them all. Keying the arrival on the
    ///         PAIR instead restarts the clock on a player who never moved their aim, and every
    ///         aimed reaction measured through that instant reads short by however long they had
    ///         been holding.
    ///     </para>
    /// </summary>
    [Test]
    public async Task OnTarget_DoesNotRestamp_WhenASecondEnemyEntersTheSameCrosshair()
    {
        AimVantageScanner vantage = Scanner((0, 2), (1, 3), (2, 3));
        VisibilityTransitionScanner transitions = new(VisibilityEngine.FromTriangles([], 0));

        vantage.Observe(0, Row(0f, 0f, -16f, 0f, 0f));
        vantage.Observe(1, Row(500f, 0f, 0f, 0f, 0f));
        transitions.Sample(1000, 1000, vantage.Sample(1000));
        await Assert.That(transitions.OnTargetSince(0)).IsEqualTo(1000);

        // A second enemy steps onto the same sightline, nearer. The crosshair never moved.
        vantage.Observe(2, Row(300f, 0f, 0f, 0f, 0f));
        transitions.Sample(1001, 1001, vantage.Sample(1001));

        await Assert.That(transitions.OnTargetSince(0)).IsEqualTo(1000)
            .Because("the crosshair has been on an enemy continuously since 1000");
    }

    /// <summary>
    ///     A viewer who FLICKS from one enemy straight onto another never leaves every enemy, so the
    ///     re-arm sweep never clears their bit and the anchor still reads the arrival on the enemy
    ///     they turned away from. Pinned as an ACCEPTED limit, not a defect.
    ///     <para>
    ///         <b>Measured before it was accepted.</b> Over the five benchmark demos, recording both
    ///         this anchor and a per-pair one for every bullet shot taken with the crosshair on an
    ///         enemy: 11 of 1596 shots carried a nonzero anchor error, and 3 of the 1030 that the
    ///         aimed-reaction column actually counts (0.29%). Ten of the eleven were anchored on a
    ///         DIFFERENT enemy from the one the crosshair was nearest; the eleventh, on dust2 and
    ///         outside the counted population, was the same enemy re-acquired after a detour through
    ///         another one, which the mechanism produces just as readily. The worst single error was
    ///         31 ticks and the worst on a counted shot 17; the mean over the counted population is
    ///         0.026 ticks, about 0.4 ms against a column that reads 70 to 82 ms (the per-demo means
    ///         are 0 on four demos and 0.13 ticks on the fifth). Switching to a per-pair anchor moves
    ///         that column by -2.1 to +0.8 ms per demo, which is inside the rounding the board
    ///         displays.
    ///     </para>
    ///     <para>
    ///         <b>What "different enemy" means in those numbers.</b> The probe recorded the enemy
    ///         NEAREST the crosshair at the shot, not the one the bullet was aimed at, because the
    ///         scanner has no notion of a target. The two differ when the crosshair sits between two
    ///         enemies or the shot misses, so the 0.29% is a proxy. It is a generous one: the
    ///         mechanism needs two enemies inside a cone a couple of degrees wide, crossed inside one
    ///         15.6 ms sample, and four of the five demos produced no such shot at all.
    ///     </para>
    ///     <para>
    ///         <b>The evidence outlives the instrumentation.</b> The probe, the per-pair anchor and
    ///         the bench flag that drove them were all reverted once the measurement was taken, so
    ///         nothing in the tree reproduces these figures today. Anyone reopening this should
    ///         expect to rebuild the probe rather than re-run it.
    ///     </para>
    ///     <para>
    ///         The per-viewer rule is also what
    ///         <see cref="OnTarget_DoesNotRestamp_WhenASecondEnemyEntersTheSameCrosshair" /> depends
    ///         on, so the two cases trade against each other and the rarer one is the one being
    ///         given up.
    ///     </para>
    /// </summary>
    [Test]
    public async Task OnTarget_StampSurvivesAFlickOntoADifferentEnemy()
    {
        AimVantageScanner vantage = Scanner((0, 2), (1, 3), (2, 3));
        VisibilityTransitionScanner transitions = new(VisibilityEngine.FromTriangles([], 0));

        // Viewer at the origin looking down +X, enemy 1 down +X and enemy 2 down +Y, both level
        // with the viewer's eye so a pure yaw change swaps which one the crosshair is on.
        vantage.Observe(0, Row(0f, 0f, -16f, 0f, 0f));
        vantage.Observe(1, Row(500f, 0f, 0f, 0f, 0f));
        vantage.Observe(2, Row(0f, 500f, 0f, 0f, 0f));
        transitions.Sample(1000, 1000, vantage.Sample(1000));
        await Assert.That(transitions.OnTargetSince(0)).IsEqualTo(1000);

        // One second of holding enemy 1, then a flick onto enemy 2 inside a single tick.
        for (int tick = 1001; tick <= 1063; tick++)
        {
            transitions.Sample(tick, tick, vantage.Sample(tick));
        }

        vantage.Observe(0, [null, null, null, 90f, null, null, null]);
        transitions.Sample(1064, 1064, vantage.Sample(1064));

        await Assert.That(transitions.OnTargetSince(0)).IsEqualTo(1000)
            .Because("the per-viewer bit never cleared, so an aimed reaction measured here reads "
                     + "64 ticks rather than 0: rare enough to accept, see the remark above");
    }

    /// <summary>
    ///     The tolerance is the angle a 16-unit half-width subtends AT THE POINT THE ANGLE WAS
    ///     MEASURED TO, which is the chest anchor. Taking the range to the target's feet instead
    ///     inflates it by the eye-to-chest height difference: nothing at 500 units, but a tenth of a
    ///     degree out of nine inside 100, which is where duels are decided.
    /// </summary>
    [Test]
    public async Task OnTarget_ToleranceIsMeasuredAtTheChestAnchorsRange()
    {
        // Eye level with the chest anchor, 100 units from it: 16 units of half-width subtends
        // atan(16 / 100) = 9.09 degrees. The feet are 110.9 away and would give 8.21.
        AimVantageScanner inside = Scanner((0, 2), (1, 3));
        VisibilityTransitionScanner acquired = new(VisibilityEngine.FromTriangles([], 0));
        inside.Observe(0, Row(0f, 0f, -16f, 0f, 8.6f));
        inside.Observe(1, Row(100f, 0f, 0f, 0f, 0f));
        acquired.Sample(1000, 1000, inside.Sample(1000));

        await Assert.That(acquired.OnTargetSince(0)).IsEqualTo(1000)
            .Because("8.6 degrees is 15 units off centre at 100 units, still inside the body");

        AimVantageScanner outside = Scanner((0, 2), (1, 3));
        VisibilityTransitionScanner missed = new(VisibilityEngine.FromTriangles([], 0));
        outside.Observe(0, Row(0f, 0f, -16f, 0f, 9.5f));
        outside.Observe(1, Row(100f, 0f, 0f, 0f, 0f));
        missed.Sample(1000, 1000, outside.Sample(1000));

        await Assert.That(missed.OnTargetSince(0)).IsEqualTo(-1)
            .Because("9.5 degrees clears the body, and a tolerance that admitted it would admit misses");
    }

    /// <summary>
    ///     Pair state is a bit per slot, so a slot the rows cannot hold is refused outright rather
    ///     than reported on partially. PawnLookup derives slot from a controller entity index that a
    ///     well-formed demo keeps in 1..64, so this cannot fire on one; it is the loud arm for a
    ///     malformed one. The last legal slot is exercised too, so the bound is exact.
    /// </summary>
    [Test]
    public async Task Sample_RefusesASlotOutsideTheRows_AndAcceptsTheLastLegalOne()
    {
        VisibilityTransitionScanner transitions = new(VisibilityEngine.FromTriangles([], 0));
        static AimVantage At(int slot, int team, float x)
        {
            Vector3 feet = new(x, 0f, 0f);
            return new AimVantage(
                new VisibilityAnalyzer.Vantage(slot, team, feet, PlayerVantage.Eye(feet, 0f), PlayerVantage.Forward(0f, 0f), true, 0f),
                0f, 0f, 0f);
        }

        Assert.Throws<ArgumentOutOfRangeException>(
            () => transitions.Sample(1000, 1000, [At(0, 2, 0f), At(VisibilityTransitionScanner.MaxSlots, 3, 500f)]));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => transitions.Sample(1000, 1000, [At(-1, 2, 0f), At(1, 3, 500f)]));

        IReadOnlyList<EnemySpottedEvent> spots = transitions.Sample(
            1000, 1000, [At(VisibilityTransitionScanner.MaxSlots - 1, 2, 0f), At(0, 3, 500f)]);
        await Assert.That(spots.Count).IsEqualTo(1);
        await Assert.That(spots[0].ViewerSlot).IsEqualTo(VisibilityTransitionScanner.MaxSlots - 1);
        await Assert.That(transitions.IsAnyEnemyVisibleTo(VisibilityTransitionScanner.MaxSlots - 1)).IsTrue();
        await Assert.That(transitions.IsAnyEnemyVisibleTo(VisibilityTransitionScanner.MaxSlots)).IsFalse()
            .Because("a slot outside the rows is not visible to anyone, not an exception on a read");
        await Assert.That(transitions.OnTargetSince(-1)).IsEqualTo(-1);
    }

    /// <summary>
    ///     Two standing players on the same floor, crosshair dead level: the acquisition test says
    ///     NO, at every range. This is the geometry named in
    ///     <see cref="VisibilityTransitionScanner.OnTargetHalfWidthUnits" />'s remarks, and it is
    ///     pinned here rather than fixed. The cone is centred on the chest and sized to the body's
    ///     HALF-WIDTH, so with the eye 16 units above the chest the measured angle atan(16 / D) is
    ///     always a shade larger than the tolerance atan(16 / sqrt(D^2 + 256)) taken at the chest's
    ///     range.
    ///     <para>
    ///         Every other on-target case in this file puts the viewer's feet at z = -16, which
    ///         parks the eye exactly level with the chest anchor; that is how the limit stayed
    ///         invisible. The two halves below bracket it: level on a same-floor enemy is refused,
    ///         and dropping the crosshair one degree onto the chest is accepted. So the verdict in
    ///         the commonest geometry in the game turns on a tenth of a degree, and a player holding
    ///         head level reads as not-aimed while one aiming at the chest reads as aimed.
    ///     </para>
    ///     <para>
    ///         Correcting it means an anisotropic acceptance (a half-width laterally, a half-HEIGHT
    ///         vertically, or a capsule), which moves <c>enemy_spotted</c> and
    ///         <c>ticks_since_on_target</c> — and so moves both
    ///         <c>tests/fixtures/sample-de_nuke/visibility-trace.golden.txt</c> and the frozen
    ///         <c>ReferenceVisibility.ReferenceScanner</c> the parity suite measures the live
    ///         scanner against. This test is what makes that a deliberate change rather than a
    ///         golden regenerated to make a suite go green.
    ///     </para>
    /// </summary>
    [Test]
    public async Task OnTarget_IsRefusedForALevelCrosshairOnASameFloorEnemy()
    {
        AimVantageScanner level = Scanner((0, 2), (1, 3));
        VisibilityTransitionScanner refused = new(VisibilityEngine.FromTriangles([], 0));

        // Both standing on z = 0, 500 apart, viewer looking straight down +X with no pitch: the
        // crosshair is level with the target's HEAD, which is where players hold it.
        level.Observe(0, Row(0f, 0f, 0f, 0f, 0f));
        level.Observe(1, Row(500f, 0f, 0f, 0f, 0f));
        IReadOnlyList<EnemySpottedEvent> spots = refused.Sample(1000, 1000, level.Sample(1000));

        await Assert.That(spots.Count).IsEqualTo(1)
            .Because("it is still a spot — only the acquisition verdict is at issue");
        await Assert.That(spots[0].AngleToChestDeg).IsEqualTo(1.8328f).Within(0.001f);
        await Assert.That(refused.OnTargetSince(0)).IsEqualTo(-1)
            .Because("the tolerance at the chest's range is 1.8319 degrees, a hair under the 1.8328 "
                     + "a level crosshair measures — see OnTargetHalfWidthUnits");

        // The same crosshair pitched one degree down, onto the chest rather than the head.
        AimVantageScanner onChest = Scanner((0, 2), (1, 3));
        VisibilityTransitionScanner accepted = new(VisibilityEngine.FromTriangles([], 0));
        onChest.Observe(0, Row(0f, 0f, 0f, 1f, 0f));
        onChest.Observe(1, Row(500f, 0f, 0f, 0f, 0f));
        accepted.Sample(1000, 1000, onChest.Sample(1000));

        await Assert.That(accepted.OnTargetSince(0)).IsEqualTo(1000)
            .Because("one degree of pitch is the whole difference between the two verdicts");
    }

    // ── Round boundaries and feed interruptions ──────────────────────────────

    /// <summary>
    ///     A pair still in each other's view when a round ends would otherwise carry that visibility
    ///     into the next round and suppress its own first contact — the one event this whole path
    ///     exists to produce, dropped silently, with <c>SpottedEnrichmentEdge</c> then numbering
    ///     whichever contact came second as the round's first.
    /// </summary>
    [Test]
    public async Task Reset_ReArmsTheFirstContactOfTheNextRound()
    {
        AimVantageScanner vantage = Scanner((0, 2), (1, 3));
        VisibilityTransitionScanner transitions = new(VisibilityEngine.FromTriangles([], 0));

        vantage.Observe(0, Row(0f, 0f, 0f, 0f, 0f));
        vantage.Observe(1, Row(500f, 0f, 0f, 0f, 0f));
        await Assert.That(transitions.Sample(1000, 1000, vantage.Sample(1000)).Count).IsEqualTo(1);
        await Assert.That(transitions.Sample(1001, 1001, vantage.Sample(1001)).Count).IsEqualTo(0)
            .Because("the pair is still visible, and a hold is not an edge");

        transitions.Reset();

        await Assert.That(transitions.Sample(1002, 1002, vantage.Sample(1002)).Count).IsEqualTo(1)
            .Because("first contact of a round is a first contact even for a pair that was still "
                     + "looking at each other when the last one ended");
    }

    /// <summary>
    ///     The acquisition stamp outlives everything else, because the re-arm sweep that expires it
    ///     runs inside <c>Sample</c>: stop sampling and it freezes rather than clearing, and a
    ///     frozen stamp does not read as missing data. It reads as an acquisition still in progress,
    ///     so an aimed reaction measured against it comes back as however long the feed has been
    ///     down — a plausible-looking number, under the sentinel, in a column of milliseconds.
    /// </summary>
    [Test]
    public async Task Reset_DropsTheAcquisitionAnchorAndTheVisibleLevel()
    {
        AimVantageScanner vantage = Scanner((0, 2), (1, 3));
        VisibilityTransitionScanner transitions = new(VisibilityEngine.FromTriangles([], 0));

        vantage.Observe(0, Row(0f, 0f, -16f, 0f, 0f));
        vantage.Observe(1, Row(500f, 0f, 0f, 0f, 0f));
        transitions.Sample(1000, 1000, vantage.Sample(1000));
        await Assert.That(transitions.OnTargetSince(0)).IsEqualTo(1000);
        await Assert.That(transitions.IsAnyEnemyVisibleTo(0)).IsTrue();

        transitions.Reset();

        await Assert.That(transitions.OnTargetSince(0)).IsEqualTo(-1);
        await Assert.That(transitions.IsAnyEnemyVisibleTo(0)).IsFalse();
    }

    /// <summary>
    ///     Attached to the index, the scanner ends its round on the same edge every latched
    ///     per-player anchor does, so "first contact of the round" means one thing to the scanner
    ///     that emits the contact and to the enrichment that numbers it.
    /// </summary>
    [Test]
    public async Task RoundStateReset_EndsTheScannersRound_WhenItIsAttachedToTheIndex()
    {
        AimVantageScanner vantage = Scanner((0, 2), (1, 3));
        VisibilityTransitionScanner transitions = new(VisibilityEngine.FromTriangles([], 0));
        PlayerContextIndex index = new() { VisibilityTransitions = transitions };
        index.Register(0, new PlayerContextIndex.PlayerContext(0, 2));

        vantage.Observe(0, Row(0f, 0f, -16f, 0f, 0f));
        vantage.Observe(1, Row(500f, 0f, 0f, 0f, 0f));
        await Assert.That(transitions.Sample(1000, 1000, vantage.Sample(1000)).Count).IsEqualTo(1);
        await Assert.That(transitions.OnTargetSince(0)).IsEqualTo(1000);

        index.ResetRoundState();

        await Assert.That(transitions.OnTargetSince(0)).IsEqualTo(-1);
        await Assert.That(transitions.Sample(1001, 1001, vantage.Sample(1001)).Count).IsEqualTo(1);
    }

    /// <summary>
    ///     End to end through <see cref="EntityChangeScanner" />: a compromised digest closes the
    ///     vantage feed, and the transition scanner is RESET rather than merely skipped.
    ///     <para>
    ///         Skipping alone is what made this worth a test. The freeze is sticky, so from the
    ///         first bad digest the scanner is never sampled again and its state — the visible set,
    ///         the acquisition stamps — stands frozen for the rest of the demo. A shot ten thousand
    ///         ticks later then reads the stamp as live and reports a 156-second aimed reaction as a
    ///         measurement, comfortably under every sentinel a gate could catch it with.
    ///     </para>
    /// </summary>
    [Test]
    public async Task CompromisedDecode_ResetsTheTransitionScanner_RatherThanFreezingIt()
    {
        VisibilityTransitionScanner transitions = new(VisibilityEngine.FromTriangles([], 0));
        AimVantageScanner vantage = new(
            _aimProviders.Select(provider => provider.Name).ToList(), slot => slot == 0 ? 2 : 3);
        EntityChangeScanner entities = new(
            new EntityStateLayer([]),
            providers: [],
            perPlayerProviders: _aimProviders,
            emitMolotovThrows: false,
            vantageScanner: vantage,
            transitionScanner: transitions);

        // Frame 0 puts slot 0's crosshair on slot 1's chest; frame 1's decode is compromised.
        entities.InjectDigests(
        [
            AimDigest(false, (0, AimRow(0f, 0f, -16f, 0f, 0f)), (1, AimRow(500f, 0f, 0f, 0f, 0f))),
            AimDigest(true)
        ]);

        entities.AdvanceAndPollAt(0, 1000);
        await Assert.That(transitions.OnTargetSince(0)).IsEqualTo(1000);
        await Assert.That(transitions.IsAnyEnemyVisibleTo(0)).IsTrue();

        entities.AdvanceAndPollAt(1, 1001);

        await Assert.That(transitions.OnTargetSince(0)).IsEqualTo(-1)
            .Because("a hole in the feed is missing data, not a held crosshair");
        await Assert.That(transitions.IsAnyEnemyVisibleTo(0)).IsFalse();
    }

    // ── Catalog surface ──────────────────────────────────────────────────────

    [Test]
    public async Task EnemySpottedView_IsAuthorable_WithItsFacetsAndTargetRole()
    {
        // The scanner and the catalog entry are only half the feature: this is the pin that an
        // author can actually reach it. Exercises all four facets (the event-field one and the
        // three enrichments) plus the target role handle, which is the piece that would silently
        // fail if the second role were dropped from the view.
        const string Yaml = """
                            ruleset: spotted_authorable
                            for: each_player
                            stats:
                              first_contacts:
                                count: enemy_spotted
                                match: { first_contact: true }
                                per: match
                              tight_preaim:
                                count: enemy_spotted
                                where: 'event.AngleToChestDeg < 10.0 and enrich.spotted.spot_index >= 1'
                                per: match
                              spots_on_low_enemies:
                                count: enemy_spotted
                                where: 'target.health < 50'
                                per: match
                            """;

        RulesetDocumentLoader.Outcome outcome = RulesetDocumentLoader.Load(Yaml, "test.rules.yaml");
        await Assert.That(outcome.Doc).IsNotNull()
            .Because("YAML failed to map: " + string.Join("; ", outcome.Diagnostics));

        RulesetResolveResult result = CheckedRulesetDraft
            .Load(outcome.Doc!, CatalogScopeAdapter.From(CatalogResource.Load()))
            .Build(64.0, "Cs2GotvProfile");

        await Assert.That(result.Success).IsTrue()
            .Because("the enemy_spotted view must resolve and type-check: "
                     + string.Join("; ", result.Diagnostics.Select(d => $"{d.Code}: {d.Message}")));
        await Assert.That(result.Ruleset!.Stats.All(s =>
                s.ConcreteEvents.Contains("enemy_spotted", StringComparer.Ordinal)))
            .IsTrue()
            .Because("every stat must resolve to the synthesized event the scanner produces");
    }

    // ── SpottedEnrichmentEdge ────────────────────────────────────────────────

    [Test]
    public async Task ContactHistory_CountsSpotsAndGapsPerViewer_AndResetsEachRound()
    {
        PlayerContextIndex index = new();
        index.Register(0, new PlayerContextIndex.PlayerContext(0, 2));

        TransientValueNode<int> gap = new(
            "enrich.spotted.ticks_since_last_spot", SpottedEnrichmentEdge.NoPreviousSpotSentinel);
        TransientValueNode<int> spotIndex = new("enrich.spotted.spot_index");
        TransientBoolNode firstContact = new("enrich.spotted.is_first_contact");
        GenericBoolNode root = new("root");
        SpottedEnrichmentEdge edge = new(root, index, gap, spotIndex, firstContact);

        await Assert.That(Dispatch(edge, gap, spotIndex, firstContact, 1000, 0, 1)).IsTrue();
        await Assert.That(gap.Value).IsEqualTo(SpottedEnrichmentEdge.NoPreviousSpotSentinel);
        await Assert.That(spotIndex.Value).IsEqualTo(1);
        await Assert.That(firstContact.IsActive).IsTrue();

        Dispatch(edge, gap, spotIndex, firstContact, 1032, 0, 2);
        await Assert.That(gap.Value).IsEqualTo(32);
        await Assert.That(spotIndex.Value).IsEqualTo(2);
        await Assert.That(firstContact.IsActive).IsFalse();

        index.ResetRoundState();
        Dispatch(edge, gap, spotIndex, firstContact, 2000, 0, 1);
        await Assert.That(gap.Value).IsEqualTo(SpottedEnrichmentEdge.NoPreviousSpotSentinel);
        await Assert.That(spotIndex.Value).IsEqualTo(1);
        await Assert.That(firstContact.IsActive).IsTrue();
    }

    private static bool Dispatch(
        SpottedEnrichmentEdge edge,
        TransientValueNode<int> gap,
        TransientValueNode<int> spotIndex,
        TransientBoolNode firstContact,
        int tick, int viewer, int target)
    {
        // Mirrors StateGraphEvaluator's transient-reset loop, so each assertion sees exactly what a
        // rule's where: read would see on that one event.
        ((ITransientNode)gap).Reset();
        ((ITransientNode)spotIndex).Reset();
        ((ITransientNode)firstContact).Reset();

        EnemySpottedEvent spotted = new(tick, tick, tick, viewer, target, 3.5f, 0f, 0f);
        GameEventMessage msg = GameEventMessage.ForSynthesizedEvent(spotted);
        return edge.TryApplyDirect(spotted, new EvaluationContext(msg, new DemoFrame
        {
            CommandKind = EDemoCommands.DemPacket,
            FrameNumber = 0,
            ServerTick = tick,
            RawStart = 0,
            RawLength = 1,
            HeaderLength = 1,
            IsCompressed = false,
            MessageList = [msg]
        }));
    }
}
