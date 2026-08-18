#region

using CS2DemoKit.Analysis.RulesetsV2.Model;
using CS2DemoKit.Analysis.Yaml;

#endregion

namespace CS2DemoKit.Analysis.Tests;

/// <summary>
///     CONS-4 (non-file rule sources lose directory-loader semantics):
///     <see cref="YamlConfigLoader.LoadDocuments" /> must be the directory loader with the disk
///     removed — same classification, same duplicate-id behaviour, same error ordering — so a
///     service storing user-authored rules in a database gets exactly what a consumer with a
///     <c>rules/</c> folder gets. The equivalence is asserted against a real temp directory rather
///     than restated, because the failure mode this guards is silent divergence.
/// </summary>
[Category("Unit")]
public class LoadDocumentsTests
{
    private const string Healthy = """
                                   ruleset: healthy
                                   for: each_player
                                   stats:
                                     kills:
                                       count: kill
                                       per: round
                                   """;

    private const string AlsoHealthy = """
                                       ruleset: also_healthy
                                       for: each_player
                                       stats:
                                         deaths:
                                           count: death
                                           per: round
                                       """;

    /// <summary>A second document claiming the id <c>healthy</c> already took.</summary>
    private const string DuplicateId = """
                                       ruleset: healthy
                                       for: each_player
                                       stats:
                                         other:
                                           count: death
                                           per: round
                                       """;

    private const string YamlSyntaxError = "ruleset: healthy\nfor: [unclosed\n";

    private const string RetiredV1 = """
                                     chains:
                                       - id: old
                                         rules: []
                                     """;

    private const string NotARuleset = """
                                       something_else: 1
                                       """;

    /// <summary>The comparable projection of an error: everything except the absolute directory prefix.</summary>
    private static (string? File, string Message, string? ChainId, string? RuleId, int? Line, int? Column)
        Shape(RuleConfigError error) =>
        (error.FilePath is null ? null : Path.GetFileName(error.FilePath),
            error.Message, error.ChainId, error.RuleId, error.Line, error.Column);

    /// <summary>
    ///     Loads the same documents both ways — written to a temp directory, and handed over in
    ///     memory — and returns the two results. Names are supplied in the order
    ///     <see cref="YamlConfigLoader.TryLoadDirectory" /> would enumerate them (ordinal
    ///     case-insensitive by file name) so the orderings are comparable.
    /// </summary>
    private static (RuleConfigLoadResult FromDirectory, RuleConfigLoadResult FromMemory) LoadBothWays(
        params (string Name, string Yaml)[] documents)
    {
        (string Name, string Yaml)[] ordered =
            [.. documents.OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)];

        string dir = Path.Combine(Path.GetTempPath(), "load_documents_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            foreach ((string name, string yaml) in ordered)
            {
                File.WriteAllText(Path.Combine(dir, name), yaml);
            }

            return (YamlConfigLoader.TryLoadDirectory(dir), YamlConfigLoader.LoadDocuments(ordered));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Test]
    public async Task LoadDocuments_MatchesDirectoryLoading_ForAMixOfGoodAndBrokenDocuments()
    {
        // Every classification path at once: healthy, duplicate id, YAML syntax error, retired v1,
        // and a well-formed YAML file that is not a ruleset.
        (RuleConfigLoadResult fromDirectory, RuleConfigLoadResult fromMemory) = LoadBothWays(
            ("a_healthy.rules.yaml", Healthy),
            ("b_duplicate.rules.yaml", DuplicateId),
            ("c_syntax.rules.yaml", YamlSyntaxError),
            ("d_v1.rules.yaml", RetiredV1),
            ("e_not_a_ruleset.rules.yaml", NotARuleset),
            ("f_healthy.rules.yaml", AlsoHealthy));

        await Assert.That(fromMemory.Rulesets.Select(r => r.Id).ToList())
            .IsEquivalentTo(fromDirectory.Rulesets.Select(r => r.Id).ToList())
            .Because("the same documents must yield the same rulesets, in the same order");
        await Assert.That(fromMemory.Errors.Select(Shape).ToList())
            .IsEquivalentTo(fromDirectory.Errors.Select(Shape).ToList())
            .Because("classification, attribution, positions AND error ordering must all match");
        await Assert.That(fromMemory.LoadedFiles.Select(Path.GetFileName).ToList())
            .IsEquivalentTo(fromDirectory.LoadedFiles.Select(Path.GetFileName).ToList());
        await Assert.That(fromMemory.FailedFiles.Select(Path.GetFileName).ToList())
            .IsEquivalentTo(fromDirectory.FailedFiles.Select(Path.GetFileName).ToList());
    }

