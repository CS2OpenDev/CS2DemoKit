#region

using System.Globalization;
using System.Numerics;
using CS2DemoKit.Analysis.Visibility;

#endregion

namespace CS2DemoKit.Analysis.Tests;

/// <summary>
///     The eight-wide tree against the frozen binary tree over real bakes, every divergent ray
///     adjudicated by the brute-force oracle and counted by class.
///     <para>
///         A different tree has different node boxes, so it loses a different set of rays to the
///         float boundary class: a triangle hit landing within an ulp or two of a box edge, or of
///         the segment window, so that the slab crossing and the triangle test disagree about which
///         side it is on. Each one is named, traced to the lane box that rejected it, re-tested
///         there in double precision and reported; none is waved through. The frozen tree's own
///         losses (the NaN class the slab fix addressed, and its own boundary rays) show up as rays
///         where the live tree is the one the oracle backs.
///     </para>
///     <para>
///         Being explicable is not the same as being free, so neither arm passes on the strength of
///         the classifier alone. <c>AnyHit</c> holds the whole segment window and loses nothing at
///         all on the three shipped bakes — 0 of 78,000 on each — so it is held to zero live-wrong
///         rays outright, the same bar <c>SlabDegeneracyTests</c> uses: the first boundary ray the
///         wide tree ever loses on this corpus is a person's decision, not a counter's.
///         <c>NearestHit</c> shrinks its window as it goes and does lose a handful (2 of 31,000 on
///         de_nuke, 7 on de_dust2), so there it is a rate that is bounded rather than a count of
///         zero. Either way a live-wrong ray whose path no float-rejected box explains, or whose
///         rejected box the ray truly misses by more than rounding, is a defect and fails the test.
///     </para>
///     <para>
///         Counts are printed per bake. Corpora: random segments across the map, walked sightlines,
///         and the same-floor grid with and without tilt, all seeded.
///     </para>
/// </summary>
[Category("RealAsset")]
public class WideBvhDifferentialTests
{
    private const float NearEps = 1e-3f;

    /// <param name="map">Map whose <c>collision.tris</c> to load.</param>
    [Test]
    [Arguments("de_nuke")]
    [Arguments("de_dust2")]
    [Arguments("de_ancient")]
    public async Task AnyHit_EveryDivergenceFromTheFrozenTree_IsAdjudicatedAndCounted(string map)
    {
        CollisionTris.Data bake = CollisionTris.Load(VisibilityReplay.RequireBakePath(map));
        TriangleBvh bvh = TriangleBvh.Build(bake.Vertices, bake.TriangleCount);
        Vector3 lo = bvh.Min, hi = bvh.Max;

        List<(Vector3 A, Vector3 B)> corpus = RayCorpora.Random(lo, hi, 50_000, 20260918);
        corpus.AddRange(RayCorpora.Walked(lo, hi, 400, 40, 20260919));
        corpus.AddRange(RayCorpora.SameFloor(lo, hi, 0f, 20260910));
        corpus.AddRange(RayCorpora.SameFloor(lo, hi, 0.5f, 20260910));

        RayDifferentialHarness.Result result = RayDifferentialHarness.Run(bake.Vertices, bake.TriangleCount, corpus);
        Console.WriteLine($"[bvh8-differential] {map} AnyHit: {result.Describe(10)}");

        await Assert.That(result.RayCount).IsGreaterThan(70_000);
        await Assert.That(result.CurrentWrongOutsideBoundary.Count).IsEqualTo(0)
            .Because("a live verdict the oracle rejects and no box edge explains is a defect. " + result.Describe(10));
        await Assert.That(result.CurrentWrong.Count).IsEqualTo(0)
            .Because("the boundary class is a class of ray that gets explained, not a budget that gets "
                     + "spent: the wide tree loses none of these on any shipped bake, so a new one is a "
                     + "behaviour change to read rather than a count to tolerate. " + result.Describe(10));
    }

