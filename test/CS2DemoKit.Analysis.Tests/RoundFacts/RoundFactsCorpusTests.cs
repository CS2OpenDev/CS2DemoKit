#region

using CS2DemoKit.Analysis.Abstractions;
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

            // The round-end enrichment reports the server's winners.
            await Assert.That(List(run, "round_ends.closed_winners")).IsEqualTo(match.Winners).Because(label);
            await AssertRoundCloses(run, label);
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
