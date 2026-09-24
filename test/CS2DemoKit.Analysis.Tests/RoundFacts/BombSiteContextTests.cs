#region

using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Analysis.Edges;
using CS2DemoKit.Analysis.Output;
using CS2DemoKit.Analysis.RulesetsV2.Model;
using CS2DemoKit.Parser;
using CS2DemoKit.Parser.EntityTracking;
using CS2DemoKit.Parser.GameEvents;
using CS2DemoKit.TestSupport;

#endregion

namespace CS2DemoKit.Analysis.Tests.RoundFacts;

/// <summary>
///     The plant-site round facts (issue #54, piece 6): <c>round.bomb.site</c> is the bombsite letter,
///     <c>round.bomb.plant_place</c> the planter's nav place, <c>round.bomb.site_entity</c> the raw
///     <c>bomb_planted.Site</c>. Written at the plant, held to the round's close, reset at the next
///     freeze end.
/// </summary>
[Category("Integration")]
[NotInParallel]
public class BombSiteContextTests
{
    [Test]
    [Category("Unit")]
    [Arguments("BombsiteA", "A")]
    [Arguments("BombsiteB", "B")]
    [Arguments("bombsiteb", "B")]
    [Arguments("BombsiteC", "")]
    [Arguments("CTSpawn", "")]
    [Arguments("", "")]
    [Arguments(null, "")]
    public async Task SiteLetter_ReadsTheStandardPlaceNames(string? place, string letter) =>
        await Assert.That(BombPlantSiteEdge.SiteLetter(place)).IsEqualTo(letter);

    [Test]
    public async Task Sample_PlantAtA_HoldsToTheClose_AndResetsAtTheNextFreezeEnd()
    {
        ParsedDemo demo = RoundFactsTestSupport.Sample();
        AnalysisRun run = RoundFactsTestSupport.Run(demo, """
            ruleset: site
            for: each_player
            stats:
              site_at_close:
                capture: round.bomb.site
                on: round_ended
                per: round
              place_at_close:
                capture: round.bomb.plant_place
                on: round_ended
                per: round
              entity_at_close:
                capture: round.bomb.site_entity
                on: round_ended
                per: round
              entity_at_freeze_end:
                capture: round.bomb.site_entity
                on: raw.round_freeze_end
                per: round
              site_at_freeze_end:
                capture: round.bomb.site
                on: raw.round_freeze_end
                per: round
            show:
              tables:
                site:
                  per: player_round
                  columns:
                    - { stat: site_at_close, label: Site }
                    - { stat: place_at_close, label: Place }
                    - { stat: entity_at_close, label: Entity }
                    - { stat: entity_at_freeze_end, label: EntityFE }
                    - { stat: site_at_freeze_end, label: SiteFE }
            """);

        MetricTable table = RoundFactsTestSupport.Table(run, "site", demo);
        MetricRow planted = table.Rows.First(r => RoundFactsTestSupport.Dim(r, "round_number") == 1);
        await Assert.That(planted.Values["Site"]).IsEqualTo("A");
        await Assert.That(planted.Values["Place"]).IsEqualTo("BombsiteA");
        await Assert.That(RoundFactsTestSupport.Int(planted, "Entity")).IsEqualTo(248);

        foreach (MetricRow row in table.Rows)
        {
            // Read at each round's freeze end, after the reset: nothing planted yet this round.
            await Assert.That(RoundFactsTestSupport.Int(row, "EntityFE")).IsEqualTo(-1);
            await Assert.That(row.Values["SiteFE"]?.ToString() ?? "").IsEqualTo("");
        }

        MetricRow unplanted = table.Rows.First(r => RoundFactsTestSupport.Dim(r, "round_number") == 2);
        await Assert.That(RoundFactsTestSupport.Int(unplanted, "Entity") ?? -1).IsEqualTo(-1);
    }

    /// <summary>
    ///     On the two issue demos: every plant resolves to A or B and to the planter's matching
    ///     place, the site entities are the raw <c>bomb_planted.Site</c> values in order, and the
    ///     letters agree with the planted C4's own <c>m_nBombSite</c> (0 = A, 1 = B), read raw.
    /// </summary>
    [Test]
    [Category("Corpus")]
    [Arguments("match730_003731893271710924851_1024675027_129.dem", "173,236")]
    [Arguments(RoundFactsCorpusTests.Build10924Dust2, "96,97")]
    public async Task Corpus_EveryPlant_HasItsSite(string demoName, string entities)
    {
        string path = DemoTestHelper.RequireDemo(demoName);
        ParsedDemo demo = DemoTestHelper.GetOrParse(path);
        List<(int Site, int BombSite)> raw = RawPlants(demo);

        IReadOnlyList<RulesetDoc> rules = RoundFactsTestSupport.Load("""
            ruleset: sites
            for: match
            stats:
              entities:
                capture: round.bomb.site_entity
                on: bomb_planted
                keep: list
                per: match
              a_plants:
                count: bomb_planted
                where: 'round.bomb.site == "A" && round.bomb.plant_place == "BombsiteA"'
                per: match
              b_plants:
                count: bomb_planted
                where: 'round.bomb.site == "B" && round.bomb.plant_place == "BombsiteB"'
                per: match
            """);

        foreach ((string label, AnalysisRun run) in RoundFactsCorpusTests.RunBothPaths(path, rules))
        {
            int[] engine = RoundFactsCorpusTests.Ints(run, "sites.entities");
            int a = ((ValueNode<int>)run.Build.GameNodesByRuleId!["sites.a_plants"]).Value;
            int b = ((ValueNode<int>)run.Build.GameNodesByRuleId!["sites.b_plants"]).Value;
            Console.WriteLine($"[sites] {demoName} {label}: entities={string.Join(",", engine)} A={a} B={b}");

            await Assert.That(string.Join(",", engine)).IsEqualTo(string.Join(",", raw.Select(p => p.Site))).Because(label);
            await Assert.That(string.Join(",", engine.Distinct().Order())).IsEqualTo(entities).Because(label);
            await Assert.That(a + b).IsEqualTo(raw.Count).Because($"{label}: every plant has a lettered site");
            await Assert.That(a).IsEqualTo(raw.Count(p => p.BombSite == 0)).Because(label);
            await Assert.That(b).IsEqualTo(raw.Count(p => p.BombSite == 1)).Because(label);
        }
    }

    /// <summary>Each bomb_planted's Site, and the planted C4's m_nBombSite once its frame is applied.</summary>
    private static List<(int Site, int BombSite)> RawPlants(ParsedDemo demo)
    {
        List<(int, int)> plants = [];
        EntityStateLayer layer = new();
        foreach (DemoFrame frame in demo.Frames)
        {
            layer.Apply(frame);
            foreach (NetMessage message in frame.DecodedMessages)
            {
                if (message is not GameEventMessage { DecodedEvent.Payload: BombPlantedEvent planted })
                {
                    continue;
                }

                int bombSite = -1;
                foreach ((int _, EntityState entity) in layer.Tracker.CurrentEntities.AllIndexed())
                {
                    if (entity.ClassName == "CPlantedC4" && entity.TryGet<int>("m_nBombSite") is { } site)
                    {
                        bombSite = site;
                    }
                }

                plants.Add((planted.Site, bombSite));
            }
        }

        return plants;
    }
}
