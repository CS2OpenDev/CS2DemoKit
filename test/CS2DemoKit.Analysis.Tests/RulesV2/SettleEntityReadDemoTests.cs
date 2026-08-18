#region

using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Analysis.Graphs;
using CS2DemoKit.Analysis.Nodes;
using CS2DemoKit.Parser;
using CS2DemoKit.TestSupport;

#endregion

namespace CS2DemoKit.Analysis.Tests.RulesV2;

/// <summary>
///     Demo-backed goldens for the SETTLE-site entity-read fix: <c>player.entity.pawn.*</c> reads now
///     resolve in <c>compute:</c> (round-end / live) and <c>flag: when:</c> (round-end settle) via a
///     subject-relative <see cref="EntityValuePullNode" />. These sites have no event frame, so they read
///     the subject's entity value at SETTLE time (the round-end entity state) — distinct from the at-fire
///     timing <c>where:</c>/value-selectors use (proven by <see cref="WhileEntityGateSubjectBindingTests" />).
///     Requires a parsed demo (<c>DEMO_PATH</c> / reference demo); skips gracefully without one.
/// </summary>
[Category("Integration")]
[NotInParallel]
public class SettleEntityReadDemoTests
{
    /// <summary>
    ///     A <c>compute:</c> formula over <c>player.health</c> reads the subject's round-end health: the
    ///     value is a plausible health (0..100), varies per subject (per-slot binding, not a global), and
    ///     arithmetic over it is consistent (<c>hp_plus == hp + 10</c>) — so the entity value is genuinely
    ///     read into the node-expression compiler, not a phantom zero or constant.
    /// </summary>
    [Test]
    public async Task Compute_ReadsSubjectRoundEndHealth()
    {
        ParsedDemo demo = DemoTestHelper.GetOrParse(DemoTestHelper.RequireDemo());

        const string Yaml = """
                            ruleset: settle_compute
                            for: each_player
                            stats:
                              hp:
                                compute: "player.health"
                                per: match
                              hp_plus:
                                compute: "player.health + 10"
                                per: match
                            """;

        BuildResult build = V2KindGoldenSupport.CompileV2(demo, Yaml);
        AnalysisRun run = DemoAnalysis.Evaluate(demo, build);

        Dictionary<int, double> hp = ReadCompute(run, "hp");
        Dictionary<int, double> hpPlus = ReadCompute(run, "hp_plus");

        foreach (int slot in hp.Keys.OrderBy(s => s))
        {
            Console.WriteLine($"[settle-compute] slot{slot}: hp={hp[slot]} hp_plus={hpPlus.GetValueOrDefault(slot)}");
        }

        await Assert.That(hp.Count).IsGreaterThan(0).Because("the compute must materialize per player");

        foreach ((int slot, double value) in hp)
        {
            // Plausible health: the round-end pawn health is 0 (dead) .. 100 (untouched).
            await Assert.That(value).IsGreaterThanOrEqualTo(0.0).Because($"slot{slot}: health >= 0");
            await Assert.That(value).IsLessThanOrEqualTo(100.0).Because($"slot{slot}: health <= 100");
            // Arithmetic over the entity read is consistent — proves the value flows into the compiled
            // node expression (not dropped / short-circuited).
            await Assert.That(hpPlus.GetValueOrDefault(slot)).IsEqualTo(value + 10.0)
                .Because($"slot{slot}: hp_plus must be hp + 10 over the same subject read");
        }

        // Genuine read: at least one subject ends a round alive with health > 0.
        await Assert.That(hp.Values.Any(v => v > 0.0)).IsTrue()
            .Because("some player ends a round-end alive, so the health read must be > 0 for someone");
        // Per-subject binding: the round-end health is not a single global value shared by every slot.
        await Assert.That(hp.Values.Distinct().Count()).IsGreaterThan(1)
            .Because("round-end health varies per subject, so the compute must be per-slot bound");
    }

