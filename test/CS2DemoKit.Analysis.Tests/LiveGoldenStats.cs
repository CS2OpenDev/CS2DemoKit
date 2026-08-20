#region

using System.Globalization;
using CS2DemoKit.Analysis.Building;
using CS2DemoKit.Analysis.Catalog;
using CS2DemoKit.Analysis.GoldenStats;
using CS2DemoKit.Analysis.Graphs;
using CS2DemoKit.Analysis.Output;
using CS2DemoKit.Analysis.Plugins;
using CS2DemoKit.Analysis.Registry;
using CS2DemoKit.Analysis.RulesetsV2.Model;
using CS2DemoKit.Analysis.RulesetsV2.Resolve;
using CS2DemoKit.Analysis.Yaml;
using CS2DemoKit.Parser;

#endregion

namespace CS2DemoKit.Analysis.Tests;

/// <summary>
///     Derives an <c>ours</c> stat document by running the engine end to end over a demo.
///     <para>
///         There is no committed <c>ours</c>. It is what the code currently produces, so it is
///         always computed here and never read from disk: parse, compile and evaluate the shipped v2
///         rulesets, project the player-stats table, then rekey through
///         <see cref="OursGoldenStatsConverter" /> into the canonical shape the committed
///         <c>expected</c> files use.
///     </para>
/// </summary>
internal static class LiveGoldenStats
{
    /// <summary>Runs the engine over <paramref name="demo" /> and returns its stat document.</summary>
    /// <param name="demoFileName">The demo's bare filename, stamped into the document.</param>
    /// <param name="demo">The parsed demo.</param>
    /// <param name="demoSha256">
    ///     Hash of the bytes this was derived from, so a pinned reference records which demo it
    ///     describes and a later run can tell it is looking at the same one.
    /// </param>
    /// <param name="generatedAt">Fixed timestamp when pinning, so a re-pin diffs only on values.</param>
    /// <returns>The derived document.</returns>
    public static GoldenStatsDocument Derive(
        string demoFileName, ParsedDemo demo, string? demoSha256 = null, string? generatedAt = null)
    {
        ArgumentNullException.ThrowIfNull(demo);

        AnalysisRun run = DemoAnalysis.Evaluate(demo, CompileV2(demo));
        EvaluationResult result = run.Snapshots
                                  ?? throw new InvalidOperationException("evaluation produced no snapshots");

        MetricTable table = new PlayerGameStatsProjector().Project(result, demo).Single();

        List<PlayerStatsInput> players = [];
        foreach (MetricRow row in RichestBySlot(table).Values)
        {
            string name = row.Dimensions.GetValueOrDefault("player_name") as string ?? string.Empty;
            if (name.Length == 0)
            {
                continue; // the converter drops nameless rows anyway
            }

            players.Add(new PlayerStatsInput(
                name,
                AsInt(row.Dimensions.GetValueOrDefault("team")),
                AsInt(row.Dimensions.GetValueOrDefault("player_slot")),
                row.Values));
        }

        // Ordered so a pinned document is byte-stable regardless of row order.
        players.Sort((a, b) => a.PlayerSlot.CompareTo(b.PlayerSlot));

        return OursGoldenStatsConverter.Convert(demoFileName, demoSha256, demo, players, null, generatedAt);
    }

    /// <summary>Per slot, keep the row with the most non-null cells (drops context-only phantom rows).</summary>
    private static Dictionary<int, MetricRow> RichestBySlot(MetricTable table)
    {
        Dictionary<int, MetricRow> byKey = new();
        foreach (MetricRow row in table.Rows)
        {
            int slot = AsInt(row.Dimensions.GetValueOrDefault("player_slot"));
            if (!byKey.TryGetValue(slot, out MetricRow? existing)
                || row.Values.Count(kv => kv.Value is not null) > existing.Values.Count(kv => kv.Value is not null))
            {
                byKey[slot] = row;
            }
        }

        return byKey;
    }

    // player_stats' HLTV reads kast.kast_pct, so both documents compile together for the export
    // graph to resolve it. Mirrors PlayerStatsPilotTests.CompileV2.
    private static BuildResult CompileV2(ParsedDemo demo)
    {
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

    private static int AsInt(object? v) => v is null ? 0 : Convert.ToInt32(v, CultureInfo.InvariantCulture);

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
