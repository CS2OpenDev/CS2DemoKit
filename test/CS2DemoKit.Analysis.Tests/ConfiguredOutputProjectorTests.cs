#region

using System.Globalization;
using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Analysis.Config;
using CS2DemoKit.Analysis.Graphs;
using CS2DemoKit.Analysis.Output;
using CS2DemoKit.Parser;

#endregion

namespace CS2DemoKit.Analysis.Tests;

/// <summary>
///     Tests for <see cref="ConfiguredOutputProjector" /> driven entirely by SYNTHETIC
///     <see cref="EvaluationResult" /> fixtures (the <see cref="PlayerRoundStatsProjectorTests" />
///     pattern — stub nodes, hand-built snapshot vectors, no demo file): final-snapshot sampling for
///     <c>per_player_per_game</c>, last-snapshot-per-live-round sampling for
///     <c>per_player_per_round</c>, chain-filtered rising edges for <c>per_event</c>, declared-only
///     dimension emission, and the per-player → game-scope metric resolution fallback.
/// </summary>
[Category("Unit")]
public class ConfiguredOutputProjectorTests
{
    private static readonly string[] _playerNameOnlyDimension = ["player_name"];

    /// <summary>An int counter snapshot (numeric + display populated, active).</summary>
    private static NodeSnapshot Num(int value) => new(true, value.ToString(CultureInfo.InvariantCulture), value);

    /// <summary>Culture-invariant boxed-int read for dimension/value assertions.</summary>
    private static int AsInt(object? value) => Convert.ToInt32(value, CultureInfo.InvariantCulture);

    private static ParsedDemo SyntheticDemo(string mapName, IReadOnlyDictionary<int, PlayerInfo> players) =>
        new(
            [],
            [],
            players,
            null,
            mapName,
            0,
            1f / 64f,
            "test",
            "test",
            "csgo",
            0,
            0,
            0,
            "valve_demo_2",
            "",
            "",
            DemoProfile.Unknown);

    /// <summary>
    ///     Builds a synthetic result: tracked index 0 = the "RoundNumber" counter (its production
    ///     display name), then one "kills" node per player. Each player's NodesByRuleId exposes its
    ///     kills node under the bare id and a "my_chain.kills" qualified alias.
    /// </summary>
    private static (EvaluationResult Result, ParsedDemo Demo) BuildScenario(
        int[] roundNumberPerMessage,
        (int Slot, string Name)[] players,
        Func<int, int, int> killsCell)
    {
        StubNode roundNumberNode = new("RoundNumber");
        List<StateNode> tracked = [roundNumberNode];

        List<PerPlayerNodeTemplate.MaterializedPlayer> materialized = new();
        List<int> killsIndexByPlayer = new();

        foreach ((int slot, string name) in players)
        {
            StubNode killsNode = new($"{name}_kills");
            killsIndexByPlayer.Add(tracked.Count);
            tracked.Add(killsNode);

            Dictionary<string, StateNode> nodesByRuleId = new(StringComparer.OrdinalIgnoreCase)
            {
                ["kills"] = killsNode,
                ["my_chain.kills"] = killsNode
            };

            materialized.Add(new PerPlayerNodeTemplate.MaterializedPlayer(
                slot, name, [killsNode], [], [], [],
                NodesByRuleId: nodesByRuleId));
        }

        NodeSnapshot[][] snapshots = new NodeSnapshot[roundNumberPerMessage.Length][];
        for (int m = 0; m < roundNumberPerMessage.Length; m++)
        {
            NodeSnapshot[] vec = new NodeSnapshot[tracked.Count];
            vec[0] = Num(roundNumberPerMessage[m]);
            for (int p = 0; p < players.Length; p++)
            {
                vec[killsIndexByPlayer[p]] = Num(killsCell(m, p));
            }

            snapshots[m] = vec;
        }

        EvaluationResult result = new(
            new RuleChainTimeline([]),
            snapshots,
            [], // Messages intentionally empty — the projector reads snapshots, not messages.
            tracked,
            materialized,
            []);

        Dictionary<int, PlayerInfo> playerInfos = new();
        foreach ((int slot, string name) in players)
        {
            playerInfos[slot] = new PlayerInfo(slot, name, 0UL, slot, slot % 2 == 0 ? 2 : 3, false);
        }

        return (result, SyntheticDemo("de_mirage", playerInfos));
    }

