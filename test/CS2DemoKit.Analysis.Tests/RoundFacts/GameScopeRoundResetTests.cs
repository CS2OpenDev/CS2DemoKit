#region

using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Analysis.Output;
using CS2DemoKit.Parser;

#endregion

namespace CS2DemoKit.Analysis.Tests.RoundFacts;

/// <summary>
///     The static nodes a <c>for: match</c> ruleset builds get what a per-player node gets from
///     materialization: a round-scoped one resets at each round boundary, and every one returns to
///     its build-time value on a match restart. A match-only build also tracks players, so its
///     enrichments see live teams.
/// </summary>
[Category("Integration")]
[NotInParallel]
public class GameScopeRoundResetTests
{
    private const string PerPlayerKills = """
        ruleset: pp_kills
        for: each_player
        stats:
          round_kills:
            count: kill
            per: round
          kills:
            count: kill
            per: match
        show:
          tables:
            ppr:
              per: player_round
              columns:
                - { stat: round_kills, label: K }
            ppm:
              per: player_match
              columns:
                - { stat: kills, label: K }
        """;

    private const string MatchKills = """
        ruleset: match_kills
        for: match
        stats:
          round_kills:
            count: kill
            per: round
          kills:
            count: kill
            per: match
        show:
          tables:
            m:
              per: match
              columns:
                - { stat: round_kills, label: RoundK }
                - { stat: kills, label: K }
        """;

    /// <summary>
    ///     At the end of the demo a <c>per: round</c> match count holds the last round's kills, the
    ///     sum over players of their last-round count; before the fix it held the match total. The
    ///     <c>per: match</c> count equals the per-player sum across the sample's second
    ///     begin_new_match, which discards the warmup's kills from both.
    /// </summary>
    [Test]
    public async Task MatchScope_PerRound_ResetsEachRound_AndPerMatch_RestartsWithTheMatch()
    {
        ParsedDemo demo = RoundFactsTestSupport.Sample();
        AnalysisRun run = RoundFactsTestSupport.Run(demo, MatchKills, PerPlayerKills);

        MetricTable perRound = RoundFactsTestSupport.Table(run, "ppr", demo);
        int lastRound = perRound.Rows.Max(r => RoundFactsTestSupport.Dim(r, "round_number"));
        int lastRoundKills = perRound.Rows.Where(r => RoundFactsTestSupport.Dim(r, "round_number") == lastRound)
            .Sum(r => RoundFactsTestSupport.Int(r, "K") ?? 0);
        int matchKills = RoundFactsTestSupport.Table(run, "ppm", demo).Rows.Sum(r => RoundFactsTestSupport.Int(r, "K") ?? 0);

        MetricRow row = RoundFactsTestSupport.Table(run, "m", demo).Rows.Single();
        Console.WriteLine($"[reset] RoundK={RoundFactsTestSupport.Int(row, "RoundK")} lastRound={lastRoundKills} K={RoundFactsTestSupport.Int(row, "K")} total={matchKills}");
        await Assert.That(matchKills).IsGreaterThan(lastRoundKills);
        await Assert.That(RoundFactsTestSupport.Int(row, "RoundK")).IsEqualTo(lastRoundKills);
        await Assert.That(RoundFactsTestSupport.Int(row, "K")).IsEqualTo(matchKills);
    }

