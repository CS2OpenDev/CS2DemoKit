#region

using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Analysis.Building;
using CS2DemoKit.Analysis.Catalog;
using CS2DemoKit.Analysis.Graphs;
using CS2DemoKit.Analysis.Nodes;
using CS2DemoKit.Analysis.Profiles;
using CS2DemoKit.Analysis.Registry;
using CS2DemoKit.Analysis.RulesetsV2.Model;
using CS2DemoKit.Analysis.RulesetsV2.Resolve;
using CS2DemoKit.Analysis.Yaml;
using CS2DemoKit.Parser;
using CS2DemoKit.TestSupport;

#endregion

namespace CS2DemoKit.Analysis.Tests;

/// <summary>
///     <see cref="RuleGraph" />, the graph a consumer draws: the game scope joined with every
///     materialised player, nodes keyed stably and carrying their template position, provenance and
///     highlight chains, edges already resolved to view nodes, and the per-player copies foldable into
///     one. The demo tests run the shipped rulesets on the committed sample.
/// </summary>
[Category("Integration")]
[NotInParallel]
public class RuleGraphTests
{
    private static ParsedDemo Sample() =>
        DemoTestHelper.GetOrParse(DemoTestHelper.RequireDemo(DemoTestHelper.SampleDemoFileName));

    [Test]
    public async Task FromRun_OnSample_JoinsEveryPlayer()
    {
        ParsedDemo demo = Sample();
        AnalysisRun run = DemoAnalysis.Run(demo, RuleGraphFixtures.Shipped());
        RuleGraph view = RuleGraph.FromRun(run);

        int playerNodes = run.MaterializedPlayers.Sum(p => p.Nodes.Count);
        await Assert.That(run.MaterializedPlayers.Count).IsGreaterThan(1);
        await Assert.That(view.Nodes.Count).IsEqualTo(run.Build.Nodes.Count + run.Build.ExternalNodes.Count + playerNodes);
        await Assert.That(view.Diagnostics).IsEmpty();
        await Assert.That(view.Edges.Any(e => e.Descriptor.Kind == GraphEdgeKind.Undescribed)).IsFalse();
        await Assert.That(view.Nodes.Select(n => n.Key).Distinct().Count()).IsEqualTo(view.Nodes.Count);

        List<RuleGraphNode> perPlayer = [.. view.Nodes.Where(n => n.Scope == RuleGraphScope.Player)];
        await Assert.That(perPlayer.Count).IsEqualTo(playerNodes);
        await Assert.That(perPlayer.All(n => n.PlayerSlot is not null && n.Template is not null)).IsTrue();

        HashSet<RuleGraphNode> members = new(view.Nodes, ReferenceEqualityComparer.Instance);
        await Assert.That(view.Edges.All(e => members.Contains(e.Source) && members.Contains(e.Destination))).IsTrue();

        // The view grows by exactly one template's worth per player.
        int descriptors = run.Build.Edges.Count + run.MaterializedPlayers.Sum(p => p.EdgeDescriptors.Count);
        await Assert.That(view.Edges.Count).IsEqualTo(descriptors);
        await Assert.That(run.MaterializedPlayers.Select(p => p.EdgeDescriptors.Count).Distinct().Count()).IsEqualTo(1);
    }

    /// <summary>
    ///     A template position names the same node for every player, which is what lets ten copies fold
    ///     into one: the factory never reads the slot into what it builds.
    /// </summary>
    [Test]
    public async Task TemplateKeys_AreSlotIndependent()
    {
        AnalysisRun run = DemoAnalysis.Run(Sample(), RuleGraphFixtures.Shipped());
        List<string> reference = Shape(run.MaterializedPlayers[0]);
        foreach (PerPlayerNodeTemplate.MaterializedPlayer player in run.MaterializedPlayers.Skip(1))
        {
            await Assert.That(Shape(player)).IsEquivalentTo(reference);
        }

        BuildResult matrix = RuleGraphFixtures.Build(RuleGraphFixtures.Matrix(), new Cs2GotvProfile());
        List<string> matrixReference = Shape(matrix.Graph.PerPlayerTemplates[0].Materialize(0, 0, "a"));
        foreach (int slot in (int[])[3, 9])
        {
            await Assert.That(Shape(matrix.Graph.PerPlayerTemplates[0].Materialize(slot, slot, "b")))
                .IsEquivalentTo(matrixReference);
        }
    }

