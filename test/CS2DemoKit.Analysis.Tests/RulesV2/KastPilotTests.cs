#region

using System.Globalization;
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
///     The kast corpus-port phase-exit golden, pinned at the Rulesets v2 production cutover: the v2
///     <c>rules/kast.rules.yaml</c>, compiled by the planner and evaluated on the reference demo,
///     must reproduce the numbers captured from the v2==v1-verified run at cutover — both the
///     per-(player, round) round-scoped columns (kills … has_kast, via the shared
///     <see cref="PlayerRoundStatsProjector" />) and the per-player game totals (kast.count, the
///     2K–5K tally targets, kast_pct, read as runtime nodes). It also re-pins the
///     <c>rules/kast.test.yaml</c> numbers (98 / 105 / 105 / 33 / 1633).
///     <para>
///         Numbers pinned from a v2==v1-verified run at the v2 cutover;
///         the v1 files were later removed. The live v1 oracle is gone; the golden now asserts
///         the captured pins in <c>tests/fixtures/rules-v2/kast.expected.json</c>. Regenerate the
///         fixture with <c>PIN_RULES_V2=1</c>. Parses the demo, so <see cref="NotInParallelAttribute" />
///         and the shared parse cache apply.
///     </para>
/// </summary>
[Category("Integration")]
[NotInParallel]
public class KastPilotTests
{
    private const string FixtureName = "kast";

    // rules/kast.test.yaml pins for this reference demo.
    private const int PinnedKastRounds = 98; // chains.kast.satisfied_count
    private const int PinnedKills = 105; // kast.kills.fires
    private const int PinnedDeaths = 105; // kast.deaths.fires
    private const int PinnedAssists = 33; // kast.assists.fires
    private const int PinnedShots = 1633; // kast.shots_fired.fires

    // v2 per-player game-total node ids read node-direct (v2 highlight .count + the tally targets).
    private static readonly string[] _gameTotalNodes = ["kast.count", "rounds_2k", "rounds_3k", "rounds_4k", "rounds_5k"];

