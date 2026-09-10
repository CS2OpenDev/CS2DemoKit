#region

using System.Numerics;
using CS2DemoKit.Analysis.Visibility;

#endregion

namespace CS2DemoKit.Analysis.Tests;

/// <summary>
///     Pins the last-occluder hint to its contract: it may only ever save work, never change an
///     answer, where the answer is the plain traversal's. A hint that names a triangle which does
///     not block the ray must fall through to the traversal; a stale hint must not stop a different
///     occluder from being found; a hinted triangle hit outside the segment's
///     <c>(eps, tMax - eps)</c> window must not count; a hinted triangle the traversal would not
///     have reached must not count either; and a repeated occluded ray must be decided by the hint
///     without a traversal.
///     <para>
///         The geometry is three parallel walls at known X so every case is about a named
///         triangle. The random-soup test at the end covers the shape of the argument in general:
///         the hinted and plain traversals agree on every ray, and every triangle the hint names
///         after an occluded ray is one the brute-force oracle confirms blocks that ray.
///     </para>
/// </summary>
[Category("Unit")]
public class OccluderHintTests
{
    private const float Eps = 0.1f;
    private const int WallA = 0; // X = 100
    private const int WallB = 1; // X = 300
    private const int WallC = 2; // X = -500, behind every ray that starts at the origin

    private static float[] Wall(float x) => [x, -1000f, -100f, x, 1000f, -100f, x, 0f, 1000f];

    private static float[] ThreeWalls() => [.. Wall(100f), .. Wall(300f), .. Wall(-500f)];

    // Two triangles covering the rectangle [y0, y1] x [z0, z1] in the plane X = x.
    private static float[] Quad(float x, float y0, float y1, float z0, float z1) =>
    [
        x, y0, z0, x, y1, z0, x, y1, z1,
        x, y0, z0, x, y1, z1, x, y0, z1
    ];

    // A ray at eye height along X from x0 to x1.
    private static (Vector3 Origin, Vector3 Dir, float Len) Ray(float x0, float x1)
    {
        Vector3 a = new(x0, 0f, 64f);
        Vector3 b = new(x1, 0f, 64f);
        Vector3 d = b - a;
        float len = d.Length();
        return (a, d / len, len);
    }

    [Test]
    public async Task HintAtTriangleThatDoesNotBlock_FallsThroughToTraversal()
    {
        TriangleBvh bvh = TriangleBvh.Build(ThreeWalls(), 3);
        (Vector3 o, Vector3 d, float len) = Ray(0f, 200f);

        // Wall B is beyond the segment's end: the hinted triangle is real, on the ray's line, and
        // does not block this segment. The traversal has to run and find wall A.
        int hint = WallB;
        bool occluded = bvh.AnyHit(o, d, len, Eps, ref hint, out bool shortCircuited);
        await Assert.That(occluded).IsTrue().Because("wall A sits at X=100, inside the segment");
        await Assert.That(shortCircuited).IsFalse().Because("wall B does not block, so the hint must not decide");
        await Assert.That(hint).IsEqualTo(WallA).Because("the blocking triangle is written back");

        // Wall C is behind the origin: Moller-Trumbore reports the plane hit at t = -500, which
        // the window rejects. A hinted test that dropped the (lo, hi) predicate would say occluded
        // for the wrong reason here; the traversal must still be the thing that finds A.
        hint = WallC;
        occluded = bvh.AnyHit(o, d, len, Eps, ref hint, out shortCircuited);
        await Assert.That(occluded).IsTrue();
        await Assert.That(shortCircuited).IsFalse();
        await Assert.That(hint).IsEqualTo(WallA);
    }

