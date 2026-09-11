using System.Diagnostics;
using System.Numerics;
using CS2DemoKit.Analysis.Visibility;

namespace CS2DemoKit.Bench;

/// <summary>
///     Per-ray throughput of the occlusion query on one named bake, for the old binary tree and
///     every traversal tier of the eight-wide tree this machine accelerates, with the work behind
///     each number: nodes popped, box tests and triangles tested per ray. A speedup that cannot be
///     explained by a drop in one of those is noise, and this verb exists so the next tree change
///     can be explained rather than asserted. The tiers do the same work per ray by construction
///     (same tree, same verdicts, same pop order), so between them only the time can differ.
///     <para>
///         The corpus is seeded and identical for every arm: origins uniform in the bake's bounds,
///         directions uniform on the sphere, lengths uniform in a fixed range, which is roughly the
///         mix of short and long sightlines a demo casts. It is not the mix the engine's plain
///         traversals see after the last-occluder hint has answered most rays (those are nearly all
///         occluded and short), so a per-core ratio measured here need not transfer to that
///         remainder; only the app's AnalysisBench on real demos can say what the ray phase costs.
///     </para>
///     <para>
///         Each tier has two arms. The plain arm casts every ray unhinted. The hinted arm casts
///         only the rays the scalar tier found occluded, each with its own blocking triangle as the
///         hint, so every one of them is answered by the hinted leaf test and the row is the cost
///         of that path alone: on a real demo it decides nine rays in ten, and it is the path the
///         vector tiers changed (their leaf test evaluates a whole register to read one lane).
///         Every timed pass runs the production traversal body, the one with no work counters
///         compiled in; the counts in the row come from a separate untimed pass with the counting
///         policy, which visits the same nodes and tests the same triangles by construction. The
///         counting policy records the traversal only, so a hinted row that short-circuits every
///         ray shows no nodes and no triangles: its work is one lane box test and one triangle test
///         per ray, outside the traversal, and its ns/ray is the whole of that.
///     </para>
///     <para>
///         Every arm is warmed on a slice of the corpus before the timed passes. The arms run
///         interleaved for <c>--rounds</c> rounds, the order flipped each round so drift lands on
///         every arm rather than the last one, and each arm's row is the median of its rounds.
///         Single-threaded unless <c>--threads</c> says otherwise; the threaded figure is aggregate
///         throughput and says nothing about per-core cost. Timings drift by tens of percent
///         between sessions on this workstation; compare arms within one invocation and quote the
///         per-ray work counts, which do not drift.
///     </para>
///     <para>
///         The verdict lines at the end are a smoke check, not the identity proof: the scalar arm
///         is compared to the binary tree (their differences are adjudicated per ray by the test
///         suite's differential harness) and each vector tier to the scalar arm on the occlusion
///         verdict only. <c>TraversalTierTests</c> is what holds the tiers to bit identity on the
///         hint and the nearest-hit distance as well.
///     </para>
/// </summary>
internal static class Rays
{
    private const float Eps = 0.1f;