    [Test]
    public async Task CollapsePlayers_CollapsesEveryCopyToOne()
    {
        AnalysisRun run = DemoAnalysis.Run(Sample(), RuleGraphFixtures.Shipped());
        RuleGraph full = RuleGraph.FromRun(run);
        RuleGraph collapsed = full.CollapsePlayers();
        int players = run.MaterializedPlayers.Count;
        PerPlayerNodeTemplate.MaterializedPlayer one = run.MaterializedPlayers[0];

        List<RuleGraphNode> perPlayer = [.. collapsed.Nodes.Where(n => n.Scope == RuleGraphScope.Player)];
        await Assert.That(perPlayer.Count).IsEqualTo(one.Nodes.Count);
        await Assert.That(perPlayer.All(n => n.Instances.Count == players && n.PlayerSlot is null)).IsTrue();
        await Assert.That(perPlayer.All(n => n.Key.StartsWith('t'))).IsTrue();

        HashSet<RuleGraphNode> members = new(collapsed.Nodes, ReferenceEqualityComparer.Instance);
        await Assert.That(collapsed.Edges.All(e => members.Contains(e.Source) && members.Contains(e.Destination))).IsTrue()
            .Because("a collapsed edge must point at the collapsed nodes, not at one player's copies");
        await Assert.That(collapsed.Edges.Count).IsEqualTo(run.Build.Edges.Count + one.EdgeDescriptors.Count);

        // Every copy resolves to its collapsed node.
        foreach (StateNode copy in run.MaterializedPlayers[players - 1].Nodes)
        {
            await Assert.That(collapsed.TryGetNode(copy, out RuleGraphNode? node) && perPlayer.Contains(node)).IsTrue();
        }

        // Game, team and external edges are not copies: each one survives, however many share its
        // endpoints and label (the conditioned round_officially_ended trigger beside the plain one,
        // the enrichments that all write player_context on player_death).
        await Assert.That(collapsed.Edges.Where(e => e.Scope != RuleGraphScope.Player).Select(e => e.Key))
            .IsEquivalentTo(full.Edges.Where(e => e.Scope != RuleGraphScope.Player).Select(e => e.Key));
        await Assert.That(collapsed.Edges.Where(e => e.Scope != RuleGraphScope.Player).All(e => e.Instances.Count == 1)).IsTrue();

        // A per-player edge stands for every player's copy, the lowest slot's first, and its fire
        // count adds up over them.
        List<RuleGraphEdge> perPlayerEdges = [.. collapsed.Edges.Where(e => e.Scope == RuleGraphScope.Player)];
        await Assert.That(perPlayerEdges.Select(e => e.Template).Distinct().Count()).IsEqualTo(one.EdgeDescriptors.Count);
        foreach (RuleGraphEdge edge in perPlayerEdges)
        {
            await Assert.That(edge.Instances.Count).IsEqualTo(players).Because(edge.Key);
            await Assert.That(edge.Instances[0]).IsSameReferenceAs(edge.Descriptor);
            await Assert.That(edge.Descriptor.Source).IsSameReferenceAs(edge.Source.Node).Because(edge.Key);
            await Assert.That(edge.Descriptor.Destination).IsSameReferenceAs(edge.Destination.Node).Because(edge.Key);
        }

        int collapsedFires = perPlayerEdges.SelectMany(e => e.Instances).Sum(d => d.Edge?.FireCount ?? 0);
        int playerFires = full.Edges.Where(e => e.Scope == RuleGraphScope.Player).Sum(e => e.Descriptor.Edge?.FireCount ?? 0);
        await Assert.That(collapsedFires).IsEqualTo(playerFires);
        await Assert.That(collapsedFires).IsGreaterThan(perPlayerEdges.Sum(e => e.Descriptor.Edge?.FireCount ?? 0));
    }