    [Test]
    public async Task HintAtTriangleHitOutsideTheWindow_DoesNotShortCircuit()
    {
        TriangleBvh bvh = TriangleBvh.Build(ThreeWalls(), 3);

        // Segment ends before wall A: the hinted triangle is on the ray's line at t = 100 but the
        // window is (0.1, 49.9). Clear, hinted or not, and the hint is left alone on a clear ray.
        (Vector3 o, Vector3 d, float len) = Ray(0f, 50f);
        int hint = WallA;
        bool occluded = bvh.AnyHit(o, d, len, Eps, ref hint, out bool shortCircuited);
        await Assert.That(occluded).IsFalse();
        await Assert.That(shortCircuited).IsFalse();
        await Assert.That(hint).IsEqualTo(WallA).Because("a clear ray leaves the hint as it was");
        await Assert.That(bvh.AnyHit(o, d, len, Eps)).IsFalse();

        // Segment ends 0.05 units past wall A, inside the endpoint exclusion: t = 100 is not
        // below hi = 99.95, so the wall is excluded by the same predicate on both paths. This is
        // the case a hinted test with its own epsilon would get wrong, and it is the ONLY test that
        // does: the random corpora never land a triangle within eps of an endpoint, so a hinted
        // test on (0, tMax) instead of (eps, tMax - eps) passes everything else. Do not weaken it.
        (o, d, len) = Ray(0f, 100.05f);
        hint = WallA;
        occluded = bvh.AnyHit(o, d, len, Eps, ref hint, out shortCircuited);
        await Assert.That(bvh.AnyHit(o, d, len, Eps)).IsFalse().Because("the plain traversal excludes the endpoint");
        await Assert.That(occluded).IsFalse().Because("the hinted test must apply the identical window");
        await Assert.That(shortCircuited).IsFalse();
    }

    /// <summary>
    ///     The traversal can miss a triangle the ray really hits. The slab test's crossing time for
    ///     a node face and Moller-Trumbore's hit time for a triangle on that face come from
    ///     different float arithmetic and land a few ulp apart, so a segment whose far limit falls
    ///     between the two has the node rejected while the triangle test alone would say hit. That
    ///     is a property of the traversal and it is the verdict every fixture pins. A hint that
    ///     tested only the triangle would say occluded on such a ray: the oracle's answer, but not
    ///     the traversal's, and the answer would then depend on what the hint happened to hold. So
    ///     the hinted test also runs the leaf's slab test and takes the traversal whenever the
    ///     traversal would not have reached the triangle.
    ///     <para>
    ///         The ray is found by search rather than hardcoded: oblique segments ending on the wall,
    ///         with the far limit moved a few ulp either way until the plain traversal says clear
    ///         and the oracle says occluded. On this traversal that takes a few hundred tries; if
    ///         it cannot be found at all, the slab arithmetic changed and the identity argument in
    ///         <c>TriangleBvh.AnyHit</c> has to be re-derived before this test is touched. The
    ///         mesh puts the wall in its own leaf under a two-leaf root so the case runs through an
    ///         internal node as well as the leaf.
    ///     </para>
    /// </summary>
    [Test]
    public async Task HintOnATriangleTheTraversalWouldMiss_TakesTheTraversal()
    {
        // Four triangles in the plane X = 100 (one leaf, box flat in X) and four far away at
        // X = 5000 (the other leaf); the median split on X separates them.
        float[] vertices =
        [
            .. Quad(100f, -1000f, 1000f, -100f, 450f), .. Quad(100f, -1000f, 1000f, 450f, 1000f),
            .. Quad(5000f, -1000f, 1000f, -100f, 450f), .. Quad(5000f, -1000f, 1000f, 450f, 1000f)
        ];
        const int triangles = 8;
        TriangleBvh bvh = TriangleBvh.Build(vertices, triangles);
        Random rng = new(20260915);

        int found = 0, tried = 0;
        for (int attempt = 0; attempt < 20_000 && found < 5; attempt++)
        {
            Vector3 o = new(
                (float)(rng.NextDouble() * 80.0 - 40.0),
                (float)(rng.NextDouble() * 400.0 - 200.0),
                (float)(rng.NextDouble() * 200.0 - 50.0));
            Vector3 onWall = new(
                100f,
                (float)(rng.NextDouble() * 400.0 - 200.0),
                (float)(rng.NextDouble() * 200.0 - 50.0));
            Vector3 d = onWall - o;
            float len = d.Length();
            Vector3 dir = d / len;

            // The segment ends eps past the wall, so hi = tMax - eps sits on the crossing; then
            // walk tMax by single ulps to land hi between the two crossing times.
            float centre = len + Eps;
            for (int k = -8; k <= 8; k++)
            {
                float tMax = centre;
                for (int step = 0; step < Math.Abs(k); step++)
                {
                    tMax = k < 0 ? MathF.BitDecrement(tMax) : MathF.BitIncrement(tMax);
                }

                tried++;
                bool plain = bvh.AnyHit(o, dir, tMax, Eps);
                int oracleTriangle = BruteForceOracle.FirstHitTriangle(vertices, triangles, o, dir, tMax, Eps);
                if (plain || oracleTriangle < 0)
                {
                    continue;
                }

                found++;
                int hint = oracleTriangle;
                bool hinted = bvh.AnyHit(o, dir, tMax, Eps, ref hint, out bool shortCircuited);
                await Assert.That(hinted).IsEqualTo(plain)
                    .Because($"o=({o.X:R},{o.Y:R},{o.Z:R}) dir=({dir.X:R},{dir.Y:R},{dir.Z:R}) tMax={tMax:R}: the plain "
                             + $"traversal says clear and the oracle says triangle {oracleTriangle} blocks; the hinted "
                             + "test must give the traversal's answer, not the oracle's");
                await Assert.That(shortCircuited).IsFalse();
                await Assert.That(hint).IsEqualTo(oracleTriangle).Because("a clear ray leaves the hint as it was");
            }
        }

        Console.WriteLine($"[occluder-hint] traversal-miss search: {found} found in {tried} rays");
        await Assert.That(found).IsGreaterThan(0)
            .Because("the search must produce a ray the plain traversal misses and the oracle hits; if this "
                     + "traversal no longer has that class, re-derive the leaf-test identity argument in "
                     + "TriangleBvh.AnyHit before changing this test");
    }

