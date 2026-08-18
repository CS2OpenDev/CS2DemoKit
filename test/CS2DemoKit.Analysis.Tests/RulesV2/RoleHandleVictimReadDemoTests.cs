#region

using CS2DemoKit.Analysis.Graphs;
using CS2DemoKit.Analysis.Nodes;
using CS2DemoKit.Parser;
using CS2DemoKit.TestSupport;

#endregion

namespace CS2DemoKit.Analysis.Tests.RulesV2;

/// <summary>
///     B5 role-handle DEMO golden: a <c>where:</c> that reads a non-subject ROLE's entity value
///     (<c>victim.health</c> on the kill view) must bind the VICTIM's per-fire slot — not the subject
///     (killer) slot, and not a global. Companion to <see cref="RoleHandleEntityReadTests" /> (which
///     proves the emitted <c>UserId.entity.pawn.health</c> spelling) and
///     <see cref="WhileEntityGateSubjectBindingTests" /> (the subject-slot analogue).
///     <para>
///         Three match-scoped counters over <c>count: kill</c> (actor = killer):
///     </para>
///     <list type="bullet">
///         <item><c>all_kills</c> — ungated total kills by each player.</item>
///         <item>
///             <c>victim_full_hp</c> — kills where <c>victim.health == 100</c> (a one-shot: the VICTIM
///             was at full HP pre-frame). Reads the victim's slot per fire.
///         </item>
///         <item>
///             <c>killer_full_hp</c> — kills where <c>player.health == 100</c> (the KILLER/subject at
///             full HP). The subject-slot control.
///         </item>
///     </list>
///     <para>
///         Proofs: (1) <c>victim_full_hp &lt;= all</c> per player (a gate only restricts); (2)
///         <c>victim_full_hp &gt; 0</c> in total (the victim entity VALUE is actually read — one-shot
///         kills exist, not constant-false); (3) <c>victim_full_hp != killer_full_hp</c> in total (the
///         read binds the VICTIM slot, a different population from the subject/killer slot — if it read
///         the subject it would equal the killer control). Parses the demo, so
///         <see cref="NotInParallelAttribute" /> and the shared parse cache apply.
///     </para>
/// </summary>
[Category("Integration")]
[NotInParallel]
public class RoleHandleVictimReadDemoTests
{
    private const string Yaml = """
                                ruleset: victim_read
                                for: each_player
                                stats:
                                  all_kills:
                                    count: kill
                                    per: match
                                  victim_full_hp:
                                    count: kill
                                    where: "victim.health == 100"
                                    per: match
                                  killer_full_hp:
                                    count: kill
                                    where: "player.health == 100"
                                    per: match
                                """;

    [Test]
    public async Task VictimHandle_BindsVictimSlot_DistinctFromSubject()
    {
        ParsedDemo demo = DemoTestHelper.GetOrParse(DemoTestHelper.RequireDemo());

        BuildResult build = V2KindGoldenSupport.CompileV2(demo, Yaml);
        AnalysisRun run = DemoAnalysis.Evaluate(demo, build);

        Dictionary<int, int> all = ReadCounter(run, "all_kills");
        Dictionary<int, int> victim = ReadCounter(run, "victim_full_hp");
        Dictionary<int, int> killer = ReadCounter(run, "killer_full_hp");

        int allTotal = all.Values.Sum();
        int victimTotal = victim.Values.Sum();
        int killerTotal = killer.Values.Sum();
        Console.WriteLine($"[victim-read] totals: all={allTotal} victim(hp==100)={victimTotal} "
                          + $"killer(hp==100)={killerTotal}");
        foreach (int slot in all.Keys.OrderBy(s => s))
        {
            Console.WriteLine($"[victim-read] slot{slot}: all={all.GetValueOrDefault(slot)} "
                              + $"victim={victim.GetValueOrDefault(slot)} killer={killer.GetValueOrDefault(slot)}");
        }

        // (1) A gate only ever restricts the subject's kills.
        foreach (int slot in all.Keys)
        {
            await Assert.That(victim.GetValueOrDefault(slot)).IsLessThanOrEqualTo(all.GetValueOrDefault(slot))
                .Because($"slot{slot}: a victim-health gate can only drop kills");
        }

        // (2) The victim entity value is genuinely read (one-shot full-HP-victim kills exist).
        await Assert.That(victimTotal).IsGreaterThan(0)
            .Because("some kills one-shot a full-HP victim, so the victim-gated total must be > 0");

        // (3) The read binds the VICTIM slot, not the subject/killer slot: the victim-full and
        // killer-full populations differ (equal totals would mean the role handle read the subject).
        await Assert.That(victimTotal).IsNotEqualTo(killerTotal)
            .Because("victim.health reads the victim slot — a different population from the killer/subject slot");
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
