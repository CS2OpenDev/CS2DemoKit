#region

using CS2DemoKit.Analysis.Catalog;
using CS2DemoKit.Analysis.RulesetsV2.Model;
using CS2DemoKit.Analysis.RulesetsV2.Resolve;
using CS2DemoKit.Analysis.Yaml;

#endregion

namespace CS2DemoKit.Analysis.Tests.RulesV2;

/// <summary>
///     The demo-less checker must reject a <c>show:</c> reference the build would reject.
///     <para>
///         Before this, <c>ComposeDraft</c> ran cross-ruleset validation, resolution and cycle
///         detection and never looked at <c>show:</c>. An author got a clean lint and then two
///         different failures: a bad <c>scoreboard:</c> ref threw from <c>ShowLowering</c> at build,
///         while a bad <c>tables:</c> column resolved to no metric node and projected an all-null
///         column with nothing reported at all.
///     </para>
/// </summary>
[Category("Unit")]
public class ShowReferenceValidationTests
{
    // A highlight, a plain stat, a tally and its targets: one of each thing a show ref may name.
    private const string Probe = """
                                 ruleset: show_probe
                                 for: each_player
                                 stats:
                                   kills:
                                     count: kill
                                     per: round
                                   big:
                                     flag:
                                       when: "kills > 2"
                                     per: round
                                   multi_kill_tally:
                                     tally: kills
                                     thresholds:
                                       - { min: 3, target: rounds_3k }
                                       - { min: 2, target: rounds_2k }
                                     per: match
                                 highlights:
                                   big_round:
                                     when: big
                                     per: match
                                     kind: hidden
                                     title: "big round for {player.name}"
                                 show:
                                   scoreboard:
                                     - { stat: kills }
                                     - { stat: big_round }
                                     - { stat: big_round.count }
                                     - { stat: rounds_2k }
                                   tables:
                                     totals:
                                       per: player_match
                                       columns:
                                         - { stat: kills }
                                         - { stat: rounds_3k }
                                 """;

    /// <summary>
    ///     The safety net for making this a hard diagnostic: a bad reference now drops the whole
    ///     document, so the shipped rulesets must be clean or the library ships broken.
    /// </summary>
    [Test]
    public async Task ShippedRulesets_PassTheChecker()
    {
        string rules = Path.Combine(FindRepoRoot(), "src", "CS2DemoKit.Analysis", "Rules");
        List<RulesetDoc> docs = [];
        foreach (string path in Directory.EnumerateFiles(rules, "*.rules.yaml").OrderBy(p => p, StringComparer.Ordinal))
        {
            docs.Add(Load(File.ReadAllText(path), Path.GetFileName(path)));
        }

        await Assert.That(docs).IsNotEmpty();

        RulesetComposition.Result result = RulesetComposition.ComposeDraft(docs, Adapter());

        await Assert.That(ShowDiagnostics(result)).IsEmpty()
            .Because("every shipped show: reference must name something its ruleset declares");
        await Assert.That(result.Rulesets.Count).IsEqualTo(docs.Count)
            .Because("no shipped document may be dropped by the new check");
    }

    [Test]
    public async Task ValidReferences_AreAccepted()
    {
        RulesetComposition.Result result = RulesetComposition.ComposeDraft([Load(Probe)], Adapter());

        // Assert the probe actually composes before trusting "no show diagnostics": a document
        // dropped for an unrelated reason would also report none, and pass vacuously.
        await Assert.That(result.Rulesets.Count).IsEqualTo(1)
            .Because("probe must compose: " + string.Join("; ", result.Diagnostics));
        await Assert.That(ShowDiagnostics(result)).IsEmpty()
            .Because("a stat, a highlight, a highlight .count and a tally target all resolve");
    }

    [Test]
    public async Task ScoreboardReferenceToUnknownId_IsReported()
    {
        RulesetComposition.Result result = RulesetComposition.ComposeDraft(
            [Load(Probe.Replace("- { stat: rounds_2k }", "- { stat: nope }", StringComparison.Ordinal))],
            Adapter());

        RulesetDiagnostic diagnostic = ShowDiagnostics(result).Single();
        await Assert.That(diagnostic.Code).IsEqualTo(ResolveDiagnosticCodes.ShowUnknownRef);
        await Assert.That(diagnostic.Message).Contains("nope");
        await Assert.That(diagnostic.Position.Line).IsGreaterThan(0)
            .Because("the author needs the line, which is why this runs on the entry not the block");
    }

    // The path that used to fail silently: no throw, just an all-null column downstream.
    [Test]
    public async Task TableColumnReferenceToUnknownId_IsReported()
    {
        RulesetComposition.Result result = RulesetComposition.ComposeDraft(
            [Load(Probe.Replace("- { stat: rounds_3k }", "- { stat: nope }", StringComparison.Ordinal))],
            Adapter());

        RulesetDiagnostic diagnostic = ShowDiagnostics(result).Single();
        await Assert.That(diagnostic.Code).IsEqualTo(ResolveDiagnosticCodes.ShowUnknownRef);
        await Assert.That(diagnostic.Message).Contains("nope");
        await Assert.That(diagnostic.Message).Contains("totals").Because("name the table it sits in");
    }

    [Test]
    [Arguments("per: player_match", "per: per_player", "per_player")]
    [Arguments("per: player_match", "per: match_player", "match_player")]
    public async Task UnsupportedTableDimension_IsReported(string from, string to, string written)
    {
        RulesetComposition.Result result = RulesetComposition.ComposeDraft(
            [Load(Probe.Replace(from, to, StringComparison.Ordinal))], Adapter());

        RulesetDiagnostic diagnostic = ShowDiagnostics(result).Single();
        await Assert.That(diagnostic.Code).IsEqualTo(ResolveDiagnosticCodes.ShowBadTableDimension);
        await Assert.That(diagnostic.Message).Contains(written);
    }

    /// <summary>A rejected document is dropped, so a consumer never builds against a bad show block.</summary>
    [Test]
    public async Task ARejectedDocument_IsExcluded()
    {
        RulesetComposition.Result result = RulesetComposition.ComposeDraft(
            [Load(Probe.Replace("- { stat: kills }\n    - { stat: big_round }", "- { stat: nope }", StringComparison.Ordinal))],
            Adapter());

        await Assert.That(result.Rulesets).IsEmpty();
        await Assert.That(result.Excluded.Count).IsEqualTo(1);
        await Assert.That(result.AttributedDiagnostics.Count).IsEqualTo(result.Diagnostics.Count)
            .Because("the attributed mirror must stay 1:1 with the raw list");
    }

    private static CatalogScopeAdapter Adapter() => CatalogScopeAdapter.From(CatalogResource.Load());

    private static List<RulesetDiagnostic> ShowDiagnostics(RulesetComposition.Result result) =>
        result.Diagnostics.Where(d => d.Code.StartsWith("resolve.show.", StringComparison.Ordinal)).ToList();

    private static RulesetDoc Load(string yaml, string file = "test.rules.yaml")
    {
        RulesetDocumentLoader.Outcome outcome = RulesetDocumentLoader.Load(yaml, file);
        return outcome.Doc
               ?? throw new InvalidOperationException(
                   $"{file} failed to map: " + string.Join("; ", outcome.Diagnostics));
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