    private static OutputDef PerGameOutput(
        string[] dimensions, params string[] ruleRefs) => new(
        "my_game_table",
        OutputScope.PerPlayerPerGame,
        ruleRefs.Select(r => new MetricRef(r, "Kills")).ToList(),
        dimensions);

    // ── per_player_per_game ──────────────────────────────────────────────────

    /// <summary>Per-game scope samples the FINAL snapshot — one row per player, metric under its label.</summary>
    [Test]
    public async Task PerGame_SamplesFinalSnapshot()
    {
        (EvaluationResult result, ParsedDemo demo) = BuildScenario(
            [0, 1, 2],
            [(0, "Alice"), (1, "Bob")],
            (m, p) => m * 10 + p); // final message (m=2): Alice 20, Bob 21

        OutputDef output = PerGameOutput(["map", "player_slot", "player_name", "team"], "kills");
        MetricTable table = new ConfiguredOutputProjector(output).Project(result, demo).Single();

        await Assert.That(table.Name).IsEqualTo("my_game_table");
        await Assert.That(table.ValueColumns).Contains("Kills");
        await Assert.That(table.Rows.Count).IsEqualTo(2);

        MetricRow alice = table.Rows.Single(r => (string?)r.Dimensions["player_name"] == "Alice");
        await Assert.That(AsInt(alice.Values["Kills"])).IsEqualTo(20);
        await Assert.That(alice.Dimensions["map"]).IsEqualTo("de_mirage");
        await Assert.That(AsInt(alice.Dimensions["team"])).IsEqualTo(2);

        MetricRow bob = table.Rows.Single(r => (string?)r.Dimensions["player_name"] == "Bob");
        await Assert.That(AsInt(bob.Values["Kills"])).IsEqualTo(21);
    }

    /// <summary>Only the declared dimensions are emitted — nothing extra, in declared order.</summary>
    [Test]
    public async Task Dimensions_EmittedPerDeclaredListOnly()
    {
        (EvaluationResult result, ParsedDemo demo) = BuildScenario(
            [1], [(0, "Alice")], (_, _) => 5);

        OutputDef output = PerGameOutput(["player_name"], "kills");
        MetricTable table = new ConfiguredOutputProjector(output)
            {
                MatchId = "match.dem"
            }
            .Project(result, demo).Single();

        await Assert.That(table.DimensionColumns).IsEquivalentTo(_playerNameOnlyDimension);
        MetricRow row = table.Rows.Single();
        await Assert.That(row.Dimensions.Count).IsEqualTo(1);
        await Assert.That(row.Dimensions.ContainsKey("map")).IsFalse();
        await Assert.That(row.Dimensions.ContainsKey("match_id")).IsFalse(); // not declared → not emitted
    }

    /// <summary>match_id is emitted when declared AND provided; omitted per row when MatchId is null.</summary>
    [Test]
    public async Task MatchId_OmittedWhenNull()
    {
        (EvaluationResult result, ParsedDemo demo) = BuildScenario(
            [1], [(0, "Alice")], (_, _) => 5);

        OutputDef output = PerGameOutput(["match_id", "player_name"], "kills");

        MetricRow withId = new ConfiguredOutputProjector(output)
            {
                MatchId = "m.dem"
            }
            .Project(result, demo).Single().Rows.Single();
        await Assert.That(withId.Dimensions["match_id"]).IsEqualTo("m.dem");

        MetricRow withoutId = new ConfiguredOutputProjector(output)
            .Project(result, demo).Single().Rows.Single();
        await Assert.That(withoutId.Dimensions.ContainsKey("match_id")).IsFalse();
    }