    [Test]
    public async Task StaleHint_DoesNotPreventFindingAnotherOccluder()
    {
        TriangleBvh bvh = TriangleBvh.Build(ThreeWalls(), 3);
        int hint = -1;

        (Vector3 o, Vector3 d, float len) = Ray(0f, 200f);
        await Assert.That(bvh.AnyHit(o, d, len, Eps, ref hint, out bool shortCircuited)).IsTrue();
        await Assert.That(shortCircuited).IsFalse().Because("no hint yet");
        await Assert.That(hint).IsEqualTo(WallA);

        // A different segment, blocked only by wall B; the hint still says A.
        (o, d, len) = Ray(200f, 400f);
        await Assert.That(bvh.AnyHit(o, d, len, Eps, ref hint, out shortCircuited)).IsTrue()
            .Because("wall B blocks this segment and the stale hint must not hide it");
        await Assert.That(shortCircuited).IsFalse();
        await Assert.That(hint).IsEqualTo(WallB);

        // Back to the first segment with the hint now stale the other way.
        (o, d, len) = Ray(0f, 200f);
        await Assert.That(bvh.AnyHit(o, d, len, Eps, ref hint, out shortCircuited)).IsTrue();
        await Assert.That(shortCircuited).IsFalse();
        await Assert.That(hint).IsEqualTo(WallA);

        // And once the hint is right, it decides.
        await Assert.That(bvh.AnyHit(o, d, len, Eps, ref hint, out shortCircuited)).IsTrue();
        await Assert.That(shortCircuited).IsTrue();
        await Assert.That(hint).IsEqualTo(WallA);
    }

