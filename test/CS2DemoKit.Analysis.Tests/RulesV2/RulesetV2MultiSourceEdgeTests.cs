#region

using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Analysis.Building;
using CS2DemoKit.Analysis.Catalog;
using CS2DemoKit.Analysis.Config;
using CS2DemoKit.Analysis.Graphs;
using CS2DemoKit.Analysis.Nodes;
using CS2DemoKit.Analysis.Registry;
using CS2DemoKit.Analysis.RulesetsV2.Model;
using CS2DemoKit.Analysis.RulesetsV2.Resolve;
using CS2DemoKit.Analysis.Yaml;

#endregion

namespace CS2DemoKit.Analysis.Tests.RulesV2;

/// <summary>
///     Wiring battery for the planner: the structural halves of "wire DeclaredReads +
///     multi-source conditional edges into the v2 planner", exercised demo-free (the per-player
///     template materializes against a null demo, the same seam
///     <see cref="RulesetV2PlannerHashTests" /> uses).
///     <list type="bullet">
///         <item>
///             <b>DeclaredReads (A1):</b> a v2 game-event edge whose condition reads a graph node
///             (the <c>enrich.kill.was_enemy_kill</c> enrichment behind <c>match: {enemy: true}</c>)
///             carries that node in <see cref="StateEdge.DeclaredReads" />; an edge whose condition
///             reads only event fields carries none (the v1-identical, pre-A1 empty set).
///         </item>
///         <item>
///             <b>OR → <see cref="DisjunctionNode" />:</b> a <c>flag: when: a or b</c> lowers to a
///             disjunction (v1's <c>parents: {mode: any}</c> shape); <c>a and b</c> to a
///             <see cref="ConjunctionNode" />.
///         </item>
///         <item>
///             <b>Multi-source arithmetic (A2):</b> a <c>flag: when: a + b &gt; 5</c> — one predicate
///             reading two siblings — lowers to a single
///             <see cref="MultiSourceConditionalEdge" /> (<see cref="ConditionalEdge.FromAll" />)
///             over both sources, satisfied only when both are active and the compiled predicate holds.
///         </item>
///     </list>
/// </summary>
[Category("Unit")]
public class RulesetV2MultiSourceEdgeTests
{
    // Two structurally-distinct round-scoped counters (so the resolved-identity hasher keeps them as
    // separate nodes): `kills` gates only on the kill view's baked event-field filter; `assists`
    // additionally reads the enemy enrichment. The flags below combine them under or/and/arithmetic.
    private const string ProbeYaml = """
                                     ruleset: probe
                                     for: each_player
                                     stats:
                                       kills:
                                         count: kill
                                         per: round
                                       assists:
                                         count: kill
                                         match: { enemy: true, actor: any }
                                         where: "event.Assister == player.slot"
                                         per: round
                                       or_flag:
                                         flag:
                                           when: "kills > 0 or assists > 0"
                                         per: round
                                       and_flag:
                                         flag:
                                           when: "kills > 0 and assists > 0"
                                         per: round
                                       sum_flag:
                                         flag:
                                           when: "kills + assists > 5"
                                         per: round
                                     """;

    /// <summary>
    ///     A2's <c>flag: when: a or b</c> lowers to a <see cref="DisjunctionNode" /> with one input per
    ///     operand, and <c>a and b</c> to a <see cref="ConjunctionNode" /> — the OR case matching v1's
    ///     <c>parents: {mode: any}</c> disjunction.
    /// </summary>
    [Test]
    public async Task WhenOr_LowersToDisjunction_AndAnd_LowersToConjunction()
    {
        PerPlayerNodeTemplate.MaterializedPlayer player = MaterializeProbe();

        StateNode orNode = FindNode(player, "or_flag");
        StateNode andNode = FindNode(player, "and_flag");

        await Assert.That(orNode).IsTypeOf<DisjunctionNode>()
            .Because("a top-level OR when: must lower to a DisjunctionNode (v1 parents: {mode: any})");
        await Assert.That(andNode).IsTypeOf<ConjunctionNode>()
            .Because("a top-level AND when: must lower to a ConjunctionNode (v1 parents: {mode: all})");

        await Assert.That(((DisjunctionNode)orNode).Inputs).HasCount().EqualTo(2)
            .Because("the OR has two operands, so the disjunction gets two single-source inputs");
        await Assert.That(((ConjunctionNode)andNode).Inputs).HasCount().EqualTo(2);

        // Both operands read distinct single sources (not a multi-source edge).
        foreach (IConditionalEdge input in ((DisjunctionNode)orNode).Inputs)
        {
            await Assert.That(input.Sources).HasCount().EqualTo(1)
                .Because("each `x > 0` disjunct reads exactly one sibling");
        }
    }