    /// <summary>A chain-qualified metric reference resolves through the per-player alias map.</summary>
    [Test]
    public async Task QualifiedMetricRef_ResolvesPerPlayer()
    {
        (EvaluationResult result, ParsedDemo demo) = BuildScenario(
            [1], [(0, "Alice")], (_, _) => 7);

        OutputDef output = PerGameOutput(["player_name"], "my_chain.kills");
        MetricRow row = new ConfiguredOutputProjector(output).Project(result, demo).Single().Rows.Single();

        await Assert.That(AsInt(row.Values["Kills"])).IsEqualTo(7);
    }

    /// <summary>A metric not in the player map falls back to the game-scope node map.</summary>
    [Test]
    public async Task GameScopedMetric_ResolvesViaFallback()
    {
        (EvaluationResult result, ParsedDemo demo) = BuildScenario(
            [3], [(0, "Alice"), (1, "Bob")], (_, _) => 0);

        // The tracked "RoundNumber" stub doubles as a game-scoped metric node here.
        Dictionary<string, StateNode> gameNodes = new(StringComparer.OrdinalIgnoreCase)
        {
            ["total_rounds"] = result.FinalTrackedNodes[0]
        };

        OutputDef output = new(
            "my_game_table",
            OutputScope.PerPlayerPerGame,
            [new MetricRef("total_rounds", "Rounds")],
            ["player_name"]);

        MetricTable table = new ConfiguredOutputProjector(output, gameNodes).Project(result, demo).Single();

        // Same game-scoped value on every player row.
        await Assert.That(table.Rows.Count).IsEqualTo(2);
        foreach (MetricRow row in table.Rows)
        {
            await Assert.That(AsInt(row.Values["Rounds"])).IsEqualTo(3);
        }
    }

    /// <summary>An unresolvable metric (requires-skipped rule) reads null, not an exception.</summary>
    [Test]
    public async Task UnresolvableMetric_ReadsNull()
    {
        (EvaluationResult result, ParsedDemo demo) = BuildScenario(
            [1], [(0, "Alice")], (_, _) => 5);

        OutputDef output = PerGameOutput(["player_name"], "not_built_on_this_profile");
        MetricRow row = new ConfiguredOutputProjector(output).Project(result, demo).Single().Rows.Single();

        await Assert.That(row.Values["Kills"]).IsNull();
    }

    // ── per_player_per_round ─────────────────────────────────────────────────

    /// <summary>Per-round scope samples the LAST snapshot of each live round; warmup (round 0) is skipped.</summary>
    [Test]
    public async Task PerRound_SamplesLastSnapshotPerLiveRound()
    {
        // round_number: warmup, then round 1 spans messages 1-2 (kills 1 → 5), round 2 at message 3.
        int[] rounds = [0, 1, 1, 2];
        int[] kills = [0, 1, 5, 9];

        (EvaluationResult result, ParsedDemo demo) = BuildScenario(
            rounds, [(0, "Alice")], (m, _) => kills[m]);

        OutputDef output = new(
            "my_round_table",
            OutputScope.PerPlayerPerRound,
            [new MetricRef("kills", "Kills")],
            ["round_number", "player_name"]);

        MetricTable table = new ConfiguredOutputProjector(output).Project(result, demo).Single();

        await Assert.That(table.Rows.Count).IsEqualTo(2); // rounds 1 and 2, no warmup row

        MetricRow round1 = table.Rows.Single(r => AsInt(r.Dimensions["round_number"]) == 1);
        MetricRow round2 = table.Rows.Single(r => AsInt(r.Dimensions["round_number"]) == 2);
        await Assert.That(AsInt(round1.Values["Kills"])).IsEqualTo(5); // last index of round 1
        await Assert.That(AsInt(round2.Values["Kills"])).IsEqualTo(9);
    }

    // ── per_event ────────────────────────────────────────────────────────────