    public static int Run(string[] args)
    {
        if (args.Length == 0 || args.Contains("--help") || args.Contains("-h"))
        {
            Console.Error.WriteLine("usage: CS2DemoKit.Bench rays <collision.tris> [--rays N] [--seed S] [--threads T] [--rounds R] [--min-len L] [--max-len L]");
            return 2;
        }

        string path = args[0];
        int rays = 1_000_000, seed = 20260910, threads = 1, rounds = 3;
        float minLen = 64f, maxLen = 2048f;
        for (int i = 1; i < args.Length; i++)
        {
            string? next = i + 1 < args.Length ? args[i + 1] : null;
            switch (args[i])
            {
                case "--rays" when next is not null && int.TryParse(next, out int n) && n > 0:
                    rays = n;
                    i++;
                    break;
                case "--seed" when next is not null && int.TryParse(next, out int s):
                    seed = s;
                    i++;
                    break;
                case "--threads" when next is not null && int.TryParse(next, out int t) && t > 0:
                    threads = t;
                    i++;
                    break;
                case "--rounds" when next is not null && int.TryParse(next, out int r) && r > 0:
                    rounds = r;
                    i++;
                    break;
                case "--min-len" when next is not null && float.TryParse(next, out float lo) && lo > 0f:
                    minLen = lo;
                    i++;
                    break;
                case "--max-len" when next is not null && float.TryParse(next, out float hi) && hi > 0f:
                    maxLen = hi;
                    i++;
                    break;
                default:
                    Console.Error.WriteLine($"unrecognised argument: {args[i]}");
                    return 2;
            }
        }

        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"no bake at {path}");
            return 1;
        }

        CollisionTris.Data bake = CollisionTris.Load(path);
        Console.WriteLine($"bake: {path} ({bake.TriangleCount} triangles)");

        // Each tree is built twice: the first build is what an engine load pays (it includes the
        // builder's own JIT tier-up), the second is the builder's steady-state cost.
        Stopwatch sw = Stopwatch.StartNew();
        BinaryTriangleBvh binary = BinaryTriangleBvh.Build(bake.Vertices, bake.TriangleCount);
        double binaryColdMs = sw.Elapsed.TotalMilliseconds;
        sw.Restart();
        TriangleBvh wide = TriangleBvh.Build(bake.Vertices, bake.TriangleCount);
        double wideColdMs = sw.Elapsed.TotalMilliseconds;
        sw.Restart();
        _ = BinaryTriangleBvh.Build(bake.Vertices, bake.TriangleCount);
        double binaryWarmMs = sw.Elapsed.TotalMilliseconds;
        sw.Restart();
        _ = TriangleBvh.Build(bake.Vertices, bake.TriangleCount);
        double wideWarmMs = sw.Elapsed.TotalMilliseconds;
        Console.WriteLine($"build: binary {binaryColdMs:F0} ms cold, {binaryWarmMs:F0} ms warm; "
                          + $"wide {wideColdMs:F0} ms cold, {wideWarmMs:F0} ms warm "
                          + $"({wide.NodeCount} nodes, depth {wide.Depth}, stack {wide.StackCapacity})");

        Corpus corpus = Corpus.Generate(rays, seed, wide.Min, wide.Max, minLen, maxLen);
        Console.WriteLine($"corpus: {rays} rays, seed {seed}, length {minLen:F0}..{maxLen:F0}, {rounds} rounds, default tier {TriangleBvh.DefaultTier}");

        // The scalar tier once, untimed, for the triangle each occluded ray would carry into its
        // next sample as the hint: the input of the hinted arms.
        int[] hintOf = new int[rays];
        List<int> occludedRays = [];
        for (int i = 0; i < rays; i++)
        {
            int hint = -1;
            if (wide.AnyHit(corpus.Origin(i), corpus.Direction(i), corpus.Length[i], Eps, ref hint, out _, TraversalTier.Scalar))
            {
                occludedRays.Add(i);
            }

            hintOf[i] = hint;
        }

        int[] hinted = occludedRays.ToArray();
        Console.WriteLine($"hinted arms: {hinted.Length} occluded rays, each hinted with the triangle the scalar tier found");

        List<Arm> arms =
        [
            new Arm("binary", 1, rays,
                (lo, hi, out o) => RunBinary(binary, corpus, lo, hi, out o),
                (lo, hi, out n, out t, out s) => CountBinary(binary, corpus, lo, hi, out n, out t, out s))
        ];
        foreach (TraversalTier tier in TriangleBvh.AvailableTiers)
        {
            TraversalTier captured = tier;
            arms.Add(new Arm($"wide/{tier}", TriangleBvh.Width, rays,
                (lo, hi, out o) => RunWide(wide, captured, corpus, lo, hi, out o),
                (lo, hi, out n, out t, out s) => CountWide(wide, captured, corpus, lo, hi, out n, out t, out s)));
        }

        foreach (TraversalTier tier in TriangleBvh.AvailableTiers)
        {
            TraversalTier captured = tier;
            arms.Add(new Arm($"wide/{tier}/hint", TriangleBvh.Width, hinted.Length,
                (lo, hi, out o) => RunHinted(wide, captured, corpus, hinted, hintOf, lo, hi, out o),
                (lo, hi, out n, out t, out s) => CountHinted(wide, captured, corpus, hinted, hintOf, lo, hi, out n, out t, out s)));
        }

        // Warm every arm on the same slice so none pays JIT or a cold cache in its timed passes,
        // then take each arm's work counts once, untimed.
        foreach (Arm arm in arms)
        {
            _ = arm.Timed(0, Math.Min(arm.Count, 20_000), out _);
            arm.Counting(0, arm.Count, out long nodes, out long triangles, out long shortCircuits);
            arm.Nodes = nodes;
            arm.Triangles = triangles;
            arm.ShortCircuits = shortCircuits;
        }

        Console.WriteLine();
        Console.WriteLine($"{"tree",-22} {"threads",7} {"rays/s",12} {"ns/ray",8} {"occluded",9} {"nodes/ray",10} {"boxes/ray",10} {"tris/ray",9} {"hinted",7}  rounds (ns/ray)");

        Measure(arms, 1, rounds);
        if (threads > 1)
        {
            Measure(arms, threads, rounds);
        }

        Console.WriteLine();
        int[] binaryVerdicts = arms[0].Verdicts!;
        int[] scalarVerdicts = arms[1].Verdicts!;
        Console.WriteLine($"verdicts: {arms[1].Label} differs from binary on {Disagreements(binaryVerdicts, scalarVerdicts)} of {rays} rays "
                          + "(adjudicated per ray by the test suite's differential harness, not here)");
        for (int k = 2; k < arms.Count; k++)
        {
            Arm arm = arms[k];
            if (arm.Count == rays)
            {
                Console.WriteLine($"verdicts: {arm.Label} differs from {arms[1].Label} on {Disagreements(scalarVerdicts, arm.Verdicts!)} of {rays} rays "
                                  + "(must be 0: the tiers are one traversal; TraversalTierTests holds them to bit identity on hint and distance as well)");
            }
            else
            {
                int notOccluded = arm.Verdicts!.Count(v => v == 0);
                Console.WriteLine($"hinted: {arm.Label} short-circuited {arm.ShortCircuits} of {arm.Count} rays on their own triangle, "
                                  + $"{notOccluded} came back clear (both must be exact: a traversal true is a hinted true)");
            }
        }

        return 0;
    }

    private delegate int[] TimedPass(int lo, int hi, out long occluded);

    private delegate void CountPass(int lo, int hi, out long nodes, out long triangles, out long shortCircuits);

    private static int Disagreements(int[] reference, int[] other)
    {
        int disagree = 0;
        for (int i = 0; i < reference.Length; i++)
        {
            if (reference[i] != other[i])
            {
                disagree++;
            }
        }

        return disagree;
    }

    // Runs every arm over its rays for the given number of rounds, interleaved with the order
    // flipped each round, and prints one row per arm with the median round.
    private static void Measure(List<Arm> arms, int threads, int rounds)
    {
        double[][] seconds = new double[arms.Count][];
        for (int k = 0; k < arms.Count; k++)
        {
            seconds[k] = new double[rounds];
        }

        for (int round = 0; round < rounds; round++)
        {
            for (int step = 0; step < arms.Count; step++)
            {
                int k = round % 2 == 0 ? step : arms.Count - 1 - step;
                seconds[k][round] = Time(arms[k], threads);
            }
        }

        for (int k = 0; k < arms.Count; k++)
        {
            Arm arm = arms[k];
            int rays = arm.Count;
            double[] sorted = (double[])seconds[k].Clone();
            Array.Sort(sorted);
            double median = sorted.Length % 2 == 1 ? sorted[sorted.Length / 2] : 0.5 * (sorted[(sorted.Length / 2) - 1] + sorted[sorted.Length / 2]);
            string perRound = string.Join(" ", seconds[k].Select(s => $"{s * 1e9 / rays:F0}"));
            string hintedShare = k == 0 ? "-" : $"{arm.ShortCircuits / (double)rays:P1}";
            Console.WriteLine(
                $"{arm.Label,-22} {threads,7} {rays / median,12:F0} {median * 1e9 / rays,8:F0} {arm.Occluded / (double)rays,9:P1} "
                + $"{arm.Nodes / (double)rays,10:F2} {arm.Nodes * arm.BoxesPerNode / (double)rays,10:F2} {arm.Triangles / (double)rays,9:F2} {hintedShare,7}  {perRound}");
        }
    }

    // One timed pass of one arm, in one thread or in `threads` chunks. Records the verdicts on the
    // arm (identical every round) and returns the wall time.
    private static double Time(Arm arm, int threads)
    {
        int rays = arm.Count;
        int[] verdicts = new int[rays];
        long occluded = 0;
        Stopwatch sw = Stopwatch.StartNew();
        if (threads == 1)
        {
            verdicts = arm.Timed(0, rays, out occluded);
        }
        else
        {
            int chunk = (rays + threads - 1) / threads;
            object gate = new();
            Parallel.For(0, threads, new ParallelOptions { MaxDegreeOfParallelism = threads }, k =>
            {
                int lo = k * chunk, hi = Math.Min(rays, lo + chunk);
                if (lo >= hi)
                {
                    return;
                }

                int[] part = arm.Timed(lo, hi, out long o);
                lock (gate)
                {
                    Array.Copy(part, 0, verdicts, lo, hi - lo);
                    occluded += o;
                }
            });
        }

        double seconds = sw.Elapsed.TotalSeconds;
        arm.Verdicts = verdicts;
        arm.Occluded = occluded;
        return seconds;
    }

    private static int[] RunBinary(BinaryTriangleBvh bvh, Corpus c, int lo, int hi, out long occluded)
    {
        int[] verdicts = new int[hi - lo];
        occluded = 0;
        for (int i = lo; i < hi; i++)
        {
            bool hit = bvh.AnyHit(c.Origin(i), c.Direction(i), c.Length[i], Eps);
            verdicts[i - lo] = hit ? 1 : 0;
            if (hit)
            {
                occluded++;
            }
        }

        return verdicts;
    }

    private static void CountBinary(BinaryTriangleBvh bvh, Corpus c, int lo, int hi, out long nodes, out long triangles, out long shortCircuits)
    {
        nodes = 0;
        triangles = 0;
        shortCircuits = 0;
        for (int i = lo; i < hi; i++)
        {
            _ = bvh.AnyHitCounted(c.Origin(i), c.Direction(i), c.Length[i], Eps, ref nodes, ref triangles);
        }
    }

    // The production body: the named-tier overload with no work counters compiled in.
    private static int[] RunWide(TriangleBvh bvh, TraversalTier tier, Corpus c, int lo, int hi, out long occluded)
    {
        int[] verdicts = new int[hi - lo];
        occluded = 0;
        for (int i = lo; i < hi; i++)
        {
            int hint = -1;
            bool hit = bvh.AnyHit(c.Origin(i), c.Direction(i), c.Length[i], Eps, ref hint, out _, tier);
            verdicts[i - lo] = hit ? 1 : 0;
            if (hit)
            {
                occluded++;
            }
        }

        return verdicts;
    }

    private static void CountWide(TriangleBvh bvh, TraversalTier tier, Corpus c, int lo, int hi, out long nodes, out long triangles, out long shortCircuits)
    {
        TraversalCounters counters = default;
        shortCircuits = 0;
        for (int i = lo; i < hi; i++)
        {
            int hint = -1;
            _ = bvh.AnyHit(c.Origin(i), c.Direction(i), c.Length[i], Eps, ref hint, out bool shortCircuited, ref counters, tier);
            shortCircuits += shortCircuited ? 1 : 0;
        }

        nodes = counters.Nodes;
        triangles = counters.Triangles;
    }

    // The occluded subset, each ray hinted with its own blocking triangle. Indices lo..hi are into
    // the subset; the hint is copied per ray so an arm cannot see another arm's write-back.
    private static int[] RunHinted(TriangleBvh bvh, TraversalTier tier, Corpus c, int[] subset, int[] hintOf, int lo, int hi, out long occluded)
    {
        int[] verdicts = new int[hi - lo];
        occluded = 0;
        for (int j = lo; j < hi; j++)
        {
            int i = subset[j];
            int hint = hintOf[i];
            bool hit = bvh.AnyHit(c.Origin(i), c.Direction(i), c.Length[i], Eps, ref hint, out _, tier);
            verdicts[j - lo] = hit ? 1 : 0;
            if (hit)
            {
                occluded++;
            }
        }

        return verdicts;
    }

    private static void CountHinted(TriangleBvh bvh, TraversalTier tier, Corpus c, int[] subset, int[] hintOf, int lo, int hi, out long nodes, out long triangles, out long shortCircuits)
    {
        TraversalCounters counters = default;
        shortCircuits = 0;
        for (int j = lo; j < hi; j++)
        {
            int i = subset[j];
            int hint = hintOf[i];
            _ = bvh.AnyHit(c.Origin(i), c.Direction(i), c.Length[i], Eps, ref hint, out bool shortCircuited, ref counters, tier);
            shortCircuits += shortCircuited ? 1 : 0;
        }

        nodes = counters.Nodes;
        triangles = counters.Triangles;
    }

    /// <summary>One arm: a label, its box tests per node, its ray count, the timed pass and the untimed counting pass.</summary>
    private sealed class Arm(string label, int boxesPerNode, int count, TimedPass timed, CountPass counting)
    {
        public string Label { get; } = label;
        public int BoxesPerNode { get; } = boxesPerNode;
        public int Count { get; } = count;
        public TimedPass Timed { get; } = timed;
        public CountPass Counting { get; } = counting;
        public int[]? Verdicts { get; set; }
        public long Occluded { get; set; }
        public long Nodes { get; set; }
        public long Triangles { get; set; }
        public long ShortCircuits { get; set; }
    }

    /// <summary>A seeded ray corpus in structure-of-arrays form.</summary>
    private sealed class Corpus
    {
        private readonly float[] _dx;
        private readonly float[] _dy;
        private readonly float[] _dz;
        private readonly float[] _ox;
        private readonly float[] _oy;
        private readonly float[] _oz;

        private Corpus(int count)
        {
            Count = count;
            _ox = new float[count];
            _oy = new float[count];
            _oz = new float[count];
            _dx = new float[count];
            _dy = new float[count];
            _dz = new float[count];
            Length = new float[count];
        }

        public int Count { get; }
        public float[] Length { get; }

        public Vector3 Origin(int i) => new(_ox[i], _oy[i], _oz[i]);
        public Vector3 Direction(int i) => new(_dx[i], _dy[i], _dz[i]);

        public static Corpus Generate(int count, int seed, Vector3 lo, Vector3 hi, float minLen, float maxLen)
        {
            Random rng = new(seed);
            Corpus c = new(count);
            for (int i = 0; i < count; i++)
            {
                c._ox[i] = lo.X + ((hi.X - lo.X) * (float)rng.NextDouble());
                c._oy[i] = lo.Y + ((hi.Y - lo.Y) * (float)rng.NextDouble());
                c._oz[i] = lo.Z + ((hi.Z - lo.Z) * (float)rng.NextDouble());

                // Uniform on the sphere: z uniform in [-1, 1], azimuth uniform.
                double z = (rng.NextDouble() * 2.0) - 1.0;
                double phi = rng.NextDouble() * 2.0 * Math.PI;
                double r = Math.Sqrt(1.0 - (z * z));
                Vector3 d = Vector3.Normalize(new Vector3((float)(r * Math.Cos(phi)), (float)(r * Math.Sin(phi)), (float)z));
                c._dx[i] = d.X;
                c._dy[i] = d.Y;
                c._dz[i] = d.Z;
                c.Length[i] = minLen + ((maxLen - minLen) * (float)rng.NextDouble());
            }

            return c;
        }
    }
}
