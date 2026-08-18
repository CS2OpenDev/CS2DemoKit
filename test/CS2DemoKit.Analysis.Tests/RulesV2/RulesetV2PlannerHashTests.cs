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
///     The corruption-class property gate for the planner: node
///     dedup is driven by the resolved-identity hasher. Hash-<b>equal</b> stats
///     compile to <b>one shared node</b> (so they evaluate identically — the same node cannot hold
///     two values); hash-<b>distinct</b> stats compile to <b>separate nodes</b>. The distinctness
///     axes that would otherwise false-share (the corruption class) are pinned: a per-player
///     <c>per: round</c> stat and its <c>per: match</c> twin differ only in the compound scope axis
///     (decision 5) and MUST NOT dedup. Demo-free — the per-player template materializes against a
///     null demo.
/// </summary>
[Category("Unit")]
public class RulesetV2PlannerHashTests
{
    private const string DedupYaml = """
                                     ruleset: dedup_probe
                                     for: each_player
                                     stats:
                                       tick_a:
                                         capture: event.tick
                                         on: bomb_planted
                                         per: round
                                       tick_b:
                                         capture: event.tick
                                         on: bomb_planted
                                         per: round
                                       tick_match:
                                         capture: event.tick
                                         on: bomb_planted
                                         per: match
                                     """;

    // Three buckets over the same trigger (damage_dealt) + key (event.Weapon): a capped-damage SUM,
    // a raw-damage SUM, and a plain COUNT. The two sums differ ONLY in value:, so the value: selector
    // is the sole thing that can keep them apart — the false-dedup corruption class (the Min-only
    // tally bug's bucket analogue).
    private const string BucketSumYaml = """
                                         ruleset: bucket_sum_probe
                                         for: each_player
                                         stats:
                                           dmg_capped:
                                             bucket: damage_dealt
                                             key: event.Weapon
                                             value: enrich.hurt.capped_damage
                                             per: match
                                           dmg_raw:
                                             bucket: damage_dealt
                                             key: event.Weapon
                                             value: event.DmgHealth
                                             per: match
                                           dmg_count:
                                             bucket: damage_dealt
                                             key: event.Weapon
                                             per: match
                                         """;

    // Three counts over player_death, all per: round: two `count: kill` (killer actor) and one
    // `count: assist` (assister actor). The kill and assist views share EVERY hashed row except the
    // view actor-role binding (preimage row 10) — same kind (count), value type, scope, concrete
    // events (player_death), and baked trigger (Attacker != UserId). Before row 10 they
    // false-dedup onto one node and the assist edge is silently dropped (v2 assists == kills). The
    // two `count: kill` stats differ ONLY in id, so they must still share (intended cross-name dedup).
    private const string ActorRoleYaml = """
                                         ruleset: actor_probe
                                         for: each_player
                                         stats:
                                           kills:
                                             count: kill
                                             per: round
                                           kills_twin:
                                             count: kill
                                             per: round
                                           assists:
                                             count: assist
                                             per: round
                                         """;

    // Two buckets over the same trigger + key + value, differing ONLY in reduce: (max vs. sum). The
    // reducer name is the sole identity discriminator — a false dedup would collapse a max onto a sum.
    private const string BucketReduceYaml = """
                                            ruleset: bucket_reduce_probe
                                            for: each_player
                                            stats:
                                              hp_max:
                                                bucket: damage_dealt
                                                key: event.Weapon
                                                value: event.DmgHealth
                                                reduce: max
                                                per: match
                                              hp_sum:
                                                bucket: damage_dealt
                                                key: event.Weapon
                                                value: event.DmgHealth
                                                reduce: sum
                                                per: match
                                            """;

