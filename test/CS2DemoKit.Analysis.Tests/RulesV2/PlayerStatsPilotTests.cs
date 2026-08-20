#region

using System.Globalization;
using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Analysis.Building;
using CS2DemoKit.Analysis.Catalog;
using CS2DemoKit.Analysis.Config;
using CS2DemoKit.Analysis.Graphs;
using CS2DemoKit.Analysis.Nodes;
using CS2DemoKit.Analysis.Output;
using CS2DemoKit.Analysis.Plugins;
using CS2DemoKit.Analysis.Registry;
using CS2DemoKit.Analysis.RulesetsV2.Model;
using CS2DemoKit.Analysis.RulesetsV2.Resolve;
using CS2DemoKit.Analysis.Yaml;
using CS2DemoKit.Parser;
using CS2DemoKit.TestSupport;

#endregion

namespace CS2DemoKit.Analysis.Tests.RulesV2;

/// <summary>
///     The player-stats corpus-port phase-exit golden, pinned at the Rulesets v2 production cutover:
///     the v2 <c>rules/player_stats.rules.yaml</c> (composed with <c>rules/kast.rules.yaml</c> so
///     HLTV's kast_pct read resolves), compiled by the planner and evaluated on the reference demo,
///     must reproduce the numbers captured from the v2==v1-verified run at cutover, for every column
///     ported: counters incl. grenade usage, entity-read stats (Armor/AvgHP→Dmg/Equip), the computed
///     metrics (ADR/HLTV/DuelWin%/…), the streak (RapidKills), TotalBlindDuration, and the round
///     columns (FK/FD/DeagleHS).
///     <para>
///         Numbers pinned from a v2==v1-verified run at the v2 cutover;
///         the v1 files were later removed. The live v1 oracle (the whole shipped <c>rules/</c>
///         directory) is gone; the golden now asserts the captured pins in
///         <c>tests/fixtures/rules-v2/player_stats.expected.json</c>. Regenerate with
///         <c>PIN_RULES_V2=1</c>.
///     </para>
/// </summary>
[Category("Integration")]
[NotInParallel]
public class PlayerStatsPilotTests
{
    private const string FixtureName = "player_stats";

    // Game-group INT columns compared by projector label (incl. grenade-usage HE/Flash/Smokes/Molly).
    private static readonly string[] _gameIntColumns =
    [
        "TotalK", "TotalD", "TotalA", "EnemyDmg", "TeamDmg", "SelfDmg", "TotalHS", "FlashAst", "NoScope",
        "WB", "Smoke", "Blind", "Shots", "HE", "Flash", "Smokes", "Molly", "Plants", "Defuses", "Revenge",
        "AWP", "Pistol", "Rifle", "SMG", "Knife", "Survived", "Clutch", "EFlash", "Armor", "FlashK",
        "HitFoe", "HitTeam", "TrdK", "CTFK", "CTFD", "TFK", "TFD", "CTW", "CTL", "TW", "TL",
        "TotalFK", "TotalFD", "DeagleHSRnds", "TradedD"
    ];

    // Computed metrics, compared node-direct (ComputedStatNode) by stat id.
    private static readonly string[] _computeIds =
    [
        "ADR", "HS%", "KPR", "KD", "Surv%", "HLTV", "AvgHealthWhileDamaging",
        "AvgEquipmentValue", "AvgBlind", "DuelWin%", "FK±"
    ];

    // Round-group columns compared by round-projector label.
    private static readonly string[] _roundColumns = ["FK", "FD", "DeagleHS"];