    [Test]
    public async Task RepeatedSightline_ShortCircuitsThroughTheEngine()
    {
        VisibilityEngine engine = VisibilityEngine.FromTriangles(ThreeWalls(), 3);
        Vector3 eye = new(0f, 0f, 64f);
        Vector3 anchor = new(200f, 0f, 64f);

        int hint = -1;
        await Assert.That(engine.IsVisible(eye, anchor, ref hint)).IsFalse();
        await Assert.That(hint).IsEqualTo(WallA);
        await Assert.That(engine.IsVisible(eye, anchor, ref hint, out bool shortCircuited)).IsFalse();
        await Assert.That(shortCircuited).IsTrue();
        await Assert.That(engine.IsVisible(eye, anchor)).IsFalse().Because("the plain overload agrees");

        // Coincident endpoints are trivially visible before any triangle is consulted.
        hint = WallA;
        await Assert.That(engine.IsVisible(eye, eye, ref hint, out shortCircuited)).IsTrue();
        await Assert.That(shortCircuited).IsFalse();
    }

    [Test]
    public async Task EmptyMesh_IgnoresTheHint()
    {
        TriangleBvh bvh = TriangleBvh.Build([], 0);
        (Vector3 o, Vector3 d, float len) = Ray(0f, 200f);
        int hint = 5;
        await Assert.That(bvh.AnyHit(o, d, len, Eps, ref hint, out bool shortCircuited)).IsFalse();
        await Assert.That(shortCircuited).IsFalse();
        await Assert.That(hint).IsEqualTo(5);
    }

    [Test]
    public async Task HintSpanShorterThanTheAnchorSet_IsRejected()
    {
        VisibilityEngine engine = VisibilityEngine.FromTriangles(ThreeWalls(), 3);
        VisibilityAnalyzer.Vantage a = Player(0, 2, 0f, 0f);
        VisibilityAnalyzer.Vantage b = Player(1, 3, 500f, 180f);
        int[] tooShort = new int[PlayerVantage.MaxAnchors - 1];

        ArgumentException couldSee = Assert.Throws<ArgumentException>(
            () => _ = VisibilityAnalyzer.CouldSee(engine, a, b, 53f, 37f, ReadOnlySpan<Vector4>.Empty, tooShort));
        ArgumentException pair = Assert.Throws<ArgumentException>(
            () => _ = VisibilityAnalyzer.EvaluatePair(engine, a, b, 53f, 37f, ReadOnlySpan<Vector4>.Empty, tooShort));
        await Assert.That(couldSee.ParamName).IsEqualTo("occluderHints");
        await Assert.That(pair.ParamName).IsEqualTo("occluderHints");
    }

    [Test]
    public async Task HintTable_RowsAreDisjoint_AndOutOfRangeSlotsGetNoHints()
    {
        OccluderHintTable table = new();
        await Assert.That(table.For(0, 1).Length).IsEqualTo(PlayerVantage.MaxAnchors);
        await Assert.That(table.For(63, 63).Length).IsEqualTo(PlayerVantage.MaxAnchors);
        await Assert.That(table.For(64, 0).IsEmpty).IsTrue();
        await Assert.That(table.For(0, 64).IsEmpty).IsTrue();
        await Assert.That(table.For(-1, 0).IsEmpty).IsTrue();

        table.For(0, 1)[0] = 7;
        table.For(0, 1)[PlayerVantage.MaxAnchors - 1] = 9;
        await Assert.That(table.For(0, 1)[0]).IsEqualTo(7);
        await Assert.That(table.For(1, 0)[0]).IsEqualTo(-1).Because("the reverse pair is its own row");
        await Assert.That(table.For(0, 2)[0]).IsEqualTo(-1);
        await Assert.That(table.For(0, 0)[PlayerVantage.MaxAnchors - 1]).IsEqualTo(-1)
            .Because("the row before (0, 1) must not see (0, 1)'s last slot");
    }