    /// <summary>
    ///     The actor-role corruption class: a <c>count: kill</c> (actor = killer) and a
    ///     <c>count: assist</c> (actor = assister) are identical on every hashed row except the view's
    ///     actor-role binding (preimage row 10). They MUST hash apart and materialize to SEPARATE nodes
    ///     — otherwise the resolved-identity dedup collapses them onto one counter and the assist edge
    ///     is silently dropped (v2 Assists comes out == Kills). Two structurally-identical same-view
    ///     stats (differ only in id) must still hash EQUAL and share one node, so the fix ONLY adds
    ///     discrimination and does not break intended cross-name dedup.
    /// </summary>
    [Test]
    public async Task ActorRole_IsPartOfIdentity_NoFalseDedup_SameViewStillShares()
    {
        CheckedRuleset rs = Compile(ActorRoleYaml);
        MapStatHashSource source = new(new Dictionary<string, ReadOnlyMemory<byte>>());
        CheckedStat kills = rs.Stats.Single(s => s.StatId == "kills");
        CheckedStat killsTwin = rs.Stats.Single(s => s.StatId == "kills_twin");
        CheckedStat assists = rs.Stats.Single(s => s.StatId == "assists");

        // The two views resolve to different actor roles; the assist view supplies its own actor.
        await Assert.That(kills.ResolvedView).IsEqualTo("kill");
        await Assert.That(assists.ResolvedView).IsEqualTo("assist");

        // Hasher property (the fix): different actor role -> distinct hash; same view -> equal hash.
        await Assert.That(Hex(kills, source)).IsNotEqualTo(Hex(assists, source))
            .Because("count: kill (actor = killer) and count: assist (actor = assister) bind different "
                     + "slots — row 10 must keep them apart even though rows 1-9 are identical");
        await Assert.That(Hex(kills, source)).IsEqualTo(Hex(killsTwin, source))
            .Because("two count: kill stats differing only in id must still dedup (cross-name sharing)");

        // Planner property: hash-distinct -> separate nodes; hash-equal -> one shared node.
        Dictionary<string, StateNode> nodes = Materialize(rs);
        StateNode nodeKills = nodes["actor_probe.kills"];
        StateNode nodeKillsTwin = nodes["actor_probe.kills_twin"];
        StateNode nodeAssists = nodes["actor_probe.assists"];

        await Assert.That(ReferenceEquals(nodeKills, nodeAssists)).IsFalse()
            .Because("kill and assist must be distinct nodes (else the assist edge is silently dropped)");
        await Assert.That(ReferenceEquals(nodeKills, nodeKillsTwin)).IsTrue()
            .Because("two structurally-identical count: kill stats must dedup onto the SAME node");
    }

    /// <summary>Two structurally-identical stats share one node; a scope-different twin does not.</summary>
    [Test]
    public async Task HashEqualStats_ShareOneNode_ScopeTwinStaysSeparate()
    {
        CheckedRuleset rs = Compile(DedupYaml);
        MapStatHashSource source = new(new Dictionary<string, ReadOnlyMemory<byte>>());
        CheckedStat a = rs.Stats.Single(s => s.StatId == "tick_a");
        CheckedStat b = rs.Stats.Single(s => s.StatId == "tick_b");
        CheckedStat m = rs.Stats.Single(s => s.StatId == "tick_match");

        // Hasher property: identical structure -> identical hash; scope twin -> distinct hash.
        await Assert.That(Hex(a, source)).IsEqualTo(Hex(b, source))
            .Because("tick_a and tick_b are structurally identical");
        await Assert.That(Hex(a, source)).IsNotEqualTo(Hex(m, source))
            .Because("a per: round stat and its per: match twin differ in the compound scope axis (decision 5)");

        // Planner property: hash-equal -> one shared node; hash-distinct -> separate node.
        Dictionary<string, StateNode> nodes = Materialize(rs);
        StateNode nodeA = nodes["dedup_probe.tick_a"];
        StateNode nodeB = nodes["dedup_probe.tick_b"];
        StateNode nodeM = nodes["dedup_probe.tick_match"];

        await Assert.That(ReferenceEquals(nodeA, nodeB)).IsTrue()
            .Because("hash-equal stats must dedup onto the SAME node (identical evaluation)");
        await Assert.That(ReferenceEquals(nodeA, nodeM)).IsFalse()
            .Because("the per: match twin must be a distinct node");
    }

    /// <summary>
    ///     C8 single-value SUM bucket, end-to-end through the resolver + planner: a summing bucket's
    ///     <c>value:</c> is part of node identity. Two sum buckets over the same trigger + key but a
    ///     DIFFERENT value: expression must hash apart AND materialize to SEPARATE nodes (else the
    ///     resolved-identity dedup collapses them onto one <c>KeyedCounterNode</c> = silent
    ///     corruption); a count bucket stays distinct from both. This proves the resolver actually
    ///     carries the value selector into the hash preimage (not just the descriptor-level hasher).
    /// </summary>
    [Test]
    public async Task BucketSumValue_IsPartOfIdentity_NoFalseDedup()
    {
        CheckedRuleset rs = Compile(BucketSumYaml);
        MapStatHashSource source = new(new Dictionary<string, ReadOnlyMemory<byte>>());
        CheckedStat capped = rs.Stats.Single(s => s.StatId == "dmg_capped");
        CheckedStat raw = rs.Stats.Single(s => s.StatId == "dmg_raw");
        CheckedStat count = rs.Stats.Single(s => s.StatId == "dmg_count");

        // The reducer + value selector are set only on the sum buckets.
        await Assert.That(capped.BucketReducer).IsEqualTo("sum");
        await Assert.That(count.BucketReducer).IsNull();

        // Hasher property: the value selector splits identity.
        await Assert.That(Hex(capped, source)).IsNotEqualTo(Hex(raw, source))
            .Because("two sum buckets summing different amounts must hash apart (no false dedup)");
        await Assert.That(Hex(capped, source)).IsNotEqualTo(Hex(count, source))
            .Because("a sum bucket and a count bucket over the same trigger + key must hash apart");

        // Planner property: hash-distinct -> separate nodes (no shared KeyedCounterNode).
        Dictionary<string, StateNode> nodes = Materialize(rs);
        StateNode nodeCapped = nodes["bucket_sum_probe.dmg_capped"];
        StateNode nodeRaw = nodes["bucket_sum_probe.dmg_raw"];
        StateNode nodeCount = nodes["bucket_sum_probe.dmg_count"];

        await Assert.That(ReferenceEquals(nodeCapped, nodeRaw)).IsFalse()
            .Because("different-value sum buckets must be distinct nodes");
        await Assert.That(ReferenceEquals(nodeCapped, nodeCount)).IsFalse()
            .Because("a sum bucket and a count bucket must be distinct nodes");
    }

