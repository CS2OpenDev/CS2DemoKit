#region

using CS2DemoKit.Analysis.Output;
using CS2DemoKit.Analysis.RulesetsV2.Resolve;
using CS2DemoKit.Parser;
using CS2DemoKit.Parser.GameEvents;
using CS2DemoKit.TestSupport;

using CS2OpenSchema.Protos;

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
    ///     A subject on neither side (a spectator or coach on team 1, or a slot whose team was never
    ///     seen) neither wins nor loses a round. The winner is always 2 or 3, so a lost binding of
    ///     <c>winner_team != player.team</c> alone had them lose every decided round. Synthetic
    ///     frames: a terrorist, a counter-terrorist and a spectator, one round closed with the
    ///     counter-terrorists as the (derived) winner.
    /// </summary>
    [Test]
    [Category("Unit")]
    public async Task EachPlayer_ASubjectOnNeitherSide_NeitherWinsNorLoses()
    {
        ParsedDemo sample = RoundFactsTestSupport.Sample();
        Dictionary<int, PlayerInfo> players = new()
        {
            [0] = new PlayerInfo(0, "T", 0UL, 0, 2, false),
            [1] = new PlayerInfo(1, "CT", 0UL, 1, 3, false),
            [2] = new PlayerInfo(2, "Spectator", 0UL, 2, 1, false)
        };

        DemoFrame[] frames =
        [
            Frame(1,
                TestGameEvents.PlayerTeam(0, 2),
                TestGameEvents.PlayerTeam(1, 3),
                TestGameEvents.PlayerTeam(2, 1)),
            Frame(2, TestGameEvents.RoundFreezeEnd(2, 2, 2)),
            Frame(3, TestGameEvents.RoundOfficiallyEnded(3, 3, 3))
        ];

        ParsedDemo demo = new(frames, [], players, null, "de_anytown", 3, 1f / 64f, "test", "test", "csgo",
            sample.BuildNumber, 0, 0, "valve_demo_2", "", "", sample.Profile);

        AnalysisRun run = RoundFactsTestSupport.Run(demo, """
            ruleset: wl
            for: each_player
            stats:
              won:
                count: round_won
                per: match
              lost:
                count: round_lost
                per: match
              closed:
                count: round_ended
                per: match
            show:
              tables:
                wl:
                  per: player_match
                  columns:
                    - { stat: won, label: Won }
                    - { stat: lost, label: Lost }
                    - { stat: closed, label: Closed }
            """);

        MetricTable table = RoundFactsTestSupport.Table(run, "wl", demo);
        string Row(int slot)
        {
            MetricRow row = table.Rows.Single(r => RoundFactsTestSupport.Dim(r, "player_slot") == slot);
            return $"{RoundFactsTestSupport.Int(row, "Won") ?? 0}/{RoundFactsTestSupport.Int(row, "Lost") ?? 0}/"
                   + $"{RoundFactsTestSupport.Int(row, "Closed") ?? 0}";
        }

        // The round closed for all three; the CT side won it.
        await Assert.That(Row(0)).IsEqualTo("0/1/1");
        await Assert.That(Row(1)).IsEqualTo("1/0/1");
        await Assert.That(Row(2)).IsEqualTo("0/0/1");
    }

    private static DemoFrame Frame(int tick, params GameEvent[] events) => new()
    {
        CommandKind = EDemoCommands.DemPacket,
        FrameNumber = tick,
        ServerTick = tick,
        RawStart = 0,
        RawLength = 1,
        HeaderLength = 1,
        IsCompressed = false,
        MessageList = [.. events.Select(e => (NetMessage)GameEventMessage.ForSynthesizedEvent(e))]
    };

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
