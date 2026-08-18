#region

using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Analysis.Building;
using CS2DemoKit.Analysis.Catalog;
using CS2DemoKit.Analysis.Config;
using CS2DemoKit.Analysis.Graphs;
using CS2DemoKit.Analysis.Nodes;
using CS2DemoKit.Analysis.Registry;
using CS2DemoKit.Analysis.RulesetsV2.Compile;
using CS2DemoKit.Analysis.RulesetsV2.Model;
using CS2DemoKit.Analysis.RulesetsV2.Resolve;
using CS2DemoKit.Analysis.Yaml;

#endregion

namespace CS2DemoKit.Analysis.Tests.RulesV2;

/// <summary>
///     The pre-freeze planner-completeness gap closure: <c>flag: when:</c> shapes the structural
///     OR/AND-of-comparisons decomposition cannot reduce — a negation (<c>not X</c>), a mixed nested
///     boolean (<c>a and (b or c)</c>'s inner OR operand), a mix of the two — used to throw
///     <c>"when: expression shape … is not yet lowered by the planner."</c> at
///     <c>LowerWhenTerm</c>'s default. They now lower through the general whole-predicate fallback
///     (<c>LowerWhenTermGeneral</c>): one <see cref="MultiSourceConditionalEdge" />
///     (<see cref="ConditionalEdge.FromAll" />) whose predicate — compiled by
///     <c>CompileNodeBoolExpression</c> — evaluates the whole term (<c>not</c>/<c>and</c>/<c>or</c>/
///     comparisons) over the referenced siblings' live values. The regression half asserts the proven
///     fast paths (top-level OR → <see cref="DisjunctionNode" /> single-source inputs, AND-of-
///     comparisons → <see cref="ConjunctionNode" /> single-source inputs, a bare ref → single-source
///     <c>active</c>) are UNCHANGED — the byte-identity guard that keeps the corpus goldens stable.
///     Demo-free — the per-player template materializes against a null demo, the same seam
///     <see cref="RulesetV2MultiSourceEdgeTests" /> uses.
/// </summary>
[Category("Unit")]
public class RulesetV2WhenFallbackTests
{
    // `kills` and `assists` are two structurally-distinct round-scoped int counters (the assist view's
    // match/where keeps the resolved-identity hasher from deduping them onto one node — so a
    // multi-source edge genuinely declares TWO sources). The flags combine them under the shapes the
    // fallback must now lower.
    private const string ProbeYaml = """
                                     ruleset: fallback_probe
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
                                       k_flag:
                                         flag:
                                           when: "kills > 0"
                                         per: round
                                       bare_flag:
                                         flag:
                                           when: "k_flag"
                                         per: round
                                       or_flag:
                                         flag:
                                           when: "kills > 0 or assists > 0"
                                         per: round
                                       and_flag:
                                         flag:
                                           when: "kills > 0 and assists > 0"
                                         per: round
                                       nested_flag:
                                         flag:
                                           when: "(kills > 0 or assists > 0) and kills > 1"
                                         per: round
                                       not_flag:
                                         flag:
                                           when: "not (kills > 0)"
                                         per: round
                                       and_not_flag:
                                         flag:
                                           when: "kills > 0 and not (assists > 0)"
                                         per: round
                                     """;

    /// <summary>
    ///     Mixed nesting: <c>when: (kills &gt; 0 or assists &gt; 0) and kills &gt; 1</c>. The top AND
    ///     flattens to a <see cref="ConjunctionNode" /> with two inputs — the inner OR (a non-comparison
    ///     <c>BinaryNode</c> the structural flatten cannot reduce) lowers through the fallback to one
    ///     <see cref="MultiSourceConditionalEdge" /> over both siblings, and <c>kills &gt; 1</c> stays a
    ///     single-source fast-path edge. The fallback edge's predicate genuinely evaluates the OR over
    ///     live values, and the whole node fires only when <c>(kills&gt;0 ∨ assists&gt;0) ∧ kills&gt;1</c>.
    /// </summary>
    [Test]
    public async Task MixedNesting_LowersToFallbackEdge_AndGates()
    {
        PerPlayerNodeTemplate.MaterializedPlayer player = MaterializeProbe();
        ConjunctionNode nested = (ConjunctionNode)FindNode(player, "nested_flag");

        await Assert.That(nested.Inputs).HasCount().EqualTo(2)
            .Because("(a or b) flattens as one AND operand, kills>1 as the other");

        MultiSourceConditionalEdge orEdge = nested.Inputs.OfType<MultiSourceConditionalEdge>().Single();
        await Assert.That(orEdge.Sources).HasCount().EqualTo(2)
            .Because("the (kills>0 or assists>0) fallback edge declares BOTH siblings it reads");

        GenericRoundScopedValueNode<int> kills = (GenericRoundScopedValueNode<int>)FindNode(player, "kills");
        GenericRoundScopedValueNode<int> assists = (GenericRoundScopedValueNode<int>)FindNode(player, "assists");

        // The inner-OR fallback edge, in isolation: satisfied iff both sources active AND (a>0 ∨ b>0).
        kills.SetValue(0);
        assists.SetValue(3);
        await Assert.That(orEdge.IsSatisfied).IsTrue().Because("0>0 is false but 3>0 is true → the OR holds");
        kills.SetValue(0);
        assists.SetValue(0);
        await Assert.That(orEdge.IsSatisfied).IsFalse().Because("neither 0>0 nor 0>0 → the OR is false");

        // The whole node: fires only when (kills>0 ∨ assists>0) ∧ kills>1.
        await Assert.That(Fires(nested, kills, 2, assists, 0)).IsTrue().Because("(2>0) ∧ 2>1");
        await Assert.That(Fires(nested, kills, 0, assists, 5)).IsFalse().Because("(∨ holds) but 0>1 is false");
        await Assert.That(Fires(nested, kills, 1, assists, 0)).IsFalse().Because("(1>0) ∧ 1>1 is false");
    }

