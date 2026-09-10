#region

using System.Numerics;
using CS2DemoKit.Analysis.Visibility;
using CS2DemoKit.Parser;

#endregion

namespace CS2DemoKit.Analysis.Tests;

/// <summary>
///     The last-occluder hint over real geometry: hinted and plain traversals must agree on every
///     ray, and every hint written after an occluded ray must name a triangle the brute-force
///     oracle confirms blocks it. Two corpora: random and walked sightlines over a bake, and the
///     actual in-frustum sightlines the transition scanner casts on the sample demo, keyed the way
///     the scanner keys its hints. The second one also measures the hit rate on real play, which is
///     the number that decides whether a batched ray precompute is worth building at all.
/// </summary>
[Category("RealAsset")]
public class OccluderHintDifferentialTests
{
    private const int Slots = VisibilityTransitionScanner.MaxSlots;

    /// <param name="map">Map whose <c>collision.tris</c> to load.</param>
    [Test]
    [Arguments("de_nuke")]
    [Arguments("de_dust2")]
    public async Task RandomAndWalkedRays_HintsDivergeNowhere(string map)
    {
        CollisionTris.Data bake = CollisionTris.Load(VisibilityReplay.RequireBakePath(map));
        TriangleBvh bvh = TriangleBvh.Build(bake.Vertices, bake.TriangleCount);
        Vector3 lo = bvh.Min, hi = bvh.Max;
        Random rng = new(20260914);

        List<(Vector3 A, Vector3 B, int Key)> corpus = new(20_000 + (400 * 40));
        for (int i = 0; i < 20_000; i++)
        {
            // One shared key: every one of these rays sees a hint left by an unrelated ray.
            corpus.Add((RandomPoint(rng, lo, hi), RandomPoint(rng, lo, hi), 0));
        }

        for (int walk = 0; walk < 400; walk++)
        {
            Vector3 a = RandomPoint(rng, lo, hi);
            Vector3 b = a + new Vector3(
                (float)(rng.NextDouble() * 1600.0 - 800.0),
                (float)(rng.NextDouble() * 1600.0 - 800.0),
                (float)(rng.NextDouble() * 200.0 - 100.0));
            for (int step = 0; step < 40; step++)
            {
                a += Step(rng);
                b += Step(rng);
                corpus.Add((a, b, 1 + walk));
            }
        }

        RayDifferentialHarness.HintResult result =
            RayDifferentialHarness.RunWithHints(bake.Vertices, bake.TriangleCount, corpus);
        Console.WriteLine($"[occluder-hint] {map}: {result.Describe()}");

        await Assert.That(result.Divergences.Count).IsEqualTo(0)
            .Because("hinted and plain traversals must give the same verdict on every ray. " + result.Describe());
        await Assert.That(result.BadHints).IsEqualTo(0)
            .Because("every hint written after an occluded ray must be a triangle that blocks it");
        await Assert.That(result.Occluded).IsGreaterThan(5_000);
        await Assert.That(result.RayCount - result.Occluded).IsGreaterThan(1_000);
        await Assert.That(result.ShortCircuited).IsGreaterThan(1_000)
            .Because("walked sightlines on a real bake must reuse their occluder");
    }

    /// <summary>
    ///     The scanner's own rays on the sample demo: for every frame, every directed enemy pair,
    ///     every anchor inside the viewer's frustum and not behind smoke, one segment keyed by
    ///     (viewer, target, anchor) exactly as <see cref="OccluderHintTable" /> keys it. This is a
    ///     superset of what the scanner casts (it does not stop at the first clear anchor), so a
    ///     divergence anywhere in it is a divergence the golden could see. Because of that, the hint
    ///     each key carries from one frame to the next is not always the one the live scanner would
    ///     carry; the live sequence is covered by <c>VisibilityTraceGoldenTests</c> (fixture
    ///     identical, hit rate printed) and <c>VisibilityTransitionScannerParityTests</c> (the real
    ///     scanner, hints and all, against a hint-free reference).
    /// </summary>
    [Test]
    public async Task SampleDemoSightlines_HintsDivergeNowhere_AndMostRaysShortCircuit()
    {
        ParsedDemo demo = VisibilityReplay.RequireSampleDemo();
        await Assert.That(demo.Frames.Count).IsEqualTo(VisibilityReplay.SampleDemoFrameCount);
        CollisionTris.Data bake = CollisionTris.Load(VisibilityReplay.RequireBakePath(VisibilityReplay.SampleDemoMap));

        List<(Vector3 A, Vector3 B, int Key)> corpus = new(1 << 20);
        Vector3[] anchors = new Vector3[PlayerVantage.MaxAnchors];
        int frames = 0;
        VisibilityReplay.ForEachFrame(demo.Frames, (_, vantages, smokes) =>
        {
            frames++;
            for (int v = 0; v < vantages.Count; v++)
            {
                VisibilityAnalyzer.Vantage viewer = vantages[v].Vantage;
                ViewFrustum frustum = new(viewer.Eye, viewer.Forward, 53f, 37f);
                for (int t = 0; t < vantages.Count; t++)
                {
                    VisibilityAnalyzer.Vantage target = vantages[t].Vantage;
                    if (viewer.Slot == target.Slot || !VisibilityAnalyzer.AreEnemies(viewer, target)
                        || (uint)viewer.Slot >= Slots || (uint)target.Slot >= Slots)
                    {
                        continue;
                    }

                    int n = PlayerVantage.BuildAnchors(target.Feet, target.Duck, viewer.Eye, anchors);
                    for (int i = 0; i < n; i++)
                    {
                        if (!frustum.Contains(anchors[i])
                            || SmokeVolumes.SegmentBlocked(viewer.Eye, anchors[i], smokes.ToArray()))
                        {
                            continue;
                        }

                        int key = (((viewer.Slot * Slots) + target.Slot) * PlayerVantage.MaxAnchors) + i;
                        corpus.Add((viewer.Eye, anchors[i], key));
                    }
                }
            }
        });

        RayDifferentialHarness.HintResult result =
            RayDifferentialHarness.RunWithHints(bake.Vertices, bake.TriangleCount, corpus);
        Console.WriteLine($"[occluder-hint] sample demo ({frames} frames): {result.Describe()}");

        await Assert.That(frames).IsEqualTo(VisibilityReplay.SampleDemoFrameCount);
        await Assert.That(result.RayCount).IsGreaterThan(100_000).Because("four rounds of play cast far more than this");
        await Assert.That(result.Divergences.Count).IsEqualTo(0)
            .Because("the scanner's own rays must not change verdict under hints. " + result.Describe());
        await Assert.That(result.BadHints).IsEqualTo(0);
        // Consecutive samples of a sightline share an occluder on real geometry; this floor is well
        // under the measured rate (see the PR for the number) and well above what a hint that only
        // worked by accident could reach.
        await Assert.That(result.HitRate).IsGreaterThan(0.5)
            .Because("the whole point of the hint is that consecutive samples reuse an occluder");
    }

    private static Vector3 Step(Random rng) => new(
        (float)(rng.NextDouble() * 12.0 - 6.0),
        (float)(rng.NextDouble() * 12.0 - 6.0),
        (float)(rng.NextDouble() * 2.0 - 1.0));

    private static Vector3 RandomPoint(Random rng, Vector3 lo, Vector3 hi) => new(
        lo.X + (hi.X - lo.X) * (float)rng.NextDouble(),
        lo.Y + (hi.Y - lo.Y) * (float)rng.NextDouble(),
        lo.Z + (hi.Z - lo.Z) * (float)rng.NextDouble());
}
