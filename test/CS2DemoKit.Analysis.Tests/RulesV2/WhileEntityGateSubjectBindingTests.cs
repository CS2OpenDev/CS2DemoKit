#region

using CS2DemoKit.Analysis.Graphs;
using CS2DemoKit.Analysis.Nodes;
using CS2DemoKit.Parser;
using CS2DemoKit.TestSupport;

#endregion

namespace CS2DemoKit.Analysis.Tests.RulesV2;

/// <summary>
///     Fix golden for the entity-read <c>while:</c> site: an entity-provider read
///     (<c>player.health</c>) inside a stat's <c>while:</c> gate must bind the SUBJECT player's per-slot
///     value at fire time — not throw (the pre-fix behaviour, see
///     <see cref="EntityReadSiteSweepTests" />) and not read a wrong/global value. The fix folds the
///     entity-bearing while: comparison into the same fire-time event condition a <c>where:</c> entity
///     read uses, so the two must be indistinguishable.
///     <para>
///         Three counters over <c>count: kill</c> on the reference demo:
///     </para>
///     <list type="bullet">
///         <item><c>all_kills</c> — ungated total kills per player.</item>
///         <item>
///             <c>full_hp_kills_while</c> — kills gated by <c>while: player.health == 100</c> (the
///             killer, i.e. the kill view's subject, at full HP at the kill tick).
///         </item>
///         <item>
///             <c>full_hp_kills_where</c> — the same gate expressed as a <c>where:</c> — the
///             known-working entity site, used as the equality oracle.
///         </item>
///     </list>
///     <para>
///         Proofs: (1) per player <c>while == where</c> (the fold routes through the identical
///         subject-bound entity seam); (2) per player <c>while &lt;= all</c> (the gate only ever
///         restricts); (3) total <c>while &lt; all</c> (players get some kills below full HP, so the
///         gate genuinely restricts — not a constant-true no-op); (4) total <c>while &gt; 0</c> (some
///         full-HP kills exist, so the entity VALUE is actually read — not a constant-false). Parses the
///         demo, so <see cref="NotInParallelAttribute" /> and the shared parse cache apply.
///     </para>
/// </summary>
[Category("Integration")]
[NotInParallel]
public class WhileEntityGateSubjectBindingTests
{
    private const string Yaml = """
                                ruleset: while_entity_gate
                                for: each_player
                                stats:
                                  all_kills:
                                    count: kill
                                    per: match
                                  full_hp_kills_while:
                                    count: kill
                                    while: "player.health == 100"
                                    per: match
                                  full_hp_kills_where:
                                    count: kill
                                    where: "player.health == 100"
                                    per: match
                                """;

    [Test]
    public async Task WhileEntityGate_BindsSubjectSlot_AndRestricts()
    {
        ParsedDemo demo = DemoTestHelper.GetOrParse(DemoTestHelper.RequireDemo());

        BuildResult build = V2KindGoldenSupport.CompileV2(demo, Yaml);
        AnalysisRun run = DemoAnalysis.Evaluate(demo, build);

        Dictionary<int, int> all = ReadCounter(run, "all_kills");
        Dictionary<int, int> whileGated = ReadCounter(run, "full_hp_kills_while");
        Dictionary<int, int> whereGated = ReadCounter(run, "full_hp_kills_where");

        int allTotal = all.Values.Sum();
        int whileTotal = whileGated.Values.Sum();
        int whereTotal = whereGated.Values.Sum();
        Console.WriteLine($"[while-entity] totals: all={allTotal} while(hp==100)={whileTotal} "
                          + $"where(hp==100)={whereTotal}");
        foreach (int slot in all.Keys.OrderBy(s => s))
        {
            Console.WriteLine($"[while-entity] slot{slot}: all={all.GetValueOrDefault(slot)} "
                              + $"while={whileGated.GetValueOrDefault(slot)} where={whereGated.GetValueOrDefault(slot)}");
        }

        foreach (int slot in all.Keys)
        {
            // (1) The folded while: entity gate binds the SUBJECT slot exactly as the where: gate does.
            await Assert.That(whileGated.GetValueOrDefault(slot)).IsEqualTo(whereGated.GetValueOrDefault(slot))
                .Because($"slot{slot}: while: player.health must bind the same subject slot as where:");

            // (2) The gate only ever restricts the subject's kills.
            await Assert.That(whileGated.GetValueOrDefault(slot)).IsLessThanOrEqualTo(all.GetValueOrDefault(slot))
                .Because($"slot{slot}: a health gate can only drop kills");
        }

        // (3) Genuine restriction: players get some kills while hurt, so the gate drops the total.
        await Assert.That(whileTotal).IsLessThan(allTotal)
            .Because("some kills happen below full HP, so the health gate must restrict the total");
        // (4) The entity value is genuinely read, not short-circuited false: some full-HP kills survive.
        await Assert.That(whileTotal).IsGreaterThan(0)
            .Because("some kills happen at full HP, so the gated total must be > 0");
    }

    /// <summary>Reads a match-scoped counter node's per-player final value into slot -> count.</summary>
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
