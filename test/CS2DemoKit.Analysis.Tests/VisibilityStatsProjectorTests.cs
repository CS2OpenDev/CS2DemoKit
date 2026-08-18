#region

using System.Globalization;
using CS2DemoKit.Analysis.Output;
using CS2DemoKit.Analysis.Visibility;
using CS2DemoKit.Parser;

#endregion

namespace CS2DemoKit.Analysis.Tests;

/// <summary>
///     Pure mapping tests for <see cref="VisibilityStatsProjector" /> (deferred-features plan F4 —
///     3D visibility stat columns), driven by a synthetic <see cref="VisibilityAnalyzer.Report" />
///     (no demo file, no raycasts — the engine itself is oracle-tested elsewhere). Covers the two
///     table schemas, per-slot union rows, share division (including the SampledSeconds==0 guard),
///     player name/team enrichment from <see cref="ParsedDemo.Players" />, pair ordering, and the
///     match_id-omitted-when-null convention shared with the scoreboard projectors.
/// </summary>
[Category("Unit")]
public class VisibilityStatsProjectorTests
{
    private static readonly string[] _expectedPlayerDimensions =
        ["match_id", "map", "player_slot", "player_name", "team"];

    private static readonly string[] _expectedPlayerValues =
        ["ExposedToEnemiesSec", "CouldSeeEnemySec", "ExposedShare", "VisionShare"];

    private static readonly string[] _expectedPairDimensions =
        ["match_id", "map", "viewer_slot", "viewer_name", "target_slot", "target_name"];

    private static readonly string[] _expectedPairValues = ["exposed_sec", "could_see_sec"];