    /// <summary>
    ///     A match-only build has no per-player template, and used to register no player context:
    ///     the round-end derivation saw nobody alive and named the same winner every round. It now
    ///     tracks players, and reads the winners a per-player build reads.
    /// </summary>
    [Test]
    public async Task MatchOnlyBuild_TracksPlayers_AndReadsTheRealWinners()
    {
        ParsedDemo demo = RoundFactsTestSupport.Sample();
        AnalysisRun matchOnly = RoundFactsTestSupport.Run(demo, """
            ruleset: winners_match
            for: match
            stats:
              sides:
                capture: enrich.round.winner_side
                on: round_won
                keep: list
                per: match
              enemy_kills:
                count: kill
                match: { enemy: true }
                per: match
            show:
              tables:
                w:
                  per: match
                  columns:
                    - { stat: sides, label: Sides }
                    - { stat: enemy_kills, label: EK }
            """);
        AnalysisRun perPlayer = RoundFactsTestSupport.Run(demo, """
            ruleset: winners_pp
            for: each_player
            stats:
              sides:
                capture: enrich.round.winner_side
                on: round_won
                match: { actor: any }
                keep: list
                per: match
              enemy_kills:
                count: kill
                match: { enemy: true }
                per: match
            show:
              tables:
                wp:
                  per: player_match
                  columns:
                    - { stat: sides, label: Sides }
                    - { stat: enemy_kills, label: EK }
            """);

        MetricRow match = RoundFactsTestSupport.Table(matchOnly, "w", demo).Rows.Single();
        List<MetricRow> players = RoundFactsTestSupport.Table(perPlayer, "wp", demo).Rows.ToList();
        string longest = players.Select(r => r.Values["Sides"]?.ToString() ?? "").OrderByDescending(s => s.Length).First();

        await Assert.That(matchOnly.Build.PlayerContextIndex).IsNotNull();
        await Assert.That(match.Values["Sides"]?.ToString()).IsEqualTo(longest);
        await Assert.That(RoundFactsTestSupport.Int(match, "EK"))
            .IsEqualTo(players.Sum(r => RoundFactsTestSupport.Int(r, "EK") ?? 0));
    }

    /// <summary>
    ///     A <c>per: round</c> cadence count resets at each freeze end, in both scopes: every player
    ///     holds the same count for a round, the per-round counts add up to the <c>per: match</c>
    ///     count, and the match-scope round count at the end is the last round's.
    /// </summary>
    [Test]
    public async Task PerRound_CadenceCounts_ResetEachRound()
    {
        const string Players = """
            ruleset: cadence_pp
            for: each_player
            cadences:
              second: { ticks: 64, anchor: round_freeze_end, until: round_ended }
            stats:
              round_samples:
                count: cadence.second
                per: round
              samples:
                count: cadence.second
                per: match
            show:
              tables:
                cpr:
                  per: player_round
                  columns:
                    - { stat: round_samples, label: S }
                cpm:
                  per: player_match
                  columns:
                    - { stat: samples, label: S }
            """;
        const string Match = """
            ruleset: cadence_m
            for: match
            cadences:
              second: { ticks: 64, anchor: round_freeze_end, until: round_ended }
            stats:
              round_samples:
                count: cadence.second
                per: round
              samples:
                count: cadence.second
                per: match
            show:
              tables:
                cm:
                  per: match
                  columns:
                    - { stat: round_samples, label: RoundS }
                    - { stat: samples, label: S }
            """;

        ParsedDemo demo = RoundFactsTestSupport.Sample();
        AnalysisRun run = RoundFactsTestSupport.Run(demo, Players, Match);

        List<IGrouping<int, MetricRow>> rounds = RoundFactsTestSupport.Table(run, "cpr", demo).Rows
            .GroupBy(r => RoundFactsTestSupport.Dim(r, "round_number")).OrderBy(g => g.Key).ToList();
        int[] perRound = rounds.Select(g => RoundFactsTestSupport.Int(g.First(), "S") ?? 0).ToArray();
        foreach (IGrouping<int, MetricRow> round in rounds)
        {
            await Assert.That(round.Select(r => RoundFactsTestSupport.Int(r, "S") ?? 0).Distinct().Count())
                .IsEqualTo(1).Because($"round {round.Key}");
        }

        int matchSamples = RoundFactsTestSupport.Table(run, "cpm", demo).Rows
            .Select(r => RoundFactsTestSupport.Int(r, "S") ?? 0).Max();
        MetricRow match = RoundFactsTestSupport.Table(run, "cm", demo).Rows.Single();

        await Assert.That(perRound.Count(n => n > 0)).IsGreaterThan(1);
        await Assert.That(perRound.Sum()).IsEqualTo(matchSamples);
        await Assert.That(RoundFactsTestSupport.Int(match, "S")).IsEqualTo(matchSamples);
        await Assert.That(RoundFactsTestSupport.Int(match, "RoundS")).IsEqualTo(perRound[^1]);
    }
}
