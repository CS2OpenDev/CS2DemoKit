#region

using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Analysis.Graphs;
using CS2DemoKit.Analysis.Nodes;
using CS2DemoKit.Parser;
using CS2DemoKit.TestSupport;

#endregion

namespace CS2DemoKit.Analysis.Tests.RulesV2;

/// <summary>
///     Fix golden for pre-freeze gap G4 (B6 aggregate / per-player pull-nodes as
///     <c>while:</c> and <c>when:</c> gate sources). Before the fix, a B6 live pull-node
///     (<see cref="RoundTeamAggregateNode" /> / <see cref="RoundClutchFacetNode" />) — always
///     <see cref="StateNode.IsActive" />, value surfaced only through a reflectively-read
///     <c>Value</c> — made a <c>while:</c> gate a silent no-op (the parent-as-edge-source
///     activation never restricts) and made a <c>flag: when:</c> over the clutch facet throw a
///     ctor <see cref="MissingMethodException" /> (<c>ConditionalEdge&lt;object&gt;</c> cannot bind
///     a non-<see cref="ValueNode{T}" /> source). This exercises every fixed site against the
///     reference demo.
///     <para>Parses the demo, so <see cref="NotInParallelAttribute" /> and the shared parse cache apply.</para>
/// </summary>
[Category("Integration")]
[NotInParallel]
public class G4WhilePullNodeGateTests
{
    private const string Yaml = """
                                ruleset: g4_pull_node_gate
                                for: each_player
                                stats:
                                  kills_ungated:
                                    count: kill
                                    per: match
                                  kills_while_enemies:
                                    count: kill
                                    while: round.enemies.alive > 0
                                    per: match
                                  kills_where_enemies:
                                    count: kill
                                    where: "round.enemies.alive > 0"
                                    per: match
                                  kills_while_alive:
                                    count: kill
                                    while: player.alive
                                    per: match
                                  kills_while_clutch:
                                    count: kill
                                    while: round.alive.in_clutch
                                    per: match
                                  kills_where_clutch:
                                    count: kill
                                    where: "round.alive.in_clutch == true"
                                    per: match
                                  in_clutch_flag:
                                    flag:
                                      when: "round.alive.in_clutch"
                                    per: match
                                  survived_flag:
                                    flag:
                                      when: "player.survived"
                                    per: match
                                """;

