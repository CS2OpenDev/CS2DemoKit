#region

using CS2DemoKit.Parser;
using CS2DemoKit.Parser.EntityTracking;
using CS2DemoKit.TestSupport;

#endregion

namespace CS2DemoKit.Analysis.Tests;

/// <summary>
///     Multi-demo parser canary. Runs the
///     "parse + entity replay does not throw" smoke check across every
///     <c>.dem</c> file found under the repo's <c>demos/</c> tree (and the
///     bench-suite under <c>demos/benchmarks/</c>). Catches "new CS2 patch
///     broke decoding on demos from one source profile" failure modes that a
///     single-demo smoke test misses — the specific concern is the
///     pinned-demo case where the parser would silently regress on demos
///     recorded after a patch.
///     <para>
///         <b>Limitation:</b> the audit envisioned this as a nightly CI test
///         that downloads or pulls the most-recent demo per source profile.
///         That upgrade is gated on F3 (CI demo provisioning) — without
///         demo distribution, "recent" demos can't be fetched
///         automatically. The local-demo sweep is the current substitute:
///         it catches "doesn't parse on this contributor's local set" but
///         not "doesn't parse on yesterday's HLTV match." When F3 lands
///         the test can be extended with a download-and-canary mode.
///     </para>
///     <para>
///         Skip-as-pass semantics: if no demos are discoverable, the test
///         method generates zero parameterised cases (rather than failing).
///         This is intentional — the canary's value comes from broad demo
///         coverage; on a clean clone with no demos it has nothing to assert.
///         Pair with F3 if you need stricter "must have demos" gating.
///     </para>
/// </summary>
[NotInParallel]
[Category("Smoke")]
public class MultiDemoCanaryTests
{
    /// <summary>
    ///     Discovers every <c>.dem</c> file under the repo's <c>demos/</c>
    ///     subtree. Uses the canonical <see cref="DemoTestHelper" /> repo-root
    ///     walk so the data source is consistent with the rest of the test
    ///     suite.
    /// </summary>
    public static IEnumerable<string> AllDiscoveredDemos()
    {
        string? repoRoot = FindRepoRoot();
        if (repoRoot is null)
        {
            yield break;
        }

        string demosRoot = Path.Combine(repoRoot, "demos");
        if (!Directory.Exists(demosRoot))
        {
            yield break;
        }

        // Recursive sweep, ordered for determinism. Cap at 25 demos so a
        // contributor with 200 demos doesn't get a 30-minute test run —
        // the canary's value is breadth, not exhaustive coverage.
        List<string> demos = Directory.EnumerateFiles(demosRoot, "*.dem", SearchOption.AllDirectories)
            .OrderBy(p => p, StringComparer.Ordinal)
            .Take(25)
            .ToList();
        foreach (string path in demos)
        {
            yield return path;
        }
    }

    /// <summary>Parse and replay_does not throw.</summary>
    [Test]
    [MethodDataSource(nameof(AllDiscoveredDemos))]
    public async Task ParseAndReplay_DoesNotThrow(string demoPath)
    {
        // The single discovered-demo equivalent of this lives in
        // DemoParserTests.ParseDemo_DoesNotThrow. This multi-demo variant
        // exercises the SAME contract across every locally-available demo so
        // a patch-induced failure shows up as a specific failing test (with
        // the demo path baked into the test name) rather than a roll-the-dice
        // outcome on which demo got discovered first.
        await Assert.That(File.Exists(demoPath)).IsTrue();

        Exception? caught = null;
        ParsedDemo? parsed = null;
        try
        {
            byte[] bytes = await File.ReadAllBytesAsync(demoPath);
            parsed = DemoParser.Parse(bytes.AsMemory());
        }
        catch (Exception ex)
        {
            caught = ex;
        }

        if (caught is not null)
        {
            Console.WriteLine($"PARSE FAILED on {Path.GetFileName(demoPath)}: " +
                              $"{caught.GetType().Name}: {caught.Message}");
        }

        await Assert.That(caught).IsNull();
        await Assert.That(parsed!.Frames.Count).IsGreaterThan(0);

        // Quick entity-replay too — the second class of "patch broke our parser"
        // failure mode (mid-replay bit-misalignment).
        EntityTracker tracker = new();
        Exception? replayError = null;
        try
        {
            tracker.Replay(parsed.Frames);
        }
        catch (Exception ex)
        {
            replayError = ex;
        }

        if (replayError is not null)
        {
            Console.WriteLine($"REPLAY FAILED on {Path.GetFileName(demoPath)}: " +
                              $"{replayError.GetType().Name}: {replayError.Message}");
        }
        else if (tracker.LastEntityError is { } err)
        {
            Console.WriteLine($"REPLAY error (recorded, not thrown) on {Path.GetFileName(demoPath)}: {err}");
        }

        await Assert.That(replayError).IsNull();
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