    /// <summary>Two known players (Alice slot 0 / T, Bob slot 1 / CT); slot 7 is deliberately absent.</summary>
    private static ParsedDemo BuildDemo()
    {
        Dictionary<int, PlayerInfo> players = new()
        {
            [0] = new PlayerInfo(0, "Alice", 0UL, 0, 2, false),
            [1] = new PlayerInfo(1, "Bob", 0UL, 1, 3, false)
        };

        return new ParsedDemo(
            [],
            [],
            players,
            null,
            "de_anytown",
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
    }

    /// <summary>Report: 20 s sampled; Alice saw 4 s / exposed 2 s, Bob saw 1 s / exposed 10 s.</summary>
    private static VisibilityAnalyzer.Report BuildReport() => new(
        [
            new VisibilityAnalyzer.PairStat(1, 0, 2.0, 1.0),
            new VisibilityAnalyzer.PairStat(0, 1, 10.0, 4.0)
        ],
        new Dictionary<int, double>
        {
            [0] = 4.0,
            [1] = 1.0
        },
        new Dictionary<int, double>
        {
            [1] = 10.0,
            [0] = 2.0
        },
        320,
        20.0);

    private static double Value(MetricRow row, string column) =>
        Convert.ToDouble(row.Values[column], CultureInfo.InvariantCulture);

    // ── player_visibility_stats ───────────────────────────────────────────────

    [Test]
    public async Task PlayerTable_HasSchema_RowsAndShares()
    {
        IReadOnlyList<MetricTable> tables = new VisibilityStatsProjector
            {
                MatchId = "match.dem"
            }
            .Project(BuildReport(), BuildDemo());

        await Assert.That(tables.Count).IsEqualTo(2);
        MetricTable players = tables[0];

        await Assert.That(players.Name).IsEqualTo("player_visibility_stats");
        await Assert.That(players.DimensionColumns).IsEquivalentTo(_expectedPlayerDimensions);
        await Assert.That(players.ValueColumns).IsEquivalentTo(_expectedPlayerValues);
        await Assert.That(players.Rows.Count).IsEqualTo(2);

        MetricRow alice = players.Rows.Single(r => (int)r.Dimensions["player_slot"]! == 0);
        await Assert.That(alice.Dimensions["match_id"]).IsEqualTo("match.dem");
        await Assert.That(alice.Dimensions["map"]).IsEqualTo("de_anytown");
        await Assert.That(alice.Dimensions["player_name"]).IsEqualTo("Alice");
        await Assert.That(alice.Dimensions["team"]).IsEqualTo(2);
        await Assert.That(Value(alice, "ExposedToEnemiesSec")).IsEqualTo(2.0);
        await Assert.That(Value(alice, "CouldSeeEnemySec")).IsEqualTo(4.0);
        await Assert.That(Value(alice, "ExposedShare")).IsEqualTo(0.1); // 2 / 20
        await Assert.That(Value(alice, "VisionShare")).IsEqualTo(0.2); // 4 / 20

        MetricRow bob = players.Rows.Single(r => (int)r.Dimensions["player_slot"]! == 1);
        await Assert.That(Value(bob, "ExposedShare")).IsEqualTo(0.5); // 10 / 20
        await Assert.That(Value(bob, "VisionShare")).IsEqualTo(0.05); // 1 / 20
    }

    /// <summary>A slot in only ONE accumulator still gets a row, with 0 for the other value.</summary>
    [Test]
    public async Task PlayerTable_UnionsSlots_AcrossBothAccumulators()
    {
        VisibilityAnalyzer.Report report = new(
            [],
            new Dictionary<int, double>
            {
                [0] = 3.0
            },
            new Dictionary<int, double>
            {
                [1] = 6.0
            },
            100,
            10.0);

        MetricTable players = new VisibilityStatsProjector().Project(report, BuildDemo())[0];

        await Assert.That(players.Rows.Count).IsEqualTo(2);
        MetricRow alice = players.Rows.Single(r => (int)r.Dimensions["player_slot"]! == 0);
        await Assert.That(Value(alice, "ExposedToEnemiesSec")).IsEqualTo(0.0);
        await Assert.That(Value(alice, "CouldSeeEnemySec")).IsEqualTo(3.0);
    }

    /// <summary>SampledSeconds == 0 → shares are 0 (no division blow-up).</summary>
    [Test]
    public async Task PlayerTable_ZeroSampledSeconds_YieldsZeroShares()
    {
        VisibilityAnalyzer.Report report = new(
            [],
            new Dictionary<int, double>
            {
                [0] = 0.0
            },
            new Dictionary<int, double>(),
            0,
            0.0);

        MetricTable players = new VisibilityStatsProjector().Project(report, BuildDemo())[0];

        await Assert.That(players.Rows.Count).IsEqualTo(1);
        await Assert.That(Value(players.Rows[0], "ExposedShare")).IsEqualTo(0.0);
        await Assert.That(Value(players.Rows[0], "VisionShare")).IsEqualTo(0.0);
    }

    /// <summary>Null MatchId omits the dimension from rows (the shared projector convention).</summary>
    [Test]
    public async Task NullMatchId_OmitsDimension_FromBothTables()
    {
        IReadOnlyList<MetricTable> tables = new VisibilityStatsProjector().Project(BuildReport(), BuildDemo());

        await Assert.That(tables[0].Rows.All(r => !r.Dimensions.ContainsKey("match_id"))).IsTrue();
        await Assert.That(tables[1].Rows.All(r => !r.Dimensions.ContainsKey("match_id"))).IsTrue();
        // The column stays in the schema either way.
        await Assert.That(tables[0].DimensionColumns).Contains("match_id");
    }

    /// <summary>An unknown slot (not in ParsedDemo.Players) degrades to a placeholder name, team 0.</summary>
    [Test]
    public async Task UnknownSlot_GetsPlaceholderName_AndTeamZero()
    {
        VisibilityAnalyzer.Report report = new(
            [new VisibilityAnalyzer.PairStat(7, 0, 1.0, 0.5)],
            new Dictionary<int, double>
            {
                [7] = 0.5
            },
            new Dictionary<int, double>(),
            10,
            1.0);

        IReadOnlyList<MetricTable> tables = new VisibilityStatsProjector().Project(report, BuildDemo());

        MetricRow row = tables[0].Rows.Single(r => (int)r.Dimensions["player_slot"]! == 7);
        await Assert.That(row.Dimensions["player_name"]).IsEqualTo("slot 7");
        await Assert.That(row.Dimensions["team"]).IsEqualTo(0);

        MetricRow pair = tables[1].Rows.Single();
        await Assert.That(pair.Dimensions["viewer_name"]).IsEqualTo("slot 7");
        await Assert.That(pair.Dimensions["target_name"]).IsEqualTo("Alice");
    }

    // ── visibility_pairs ──────────────────────────────────────────────────────

    [Test]
    public async Task PairsTable_HasSchema_ValuesAndStableOrdering()
    {
        MetricTable pairs = new VisibilityStatsProjector
            {
                MatchId = "match.dem"
            }
            .Project(BuildReport(), BuildDemo())[1];

        await Assert.That(pairs.Name).IsEqualTo("visibility_pairs");
        await Assert.That(pairs.DimensionColumns).IsEquivalentTo(_expectedPairDimensions);
        await Assert.That(pairs.ValueColumns).IsEquivalentTo(_expectedPairValues);
        await Assert.That(pairs.Rows.Count).IsEqualTo(2);

        // Ordered by (viewer_slot, target_slot) regardless of report order.
        MetricRow first = pairs.Rows[0];
        await Assert.That(first.Dimensions["viewer_slot"]).IsEqualTo(0);
        await Assert.That(first.Dimensions["viewer_name"]).IsEqualTo("Alice");
        await Assert.That(first.Dimensions["target_slot"]).IsEqualTo(1);
        await Assert.That(first.Dimensions["target_name"]).IsEqualTo("Bob");
        await Assert.That(Value(first, "exposed_sec")).IsEqualTo(10.0);
        await Assert.That(Value(first, "could_see_sec")).IsEqualTo(4.0);

        MetricRow second = pairs.Rows[1];
        await Assert.That(second.Dimensions["viewer_slot"]).IsEqualTo(1);
        await Assert.That(Value(second, "exposed_sec")).IsEqualTo(2.0);
    }

    /// <summary>An empty report yields schema-only tables (0 rows), same contract as the built-ins.</summary>
    [Test]
    public async Task EmptyReport_YieldsSchemaOnlyTables()
    {
        VisibilityAnalyzer.Report report = new(
            [],
            new Dictionary<int, double>(),
            new Dictionary<int, double>(),
            0,
            0.0);

        IReadOnlyList<MetricTable> tables = new VisibilityStatsProjector().Project(report, BuildDemo());

        await Assert.That(tables[0].Rows.Count).IsEqualTo(0);
        await Assert.That(tables[1].Rows.Count).IsEqualTo(0);
        await Assert.That(tables[0].ValueColumns).IsEquivalentTo(_expectedPlayerValues);
        await Assert.That(tables[1].ValueColumns).IsEquivalentTo(_expectedPairValues);
    }
}