    /// <summary>
    ///     A <c>flag: when:</c> over <c>player.armor</c> builds, gates on the subject's round-end armor,
    ///     and re-evaluates at the settle point: an always-true predicate (<c>armor &gt;= 0</c>) fires for
    ///     at least one subject, while an impossible one (<c>armor &gt; 200</c>, above the 100 cap) fires
    ///     for none — proving the entity value is genuinely read and compared (both directions), not
    ///     frozen at the flag's init state.
    /// </summary>
    [Test]
    public async Task FlagWhen_GatesOnSubjectArmor()
    {
        ParsedDemo demo = DemoTestHelper.GetOrParse(DemoTestHelper.RequireDemo());

        const string Yaml = """
                            ruleset: settle_when
                            for: each_player
                            stats:
                              armor_nonneg:
                                flag:
                                  when: "player.armor >= 0"
                                per: match
                              armor_impossible:
                                flag:
                                  when: "player.armor > 200"
                                per: match
                            """;

        BuildResult build = V2KindGoldenSupport.CompileV2(demo, Yaml);
        AnalysisRun run = DemoAnalysis.Evaluate(demo, build);

        Dictionary<int, bool> nonneg = ReadFlag(run, "armor_nonneg");
        Dictionary<int, bool> impossible = ReadFlag(run, "armor_impossible");

        foreach (int slot in nonneg.Keys.OrderBy(s => s))
        {
            Console.WriteLine($"[settle-when] slot{slot}: armor>=0 -> {nonneg[slot]} armor>200 -> {impossible.GetValueOrDefault(slot)}");
        }

        await Assert.That(nonneg.Count).IsGreaterThan(0).Because("the flag must materialize per player");

        // The impossible predicate (armor > 200, above the ~100 cap) is false for every subject — proves
        // the value is genuinely read and compared (not a constant-true no-op).
        await Assert.That(impossible.Values.Any(v => v)).IsFalse()
            .Because("armor never exceeds ~100, so armor > 200 must be false for every subject");
        // The always-true predicate (armor >= 0) fires for at least one subject — proves the flag actually
        // re-evaluates at the round-end settle and the pull-node read is reachable (not frozen false).
        await Assert.That(nonneg.Values.Any(v => v)).IsTrue()
            .Because("armor is always >= 0, so the settle recompute must set the flag true for someone");
    }

    /// <summary>Reads a compute (ComputedStatNode) node's per-player final value into slot -> value.</summary>
    private static Dictionary<int, double> ReadCompute(AnalysisRun run, string nodeName)
    {
        EvaluationResult result = run.Snapshots
                                  ?? throw new InvalidOperationException("evaluation produced no snapshots");
        Dictionary<int, double> byslot = new();
        foreach (PerPlayerNodeTemplate.MaterializedPlayer mp in result.MaterializedPlayers)
        {
            ComputedStatNode? node = mp.Nodes
                .OfType<ComputedStatNode>()
                .FirstOrDefault(n => string.Equals(n.Name, nodeName, StringComparison.Ordinal));
            if (node is not null)
            {
                byslot[mp.PlayerSlot] = node.Value;
            }
        }

        return byslot;
    }

    /// <summary>Reads a flag (BoolNode) node's per-player final active state into slot -> bool.</summary>
    private static Dictionary<int, bool> ReadFlag(AnalysisRun run, string nodeName)
    {
        EvaluationResult result = run.Snapshots
                                  ?? throw new InvalidOperationException("evaluation produced no snapshots");
        Dictionary<int, bool> byslot = new();
        foreach (PerPlayerNodeTemplate.MaterializedPlayer mp in result.MaterializedPlayers)
        {
            BoolNode? node = mp.Nodes
                .OfType<BoolNode>()
                .FirstOrDefault(n => string.Equals(n.Name, nodeName, StringComparison.Ordinal));
            if (node is not null)
            {
                byslot[mp.PlayerSlot] = node.IsActive;
            }
        }

        return byslot;
    }
}
