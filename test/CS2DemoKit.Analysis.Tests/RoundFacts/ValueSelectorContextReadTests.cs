#region

using CS2DemoKit.Analysis.Output;
using CS2DemoKit.Parser;

#endregion

namespace CS2DemoKit.Analysis.Tests.RoundFacts;

/// <summary>
///     A context or B6 aggregate read as a VALUE (a capture) builds and reads what the same read in
///     <c>where:</c> reads, and a list capture is ordered after the writer of the enrichment it
///     appends.
/// </summary>
[Category("Integration")]
[NotInParallel]
public class ValueSelectorContextReadTests
{
    /// <summary>
    ///     <c>capture: round.number</c> used to fail at build with "Unknown identifier: round"; the
    ///     same read in a <c>where:</c> worked. A capture on a kill reads the row's own round.
    /// </summary>
    [Test]
    public async Task Capture_RoundNumber_ReadsTheRowsRound()
    {
        ParsedDemo demo = RoundFactsTestSupport.Sample();
        AnalysisRun run = RoundFactsTestSupport.Run(demo, """
            ruleset: rn
            for: each_player
            stats:
              round_at_kill:
                capture: round.number
                on: kill
                per: round
            show:
              tables:
                t:
                  per: player_round
                  columns:
                    - { stat: round_at_kill, label: R }
            """);

        MetricTable table = RoundFactsTestSupport.Table(run, "t", demo);
        List<MetricRow> captured = table.Rows.Where(r => RoundFactsTestSupport.Int(r, "R") is > 0).ToList();
        await Assert.That(captured.Count).IsGreaterThan(0);
        foreach (MetricRow row in captured)
        {
            await Assert.That(RoundFactsTestSupport.Int(row, "R")).IsEqualTo(RoundFactsTestSupport.Dim(row, "round_number"));
        }
    }

    /// <summary>
    ///     <c>capture: round.team.equipment</c> reads the value a <c>where:</c> over it compares.
    ///     Equipment is fixed for the round, so the kills a where-gate lets through are all or none of
    ///     the round's kills, decided by the captured value.
    /// </summary>
    [Test]
    public async Task Capture_TeamEquipment_AgreesWithTheWhereGate()
    {
        ParsedDemo demo = RoundFactsTestSupport.Sample();
        AnalysisRun run = RoundFactsTestSupport.Run(demo, """
            ruleset: eq
            for: each_player
            stats:
              kills:
                count: kill
                per: round
              eq_at_kill:
                capture: round.team.equipment
                on: kill
                per: round
              rich_kills:
                count: kill
                where: "round.team.equipment > 10000"
                per: round
            show:
              tables:
                t:
                  per: player_round
                  columns:
                    - { stat: kills, label: K }
                    - { stat: eq_at_kill, label: EQ }
                    - { stat: rich_kills, label: RK }
            """);

        MetricTable table = RoundFactsTestSupport.Table(run, "t", demo);
        List<MetricRow> withKills = table.Rows.Where(r => RoundFactsTestSupport.Int(r, "K") is > 0).ToList();
        await Assert.That(withKills.Count).IsGreaterThan(0);
        await Assert.That(withKills.Any(r => RoundFactsTestSupport.Int(r, "EQ") is > 0)).IsTrue();
        foreach (MetricRow row in withKills)
        {
            int expected = RoundFactsTestSupport.Int(row, "EQ") > 10000 ? RoundFactsTestSupport.Int(row, "K")!.Value : 0;
            await Assert.That(RoundFactsTestSupport.Int(row, "RK")).IsEqualTo(expected);
        }
    }

    /// <summary>
    ///     A <c>for: match</c> list capture of an enrichment must run after the enrichment edge on the
    ///     same event. Unordered, it appended a stale value: on the probe it read the same side in
    ///     every round. The per-player twin, whose edges happen to sort late, is the reference.
    /// </summary>
    [Test]
    public async Task MatchScope_ListCapture_OfEnrichment_MatchesThePerPlayerSequence()
    {
        ParsedDemo demo = RoundFactsTestSupport.Sample();
        AnalysisRun run = RoundFactsTestSupport.Run(demo, """
            ruleset: winners_match
            for: match
            stats:
              sides:
                capture: enrich.round.winner_side
                on: round_won
                keep: list
                per: match
            show:
              tables:
                w:
                  per: match
                  columns:
                    - { stat: sides, label: Sides }
            """, """
            ruleset: winners_pp
            for: each_player
            stats:
              sides:
                capture: enrich.round.winner_side
                on: round_won
                match: { actor: any }
                keep: list
                per: match
            show:
              tables:
                wp:
                  per: player_match
                  columns:
                    - { stat: sides, label: Sides }
            """);

        string? match = RoundFactsTestSupport.Table(run, "w", demo).Rows.Single().Values["Sides"]?.ToString();
        string longest = RoundFactsTestSupport.Table(run, "wp", demo).Rows
            .Select(r => r.Values["Sides"]?.ToString() ?? "")
            .OrderByDescending(s => s.Length).First();
        Console.WriteLine($"[winners] match={match} per-player={longest}");
        await Assert.That(longest.Length).IsGreaterThan(0);
        await Assert.That(match).IsEqualTo(longest);
    }
}
