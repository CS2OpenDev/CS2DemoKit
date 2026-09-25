#region

using CS2DemoKit.Analysis.Building;
using CS2DemoKit.Analysis.Catalog;
using CS2DemoKit.Analysis.Config;
using CS2DemoKit.Analysis.Graphs;
using CS2DemoKit.Analysis.Plugins;
using CS2DemoKit.Analysis.Registry;
using CS2DemoKit.Analysis.RulesetsV2.Model;
using CS2DemoKit.Analysis.RulesetsV2.Resolve;
using CS2DemoKit.Analysis.Yaml;

#endregion

namespace CS2DemoKit.Analysis.Tests.RulesV2;

/// <summary>
///     Gates the demo-less authoring graph (<see cref="AuthoringGraph" />) the Workbench renders while
///     editing: it materializes per-player templates, anchors on the ruleset's declared outputs, and keeps
///     only those anchors plus their upstream inputs — so a bare stat is a couple of nodes, not the whole
///     engine's shared scaffolding.
/// </summary>
public class AuthoringGraphTests
{
    [Test]
    public async Task BareKillStat_ReducesToOutputPlusRoot_FlaggedPerPlayer()
    {
        AuthoringGraph.AuthoringGraphModel model = BuildAuthoring(
            "ruleset: probe\nfor: each_player\nstats:\n  kills:\n    count: kill\n    per: match\n" +
            "show:\n  scoreboard:\n    - { stat: kills, label: Kills, group: game }\n");

        await Assert.That(model.Nodes.Count).IsLessThanOrEqualTo(6)
            .Because("a bare kill stat is its output + upstream inputs, not the full engine scaffolding");
        await Assert.That(model.Nodes.Any(n => n.IsRoot)).IsTrue().Because("the root anchors the graph");
        await Assert.That(model.Nodes.Any(n => n.Name == "kills")).IsTrue()
            .Because("the ruleset's declared stat node is present (materialized from the per-player template)");
        await Assert.That(model.Nodes.Any(n => n is { Name: "kills", IsPerPlayer: true })).IsTrue()
            .Because("the kill stat materializes per player, so it is flagged for the view to identify");
    }

    [Test]
    public async Task NoRulesets_ProducesEmptyGraph()
    {
        // No declared outputs → nothing anchors → the shared scaffolding is entirely filtered away.
        RuleChainBuilder builder = NewBuilder();
        BuildResult build = builder.Build([]);
        AuthoringGraph.AuthoringGraphModel model = AuthoringGraph.Build(build, []);

        await Assert.That(model.Nodes.Count).IsEqualTo(0)
            .Because("with no ruleset outputs to anchor on, no scaffolding is kept");
    }

    [Test]
    public async Task HighlightRuleset_SurfacesChainAndCountNodes()
    {
        // A highlight ('when: <stat> >= N') builds a conjunction chain + rising-edge count downstream of
        // the stat. The graph must surface BOTH — a highlight is not a scoreboard column, so anchoring on
        // columns alone would drop it. This is the discriminating case the bare-stat test can't cover.
        AuthoringGraph.AuthoringGraphModel bare = BuildAuthoring(
            "ruleset: bare\nfor: each_player\nstats:\n  kills:\n    count: kill\n    per: round\n");
        AuthoringGraph.AuthoringGraphModel rich = BuildAuthoring(
            "ruleset: rich\nfor: each_player\nstats:\n  kills:\n    count: kill\n    per: round\n" +
            "highlights:\n  multi:\n    when: kills >= 2\n    per: match\n    title: \"multi\"\n");

        await Assert.That(rich.Nodes.Count).IsGreaterThan(bare.Nodes.Count)
            .Because("the highlight adds its chain + count nodes — graph complexity tracks the rules");
        await Assert.That(rich.Nodes.Any(n => n.Name.Contains("_chain_", StringComparison.Ordinal))).IsTrue()
            .Because("the highlight's conjunction chain node is surfaced (not just the scoreboard columns)");
        await Assert.That(rich.Nodes.Any(n => n.Name.Contains("multi", StringComparison.Ordinal) && n.IsPerPlayer)).IsTrue()
            .Because("the highlight's per-player count node is surfaced and flagged per-player");
    }

    [Test]
    public async Task TallyTarget_ReachesItsSourceStat_ThroughARead()
    {
        // A tally fires from the root at round end and reads its stat; the read is what connects the
        // bucket to the stat, and it is drawn as its own edge.
        AuthoringGraph.AuthoringGraphModel model = BuildAuthoring(
            """
            ruleset: t
            for: each_player
            stats:
              kills:
                count: kill
                per: round
              multi:
                tally: kills
                thresholds:
                  - { min: 2, target: rounds_2k }
                per: match
            """);

        int kills = IndexOf(model, "kills");
        int bucket = IndexOf(model, "rounds_2k");
        await Assert.That(model.Edges.Any(e => e.Source == kills && e.Destination == bucket && e.IsRead)).IsTrue()
            .Because("the bucket's tally reads the kills stat");
        await Assert.That(model.Edges.Any(e => model.Nodes[e.Source].IsRoot && e.Destination == bucket && !e.IsRead)).IsTrue()
            .Because("the tally fires from the root");
    }

    [Test]
    public async Task HighlightInputs_CarryTheHighlightChain()
    {
        AuthoringGraph.AuthoringGraphModel model = BuildAuthoring(
            """
            ruleset: rich
            for: each_player
            stats:
              kills:
                count: kill
                per: round
            highlights:
              multi:
                when: kills >= 2
                per: match
                title: "multi"
            """);

        foreach (string name in (string[])["kills", "_chain_multi", "multi.count"])
        {
            await Assert.That(model.Nodes[IndexOf(model, name)].ChainIds).Contains("_chain_multi");
        }

        await Assert.That(model.Nodes.Single(n => n.IsRoot).ChainIds).IsEmpty();
    }

    private static int IndexOf(AuthoringGraph.AuthoringGraphModel model, string name)
    {
        for (int i = 0; i < model.Nodes.Count; i++)
        {
            if (model.Nodes[i].Name == name)
            {
                return i;
            }
        }

        throw new InvalidOperationException($"no node '{name}' in [{string.Join(", ", model.Nodes.Select(n => n.Name))}]");
    }

    private static AuthoringGraph.AuthoringGraphModel BuildAuthoring(string yaml)
    {
        RulesetDoc doc = RulesetDocumentLoader.Load(yaml, "probe.rules.yaml").Doc
                         ?? throw new InvalidOperationException("test ruleset failed to map");
        CatalogScopeAdapter adapter = CatalogScopeAdapter.From(CatalogResource.Load());
        RulesetResolveResult resolved = CheckedRulesetDraft.Load(doc, adapter).Build(64.0, "Cs2GotvProfile");
        CheckedRuleset rs = resolved.Ruleset
                            ?? throw new InvalidOperationException(
                                "test ruleset failed to resolve: " + string.Join("; ", resolved.Diagnostics));

        RuleChainBuilder builder = NewBuilder();
        BuildResult build = builder.Build([rs]);
        return AuthoringGraph.Build(build, [rs]);
    }

    private static RuleChainBuilder NewBuilder() => new(
        EventRegistry.Build(),
        null,
        entityProviders: EntityValueProviderRegistry.CreateDefault(),
        perPlayerEntityProviders: PerPlayerEntityValueProviderRegistry.CreateDefault());
}