    [Test]
    public async Task PlayerStats_MatchesPinnedCutover()
    {
        ParsedDemo demo = DemoTestHelper.GetOrParse(DemoTestHelper.RequireDemo());

        BuildResult v2Build = CompileV2(demo);
        AnalysisRun v2Run = DemoAnalysis.Evaluate(demo, v2Build);
        EvaluationResult v2 = v2Run.Snapshots ?? throw new InvalidOperationException("v2 no snapshots");

        PlayerStatsFixture actual = Extract(v2, demo);

        if (PilotFixture.Regenerate)
        {
            PilotFixture.Write(FindRepoRoot(), FixtureName, actual);
            throw PilotFixture.Repinned(FixtureName);
        }

        PlayerStatsFixture expected = PilotFixture.Read<PlayerStatsFixture>(FindRepoRoot(), FixtureName);

        // ── 1. Game-group int columns ──
        await Assert.That(actual.GameInt.Keys.ToHashSet()).IsEquivalentTo(expected.GameInt.Keys.ToHashSet())
            .Because("v2 must materialize the pinned players");
        foreach ((string slot, Dictionary<string, long> want) in expected.GameInt)
        {
            Dictionary<string, long> got = actual.GameInt[slot];
            foreach ((string col, long value) in want)
            {
                await Assert.That(got.GetValueOrDefault(col)).IsEqualTo(value)
                    .Because($"slot{slot} game col '{col}': v2 must equal the pin (pinned={value} v2={got.GetValueOrDefault(col)})");
            }
        }

        // ── 2. Computed metrics (double tolerance) ──
        foreach ((string id, Dictionary<string, double> want) in expected.Compute)
        {
            Dictionary<string, double> got = actual.Compute[id];
            await Assert.That(got.Keys.ToHashSet()).IsEquivalentTo(want.Keys.ToHashSet())
                .Because($"compute '{id}': same players");
            foreach ((string slot, double value) in want)
            {
                await Assert.That(Math.Abs(got.GetValueOrDefault(slot) - value)).IsLessThan(1e-6)
                    .Because($"slot{slot} compute '{id}': v2 must equal the pin (pinned={value:F6} v2={got.GetValueOrDefault(slot):F6})");
            }
        }

        // ── 3. Streak (RapidKills) ──
        foreach ((string slot, int value) in expected.Rapid)
        {
            await Assert.That(actual.Rapid.GetValueOrDefault(slot)).IsEqualTo(value)
                .Because($"slot{slot} rapid_kill_sequences: v2 must equal the pin (pinned={value} v2={actual.Rapid.GetValueOrDefault(slot)})");
        }

        // ── 3b. TotalBlindDuration (Σfloor(d) is integer-valued so v2's float sum must be bit-equal) ──
        await Assert.That(actual.BlindDuration.Keys.ToHashSet()).IsEquivalentTo(expected.BlindDuration.Keys.ToHashSet())
            .Because("TotalBlindDuration: same players");
        foreach ((string slot, double value) in expected.BlindDuration)
        {
            await Assert.That(actual.BlindDuration.GetValueOrDefault(slot)).IsEqualTo(value)
                .Because($"slot{slot} TotalBlindDuration: v2 must equal the pin (pinned={value} v2={actual.BlindDuration.GetValueOrDefault(slot)})");
        }

        // ── 4. Round-group columns (per (slot, round)) ──
        await Assert.That(actual.Round.Keys.ToHashSet()).IsEquivalentTo(expected.Round.Keys.ToHashSet())
            .Because("v2 must emit the pinned (player, round) rows");
        foreach ((string key, Dictionary<string, long> want) in expected.Round)
        {
            Dictionary<string, long> got = actual.Round[key];
            foreach ((string col, long value) in want)
            {
                await Assert.That(got.GetValueOrDefault(col)).IsEqualTo(value)
                    .Because($"{key} col '{col}': v2 must equal the pin (pinned={value} v2={got.GetValueOrDefault(col)})");
            }
        }
    }

