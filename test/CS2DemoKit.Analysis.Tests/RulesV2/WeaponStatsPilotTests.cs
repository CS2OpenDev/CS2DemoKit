#region

using System.Globalization;
using CS2DemoKit.Analysis.Building;
using CS2DemoKit.Analysis.Catalog;
using CS2DemoKit.Analysis.Config;
using CS2DemoKit.Analysis.Graphs;
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
///     The C8 bucket-sum corpus-port golden, pinned at the Rulesets v2 production cutover:
///     <c>rules/weapon_stats.rules.yaml</c> (v2 — a count bucket <c>kills_by_weapon</c> and the
///     single-value SUM bucket <c>damage_by_weapon</c>), compiled by the 2.2c planner and evaluated
///     on the reference demo, must produce per-(player, weapon) buckets identical to the numbers
///     captured from the v2==v1-verified run at cutover. Both are read through the shared
///     <see cref="KeyedStatsProjector" /> output.
///     <para>
///         Numbers pinned from a v2==v1-verified run at the v2 cutover;
///         the v1 files were later removed. The live v1 oracle (<c>rules/weapon-stats.yaml</c>)
///         is gone; the golden now asserts the captured pins in
///         <c>tests/fixtures/rules-v2/weapon_stats.expected.json</c>. Regenerate with
///         <c>PIN_RULES_V2=1</c>. Parses the demo, so <see cref="NotInParallelAttribute" /> and the
///         shared parse cache apply.
///     </para>
/// </summary>
[Category("Integration")]
[NotInParallel]
public class WeaponStatsPilotTests
{
    private const string KillsTable = "player_kills_by_weapon";
    private const string DamageTable = "player_damage_by_weapon";
    private const string FixtureName = "weapon_stats";

    /// <summary>v2 buckets == the pinned cutover numbers: kills-by-weapon (count) and damage-by-weapon (sum).</summary>
    [Test]
    public async Task WeaponStats_MatchesPinnedCutover()
    {
        ParsedDemo demo = DemoTestHelper.GetOrParse(DemoTestHelper.RequireDemo());

        // ── v2: the ported ruleset, through the 2.2c planner + composition seam ──
        BuildResult v2Build = CompileV2(demo);
        AnalysisRun v2Run = DemoAnalysis.Evaluate(demo, v2Build);

        WeaponStatsFixture actual = new(
            ReadBuckets(v2Run, demo, KillsTable),
            ReadBuckets(v2Run, demo, DamageTable));

        if (PilotFixture.Regenerate)
        {
            PilotFixture.Write(FindRepoRoot(), FixtureName, actual);
            return;
        }

        WeaponStatsFixture expected = PilotFixture.Read<WeaponStatsFixture>(FindRepoRoot(), FixtureName);

        double killTotal = await CompareTable(actual.Kills, expected.Kills, "kills_by_weapon (count bucket)");
        double dmgTotal = await CompareTable(actual.Damage, expected.Damage, "damage_by_weapon (sum bucket)");

        // The pinned demo has real weapon kills and enemy damage; an empty bucket set is a regression,
        // NOT a skip (the old skip guarded a live-oracle miss that can't recur).
        await Assert.That(killTotal).IsGreaterThan(0).Because("the pinned kills buckets must be non-vacuous");
        await Assert.That(dmgTotal).IsGreaterThan(0).Because("the pinned damage buckets must be non-vacuous");
    }