    /// <summary>
    ///     Hinted and plain traversals over a random soup: stale hints shared across unrelated rays
    ///     (the worst case) and per-walk hints along drifting sightlines (the real case). Every
    ///     verdict must agree, and after every occluded ray the hint must name a triangle the oracle
    ///     confirms blocks it. The walks also have to short-circuit somewhere, or the mechanism is
    ///     being tested only on its miss path.
    /// </summary>
    [Test]
    public async Task RandomRays_HintedAndPlainAgree_AndEveryHintNamesABlockingTriangle()
    {
        Random rng = new(20260912);
        const int triangles = 3000;
        float[] vertices = RandomSoup(rng, triangles, 2000f);
        Vector3 lo = new(-2000f, -2000f, -200f), hi = new(2000f, 2000f, 400f);

        List<(Vector3 A, Vector3 B, int Key)> corpus = new(5000 + (200 * 25));
        for (int i = 0; i < 5000; i++)
        {
            // Sightlines of a few hundred units, so a real share are clear; one shared key, so
            // every one of them sees a hint left by an unrelated ray.
            Vector3 a = RandomPoint(rng, lo, hi);
            corpus.Add((a, a + Jitter(rng, 700f), 0));
        }

        for (int walk = 0; walk < 200; walk++)
        {
            Vector3 a = RandomPoint(rng, lo, hi);
            Vector3 b = RandomPoint(rng, lo, hi);
            for (int step = 0; step < 25; step++)
            {
                a += Jitter(rng, 5f);
                b += Jitter(rng, 5f);
                corpus.Add((a, b, 1 + walk));
            }
        }

        RayDifferentialHarness.HintResult result = RayDifferentialHarness.RunWithHints(vertices, triangles, corpus);
        Console.WriteLine($"[occluder-hint] synthetic: {result.Describe()}");

        await Assert.That(result.Divergences.Count).IsEqualTo(0)
            .Because("a hint may change the work, never the verdict. " + result.Describe());
        await Assert.That(result.BadHints).IsEqualTo(0)
            .Because("after an occluded ray the hint must name a triangle that blocks it");
        await Assert.That(result.Occluded).IsGreaterThan(1000).Because("the soup must occlude a real share of rays");
        await Assert.That(result.RayCount - result.Occluded).IsGreaterThan(1000).Because("and leave a real share clear");
        await Assert.That(result.ShortCircuited).IsGreaterThan(500)
            .Because("drifting sightlines on the walks must reuse their occluder, or the hit path went untested");
    }

    /// <summary>
    ///     The same guarantee at the pair-loop level, where the hints live in per-anchor slots: a
    ///     viewer and target walking together give the same <c>CouldSee</c> and <c>EvaluatePair</c>
    ///     answers with a hint row as without one, with and without smoke.
    /// </summary>
    [Test]
    public async Task PairLoop_WithHintRow_AgreesWithoutOne_AlongAWalk()
    {
        Random rng = new(20260913);
        const int triangles = 3000;
        VisibilityEngine engine = VisibilityEngine.FromTriangles(RandomSoup(rng, triangles, 2000f), triangles);
        Vector3 lo = new(-1500f, -1500f, -100f), hi = new(1500f, 1500f, 300f);
        int[] hints = new int[PlayerVantage.MaxAnchors];
        int[] pairHints = new int[PlayerVantage.MaxAnchors];
        Array.Fill(hints, -1);
        Array.Fill(pairHints, -1);
        Vector4[] smoke = new Vector4[1];

        int mismatches = 0, couldSee = 0, notCouldSee = 0;
        for (int walk = 0; walk < 40; walk++)
        {
            Vector3 viewerFeet = RandomPoint(rng, lo, hi);
            Vector3 targetFeet = viewerFeet + Jitter(rng, 600f);
            for (int step = 0; step < 30; step++)
            {
                viewerFeet += Jitter(rng, 6f);
                targetFeet += Jitter(rng, 6f);
                Vector3 toTarget = targetFeet - viewerFeet;
                float yaw = MathF.Atan2(toTarget.Y, toTarget.X) * 180f / MathF.PI + (float)(rng.NextDouble() * 40.0 - 20.0);
                VisibilityAnalyzer.Vantage viewer = new(
                    0, 2, viewerFeet, PlayerVantage.Eye(viewerFeet, 0f), PlayerVantage.Forward(0f, yaw), true, 0f);
                float duck = step % 7 == 0 ? 0.6f : 0f;
                VisibilityAnalyzer.Vantage target = new(
                    1, 3, targetFeet, PlayerVantage.Eye(targetFeet, duck), Vector3.UnitX, true, duck);

                ReadOnlySpan<Vector4> smokes = ReadOnlySpan<Vector4>.Empty;
                if (step % 5 == 0)
                {
                    Vector3 mid = Vector3.Lerp(viewer.Eye, targetFeet + new Vector3(0f, 0f, 48f), 0.5f);
                    smoke[0] = new Vector4(mid.X, mid.Y, mid.Z, SmokeVolumes.DefaultRadius);
                    smokes = smoke;
                }

                bool plain = VisibilityAnalyzer.CouldSee(engine, viewer, target, 53f, 37f, smokes);
                bool hinted = VisibilityAnalyzer.CouldSee(engine, viewer, target, 53f, 37f, smokes, hints);
                (bool, bool) plainPair = VisibilityAnalyzer.EvaluatePair(engine, viewer, target, 53f, 37f, smokes);
                (bool, bool) hintedPair = VisibilityAnalyzer.EvaluatePair(engine, viewer, target, 53f, 37f, smokes, pairHints);
                if (plain != hinted || plainPair != hintedPair)
                {
                    mismatches++;
                }

                if (plain)
                {
                    couldSee++;
                }
                else
                {
                    notCouldSee++;
                }
            }
        }

        Console.WriteLine($"[occluder-hint] pair loop walk: couldSee={couldSee} notCouldSee={notCouldSee} mismatches={mismatches}");
        await Assert.That(mismatches).IsEqualTo(0);
        await Assert.That(couldSee).IsGreaterThan(50);
        await Assert.That(notCouldSee).IsGreaterThan(50);
        await Assert.That(hints.Count(h => h >= 0)).IsGreaterThan(0).Because("some anchor was occluded and wrote its hint");
        await Assert.That(pairHints.Count(h => h >= 0)).IsGreaterThan(0);
    }

