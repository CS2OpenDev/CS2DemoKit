#region

using System.Reflection;
using CS2DemoKit.Analysis.RulesetsV2.Model;
using CS2DemoKit.Analysis.Yaml;

#endregion

namespace CS2DemoKit.Analysis.Tests;

/// <summary>
///     Drift gate for the shipped-rules embedding, mirroring
///     <see cref="CatalogDriftTests" />'s pattern: the embedded copy of the 4
///     <c>rules/*.rules.yaml</c> files + <c>rules/cs2demokit-rules.schema.json</c> can never silently
///     diverge from the repo files they were <c>EmbeddedResource</c> <c>Link</c>-sourced from,
///     and <see cref="YamlConfigLoader.LoadShippedEmbedded" /> / <see cref="YamlConfigLoader.ExtractShippedTo" />
///     must keep behaving identically to directory loading. Pure in-memory / temp-dir — no demo file.
/// </summary>
[Category("Unit")]
public class ShippedRulesEmbedTests
{
    private const string ShippedResourcePrefix = "CS2DemoKit.Analysis.ShippedRules.";

    /// <summary>
    ///     The embedded count is asserted directly (4 baseline rulesets + 1 schema = 5) so that adding a
    ///     rules file to <c>rules/</c> without the csproj's <c>rules/*.rules.yaml</c> glob picking
    ///     it up — or any other glob/Link breakage — fails the suite outright, even though (a)/(b)
    ///     below would also catch most such breaks indirectly.
    /// </summary>
    [Test]
    public async Task EmbeddedShippedRulesResources_CountMatchesRepoRulesDirectory()
    {
        string[] repoRulesetFiles = Directory.GetFiles(Path.Combine(FindRepoRoot(), "src", "CS2DemoKit.Analysis", "Rules"), "*.rules.yaml");
        List<string> embeddedNames = GetShippedResourceNames();
        List<string> embeddedRulesetNames = embeddedNames
            .Where(n => n.EndsWith(".rules.yaml", StringComparison.Ordinal))
            .ToList();

        await Assert.That(repoRulesetFiles.Length).IsEqualTo(4)
            .Because("this pins the expected count so a change to the baseline rule set is a visible test edit");
        await Assert.That(embeddedRulesetNames.Count).IsEqualTo(repoRulesetFiles.Length)
            .Because("every rules/*.rules.yaml file must be embedded — a glob/Link break silently drops one");
        await Assert.That(embeddedNames.Count).IsEqualTo(5)
            .Because("4 baseline rulesets + cs2demokit-rules.schema.json");
    }

    /// <summary>
    ///     Byte-for-byte: every embedded shipped-rules resource must be identical to the repo file
    ///     it was <c>Link</c>-sourced from. This pins the csproj wiring itself (glob, Link,
    ///     LogicalName) independent of the loader — if this ever goes red while the loader-level
    ///     test below stays green, the break is in the csproj, not the code.
    /// </summary>
    [Test]
    public async Task EmbeddedShippedRulesResources_AreByteIdenticalToRepoRulesFiles()
    {
        string repoRulesDir = Path.Combine(FindRepoRoot(), "src", "CS2DemoKit.Analysis", "Rules");
        Assembly assembly = typeof(YamlConfigLoader).Assembly;

        foreach (string resourceName in GetShippedResourceNames())
        {
            string fileName = resourceName[ShippedResourcePrefix.Length..];
            byte[] embeddedBytes = ReadResourceBytes(assembly, resourceName);
            byte[] repoBytes = await File.ReadAllBytesAsync(Path.Combine(repoRulesDir, fileName));

            await Assert.That(embeddedBytes.SequenceEqual(repoBytes)).IsTrue()
                .Because($"embedded resource '{resourceName}' must be byte-identical to rules/{fileName}");
        }
    }

