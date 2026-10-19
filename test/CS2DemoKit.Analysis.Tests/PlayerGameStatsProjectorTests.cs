#region

using System.Globalization;
using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Analysis.Graphs;
using CS2DemoKit.Analysis.Output;
using CS2DemoKit.Analysis.Yaml;
using CS2DemoKit.Parser;
using CS2DemoKit.TestSupport;

#endregion

namespace CS2DemoKit.Analysis.Tests;

/// <summary>
///     Tests for <see cref="PlayerGameStatsProjector" /> driven by a synthetic
///     <see cref="EvaluationResult" /> (no demo file) — mirrors the
///     <see cref="PlayerRoundStatsProjectorTests" /> fixture. Exercises final-snapshot sampling
///     (the end-of-match scoreboard read), the shared dimension schema, active-gating, and the
///     schema-only contract for empty results.
/// </summary>
[Category("Unit")]
public class PlayerGameStatsProjectorTests
{
    private static readonly string[] _expectedDimensionColumns =
        ["match_id", "map", "player_slot", "player_name", "team"];

    private static readonly string[] _expectedUnionColumns = ["kills", "deaths"];

    private static NodeSnapshot Num(int value) => new(true, value.ToString(CultureInfo.InvariantCulture), value);
    private static NodeSnapshot Inactive() => new(false);
    private static int AsInt(object? value) => Convert.ToInt32(value, CultureInfo.InvariantCulture);

