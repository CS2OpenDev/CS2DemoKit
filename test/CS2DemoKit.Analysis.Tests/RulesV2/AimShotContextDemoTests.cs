#region

using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Analysis.Graphs;
using CS2DemoKit.Analysis.Nodes;
using CS2DemoKit.Parser;
using CS2DemoKit.TestSupport;

#endregion

namespace CS2DemoKit.Analysis.Tests.RulesV2;

/// <summary>
///     End-to-end pin for the shot-anchored aim enrichments against a real demo: a ruleset that names
///     only the <c>shot</c> view's new facets must, by itself, pull ten per-player entity columns into
///     the digest, build the vantage sampler, prime six provider schema paths against the demo's own
///     serializers, and produce numbers that are neither all-zero nor all-everything.
///     <para>
///         This is the gate the unit tests cannot be: <see cref="AimShotContextEdgeTests" /> feeds
///         hand-built digest rows, so it proves the arithmetic and proves nothing about the wiring.
///         Every failure mode this file exists for is silent under the unit tests: a reference gate
///         that never fires leaves the columns out and every count reads 0; a wrong field path on the
///         two-hop recoil read leaves that column null, which looks like "this player had no weapon";
///         a <c>QAngle</c> provider whose declared type is rejected throws at prime time.
///     </para>
///     <para>Parses the demo, so <see cref="NotInParallelAttribute" /> and the shared parse cache apply.</para>
/// </summary>
[Category("Integration")]
[NotInParallel]
public class AimShotContextDemoTests
{
    private const string Yaml = """
                                ruleset: aim_shot_context_probe
                                for: each_player
                                stats:
                                  bullet_shots:
                                    count: shot
                                    match: { bullet: true }
                                    per: match
                                  cs_admitted:
                                    count: shot
                                    match: { bullet: true, counter_strafe_admitted: true }
                                    per: match
                                  cs_good:
                                    count: shot
                                    match: { bullet: true, counter_strafe_admitted: true, counter_strafe_good: true }
                                    per: match
                                  cs_bad:
                                    count: shot
                                    match: { bullet: true, counter_strafe_admitted: true, counter_strafe_good: false }
                                    per: match
                                  first_bullets:
                                    count: shot
                                    match: { bullet: true, first_bullet: true }
                                    per: match
                                  residual_measured:
                                    count: shot
                                    match: { bullet: true, spray_residual_measured: true }
                                    per: match
                                  residual_pitch_sq:
                                    sum: "enrich.shot.spray_residual_pitch * enrich.shot.spray_residual_pitch"
                                    on: shot
                                    match: { bullet: true, spray_residual_measured: true }
                                    per: match
                                """;