    private static VisibilityAnalyzer.Vantage Player(int slot, int team, float x, float yawDeg)
    {
        Vector3 feet = new(x, 0f, 0f);
        return new VisibilityAnalyzer.Vantage(
            slot, team, feet, PlayerVantage.Eye(feet, 0f), PlayerVantage.Forward(0f, yawDeg), true, 0f);
    }

    private static Vector3 Jitter(Random rng, float scale) => new(
        (float)(rng.NextDouble() * 2.0 - 1.0) * scale,
        (float)(rng.NextDouble() * 2.0 - 1.0) * scale,
        (float)(rng.NextDouble() * 2.0 - 1.0) * scale * 0.25f);

    private static Vector3 RandomPoint(Random rng, Vector3 lo, Vector3 hi) => new(
        lo.X + (hi.X - lo.X) * (float)rng.NextDouble(),
        lo.Y + (hi.Y - lo.Y) * (float)rng.NextDouble(),
        lo.Z + (hi.Z - lo.Z) * (float)rng.NextDouble());

    // Random triangles up to 400 units across in a 2*half box; the same shape VisibilityPairParityTests
    // uses, so sightlines of a few hundred units are usually clear and long ones usually blocked.
    private static float[] RandomSoup(Random rng, int count, float half)
    {
        float[] v = new float[count * 9];
        for (int i = 0; i < count; i++)
        {
            Vector3 c = new(
                (float)(rng.NextDouble() * 2.0 - 1.0) * half,
                (float)(rng.NextDouble() * 2.0 - 1.0) * half,
                (float)(rng.NextDouble() * 400.0 - 100.0));
            for (int k = 0; k < 3; k++)
            {
                v[i * 9 + k * 3 + 0] = c.X + (float)(rng.NextDouble() * 400.0 - 200.0);
                v[i * 9 + k * 3 + 1] = c.Y + (float)(rng.NextDouble() * 400.0 - 200.0);
                v[i * 9 + k * 3 + 2] = c.Z + (float)(rng.NextDouble() * 200.0 - 100.0);
            }
        }

        return v;
    }
}