    /// <summary>v2 kast == the pinned cutover numbers: per-(player, round) columns, per-player game totals, and the pins.</summary>
    [Test]
    public async Task Kast_MatchesPinnedCutover()
    {
        ParsedDemo demo = DemoTestHelper.GetOrParse(DemoTestHelper.RequireDemo());

        // ── v2: the ported ruleset, through the 2.2c planner + composition seam ──
        BuildResult v2Build = CompileV2(demo);
        AnalysisRun v2Run = DemoAnalysis.Evaluate(demo, v2Build);
        EvaluationResult v2Result = v2Run.Snapshots ?? throw new InvalidOperationException("v2 produced no snapshots");
        MetricTable v2Round = new PlayerRoundStatsProjector().Project(v2Result, demo).Single();

        KastFixture actual = Extract(v2Result, v2Round);

        if (PilotFixture.Regenerate)
        {
            PilotFixture.Write(FindRepoRoot(), FixtureName, actual);
            return;
        }

        KastFixture expected = PilotFixture.Read<KastFixture>(FindRepoRoot(), FixtureName);

        // ── 1. Per-(player, round) round-scoped columns ──
        await Assert.That(actual.RoundColumns).Contains("HasKAST")
            .Because("the has_kast per-round column must be present");
        await Assert.That(actual.RoundColumns.Count).IsGreaterThanOrEqualTo(14)
            .Because("all kast round columns must be present");
        await Assert.That(actual.RoundColumns.ToHashSet()).IsEquivalentTo(expected.RoundColumns.ToHashSet())
            .Because("v2 must project the pinned set of round columns");
        await Assert.That(actual.Rounds.Keys.ToHashSet()).IsEquivalentTo(expected.Rounds.Keys.ToHashSet())
            .Because("v2 must emit the pinned (player, round) rows");

        foreach ((string key, Dictionary<string, long> cols) in expected.Rounds)
        {
            Dictionary<string, long> got = actual.Rounds[key];
            foreach ((string col, long want) in cols)
            {
                await Assert.That(got.GetValueOrDefault(col)).IsEqualTo(want)
                    .Because($"{key} col '{col}': v2 must equal the pinned value (pinned={want} v2={got.GetValueOrDefault(col)})");
            }
        }

        // ── 2. Per-player game totals: kast.count, tally targets, kast_pct ──
        foreach (string id in _gameTotalNodes)
        {
            Dictionary<string, int> want = expected.GameTotals[id];
            Dictionary<string, int> got = actual.GameTotals[id];
            await Assert.That(got.Keys.ToHashSet()).IsEquivalentTo(want.Keys.ToHashSet())
                .Because($"'{id}': v2 must materialize the pinned players");
            foreach ((string slot, int value) in want)
            {
                await Assert.That(got.GetValueOrDefault(slot)).IsEqualTo(value)
                    .Because($"slot{slot} game total '{id}': v2 must equal the pin (pinned={value} v2={got.GetValueOrDefault(slot)})");
            }
        }

        await Assert.That(actual.KastPct.Keys.ToHashSet()).IsEquivalentTo(expected.KastPct.Keys.ToHashSet())
            .Because("kast_pct: v2 must materialize the pinned players");
        foreach ((string slot, double want) in expected.KastPct)
        {
            await Assert.That(Math.Abs(actual.KastPct.GetValueOrDefault(slot) - want)).IsLessThan(1e-6)
                .Because($"slot{slot} kast_pct: v2 must equal the pin (pinned={want:F4} v2={actual.KastPct.GetValueOrDefault(slot):F4})");
        }

        // ── 3. Sanity pins (kast.test.yaml) ──
        int kastRoundsTotal = actual.GameTotals["kast.count"].Values.Sum();
        await Assert.That(kastRoundsTotal).IsEqualTo(PinnedKastRounds)
            .Because("total has_kast satisfied across players must be the pinned 98");
        await Assert.That(SumColumn(v2Round, "Kills")).IsEqualTo(PinnedKills).Because("total kills == 105");
        await Assert.That(SumColumn(v2Round, "Deaths")).IsEqualTo(PinnedDeaths).Because("total deaths == 105");
        await Assert.That(SumColumn(v2Round, "Assists")).IsEqualTo(PinnedAssists).Because("total assists == 33");
        await Assert.That(SumColumn(v2Round, "Shots")).IsEqualTo(PinnedShots).Because("total shots_fired == 1633");
    }

    /// <summary>Captures the pinnable v2 quantities into the fixture DTO (used by both regen and compare).</summary>
    private static KastFixture Extract(EvaluationResult v2Result, MetricTable v2Round)
    {
        Dictionary<string, Dictionary<string, long>> rounds = new(StringComparer.Ordinal);
        foreach (MetricRow row in v2Round.Rows)
        {
            int slot = AsInt(row.Dimensions.GetValueOrDefault("player_slot"));
            int round = AsInt(row.Dimensions.GetValueOrDefault("round_number"));
            Dictionary<string, long> cols = new(StringComparer.Ordinal);
            foreach (string col in v2Round.ValueColumns)
            {
                cols[col] = long.Parse(Display(row.Values.GetValueOrDefault(col)), CultureInfo.InvariantCulture);
            }

            rounds[$"{slot}|{round}"] = cols;
        }

        Dictionary<string, Dictionary<string, int>> gameTotals = new(StringComparer.Ordinal);
        foreach (string id in _gameTotalNodes)
        {
            gameTotals[id] = ReadIntNode(v2Result, id)
                .ToDictionary(kv => kv.Key.ToString(CultureInfo.InvariantCulture), kv => kv.Value, StringComparer.Ordinal);
        }

        Dictionary<string, double> kastPct = ReadDoubleNode(v2Result, "kast_pct")
            .ToDictionary(kv => kv.Key.ToString(CultureInfo.InvariantCulture), kv => kv.Value, StringComparer.Ordinal);

        return new KastFixture([.. v2Round.ValueColumns], rounds, gameTotals, kastPct);
    }