    /// <summary>
    ///     The copy a collapsed node or edge shows is the lowest slot's, whatever order the players
    ///     materialised in, so an edge's descriptor always names the nodes it is drawn between.
    /// </summary>
    [Test]
    public async Task CollapsePlayers_PicksTheLowestSlot_WhateverTheMaterialisationOrder()
    {
        AnalysisRun run = DemoAnalysis.Run(Sample(), RuleGraphFixtures.Shipped());
        AnalysisRun reversed = run with { MaterializedPlayers = [.. run.MaterializedPlayers.Reverse()] };
        RuleGraph collapsed = RuleGraph.FromRun(reversed).CollapsePlayers();
        int lowest = run.MaterializedPlayers.Min(p => p.PlayerSlot);

        foreach (RuleGraphEdge edge in collapsed.Edges.Where(e => e.Scope == RuleGraphScope.Player))
        {
            await Assert.That(edge.Descriptor.Source).IsSameReferenceAs(edge.Source.Node).Because(edge.Key);
            await Assert.That(edge.Descriptor.Destination).IsSameReferenceAs(edge.Destination.Node).Because(edge.Key);
        }

        PerPlayerNodeTemplate.MaterializedPlayer first = run.MaterializedPlayers.Single(p => p.PlayerSlot == lowest);
        await Assert.That(collapsed.Edges.Where(e => e.Scope == RuleGraphScope.Player).Select(e => e.Descriptor)
            .SequenceEqual(first.EdgeDescriptors, ReferenceEqualityComparer.Instance)).IsTrue();
    }

    [Test]
    public async Task FromRun_ForwardStream_EqualsRetained()
    {
        string path = DemoTestHelper.RequireDemo(DemoTestHelper.SampleDemoFileName);
        IReadOnlyList<RulesetDoc> shipped = RuleGraphFixtures.Shipped();
        RuleGraph retained = RuleGraph.FromRun(DemoAnalysis.Run(Sample(), shipped));
        RuleGraph streamed = RuleGraph.FromRun(DemoAnalysis.Run(path, shipped));

        await Assert.That(streamed.Nodes.Select(n => n.Key).Order(StringComparer.Ordinal))
            .IsEquivalentTo(retained.Nodes.Select(n => n.Key).Order(StringComparer.Ordinal));
        await Assert.That(KindCounts(streamed)).IsEquivalentTo(KindCounts(retained));
    }

    /// <summary>
    ///     The template preview runs the builder's per-player factory. Run after it, the build must
    ///     evaluate exactly as it does without it.
    /// </summary>
    [Test]
    public async Task FromBuild_ThenRun_IsSideEffectFree()
    {
        ParsedDemo demo = Sample();
        IReadOnlyList<RulesetDoc> shipped = RuleGraphFixtures.Shipped();

        BuildResult plain = DemoAnalysis.Build(demo, shipped);
        string expected = ForwardPathParityTests.RunDigest.Render(DemoAnalysis.Evaluate(demo, plain));

        BuildResult previewed = DemoAnalysis.Build(demo, shipped);
        RuleGraph preview = RuleGraph.FromBuild(previewed);
        await Assert.That(preview.Diagnostics).IsEmpty();
        await Assert.That(preview.Nodes.Any(n => n.Scope == RuleGraphScope.Player)).IsTrue();
        string actual = ForwardPathParityTests.RunDigest.Render(DemoAnalysis.Evaluate(demo, previewed));

        await Assert.That(actual).IsEqualTo(expected);
    }

    [Test]
    [Category("Unit")]
    public async Task FromBuild_DemoLess_SettleEntityRead_ReportsDiagnostic()
    {
        // No per-player provider registry: a settle-site entity read cannot build its pull node.
        CheckedRuleset rs = Resolve("""
                                    ruleset: settle
                                    for: each_player
                                    stats:
                                      kills:
                                        count: kill
                                        per: round
                                      hp:
                                        compute: "kills + player.health"
                                        per: round
                                    """);
        BuildResult build = new RuleChainBuilder(EventRegistry.Build()).Build([rs]);

        RuleGraph view = RuleGraph.FromBuild(build);

        await Assert.That(view.Diagnostics.Count).IsEqualTo(1);
        await Assert.That(view.Diagnostics[0]).Contains("template 0");
        await Assert.That(view.Nodes.Any(n => n.Scope == RuleGraphScope.Player)).IsFalse();
        await Assert.That(view.Nodes.Count).IsEqualTo(build.Nodes.Count + build.ExternalNodes.Count);
    }