    /// <summary>
    ///     A2's multi-source case: <c>flag: when: a + b &gt; 5</c> — one comparison reading two siblings
    ///     — lowers to a single <see cref="MultiSourceConditionalEdge" /> over both sources, whose
    ///     predicate only fires once both are active and <c>a.Value + b.Value &gt; 5</c>.
    /// </summary>
    [Test]
    public async Task WhenArithmetic_ReadingTwoSiblings_LowersToMultiSourceEdge()
    {
        PerPlayerNodeTemplate.MaterializedPlayer player = MaterializeProbe();

        StateNode sumNode = FindNode(player, "sum_flag");
        await Assert.That(sumNode).IsTypeOf<ConjunctionNode>()
            .Because("a single arithmetic comparison is one conjunction input (not an OR)");

        IReadOnlyList<IConditionalEdge> inputs = ((ConjunctionNode)sumNode).Inputs;
        await Assert.That(inputs).HasCount().EqualTo(1)
            .Because("the whole `a + b > 5` predicate is one multi-source edge");

        IConditionalEdge edge = inputs[0];
        await Assert.That(edge).IsTypeOf<MultiSourceConditionalEdge>()
            .Because("a predicate reading >1 sibling must lower via ConditionalEdge.FromAll");
        await Assert.That(edge.Sources).HasCount().EqualTo(2)
            .Because("the edge declares both siblings it reads (A2 N-source contract)");

        // Functional: the FromAll predicate reads the live node values, and requires both active.
        GenericRoundScopedValueNode<int> kills = (GenericRoundScopedValueNode<int>)FindNode(player, "kills");
        GenericRoundScopedValueNode<int> assists = (GenericRoundScopedValueNode<int>)FindNode(player, "assists");

        kills.SetValue(3);
        assists.SetValue(4);
        await Assert.That(edge.IsSatisfied).IsTrue()
            .Because("both sources active and 3 + 4 > 5");

        kills.SetValue(1);
        assists.SetValue(1);
        await Assert.That(edge.IsSatisfied).IsFalse()
            .Because("both sources active but 1 + 1 is not > 5");
    }

    /// <summary>
    ///     A1: the <c>assists</c> count edge — its condition reads the <c>enrich.kill.was_enemy_kill</c>
    ///     enrichment (via <c>match: {enemy: true}</c>) — carries that node in
    ///     <see cref="StateEdge.DeclaredReads" />; the <c>kills</c> edge, reading only event fields,
    ///     carries no declared reads (the v1-identical empty set).
    /// </summary>
    [Test]
    public async Task CountEdge_DeclaredReads_CarriesEnrichmentNode_ButNotForFieldOnlyEdge()
    {
        PerPlayerNodeTemplate.MaterializedPlayer player = MaterializeProbe();

        StateEdge assistsEdge = FindWriteEdge(player, "assists");
        StateEdge killsEdge = FindWriteEdge(player, "kills");

        await Assert.That(assistsEdge.DeclaredReads).IsNotNull()
            .Because("the assists condition reads enrich.kill.was_enemy_kill, a graph node");
        await Assert.That(assistsEdge.DeclaredReads!.Any(n =>
                string.Equals(n.Name, "enrich.kill.was_enemy_kill", StringComparison.Ordinal))).IsTrue()
            .Because("the enemy enrichment must appear in the edge's declared read set (A1)");

        await Assert.That(killsEdge.DeclaredReads is null || killsEdge.DeclaredReads.Count == 0).IsTrue()
            .Because("the kills condition reads only event fields, so its declared read set is empty "
                     + "(v1-identical, pre-A1 ordering)");
    }

    // ── Scaffolding ──────────────────────────────────────────────────────────

    private static PerPlayerNodeTemplate.MaterializedPlayer MaterializeProbe()
    {
        RulesetDoc doc = RulesetDocumentLoader.Load(ProbeYaml, "probe.rules.yaml").Doc
                         ?? throw new InvalidOperationException("probe ruleset failed to map");
        CatalogScopeAdapter adapter = CatalogScopeAdapter.From(CatalogResource.Load());
        RulesetResolveResult resolved = CheckedRulesetDraft.Load(doc, adapter).Build(64.0, "Cs2GotvProfile");
        CheckedRuleset rs = resolved.Ruleset
                            ?? throw new InvalidOperationException(
                                "probe ruleset failed to resolve: " + string.Join("; ", resolved.Diagnostics));

        RuleChainBuilder builder = new(EventRegistry.Build());
        BuildResult build = builder.Build([rs]);

        // The v2 template is the last per-player template added (after the built-in context template).
        return build.Graph.PerPlayerTemplates[^1].Materialize(0, 0, "test", null);
    }

    private static StateNode FindNode(PerPlayerNodeTemplate.MaterializedPlayer player, string name) =>
        player.Nodes.FirstOrDefault(n => string.Equals(n.Name, name, StringComparison.Ordinal))
        ?? throw new InvalidOperationException($"node '{name}' not materialized");

    private static StateEdge FindWriteEdge(PerPlayerNodeTemplate.MaterializedPlayer player, string writtenNodeName) =>
        player.Edges.FirstOrDefault(e =>
            e.WrittenNode is { } written && string.Equals(written.Name, writtenNodeName, StringComparison.Ordinal))
        ?? throw new InvalidOperationException($"no write edge for node '{writtenNodeName}'");
}
