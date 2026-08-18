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
///     The post-plant-double pilot golden, pinned at the Rulesets v2 production cutover: the v2
///     <c>rules/post_plant_double.rules.yaml</c>, compiled by the planner and evaluated on the
///     reference demo, must reproduce the numbers captured from the v2==v1-verified run at cutover —
///     both the highlight firings (the achievement satisfactions) and the per-(player, round) clip
///     context (plant tick, post-plant kill count, ordered kill ticks).
///     <para>
///         Numbers pinned from a v2==v1-verified run at the v2 cutover;
///         the v1 files were later removed. The live v1 oracle
///         (<c>rules/achievement-post-plant-double.yaml</c>) is gone; the golden now asserts the
///         captured pins in <c>tests/fixtures/rules-v2/post_plant_double.expected.json</c>.
///         Regenerate with <c>PIN_RULES_V2=1</c>. Parses the demo, so
///         <see cref="NotInParallelAttribute" /> and the shared parse cache apply.
///     </para>
/// </summary>
[Category("Integration")]
[NotInParallel]
public class PostPlantDoublePilotTests
{
    private const string ChainName = "_chain_post_plant_double";
    private const string TableName = "post_plant_double_context";
    private const string FixtureName = "post_plant_double";

    /// <summary>
    ///     v2 == the pinned cutover numbers: identical highlight firings AND identical per-round clip
    ///     context.
    /// </summary>
    [Test]
    public async Task Pilot_MatchesPinnedCutover()
    {
        ParsedDemo demo = DemoTestHelper.GetOrParse(DemoTestHelper.RequireDemo());

        // ── v2: the pilot ruleset, through the 2.2c planner + composition seam ──
        BuildResult v2Build = CompileV2(demo);
        AnalysisRun v2Run = DemoAnalysis.Evaluate(demo, v2Build);

        PpdFixture actual = Extract(v2Run, demo);

        if (PilotFixture.Regenerate)
        {
            PilotFixture.Write(FindRepoRoot(), FixtureName, actual);
            return;
        }

        PpdFixture expected = PilotFixture.Read<PpdFixture>(FindRepoRoot(), FixtureName);

        // ── 1. Highlight firings: same players, same rounds, same ticks ──
        // Pinned firings are non-empty (the reference demo has post-plant doubles); an empty v2 firing
        // set is a regression, NOT a skip (the old skip guarded a live-oracle miss that can't recur).
        await Assert.That(expected.Firings.Count).IsGreaterThan(0)
            .Because("the pinned fixture must record the post-plant-double firings");
        await Assert.That(actual.Firings).IsEquivalentTo(expected.Firings)
            .Because("every v2 highlight firing must match a pinned firing on (player_slot, tick)");

        // ── 2. Per-(player, round) clip context ──
        await Assert.That(actual.Rows.Keys.ToHashSet()).IsEquivalentTo(expected.Rows.Keys.ToHashSet())
            .Because("v2 must emit the pinned (player, round) rows");

        int comparedNonEmpty = 0;
        foreach ((string key, PpdRow want) in expected.Rows)
        {
            PpdRow got = actual.Rows[key];
            await Assert.That(got.Achieved).IsEqualTo(want.Achieved).Because($"{key}: Achieved must match the pin");
            await Assert.That(got.PostPlantKills).IsEqualTo(want.PostPlantKills)
                .Because($"{key}: PostPlantKills must match the pin");
            await Assert.That(got.PlantTick).IsEqualTo(want.PlantTick).Because($"{key}: PlantTick must match the pin");
            await Assert.That(got.KillTicks).IsEquivalentTo(want.KillTicks)
                .Because($"{key}: ordered kill ticks must match the pin "
                         + $"(pinned=[{string.Join(",", want.KillTicks)}] v2=[{string.Join(",", got.KillTicks)}])");

            if (want.PostPlantKills > 0)
            {
                comparedNonEmpty++;
            }
        }

        await Assert.That(comparedNonEmpty).IsGreaterThan(0)
            .Because("the pinned demo must exercise post-plant kills for the comparison to be meaningful");
    }

    /// <summary>Captures the pinnable v2 quantities into the fixture DTO (used by both regen and compare).</summary>
    private static PpdFixture Extract(AnalysisRun v2Run, ParsedDemo demo)
    {
        List<PpdFiring> firings = v2Run.Timeline.Events
            .Where(e => e.ChainName == ChainName && e.PlayerSlot is not null)
            .Select(e => new PpdFiring(e.PlayerSlot!.Value, e.Tick))
            .OrderBy(f => f.Tick).ThenBy(f => f.Slot)
            .ToList();

        MetricTable table = v2Run.ProjectConfiguredOutputs(demo).Single(t => t.Name == TableName);
        Dictionary<string, PpdRow> rows = new(StringComparer.Ordinal);
        foreach (MetricRow row in table.Rows)
        {
            int slot = AsInt(row.Dimensions.GetValueOrDefault("player_slot"));
            int round = AsInt(row.Dimensions.GetValueOrDefault("round_number"));
            rows[$"{slot}|{round}"] = new PpdRow(
                row.Values.GetValueOrDefault("Achieved") is true,
                AsInt(row.Values.GetValueOrDefault("PostPlantKills")),
                AsInt(row.Values.GetValueOrDefault("PlantTick")),
                ParseKillTickList(row.Values.GetValueOrDefault("KillTick")));
        }

        return new PpdFixture(firings, rows);
    }

    /// <summary>
    ///     Compiles the v2 post-plant-double ruleset through the full 2.2b resolve + 2.2c planner
    ///     pipeline against the demo's real tick rate and source profile, composing it onto an empty
    ///     v1 config (the composition seam). Returns the ready-to-evaluate build.
    /// </summary>
    private static BuildResult CompileV2(ParsedDemo demo)
    {
        string yaml = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "CS2DemoKit.Analysis", "Rules", "post_plant_double.rules.yaml"));
        RulesetDocumentLoader.Outcome outcome = RulesetDocumentLoader.Load(yaml, "post_plant_double.rules.yaml");
        RulesetDoc doc = outcome.Doc
                         ?? throw new InvalidOperationException(
                             "pilot ruleset failed to load: " + string.Join("; ", outcome.Diagnostics));

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
                                     "pilot ruleset failed to resolve: " + string.Join("; ", resolved.Diagnostics));

        return builder.Build([ruleset]);
    }

    private static int AsInt(object? value) =>
        value is null ? 0 : Convert.ToInt32(value, CultureInfo.InvariantCulture);

    /// <summary>Parses the v2 kill_ticks column (null / single int / comma-joined string) into a list.</summary>
    private static List<int> ParseKillTickList(object? value) =>
        value switch
        {
            null => [],
            int i => [i],
            string s => s.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(p => int.Parse(p, CultureInfo.InvariantCulture)).ToList(),
            _ => [Convert.ToInt32(value, CultureInfo.InvariantCulture)]
        };

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

/// <summary>Pinned post-plant-double pilot expectations (see <see cref="PilotFixture" />).</summary>
internal sealed record PpdFixture(
    List<PpdFiring> Firings,
    Dictionary<string, PpdRow> Rows);

internal sealed record PpdFiring(int Slot, int Tick);

internal sealed record PpdRow(bool Achieved, int PostPlantKills, int PlantTick, List<int> KillTicks);