    [Test]
    public async Task HighlightChains_CoverTheChainAndItsInputs_AndNothingElse()
    {
        AnalysisRun run = DemoAnalysis.Run(Sample(), RuleGraphFixtures.Shipped());
        RuleGraph view = RuleGraph.FromRun(run);
        int slot = run.MaterializedPlayers[0].PlayerSlot;
        List<RuleGraphNode> player = [.. view.Nodes.Where(n => n.PlayerSlot == slot)];

        RuleGraphNode Named(string name) => player.Single(n => n.Node.Name == name);

        foreach (string name in (string[])["_chain_kast", "kast.count", "has_kast", "enemy_kills_round", "team_assists_round", "Survived", "Traded", "Alive"])
        {
            await Assert.That(Named(name).HighlightChains.Contains("_chain_kast")).IsTrue()
                .Because($"{name} is the kast highlight or feeds it");
        }

        // The walk stops at the root and the external state, and marks game scaffolding without
        // walking through it.
        await Assert.That(view.Nodes.Where(n => n.Origin is RuleGraphNodeOrigin.Root or RuleGraphNodeOrigin.External)
            .All(n => n.HighlightChains.Count == 0)).IsTrue();
        await Assert.That(Named("deaths").HighlightChains.Contains("_chain_kast")).IsFalse()
            .Because("deaths feed no part of KAST");

        // A table column's ChainId is the key of the ruleset that declared it, one of its node's
        // owners, and a different key space from the highlight chains.
        foreach (PerPlayerColumnAssignment column in run.MaterializedPlayers[0].ColumnAssignments)
        {
            await Assert.That(view.TryGetNode(column.Node, out RuleGraphNode? node)).IsTrue();
            IEnumerable<string> owners = node!.Owners.Select(o => "_chain_" + o[..o.IndexOf('.', StringComparison.Ordinal)]);
            await Assert.That(owners.Contains(column.ChainId)).IsTrue()
                .Because($"column {column.ColumnName} ({column.ChainId}) is declared by one of {string.Join(", ", node.Owners)}");
        }
    }

    [Test]
    [Category("Unit")]
    public async Task Provenance_NamesEveryDeclarer_AndTheScaffolding()
    {
        const string a = "ruleset: rs_a\nfor: each_player\nstats:\n  kills:\n    count: kill\n    per: match\n";
        const string b = "ruleset: rs_b\nfor: each_player\nstats:\n  kills:\n    count: kill\n    per: match\n";
        RuleConfigLoadResult loaded = YamlConfigLoader.LoadDocuments([("a.rules.yaml", a), ("b.rules.yaml", b)]);
        List<RulesetDoc> docs = [.. loaded.Rulesets, .. RuleGraphFixtures.Matrix()];
        BuildResult build = RuleGraphFixtures.Build(docs, new Cs2GotvProfile());
        RuleGraph view = RuleGraph.FromBuild(build);

        RuleGraphNode shared = view.Nodes.Single(n => n.RuleIds.Contains("rs_a.kills"));
        await Assert.That(shared.RuleIds).Contains("rs_b.kills").Because("the two stats hash equal and share a node");

        // The matrix's kills_match is the same stat under another name, so it shares the node too.
        await Assert.That(shared.Owners).IsEquivalentTo(["rs_a.kills", "rs_b.kills", "fm_player.kills_match"]);
        await Assert.That(shared.Ruleset).IsEqualTo("rs_a");
        await Assert.That(shared.Origin).IsEqualTo(RuleGraphNodeOrigin.Rule);

        RuleGraphNode firstTick = view.Nodes.Single(n => n.Node.Name == "__first_tick_rs_a_kills");
        await Assert.That(firstTick.Owners).IsEquivalentTo(["rs_a.kills"]);
        RuleGraphNode guard = view.Nodes.Single(n => n.Node.Name == "__seen_tally_fm_player_multi_tally");
        await Assert.That(guard.Owners).IsEquivalentTo(["fm_player.multi_tally"]);
        RuleGraphNode count = view.Nodes.Single(n => n.Node.Name == "multi.count");
        await Assert.That(count.Owners).IsEquivalentTo(["fm_player.multi"]);

        List<RuleGraphNode> team = [.. view.Nodes.Where(n => n.Scope == RuleGraphScope.Team)];
        await Assert.That(team.Count).IsGreaterThan(0);
        await Assert.That(team.All(n => n.TeamSide is 2 or 3)).IsTrue();
        await Assert.That(team.Where(n => n.RuleIds.Contains("fm_sides.kills")).Select(n => n.TeamSide ?? 0))
            .IsEquivalentTo([2, 3]);

        await Assert.That(view.Nodes.Single(n => n.Node.Name == "enrich.kill.was_enemy_kill").Origin)
            .IsEqualTo(RuleGraphNodeOrigin.Enrichment);
        await Assert.That(view.Nodes.Single(n => n.Node is RootNode).Origin).IsEqualTo(RuleGraphNodeOrigin.Root);
        await Assert.That(view.Nodes.Where(n => n.Scope == RuleGraphScope.External).Select(n => n.Key))
            .IsEquivalentTo(["x/player_context", "x/entity_state", "x/highlights"]);
        await Assert.That(view.Nodes.Single(n => n.Node.Name == "Alive" && n.Scope == RuleGraphScope.Player).Origin)
            .IsEqualTo(RuleGraphNodeOrigin.Context);
    }