    /// <summary>
    ///     Negation: <c>when: not (kills &gt; 0)</c>. A <c>UnaryNode</c> — previously an unlowered shape —
    ///     lowers through the fallback to one <see cref="MultiSourceConditionalEdge" /> over the single
    ///     referenced sibling, whose predicate is <c>!(kills &gt; 0)</c>: satisfied exactly when the
    ///     sibling is inactive/false (<c>kills == 0</c>).
    /// </summary>
    [Test]
    public async Task Negation_LowersToFallbackEdge_AndGates()
    {
        PerPlayerNodeTemplate.MaterializedPlayer player = MaterializeProbe();
        ConjunctionNode not = (ConjunctionNode)FindNode(player, "not_flag");

        MultiSourceConditionalEdge edge = not.Inputs.OfType<MultiSourceConditionalEdge>().Single();
        await Assert.That(edge.Sources).HasCount().EqualTo(1).Because("not (kills>0) reads the single sibling kills");

        GenericRoundScopedValueNode<int> kills = (GenericRoundScopedValueNode<int>)FindNode(player, "kills");
        kills.SetValue(0);
        await Assert.That(edge.IsSatisfied).IsTrue().Because("!(0>0) = !(false) = true → fires when kills is false");
        kills.SetValue(3);
        await Assert.That(edge.IsSatisfied).IsFalse().Because("!(3>0) = !(true) = false");
    }

    /// <summary>
    ///     Mixed + negation: <c>when: kills &gt; 0 and not (assists &gt; 0)</c>. The AND flattens to a
    ///     <see cref="ConjunctionNode" /> whose <c>kills &gt; 0</c> operand stays a single-source fast-path
    ///     edge and whose <c>not (assists &gt; 0)</c> operand routes through the fallback. Fires only when
    ///     <c>kills&gt;0 ∧ ¬(assists&gt;0)</c>.
    /// </summary>
    [Test]
    public async Task MixedPlusNegation_Gates()
    {
        PerPlayerNodeTemplate.MaterializedPlayer player = MaterializeProbe();
        ConjunctionNode node = (ConjunctionNode)FindNode(player, "and_not_flag");

        await Assert.That(node.Inputs).HasCount().EqualTo(2);
        await Assert.That(node.Inputs.OfType<MultiSourceConditionalEdge>().Count()).IsEqualTo(1)
            .Because("only the `not (assists>0)` operand needs the fallback edge; kills>0 is fast-path single-source");

        GenericRoundScopedValueNode<int> kills = (GenericRoundScopedValueNode<int>)FindNode(player, "kills");
        GenericRoundScopedValueNode<int> assists = (GenericRoundScopedValueNode<int>)FindNode(player, "assists");

        await Assert.That(Fires(node, kills, 3, assists, 0)).IsTrue().Because("3>0 ∧ ¬(0>0) = T ∧ ¬F = T");
        await Assert.That(Fires(node, kills, 3, assists, 2)).IsFalse().Because("3>0 ∧ ¬(2>0) = T ∧ ¬T = F");
        await Assert.That(Fires(node, kills, 0, assists, 0)).IsFalse().Because("0>0 is false → the AND is false");
    }

