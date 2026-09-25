#region

using CS2DemoKit.Analysis.Catalog;
using CS2DemoKit.Analysis.Graphs;
using CS2DemoKit.Analysis.Output;
using CS2DemoKit.Analysis.RulesetsV2.Model;
using CS2DemoKit.Analysis.RulesetsV2.Resolve;
using CS2DemoKit.Analysis.Yaml;
using CS2DemoKit.Parser;
using CS2DemoKit.TestSupport;

#endregion

namespace CS2DemoKit.Analysis.Tests.RoundFacts;

/// <summary>
///     Shared plumbing for the issue #54 tests: load ruleset YAML from strings, resolve it in the
///     draft context, and run it over the committed sample demo (or a named corpus demo).
/// </summary>
internal static class RoundFactsTestSupport
{
    private static readonly CatalogScopeAdapter _adapter = CatalogScopeAdapter.From(CatalogResource.Load());

    /// <summary>The committed four-round sample, parsed once per process through the shared cache.</summary>
    public static ParsedDemo Sample() =>
        DemoTestHelper.GetOrParse(DemoTestHelper.RequireDemo(DemoTestHelper.SampleDemoFileName));

    /// <summary>Loads ruleset documents from YAML strings; fails on any load error.</summary>
    public static IReadOnlyList<RulesetDoc> Load(params string[] yamls)
    {
        RuleConfigLoadResult loaded = YamlConfigLoader.LoadDocuments(
            yamls.Select((y, i) => ($"doc{i}.rules.yaml", y)));
        if (loaded.Errors.Count > 0)
        {
            throw new InvalidOperationException("load failed: " + string.Join("; ", loaded.Errors));
        }

        return loaded.Rulesets;
    }

    /// <summary>Resolves one ruleset in the build context (64 ticks, GOTV profile).</summary>
    public static RulesetResolveResult Resolve(string yaml)
    {
        RulesetDoc doc = RulesetDocumentLoader.Load(yaml, "test.rules.yaml").Doc
                         ?? throw new InvalidOperationException("test ruleset failed to map");
        return CheckedRulesetDraft.Load(doc, _adapter).Build(64.0, "Cs2GotvProfile");
    }

    /// <summary>Resolves one ruleset and fails loudly on any diagnostic.</summary>
    public static CheckedRuleset Checked(string yaml)
    {
        RulesetResolveResult result = Resolve(yaml);
        return result.Ruleset
               ?? throw new InvalidOperationException(
                   "resolve failed: " + string.Join("; ", result.Diagnostics));
    }

    /// <summary>
    ///     Builds the rulesets for <paramref name="demo" /> and evaluates them, failing if any ruleset
    ///     was dropped at composition.
    /// </summary>
    public static AnalysisRun Run(ParsedDemo demo, AnalysisOptions? options, params string[] yamls)
    {
        IReadOnlyList<RulesetDoc> docs = Load(yamls);
        BuildResult build = DemoAnalysis.Build(demo, docs, options);
        if (build.RulesetDiagnostics.Count > 0)
        {
            throw new InvalidOperationException(
                "build diagnostics: " + string.Join("; ", build.RulesetDiagnostics.Select(d => d.ToString())));
        }

        return DemoAnalysis.Evaluate(demo, build, options);
    }

    /// <summary><see cref="Run(ParsedDemo, AnalysisOptions?, string[])" /> with default options.</summary>
    public static AnalysisRun Run(ParsedDemo demo, params string[] yamls) => Run(demo, null, yamls);

    /// <summary>The named configured table of a run.</summary>
    public static MetricTable Table(AnalysisRun run, string name, ParsedDemo? demo = null) =>
        (demo is null ? run.ProjectConfiguredOutputs() : run.ProjectConfiguredOutputs(demo))
        .Single(t => string.Equals(t.Name, name, StringComparison.Ordinal));

    /// <summary>A cell as an int (null when absent or null).</summary>
    public static int? Int(MetricRow row, string column) =>
        row.Values.GetValueOrDefault(column) switch
        {
            null => null,
            int i => i,
            long l => (int)l,
            double d => (int)d,
            string s when int.TryParse(s, out int p) => p,
            object o => Convert.ToInt32(o, System.Globalization.CultureInfo.InvariantCulture)
        };

    /// <summary>A dimension as an int.</summary>
    public static int Dim(MetricRow row, string column) =>
        Convert.ToInt32(row.Dimensions[column], System.Globalization.CultureInfo.InvariantCulture);
}