    /// <summary>
    ///     Runs the probe ruleset and asserts every column is a measurement rather than a default.
    /// </summary>
    [Test]
    public async Task AimEnrichments_AreProducedFromEntityState_OnARealDemo()
    {
        ParsedDemo demo = DemoTestHelper.GetOrParse(DemoTestHelper.RequireDemo(DemoTestHelper.SampleDemoFileName));

        BuildResult build = V2KindGoldenSupport.CompileV2(demo, Yaml);
        AnalysisRun run = DemoAnalysis.Evaluate(demo, build);

        Dictionary<int, int> shots = ReadCounter(run, "bullet_shots");
        Dictionary<int, int> admitted = ReadCounter(run, "cs_admitted");
        Dictionary<int, int> good = ReadCounter(run, "cs_good");
        Dictionary<int, int> bad = ReadCounter(run, "cs_bad");
        Dictionary<int, int> firstBullets = ReadCounter(run, "first_bullets");
        Dictionary<int, int> residualMeasured = ReadCounter(run, "residual_measured");
        Dictionary<int, double> residualSq = ReadSum(run, "residual_pitch_sq");

        int shotTotal = shots.Values.Sum();
        int admittedTotal = admitted.Values.Sum();
        int goodTotal = good.Values.Sum();
        int badTotal = bad.Values.Sum();
        int firstBulletTotal = firstBullets.Values.Sum();
        int measuredTotal = residualMeasured.Values.Sum();
        double residualTotal = residualSq.Values.Sum();

        Console.WriteLine($"[aim-shot] bullet shots      : {shotTotal}");
        Console.WriteLine($"[aim-shot] counter-strafe    : {admittedTotal} admitted "
                          + $"({goodTotal} good / {badTotal} bad)");
        Console.WriteLine($"[aim-shot] first bullets     : {firstBulletTotal}");
        Console.WriteLine($"[aim-shot] residual measured : {measuredTotal}");

        await Assert.That(shotTotal).IsGreaterThan(0)
            .Because("the demo has bullet shots; zero here means the shot view never fired at all");

        // Both directions on the admission gate. All-admitted would mean the lookback is a no-op and
        // CS% has quietly become "how often did this player stand still"; none-admitted would mean the
        // speed history never reached the edge and the metric is empty rather than wrong.
        await Assert.That(admittedTotal).IsGreaterThan(0)
            .Because("players do move before shooting, so some shots must enter the population");
        await Assert.That(admittedTotal).IsLessThan(shotTotal)
            .Because("a held angle is not a counter-strafe attempt; admitting every shot means the "
                     + "movement lookback never actually gated anything");

        // The verdict splits. A one-sided split would mean the threshold or the speed derivation is
        // degenerate (a column of zeros reads as a perfect counter-strafe on every shot).
        await Assert.That(goodTotal + badTotal).IsEqualTo(admittedTotal)
            .Because("good and bad partition the admitted population");
        await Assert.That(goodTotal).IsGreaterThan(0);
        await Assert.That(badTotal).IsGreaterThan(0)
            .Because("some admitted shots are fired while still moving; zero would mean max_speed or "
                     + "the derived speed read as a constant");

        // Spray segmentation is doing work: neither every shot nor no shot opens a run. This is also
        // the only assertion that proves the TWO-HOP recoil read resolved, because a null recoil
        // column would leave the gap bound as the sole segmentation signal.
        await Assert.That(firstBulletTotal).IsGreaterThan(0);
        await Assert.That(firstBulletTotal).IsLessThan(shotTotal)
            .Because("continuous fire means most shots continue a run rather than opening one");

        // Spray control. The residual is only meaningful over the shots the measured gate admits, and
        // the RMS over them has to land in a range a human wrist can produce: single-digit degrees.
        // The upper bound is not theoretical: before the aim-punch plausibility guard this demo
        // produced an RMS of 262 degrees from a column that decodes to roughly -94, and every one of
        // those shots would have entered a spray-control metric as data.
        //
        // On THIS demo the population is empty, because the guard rejects the punch column outright
        // and no bullet_damage stream exists for the landed arm to anchor on. That is stated here
        // rather than hidden behind a ternary that turns an empty population into an RMS of 0 and
        // then passes the bound: an assertion over nothing is not the assertion it reads as.
        await Assert.That(measuredTotal).IsLessThanOrEqualTo(shotTotal);
        if (measuredTotal > 0)
        {
            double residualRms = Math.Sqrt(residualTotal / measuredTotal);
            Console.WriteLine($"[aim-shot] residual pitch RMS: {residualRms:F3} deg");
            await Assert.That(residualRms).IsLessThan(MaxPlausibleSprayRmsDegrees)
                .Because("a spray residual is the recoil a player failed to pull down, which is "
                         + "degrees, not hundreds of degrees; a larger RMS means the effective aim "
                         + "is not an aim");
        }
        else
        {
            Console.WriteLine("[aim-shot] residual pitch RMS: no population (punch column rejected)");
            await Assert.That(residualTotal).IsEqualTo(0.0)
                .Because("an empty measured population must contribute nothing to the sum; a "
                         + "non-zero sum against a zero count means residuals are reaching the "
                         + "aggregate without passing spray_residual_measured, which is the gate "
                         + "every spray-control metric relies on");
        }
    }

    /// <summary>
    ///     Upper bound on a believable spray-control RMS in degrees. An AK's full 30-round pattern
    ///     spans roughly 25 degrees of vertical climb, so an RMS above that is not a player who
    ///     controls recoil badly, it is a column that is not an angle.
    /// </summary>
    private const double MaxPlausibleSprayRmsDegrees = 30.0;

    /// <summary>Reads a match-scoped counter node's per-player final value into slot to count.</summary>
    private static Dictionary<int, int> ReadCounter(AnalysisRun run, string nodeName)
    {
        EvaluationResult result = run.Snapshots
                                  ?? throw new InvalidOperationException("evaluation produced no snapshots");
        Dictionary<int, int> byslot = new();
        foreach (PerPlayerNodeTemplate.MaterializedPlayer mp in result.MaterializedPlayers)
        {
            GenericValueNode<int>? node = mp.Nodes
                .OfType<GenericValueNode<int>>()
                .FirstOrDefault(n => string.Equals(n.Name, nodeName, StringComparison.Ordinal));
            byslot[mp.PlayerSlot] = node?.Value ?? 0;
        }

        return byslot;
    }

    /// <summary>Reads a match-scoped sum node's per-player final value into slot to total.</summary>
    private static Dictionary<int, double> ReadSum(AnalysisRun run, string nodeName)
    {
        EvaluationResult result = run.Snapshots
                                  ?? throw new InvalidOperationException("evaluation produced no snapshots");
        Dictionary<int, double> byslot = new();
        foreach (PerPlayerNodeTemplate.MaterializedPlayer mp in result.MaterializedPlayers)
        {
            GenericValueNode<double>? node = mp.Nodes
                .OfType<GenericValueNode<double>>()
                .FirstOrDefault(n => string.Equals(n.Name, nodeName, StringComparison.Ordinal));
            byslot[mp.PlayerSlot] = node?.Value ?? 0.0;
        }

        return byslot;
    }
}