    [Test]
    public async Task PullNodeGates_WhileRestricts_AndWhenBuilds()
    {
        ParsedDemo demo = DemoTestHelper.GetOrParse(DemoTestHelper.RequireDemo());

        // The build itself is the primary `when:` regression guard: a `flag: when: round.alive.in_clutch`
        // used to throw MissingMethodException here. Reaching Evaluate proves it now builds.
        BuildResult build = V2KindGoldenSupport.CompileV2(demo, Yaml);
        AnalysisRun run = DemoAnalysis.Evaluate(demo, build);

        Dictionary<int, int> ungated = ReadCounter(run, "kills_ungated");
        Dictionary<int, int> whileEnemies = ReadCounter(run, "kills_while_enemies");
        Dictionary<int, int> whereEnemies = ReadCounter(run, "kills_where_enemies");
        Dictionary<int, int> whileAlive = ReadCounter(run, "kills_while_alive");
        Dictionary<int, int> whileClutch = ReadCounter(run, "kills_while_clutch");
        Dictionary<int, int> whereClutch = ReadCounter(run, "kills_where_clutch");

        int ungatedTotal = ungated.Values.Sum();
        int whileEnemiesTotal = whileEnemies.Values.Sum();
        Console.WriteLine($"[G4] totals: ungated={ungatedTotal} while(enemies>0)={whileEnemiesTotal} "
                          + $"where(enemies>0)={whereEnemies.Values.Sum()} while(alive)={whileAlive.Values.Sum()} "
                          + $"while(clutch)={whileClutch.Values.Sum()} where(clutch)={whereClutch.Values.Sum()}");

        foreach (int slot in ungated.Keys)
        {
            // (1) while: over a B6 INT pull-node gates identically to the G1-proven where: read of the
            // same aggregate — the value predicate is genuinely evaluated at fire time, not no-op'd.
            await Assert.That(whileEnemies.GetValueOrDefault(slot)).IsEqualTo(whereEnemies.GetValueOrDefault(slot))
                .Because($"slot{slot}: while: round.enemies.alive > 0 must gate == the where: read of it");

            // (2) A per-player context (player.alive) while-gate still works: the subject is always alive
            // at their own kill, so the alive-gated count equals the ungated total (regression guard).
            await Assert.That(whileAlive.GetValueOrDefault(slot)).IsEqualTo(ungated.GetValueOrDefault(slot))
                .Because($"slot{slot}: player.alive is always true at the subject's own kill");

            // (3) while: over the B6 BOOL facet (bare ref -> `value == true`) gates identically to the
            // where: comparison of the same facet — proving the clutch conditional edge activates on the
            // subject's live clutch state (the same edge the flag: when: builds).
            await Assert.That(whileClutch.GetValueOrDefault(slot)).IsEqualTo(whereClutch.GetValueOrDefault(slot))
                .Because($"slot{slot}: while: round.alive.in_clutch must gate == where: round.alive.in_clutch == true");

            // The enemies-gate is a subset of all kills (it can only exclude round-winning kills).
            await Assert.That(whileEnemies.GetValueOrDefault(slot)).IsLessThanOrEqualTo(ungated.GetValueOrDefault(slot))
                .Because($"slot{slot}: an enemies.alive>0 gate can only drop kills, never add them");
        }

        // (4) The gate genuinely FIRES: round-winning kills empty the enemy team (enemies.alive -> 0 at that
        // kill's fire), so the while-gated total is strictly below the ungated total. A no-op gate (the
        // pre-fix behaviour) would leave them equal.
        await Assert.That(ungatedTotal).IsGreaterThan(0).Because("the reference demo has kills");
        await Assert.That(whileEnemiesTotal).IsGreaterThan(0).Because("most kills happen with an enemy still alive");
        await Assert.That(whileEnemiesTotal).IsLessThan(ungatedTotal)
            .Because("round-winning kills drop enemies.alive to 0, so the while: gate excludes them");

        // (5) The `flag: when:` nodes over B6 / per-player pull-nodes were built (no MissingMethodException).
        await Assert.That(NodeExists(run, "in_clutch_flag")).IsTrue()
            .Because("flag: when: round.alive.in_clutch must build a logic node, not throw a ctor mismatch");
        await Assert.That(NodeExists(run, "survived_flag")).IsTrue()
            .Because("flag: when: player.survived must build a logic node");
    }

    /// <summary>Reads a match-scoped counter node's per-player final value into slot → count.</summary>
    private static Dictionary<int, int> ReadCounter(AnalysisRun run, string nodeName)
    {
        EvaluationResult result = run.Snapshots
                                  ?? throw new InvalidOperationException("evaluation produced no snapshots");
        Dictionary<int, int> byslot = new();
        foreach (PerPlayerNodeTemplate.MaterializedPlayer mp in result.MaterializedPlayers)
        {
            GenericValueNode<int>? node = mp.Nodes
                .OfType<GenericValueNode<int>>()
                .FirstOrDefault(n => string.Equals(n.Name, nodeName, StringComparison.Ordinal));
            byslot[mp.PlayerSlot] = node?.Value ?? 0;
        }

        return byslot;
    }

    /// <summary>True when at least one materialized player carries a graph node with the given name.</summary>
    private static bool NodeExists(AnalysisRun run, string nodeName)
    {
        EvaluationResult result = run.Snapshots
                                  ?? throw new InvalidOperationException("evaluation produced no snapshots");
        return result.MaterializedPlayers.Any(mp =>
            mp.Nodes.Any(n => string.Equals(n.Name, nodeName, StringComparison.Ordinal)));
    }
}
