#region

using System.Globalization;
using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Analysis.Output;
using CS2DemoKit.Parser;

#endregion

namespace CS2DemoKit.Analysis.Tests;

/// <summary>
///     Tests for <see cref="PlayerRoundStatsProjector" /> driven entirely by a SYNTHETIC
///     <see cref="EvaluationResult" /> — no demo file, no evaluator run. The projector reads node
///     values out of <see cref="EvaluationResult.MessageSnapshots" /> (indexed by tracked-node order)
///     and never touches <see cref="EvaluationResult.Messages" />, so the fakes use an empty message
///     list and hand-built snapshot vectors. This exercises the round-boundary sampling (last snapshot
///     index per distinct <c>round_number &gt;= 1</c>) and per-player column extraction in isolation.
/// </summary>
[Category("Unit")]
public class PlayerRoundStatsProjectorTests
{
    private static readonly int[] _expectedThreeRounds = [1, 2, 3];

    /// <summary>An int counter snapshot (numeric + display populated, active).</summary>
    private static NodeSnapshot Num(int value) => new(true, value.ToString(CultureInfo.InvariantCulture), value);

    /// <summary>An inactive snapshot — the value gate must treat this as "not reported" (null).</summary>
    private static NodeSnapshot Inactive() => new(false);

    /// <summary>Culture-invariant boxed-int read for dimension/value assertions.</summary>
    private static int AsInt(object? value) => Convert.ToInt32(value, CultureInfo.InvariantCulture);

