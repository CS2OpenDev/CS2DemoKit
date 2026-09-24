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

    /// <summary>
    ///     Each closed round, exactly the players on the side the server named read <c>won</c>, and
    ///     exactly the players on the other side read <c>lost</c>. Checking only that the two sum to
    ///     one would pass with the binding inverted, so the winner comes from <c>round_decided</c> and
    ///     the sides from the <c>for: each_team</c> freeze-end rosters.
    /// </summary>
    [Test]
    public async Task EachPlayer_WonGoesToTheDecidedWinnersSide()
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
            """, """
            ruleset: rosters
            for: each_team
            stats:
              side:
                capture: team.side
                on: round_ended
                per: round
            show:
              tables:
                rosters:
                  per: team_round
                  columns:
                    - { stat: side, label: S }
            """, """
            ruleset: decided
            for: match
            stats:
              winners:
                capture: event.Winner
                on: round_decided
                keep: list
                per: match
            """);

        MetricTable table = RoundFactsTestSupport.Table(run, "wl", demo);
        MetricTable rosters = RoundFactsTestSupport.Table(run, "rosters", demo);
        int[] winners = RoundFactsCorpusTests.Ints(run, "decided.winners");

        int closedRows = 0, checkedRounds = 0;
        foreach (MetricRow row in table.Rows)
        {
            int w = RoundFactsTestSupport.Int(row, "Won") ?? 0;
            int l = RoundFactsTestSupport.Int(row, "Lost") ?? 0;
            int c = RoundFactsTestSupport.Int(row, "Closed") ?? 0;
            if (c == 0)
            {
                await Assert.That(w + l).IsEqualTo(0);
                continue;
            }

            closedRows++;
            await Assert.That(w + l).IsEqualTo(1)
                .Because($"round {RoundFactsTestSupport.Dim(row, "round_number")} slot {row.Dimensions["player_slot"]}");
        }

        foreach (IGrouping<int, MetricRow> round in rosters.Rows.GroupBy(r => RoundFactsTestSupport.Dim(r, "round_number")))
        {
            int winner = winners[round.Key - 1];
            List<MetricRow> roundPlayers = table.Rows
                .Where(r => RoundFactsTestSupport.Dim(r, "round_number") == round.Key)
                .ToList();
            foreach (MetricRow side in round)
            {
                int sideNumber = RoundFactsTestSupport.Dim(side, "side");
                bool sideWon = sideNumber == winner;
                int[] slots = (side.Dimensions["slots"]?.ToString() ?? "")
                    .Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Select(int.Parse)
                    .ToArray();
                await Assert.That(slots.Length).IsEqualTo(5).Because($"round {round.Key} side {sideNumber}");
                foreach (int slot in slots)
                {
                    MetricRow member = roundPlayers.Single(r => RoundFactsTestSupport.Dim(r, "player_slot") == slot);
                    string because = $"round {round.Key}: slot {slot} on side {sideNumber}, server winner {winner}";
                    await Assert.That(RoundFactsTestSupport.Int(member, "Won") ?? 0).IsEqualTo(sideWon ? 1 : 0)
                        .Because(because);
                    await Assert.That(RoundFactsTestSupport.Int(member, "Lost") ?? 0).IsEqualTo(sideWon ? 0 : 1)
                        .Because(because);
                }
            }

            checkedRounds++;
        }

        Console.WriteLine($"[binding] rows={closedRows} rounds={checkedRounds} winners={string.Join(",", winners)}");
        await Assert.That(closedRows).IsGreaterThan(0);
        await Assert.That(checkedRounds).IsEqualTo(winners.Length);
        // Both sides win at least once on the sample, so neither Won nor Lost can pass by being
        // constant.
        await Assert.That(winners.Distinct().Count()).IsEqualTo(2);
    }
}