    private static int SumColumn(MetricTable table, string column)
    {
        int total = 0;
        foreach (MetricRow row in table.Rows)
        {
            total += AsInt(row.Values.GetValueOrDefault(column));
        }

        return total;
    }

    /// <summary>Reads a per-player int node's final value into slot -> count.</summary>
    private static Dictionary<int, int> ReadIntNode(EvaluationResult result, string nodeName)
    {
        Dictionary<int, int> bySlot = new();
        foreach (PerPlayerNodeTemplate.MaterializedPlayer mp in result.MaterializedPlayers)
        {
            GenericValueNode<int>? node = mp.Nodes
                .OfType<GenericValueNode<int>>()
                .FirstOrDefault(n => string.Equals(n.Name, nodeName, StringComparison.Ordinal));
            bySlot[mp.PlayerSlot] = node?.Value ?? 0;
        }

        return bySlot;
    }

    /// <summary>Reads a per-player double compute node's final value into slot -> value.</summary>
    private static Dictionary<int, double> ReadDoubleNode(EvaluationResult result, string nodeName)
    {
        Dictionary<int, double> bySlot = new();
        foreach (PerPlayerNodeTemplate.MaterializedPlayer mp in result.MaterializedPlayers)
        {
            ComputedStatNode? node = mp.Nodes
                .OfType<ComputedStatNode>()
                .FirstOrDefault(n => string.Equals(n.Name, nodeName, StringComparison.Ordinal));
            bySlot[mp.PlayerSlot] = node?.Value ?? 0.0;
        }

        return bySlot;
    }

    private static string Display(object? value) =>
        value switch
        {
            null => "0",
            bool b => b ? "1" : "0",
            _ => Convert.ToInt64(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture)
        };

    private static int AsInt(object? value) =>
        value switch
        {
            null => 0,
            bool b => b ? 1 : 0,
            _ => Convert.ToInt32(value, CultureInfo.InvariantCulture)
        };

    /// <summary>
    ///     Compiles the v2 kast ruleset through the full 2.2b resolve + 2.2c planner pipeline against
    ///     the demo's tick rate and source profile, composing it onto an empty v1 config (the
    ///     composition seam). Returns the ready-to-evaluate build.
    /// </summary>
    private static BuildResult CompileV2(ParsedDemo demo)
    {
        string yaml = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "CS2DemoKit.Analysis", "Rules", "kast.rules.yaml"));
        RulesetDocumentLoader.Outcome outcome = RulesetDocumentLoader.Load(yaml, "kast.rules.yaml");
        RulesetDoc doc = outcome.Doc
                         ?? throw new InvalidOperationException(
                             "kast ruleset failed to load: " + string.Join("; ", outcome.Diagnostics));
        if (outcome.Diagnostics.Count > 0)
        {
            throw new InvalidOperationException(
                "kast ruleset has mapping/structural diagnostics: " + string.Join("; ", outcome.Diagnostics));
        }

        RuleChainBuilder builder = new(
            EventRegistry.Build(),
            demo,
            entityProviders: EntityValueProviderRegistry.CreateDefault(),
            perPlayerEntityProviders: PerPlayerEntityValueProviderRegistry.CreateDefault());

        string profileId = builder.Profile.GetType().Name;
        CatalogScopeAdapter adapter = CatalogScopeAdapter.From(CatalogResource.Load());
        RulesetResolveResult resolved = CheckedRulesetDraft.Load(doc, adapter).Build(demo.TickRate, profileId);
        CheckedRuleset ruleset = resolved.Ruleset
                                 ?? throw new InvalidOperationException(
                                     "kast ruleset failed to resolve: " + string.Join("; ", resolved.Diagnostics));

        return builder.Build([ruleset]);
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

/// <summary>Pinned kast pilot expectations (see <see cref="PilotFixture" />).</summary>
internal sealed record KastFixture(
    List<string> RoundColumns,
    Dictionary<string, Dictionary<string, long>> Rounds,
    Dictionary<string, Dictionary<string, int>> GameTotals,
    Dictionary<string, double> KastPct);
