#region

using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Analysis.Building;
using CS2DemoKit.Analysis.Catalog;
using CS2DemoKit.Analysis.Config;
using CS2DemoKit.Analysis.Edges;
using CS2DemoKit.Analysis.Graphs;
using CS2DemoKit.Analysis.Nodes;
using CS2DemoKit.Analysis.Registry;
using CS2DemoKit.Analysis.Rules;
using CS2DemoKit.Analysis.Rules.Hashing;
using CS2DemoKit.Analysis.Rules.Scopes;
using CS2DemoKit.Analysis.RulesetsV2.Model;
using CS2DemoKit.Analysis.RulesetsV2.Resolve;
using CS2DemoKit.Analysis.Yaml;
using CS2DemoKit.Parser.GameEvents;

#endregion

using CS2DemoKit.TestSupport;

namespace CS2DemoKit.Analysis.Tests.RulesV2;

/// <summary>
///     B6 team aggregates (<c>round.team.*</c> / <c>round.enemies.*</c> /
///     <c>round.alive.*</c>). Covers the single-writer design (docs/rules-v2/rule-authoring-ux-review.md
///     §3.3 risk 1): the alive/count aggregates are per-player nodes recomputed live from the shared
///     <see cref="PlayerContextIndex" /> (no second store), and the clutch enrichment is exposed as a
///     typed facet. Verifies the nodes' recompute logic, the catalog scope + typing, that a v2
///     <c>compute:</c> / <c>while:</c> read resolves + types + lowers, and that an unknown member is
///     an attributed error. Demo-free.
/// </summary>
[Category("Unit")]
public class B6TeamAggregateTests
{
    private const string Gotv = "Cs2GotvProfile";
    private static readonly CatalogScopeAdapter _adapter = CatalogScopeAdapter.From(CatalogResource.Load());

    private static PlayerContextIndex FiveVsFive()
    {
        PlayerContextIndex index = new();
        for (int slot = 0; slot < 5; slot++)
        {
            index.Register(slot, new PlayerContextIndex.PlayerContext(slot, 2)); // T
        }

        for (int slot = 5; slot < 10; slot++)
        {
            index.Register(slot, new PlayerContextIndex.PlayerContext(slot, 3)); // CT
        }

        return index;
    }

    // ── Node recompute (decision a: recomputed from PlayerContextIndex, no second store) ──

    /// <summary>The aggregate reflects deaths within a round live (single writer = MarkDead).</summary>
    [Test]
    public async Task TeamAlive_RecomputesFromIndex_AcrossKills()
    {
        PlayerContextIndex index = FiveVsFive();
        // Subject is a T (slot 0); enemies are CT.
        RoundTeamAggregateNode teamAlive = new("round_team_alive", index, 0, RoundTeamAggregateNode.AggregateKind.TeamAlive);
        RoundTeamAggregateNode enemyAlive = new("round_enemies_alive", index, 0, RoundTeamAggregateNode.AggregateKind.EnemyAlive);

        await Assert.That(teamAlive.Value).IsEqualTo(5);
        await Assert.That(enemyAlive.Value).IsEqualTo(5);

        index.MarkDead(1); // a teammate dies
        index.MarkDead(5); // an enemy dies
        await Assert.That(teamAlive.Value).IsEqualTo(4);
        await Assert.That(enemyAlive.Value).IsEqualTo(4);

        index.ResetRoundState(); // next round: all alive again
        await Assert.That(teamAlive.Value).IsEqualTo(5);
        await Assert.That(enemyAlive.Value).IsEqualTo(5);
    }

    /// <summary>Player-count aggregates are Connected-gated (disconnect-aware).</summary>
    [Test]
    public async Task TeamPlayers_ExcludesDisconnected()
    {
        PlayerContextIndex index = FiveVsFive();
        RoundTeamAggregateNode teamPlayers = new("round_team_players", index, 0, RoundTeamAggregateNode.AggregateKind.TeamPlayers);
        RoundTeamAggregateNode teamAlive = new("round_team_alive", index, 0, RoundTeamAggregateNode.AggregateKind.TeamAlive);

        await Assert.That(teamPlayers.Value).IsEqualTo(5);

        index.MarkDisconnected(1);
        index.ResetRoundState(); // resurrects IsAlive, but Connected stays cleared
        await Assert.That(teamPlayers.Value).IsEqualTo(4);
        await Assert.That(teamAlive.Value).IsEqualTo(4);
    }