    /// <summary>Per-event scope logs only the declared chains' satisfactions, dimensions-only rows.</summary>
    [Test]
    public async Task PerEvent_FiltersToDeclaredChains()
    {
        (EvaluationResult baseResult, ParsedDemo demo) = BuildScenario(
            [1], [(0, "Alice")], (_, _) => 0);

        EvaluationResult result = baseResult with
        {
            Timeline = new RuleChainTimeline(
            [
                new RuleChainEvent("_chain_ace_round", 10, 640),
                new RuleChainEvent("_chain_other_chain", 20, 1280),
                new RuleChainEvent("bare_logic_node", 30, 1920) // internal rising edge — never a row
            ])
        };

        OutputDef output = new(
            "my_events",
            OutputScope.PerEvent,
            [],
            ["chain", "frame_index", "tick"],
            ["ace_round"]);

        MetricTable table = new ConfiguredOutputProjector(output).Project(result, demo).Single();

        await Assert.That(table.ValueColumns).IsEmpty();
        MetricRow row = table.Rows.Single();
        await Assert.That(row.Dimensions["chain"]).IsEqualTo("ace_round");
        await Assert.That(AsInt(row.Dimensions["frame_index"])).IsEqualTo(10);
        await Assert.That(AsInt(row.Dimensions["tick"])).IsEqualTo(640);
        await Assert.That(row.Values).IsEmpty();
    }

    // ── Facade convenience ───────────────────────────────────────────────────

    /// <summary>AnalysisRun.ProjectConfiguredOutputs emits one table per configured output, in order.</summary>
    [Test]
    public async Task ProjectConfiguredOutputs_EmitsAllConfiguredTables()
    {
        (EvaluationResult result, ParsedDemo demo) = BuildScenario(
            [1, 2], [(0, "Alice")], (m, _) => m);

        BuildResult build = new(
            new StateGraph(), [], [], [], new HashSet<Type>(), [],
            GameNodesByRuleId: null,
            Outputs:
            [
                PerGameOutput(["player_name"], "kills"),
                new OutputDef("my_round_table", OutputScope.PerPlayerPerRound,
                    [new MetricRef("kills", "Kills")], ["round_number", "player_name"])
            ]);

        AnalysisRun run = new(build, result.Timeline, result);
        IReadOnlyList<MetricTable> tables = run.ProjectConfiguredOutputs(demo, "match.dem");

        await Assert.That(tables.Count).IsEqualTo(2);
        await Assert.That(tables[0].Name).IsEqualTo("my_game_table");
        await Assert.That(tables[1].Name).IsEqualTo("my_round_table");
        await Assert.That(tables[1].Rows.Count).IsEqualTo(2); // two live rounds
    }

    /// <summary>Snapshot-less runs (bare mode) fail loudly instead of returning empty tables.</summary>
    [Test]
    public async Task ProjectConfiguredOutputs_WithoutSnapshots_Throws()
    {
        (EvaluationResult result, ParsedDemo demo) = BuildScenario(
            [1], [(0, "Alice")], (_, _) => 0);

        BuildResult build = new(
            new StateGraph(), [], [], [], new HashSet<Type>(), [],
            Outputs: [PerGameOutput(["player_name"], "kills")]);

        AnalysisRun run = new(build, result.Timeline, null);

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => run.ProjectConfiguredOutputs(demo));
        await Assert.That(ex.Message).Contains("snapshot");
    }

    /// <summary>No configured outputs → empty list (never null), even without snapshots.</summary>
    [Test]
    public async Task ProjectConfiguredOutputs_NoOutputs_ReturnsEmpty()
    {
        (EvaluationResult result, ParsedDemo demo) = BuildScenario(
            [1], [(0, "Alice")], (_, _) => 0);

        BuildResult build = new(new StateGraph(), [], [], [], new HashSet<Type>(), []);
        AnalysisRun run = new(build, result.Timeline, null);

        await Assert.That(run.ProjectConfiguredOutputs(demo)).IsEmpty();
    }

    /// <summary>A minimal concrete StateNode — the projector only uses reference identity + Name.</summary>
    private sealed class StubNode(string name) : StateNode
    {
        public override bool IsActive => true;
        public override string Name { get; } = name;
    }
}