    /// <summary>Captures the pinnable v2 quantities into the fixture DTO (used by both regen and compare).</summary>
    private static PlayerStatsFixture Extract(EvaluationResult v2, ParsedDemo demo)
    {
        Dictionary<int, MetricRow> g = RichestBySlot(new PlayerGameStatsProjector().Project(v2, demo).Single());
        Dictionary<string, Dictionary<string, long>> gameInt = new(StringComparer.Ordinal);
        foreach ((int slot, MetricRow row) in g)
        {
            Dictionary<string, long> cols = new(StringComparer.Ordinal);
            foreach (string col in _gameIntColumns)
            {
                cols[col] = AsLong(row.Values.GetValueOrDefault(col));
            }

            gameInt[slot.ToString(CultureInfo.InvariantCulture)] = cols;
        }

        Dictionary<string, Dictionary<string, double>> compute = new(StringComparer.Ordinal);
        foreach (string id in _computeIds)
        {
            compute[id] = ReadDoubleBySlot(v2, id)
                .ToDictionary(kv => kv.Key.ToString(CultureInfo.InvariantCulture), kv => kv.Value, StringComparer.Ordinal);
        }

        Dictionary<string, int> rapid = ReadIntBySlot(v2, "rapid_kill_sequences")
            .ToDictionary(kv => kv.Key.ToString(CultureInfo.InvariantCulture), kv => kv.Value, StringComparer.Ordinal);

        Dictionary<string, double> blind = ReadNumericBySlot(v2, "TotalBlindDuration")
            .ToDictionary(kv => kv.Key.ToString(CultureInfo.InvariantCulture), kv => kv.Value, StringComparer.Ordinal);

        Dictionary<(int, int), MetricRow> rr = RichestByPlayerRound(new PlayerRoundStatsProjector().Project(v2, demo).Single());
        Dictionary<string, Dictionary<string, long>> round = new(StringComparer.Ordinal);
        foreach (((int slot, int rn) key, MetricRow row) in rr)
        {
            Dictionary<string, long> cols = new(StringComparer.Ordinal);
            foreach (string col in _roundColumns)
            {
                cols[col] = AsLong(row.Values.GetValueOrDefault(col));
            }

            round[$"{key.slot}|{key.rn}"] = cols;
        }

        return new PlayerStatsFixture(gameInt, compute, rapid, blind, round);
    }

    /// <summary>Per slot, keep the row with the most non-null value cells (drops context-only phantom rows).</summary>
    private static Dictionary<int, MetricRow> RichestBySlot(MetricTable table)
    {
        Dictionary<int, MetricRow> byKey = new();
        foreach (MetricRow row in table.Rows)
        {
            int slot = AsInt(row.Dimensions.GetValueOrDefault("player_slot"));
            if (!byKey.TryGetValue(slot, out MetricRow? existing) || NonNull(row) > NonNull(existing))
            {
                byKey[slot] = row;
            }
        }

        return byKey;
    }

    private static Dictionary<(int, int), MetricRow> RichestByPlayerRound(MetricTable table)
    {
        Dictionary<(int, int), MetricRow> byKey = new();
        foreach (MetricRow row in table.Rows)
        {
            int slot = AsInt(row.Dimensions.GetValueOrDefault("player_slot"));
            int round = AsInt(row.Dimensions.GetValueOrDefault("round_number"));
            (int, int) key = (slot, round);
            if (!byKey.TryGetValue(key, out MetricRow? existing) || NonNull(row) > NonNull(existing))
            {
                byKey[key] = row;
            }
        }

        return byKey;
    }

    private static int NonNull(MetricRow row) => row.Values.Count(kv => kv.Value is not null);

    /// <summary>Reads a per-player int node's final value into slot -> count (skip-null so phantoms don't clobber).</summary>
    private static Dictionary<int, int> ReadIntBySlot(EvaluationResult result, string nodeName)
    {
        Dictionary<int, int> bySlot = new();
        foreach (PerPlayerNodeTemplate.MaterializedPlayer mp in result.MaterializedPlayers)
        {
            ValueNode<int>? node = mp.Nodes.OfType<ValueNode<int>>()
                .FirstOrDefault(n => string.Equals(n.Name, nodeName, StringComparison.Ordinal));
            if (node is not null)
            {
                bySlot[mp.PlayerSlot] = node.Value;
            }
        }

        return bySlot;
    }

