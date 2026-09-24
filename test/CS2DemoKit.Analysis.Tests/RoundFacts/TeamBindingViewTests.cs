#region

using CS2DemoKit.Analysis.Output;
using CS2DemoKit.Analysis.RulesetsV2.Resolve;
using CS2DemoKit.Parser;

#endregion

namespace CS2DemoKit.Analysis.Tests.RoundFacts;

/// <summary>
///     <c>round_won</c> and <c>round_lost</c> filter on the round's winner against the ruleset player's
///     live team (views.yaml's <c>binding: team</c>). The binding was documented and never lowered:
///     both views fired for every player, so each read 1 in every round.
/// </summary>
[Category("Integration")]
[NotInParallel]
public class TeamBindingViewTests
{
    [Test]
    [Category("Unit")]
    public async Task TeamViews_DeclareTheWinnerReads_TheBindingNeeds()
    {
        CheckedRuleset rs = RoundFactsTestSupport.Checked("""
            ruleset: wl
            for: each_player
            stats:
              won:
                count: round_won
                per: round
              lost_any:
                count: round_lost
                match: { actor: any }
                per: round
            """);

        await Assert.That(rs.Stats[0].DeclaredReads).Contains("enrich.round.has_winner");
        await Assert.That(rs.Stats[0].DeclaredReads).Contains("enrich.round.winner_team");
        // actor: any suppresses the binding, and with it the reads it would make.
        await Assert.That(rs.Stats[1].DeclaredReads).DoesNotContain("enrich.round.winner_team");
    }

    [Test]
    public async Task EachPlayer_WonPlusLost_IsOnePerClosedRound()
    {
        ParsedDemo demo = RoundFactsTestSupport.Sample();
        AnalysisRun run = RoundFactsTestSupport.Run(demo, """
            ruleset: wl
            for: each_player
            stats:
              won:
                count: round_won
                per: round
              lost:
                count: round_lost
                per: round
              closed:
                count: round_ended
                per: round
            show:
              tables:
                wl:
                  per: player_round
                  columns:
                    - { stat: won, label: Won }
                    - { stat: lost, label: Lost }
                    - { stat: closed, label: Closed }
            """);

        MetricTable table = RoundFactsTestSupport.Table(run, "wl", demo);
        int won = 0, lost = 0, closedRows = 0;
        foreach (MetricRow row in table.Rows)
        {
            int w = RoundFactsTestSupport.Int(row, "Won") ?? 0;
            int l = RoundFactsTestSupport.Int(row, "Lost") ?? 0;
            int c = RoundFactsTestSupport.Int(row, "Closed") ?? 0;
            won += w;
            lost += l;
            if (c == 0)
            {
                await Assert.That(w + l).IsEqualTo(0);
                continue;
            }

            closedRows++;
            await Assert.That(w + l).IsEqualTo(1)
                .Because($"round {RoundFactsTestSupport.Dim(row, "round_number")} slot {row.Dimensions["player_slot"]}");
        }

        Console.WriteLine($"[binding] rows={closedRows} won={won} lost={lost}");
        await Assert.That(closedRows).IsGreaterThan(0);
        await Assert.That(won).IsGreaterThan(0);
        await Assert.That(lost).IsGreaterThan(0);
    }
}