    [Test]
    public async Task LoadDocuments_DedupesByRulesetId_FirstWins()
    {
        RuleConfigLoadResult loaded = YamlConfigLoader.LoadDocuments(
        [
            ("db://user/1", Healthy),
            ("db://user/2", DuplicateId)
        ]);

        await Assert.That(loaded.Rulesets.Count).IsEqualTo(1)
            .Because("ruleset ids are one namespace per tier — the second 'healthy' is rejected, not merged");
        await Assert.That(loaded.Rulesets[0].Stats.Any(s => s.Id == "kills")).IsTrue()
            .Because("first wins, exactly as in a directory");
        await Assert.That(loaded.Errors.Count).IsEqualTo(1);
        await Assert.That(loaded.Errors[0].FilePath).IsEqualTo("db://user/2")
            .Because("the error is attributed to the caller's own label, not a synthesized path");
        await Assert.That(loaded.Errors[0].Message).Contains("duplicate ruleset id");
    }

    [Test]
    public async Task LoadDocuments_EnumeratesLazilyAndExactlyOnce()
    {
        int pulled = 0;

        IEnumerable<(string Label, string Yaml)> Streamed()
        {
            foreach ((string label, string yaml) in new[]
                     {
                         ("one", Healthy), ("two", AlsoHealthy)
                     })
            {
                pulled++;
                yield return (label, yaml);
            }
        }

        IEnumerable<(string Label, string Yaml)> source = Streamed();
        await Assert.That(pulled).IsEqualTo(0).Because("constructing the sequence must not read anything");

        RuleConfigLoadResult loaded = YamlConfigLoader.LoadDocuments(source);

        await Assert.That(pulled).IsEqualTo(2)
            .Because("a streaming database reader must be pulled exactly once per row, never buffered twice");
        await Assert.That(loaded.Rulesets.Count).IsEqualTo(2);
    }

    [Test]
    public async Task LoadDocuments_NullText_IsAnAttributedError_NotASilentSkip()
    {
        RuleConfigLoadResult loaded = YamlConfigLoader.LoadDocuments(
        [
            ("db://user/1", null!),
            ("db://user/2", Healthy)
        ]);

        await Assert.That(loaded.Success).IsFalse()
            .Because("a missing document must never leave the load reporting success");
        await Assert.That(loaded.Errors.Single().FilePath).IsEqualTo("db://user/1");
        await Assert.That(loaded.Rulesets.Count).IsEqualTo(1)
            .Because("the remaining documents still load — per-document failure containment");
    }

    [Test]
    public async Task LoadShippedWithOverlay_ReplacesById_AppendsNewIds_AndDropsDisabled()
    {
        RuleConfigLoadResult shipped = YamlConfigLoader.LoadShippedEmbedded();
        string replacedId = shipped.Rulesets[0].Id;

        RuleConfigLoadResult overlaid = YamlConfigLoader.LoadShippedWithOverlay(
        [
            ("db://user/replacement", $"""
                                       ruleset: {replacedId}
                                       for: each_player
                                       stats:
                                         only_stat:
                                           count: kill
                                           per: round
                                       """),
            ("db://user/new", AlsoHealthy),
            ("db://user/disabled", """
                                   ruleset: switched_off
                                   enabled: false
                                   for: each_player
                                   stats:
                                     kills:
                                       count: kill
                                       per: round
                                   """)
        ]);

        await Assert.That(overlaid.Success).IsTrue()
            .Because("no user document is broken: " + string.Join("; ", overlaid.Errors));

        RulesetDoc replaced = overlaid.Rulesets.Single(r => r.Id == replacedId);
        await Assert.That(replaced.Stats.Select(s => s.Id).ToList())
            .IsEquivalentTo(new List<string>
            {
                "only_stat"
            })
            .Because("a same-id user ruleset replaces the shipped one wholesale, never merges into it");
        await Assert.That(overlaid.Rulesets.Any(r => r.Id == "also_healthy")).IsTrue()
            .Because("a new user id is appended");
        await Assert.That(overlaid.Rulesets.Any(r => r.Id == "switched_off")).IsFalse()
            .Because("enabled: false is dropped after overlay (unlike bare LoadShippedEmbedded)");
        await Assert.That(overlaid.Rulesets.Count).IsEqualTo(shipped.Rulesets.Count + 1)
            .Because("14 shipped, one replaced in place, one appended, one disabled and dropped");
    }

    [Test]
    public async Task LoadShippedWithOverlay_NoUserDocuments_IsTheShippedTierAlone()
    {
        RuleConfigLoadResult shipped = YamlConfigLoader.LoadShippedEmbedded();
        RuleConfigLoadResult overlaid = YamlConfigLoader.LoadShippedWithOverlay([]);

        await Assert.That(overlaid.Success).IsTrue();
        await Assert.That(overlaid.Rulesets.Select(r => r.Id).ToList())
            .IsEquivalentTo(shipped.Rulesets.Where(r => r.Enabled).Select(r => r.Id).ToList())
            .Because("an empty overlay is not an error — the enabled shipped tier comes back unchanged");
    }
}