    /// <summary>
    ///     Builds a synthetic result: per-player stat nodes tracked in declaration order, one snapshot
    ///     vector per message index; <paramref name="cell" />(messageIdx, playerIdx, colIdx) supplies cells.
    /// </summary>
    private static (EvaluationResult Result, ParsedDemo Demo) BuildScenario(
        int messageCount,
        (int Slot, string Name, string[] Columns)[] players,
        Func<int, int, int, NodeSnapshot> cell)
    {
        List<StateNode> tracked = [];
        List<PerPlayerNodeTemplate.MaterializedPlayer> materialized = new();
        List<List<int>> playerColumnNodeIndex = new();

        foreach ((int slot, string name, string[] columns) in players)
        {
            List<PerPlayerColumnAssignment> assignments = new();
            List<int> colIndices = new();
            List<StateNode> nodes = new();
            foreach (string column in columns)
            {
                StubNode node = new($"{name}_{column}");
                colIndices.Add(tracked.Count);
                tracked.Add(node);
                nodes.Add(node);
                assignments.Add(new PerPlayerColumnAssignment(node, column));
            }

            playerColumnNodeIndex.Add(colIndices);
            materialized.Add(new PerPlayerNodeTemplate.MaterializedPlayer(
                slot, name, nodes, [], assignments, []));
        }

        NodeSnapshot[][] snapshots = new NodeSnapshot[messageCount][];
        for (int m = 0; m < messageCount; m++)
        {
            NodeSnapshot[] vec = new NodeSnapshot[tracked.Count];
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
            [],
            tracked,
            materialized,
            []);

        Dictionary<int, PlayerInfo> playerInfos = new();
        foreach ((int slot, string name, string[] _) in players)
        {
            playerInfos[slot] = new PlayerInfo(slot, name, 0UL, slot, slot % 2 == 0 ? 2 : 3, false);
        }

        ParsedDemo demo = new(
            [],
            [],
            playerInfos,
            null,
            "de_mirage",
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

        return (result, demo);
    }

    /// <summary>Project_one row per player, sampled at the FINAL snapshot.</summary>
    [Test]
    public async Task Project_OneRowPerPlayer_FromFinalSnapshot()
    {
        // kills rise across 4 messages: value = messageIdx * 10 + slot. The scoreboard row
        // must hold the LAST message's value (30 + slot), not any earlier one.
        (int, string, string[])[] players = [(0, "Alice", ["kills"]), (1, "Bob", ["kills"])];

        (EvaluationResult result, ParsedDemo demo) = BuildScenario(
            4, players,
            (m, p, _) => Num(m * 10 + players[p].Item1));

        MetricTable table = new PlayerGameStatsProjector
        {
            MatchId = "match.dem"
        }.Project(result, demo).Single();

        await Assert.That(table.Name).IsEqualTo("player_game_stats");
        await Assert.That(table.Rows.Count).IsEqualTo(2);

        MetricRow alice = table.Rows.Single(r => (string?)r.Dimensions["player_name"] == "Alice");
        MetricRow bob = table.Rows.Single(r => (string?)r.Dimensions["player_name"] == "Bob");
        await Assert.That(AsInt(alice.Values["kills"])).IsEqualTo(30);
        await Assert.That(AsInt(bob.Values["kills"])).IsEqualTo(31);
    }

    /// <summary>Project_dimension schema matches the round projector minus round_number.</summary>
    [Test]
    public async Task Project_DimensionSchema_JoinsWithRoundTable()
    {
        (int, string, string[])[] players = [(2, "Carol", ["kills"])];

        (EvaluationResult result, ParsedDemo demo) = BuildScenario(1, players, (_, _, _) => Num(7));

        MetricTable table = new PlayerGameStatsProjector
        {
            MatchId = "my-match.dem"
        }.Project(result, demo).Single();
        MetricRow row = table.Rows.Single();

        await Assert.That(table.DimensionColumns).IsEquivalentTo(_expectedDimensionColumns);
        await Assert.That(row.Dimensions["match_id"]).IsEqualTo("my-match.dem");
        await Assert.That(row.Dimensions["map"]).IsEqualTo("de_mirage");
        await Assert.That(AsInt(row.Dimensions["player_slot"])).IsEqualTo(2);
        await Assert.That(AsInt(row.Dimensions["team"])).IsEqualTo(2);
    }

    /// <summary>Project_inactive node in the final snapshot yields null value.</summary>
    [Test]
    public async Task Project_InactiveNodeYieldsNullValue()
    {
        (int, string, string[])[] players = [(0, "Alice", ["kills"])];

        (EvaluationResult result, ParsedDemo demo) = BuildScenario(2, players, (_, _, _) => Inactive());

        MetricRow row = new PlayerGameStatsProjector().Project(result, demo).Single().Rows.Single();
        await Assert.That(row.Values["kills"]).IsNull();
    }

    /// <summary>Project_no snapshots produces schema-only table.</summary>
    [Test]
    public async Task Project_NoSnapshotsProducesSchemaOnlyTable()
    {
        (int, string, string[])[] players = [(0, "Alice", ["kills"])];

        (EvaluationResult result, ParsedDemo demo) = BuildScenario(0, players, (_, _, _) => Num(0));

        MetricTable table = new PlayerGameStatsProjector().Project(result, demo).Single();
        await Assert.That(table.Rows).IsEmpty();
        await Assert.That(table.ValueColumns).Contains("kills");
    }

    /// <summary>
    ///     Round-scoped columns are excluded from the match table — at the final snapshot they hold
    ///     the LAST round's value, which the scoreboard would otherwise present as a match total
    ///     (the wrong-Kills/Deaths/Assists bug).
    /// </summary>
    [Test]
    public async Task Project_ExcludesRoundScopedColumns()
    {
        // Hand-build: one player with a game-lifetime column and a round-scoped column.
        StubNode total = new("Alice_TotalK");
        RoundScopedStubNode lastRound = new("Alice_Kills");
        List<StateNode> tracked = [total, lastRound];
        PerPlayerNodeTemplate.MaterializedPlayer player = new(
            0, "Alice", tracked, [],
            [
                new PerPlayerColumnAssignment(total, "TotalK"),
                new PerPlayerColumnAssignment(lastRound, "Kills", IsRoundScoped: true)
            ],
            []);

        NodeSnapshot[][] snapshots = [[Num(42), Num(3)]];
        EvaluationResult result = new(new RuleChainTimeline([]), snapshots, [], tracked, [player], []);
        (_, ParsedDemo demo) = BuildScenario(0, [(0, "Alice", Array.Empty<string>())], (_, _, _) => Num(0));

        MetricTable table = new PlayerGameStatsProjector().Project(result, demo).Single();

        await Assert.That(table.ValueColumns).Contains("TotalK");
        await Assert.That(table.ValueColumns).DoesNotContain("Kills");
        MetricRow row = table.Rows.Single();
        await Assert.That(row.Values["TotalK"]).IsEqualTo(42);
        await Assert.That(row.Values.ContainsKey("Kills")).IsFalse();
    }

    /// <summary>Project_value columns are the union across players in first-seen order.</summary>
    [Test]
    public async Task Project_ValueColumnsAreUnionAcrossPlayers()
    {
        // Bob has an extra column Alice lacks; the schema is the union, Alice's missing cell is absent
        // from her Values (schema union lives on the table, not per-row).
        (int, string, string[])[] players = [(0, "Alice", ["kills"]), (1, "Bob", ["kills", "deaths"])];

        (EvaluationResult result, ParsedDemo demo) = BuildScenario(1, players, (_, _, c) => Num(c));

        MetricTable table = new PlayerGameStatsProjector().Project(result, demo).Single();
        await Assert.That(table.ValueColumns).IsEquivalentTo(_expectedUnionColumns);
    }

    /// <summary>
    ///     The shipped rulesets on the trimmed sample, run bare and with snapshots over one build:
    ///     the bare run's scoreboard, read from its final node values, matches the snapshot run's.
    /// </summary>
    [Test]
    [NotInParallel]
    [Category("Integration")]
    public async Task BareRun_MatchesSnapshotRun_RowForRow()
    {
        string path = DemoTestHelper.RequireDemo(DemoTestHelper.SampleDemoFileName);
        ParsedDemo demo = DemoTestHelper.GetOrParse(path);
        RuleConfigLoadResult rules = YamlConfigLoader.LoadShippedEmbedded();
        BuildResult build = DemoAnalysis.Build(demo, rules.Rulesets);

        AnalysisRun bare = DemoAnalysis.Evaluate(demo, build, new AnalysisOptions { CaptureSnapshots = false });
        AnalysisRun snapshot = DemoAnalysis.Evaluate(demo, build, new AnalysisOptions { CaptureSnapshots = true });
        await Assert.That(bare.Snapshots).IsNull();

        PlayerGameStatsProjector projector = new() { MatchId = "sample" };
        MetricTable fromBare = projector.Project(bare, bare.Demo).Single();
        MetricTable fromSnapshot = projector.Project(snapshot, snapshot.Demo).Single();

        await Assert.That(fromSnapshot.Rows.Count).IsGreaterThan(0);
        await Assert.That(fromSnapshot.Rows.Any(r => r.Values.Values.Any(v => v is not null))).IsTrue();
        await Assert.That(RenderTable(fromBare)).IsEqualTo(RenderTable(fromSnapshot));
    }

    /// <summary>The default interface member refuses a run without snapshots rather than projecting nothing.</summary>
    [Test]
    public async Task DefaultRunProjection_WithoutSnapshots_Throws()
    {
        (int, string, string[])[] players = [(0, "Alice", ["kills"])];
        (EvaluationResult result, ParsedDemo demo) = BuildScenario(1, players, (_, _, _) => Num(1));
        IOutputProjector projector = new SnapshotOnlyProjector();
        DemoDescriptor descriptor = DemoDescriptor.From(demo);

        await Assert.That(projector.Project(RunOf(result, descriptor), descriptor).Single().Rows.Count).IsEqualTo(1);
        Assert.Throws<NotSupportedException>(() => projector.Project(RunOf(null, descriptor), descriptor));
    }

    private static AnalysisRun RunOf(EvaluationResult? result, DemoDescriptor demo) =>
        new(null!, new RuleChainTimeline([]), result) { Demo = demo, Provenance = null! };

    internal static string RenderTable(MetricTable table)
    {
        static string Fmt(object? v) => v switch
        {
            null => "null",
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => v.ToString() ?? "null"
        };

        IEnumerable<string> rows = table.Rows
            .Select(r =>
                string.Join("|", table.DimensionColumns.Select(c => Fmt(r.Dimensions.GetValueOrDefault(c)))) + "->" +
                string.Join("|", table.ValueColumns.Select(c =>
                    r.Values.TryGetValue(c, out object? v) ? Fmt(v) : "absent")))
            .Order(StringComparer.Ordinal);
        return $"{table.Name}\ndims={string.Join(",", table.DimensionColumns)}\nvals={string.Join(",", table.ValueColumns)}\n"
               + string.Join("\n", rows);
    }

    private sealed class SnapshotOnlyProjector : IOutputProjector
    {
        public IReadOnlyList<MetricTable> Project(EvaluationResult result, DemoDescriptor demo) =>
            new PlayerGameStatsProjector().Project(result, demo);
    }

    private sealed class StubNode(string name) : StateNode
    {
        public override bool IsActive => true;
        public override string Name { get; } = name;
    }

    /// <summary>
    ///     A round-scoped stub — must be EXCLUDED from the match scoreboard (its final-snapshot
    ///     value is the last round's, not a total; the wrong-Kills/Deaths regression).
    /// </summary>
    private sealed class RoundScopedStubNode(string name) : StateNode, IRoundScopedNode
    {
        public override bool IsActive => true;
        public override string Name { get; } = name;

        public void Reset()
        {
        }
    }
}