    /// <summary>
    ///     Compares one bucket map against its pin: same players, same weapon key set per player, same
    ///     value per (player, weapon). Returns the total of all pinned buckets so the caller can assert
    ///     non-vacuous coverage.
    /// </summary>
    private static async Task<double> CompareTable(
        Dictionary<string, Dictionary<string, double>> actual,
        Dictionary<string, Dictionary<string, double>> expected,
        string label)
    {
        await Assert.That(actual.Keys.ToHashSet()).IsEquivalentTo(expected.Keys.ToHashSet())
            .Because($"{label}: v2 must materialize the pinned players");

        double total = 0;
        foreach ((string slot, Dictionary<string, double> want) in expected)
        {
            Dictionary<string, double> got = actual[slot];
            await Assert.That(got.Keys.ToHashSet()).IsEquivalentTo(want.Keys.ToHashSet())
                .Because($"{label}: slot{slot} weapon key sets must match the pin");
            foreach ((string weapon, double amount) in want)
            {
                await Assert.That(got.GetValueOrDefault(weapon)).IsEqualTo(amount)
                    .Because($"{label}: slot{slot} {weapon}: v2 bucket must equal the pin ({amount})");
                total += amount;
            }
        }

        return total;
    }

    /// <summary>
    ///     Projects a run's keyed buckets and reads the named table into slot → (weapon key → value).
    ///     Slot keys are stringified for JSON fixture stability.
    /// </summary>
    private static Dictionary<string, Dictionary<string, double>> ReadBuckets(
        AnalysisRun run, ParsedDemo demo, string tableName)
    {
        EvaluationResult result = run.Snapshots
                                  ?? throw new InvalidOperationException("evaluation produced no snapshots");
        MetricTable? table = new KeyedStatsProjector().Project(result, demo)
            .FirstOrDefault(t => string.Equals(t.Name, tableName, StringComparison.Ordinal));

        Dictionary<string, Dictionary<string, double>> bySlot = new(StringComparer.Ordinal);
        if (table is null)
        {
            return bySlot;
        }

        string valueColumn = table.ValueColumns[0];
        foreach (MetricRow row in table.Rows)
        {
            string slot = AsInt(row.Dimensions.GetValueOrDefault("player_slot")).ToString(CultureInfo.InvariantCulture);
            string key = Convert.ToString(row.Dimensions.GetValueOrDefault("key"), CultureInfo.InvariantCulture) ?? "";
            double value = Convert.ToDouble(row.Values.GetValueOrDefault(valueColumn), CultureInfo.InvariantCulture);
            if (!bySlot.TryGetValue(slot, out Dictionary<string, double>? buckets))
            {
                buckets = new Dictionary<string, double>(StringComparer.Ordinal);
                bySlot[slot] = buckets;
            }

            buckets[key] = value;
        }

        return bySlot;
    }

    /// <summary>
    ///     Compiles the v2 weapon_stats ruleset through the full 2.2b resolve + 2.2c planner pipeline
    ///     against the demo's real tick rate and source profile, composing it onto an empty v1 config
    ///     (the composition seam). Returns the ready-to-evaluate build.
    /// </summary>
    private static BuildResult CompileV2(ParsedDemo demo)
    {
        string yaml = File.ReadAllText(
            Path.Combine(FindRepoRoot(), "src", "CS2DemoKit.Analysis", "Rules", "weapon_stats.rules.yaml"));
        RulesetDocumentLoader.Outcome outcome = RulesetDocumentLoader.Load(yaml, "weapon_stats.rules.yaml");
        RulesetDoc doc = outcome.Doc
                         ?? throw new InvalidOperationException(
                             "weapon_stats ruleset failed to load: " + string.Join("; ", outcome.Diagnostics));
        if (outcome.Diagnostics.Count > 0)
        {
            throw new InvalidOperationException(
                "weapon_stats ruleset has mapping/structural diagnostics: " + string.Join("; ", outcome.Diagnostics));
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
                                     "weapon_stats ruleset failed to resolve: " + string.Join("; ", resolved.Diagnostics));

        return builder.Build([ruleset]);
    }

    private static int AsInt(object? value) =>
        value is null ? 0 : Convert.ToInt32(value, CultureInfo.InvariantCulture);

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

/// <summary>Pinned weapon-stats pilot expectations (see <see cref="PilotFixture" />).</summary>
internal sealed record WeaponStatsFixture(
    Dictionary<string, Dictionary<string, double>> Kills,
    Dictionary<string, Dictionary<string, double>> Damage);