    /// <summary>
    ///     Builds a synthetic result with one round_number node (tracked index 0) and a fixed number of
    ///     per-player stat nodes per materialized player, appended after it.
    /// </summary>
    private static (EvaluationResult Result, ParsedDemo Demo) BuildScenario(
        int[] roundNumberPerMessage,
        (int Slot, string Name, string[] Columns)[] players,
        // For each message index, for each player, for each column: the snapshot to store.
        Func<int, int, int, NodeSnapshot> cell)
    {
        // Use the production DISPLAY name ("RoundNumber" — the 3rd RuleDef arg in BuiltinContexts),
        // NOT the rule id "round_number", so this test exercises the same node name the projector sees
        // on a real demo. (A prior version named this "round_number" and silently passed while the real
        // export emitted 0 rows.)
        StubNode roundNumberNode = new("RoundNumber");
        List<StateNode> tracked = [roundNumberNode];

        List<PerPlayerNodeTemplate.MaterializedPlayer> materialized = new();
        // playerColumnNodeIndex[p][c] = tracked index of player p's column c.
        List<List<int>> playerColumnNodeIndex = new();

        foreach ((int slot, string name, string[] columns) in players)
        {
            List<PerPlayerColumnAssignment> assignments = new();
            List<int> colIndices = new();
            List<StateNode> nodes = new();
            foreach (string column in columns)
            {
                RoundStubNode node = new($"{name}_{column}");
                colIndices.Add(tracked.Count);
                tracked.Add(node);
                nodes.Add(node);
                assignments.Add(new PerPlayerColumnAssignment(node, column, IsRoundScoped: true));
            }

            playerColumnNodeIndex.Add(colIndices);
            materialized.Add(new PerPlayerNodeTemplate.MaterializedPlayer(
                slot, name, nodes, [], assignments, []));
        }

        // Snapshot vectors: index 0 = round_number, then the player column nodes in tracked order.
        NodeSnapshot[][] snapshots = new NodeSnapshot[roundNumberPerMessage.Length][];
        for (int m = 0; m < roundNumberPerMessage.Length; m++)
        {
            NodeSnapshot[] vec = new NodeSnapshot[tracked.Count];
            vec[0] = Num(roundNumberPerMessage[m]);
            for (int p = 0; p < players.Length; p++)
            {
                for (int c = 0; c < players[p].Columns.Length; c++)
                {
                    vec[playerColumnNodeIndex[p][c]] = cell(m, p, c);
                }
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
        foreach ((int slot, string name, string[] _) in players)
        {
            // team 2 = T, 3 = CT — alternate so the team dimension is exercised.
            playerInfos[slot] = new PlayerInfo(slot, name, 0UL, slot, slot % 2 == 0 ? 2 : 3, false);
        }

        ParsedDemo demo = SyntheticDemo("de_mirage", playerInfos);
        return (result, demo);
    }

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

    /// <summary>Project_one row per player per live round.</summary>
    [Test]
    public async Task Project_OneRowPerPlayerPerLiveRound()
    {
        // round_number goes 0 (warmup) → 1 → 1 → 2 → 2 → 3 across 6 messages.
        // 3 live rounds, 2 players → 6 rows expected. Each player's "kills" = round number * 10 + slot.
        int[] rounds = [0, 1, 1, 2, 2, 3];
        (int, string, string[])[] players = [(0, "Alice", ["kills"]), (1, "Bob", ["kills"])];

        (EvaluationResult result, ParsedDemo demo) = BuildScenario(
            rounds, players,
            (m, p, _) => Num(rounds[m] * 10 + players[p].Item1));

        MetricTable table = new PlayerRoundStatsProjector
        {
            MatchId = "match.dem"
        }.Project(result, demo).Single();

        await Assert.That(table.Name).IsEqualTo("player_round_stats");
        await Assert.That(table.Rows.Count).IsEqualTo(6); // 3 rounds × 2 players
        await Assert.That(table.ValueColumns).Contains("kills");

        // Distinct rounds present, no warmup (round 0).
        IEnumerable<int> distinctRounds = table.Rows
            .Select(r => AsInt(r.Dimensions["round_number"]))
            .Distinct()
            .Order();
        await Assert.That(distinctRounds).IsEquivalentTo(_expectedThreeRounds);
    }

    /// <summary>Project_samples value at last snapshot index per round.</summary>
    [Test]
    public async Task Project_SamplesValueAtLastSnapshotIndexPerRound()
    {
        // Round 1 spans messages 1 and 2; kills rises 1 → 5. The end-of-round sample must take the LAST
        // index (5), not the first (1) — proving last-index-per-round semantics.
        int[] rounds = [0, 1, 1, 2];
        (int, string, string[])[] players = [(0, "Alice", ["kills"])];
        int[] aliceKills = [0, 1, 5, 9]; // by message index

        (EvaluationResult result, ParsedDemo demo) = BuildScenario(
            rounds, players,
            (m, _, _) => Num(aliceKills[m]));

        MetricTable table = new PlayerRoundStatsProjector().Project(result, demo).Single();

        MetricRow round1 = table.Rows.Single(r => AsInt(r.Dimensions["round_number"]) == 1);
        MetricRow round2 = table.Rows.Single(r => AsInt(r.Dimensions["round_number"]) == 2);

        await Assert.That(AsInt(round1.Values["kills"])).IsEqualTo(5); // last index of round 1
        await Assert.That(AsInt(round2.Values["kills"])).IsEqualTo(9);
    }

    /// <summary>Project_inactive node yields null value.</summary>
    [Test]
    public async Task Project_InactiveNodeYieldsNullValue()
    {
        int[] rounds = [1];
        (int, string, string[])[] players = [(0, "Alice", ["kills"])];

        (EvaluationResult result, ParsedDemo demo) = BuildScenario(
            rounds, players,
            (_, _, _) => Inactive()); // node never active → not-reported

        MetricRow row = new PlayerRoundStatsProjector().Project(result, demo).Single().Rows.Single();
        await Assert.That(row.Values["kills"]).IsNull();
    }

    /// <summary>Project_dimensions populated from demo and player.</summary>
    [Test]
    public async Task Project_DimensionsPopulatedFromDemoAndPlayer()
    {
        int[] rounds = [1];
        (int, string, string[])[] players = [(2, "Carol", ["kills"])];

        (EvaluationResult result, ParsedDemo demo) = BuildScenario(
            rounds, players,
            (_, _, _) => Num(3));

        MetricRow row = new PlayerRoundStatsProjector
        {
            MatchId = "my-match.dem"
        }.Project(result, demo).Single().Rows.Single();

        await Assert.That(row.Dimensions["match_id"]).IsEqualTo("my-match.dem");
        await Assert.That(row.Dimensions["map"]).IsEqualTo("de_mirage");
        await Assert.That(AsInt(row.Dimensions["round_number"])).IsEqualTo(1);
        await Assert.That(AsInt(row.Dimensions["player_slot"])).IsEqualTo(2);
        await Assert.That(row.Dimensions["player_name"]).IsEqualTo("Carol");
        await Assert.That(AsInt(row.Dimensions["team"])).IsEqualTo(2); // slot 2 → team 2
    }

    /// <summary>Project_omits match id dimension when not set.</summary>
    [Test]
    public async Task Project_OmitsMatchIdDimensionWhenNotSet()
    {
        int[] rounds = [1];
        (int, string, string[])[] players = [(0, "Alice", ["kills"])];

        (EvaluationResult result, ParsedDemo demo) = BuildScenario(
            rounds, players,
            (_, _, _) => Num(1));

        MetricRow row = new PlayerRoundStatsProjector().Project(result, demo).Single().Rows.Single();
        await Assert.That(row.Dimensions.ContainsKey("match_id")).IsFalse();
    }

    /// <summary>Project_no live rounds produces empty rows.</summary>
    [Test]
    public async Task Project_NoLiveRoundsProducesEmptyRows()
    {
        // All warmup (round_number == 0) → no rows, but the table still declares its schema.
        int[] rounds = [0, 0, 0];
        (int, string, string[])[] players = [(0, "Alice", ["kills"])];

        (EvaluationResult result, ParsedDemo demo) = BuildScenario(
            rounds, players,
            (_, _, _) => Num(0));

        MetricTable table = new PlayerRoundStatsProjector().Project(result, demo).Single();
        await Assert.That(table.Rows).IsEmpty();
        await Assert.That(table.ValueColumns).Contains("kills");
    }

    /// <summary>A minimal concrete StateNode — the projector only uses reference identity + Name.</summary>
    private sealed class StubNode(string name) : StateNode
    {
        public override bool IsActive => true;
        public override string Name { get; } = name;
    }

    /// <summary>
    ///     Per-player stat stubs are ROUND-scoped (they model reset:round counters) — the projector
    ///     now filters the per-round table to <see cref="IRoundScopedNode" /> columns only.
    /// </summary>
    private sealed class RoundStubNode(string name) : StateNode, IRoundScopedNode
    {
        public override bool IsActive => true;
        public override string Name { get; } = name;

        public void Reset()
        {
        }
    }
}
