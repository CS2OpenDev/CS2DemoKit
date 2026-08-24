#region

using System.Security.Cryptography;
using CS2DemoKit.Parser;
using CS2DemoKit.Parser.GameEvents;
using TUnit.Core.Exceptions;

#endregion

namespace CS2DemoKit.TestSupport;

/// <summary>
///     Resolves CS2 <c>.dem</c> files for integration tests, and provides a
///     <see cref="RequireDemo()" /> helper that throws <see cref="SkipTestException" /> when no
///     demo is available — so TUnit reports the test under <c>skipped:</c> rather than the
///     misleading <c>succeeded:</c> count that an early <c>return</c> produces.
///     <para>
///         <b>Discovery order</b> (first match wins):
///         <list type="number">
///             <item>The <c>DEMO_PATH</c> environment variable, if it points at an existing file.</item>
///             <item><see cref="ReferenceDemoFileName" />, wherever the named-file lookup finds it.</item>
///             <item>The first <c>*.dem</c> by ordinal filename under <c>TestData/</c> next to the test assembly.</item>
///             <item>
///                 The first <c>*.dem</c> by ordinal filename under <c>&lt;repo-root&gt;/demos/benchmarks/</c>
///                 or <c>&lt;repo-root&gt;/demos/</c>.
///             </item>
///         </list>
///         The repo root is located by walking up from <see cref="AppContext.BaseDirectory" /> until
///         a folder containing <c>CS2DemoKit.slnx</c> is found. No hard-coded personal paths.
///     </para>
///     <para>
///         <b>Sorted, and announced.</b> Steps 3 and 4 sort rather than taking enumeration order,
///         which is undefined and filesystem-dependent, and the resolved file is printed once per
///         process. Both exist because the fallback is a moving target: adding a demo to the corpus
///         can change which one every demo-agnostic test runs against, and that has to be visible in
///         the output rather than inferred from a bisect. See issue #39.
///     </para>
/// </summary>
public static class DemoTestHelper
{
    /// <summary>
    ///     Locates a demo file via the discovery order in the class summary. Returns <c>null</c>
    ///     when nothing matches. Prefer <see cref="RequireDemo()" /> in tests so the missing-demo
    ///     state surfaces as a skip rather than a misleading pass.
    /// </summary>
    /// <summary>
    ///     Canonical "reference" demo for integration tests that need a
    ///     deterministic structural shape (5v5 MM, ~22 rounds, no OT). Pinned
    ///     here so every demo-agnostic test gets the SAME demo across runs and
    ///     machines, addressing audit S10's "first-found means whichever
    ///     contributor's TestData/ happened to be at the front of an
    ///     enumeration." The plain <see cref="FindDemoPath()" /> chain prefers
    ///     this demo when available before falling back to first-found.
    /// </summary>
    public const string ReferenceDemoFileName = "003816248937665266002_0544286934.dem";

    /// <summary>
    ///     Whether <see cref="FindDemoPath()" /> may fall back to the committed sample demo in
    ///     <c>tests/assets/</c>. Off by default: the sample is a four-round trim, so it satisfies
    ///     structural tests but not assertions pinned against a full match. A test assembly opts
    ///     in from a <c>[ModuleInitializer]</c>.
    /// </summary>
    public static bool AllowSampleDemo { get; set; }

    /// <summary>
    ///     Maximum number of <see cref="ParsedDemo" /> instances the process-wide cache retains.
    ///     The bound is load-bearing, not a tuning knob: the full App suite touches ~6 distinct
    ///     large demos, and an unbounded cache accumulates all of them for process lifetime —
    ///     enough to get the test process killed by the OS mid-suite on a memory-pressured
    ///     16 GB dev machine (measured: the suite process peaks ~4.7 GB with the machine's
    ///     compressor already holding ~8 GB). Capacity 1 caches only the current demo — the
    ///     reference demo shared by most classes stays hot across long class runs, and the
    ///     handful of pro-demo classes pay one re-parse each. Override with the
    ///     <c>DEMOVIEWER_TEST_PARSE_CACHE</c> env var on machines with more headroom.
    /// </summary>
    private static readonly int _parseCacheCapacity =
        int.TryParse(Environment.GetEnvironmentVariable("DEMOVIEWER_TEST_PARSE_CACHE"), out int cap)
        && cap >= 1
            ? cap
            : 1;

