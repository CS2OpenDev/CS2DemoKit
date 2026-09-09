#region

using System.Numerics;
using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Analysis.Building;
using CS2DemoKit.Analysis.Catalog;
using CS2DemoKit.Analysis.Edges;
using CS2DemoKit.Analysis.Events;
using CS2DemoKit.Analysis.Nodes;
using CS2DemoKit.Analysis.RulesetsV2.Model;
using CS2DemoKit.Analysis.RulesetsV2.Resolve;
using CS2DemoKit.Analysis.Visibility;
using CS2DemoKit.Analysis.Yaml;
using CS2DemoKit.Parser;
using CS2DemoKit.Parser.GameEvents;

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
            Command = "DEM_Packet",
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
