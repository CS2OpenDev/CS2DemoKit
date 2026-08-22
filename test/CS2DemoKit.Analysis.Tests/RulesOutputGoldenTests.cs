#region

using CS2DemoKit.Parser;
using CS2DemoKit.TestSupport;
using TUnit.Core.Exceptions;

#endregion

namespace CS2DemoKit.Analysis.Tests;

/// <summary>
///     Pins the rules engine's output for every demo in the local corpus.
///     <para>
///         This exists because the pinned-cutover suites (<c>Kast_MatchesPinnedCutover</c> and its
///         siblings) resolve one named reference demo and skip when it is absent, which is most
///         checkouts. That left parser-side changes able to alter every computed stat with the whole
///         golden suite reporting green by skipping. This one takes whatever demos are present.
///     </para>
///     <para>
///         Demo-independent logic, demo-specific golden: the comparison is a digest of the full
///         rules rendering, and the fixture is keyed by demo file name. A demo with no fixture skips
///         rather than fails, so adding a demo never breaks the build.
///     </para>
///     <para>
///         Regenerate after an intended change with <c>CS2DEMOKIT_UPDATE_RULES_GOLDEN=1</c>. Read the
///         diff before committing it: the point of the fixture is that it should not move.
///     </para>
/// </summary>
[Category("Golden")]
[NotInParallel]
public class RulesOutputGoldenTests
{
    private const string UpdateVariable = "CS2DEMOKIT_UPDATE_RULES_GOLDEN";

    /// <summary>Every <c>.dem</c> under the repo's <c>demos/</c> tree, ordered for determinism.</summary>
    public static IEnumerable<string> CorpusDemos()
    {
        string? root = RepoRoot();
        if (root is null)
        {
            yield break;
        }

        string demos = Path.Combine(root, "demos");
        if (!Directory.Exists(demos))
        {
            yield break;
        }

        foreach (string path in Directory.EnumerateFiles(demos, "*.dem", SearchOption.AllDirectories)
                     .OrderBy(p => p, StringComparer.Ordinal)
                     .Take(25))
        {
            yield return path;
        }
    }

    [Test]
    [MethodDataSource(nameof(CorpusDemos))]
    public async Task RulesOutput_MatchesGolden(string demoPath)
    {
        string root = RepoRoot() ?? throw new SkipTestException("repo root not found");
        string goldenPath = Path.Combine(root, "tests", "fixtures", "rules-output",
            Path.GetFileNameWithoutExtension(demoPath) + ".digest.txt");

        ParsedDemo demo = DemoParser.Parse(File.ReadAllBytes(demoPath).AsMemory());
        string rendered = RulesOutputDigest.Render(demo);
        string digest = RulesOutputDigest.Digest(rendered);

        if (Environment.GetEnvironmentVariable(UpdateVariable) == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(goldenPath)!);
            await File.WriteAllTextAsync(goldenPath, digest);
            Console.WriteLine($"wrote {goldenPath}");
            return;
        }

        if (!File.Exists(goldenPath))
        {
            throw new SkipTestException(
                $"no golden for {Path.GetFileName(demoPath)}; regenerate with {UpdateVariable}=1");
        }

        string expected = await File.ReadAllTextAsync(goldenPath);

        // A demo that parsed to nothing would produce a stable digest that pins nothing, so check
        // the run actually did work before trusting a match.
        await Assert.That(rendered).Contains("[nodes]");
        await Assert.That(demo.Frames.Count).IsGreaterThan(0);

        await Assert.That(Normalize(digest)).IsEqualTo(Normalize(expected))
            .Because($"rules output changed for {Path.GetFileName(demoPath)}. If that was intended, "
                     + $"re-run with {UpdateVariable}=1 and read the diff before committing it.");
    }

    private static string Normalize(string s) => s.Replace("\r\n", "\n").TrimEnd('\n');

    private static string? RepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8 && dir is not null; i++)
        {
            if (File.Exists(Path.Combine(dir, "CS2DemoKit.slnx")))
            {
                return dir;
            }

            dir = Path.GetDirectoryName(dir);
        }

        return null;
    }
}
