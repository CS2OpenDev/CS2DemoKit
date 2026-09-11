#region

using System.Diagnostics;
using CS2DemoKit.Analysis.Visibility;

#endregion

namespace CS2DemoKit.Bench;

/// <summary>
///     Cost and shape of building the eight-wide tree over one or more bakes: wall-clock per
///     build, bytes allocated per build, the heap's sampled high-water mark during the build, the
///     tree's retained size, and a digest of its structure (every lane's box bits and child
///     reference, every slot's triangle and lane, node count, depth and stack capacity).
///     <para>
///         The digest is the identity check for a builder change: two builders that produce the
///         same digest produce the same tree, and every ray then answers identically without a
///         differential run. Allocation and retained bytes are exact and stable across runs. The
///         wall-clock is not: it drifts by tens of percent between sessions on this workstation, so
///         compare builders by running each arm's published copy of this verb alternately in one
///         sitting, never by quoting one run against a number from another day. The sampled peak is
///         a thread polling the GC's total-bytes figure every fraction of a millisecond, so it is a
///         floor on the heap's true high-water mark, garbage included: whenever no collection runs
///         during a build it simply equals the bytes allocated, and it never reads as a live set.
///         With <c>--live</c> each sample first forces a full collection, so the peak is a floor on
///         the builder's live set instead; that slows the build by an order of magnitude, so the
///         flag reports memory only and no wall-clock. Both peaks are repeatable to a few megabytes.
///     </para>
/// </summary>
internal static class Build
{
    public static int Run(string[] args)
    {
        if (args.Length == 0 || args.Contains("--help") || args.Contains("-h"))
        {
            Console.Error.WriteLine("usage: CS2DemoKit.Bench build <collision.tris> [<collision.tris> ...] [--rounds R] [--live]");
            return 2;
        }

        int rounds = 5;
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
            Measure(path, rounds, live);
        }

        return 0;
    }

    private static void Measure(string path, int rounds, bool live)
    {
        CollisionTris.Data bake = CollisionTris.Load(path);
        string name = Path.GetFileName(Path.GetDirectoryName(path)) ?? path;
        Console.WriteLine($"{name}: {bake.TriangleCount} triangles");

        // Round 0 is the cold build an engine load pays, JIT tier-up included; the rest are the
        // builder's steady state. Every round is reported, none is dropped.
        for (int round = 0; round < rounds; round++)
        {
            Console.WriteLine(Round(bake, round, live));
        }
    }

    // One build in its own frame, so the previous round's tree is unreachable when this round's
    // baseline is taken; inside the loop the JIT keeps the old reference alive across iterations
    // and every round after the first reports a retained size of zero.
    private static string Round(CollisionTris.Data bake, int round, bool live)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        long baseline = GC.GetTotalMemory(true);
        long allocBefore = GC.GetTotalAllocatedBytes(true);

        using HeapSampler sampler = new(live);
        Stopwatch sw = Stopwatch.StartNew();
        TriangleBvh bvh = TriangleBvh.Build(bake.Vertices, bake.TriangleCount);
        double ms = sw.Elapsed.TotalMilliseconds;
        long peak = sampler.Stop();

        long alloc = GC.GetTotalAllocatedBytes(true) - allocBefore;
        long retained = GC.GetTotalMemory(true) - baseline;
        string digest = round == 0
            ? $" digest {bvh.StructuralDigest():X16} nodes {bvh.NodeCount} depth {bvh.Depth} stack {bvh.StackCapacity}"
            : string.Empty;
        GC.KeepAlive(bvh);
        string clock = live ? "  (live, no clock)" : $"{ms,7:F1} ms,";
        string kind = live ? "live peak over baseline" : "peak over baseline";
        return $"  round {round}: {clock} alloc {alloc / 1048576.0,6:F1} MB, retained {retained / 1048576.0,6:F1} MB, "
               + $"{kind} {(peak - baseline) / 1048576.0,6:F1} MB{digest}";
    }

    /// <summary>
    ///     A background thread recording the largest GC total-bytes figure it sees until stopped.
    ///     Forcing a collection before each sample turns the figure from heap size into live set.
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
                long now = GC.GetTotalMemory(_collect);
                if (now > _peak)
                {
                    _peak = now;
                }

                Thread.SpinWait(2000);
            }
        }
    }
}
