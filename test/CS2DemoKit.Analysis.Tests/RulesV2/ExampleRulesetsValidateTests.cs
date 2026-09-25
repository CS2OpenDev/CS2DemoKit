#region

using CS2DemoKit.Analysis.Output;
using CS2DemoKit.Analysis.RulesetsV2.Model;
using CS2DemoKit.Analysis.Tests.RoundFacts;
using CS2DemoKit.Analysis.Yaml;
using CS2DemoKit.Parser;

#endregion

namespace CS2DemoKit.Analysis.Tests.RulesV2;

/// <summary>
///     Every ruleset under <c>Rules/examples/</c> is a working sample: it validates clean alongside
///     the shipped tier and builds on the committed sample. The authoring guide promises exactly that
///     ("they all validate clean"), and nothing held it until now.
/// </summary>
[Category("Integration")]
[NotInParallel]
public class ExampleRulesetsValidateTests
{
    private static string ExamplesDirectory()
    {
        string? dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8 && dir is not null; i++)
        {
            if (File.Exists(Path.Combine(dir, "CS2DemoKit.slnx")))
            {
                return Path.Combine(dir, "src", "CS2DemoKit.Analysis", "Rules", "examples");
            }

            dir = Path.GetDirectoryName(dir);
        }

        throw new InvalidOperationException("repo root not found");
    }

    public static IEnumerable<string> ExampleFiles() =>
        Directory.EnumerateFiles(ExamplesDirectory(), "*.rules.yaml", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal)
            .Select(p => Path.GetRelativePath(ExamplesDirectory(), p));

    [Test]
    [MethodDataSource(nameof(ExampleFiles))]
    public async Task Example_ValidatesClean_AndBuildsOnTheSample(string relativePath)
    {
        string path = Path.Combine(ExamplesDirectory(), relativePath);
        RuleConfigLoadResult example = YamlConfigLoader.LoadDocuments([(relativePath, await File.ReadAllTextAsync(path))]);
        await Assert.That(example.Errors.Count).IsEqualTo(0).Because(string.Join("; ", example.Errors));

        List<RulesetDoc> docs = [.. YamlConfigLoader.LoadShippedEmbedded().Rulesets, .. example.Rulesets];
        RulesetValidationResult validation = DemoAnalysis.ValidateRulesets(docs);
        await Assert.That(validation.Success).IsTrue()
            .Because(string.Join("; ", validation.Diagnostics.Select(d => $"{d.RulesetId}: {d.Code} {d.Message}")));

        ParsedDemo demo = RoundFactsTestSupport.Sample();
        Graphs.BuildResult build = DemoAnalysis.Build(demo, docs);
        await Assert.That(build.RulesetDiagnostics.Count).IsEqualTo(0);
        await Assert.That(build.ExcludedRulesets.Count).IsEqualTo(0);
    }

    /// <summary>
    ///     The round-facts example end to end on the sample: two rows per round, the round-1 plant at
    ///     A on frame 13085, the server's winners, and every tick column on the frame clock.
    /// </summary>
    [Test]
    public async Task RoundFacts_Example_ProjectsOneRowPerRoundPerSide()
    {
        string path = Path.Combine(ExamplesDirectory(), "round_facts.rules.yaml");
        ParsedDemo demo = RoundFactsTestSupport.Sample();
        AnalysisRun run = RoundFactsTestSupport.Run(demo, await File.ReadAllTextAsync(path));
        MetricTable table = RoundFactsTestSupport.Table(run, "round_facts", demo);

        await Assert.That(table.Rows.Count).IsEqualTo(4);
        List<MetricRow> first = table.Rows.Where(r => RoundFactsTestSupport.Dim(r, "round_number") == 1).ToList();
        await Assert.That(first.Count).IsEqualTo(2);
        foreach (MetricRow row in first)
        {
            await Assert.That(row.Values["PlantSite"]).IsEqualTo("A");
            await Assert.That(RoundFactsTestSupport.Int(row, "PlantTick")).IsEqualTo(13085);
            await Assert.That(RoundFactsTestSupport.Int(row, "WinnerSide")).IsEqualTo(2);
            await Assert.That(RoundFactsTestSupport.Int(row, "DecidedTick")).IsEqualTo(13564);
        }

        await Assert.That(first.Single(r => RoundFactsTestSupport.Dim(r, "side") == 2).Values["Won"]).IsEqualTo(1);
        foreach (string tick in (string[])["DecidedTick", "EndedTick", "PlantTick", "DefuseTick", "ExplodeTick", "KillTicks"])
        {
            await Assert.That(table.ColumnClocks.GetValueOrDefault(tick)).IsEqualTo(MetricTable.FrameClock).Because(tick);
        }
    }
}