    /// <summary>The subject's CURRENT team is read live, so a halftime side-swap flips team↔enemies.</summary>
    [Test]
    public async Task Aggregate_FollowsHalftimeTeamSwap()
    {
        PlayerContextIndex index = FiveVsFive();
        RoundTeamAggregateNode teamAlive = new("round_team_alive", index, 0, RoundTeamAggregateNode.AggregateKind.TeamAlive);

        index.MarkDead(5); // one CT down; subject (T) team still 5 alive (incl. the subject)
        await Assert.That(teamAlive.Value).IsEqualTo(index.CountAlive(2));

        // Halftime: the subject swaps to CT (team 3). The node reads GetCurrentTeam live, so it must
        // now report the CT population, not the frozen T population.
        index.TryGet(0, out PlayerContextIndex.PlayerContext? ctx);
        ctx!.Team = 3;
        await Assert.That(teamAlive.Value).IsEqualTo(index.CountAlive(3));
        await Assert.That(teamAlive.Value).IsNotEqualTo(index.CountAlive(2))
            .Because("the aggregate must follow the subject's CURRENT team after the swap");
    }

    /// <summary>The clutch facet reflects the subject's live IsInClutch (Connected-gated).</summary>
    [Test]
    public async Task ClutchFacet_ReflectsInClutchFlag()
    {
        PlayerContextIndex index = FiveVsFive();
        RoundClutchFacetNode facet = new("round_alive_in_clutch", index, 0);

        await Assert.That(facet.Value).IsFalse();

        index.TryGet(0, out PlayerContextIndex.PlayerContext? ctx);
        ctx!.IsInClutch = true;
        await Assert.That(facet.Value).IsTrue();

        index.MarkDisconnected(0); // a disconnected ghost never reports a clutch
        await Assert.That(facet.Value).IsFalse();
    }

    // ── Freeze-end economy (decision c) ─────────────────────────────────────────

    /// <summary>
    ///     The freeze-end economy fold sums each connected player's equipment by team, relative to the
    ///     subject: <c>round.team.equipment</c> is the subject's team, <c>round.enemies.equipment</c>
    ///     the opposing team. Disconnected players are excluded.
    /// </summary>
    [Test]
    public async Task Economy_SumsByTeam_ExcludingDisconnected()
    {
        PlayerContextIndex index = FiveVsFive();

        // Equipment: every T (slots 0-4) has 1000, every CT (slots 5-9) has 2000.
        int Equip(int slot)
        {
            return slot < 5 ? 1000 : 2000;
        }

        // Subject slot 0 is a T: team sum = 5×1000, enemy sum = 5×2000.
        (int team, int enemies) = PlayerEconomyFreezeEndEdge.ComputeSums(index, 0, Equip);
        await Assert.That(team).IsEqualTo(5000);
        await Assert.That(enemies).IsEqualTo(10000);

        // A disconnected teammate drops out of the subject's team sum.
        index.MarkDisconnected(1);
        (team, enemies) = PlayerEconomyFreezeEndEdge.ComputeSums(index, 0, Equip);
        await Assert.That(team).IsEqualTo(4000);
        await Assert.That(enemies).IsEqualTo(10000);
    }

    /// <summary>The economy fold is subject-relative: an enemy subject sees the sums mirrored.</summary>
    [Test]
    public async Task Economy_IsSubjectRelative()
    {
        PlayerContextIndex index = FiveVsFive();

        int Equip(int slot)
        {
            return slot < 5 ? 1000 : 2000;
        }

        // Subject slot 5 is a CT: team sum = 5×2000, enemy sum = 5×1000 (mirror of the T subject).
        (int team, int enemies) = PlayerEconomyFreezeEndEdge.ComputeSums(index, 5, Equip);
        await Assert.That(team).IsEqualTo(10000);
        await Assert.That(enemies).IsEqualTo(5000);
    }

