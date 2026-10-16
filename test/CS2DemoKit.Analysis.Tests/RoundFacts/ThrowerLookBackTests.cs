#region

using System.Globalization;
using CS2DemoKit.Analysis.Output;
using CS2DemoKit.Parser;
using CS2DemoKit.TestSupport;

#endregion

namespace CS2DemoKit.Analysis.Tests.RoundFacts;

/// <summary>
///     On the sample, <c>thrower.pos_x@-8</c> captured on <c>projectile_created</c> in a projectile
///     ruleset reads the same cell as <c>player.pos_x@-8</c> captured on the <c>projectile_thrown</c>
///     view in a per-player ruleset: one throw, one slot, one frame, one history.
/// </summary>
[Category("Integration")]
[NotInParallel]
public class ThrowerLookBackTests
{
    private const string Rules = """
        ruleset: throws
        for: each_projectile
        stats:
          now_x:  { capture: thrower.pos_x,     on: raw.projectile_created }
          back_x: { capture: thrower.pos_x@-8,  on: raw.projectile_created }
          back_z: { capture: thrower.pos_z@-40, on: raw.projectile_created }
        show:
          tables:
            grenades:
              per: projectile
              columns:
                - { stat: now_x, label: now_x }
                - { stat: back_x, label: back_x }
                - { stat: back_z, label: back_z }
        ---
        ruleset: throwers
        for: each_player
        stats:
          now_x:  { capture: player.pos_x,     on: projectile_thrown, per: match }
          back_x: { capture: player.pos_x@-8,  on: projectile_thrown, per: match }
          back_z: { capture: player.pos_z@-40, on: projectile_thrown, per: match }
        show:
          tables:
            throws:
              per: player_event
              on: projectile_thrown
              columns:
                - { stat: now_x, label: now_x }
                - { stat: back_x, label: back_x }
                - { stat: back_z, label: back_z }
        """;

    [Test]
    public async Task ThrowerLookBack_EqualsThePlayerLookBack_OnTheSameThrow()
    {
        string path = DemoTestHelper.RequireDemo(DemoTestHelper.SampleDemoFileName);
        ParsedDemo demo = RoundFactsTestSupport.Sample();

        foreach ((string label, AnalysisRun run) in RoundFactsCorpusTests.RunBothPaths(path, RoundFactsTestSupport.Load(Rules)))
        {
            await Assert.That(run.Build.ExcludedRulesets).IsEmpty()
                .Because($"{label}: {string.Join("; ", run.Build.ExcludedRulesets.SelectMany(e => e.Diagnostics))}");

            Dictionary<(int Tick, int Slot), MetricRow> byThrow = [];
            foreach (MetricRow row in RoundFactsTestSupport.Table(run, "throws", demo).Rows)
            {
                byThrow[(RoundFactsTestSupport.Dim(row, "tick"), RoundFactsTestSupport.Dim(row, "player_slot"))] = row;
            }

            MetricTable grenades = RoundFactsTestSupport.Table(run, "grenades", demo);
            int compared = 0, moved = 0;
            foreach (MetricRow row in grenades.Rows)
            {
                (int Tick, int Slot) key = (RoundFactsTestSupport.Dim(row, "created_tick"), RoundFactsTestSupport.Dim(row, "player_slot"));
                string because = $"{label}: throw by slot {key.Slot} at tick {key.Tick}";
                await Assert.That(byThrow.ContainsKey(key)).IsTrue().Because(because);
                MetricRow thrower = byThrow[key];

                foreach (string column in new[] { "now_x", "back_x", "back_z" })
                {
                    await Assert.That(Value(row, column)).IsEqualTo(Value(thrower, column)).Because($"{because} {column}");
                }

                compared++;
                moved += Value(row, "now_x") != Value(row, "back_x") ? 1 : 0;
            }

            Console.WriteLine($"{label}: {compared} throws compared, {moved} moved in the 8 ticks before the throw");
            await Assert.That(compared).IsGreaterThan(10).Because(label);
            await Assert.That(moved).IsGreaterThan(0).Because(label);
        }
    }

    private static double Value(MetricRow row, string column) =>
        Convert.ToDouble(row.Values[column], CultureInfo.InvariantCulture);
}