    /// <summary>
    ///     Reads a per-player node's final <c>Value</c> as a double regardless of its concrete node
    ///     type (v2's float sum node). Skips phantom rows whose node is absent.
    /// </summary>
    private static Dictionary<int, double> ReadNumericBySlot(EvaluationResult result, string nodeName)
    {
        Dictionary<int, double> bySlot = new();
        foreach (PerPlayerNodeTemplate.MaterializedPlayer mp in result.MaterializedPlayers)
        {
            StateNode? node = mp.Nodes
                .FirstOrDefault(n => string.Equals(n.Name, nodeName, StringComparison.Ordinal)
                                     && n.GetType().GetProperty("Value") is not null);
            object? value = node?.GetType().GetProperty("Value")?.GetValue(node);
            if (value is not null)
            {
                bySlot[mp.PlayerSlot] = Convert.ToDouble(value, CultureInfo.InvariantCulture);
            }
        }

        return bySlot;
    }

    /// <summary>Reads a per-player compute node's final value into slot -> value (skip-null).</summary>
    private static Dictionary<int, double> ReadDoubleBySlot(EvaluationResult result, string nodeName)
    {
        Dictionary<int, double> bySlot = new();
        foreach (PerPlayerNodeTemplate.MaterializedPlayer mp in result.MaterializedPlayers)
        {
            ComputedStatNode? node = mp.Nodes.OfType<ComputedStatNode>()
                .FirstOrDefault(n => string.Equals(n.Name, nodeName, StringComparison.Ordinal));
            if (node is not null)
            {
                bySlot[mp.PlayerSlot] = node.Value;
            }
        }

        return bySlot;
    }

    private static long AsLong(object? v) =>
        v switch
        {
            null => 0,
            bool b => b ? 1 : 0,
            _ => Convert.ToInt64(v, CultureInfo.InvariantCulture)
        };

    private static int AsInt(object? v) => v is null ? 0 : Convert.ToInt32(v, CultureInfo.InvariantCulture);

    private static BuildResult CompileV2(ParsedDemo demo)
    {
        // Cross-ruleset composition: player_stats' HLTV reads kast.kast_pct,
        // so BOTH documents compile together — the export graph includes kast, and the shared
        // per-player template co-locates kast's kast_pct node for HLTV to read.
        RulesetDoc kast = LoadDoc("kast.rules.yaml");
        RulesetDoc playerStats = LoadDoc("player_stats.rules.yaml");

        RuleChainBuilder builder = new(
            EventRegistry.Build(), demo,
            entityProviders: EntityValueProviderRegistry.CreateDefault(),
            perPlayerEntityProviders: PerPlayerEntityValueProviderRegistry.CreateDefault());

        CatalogScopeAdapter adapter = CatalogScopeAdapter.From(CatalogResource.Load());
        RulesetComposition.Result composed =
            RulesetComposition.Compose([kast, playerStats], adapter, demo.TickRate, builder.Profile.GetType().Name);
        if (!composed.Success)
        {
            throw new InvalidOperationException("compose failed: " + string.Join("; ", composed.Diagnostics));
        }

        return builder.Build(composed.Rulesets);
    }

    private static RulesetDoc LoadDoc(string fileName)
    {
        string yaml = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "CS2DemoKit.Analysis", "Rules", fileName));
        RulesetDocumentLoader.Outcome outcome = RulesetDocumentLoader.Load(yaml, fileName);
        if (outcome.Diagnostics.Count > 0)
        {
            throw new InvalidOperationException($"{fileName} load diagnostics: " + string.Join("; ", outcome.Diagnostics));
        }

        return outcome.Doc ?? throw new InvalidOperationException($"{fileName} load failed");
    }

    private static string FindRepoRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "CS2DemoKit.slnx")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("repo root not found");
    }
}

/// <summary>Pinned player-stats pilot expectations (see <see cref="PilotFixture" />).</summary>
internal sealed record PlayerStatsFixture(
    Dictionary<string, Dictionary<string, long>> GameInt,
    Dictionary<string, Dictionary<string, double>> Compute,
    Dictionary<string, int> Rapid,
    Dictionary<string, double> BlindDuration,
    Dictionary<string, Dictionary<string, long>> Round);