    /// <summary>The written node reflects the freeze-end sum after the edge applies.</summary>
    [Test]
    public async Task Economy_WrittenNode_HoldsFreezeEndSum()
    {
        PlayerContextIndex index = FiveVsFive();

        int Equip(int slot)
        {
            return slot < 5 ? 800 : 3200;
        }

        GenericValueNode<int> teamEquip = new("round_team_equipment", "test");
        GenericValueNode<int> enemyEquip = new("round_enemies_equipment", "test");

        PlayerEconomyFreezeEndEdge edge = new(
            new GenericBoolNode("root"), index, Equip, 0, teamEquip, enemyEquip);
        GameEventMessage msg = GameEventMessage.ForSynthesizedEvent(
            TestGameEvents.RoundFreezeEnd(eventId: 0));
        edge.TryApply(new EvaluationContext(msg, null!));

        await Assert.That(teamEquip.Value).IsEqualTo(4000); // 5 T × 800
        await Assert.That(enemyEquip.Value).IsEqualTo(16000); // 5 CT × 3200
    }

    /// <summary>A compute reading round.team.equipment resolves + types + declares the read.</summary>
    [Test]
    public async Task Compute_ReadingTeamEquipment_Resolves()
    {
        const string Yaml = """
                            ruleset: b6_econ_probe
                            for: each_player
                            stats:
                              econ_delta:
                                compute: "round.team.equipment - round.enemies.equipment"
                            """;

        RulesetResolveResult result = Build(Yaml);
        await Assert.That(result.Success).IsTrue();
        CheckedStat stat = result.Ruleset!.Stats.Single(s => s.StatId == "econ_delta");
        await Assert.That(stat.DeclaredReads.Contains("round.team.equipment")).IsTrue();
        await Assert.That(stat.DeclaredReads.Contains("round.enemies.equipment")).IsTrue();
    }

    // ── Catalog scope + typing ──────────────────────────────────────────────────

    /// <summary>The catalog exposes the B6 members under round.* with the right types.</summary>
    [Test]
    public async Task Catalog_ExposesB6Members_WithTypes()
    {
        await Assert.That(MemberType("round", "team", "alive")).IsEqualTo(RulesType.Int);
        await Assert.That(MemberType("round", "team", "players")).IsEqualTo(RulesType.Int);
        await Assert.That(MemberType("round", "team", "equipment")).IsEqualTo(RulesType.Int);
        await Assert.That(MemberType("round", "enemies", "alive")).IsEqualTo(RulesType.Int);
        await Assert.That(MemberType("round", "enemies", "players")).IsEqualTo(RulesType.Int);
        await Assert.That(MemberType("round", "enemies", "equipment")).IsEqualTo(RulesType.Int);
        await Assert.That(MemberType("round", "alive", "in_clutch")).IsEqualTo(RulesType.Bool);
    }