    /// <summary>A template with no provenance, from a hand-built graph, still draws, as <see cref="RuleGraphNodeOrigin.Unknown" />.</summary>
    [Test]
    [Category("Unit")]
    public async Task HandBuiltTemplate_IsDrawn_AsUnknown()
    {
        StateGraph graph = new();
        graph.AddPerPlayerTemplate(new PerPlayerNodeTemplate((slot, _, name) =>
        {
            GenericBoolNode flag = new("flag", name);
            return new PerPlayerNodeTemplate.MaterializedPlayer(slot, name, [flag], [], [], []);
        }));
        BuildResult build = new(graph, [graph.Root], [], new HashSet<Type>());

        RuleGraph view = RuleGraph.FromBuild(build);

        RuleGraphNode flagNode = view.Nodes.Single(n => n.Node.Name == "flag");
        await Assert.That(flagNode.Origin).IsEqualTo(RuleGraphNodeOrigin.Unknown);
        await Assert.That(flagNode.Key).IsEqualTo("t0/0");
        await Assert.That(view.Nodes.Single(n => n.Node is RootNode).Origin).IsEqualTo(RuleGraphNodeOrigin.Root);
    }

    /// <summary>An edge added to the graph without a descriptor is still drawn, and said to be.</summary>
    [Test]
    [Category("Unit")]
    public async Task UndescribedEdge_IsDrawn_AndReported()
    {
        StateGraph graph = new();
        GenericBoolNode target = new("target");
        graph.AddEdge(new OnGameEvent<CS2OpenSchema.Events.PlayerDeathEvent>(graph.Root, target, EdgeEffect.Activate));
        BuildResult build = new(graph, [graph.Root, target], [], new HashSet<Type>());

        RuleGraph view = RuleGraph.FromBuild(build, includeTemplates: false);

        RuleGraphEdge edge = view.Edges.Single();
        await Assert.That(edge.Descriptor.Kind).IsEqualTo(GraphEdgeKind.Undescribed);
        await Assert.That(edge.Destination.Node).IsSameReferenceAs(target);
        await Assert.That(view.Diagnostics.Count).IsEqualTo(1);
    }

    /// <summary>
    ///     A descriptor made by hand, as through 0.12, names no engine edge. It describes the edge it
    ///     has the source and destination of, which is drawn once and carries the edge.
    /// </summary>
    [Test]
    [Category("Unit")]
    public async Task HandBuiltDescriptor_WithoutEdge_DescribesItsEdge()
    {
        StateGraph graph = new();
        GenericBoolNode target = new("target");
        OnGameEvent<CS2OpenSchema.Events.PlayerDeathEvent> death = new(graph.Root, target, EdgeEffect.Activate);
        OnGameEvent<CS2OpenSchema.Events.PlayerHurtEvent> hurt = new(graph.Root, target, EdgeEffect.Activate);
        graph.AddEdge(death);
        graph.AddEdge(hurt);
        GraphEdgeDescriptor deathRow = new(graph.Root, target, "player_death", EdgeEffect.Activate);
        GraphEdgeDescriptor hurtRow = new(graph.Root, target, "player_hurt", EdgeEffect.Activate);
        BuildResult build = new(graph, [graph.Root, target], [deathRow, hurtRow], new HashSet<Type>());

        RuleGraph view = RuleGraph.FromBuild(build, includeTemplates: false);

        await Assert.That(view.Diagnostics).IsEmpty();
        await Assert.That(view.Edges.Count).IsEqualTo(2);
        await Assert.That(view.Edges.All(e => e.Descriptor.Kind == GraphEdgeKind.Trigger)).IsTrue();
        await Assert.That(view.Edges[0].Descriptor.Edge).IsSameReferenceAs(death);
        await Assert.That(view.Edges[1].Descriptor.Edge).IsSameReferenceAs(hurt);
    }

