#region

using CS2DemoKit.Analysis.Graphs;
using CS2DemoKit.Analysis.Nodes;
using CS2DemoKit.Parser;
using CS2DemoKit.TestSupport;

#endregion

namespace CS2DemoKit.Analysis.Tests.RulesV2;

/// <summary>
///     Fix golden for pre-freeze gap G1 (event-gated per-player aggregate reads): a per-player
///     context (<c>player.alive</c>) AND a B6 aggregate (<c>round.team.alive</c> /
///     <c>round.enemies.alive</c>) read inside a stat's <c>where:</c> event-condition must bind the
///     SUBJECT player's per-slot value — not throw (the pre-fix behaviour, see
///     <see cref="G1WhereContextConfirmTests" />), and not read a wrong/global value.
///     <para>
///         Two independent subject-binding proofs on the reference demo:
///     </para>
///     <list type="number">
///         <item>
///             <b>Per-player context.</b> Gating a <c>count: kill</c> by <c>where: player.alive</c>
///             and by <c>while: player.alive</c> (which <c>ResolveGateSource</c> binds to the subject
///             slot's node) must give identical per-player counts — the where: read binds the same
///             subject as the known-correct gate.
///         </item>
///         <item>
///             <b>B6 aggregate.</b> At the tick a player gets a kill they are alive on their own team,
///             so the subject-relative <c>round.team.alive</c> is always ≥ 1. Hence
///             <c>count: kill where: "round.team.alive &gt; 0"</c> must equal the player's total kills
///             (== the <c>player.alive</c> count). If the B6 read bound a fixed/global slot, an
///             enemy-team wipe would drop that slot's team.alive to 0 and the count would diverge —
///             so equality is a genuine subject-binding check. <c>round.enemies.alive</c> is logged
///             too: it is NOT always > 0 (the round-winning kill empties the enemy team), so a lower
///             enemies-count total proves the aggregate VALUE is actually read, not short-circuited.
///         </item>
///     </list>
///     Parses the demo, so <see cref="NotInParallelAttribute" /> and the shared parse cache apply.
/// </summary>
[Category("Integration")]
[NotInParallel]
public class G1WhereContextSubjectBindingTests
{
    private const string Yaml = """
                                ruleset: g1_subject_binding
                                for: each_player
                                stats:
                                  alive_kills_where:
                                    count: kill
                                    where: "player.alive"
                                    per: match
                                  alive_kills_ref:
                                    count: kill
                                    while: player.alive
                                    per: match
                                  team_alive_kills_where:
                                    count: kill
                                    where: "round.team.alive > 0"
                                    per: match
                                  enemies_alive_kills_where:
                                    count: kill
                                    where: "round.enemies.alive > 0"
                                    per: match
                                """;

    [Test]
    public async Task WhereReads_BindSubjectSlot()
    {
        ParsedDemo demo = DemoTestHelper.GetOrParse(DemoTestHelper.RequireDemo(DemoTestHelper.ReferenceDemoFileName));

        BuildResult build = V2KindGoldenSupport.CompileV2(demo, Yaml);
        AnalysisRun run = DemoAnalysis.Evaluate(demo, build);

        Dictionary<int, int> aliveWhere = ReadCounter(run, "alive_kills_where");
        Dictionary<int, int> aliveRef = ReadCounter(run, "alive_kills_ref");
        Dictionary<int, int> teamWhere = ReadCounter(run, "team_alive_kills_where");
        Dictionary<int, int> enemiesWhere = ReadCounter(run, "enemies_alive_kills_where");

        int aliveTotal = aliveWhere.Values.Sum();
        int teamTotal = teamWhere.Values.Sum();
        int enemiesTotal = enemiesWhere.Values.Sum();
        Console.WriteLine($"[G1/binding] totals: alive where={aliveTotal} while={aliveRef.Values.Sum()} "
                          + $"| team.alive>0 where={teamTotal} | enemies.alive>0 where={enemiesTotal}");
        foreach (int slot in aliveWhere.Keys.OrderBy(s => s))
        {
            Console.WriteLine($"[G1/binding] slot{slot}: alive where={aliveWhere.GetValueOrDefault(slot)} "
                              + $"while={aliveRef.GetValueOrDefault(slot)} | team.alive where={teamWhere.GetValueOrDefault(slot)} "
                              + $"| enemies.alive where={enemiesWhere.GetValueOrDefault(slot)}");
        }

        foreach (int slot in aliveWhere.Keys)
        {
            // (1) Per-player context: where: == while: gate (both bind the subject slot).
            await Assert.That(aliveWhere.GetValueOrDefault(slot)).IsEqualTo(aliveRef.GetValueOrDefault(slot))
                .Because($"slot{slot}: player.alive in where: must bind the subject slot (== the while: gate)");

            // (2) B6 aggregate: round.team.alive > 0 is always true at the subject's own kill tick, so
            // this equals the subject's total kills — a wrong-slot binding would diverge on team wipes.
            await Assert.That(teamWhere.GetValueOrDefault(slot)).IsEqualTo(aliveWhere.GetValueOrDefault(slot))
                .Because($"slot{slot}: round.team.alive in where: must bind the subject slot "
                         + "(team.alive >= 1 whenever the subject kills, so == the subject's kill total)");

            // enemies.alive > 0 can only ever exclude kills the team.alive filter keeps.
            await Assert.That(enemiesWhere.GetValueOrDefault(slot)).IsLessThanOrEqualTo(aliveWhere.GetValueOrDefault(slot))
                .Because($"slot{slot}: round.enemies.alive > 0 is a subset of the subject's alive kills");
        }

        // Non-degenerate: the enemy team IS emptied by round-winning kills, so reading the actual
        // round.enemies.alive value must drop the total below the always-true team.alive total. This
        // proves the B6 aggregate value is genuinely read (not short-circuited to a constant).
        await Assert.That(enemiesTotal).IsLessThan(teamTotal)
            .Because("round-winning kills empty the enemy team, so enemies.alive>0 excludes them");
        await Assert.That(teamTotal).IsGreaterThan(0).Because("the reference demo has kills");
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
}