    private static RulesType MemberType(string root, string ns, string leaf)
    {
        if (!string.Equals(root, "round", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("only round root tested");
        }

        IScopeSymbol scope = _adapter.Round;
        if (!scope.TryGetMember(ns, out IScopeSymbol? nsSym) || !nsSym!.TryGetMember(leaf, out IScopeSymbol? leafSym))
        {
            throw new InvalidOperationException($"round.{ns}.{leaf} not in scope");
        }

        return leafSym!.ValueType ?? throw new InvalidOperationException($"round.{ns}.{leaf} has no value type");
    }

    // ── Resolve + type + declared reads ─────────────────────────────────────────

    /// <summary>A compute reading the int aggregates resolves, types Float, and declares both reads.</summary>
    [Test]
    public async Task Compute_ReadingTeamAndEnemyAlive_Resolves()
    {
        const string Yaml = """
                            ruleset: b6_probe
                            for: each_player
                            stats:
                              alive_advantage:
                                compute: "round.team.alive - round.enemies.alive"
                            """;

        RulesetResolveResult result = Build(Yaml);
        await Assert.That(result.Success).IsTrue();
        CheckedStat stat = result.Ruleset!.Stats.Single(s => s.StatId == "alive_advantage");
        await Assert.That(stat.Kind).IsEqualTo(RuleNodeKind.Compute);
        await Assert.That(stat.DeclaredReads.Contains("round.team.alive")).IsTrue();
        await Assert.That(stat.DeclaredReads.Contains("round.enemies.alive")).IsTrue();
    }

    /// <summary>A while: gate on the clutch facet resolves and types Bool.</summary>
    [Test]
    public async Task WhileGate_OnClutchFacet_Resolves()
    {
        const string Yaml = """
                            ruleset: b6_clutch_probe
                            for: each_player
                            stats:
                              clutch_kills:
                                count: kill
                                while: round.alive.in_clutch
                                per: round
                            """;

        RulesetResolveResult result = Build(Yaml);
        await Assert.That(result.Success).IsTrue();
        CheckedStat stat = result.Ruleset!.Stats.Single(s => s.StatId == "clutch_kills");
        await Assert.That(stat.WhileGate!.Root.CanonicalText).IsEqualTo("(ref round.alive.in_clutch)");
    }

    /// <summary>An unknown member under a B6 namespace is an attributed error.</summary>
    [Test]
    public async Task UnknownB6Member_Errors_WithPosition()
    {
        const string Yaml = """
                            ruleset: b6_bad
                            for: each_player
                            stats:
                              bad:
                                compute: "round.team.bogus + 1"
                            """;

        RulesetResolveResult result = Build(Yaml);
        await Assert.That(result.Success).IsFalse();
        RulesetDiagnostic error = result.Diagnostics.First(d => d.Code == DiagnosticCodes.UnknownMember);
        await Assert.That(error.Message.Contains("bogus", StringComparison.Ordinal)).IsTrue();
        await Assert.That(error.Position.Line).IsGreaterThan(0);
    }

    // ── Lowering (resolves → graph nodes) ───────────────────────────────────────

    /// <summary>
    ///     A compute reading the int aggregates and a count gated on the clutch facet both lower: the
    ///     backing per-player nodes (a <see cref="RoundTeamAggregateNode" /> and a
    ///     <see cref="RoundClutchFacetNode" />) are materialized into the graph.
    /// </summary>
    [Test]
    public async Task B6Reads_Lower_ToAggregateNodes()
    {
        const string Yaml = """
                            ruleset: b6_lower
                            for: each_player
                            stats:
                              alive_advantage:
                                compute: "round.team.alive - round.enemies.alive"
                              clutch_kills:
                                count: kill
                                while: round.alive.in_clutch
                                per: round
                            """;

        CheckedRuleset rs = Compile(Yaml);
        List<StateNode> nodes = MaterializeNodes(rs);

        await Assert.That(nodes.OfType<RoundTeamAggregateNode>().Any(n => n.Name == "round_team_alive")).IsTrue();
        await Assert.That(nodes.OfType<RoundTeamAggregateNode>().Any(n => n.Name == "round_enemies_alive")).IsTrue();
        await Assert.That(nodes.OfType<RoundClutchFacetNode>().Any(n => n.Name == "round_alive_in_clutch")).IsTrue();
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    private static RulesetDoc Doc(string yaml)
    {
        RulesetDocumentLoader.Outcome outcome = RulesetDocumentLoader.Load(yaml, "test.rules.yaml");
        return outcome.Doc ?? throw new InvalidOperationException(
            $"YAML failed to map: {string.Join("; ", outcome.Diagnostics)}");
    }

    private static RulesetResolveResult Build(string yaml) =>
        CheckedRulesetDraft.Load(Doc(yaml), _adapter).Build(64.0, Gotv);

    private static CheckedRuleset Compile(string yaml)
    {
        RulesetResolveResult result = Build(yaml);
        return result.Success
            ? result.Ruleset!
            : throw new InvalidOperationException(
                $"resolve failed: {string.Join("; ", result.Diagnostics.Select(d => d.Message))}");
    }

    private static List<StateNode> MaterializeNodes(CheckedRuleset rs)
    {
        RuleChainBuilder builder = new(EventRegistry.Build());
        BuildResult build = builder.Build([rs]);
        List<StateNode> all = [];
        foreach (PerPlayerNodeTemplate template in build.Graph.PerPlayerTemplates)
        {
            PerPlayerNodeTemplate.MaterializedPlayer player = template.Materialize(0, 0, "test", null);
            all.AddRange(player.Nodes);
        }

        return all;
    }
}
