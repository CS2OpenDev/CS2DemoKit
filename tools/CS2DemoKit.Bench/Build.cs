#region

using System.Diagnostics;
using System.Runtime;
using CS2DemoKit.Analysis.Visibility;

#endregion

namespace CS2DemoKit.Bench;

/// <summary>
///     Cost and shape of building a tree over one or more bakes, in one process, for both
///     topologies: the binary median-split tree the library shipped before the eight-wide rebuild
///     (<see cref="BinaryTriangleBvh" />) and the eight-wide tree. Per arm and per round it reports
///     wall-clock, bytes allocated, the tree's retained size and the heap's sampled high-water mark
///     during the build; for the eight-wide arm it also reports, once, a digest of the tree's
///     structure (every lane's box bits and child reference, every slot's triangle and lane, node
///     count, depth and stack capacity) and the most threads the build ran on at once, which is one
///     with <c>--threads 1</c> and never more than the flag.
///     <para>
///         Both arms run in every round and the order flips each round, so drift lands on both
///         rather than on whichever runs second. The binary builder is serial by construction and
///         takes no thread count, so <c>--threads 1</c> is the only setting under which the two
///         wall-clocks compare like for like: at that setting the difference is the topology alone.
///         At any higher setting the eight-wide figure is the topology change and the parallel build
///         together, and this verb's output cannot separate them. Run it twice, once at
///         <c>--threads 1</c> and once at the machine's core count, and the ratio between the two
///         eight-wide figures is the parallel build.
///     </para>
///     <para>
///         The digest is the identity check for a builder change: two builders that produce the
///         same digest produce the same tree, and every ray then answers identically without a
///         differential run. Allocation and retained bytes are exact and stable across runs. The
///         wall-clock is not: it drifts by tens of percent between sessions on this workstation, so
///         compare the arms within one invocation, never by quoting one run against a number from
///         another day.
///     </para>
///     <para>
///         Every live-set figure here - the baseline, the retained size, and each sample of the
///         <c>--live</c> peak - is read after a compacting collection, because both builders work
///         in large arrays and a gen2 does not compact the large object heap by default. Without
///         that, a figure counts segment slack as live data, and the two builders leave different
///         slack behind, so a difference between two uncompacted figures would be part
///         fragmentation and part live set with no way to tell the shares apart.
///     </para>
///     <para>
///         The sampled peak without <c>--live</c> is a thread polling the GC's total-bytes figure
///         every fraction of a millisecond without collecting, so it is a floor on the heap's true
///         high-water mark, garbage included: whenever no collection runs during a build it simply
///         equals the bytes allocated, and it never reads as a live set. With <c>--live</c> each
///         sample forces a compacting collection first, so the peak is a floor on the builder's
///         live set instead; that slows the build by orders of magnitude, so the flag reports
///         memory only and no wall-clock. It also perturbs what it measures - the sampler holds a
///         core, and every forced collection suspends the build's workers - so a parallel arm read
///         this way is not comparable with a serial one. Take <c>--live</c> at <c>--threads 1</c>.
///         Both peaks are repeatable to a few megabytes.
///     </para>
/// </summary>
internal static class Build
{
    public static int Run(string[] args)
    {
        if (args.Length == 0 || args.Contains("--help") || args.Contains("-h"))
        {
            Console.Error.WriteLine("usage: CS2DemoKit.Bench build <collision.tris> [<collision.tris> ...] [--rounds R] [--threads T] [--live]");
            return 2;
        }

        int rounds = 5;
        int threads = 0;
        bool live = false;
        List<string> paths = [];
        for (int i = 0; i < args.Length; i++)
        {
            string? next = i + 1 < args.Length ? args[i + 1] : null;
            if (args[i] == "--rounds" && next is not null && int.TryParse(next, out int r) && r > 0)
            {
                rounds = r;
                i++;
                continue;
            }

            if (args[i] == "--threads" && next is not null && int.TryParse(next, out int t) && t > 0)
            {
                threads = t;
                i++;
                continue;
            }

            if (args[i] == "--live")
            {
                live = true;
                continue;
            }

            if (!File.Exists(args[i]))
            {
                Console.Error.WriteLine($"no bake at {args[i]}");
                return 1;
            }

            paths.Add(args[i]);
        }

        foreach (string path in paths)
        {
            Measure(path, rounds, threads, live);
        }

        return 0;
    }

