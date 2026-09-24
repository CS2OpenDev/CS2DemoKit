#region

using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Analysis.Output;
using CS2DemoKit.Analysis.RulesetsV2.Model;
using CS2DemoKit.Parser;
using CS2DemoKit.TestSupport;

#endregion

namespace CS2DemoKit.Analysis.Tests.RoundFacts;

/// <summary>
///     Issue #54's round facts on the two matchmaking demos it was measured on, each on the forward
///     path and on the retained one: the build-10231 nuke demo and a build-10924 dust2 demo. Both
///     live in the local corpus only (point <c>CS2DEMOKIT_CORPUS_DIR</c> at the Steam replays
///     folder), and every test skips cleanly without them.
/// </summary>
[Category("Corpus")]
[NotInParallel]
public class RoundFactsCorpusTests
{
    /// <summary>The build-10924 dust2 demo standing in for the issue's build-10896 one.</summary>
    internal const string Build10924Dust2 = "match730_003844470418295488702_1553410689_408.dem";

    /// <summary>
    ///     The known sequences: winner side and reason for every round, as the game rules state them.
    /// </summary>
    public static IEnumerable<(string Demo, string Winners, string? Reasons)> KnownMatches()
    {
        yield return (GameRulesProviderTests.Build10231Nuke,
            "3,3,3,3,3,2,2,2,3,3,2,3,2,3,3,3,3,3,3,2,3,3,3",
            "8,8,8,8,8,9,9,9,8,8,9,8,9,7,7,8,8,7,7,9,8,8,8");
        yield return (Build10924Dust2,
            "3,3,3,2,2,2,3,2,2,2,3,3,2,2,2,3,3,3,3,2,3,3,3",
            "7,8,8,9,9,9,8,9,9,9,8,7,9,9,9,8,8,8,8,9,8,8,8");
    }

    internal const string RoundEnds = """
        ruleset: round_ends
        for: match
        stats:
          decided_at:
            capture: event.frame_tick
            on: round_decided
            keep: list
            per: match
          winners:
            capture: event.Winner
            on: round_decided
            keep: list
            per: match
          reasons:
            capture: event.Reason
            on: round_decided
            keep: list
            per: match
          rounds_played:
            capture: event.RoundsPlayed
            on: round_decided
            keep: list
            per: match
          closed_at:
            capture: event.frame_tick
            on: raw.round_officially_ended
            keep: list
            per: match
          match_closed_at:
            capture: event.frame_tick
            on: raw.cs_win_panel_match
            keep: list
            per: match
          closed_winners:
            capture: enrich.round.winner_side
            on: round_ended
            match: { has_winner: true }
            keep: list
            per: match
        """;

    [Test]
    [MethodDataSource(nameof(KnownMatches))]
    public async Task RoundDecided_MatchesTheServersVerdict_OnBothPaths((string Demo, string Winners, string? Reasons) match)
    {
        string path = DemoTestHelper.RequireDemo(match.Demo);
        IReadOnlyList<RulesetDoc> rules = RoundFactsTestSupport.Load(RoundEnds);

        foreach ((string label, AnalysisRun run) in RunBothPaths(path, rules))
        {
            string winners = List(run, "round_ends.winners");
            await Assert.That(winners).IsEqualTo(match.Winners).Because(label);
            if (match.Reasons is { } reasons)
            {
                await Assert.That(List(run, "round_ends.reasons")).IsEqualTo(reasons).Because(label);
            }

            // Rounds played counts the decisions, from 1.
            int decisions = match.Winners.Split(',').Length;
            await Assert.That(List(run, "round_ends.rounds_played"))
                .IsEqualTo(string.Join(",", Enumerable.Range(1, decisions))).Because(label);

            // The round-end enrichment reports the server's winners.
            await Assert.That(List(run, "round_ends.closed_winners")).IsEqualTo(match.Winners).Because(label);
            await AssertRoundCloses(run, label);
        }
    }

    /// <summary>
    ///     Matchmaking demos whose server decided a round with no freeze end before it: a surrender
    ///     vote passing in freeze time (reason 18, the counter-terrorists surrendered, in round 2 of
    ///     the first; 17 in round 4 of the second), and a round decided in freeze time mid-match (round
    ///     23 of 24 in the third). Each decision gets its own round row.
    /// </summary>
    public static IEnumerable<(string Demo, int Round, int Winner, int Rounds)> FreezeTimeDecisions()
    {
        yield return ("match730_003773181762989981708_1539622424_408.dem", 2, 2, 2);
        yield return ("match730_003773238437230936211_1318744758_392.dem", 4, 3, 4);
        yield return ("match730_003809070872640094459_0827290994_117.dem", 23, 3, 24);
    }

    private const string SideRounds = """
        ruleset: side_rounds
        for: each_team
        stats:
          won:
            count: round_won
            per: round
          lost:
            count: round_lost
            per: round
          decided:
            count: round_decided
            per: round
        show:
          tables:
            side_rounds:
              per: team_round
              columns:
                - { stat: won, label: W }
                - { stat: lost, label: L }
                - { stat: decided, label: D }
        """;

