#region

using CS2DemoKit.Analysis.Output;
using CS2DemoKit.Parser;

#endregion

namespace CS2DemoKit.Analysis.Tests.RoundFacts;

/// <summary>
///     A read of a singleton provider (<c>match.freeze_period</c>) builds and evaluates in every
///     expression site, in both scopes. It used to be spelled as a per-player read and threw
///     "Unknown per-player entity provider: 'entity.game.freeze_period'" at build.
/// </summary>
[Category("Integration")]
[NotInParallel]
public class SingletonProviderReadTests
{
    [Test]
    public async Task EachPlayer_SingletonRead_InEverySite_Evaluates()
    {
        ParsedDemo demo = RoundFactsTestSupport.Sample();
        AnalysisRun run = RoundFactsTestSupport.Run(demo, """
            ruleset: singleton_pp
            for: each_player
            stats:
              kills:
                count: kill
                per: round
              fp_at_kill:
                capture: match.freeze_period
                on: kill
                per: round
              kills_outside_freeze:
                count: kill
                where: "match.freeze_period == false"
                per: round
              kills_while_live:
                count: kill
                while: "match.freeze_period == false"
                per: round
              in_freeze:
                flag:
                  when: "match.freeze_period"
                per: round
            show:
              tables:
                s:
                  per: player_round
                  columns:
                    - { stat: kills, label: K }
                    - { stat: fp_at_kill, label: FP }
                    - { stat: kills_outside_freeze, label: KW }
                    - { stat: kills_while_live, label: KL }
            """);

        MetricTable table = RoundFactsTestSupport.Table(run, "s", demo);
        int kills = table.Rows.Sum(r => RoundFactsTestSupport.Int(r, "K") ?? 0);
        await Assert.That(kills).IsGreaterThan(0);
        // Kills happen outside the freeze period, so every gated count equals the plain count.
        await Assert.That(table.Rows.Sum(r => RoundFactsTestSupport.Int(r, "KW") ?? 0)).IsEqualTo(kills);
        await Assert.That(table.Rows.Sum(r => RoundFactsTestSupport.Int(r, "KL") ?? 0)).IsEqualTo(kills);
    }

    [Test]
    public async Task MatchScope_SingletonRead_Evaluates()
    {
        ParsedDemo demo = RoundFactsTestSupport.Sample();
        AnalysisRun run = RoundFactsTestSupport.Run(demo, """
            ruleset: singleton_match
            for: match
            stats:
              kills:
                count: kill
                per: match
              kills_outside_freeze:
                count: kill
                where: "match.freeze_period == false"
                per: match
            show:
              tables:
                m:
                  per: match
                  columns:
                    - { stat: kills, label: K }
                    - { stat: kills_outside_freeze, label: KW }
            """);

        MetricRow row = RoundFactsTestSupport.Table(run, "m", demo).Rows.Single();
        await Assert.That(RoundFactsTestSupport.Int(row, "K") ?? 0).IsGreaterThan(0);
        await Assert.That(RoundFactsTestSupport.Int(row, "KW")).IsEqualTo(RoundFactsTestSupport.Int(row, "K"));
    }
}
