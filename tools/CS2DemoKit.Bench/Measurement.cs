#region

using System.Diagnostics;
using System.Runtime;
using CS2DemoKit.Analysis;
using CS2DemoKit.Analysis.Graphs;
using CS2DemoKit.Analysis.Visibility;
using CS2DemoKit.Analysis.Yaml;
using CS2DemoKit.Parser;

#endregion

namespace CS2DemoKit.Bench;

/// <summary>
///     One measured demo load, emitted as a CSV row.
///     <para>
///         A fresh process per measurement is the point: no cross-run heap state, no allocator
///         history, nothing carried between runs. That is why <see cref="Sweep" /> re-invokes this
///         executable per measurement instead of looping in-process.
///     </para>
///     <para>
///         The warm-up runs a full discarded pipeline so no timed phase pays JIT, and each phase is
///         preceded by a forced blocking collection so a collection owed to earlier garbage cannot
///         land inside the window being measured.
///     </para>
///     <para>
///         The ray path is measured when, and only when, <c>CS2DEMOKIT_COLLISION_DIR</c> names a
///         directory holding the demo's map bake. Nothing else is consulted: the library's locator
///         would also walk up from the executable, and two arms published into two trees could
///         then load two different bakes, which is exactly the comparison this tool must never
///         make. With the variable set and no bake for the map the measurement fails rather than
///         quietly measuring the no-ray pipeline under the same label; with it unset the row says
///         <c>vis=0</c> and the orchestrator prints a banner, so a run without rays is never
///         mistaken for one with them.
///     </para>
///     <para>
///         The source compiles against whichever library its checkout holds, and the ray path needs
///         API that older checkouts lack. An arm published from one of those runs that checkout's
///         own, older bench, which casts no rays and emits a shorter row; <see cref="Reject" /> is
///         how an orchestrator built from this source keeps such a row out of its CSV.
///     </para>
/// </summary>
internal static class Measurement
{
    /// <summary>The CSV header. <c>load1</c> is appended by the orchestrator, not by a measurement.</summary>
    public const string Header =
        "variant,demo,run,parse_ms,p1_ms,p2_ms,p3_ms,parse_pause_ms,parse_alloc_mb,retained_mb,"
        + "gen0,gen1,gen2,build_ms,eval_ms,eval_pause_ms,eval_alloc_mb,frames,inner_messages,"
        + "enum_ms,enum_alloc_mb,enum_pause_ms,walked,eval_gen0,eval_gen1,eval_gen2,"
        + "vis,rulesets,bake_read_ms,bvh_build_ms,sampled_ticks,pairs,rays_cast,load1";

    /// <summary>The env var naming the directory of per-map bakes, the same one the library's locator reads.</summary>
    public const string CollisionDirVariable = CollisionAssetLocator.EnvVar;

    /// <summary>
    ///     The older name the library's locator still honours as a fallback. This tool deliberately
    ///     does not: the bake must come from one variable both arms see, and the banner says which.
    /// </summary>
    public const string LegacyCollisionDirVariable = "DEMOVIEWER_COLLISION_DIR";

    // The shipped rulesets subscribe to nothing that needs geometry, so with them alone an engine
    // is handed to the builder and never asked a question. This overlay is the smallest subscriber
    // there is: it turns the transition scan on, and the scan's cost is the sampling, not the
    // stats that read its events, so one counter measures the same rays the app's aim ruleset
    // would. Loaded in both modes so the graph is identical with and without the bake.
    private const string VisibilityOverlayLabel = "bench-visibility.rules.yaml";

    private const string VisibilityOverlayYaml =
        """
        ruleset: bench_visibility
        for: each_player
        stats:
          spots:
            count: enemy_spotted
            per: match
        """;

    private static double Ms(long t) => (Stopwatch.GetTimestamp() - t) * 1000.0 / Stopwatch.Frequency;

    private static void Settle()
    {
        GC.Collect(2, GCCollectionMode.Forced, true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, true);
    }