    /// <summary>Guards <see cref="_parseCacheMap" /> and <see cref="_parseCacheOrder" />.</summary>
    private static readonly Lock _parseCacheLock = new();

    /// <summary>LRU order for the parse cache: most recently used at the front.</summary>
    private static readonly LinkedList<(string Path, Lazy<ParsedDemo> Parse)> _parseCacheOrder = new();

    /// <summary>Path-keyed index into <see cref="_parseCacheOrder" />.</summary>
    private static readonly Dictionary<string, LinkedListNode<(string Path, Lazy<ParsedDemo> Parse)>>
        _parseCacheMap = new();

    /// <summary>
    ///     Returns the shared, cached <see cref="ParsedDemo" /> for <paramref name="path" />,
    ///     parsing it on first use. The result is shared across test classes and MUST be treated
    ///     as read-only — <see cref="ParsedDemo" /> exposes only immutable/read-only surface, and
    ///     stateful consumers (e.g. <c>EntityTracker</c>) build their own state from
    ///     <see cref="ParsedDemo.Frames" />. Tests that need to mutate parser output (or hold the
    ///     raw demo bytes alongside) should keep a private <c>DemoParser.Parse</c> call instead.
    ///     The cache is a small LRU (see <see cref="_parseCacheCapacity" />) — an evicted demo is
    ///     re-parsed on next use, and callers still holding an evicted instance keep it alive
    ///     until they finish (eviction only drops the cache's reference, so sharing stays safe).
    /// </summary>
    public static ParsedDemo GetOrParse(string path)
    {
        string fullPath = Path.GetFullPath(path);
        Lazy<ParsedDemo> parse;
        bool evicted = false;
        lock (_parseCacheLock)
        {
            if (_parseCacheMap.TryGetValue(fullPath, out LinkedListNode<(string Path, Lazy<ParsedDemo> Parse)>? node))
            {
                _parseCacheOrder.Remove(node);
                _parseCacheOrder.AddFirst(node);
                parse = node.Value.Parse;
            }
            else
            {
                parse = new Lazy<ParsedDemo>(
                    () => DemoParser.Parse(File.ReadAllBytes(fullPath).AsMemory()),
                    LazyThreadSafetyMode.ExecutionAndPublication);
                LinkedListNode<(string, Lazy<ParsedDemo>)> fresh = new((fullPath, parse));
                _parseCacheOrder.AddFirst(fresh);
                _parseCacheMap[fullPath] = fresh;
                while (_parseCacheMap.Count > _parseCacheCapacity)
                {
                    LinkedListNode<(string Path, Lazy<ParsedDemo> Parse)> evict = _parseCacheOrder.Last!;
                    _parseCacheOrder.RemoveLast();
                    _parseCacheMap.Remove(evict.Value.Path);
                    evicted = true;
                }
            }
        }

        if (evicted)
        {
            // Decommit the evicted demo BEFORE parsing the next one: without this the old
            // multi-GB ParsedDemo is garbage-but-resident exactly while the new parse
            // allocates its own — the peak that gets the process OS-killed on a
            // memory-pressured machine.
            GC.Collect(2, GCCollectionMode.Aggressive, true, true);
            GC.WaitForPendingFinalizers();
        }

        // Parse OUTSIDE the lock: Lazy(ExecutionAndPublication) still guarantees exactly one
        // parse per cached entry when classes race, without serializing unrelated lookups
        // behind a multi-second parse.
        return parse.Value;
    }

