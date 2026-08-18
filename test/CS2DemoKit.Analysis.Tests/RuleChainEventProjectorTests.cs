#region

using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Analysis.Graphs;
using CS2DemoKit.Analysis.Output;
using CS2DemoKit.Analysis.Yaml;
using CS2DemoKit.Parser;
using CS2DemoKit.TestSupport;

#endregion

namespace CS2DemoKit.Analysis.Tests;

/// <summary>
///     Tests for <see cref="RuleChainEventProjector" /> over a synthetic timeline (no demo file):
///     chain-satisfaction filtering (<c>_chain_</c> prefix discipline), prefix stripping, dimension
///     schema, player attribution (stamped when present, omitted for game-scoped events), and the
///     warmup default (round 0) when no round signal exists. Plus one demo-gated integration test
///     asserting the evaluator stamps a real per-player satisfaction with its owning player.
/// </summary>
[Category("Unit")]
public class RuleChainEventProjectorTests
{
    private static (EvaluationResult Result, ParsedDemo Demo) BuildScenario(params RuleChainEvent[] events)
    {
        EvaluationResult result = new(
            new RuleChainTimeline(events),
            SnapshotTable.FromRows([]),
            [], // no messages → no round attribution → warmup default
            [],
            [],
            []);

        ParsedDemo demo = new(
            [], [], new Dictionary<int, PlayerInfo>(), null,
            "de_test", 0, 1f / 64f,
            "t", "t", "csgo", 0,
            0, 0, "valve_demo_2",
            "", "", DemoProfile.Unknown);

        return (result, demo);
    }

    /// <summary>Only _chain_-prefixed events project; the prefix is stripped into the chain dimension.</summary>
    [Test]
    public async Task Project_KeepsOnlyChainSatisfactions_AndStripsPrefix()
    {
        (EvaluationResult result, ParsedDemo demo) = BuildScenario(
            new RuleChainEvent("_chain_deagle_hs_round", 100, 6400),
            new RuleChainEvent("SomeLogicNode", 101, 6500), // internal wiring — must not project
            new RuleChainEvent("_chain_ace", 200, 12800));

        MetricTable table = new RuleChainEventProjector
        {
            MatchId = "m.dem"
        }.Project(result, demo).Single();

        await Assert.That(table.Name).IsEqualTo("rule_chain_events");
        await Assert.That(table.Rows.Count).IsEqualTo(2);
        await Assert.That(table.Rows[0].Dimensions["chain"]).IsEqualTo("deagle_hs_round");
        await Assert.That(table.Rows[1].Dimensions["chain"]).IsEqualTo("ace");
        await Assert.That(table.Rows[1].Dimensions["tick"]).IsEqualTo(12800);
        await Assert.That(table.Rows[0].Dimensions["match_id"]).IsEqualTo("m.dem");
    }

    /// <summary>Without a round signal, events attribute to round 0 (warmup/unknown).</summary>
    [Test]
    public async Task Project_NoRoundSignal_DefaultsToRoundZero()
    {
        (EvaluationResult result, ParsedDemo demo) = BuildScenario(
            new RuleChainEvent("_chain_ace", 100, 6400));

        MetricTable table = new RuleChainEventProjector().Project(result, demo).Single();
        await Assert.That(table.Rows.Single().Dimensions["round_number"]).IsEqualTo(0);
    }

    /// <summary>An event-free timeline produces the schema-only table (formatters need the columns).</summary>
    [Test]
    public async Task Project_EmptyTimeline_ProducesSchemaOnlyTable()
    {
        (EvaluationResult result, ParsedDemo demo) = BuildScenario();

        MetricTable table = new RuleChainEventProjector().Project(result, demo).Single();
        await Assert.That(table.Rows).IsEmpty();
        await Assert.That(table.DimensionColumns).Contains("chain");
        await Assert.That(table.ValueColumns).IsEmpty();
    }

    /// <summary>Attributed events project player_slot/player_name; game-scoped events omit both.</summary>
    [Test]
    public async Task Project_PlayerAttribution_StampedWhenPresent_OmittedWhenNull()
    {
        (EvaluationResult result, ParsedDemo demo) = BuildScenario(
            new RuleChainEvent("_chain_opening_kills", 100, 6400, 3, "s1mple"),
            new RuleChainEvent("_chain_game_scoped", 200, 12800));

        MetricTable table = new RuleChainEventProjector().Project(result, demo).Single();

        await Assert.That(table.DimensionColumns).Contains("player_slot");
        await Assert.That(table.DimensionColumns).Contains("player_name");
        await Assert.That(table.Rows[0].Dimensions["player_slot"]).IsEqualTo(3);
        await Assert.That(table.Rows[0].Dimensions["player_name"]).IsEqualTo("s1mple");
        await Assert.That(table.Rows[1].Dimensions.ContainsKey("player_slot")).IsFalse();
        await Assert.That(table.Rows[1].Dimensions.ContainsKey("player_name")).IsFalse();
    }

    /// <summary>
    ///     Demo-gated (F1 acceptance): evaluating the shipped rules over a real demo produces
    ///     per-player chain satisfactions stamped with a slot from the demo roster and the roster's
    ///     name for that slot.
    /// </summary>
    [Test]
    [NotInParallel]
    [Category("Integration")]
    public async Task Evaluate_PerPlayerChainSatisfaction_CarriesOwningPlayer()
    {
        string path = DemoTestHelper.RequireDemo();
        ParsedDemo demo = DemoTestHelper.GetOrParse(path);

        // Post Rulesets v2 cutover the shipped stats are v2 rulesets (in .Rulesets), so the
        // per-player _chain_ satisfactions come from the v2 build path, not v1 config.Chains.
        RuleConfigLoadResult loaded = YamlConfigLoader.TryLoadDirectory(RuleSetLocator.ResolveShippedRulesDirectory());
        AnalysisOptions options = new()
        {
            CaptureSnapshots = false
        };
        BuildResult build = DemoAnalysis.Build(demo, loaded.Rulesets, options);
        AnalysisRun run = DemoAnalysis.Evaluate(demo, build, options);

        List<RuleChainEvent> attributed = run.Timeline.Events
            .Where(e => e.ChainName.StartsWith("_chain_", StringComparison.Ordinal) && e.PlayerSlot is not null)
            .ToList();
        Console.WriteLine($"Attributed chain satisfactions: {attributed.Count}");

        await Assert.That(attributed).IsNotEmpty();
        foreach (RuleChainEvent ev in attributed)
        {
            await Assert.That(ev.PlayerName).IsNotNull();
        }

        // At least one satisfaction must trace back to the demo roster with a matching name —
        // proving the stamp is the materialized player, not a placeholder.
        bool anyRosterMatch = attributed.Any(e =>
            demo.Players.TryGetValue(e.PlayerSlot!.Value, out PlayerInfo? info)
            && info.Name == e.PlayerName);
        await Assert.That(anyRosterMatch).IsTrue();
    }
}
