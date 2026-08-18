#region

using CS2DemoKit.Analysis.GoldenStats;
using TUnit.Core.Exceptions;

#endregion

namespace CS2DemoKit.Analysis.Tests;

/// <summary>
///     Locates and loads golden-stats fixtures under
///     <c>tests/fixtures/&lt;demo-id&gt;/&lt;provider&gt;.golden.json</c>. Missing
///     fixtures throw <see cref="SkipTestException" /> so parity tests skip
///     cleanly on machines without the data, rather than hard-failing.
///     <para>
///         Kept in the Analysis.Tests project (rather than the cross-test-project
///         <c>CS2DemoKit.TestSupport</c> library) because it depends on
///         <c>GoldenStatsDocument</c> in <c>Analysis.GoldenStats</c>. Pulling
///         <c>Analysis</c> into <c>TestSupport</c> would transitively give
///         <c>Parser.Tests</c> an analyzer dependency it doesn't need.
///     </para>
/// </summary>
internal static class GoldenStatsTestHelper
{
    /// <summary>
    ///     Returns every <c>&lt;demo-id&gt;</c> subdirectory under
    ///     <c>tests/fixtures/</c>, sorted. Empty when the fixtures dir is missing —
    ///     callers should treat that as "no parity data available."
    /// </summary>
    public static IReadOnlyList<string> AllDemoIds()
    {
        string? root = FindRepoRoot();
        if (root is null)
        {
            return Array.Empty<string>();
        }

        string fixtures = Path.Combine(root, "tests", "fixtures");
        if (!Directory.Exists(fixtures))
        {
            return Array.Empty<string>();
        }

        return Directory.EnumerateDirectories(fixtures)
            .Select(Path.GetFileName)
            .Where(name => !string.IsNullOrEmpty(name))
            .Cast<string>()
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Returns the fixture directory for a demo, throws skip if missing.</summary>
    public static string FindFixtureDir(string demoId)
    {
        string? root = FindRepoRoot()
                       ?? throw new SkipTestException(
                           "Repo root not located from the test assembly (looked for CS2DemoKit.slnx).");

        string dir = Path.Combine(root, "tests", "fixtures", demoId);
        if (!Directory.Exists(dir))
        {
            throw new SkipTestException(
                $"Fixture directory missing: {Path.GetRelativePath(root, dir)}. " +
                "Run `dotnet run -c Release --project tools/AnalysisBench -- --suite` to produce it.");
        }

        return dir;
    }

    /// <summary>
    ///     Loads the golden-stats document for a given (demo, provider) pair. Throws
    ///     <see cref="SkipTestException" /> when the fixture isn't present.
    /// </summary>
    public static GoldenStatsDocument LoadGolden(string demoId, string provider)
    {
        string path = Path.Combine(FindFixtureDir(demoId), $"{provider}.golden.json");
        if (!File.Exists(path))
        {
            throw new SkipTestException(
                $"Golden file missing: {path}. The bench should produce it on the next --suite run.");
        }

        return GoldenStatsSerializer.ReadFromFile(path);
    }

    private static string? FindRepoRoot()
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