    /// <summary>
    ///     A round the server decided without a freeze end is opened at its decision: every round's
    ///     two rows hold one decision, one win and one loss, the round numbers run 1..rounds played,
    ///     and the freeze-time round is won by the side the server named. Before, the decision and its
    ///     close folded into the previous round's rows (won 2 for one side, lost 2 for the other) and
    ///     every later round was numbered one short.
    /// </summary>
    [Test]
    [MethodDataSource(nameof(FreezeTimeDecisions))]
    public async Task ARoundDecidedInFreezeTime_GetsItsOwnRows((string Demo, int Round, int Winner, int Rounds) match)
    {
        string path = DemoTestHelper.RequireDemo(match.Demo);
        IReadOnlyList<RulesetDoc> rules = RoundFactsTestSupport.Load(RoundEnds, SideRounds);

        foreach ((string label, AnalysisRun run) in RunBothPaths(path, rules))
        {
            await Assert.That(List(run, "round_ends.rounds_played"))
                .IsEqualTo(string.Join(",", Enumerable.Range(1, match.Rounds))).Because(label);

            MetricTable table = run.ProjectConfiguredOutputs().Single(t => t.Name == "side_rounds");
            List<IGrouping<int, MetricRow>> rounds = table.Rows
                .GroupBy(r => RoundFactsTestSupport.Dim(r, "round_number")).OrderBy(g => g.Key).ToList();
            await Assert.That(string.Join(",", rounds.Select(g => g.Key)))
                .IsEqualTo(string.Join(",", Enumerable.Range(1, match.Rounds))).Because(label);

            foreach (IGrouping<int, MetricRow> round in rounds)
            {
                string because = $"{label}: round {round.Key}";
                await Assert.That(round.Count()).IsEqualTo(2).Because(because);
                foreach (MetricRow side in round)
                {
                    await Assert.That(RoundFactsTestSupport.Int(side, "D")).IsEqualTo(1).Because(because);
                    await Assert.That((RoundFactsTestSupport.Int(side, "W") ?? 0) + (RoundFactsTestSupport.Int(side, "L") ?? 0))
                        .IsEqualTo(1).Because(because);
                }
            }

            MetricRow winner = rounds.Single(g => g.Key == match.Round).Single(r => RoundFactsTestSupport.Int(r, "W") == 1);
            await Assert.That(RoundFactsTestSupport.Dim(winner, "side")).IsEqualTo(match.Winner).Because(label);
        }
    }

    /// <summary>The close-follows-decision invariant over whatever demos the corpus holds.</summary>
    [Test]
    [MethodDataSource(typeof(RulesOutputGoldenTests), nameof(RulesOutputGoldenTests.CorpusDemos))]
    public async Task EveryCorpusDemo_ClosesEachDecidedRoundOnce(string path)
    {
        ParsedDemo demo = DemoParser.Parse(File.ReadAllBytes(path).AsMemory());
        AnalysisRun run = DemoAnalysis.Run(demo, RoundFactsTestSupport.Load(RoundEnds),
            new AnalysisOptions { CaptureSnapshots = false });
        await AssertRoundCloses(run, Path.GetFileName(path));
    }

    /// <summary>
    ///     Every decided round closes once: round_officially_ended 448 ticks after the decision,
    ///     except the last round of a half, whose close waits out the 544-tick break instead (once in
    ///     regulation, and again at each overtime half); the final round has no
    ///     round_officially_ended and is closed by cs_win_panel_match.
    /// </summary>
    private static async Task AssertRoundCloses(AnalysisRun run, string label)
    {
        int[] decided = Ints(run, "round_ends.decided_at");
        int[] closed = Ints(run, "round_ends.closed_at");
        int[] matchClosed = Ints(run, "round_ends.match_closed_at");

        await Assert.That(decided.Length).IsGreaterThan(0).Because(label);
        await Assert.That(closed.Length + matchClosed.Length).IsEqualTo(decided.Length).Because(label);

        int breaks = 0;
        for (int i = 0; i < closed.Length; i++)
        {
            int gap = closed[i] - decided[i];
            if (gap == 544)
            {
                breaks++;
                continue;
            }

            await Assert.That(gap).IsEqualTo(448).Because($"{label}: round {i + 1} closed {gap} ticks after its decision");
        }

        // A 544 is a half's last round: at most one per 12 regulation rounds and one per overtime half.
        await Assert.That(breaks).IsLessThanOrEqualTo(Math.Max(1, (decided.Length - 21) / 3 + 1)).Because(label);
        if (matchClosed.Length == 1)
        {
            await Assert.That(matchClosed[0]).IsGreaterThan(decided[^1]).Because(label);
        }
    }

    /// <summary>The retained (snapshot) run and the forward run over the file.</summary>
    internal static IEnumerable<(string Label, AnalysisRun Run)> RunBothPaths(string path, IReadOnlyList<RulesetDoc> rules)
    {
        ParsedDemo demo = DemoParser.Parse(File.ReadAllBytes(path).AsMemory());
        yield return ("retained", DemoAnalysis.Run(demo, rules));
        yield return ("forward", DemoAnalysis.Run(path, rules));
    }

    /// <summary>A list-capture game node's value, comma-joined.</summary>
    internal static string List(AnalysisRun run, string key) =>
        run.Build.GameNodesByRuleId![key] is ValueNode<IReadOnlyList<int>> node && node.Value is { } values
            ? string.Join(",", values)
            : "";

    internal static int[] Ints(AnalysisRun run, string key) =>
        List(run, key).Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToArray();
}