    /// <summary>True when the ray path is armed for this process, so an orchestrator can say so once up front.</summary>
    public static bool RayPathArmed =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(CollisionDirVariable));

    /// <summary>
    ///     Why a row a child emitted cannot sit under this orchestrator's header, or null when it
    ///     can. A compare spawns two builds of this tool and only the newer may know the ray path
    ///     exists: an arm published from a checkout before the path was measured never reads
    ///     <c>CS2DEMOKIT_COLLISION_DIR</c>, casts no rays, and emits the older, shorter row, which
    ///     would otherwise land in the CSV under a header and a banner that promise rays, next to an
    ///     arm that cast them. So a row is accepted only when it has exactly the columns the header
    ///     names, less the <c>load1</c> the orchestrator appends, and its <c>vis</c> field says what
    ///     the banner said: 1 with the ray path armed, 0 without.
    /// </summary>
    public static string? Reject(string row)
    {
        string[] columns = Header.Split(',');
        string[] fields = row.Split(',');
        int expected = columns.Length - 1;
        if (fields.Length != expected)
        {
            return $"row has {fields.Length} fields where this header names {expected} before load1; "
                   + "the arm was published from a checkout whose bench measures something else";
        }

        string vis = fields[Array.IndexOf(columns, "vis")];
        string armed = RayPathArmed ? "1" : "0";
        return vis == armed
            ? null
            : $"row carries vis={vis} but the ray path is {(RayPathArmed ? "ON" : "OFF")} for this run";
    }

    /// <summary>
    ///     Resolves the bake for <paramref name="mapName" /> under <c>CS2DEMOKIT_COLLISION_DIR</c>, in
    ///     the two layouts the library's locator accepts. Null when the variable is unset; throws when
    ///     it is set and the map has no bake there, because that is a misconfigured run, not a mode.
    /// </summary>
    private static string? ResolveBake(string mapName)
    {
        string? dir = Environment.GetEnvironmentVariable(CollisionDirVariable);
        if (string.IsNullOrWhiteSpace(dir))
        {
            return null;
        }

        string flat = Path.Combine(dir, mapName + ".tris");
        if (File.Exists(flat))
        {
            return flat;
        }

        string nested = Path.Combine(dir, mapName, "collision.tris");
        if (File.Exists(nested))
        {
            return nested;
        }

        throw new FileNotFoundException(
            $"{CollisionDirVariable} is set but holds no bake for {mapName}: looked for {flat} and {nested}. "
            + "Unset it to measure without rays, or add the bake; a row without rays under a ray-path label is worthless.");
    }

    /// <summary>Runs one full load of <paramref name="demoPath" /> and returns its CSV row (no trailing load1).</summary>
    public static string Run(string demoPath, string variant, string runIndex)
    {
        byte[] bytes = File.ReadAllBytes(demoPath);
        RuleConfigLoadResult rules = YamlConfigLoader.LoadShippedWithOverlay(
            [(VisibilityOverlayLabel, VisibilityOverlayYaml)]);
        if (!rules.Success)
        {
            // The overlay tier is error-contained by design, so a broken overlay would otherwise
            // load as "no subscriber" and the run would measure no rays under a ray-path label.
            throw new InvalidOperationException(
                "bench visibility overlay failed to load: " + string.Join("; ", rules.Errors.Select(e => e.Message)));
        }

        Profiling.Enabled = true;

        // The bake is resolved from the warm-up parse's map name and built once, before the
        // warm-up, so the measured phases below neither pay for it nor skip the scanner's JIT. Its
        // read and build are timed by the library's own bake counters; a cold in-process figure,
        // which is what a host pays once per map.
        ParsedDemo w = MemoryMappedDemoSource.ParseFile(demoPath);
        string? trisPath = ResolveBake(w.MapName);
        VisibilityEngine? engine = null;
        double bakeReadMs = 0, bvhBuildMs = 0;
        if (trisPath is not null)
        {
            VisibilityCounters.Reset();
            engine = VisibilityEngine.Load(trisPath);
            VisibilityCountersSnapshot bake = VisibilityCounters.Snapshot();
            bakeReadMs = bake.BakeLoadMs;
            bvhBuildMs = bake.BvhBuildMs;
        }

        AnalysisOptions options = new()
        {
            VisibilityEngine = engine
        };

        // Warm-up: full pipeline once, discarded, so no timed phase pays JIT. The ray counters are
        // on for this pass only: the work they count is a function of the demo and the bake, so it
        // is the same in the measured pass, and their Interlocked bookkeeping per pair would
        // otherwise sit inside the eval window being timed.
        long sampledTicks, pairs, raysCast;
        {
            VisibilityCounters.Reset();
            VisibilityCounters.Enabled = engine is not null;
            BuildResult wb = DemoAnalysis.Build(w, rules.Rulesets, options);
            _ = DemoAnalysis.Evaluate(w, wb, options);
            VisibilityCounters.Enabled = false;
            VisibilityCountersSnapshot rays = VisibilityCounters.Snapshot();
            VisibilityCounters.Reset();
            sampledTicks = rays.SampledTicks;
            pairs = rays.PairsEvaluated;
            raysCast = rays.RaysCast;
        }

        if (engine is not null && sampledTicks == 0)
        {
            // The bake loaded and the subscriber is present, yet the scanner never sampled: the
            // build gated it out. Whatever the reason, this row would measure no rays under a
            // label that promises them.
            throw new InvalidOperationException(
                $"ray path armed for {w.MapName} but the transition scanner never sampled a tick; refusing to emit a row");
        }

        Settle();
        long memBefore = GC.GetTotalMemory(true);
        long allocBefore = GC.GetTotalAllocatedBytes();
        TimeSpan pauseBefore = GC.GetTotalPauseDuration();
        int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2);

        long t = Stopwatch.GetTimestamp();
        ParsedDemo demo = DemoParser.Parse(bytes.AsMemory());
        double parseMs = Ms(t);

        double parsePause = (GC.GetTotalPauseDuration() - pauseBefore).TotalMilliseconds;
        long parseAlloc = GC.GetTotalAllocatedBytes() - allocBefore;
        int p0 = GC.CollectionCount(0) - g0, p1c = GC.CollectionCount(1) - g1, p2c = GC.CollectionCount(2) - g2;
        ParseProfilingSnapshot snap = ParseProfilingSnapshot.Read();

        // Live set with only the ParsedDemo (and the file bytes, which are in memBefore too) reachable.
        // The slabs are large objects and a gen2 does not compact the LOH by default, so compact once or
        // the figure counts segment slack as live data.
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(2, GCCollectionMode.Forced, true, true);
        GC.WaitForPendingFinalizers();
        long retained = GC.GetTotalMemory(false) - memBefore;

        long innerMessages = 0;
        foreach (DemoFrame f in demo.Frames)
        {
            innerMessages += f.InnerMessages.Count;
        }

        Settle();
        long tb = Stopwatch.GetTimestamp();
        BuildResult build = DemoAnalysis.Build(demo, rules.Rulesets, options);
        double buildMs = Ms(tb);

        Settle();
        long allocEval = GC.GetTotalAllocatedBytes();
        TimeSpan pauseEval = GC.GetTotalPauseDuration();
        int e0 = GC.CollectionCount(0), e1 = GC.CollectionCount(1), e2 = GC.CollectionCount(2);
        long te = Stopwatch.GetTimestamp();
        _ = DemoAnalysis.Evaluate(demo, build, options);
        double evalMs = Ms(te);
        double evalPause = (GC.GetTotalPauseDuration() - pauseEval).TotalMilliseconds;
        long evalAlloc = GC.GetTotalAllocatedBytes() - allocEval;
        int ev0 = GC.CollectionCount(0) - e0, ev1 = GC.CollectionCount(1) - e1, ev2 = GC.CollectionCount(2) - e2;

        // Full enumeration of InnerMessages across every frame: what a consumer walking the message
        // list actually pays. Under a lazy view this is where synthesis lands, so it is the number that
        // decides whether "transient garbage is cheap" holds on a real traversal.
        Settle();
        long allocEnum = GC.GetTotalAllocatedBytes();
        TimeSpan pauseEnum = GC.GetTotalPauseDuration();
        long ten = Stopwatch.GetTimestamp();
        long walked = 0;
        foreach (DemoFrame f in demo.Frames)
        {
            // foreach, the path a consumer normally takes and the one the composed view optimises.
            foreach (NetMessage msg in f.InnerMessages)
            {
                if (msg.Payload is CSVCMsg_PacketEntities)
                {
                    walked++;
                }
            }
        }

        double enumMs = Ms(ten);
        double enumPause = (GC.GetTotalPauseDuration() - pauseEnum).TotalMilliseconds;
        long enumAlloc = GC.GetTotalAllocatedBytes() - allocEnum;

        GC.KeepAlive(demo);
        GC.KeepAlive(walked);
        GC.KeepAlive(engine);

        const double MB = 1024.0 * 1024;
        return string.Join(",",
            variant, Path.GetFileName(demoPath), runIndex,
            parseMs.ToString("F2"),
            (snap.Pass1HeaderTicks * 1000.0 / Stopwatch.Frequency).ToString("F2"),
            (snap.Pass2WallTicks * 1000.0 / Stopwatch.Frequency).ToString("F2"),
            (snap.Pass3EnrichTicks * 1000.0 / Stopwatch.Frequency).ToString("F2"),
            parsePause.ToString("F2"),
            (parseAlloc / MB).ToString("F1"),
            (retained / MB).ToString("F1"),
            p0, p1c, p2c,
            buildMs.ToString("F2"),
            evalMs.ToString("F2"),
            evalPause.ToString("F2"),
            (evalAlloc / MB).ToString("F1"),
            snap.FrameCount, innerMessages,
            enumMs.ToString("F2"),
            (enumAlloc / MB).ToString("F1"),
            enumPause.ToString("F2"),
            walked,
            ev0, ev1, ev2,
            engine is null ? 0 : 1,
            rules.Rulesets.Count,
            bakeReadMs.ToString("F2"),
            bvhBuildMs.ToString("F2"),
            sampledTicks, pairs, raysCast);
    }
}
