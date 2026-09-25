#region

using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Analysis.RulesetsV2.Model;
using CS2DemoKit.Parser;
using CS2DemoKit.TestSupport;

#endregion

namespace CS2DemoKit.Analysis.Tests;

/// <summary>
///     A <c>tally:</c> target is local to its ruleset. The shipped kast ruleset and the multikill
///     example both tally into <c>rounds_2k</c>..<c>rounds_5k</c>; run together, each must get its own
///     four counters under its own node-map key. Before #70 the second ruleset reused the first one's
///     bare-id nodes, never registered its own key, and its scoreboard threw at player
///     materialisation.
/// </summary>
[Category("Integration")]
[NotInParallel]
public class TallyTargetScopeTests
{
    private static readonly string[] Targets = ["rounds_2k", "rounds_3k", "rounds_4k", "rounds_5k"];

    private static ParsedDemo Sample() =>
        DemoTestHelper.GetOrParse(DemoTestHelper.RequireDemo(DemoTestHelper.SampleDemoFileName));

    private static IReadOnlyList<RulesetDoc> Multikill() =>
        RuleGraphFixtures.Example(RuleGraphFixtures.ExampleFiles()
            .Single(p => Path.GetFileName(p) == "multikill.rules.yaml"));

    private static Dictionary<(int Slot, string Key), int> Counters(AnalysisRun run, string ruleset) =>
        run.MaterializedPlayers
            .SelectMany(p => Targets.Select(t => (p.PlayerSlot, Key: $"{ruleset}.{t}",
                Node: p.NodesByRuleId![$"{ruleset}.{t}"])))
            .ToDictionary(x => (x.PlayerSlot, x.Key), x => ((ValueNode<int>)x.Node).Value);

    [Test]
    public async Task KastAndMultikill_RunTogether_EachWithItsOwnTallyTargets()
    {
        ParsedDemo demo = Sample();
        AnalysisRun together = DemoAnalysis.Run(demo, [.. RuleGraphFixtures.Shipped(), .. Multikill()]);
        await Assert.That(together.Build.ExcludedRulesets).IsEmpty();
        await Assert.That(together.MaterializedPlayers.Count).IsGreaterThan(1);
        await Assert.That(together.ProjectConfiguredOutputs()).IsNotEmpty();

        foreach (PerPlayerNodeTemplate.MaterializedPlayer player in together.MaterializedPlayers)
        {
            foreach (string target in Targets)
            {
                StateNode kast = player.NodesByRuleId![$"kast.{target}"];
                StateNode multikill = player.NodesByRuleId![$"multikill.{target}"];
                await Assert.That(ReferenceEquals(kast, multikill)).IsFalse();

                // Each ruleset's scoreboard column projects its own node.
                string label = target.Replace("rounds_", "", StringComparison.Ordinal).ToUpperInvariant();
                await Assert.That(player.ColumnAssignments
                    .Where(c => c.ColumnName == label && c.ChainId == "_chain_multikill")
                    .All(c => ReferenceEquals(c.Node, multikill))).IsTrue();
                await Assert.That(player.ColumnAssignments
                    .Where(c => c.ColumnName == label && c.ChainId == "_chain_kast")
                    .All(c => ReferenceEquals(c.Node, kast))).IsTrue();
            }
        }

        // Each ruleset counts exactly what it counts on its own: nothing is shared or double-bumped.
        AnalysisRun shipped = DemoAnalysis.Run(demo, RuleGraphFixtures.Shipped());
        AnalysisRun alone = DemoAnalysis.Run(demo, Multikill());
        await Assert.That(Counters(together, "kast")).IsEquivalentTo(Counters(shipped, "kast"));
        await Assert.That(Counters(together, "multikill")).IsEquivalentTo(Counters(alone, "multikill"));
        await Assert.That(Counters(alone, "multikill").Values.Sum()).IsGreaterThan(0);
    }

    /// <summary>Every example under <c>Rules/examples/</c>, beside the shipped rulesets, runs on the sample.</summary>
    [Test]
    public async Task EveryExample_BesideTheShippedRulesets_RunsOnTheSample()
    {
        ParsedDemo demo = Sample();
        List<RulesetDoc> docs = [.. RuleGraphFixtures.Shipped()];
        foreach (string path in RuleGraphFixtures.ExampleFiles())
        {
            docs.AddRange(RuleGraphFixtures.Example(path));
        }

        AnalysisRun run = DemoAnalysis.Run(demo, docs);
        await Assert.That(run.Build.ExcludedRulesets).IsEmpty();
        await Assert.That(run.MaterializedPlayers.Count).IsGreaterThan(1);
        await Assert.That(run.ProjectConfiguredOutputs()).IsNotEmpty();
    }
}
