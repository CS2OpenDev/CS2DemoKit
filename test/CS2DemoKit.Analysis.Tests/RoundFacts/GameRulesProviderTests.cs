#region

using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Analysis.Catalog;
using CS2DemoKit.Analysis.Output;
using CS2DemoKit.Analysis.Plugins;
using CS2DemoKit.Parser;
using CS2DemoKit.TestSupport;

#endregion

namespace CS2DemoKit.Analysis.Tests.RoundFacts;

/// <summary>
///     The six game-rules singletons read off <c>CCSGameRulesProxy.m_pGameRules</c> (issue #54,
///     piece 2): catalogued under <c>match.*</c> with their measured semantics, and readable from a
///     ruleset.
/// </summary>
[Category("Integration")]
[NotInParallel]
public class GameRulesProviderTests
{
    /// <summary>The build-10231 nuke demo the issue measured on (local corpus only).</summary>
    internal const string Build10231Nuke = "match730_003731893271710924851_1024675027_129.dem";

    private static readonly string[] _names =
    [
        "round_win_status", "round_win_reason", "total_rounds_played", "game_phase", "bomb_planted", "round_time"
    ];

    [Test]
    [Category("Unit")]
    public async Task Catalog_ListsTheSixProviders_UnderMatch_WithNotes()
    {
        CatalogRoot catalog = CatalogResource.Load();
        foreach (string name in _names)
        {
            CatalogProvider? provider = catalog.Providers.SingleOrDefault(p => p.Name == "entity.game." + name);
            await Assert.That(provider).IsNotNull().Because(name);
            await Assert.That(provider!.Scope).IsEqualTo("singleton");
            await Assert.That(provider.V2Name).IsEqualTo("match." + name);
            await Assert.That(provider.Note).IsNotNull().Because($"{name} needs its measured semantics on the hover");
        }

        await Assert.That(catalog.Providers.Single(p => p.Name == "entity.game.bomb_planted").V2Type).IsEqualTo("Bool");
        await Assert.That(catalog.Providers.Single(p => p.Name == "entity.game.round_time").V2Type).IsEqualTo("Int");
    }

    [Test]
    [Category("Unit")]
    public async Task DefaultRegistry_RegistersEachProvider_AfterTheFreezePeriodPoll()
    {
        List<string> names = EntityValueProviderRegistry.CreateDefault().All.Select(p => p.ContextName).ToList();
        await Assert.That(names[0]).IsEqualTo("entity.game.freeze_period");
        await Assert.That(names.Skip(1).ToList()).IsEquivalentTo(_names.Select(n => "entity.game." + n).ToList());
    }

    /// <summary>
    ///     On the sample: a live round is configured for 115 seconds (read in a capture and in a
    ///     compute), the planted flag is up on the plant frame and down on most kills, rounds played
    ///     steps at each decision, the phase is the first half throughout, and the status and reason
    ///     read on round_decided are the event's winner and reason. Each value is pinned, so a
    ///     provider reading the wrong field fails here and not only on the corpus.
    /// </summary>
    [Test]
    public async Task Sample_ReadsTheGameRules_FromARuleset()
    {
        ParsedDemo demo = RoundFactsTestSupport.Sample();
        AnalysisRun run = RoundFactsTestSupport.Run(demo, """
            ruleset: game_rules
            for: match
            stats:
              round_time:
                capture: match.round_time
                on: kill
                keep: max
                per: match
              round_time_now:
                compute: "match.round_time"
                per: match
              plants:
                count: bomb_planted
                per: match
              plants_flagged:
                count: bomb_planted
                where: "match.bomb_planted"
                per: match
              kills:
                count: kill
                per: match
              kills_while_planted:
                count: kill
                where: "match.bomb_planted"
                per: match
              played_at_kill:
                capture: match.total_rounds_played
                on: kill
                keep: list
                per: match
              phase_at_kill:
                capture: match.game_phase
                on: kill
                keep: list
                per: match
              status_at_decision:
                capture: match.round_win_status
                on: round_decided
                keep: list
                per: match
              reason_at_decision:
                capture: match.round_win_reason
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
            show:
              tables:
                g:
                  per: match
                  columns:
                    - { stat: round_time, label: RT }
                    - { stat: round_time_now, label: RTC }
                    - { stat: plants, label: P }
                    - { stat: plants_flagged, label: PF }
                    - { stat: played_at_kill, label: Played }
                    - { stat: kills, label: K }
                    - { stat: kills_while_planted, label: KP }
            """);

        MetricRow row = RoundFactsTestSupport.Table(run, "g", demo).Rows.Single();
        await Assert.That(RoundFactsTestSupport.Int(row, "RT")).IsEqualTo(115);
        await Assert.That(RoundFactsTestSupport.Int(row, "RTC")).IsEqualTo(115);
        await Assert.That(RoundFactsTestSupport.Int(row, "P") ?? 0).IsGreaterThan(0);
        await Assert.That(RoundFactsTestSupport.Int(row, "PF")).IsEqualTo(RoundFactsTestSupport.Int(row, "P"));


        // The flag is up for the kills after the plant and down for the rest.
        await Assert.That(RoundFactsTestSupport.Int(row, "KP")).IsEqualTo(2);
        await Assert.That(RoundFactsTestSupport.Int(row, "K")).IsEqualTo(14);

        // Round 1's six kills before the decision read 0, its deciding kill (same frame as the
        // decision) and round 2's first six read 1, and round 2's deciding kill reads 2.
        await Assert.That(RoundFactsCorpusTests.List(run, "game_rules.played_at_kill"))
            .IsEqualTo("0,0,0,0,0,0,1,1,1,1,1,1,1,2");
        await Assert.That(RoundFactsCorpusTests.List(run, "game_rules.phase_at_kill"))
            .IsEqualTo(string.Join(",", Enumerable.Repeat(2, 14)));

        await Assert.That(RoundFactsCorpusTests.List(run, "game_rules.winners")).IsEqualTo("2,3");
        await Assert.That(RoundFactsCorpusTests.List(run, "game_rules.status_at_decision")).IsEqualTo("2,3");
        await Assert.That(RoundFactsCorpusTests.List(run, "game_rules.reasons")).IsEqualTo("9,8");
        await Assert.That(RoundFactsCorpusTests.List(run, "game_rules.reason_at_decision")).IsEqualTo("9,8");
    }

    /// <summary>
    ///     Across a full matchmaking match the phase goes 2 (first half), 4 (the halftime break),
    ///     3 (second half), 5 (over), and nothing else. Read frame by frame straight off the
    ///     provider, since the break has no event on it to capture on.
    /// </summary>
    [Test]
    [Category("Corpus")]
    public async Task Corpus_GamePhase_RunsFirstHalf_Break_SecondHalf_Over()
    {
        string path = DemoTestHelper.RequireDemo(Build10231Nuke);
        ParsedDemo demo = DemoTestHelper.GetOrParse(path);
        IEntityValueProvider phase = BuiltinProviderSpecs.CreateGameRulesProviders()
            .Single(p => p.ContextName == "entity.game.game_phase");

        EntityStateLayer layer = new() { StoreUnlensedFields = false };
        List<int> sequence = [];
        foreach (DemoFrame frame in demo.Frames)
        {
            layer.Apply(frame);
            if (phase.Read(layer) is int value && (sequence.Count == 0 || sequence[^1] != value))
            {
                sequence.Add(value);
            }
        }

        await Assert.That(string.Join(",", sequence)).IsEqualTo("2,4,3,5");
    }
}