    /// <summary>
    ///     C8 named reducers, end-to-end: a <c>reduce: max</c> and a <c>reduce: sum</c> over the same
    ///     trigger + key + value carry distinct <see cref="CheckedStat.BucketReducer" /> names, hash apart
    ///     (no false dedup), materialize to SEPARATE nodes, and the max node's engine
    ///     <see cref="KeyedCounterNode.ReduceMode" /> is <see cref="KeyedReduceMode.Max" /> — proving the
    ///     resolved reducer threads all the way into the built node.
    /// </summary>
    [Test]
    public async Task BucketReducer_IsPartOfIdentity_ThreadsToNode()
    {
        CheckedRuleset rs = Compile(BucketReduceYaml);
        MapStatHashSource source = new(new Dictionary<string, ReadOnlyMemory<byte>>());
        CheckedStat max = rs.Stats.Single(s => s.StatId == "hp_max");
        CheckedStat sum = rs.Stats.Single(s => s.StatId == "hp_sum");

        await Assert.That(max.BucketReducer).IsEqualTo("max");
        await Assert.That(sum.BucketReducer).IsEqualTo("sum");
        await Assert.That(Hex(max, source)).IsNotEqualTo(Hex(sum, source))
            .Because("a max bucket and a sum bucket over the same key + value must hash apart");

        Dictionary<string, StateNode> nodes = Materialize(rs);
        StateNode nodeMax = nodes["bucket_reduce_probe.hp_max"];
        StateNode nodeSum = nodes["bucket_reduce_probe.hp_sum"];

        await Assert.That(ReferenceEquals(nodeMax, nodeSum)).IsFalse()
            .Because("different-reducer buckets must be distinct nodes");
        await Assert.That(((KeyedCounterNode)nodeMax).ReduceMode).IsEqualTo(KeyedReduceMode.Max);
        await Assert.That(((KeyedCounterNode)nodeSum).ReduceMode).IsEqualTo(KeyedReduceMode.Add);
    }

    private static string Hex(CheckedStat stat, MapStatHashSource source) =>
        Convert.ToHexStringLower(V2StatHasher.Hash(stat, source));

    private static CheckedRuleset Compile(string yaml)
    {
        RulesetDoc doc = RulesetDocumentLoader.Load(yaml, "test.rules.yaml").Doc
                         ?? throw new InvalidOperationException("test ruleset failed to map");
        CatalogScopeAdapter adapter = CatalogScopeAdapter.From(CatalogResource.Load());
        RulesetResolveResult resolved = CheckedRulesetDraft.Load(doc, adapter).Build(64.0, "Cs2GotvProfile");
        return resolved.Ruleset
               ?? throw new InvalidOperationException(
                   "test ruleset failed to resolve: " + string.Join("; ", resolved.Diagnostics));
    }

    private static Dictionary<string, StateNode> Materialize(CheckedRuleset rs)
    {
        RuleChainBuilder builder = new(EventRegistry.Build());
        BuildResult build = builder.Build([rs]);

        // The graph carries both the v1 built-in per-player context template and the v2 template;
        // merge their NodesByRuleId (the v2 qualified keys live on the v2 one).
        Dictionary<string, StateNode> merged = new(StringComparer.OrdinalIgnoreCase);
        foreach (PerPlayerNodeTemplate template in build.Graph.PerPlayerTemplates)
        {
            PerPlayerNodeTemplate.MaterializedPlayer player = template.Materialize(0, 0, "test", null);
            if (player.NodesByRuleId is { } byId)
            {
                foreach ((string key, StateNode node) in byId)
                {
                    merged[key] = node;
                }
            }
        }

        return merged;
    }
}