    /// <summary>
    ///     <see cref="YamlConfigLoader.LoadShippedEmbedded" /> must yield the same ruleset
    ///     documents, in the same order, as loading the repo <c>rules/</c> directory through
    ///     <see cref="YamlConfigLoader.TryLoadDirectory" /> — the two code paths share the exact
    ///     same per-file pipeline, so a divergence here can only mean the embedded resource set
    ///     itself drifted from the directory (glob/Link breakage) or the resource-reading half of
    ///     the loader is wrong.
    /// </summary>
    [Test]
    public async Task LoadShippedEmbedded_YieldsSameRulesetDocuments_AsDirectoryLoad()
    {
        string repoRulesDir = Path.Combine(FindRepoRoot(), "src", "CS2DemoKit.Analysis", "Rules");

        RuleConfigLoadResult embedded = YamlConfigLoader.LoadShippedEmbedded();
        RuleConfigLoadResult directory = YamlConfigLoader.TryLoadDirectory(repoRulesDir);

        await Assert.That(embedded.Success).IsTrue()
            .Because(embedded.Success ? "" : string.Join("; ", embedded.Errors));
        await Assert.That(directory.Success).IsTrue()
            .Because(directory.Success ? "" : string.Join("; ", directory.Errors));

        await Assert.That(embedded.Rulesets.Count).IsEqualTo(directory.Rulesets.Count);
        await Assert.That(embedded.Rulesets.Count).IsEqualTo(4);

        await Assert.That(embedded.Rulesets.Select(r => r.Id)
                .SequenceEqual(directory.Rulesets.Select(r => r.Id)))
            .IsTrue()
            .Because("same file-name ordering on both sides (embedded resource names sort the same "
                     + "way as the file paths they were Link-sourced from)");

        for (int i = 0; i < embedded.Rulesets.Count; i++)
        {
            RulesetDoc a = embedded.Rulesets[i];
            RulesetDoc b = directory.Rulesets[i];
            await Assert.That(a.Id).IsEqualTo(b.Id);
            await Assert.That(a.Title).IsEqualTo(b.Title);
            await Assert.That(a.Summary).IsEqualTo(b.Summary);
            await Assert.That(a.For).IsEqualTo(b.For);
            await Assert.That(a.Enabled).IsEqualTo(b.Enabled);
            await Assert.That(a.CatalogVersion).IsEqualTo(b.CatalogVersion);
            await Assert.That(a.MinAppVersion).IsEqualTo(b.MinAppVersion);
            await Assert.That(a.Use.Count).IsEqualTo(b.Use.Count);
            await Assert.That(a.Exports?.Count).IsEqualTo(b.Exports?.Count);
            await Assert.That(a.Params.Count).IsEqualTo(b.Params.Count);
            await Assert.That(a.Defines.Count).IsEqualTo(b.Defines.Count);
            await Assert.That(a.Stats.Count).IsEqualTo(b.Stats.Count);
            await Assert.That(a.Highlights.Count).IsEqualTo(b.Highlights.Count);
            await Assert.That(a.Show is null).IsEqualTo(b.Show is null);
        }
    }

    /// <summary>
    ///     <see cref="YamlConfigLoader.ExtractShippedTo" /> round-trip: writes all 5 embedded
    ///     files, and every written file is byte-identical to the repo file it mirrors.
    /// </summary>
    [Test]
    public async Task ExtractShippedTo_WritesAllFiles_ByteIdenticalToRepoRulesFiles()
    {
        string repoRulesDir = Path.Combine(FindRepoRoot(), "src", "CS2DemoKit.Analysis", "Rules");
        string targetDir = Directory.CreateTempSubdirectory("cs2demokit-extract-shipped-test-").FullName;
        try
        {
            IReadOnlyList<string> written = YamlConfigLoader.ExtractShippedTo(targetDir);

            await Assert.That(written.Count).IsEqualTo(5);
            await Assert.That(Directory.GetFiles(targetDir).Length).IsEqualTo(5);

            foreach (string path in written)
            {
                await Assert.That(File.Exists(path)).IsTrue();

                string fileName = Path.GetFileName(path);
                byte[] extractedBytes = await File.ReadAllBytesAsync(path);
                byte[] repoBytes = await File.ReadAllBytesAsync(Path.Combine(repoRulesDir, fileName));

                await Assert.That(extractedBytes.SequenceEqual(repoBytes)).IsTrue()
                    .Because($"extracted '{fileName}' must be byte-identical to rules/{fileName}");
            }
        }
        finally
        {
            Directory.Delete(targetDir, true);
        }
    }

    private static List<string> GetShippedResourceNames() =>
        typeof(YamlConfigLoader).Assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(ShippedResourcePrefix, StringComparison.Ordinal))
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static byte[] ReadResourceBytes(Assembly assembly, string resourceName)
    {
        using Stream stream = assembly.GetManifestResourceStream(resourceName)
                              ?? throw new InvalidOperationException($"missing resource '{resourceName}'");
        using MemoryStream buffer = new();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static string FindRepoRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, ".git"))
                || File.Exists(Path.Combine(dir.FullName, "CS2DemoKit.slnx")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("repo root not found from " + AppContext.BaseDirectory);
    }
}
