#region

using System.Globalization;
using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Analysis.Config;
using CS2DemoKit.Analysis.Graphs;
using CS2DemoKit.Analysis.Output;
using CS2DemoKit.Analysis.Profiles;
using CS2DemoKit.Analysis.RulesetsV2.Compile;
using CS2DemoKit.Analysis.Tests.RoundFacts;
using CS2DemoKit.Parser;
using CS2DemoKit.Parser.GameEvents;

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

    // ── steam_id on per-player tables ────────────────────────────────────────

    private const string PlayerTables = """
        ruleset: rs
        for: each_player
        stats:
          kills:
            count: kill
            per: round
        show:
          tables:
            by_round:
              per: player_round
              columns: [ { stat: kills } ]
            by_match:
              per: player_match
              columns: [ { stat: kills } ]
        """;

    [Test]
    public async Task PlayerTables_LowerWithSteamId()
    {
        IReadOnlyList<OutputDef> outputs = ShowLowering.LowerTables(RoundFactsTestSupport.Checked(PlayerTables));
        await Assert.That(string.Join(",", outputs.Single(o => o.Id == "by_round").Dimensions))
            .IsEqualTo("match_id,map,round_number,player_slot,player_name,steam_id,team");
        await Assert.That(string.Join(",", outputs.Single(o => o.Id == "by_match").Dimensions))
            .IsEqualTo("match_id,map,player_slot,player_name,steam_id,team");
    }

    /// <summary>A decimal string, null when the descriptor has no SteamID64 for the slot.</summary>
    [Test]
    public async Task PlayerRows_CarrySteamIdAsString()
    {
        (EvaluationResult result, _) = BuildScenario([0, 1, 1], [(0, "Alice"), (1, "Bob")], (m, p) => m + p);
        ParsedDemo demo = SyntheticDemo("de_mirage", new Dictionary<int, PlayerInfo>
        {
            [0] = new(0, "Alice", 76561198000000001UL, 0, 2, false),
            [1] = new(1, "Bob", 0UL, 1, 3, false)
        });
        string[] dims = ["player_slot", "player_name", "steam_id", "team"];

        foreach (OutputDef output in (OutputDef[])
                 [
                     PerGameOutput(dims, "kills"),
                     new("my_round_table", OutputScope.PerPlayerPerRound, [new MetricRef("kills", "Kills")], dims)
                 ])
        {
            MetricTable table = new ConfiguredOutputProjector(output).Project(result, demo).Single();
            await Assert.That(table.Rows.Count).IsEqualTo(2);
            MetricRow alice = table.Rows.Single(r => (string?)r.Dimensions["player_name"] == "Alice");
            MetricRow bob = table.Rows.Single(r => (string?)r.Dimensions["player_name"] == "Bob");
            await Assert.That(alice.Dimensions["steam_id"]).IsEqualTo("76561198000000001");
            await Assert.That(bob.Dimensions.ContainsKey("steam_id")).IsTrue();
            await Assert.That(bob.Dimensions["steam_id"]).IsNull();
        }
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
            new StateGraph(), [], [], new HashSet<Type>(),
            GameNodesByRuleId: null,
            Outputs:
            [
                PerGameOutput(["player_name"], "kills"),
                new OutputDef("my_round_table", OutputScope.PerPlayerPerRound,
                    [new MetricRef("kills", "Kills")], ["round_number", "player_name"])
            ]);

        AnalysisRun run = new(build, result.Timeline, result) { Demo = DemoDescriptor.From(demo), Provenance = _provenance };
        IReadOnlyList<MetricTable> tables = run.ProjectConfiguredOutputs(demo, "match.dem");

        await Assert.That(tables.Count).IsEqualTo(2);
        await Assert.That(tables[0].Name).IsEqualTo("my_game_table");
        await Assert.That(tables[1].Name).IsEqualTo("my_round_table");
        await Assert.That(tables[1].Rows.Count).IsEqualTo(2); // two live rounds
    }

    /// <summary>
    ///     A snapshot-less run projects its state-sampled tables from what it recorded, but a per-event
    ///     output logs timeline rising edges, which only a snapshot run keeps: that one still fails
    ///     loudly instead of returning an empty table.
    /// </summary>
    [Test]
    public async Task ProjectConfiguredOutputs_WithoutSnapshots_ThrowsForAPerEventOutput()
    {
        (EvaluationResult result, ParsedDemo demo) = BuildScenario(
            [1], [(0, "Alice")], (_, _) => 0);

        OutputDef events = new("my_events", OutputScope.PerEvent, [], ["chain"], ["ace_round"]);
        BuildResult build = new(
            new StateGraph(), [], [], new HashSet<Type>(),
            Outputs: [PerGameOutput(["player_name"], "kills"), events]);

        AnalysisRun run = new(build, result.Timeline, null) { Demo = DemoDescriptor.From(demo), Provenance = _provenance };

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => run.ProjectConfiguredOutputs(demo));
        await Assert.That(ex.Message).Contains("snapshot");
    }

    /// <summary>
    ///     Without snapshots and without a recording (a run assembled by hand), a state-sampled table
    ///     projects with no rows rather than throwing.
    /// </summary>
    [Test]
    public async Task ProjectConfiguredOutputs_WithoutSnapshots_ProjectsStateTables()
    {
        (EvaluationResult result, ParsedDemo demo) = BuildScenario(
            [1], [(0, "Alice")], (_, _) => 0);

        BuildResult build = new(
            new StateGraph(), [], [], new HashSet<Type>(),
            Outputs: [PerGameOutput(["player_name"], "kills")]);

        AnalysisRun run = new(build, result.Timeline, null) { Demo = DemoDescriptor.From(demo), Provenance = _provenance };

        MetricTable table = run.ProjectConfiguredOutputs(demo).Single();
        await Assert.That(table.Rows).IsEmpty();
    }

    /// <summary>No configured outputs → empty list (never null), even without snapshots.</summary>
    [Test]
    public async Task ProjectConfiguredOutputs_NoOutputs_ReturnsEmpty()
    {
        (EvaluationResult result, ParsedDemo demo) = BuildScenario(
            [1], [(0, "Alice")], (_, _) => 0);

        BuildResult build = new(new StateGraph(), [], [], new HashSet<Type>());
        AnalysisRun run = new(build, result.Timeline, null) { Demo = DemoDescriptor.From(demo), Provenance = _provenance };

        await Assert.That(run.ProjectConfiguredOutputs(demo)).IsEmpty();
    }

    private static readonly AnalysisProvenance _provenance =
        new(AnalysisSourceKind.Materialised, DemoSourceProfileRegistry.DefaultFallback, ProfileResolutionKind.Explicit,
            null, DigestProducerKind.None, true, 0, 0, default);

    // ── per: player_event ─────────────────────────────────────────────────

    /// <summary>
    ///     An event table emits one row per rise of an instance's sequence node, read from the row of
    ///     the firing message: its tick, the live round, and the instance's columns at that moment. A
    ///     fall (a match restart's reset) and a flat row emit nothing, and two players firing on one
    ///     message are two rows. The recorded source of a run without snapshots projects the same rows.
    /// </summary>
    [Test]
    public async Task PlayerEventTable_OneRowPerFiring()
    {
        StubNode round = new("RoundNumber");
        StubNode aliceSeq = new("alice_seq");
        StubNode alicePos = new("alice_pos");
        StubNode bobSeq = new("bob_seq");
        StubNode bobPos = new("bob_pos");
        List<StateNode> tracked = [round, aliceSeq, alicePos, bobSeq, bobPos];

        // round, alice seq, alice pos, bob seq, bob pos
        int[][] cells =
        [
            [1, 0, 10, 0, 50],
            [1, 1, 11, 1, 51], // both fire
            [1, 1, 12, 1, 52], // nothing fires
            [2, 2, 13, 1, 53], // alice fires
            [2, 0, 14, 0, 54], // reset: no row
            [2, 1, 15, 0, 55] // alice fires again
        ];
        int[] ticks = [100, 164, 200, 228, 300, 356];

        NodeSnapshot[][] snapshots = cells.Select(r => r.Select(Num).ToArray()).ToArray();
        GameEventMessage message = GameEventMessage.ForSynthesizedEvent(new GameEvent("cadence_tick", -1, 0, 0, 0));
        List<MessageRef> messages = ticks.Select((t, i) => new MessageRef(i, t, 0, message)).ToList();

        PerPlayerNodeTemplate.MaterializedPlayer Player(int slot, string name, StateNode seq, StateNode pos) =>
            new(slot, name, [seq, pos], [], [], [],
                NodesByRuleId: new Dictionary<string, StateNode>(StringComparer.OrdinalIgnoreCase)
                {
                    ["rs.#seq_positions"] = seq,
                    ["rs.pos"] = pos
                });

        List<PerPlayerNodeTemplate.MaterializedPlayer> players =
            [Player(0, "Alice", aliceSeq, alicePos), Player(1, "Bob", bobSeq, bobPos)];
        EvaluationResult result = new(new RuleChainTimeline([]), snapshots, messages, tracked, players, []);
        ParsedDemo demo = SyntheticDemo("de_mirage", new Dictionary<int, PlayerInfo>
        {
            [0] = new(0, "Alice", 76561198000000001UL, 0, 2, false),
            [1] = new(1, "Bob", 0UL, 1, 3, false)
        });

        OutputDef output = new("positions", OutputScope.PerPlayerPerEvent, [new MetricRef("rs.pos", "pos")],
            ["round_number", "tick", "player_slot", "player_name", "steam_id", "team"])
        {
            Trigger = "cadence.second",
            SequenceRef = "rs.#seq_positions"
        };

        MetricTable table = new ConfiguredOutputProjector(output).Project(result, demo).Single();

        string Render(MetricTable t) => string.Join("\n", t.Rows.Select(r =>
            string.Join(",", t.DimensionColumns.Select(c => Convert.ToString(r.Dimensions.GetValueOrDefault(c), CultureInfo.InvariantCulture)))
            + "->" + Convert.ToString(r.Values["pos"], CultureInfo.InvariantCulture)));

        await Assert.That(Render(table)).IsEqualTo(string.Join("\n",
            "1,164,0,Alice,76561198000000001,2->11",
            "1,164,1,Bob,,3->51",
            "2,228,0,Alice,76561198000000001,2->13",
            "2,356,0,Alice,76561198000000001,2->15"));
        await Assert.That(table.ColumnClocks["tick"]).IsEqualTo(MetricTable.FrameClock);

        // The recorded path: rows built live, projected by the same code.
        ProjectionSource recorded = new([], null, players)
        {
            Events = ProjectionSource.EventsFromSnapshots(result, [aliceSeq, bobSeq])
        };
        MetricTable fromRecorded = new ConfiguredOutputProjector(output)
            .Project(recorded, DemoDescriptor.From(demo)).Single();
        await Assert.That(Render(fromRecorded)).IsEqualTo(Render(table));
    }

    /// <summary>A minimal concrete StateNode — the projector only uses reference identity + Name.</summary>
    private sealed class StubNode(string name) : StateNode
    {
        public override bool IsActive => true;
        public override string Name { get; } = name;
    }
}