    /// <summary>
    ///     The same for <c>NearestHit</c>: verdict and distance, bit for bit, against the frozen
    ///     tree, with every difference adjudicated against the oracle's nearest triangle.
    /// </summary>
    /// <param name="map">Map whose <c>collision.tris</c> to load.</param>
    [Test]
    [Arguments("de_nuke")]
    [Arguments("de_dust2")]
    public async Task NearestHit_EveryDivergenceFromTheFrozenTree_IsAdjudicatedAndCounted(string map)
    {
        CollisionTris.Data bake = CollisionTris.Load(VisibilityReplay.RequireBakePath(map));
        LegacyTriangleBvh legacy = LegacyTriangleBvh.Build(bake.Vertices, bake.TriangleCount);
        TriangleBvh bvh = TriangleBvh.Build(bake.Vertices, bake.TriangleCount);
        Vector3 lo = bvh.Min, hi = bvh.Max;

        List<(Vector3 A, Vector3 B)> corpus = RayCorpora.Random(lo, hi, 20_000, 20260920);
        corpus.AddRange(RayCorpora.SameFloor(lo, hi, 0f, 20260910));
        // Straight down from random points, the ray the frame gate actually casts.
        Random rng = new(20260921);
        for (int i = 0; i < 5000; i++)
        {
            Vector3 a = new(
                lo.X + ((hi.X - lo.X) * (float)rng.NextDouble()),
                lo.Y + ((hi.Y - lo.Y) * (float)rng.NextDouble()),
                lo.Z + ((hi.Z - lo.Z) * (float)rng.NextDouble()));
            corpus.Add((a, a - new Vector3(0f, 0f, 512f)));
        }

        int rays = 0, divergent = 0, legacyWrong = 0, currentWrong = 0, boundary = 0, unexplained = 0, hits = 0;
        List<string> unexplainedRays = [];
        List<string> boundaryRays = [];
        int[] parents = RayDifferentialHarness.ParentLanes(bvh);
        foreach ((Vector3 a, Vector3 b) in corpus)
        {
            Vector3 delta = b - a;
            float len = delta.Length();
            if (len <= 2f * NearEps)
            {
                continue;
            }

            rays++;
            Vector3 dir = delta / len;
            bool legacyHit = legacy.NearestHit(a, dir, len, NearEps, out float legacyT);
            bool currentHit = bvh.NearestHit(a, dir, len, NearEps, out float currentT);
            hits += currentHit ? 1 : 0;
            if (legacyHit == currentHit && BitConverter.SingleToInt32Bits(legacyT) == BitConverter.SingleToInt32Bits(currentT))
            {
                continue;
            }

            divergent++;
            bool oracleHit = BruteForceOracle.NearestHit(bake.Vertices, bake.TriangleCount, a, dir, len, NearEps, out float oracleT, out int oracleTriangle);
            bool legacyRight = legacyHit == oracleHit && BitConverter.SingleToInt32Bits(legacyT) == BitConverter.SingleToInt32Bits(oracleT);
            bool currentRight = currentHit == oracleHit && BitConverter.SingleToInt32Bits(currentT) == BitConverter.SingleToInt32Bits(oracleT);
            legacyWrong += legacyRight ? 0 : 1;
            if (currentRight)
            {
                continue;
            }

            currentWrong++;
            // The window the traversal ends with: the near exclusion up to the distance it settled
            // on, or the far limit. That is the tightest window any lane on the path was held to,
            // so a lane that fails it here may be one the traversal admitted earlier, when its
            // window was still wider, with the lane that really stopped it deeper on the path. The
            // named lane can therefore be an ancestor of the true one; the margin class cannot be
            // wrong for it, because an ancestor's box contains the descendant's and a float
            // rejection of a box double admits by more than rounding is a defect at any depth.
            RayDifferentialHarness.LossClassification loss = oracleHit
                ? RayDifferentialHarness.Classify(bvh, parents, oracleTriangle, a, dir, NearEps, currentHit ? currentT : len)
                : new RayDifferentialHarness.LossClassification(-1, double.NaN, 0);
            string line = $"origin=({a.X:R},{a.Y:R},{a.Z:R}) dir=({dir.X:R},{dir.Y:R},{dir.Z:R}) len={len:R} "
                          + $"legacy={(legacyHit ? legacyT.ToString("R", CultureInfo.InvariantCulture) : "miss")} current={(currentHit ? currentT.ToString("R", CultureInfo.InvariantCulture) : "miss")} "
                          + $"oracle={(oracleHit ? oracleT.ToString("R", CultureInfo.InvariantCulture) : "miss")} tri={oracleTriangle} {loss}";
            if (loss.IsRounding)
            {
                boundary++;
                boundaryRays.Add(line);
            }
            else
            {
                unexplained++;
                unexplainedRays.Add(line);
            }
        }

        Console.WriteLine($"[bvh8-differential] {map} NearestHit: {rays} rays, {hits} hits, {divergent} divergent, "
                          + $"{legacyWrong} where legacy is wrong, {currentWrong} where current is wrong "
                          + $"({boundary} boundary class, {unexplained} unexplained)");
        foreach (string line in boundaryRays.Concat(unexplainedRays))
        {
            Console.WriteLine($"[bvh8-differential] {map} NearestHit: {line}");
        }

        await Assert.That(hits).IsGreaterThan(rays / 2);
        // Bounded, not merely classified. The shrinking window means this arm does lose a few rays at
        // box edges where AnyHit loses none (2 in 31,000 on de_nuke, 7 on de_dust2), so zero is not
        // the right bar here; a tenth of a percent is more than an order of magnitude above what
        // either bake produces and still small enough that a traversal quietly going wrong on
        // thousands of rays cannot hide inside it.
        await Assert.That(boundary * 1000).IsLessThan(rays)
            .Because("rays explained by a box edge must stay a handful in tens of thousands: "
                     + string.Join(Environment.NewLine, boundaryRays.Take(10)));
        await Assert.That(unexplained).IsEqualTo(0)
            .Because("a nearest distance the oracle rejects and no box edge explains is a defect: "
                     + string.Join(Environment.NewLine, unexplainedRays.Take(10)));
    }
}