    /// <summary>
    ///     A highlight chain is walked back from each player's own logic node. A game node that feeds
    ///     several players is on the chain, but the walk does not come forward out of it into another
    ///     player's nodes.
    /// </summary>
    [Test]
    public async Task HighlightChain_DoesNotReachAnotherPlayer()
    {
        StateGraph graph = new();
        GenericBoolNode shared = new("shared");
        BuildResult build = new(graph, [graph.Root, shared],
            [new GraphEdgeDescriptor(graph.Root, shared, "round_start", EdgeEffect.Activate)], new HashSet<Type>());

        // Only slot 0 has the highlight; both players' "a" read the same game node.
        PerPlayerNodeTemplate.MaterializedPlayer Player(int slot)
        {
            GenericBoolNode a = new("a", $"p{slot}");
            List<StateNode> nodes = [a];
            List<GraphEdgeDescriptor> rows = [new(shared, a, "player_death", EdgeEffect.Activate)];
            if (slot == 0)
            {
                ConjunctionNode chain = new("_chain_x");
                nodes.Add(chain);
                rows.Add(new GraphEdgeDescriptor(a, chain, "", EdgeEffect.SetValue) { Kind = GraphEdgeKind.LogicInput });
            }

            return new PerPlayerNodeTemplate.MaterializedPlayer(slot, $"p{slot}", nodes, [], [], rows);
        }

        AnalysisRun run = DemoAnalysis.Run(Sample(), RuleGraphFixtures.Shipped()) with
        {
            Build = build,
            MaterializedPlayers = [Player(0), Player(1)]
        };
        RuleGraph view = RuleGraph.FromRun(run);

        RuleGraphNode Node(string key) => view.TryGetNode(key, out RuleGraphNode? node) ? node : throw new InvalidOperationException(key);
        await Assert.That(Node("p0/t0/1").HighlightChains).IsEquivalentTo(["_chain_x"]);
        await Assert.That(Node("p0/t0/0").HighlightChains).IsEquivalentTo(["_chain_x"]);
        await Assert.That(Node("g/1").HighlightChains).IsEquivalentTo(["_chain_x"]);
        await Assert.That(Node("p1/t0/0").HighlightChains).IsEmpty()
            .Because("player 1's node is fed by the shared node, not a feeder of player 0's chain");
    }

    private static List<string> Shape(PerPlayerNodeTemplate.MaterializedPlayer player) =>
    [
        .. player.Nodes.Select(n => $"{n.GetType().Name}|{n.Name}"),
        .. player.EdgeDescriptors.Select(d => $"{d.Kind}|{d.Source.Name}|{d.Destination.Name}|{d.Label}")
    ];

    private static List<string> KindCounts(RuleGraph view) =>
    [
        .. view.Edges.GroupBy(e => e.Descriptor.Kind).Select(g => $"{g.Key}={g.Count()}").Order(StringComparer.Ordinal)
    ];

    private static CheckedRuleset Resolve(string yaml)
    {
        RulesetDoc doc = RulesetDocumentLoader.Load(yaml, "probe.rules.yaml").Doc
                         ?? throw new InvalidOperationException("test ruleset failed to map");
        CatalogScopeAdapter adapter = CatalogScopeAdapter.From(CatalogResource.Load());
        RulesetResolveResult resolved = CheckedRulesetDraft.Load(doc, adapter).Build(64.0, "Cs2GotvProfile");
        return resolved.Ruleset
               ?? throw new InvalidOperationException("test ruleset failed to resolve: " + string.Join("; ", resolved.Diagnostics));
    }
}