    private static void Measure(string path, int rounds, int threads, bool live)
    {
        CollisionTris.Data bake = CollisionTris.Load(path);
        string name = Path.GetFileName(Path.GetDirectoryName(path)) ?? path;
        int wideThreads = threads > 0 ? threads : Environment.ProcessorCount;
        string parity = wideThreads == 1
            ? "equal parallelism, so the difference between the arms is the topology alone"
            : "not equal parallelism, so the wide arm carries the topology and the parallel build together";
        Console.WriteLine($"{name}: {bake.TriangleCount} triangles, wide build on {wideThreads} thread{(wideThreads == 1 ? string.Empty : "s")}, "
                          + $"binary build serial by construction ({parity})");

        // Round 0 is the cold build an engine load pays, JIT tier-up included; the rest are the
        // builders' steady state. Every round is reported, none is dropped. The two arms alternate
        // within the round so session drift lands on both rather than on whichever runs second.
        for (int round = 0; round < rounds; round++)
        {
            for (int step = 0; step < 2; step++)
            {
                bool binary = (step == 0) == (round % 2 == 0);
                Console.WriteLine(binary
                    ? Round("binary", round, live, () => BinaryTriangleBvh.Build(bake.Vertices, bake.TriangleCount))
                    : Round("wide  ", round, live, () => TriangleBvh.Build(bake.Vertices, bake.TriangleCount, threads)));
            }
        }
    }

    // One build in its own frame, so the previous round's tree is unreachable when this round's
    // baseline is taken; inside the loop the JIT keeps the old reference alive across iterations
    // and every round after the first reports a retained size of zero.
    private static string Round(string arm, int round, bool live, Func<object> build)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        long baseline = LiveBytes();
        long allocBefore = GC.GetTotalAllocatedBytes(true);

        using HeapSampler sampler = new(live);
        Stopwatch sw = Stopwatch.StartNew();
        object tree = build();
        double ms = sw.Elapsed.TotalMilliseconds;
        long peak = sampler.Stop();

        long alloc = GC.GetTotalAllocatedBytes(true) - allocBefore;
        long retained = LiveBytes() - baseline;
        string shape = round == 0 ? Shape(tree) : string.Empty;
        GC.KeepAlive(tree);
        string clock = live ? "  (live, no clock)" : $"{ms,7:F1} ms,";
        string kind = live ? "live peak over baseline" : "peak over baseline";
        return $"  round {round} {arm}: {clock} alloc {alloc / 1048576.0,6:F1} MB, retained {retained / 1048576.0,6:F1} MB, "
               + $"{kind} {(peak - baseline) / 1048576.0,6:F1} MB{shape}";
    }

    /// <summary>The tree's shape, printed on the cold round only; the digest is the eight-wide tree's identity.</summary>
    private static string Shape(object tree) => tree switch
    {
        TriangleBvh wide => $" digest {wide.StructuralDigest():X16} nodes {wide.NodeCount} depth {wide.Depth} stack {wide.StackCapacity} workers {wide.PeakBuildWorkers}",
        BinaryTriangleBvh binary => $" nodes {binary.NodeCount}",
        _ => string.Empty
    };

    /// <summary>
    ///     The live set, read after a compacting collection. Both builders work in large arrays, and
    ///     a gen2 leaves the large object heap uncompacted unless asked, so an uncompacted figure
    ///     counts segment slack as live data. The two builders leave different slack behind - the
    ///     binary builder allocates several large intermediates and drops them, the eight-wide one
    ///     pools its segment arrays and releases them - so an uncompacted difference between the
    ///     arms is part fragmentation and part live set. Compaction is requested one collection at a
    ///     time and the setting resets afterwards, so every reading asks again.
    /// </summary>
    private static long LiveBytes()
    {
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(2, GCCollectionMode.Forced, true, true);
        return GC.GetTotalMemory(false);
    }

    /// <summary>
    ///     A background thread recording the largest GC total-bytes figure it sees until stopped.
    ///     Forcing a compacting collection before each sample turns the figure from heap size into
    ///     live set.
    /// </summary>
    private sealed class HeapSampler : IDisposable
    {
        private readonly bool _collect;
        private readonly Thread _thread;
        private long _peak;
        private volatile bool _running = true;

        public HeapSampler(bool collect)
        {
            _collect = collect;
            _thread = new Thread(Sample) { IsBackground = true, Name = "heap-sampler" };
            _thread.Start();
        }

        public void Dispose() => Stop();

        public long Stop()
        {
            if (_running)
            {
                _running = false;
                _thread.Join();
            }

            return _peak;
        }

        private void Sample()
        {
            while (_running)
            {
                long now = _collect ? LiveBytes() : GC.GetTotalMemory(false);
                if (now > _peak)
                {
                    _peak = now;
                }

                Thread.SpinWait(2000);
            }
        }
    }
}