    /// <summary>Find demo path.</summary>
    public static string? FindDemoPath()
    {
        // 1. Explicit env var (developer override — always wins).
        string? env = Environment.GetEnvironmentVariable("DEMO_PATH");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env))
        {
            return Announce(env, "DEMO_PATH");
        }

        // 2. Reference demo, if available. Pinning to a specific filename when
        // possible gives tests a deterministic structural shape (addresses
        // audit S10). Falls through to first-sorted if the reference isn't
        // present locally.
        string? reference = FindDemoPath(ReferenceDemoFileName);
        if (reference is not null)
        {
            return Announce(reference, "reference demo");
        }

        // 3. TestData/ next to the test assembly.
        string testData = Path.Combine(AppContext.BaseDirectory, "TestData");
        if (Directory.Exists(testData))
        {
            if (FirstSorted(testData) is { } first)
            {
                return Announce(first, "TestData fallback");
            }
        }

        // 4. <repo-root>/demos/benchmarks/ then <repo-root>/demos/.
        foreach (string dir in RepoRelativeDemoDirs())
        {
            if (FirstSorted(dir) is { } first)
            {
                return Announce(first, "corpus fallback");
            }
        }

        return null;
    }

    /// <summary>
    ///     The first <c>*.dem</c> by ordinal filename, or null. Sorted rather than left to
    ///     <see cref="Directory.EnumerateFiles(string,string)" />, whose order is undefined and not
    ///     stable across filesystems: unsorted, two machines with an identical corpus can run the
    ///     same test against different demos.
    /// </summary>
    private static string? FirstSorted(string dir) =>
        Directory.EnumerateFiles(dir, "*.dem")
            .OrderBy(Path.GetFileName, StringComparer.Ordinal)
            .FirstOrDefault();

    /// <summary>Guards <see cref="_announced" />.</summary>
    private static readonly Lock _announceLock = new();

    /// <summary>Paths already named on the console, so a repeated lookup stays quiet.</summary>
    private static readonly HashSet<string> _announced = new(StringComparer.Ordinal);

    /// <summary>
    ///     Names the resolved demo once per distinct path. The demo is an input to hundreds of
    ///     assertions and used to be invisible: when the corpus grew, the selected demo changed,
    ///     tests began failing, and nothing in the output said the subject had moved, so the
    ///     failures read as a code regression.
    /// </summary>
    private static string Announce(string path, string how)
    {
        lock (_announceLock)
        {
            if (_announced.Add(path))
            {
                Console.WriteLine($"[demo] {how}: {Path.GetFileName(path)}");
            }
        }

        return path;
    }

    /// <summary>
    ///     Locates a specific demo by filename. Used by oracle tests that need a deterministic
    ///     known-good demo (e.g. <c>furia-vs-vitality-m1-mirage.dem</c>). Search is recursive
    ///     under <c>TestData/</c> and the repo-root demo directories.
    /// </summary>
    public static string? FindDemoPath(string filename)
    {
        // 1. TestData/<filename>
        string testDataPath = Path.Combine(AppContext.BaseDirectory, "TestData", filename);
        if (File.Exists(testDataPath))
        {
            return testDataPath;
        }

        // 2. Recursive search under the repo-root demo directories
        foreach (string dir in RepoRelativeDemoDirs())
        {
            string? match = Directory.EnumerateFiles(dir, filename, SearchOption.AllDirectories).FirstOrDefault();
            if (match is not null)
            {
                return match;
            }
        }

        // 3. tests/assets, unconditionally rather than behind AllowSampleDemo. That flag governs
        // whether the sample may be SUBSTITUTED for an absent full match; naming a file outright is
        // not a substitution, so it cannot silently redirect anything.
        if (FindRepoRoot() is { } repoRoot)
        {
            string asset = Path.Combine(repoRoot, "tests", "assets", filename);
            if (File.Exists(asset))
            {
                return asset;
            }
        }

        return null;
    }

    /// <summary>
    ///     Returns a demo path or throws <see cref="SkipTestException" />. Tests should call this
    ///     as the first line: any test that reaches the assertion stage is guaranteed to have a
    ///     real demo. TUnit catches the exception and reports the test under <c>skipped:</c>.
    /// </summary>
    public static string RequireDemo() =>
        FindDemoPath() ?? throw new SkipTestException(
            "No CS2 demo available. Set the DEMO_PATH env var to a .dem file, " +
            "place one under TestData/ next to the test assembly, " +
            "or under <repo-root>/demos/benchmarks/ or <repo-root>/demos/.");

    /// <summary>
    ///     Returns the path to a specific demo by filename or throws <see cref="SkipTestException" />.
    ///     Use this for oracle/regression tests pinned to a known reference demo.
    /// </summary>
    public static string RequireDemo(string filename) =>
        FindDemoPath(filename) ?? throw new SkipTestException(
            $"Required demo '{filename}' was not found. " +
            $"Place it under <repo-root>/demos/ (recursive lookup) or " +
            $"under TestData/ next to the test assembly.");

    /// <summary>
    ///     The committed sample demo: a four-round <c>de_nuke</c> trim, small enough to live in git.
    /// </summary>
    public const string SampleDemoFileName = "sample-de_nuke.dem";

    /// <summary>
    ///     A frame index that is during live play, for tests asserting "the match is running" rather
    ///     than anything about a particular moment.
    ///     <para>
    ///         <c>Frames.Count / 2</c> is not that. On a full match the frame midpoint can land in
    ///         warmup, a freeze period, or halftime, where no pawn is alive, which is why
    ///         midpoint-based assertions held on the four-round sample and failed on every real demo.
    ///         The median <c>player_death</c> is mid-match on any demo by construction.
    ///     </para>
    /// </summary>
    /// <param name="demo">The parsed demo.</param>
    /// <returns>A frame index during live play.</returns>
    /// <exception cref="SkipTestException">The demo has no kills, so it has no live play to anchor on.</exception>
    public static int LivePlayFrameIndex(ParsedDemo demo) => MedianKill(demo).FrameNumber;

    /// <summary>The <see cref="LivePlayFrameIndex" /> anchor as a server tick, for tick-based seeks.</summary>
    /// <param name="demo">The parsed demo.</param>
    /// <returns>A server tick during live play.</returns>
    /// <exception cref="SkipTestException">The demo has no kills, so it has no live play to anchor on.</exception>
    public static int LivePlayTick(ParsedDemo demo) => MedianKill(demo).ServerTick;

    private static GameEvent MedianKill(ParsedDemo demo)
    {
        ArgumentNullException.ThrowIfNull(demo);

        List<GameEvent> kills = demo.AllGameEvents
            .Where(e => string.Equals(e.Name, "player_death", StringComparison.Ordinal))
            .Where(e => e.FrameNumber >= 0 && e.FrameNumber < demo.Frames.Count)
            .OrderBy(e => e.FrameNumber)
            .ToList();

        return kills.Count > 0
            ? kills[kills.Count / 2]
            : throw new SkipTestException("Demo has no player_death events, so it has no live play to anchor on.");
    }

    /// <summary>
    ///     Lowercase hex SHA-256 of a demo file, matching the <c>demo_sha256</c> a fixture records.
    ///     A reference is only valid for the exact bytes it was pinned from: two files can carry the
    ///     same match and still differ, so comparing a live run against a fixture pinned from a
    ///     different recording produces divergences that read as engine regressions.
    /// </summary>
    /// <param name="path">The demo file.</param>
    /// <returns>The hash.</returns>
    public static string Sha256OfFile(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    /// <summary>
    ///     Walks up from <see cref="AppContext.BaseDirectory" /> until it finds a directory
    ///     containing <c>CS2DemoKit.slnx</c>. Used to locate <c>demos/</c> relative to
    ///     the repo, not relative to the build output. Returns <c>null</c> if no slnx is
    ///     found within 8 levels (defensive cap; the test assembly is typically 5–6 levels deep).
    /// </summary>
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

    // ── Internal: repo-root and demo-directory resolution ─────────────────────

    private static IEnumerable<string> RepoRelativeDemoDirs()
    {
        string? repoRoot = FindRepoRoot();
        if (repoRoot is null)
        {
            yield break;
        }

        string benchmarks = Path.Combine(repoRoot, "demos", "benchmarks");
        if (Directory.Exists(benchmarks))
        {
            yield return benchmarks;
        }

        string demos = Path.Combine(repoRoot, "demos");
        if (Directory.Exists(demos))
        {
            yield return demos;
        }

        // Last resort, opt-in: the committed sample (a trimmed de_nuke match, ~11 MB — small
        // enough to live in git) so a bare clone exercises demo-dependent tests instead of
        // skipping them all.
        //
        // Opt-in per test assembly rather than global, because the sample is TRIMMED: it holds
        // four rounds, not a full match. Suites whose assertions are structural (parse, decode,
        // seek) opt in; suites that compare against fixtures pinned on a full match must not, or
        // they fail against a demo those fixtures never described. Set it from a
        // [ModuleInitializer] in the test assembly.
        if (AllowSampleDemo)
        {
            string sample = Path.Combine(repoRoot, "tests", "assets");
            if (Directory.Exists(sample))
            {
                yield return sample;
            }
        }
    }
}