    /// <summary>
    ///     Regression / byte-identity guard: the proven fast paths are UNTOUCHED by the fallback. A
    ///     top-level OR still lowers to a <see cref="DisjunctionNode" /> with single-source inputs; an
    ///     AND-of-comparisons to a <see cref="ConjunctionNode" /> with single-source
    ///     <see cref="ConditionalEdge{T}" /> inputs (NOT the multi-source fallback edge); a bare sibling
    ///     ref to one single-source <c>active</c> edge. If any of these silently routed to the fallback,
    ///     the corpus goldens would shift — these asserts are the tripwire.
    /// </summary>
    [Test]
    public async Task FastPaths_Unchanged_NotRoutedThroughFallback()
    {
        PerPlayerNodeTemplate.MaterializedPlayer player = MaterializeProbe();

        DisjunctionNode orFlag = (DisjunctionNode)FindNode(player, "or_flag");
        await Assert.That(orFlag.Inputs).HasCount().EqualTo(2);
        foreach (IConditionalEdge input in orFlag.Inputs)
        {
            await Assert.That(input).IsNotTypeOf<MultiSourceConditionalEdge>()
                .Because("each `x > 0` disjunct is a single-source fast-path edge, not the fallback");
            await Assert.That(input.Sources).HasCount().EqualTo(1);
        }

        ConjunctionNode andFlag = (ConjunctionNode)FindNode(player, "and_flag");
        await Assert.That(andFlag.Inputs).HasCount().EqualTo(2);
        foreach (IConditionalEdge input in andFlag.Inputs)
        {
            await Assert.That(input).IsNotTypeOf<MultiSourceConditionalEdge>()
                .Because("a > 0 and b > 0 is two single-source comparisons, NOT one multi-source predicate");
            await Assert.That(input.Sources).HasCount().EqualTo(1);
        }

        ConjunctionNode bareFlag = (ConjunctionNode)FindNode(player, "bare_flag");
        await Assert.That(bareFlag.Inputs).HasCount().EqualTo(1);
        await Assert.That(bareFlag.Inputs[0]).IsNotTypeOf<MultiSourceConditionalEdge>()
            .Because("a bare sibling ref stays a single-source `active` edge");
        await Assert.That(bareFlag.Inputs[0].Sources).HasCount().EqualTo(1);
    }

    /// <summary>
    ///     Identity: the resolved-identity hash is driven by the <c>when:</c> AST (row 5,
    ///     <see cref="CheckedStat.TriggerCondition" />), which the fallback does NOT touch — it changes
    ///     only edge construction. So <c>when: not (kills &gt; 0)</c> and <c>when: kills &gt; 0</c> hash
    ///     APART (they are different predicates) even though both read the same sibling — no preimage
    ///     change is needed for the fallback to dedup/discriminate correctly.
    /// </summary>
    [Test]
    public async Task Identity_NegatedWhen_HashesApartFromBareWhen()
    {
        CheckedRuleset rs = Compile(ProbeYaml);

        // The two flags both read `kills`, so its hash must be seeded first (the dependency-ordered
        // hashing invariant the planner upholds). Both flags reference it identically — the ONLY
        // difference between them is the `not`, so any hash divergence is the when: AST alone.
        Dictionary<string, ReadOnlyMemory<byte>> map = new(StringComparer.Ordinal);
        MapStatHashSource source = new(map);
        CheckedStat kills = rs.Stats.Single(s => s.StatId == "kills");
        byte[] killsHash = V2StatHasher.Hash(kills, source);
        map["kills"] = killsHash;
        map["fallback_probe.kills"] = killsHash;

        CheckedStat pos = rs.Stats.Single(s => s.StatId == "k_flag"); // when: kills > 0
        CheckedStat neg = rs.Stats.Single(s => s.StatId == "not_flag"); // when: not (kills > 0)

        await Assert.That(Hex(neg, source)).IsNotEqualTo(Hex(pos, source))
            .Because("`not (kills>0)` and `kills>0` are different when: predicates → distinct identity hashes");
    }

    // ── Scaffolding ──────────────────────────────────────────────────────────

    /// <summary>Sets both counters, re-evaluates the conjunction, and returns whether it is now active.</summary>
    private static bool Fires(ConjunctionNode node,
        GenericRoundScopedValueNode<int> kills, int killCount,
        GenericRoundScopedValueNode<int> assists, int assistCount)
    {
        kills.SetValue(killCount);
        assists.SetValue(assistCount);
        node.MarkInputsDirty();
        node.Recompute();
        return node.Value;
    }

    private static PerPlayerNodeTemplate.MaterializedPlayer MaterializeProbe()
    {
        RulesetDoc doc = RulesetDocumentLoader.Load(ProbeYaml, "fallback_probe.rules.yaml").Doc
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

    private static CheckedRuleset Compile(string yaml)
    {
        RulesetDoc doc = RulesetDocumentLoader.Load(yaml, "fallback_probe.rules.yaml").Doc
                         ?? throw new InvalidOperationException("probe ruleset failed to map");
        CatalogScopeAdapter adapter = CatalogScopeAdapter.From(CatalogResource.Load());
        RulesetResolveResult resolved = CheckedRulesetDraft.Load(doc, adapter).Build(64.0, "Cs2GotvProfile");
        return resolved.Ruleset
               ?? throw new InvalidOperationException(
                   "probe ruleset failed to resolve: " + string.Join("; ", resolved.Diagnostics));
    }

    private static string Hex(CheckedStat stat, MapStatHashSource source) =>
        Convert.ToHexStringLower(V2StatHasher.Hash(stat, source));

    private static StateNode FindNode(PerPlayerNodeTemplate.MaterializedPlayer player, string name) =>
        player.Nodes.FirstOrDefault(n => string.Equals(n.Name, name, StringComparison.Ordinal))
        ?? throw new InvalidOperationException($"node '{name}' not materialized");
}
