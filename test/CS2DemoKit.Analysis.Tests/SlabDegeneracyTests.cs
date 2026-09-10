#region

using System.Numerics;
using CS2DemoKit.Analysis.Visibility;
using TUnit.Core.Exceptions;

#endregion

namespace CS2DemoKit.Analysis.Tests;

/// <summary>
///     Pins the axis-aligned slab degeneracy: a ray whose direction is exactly zero on one axis and
///     whose origin lies exactly on a node's slab plane for that axis.
///     <para>
///         <c>inv = 1 / dir</c> is infinite on a zero-direction axis, which the traversal comment
///         claimed the min/max ordering handled. It does, except on the one case where the numerator
///         is also zero: <c>inf * 0</c> is NaN, <c>Math.Max</c> and <c>Math.Min</c> propagate NaN,
///         every subsequent comparison against it is false, the node is rejected, and the ray reports
///         CLEAR THROUGH SOLID GEOMETRY.
///     </para>
///     <para>
///         This is not a tolerance question and it is not rare in the geometry that matters. Two
///         players standing on the same floor produce an eye-to-eye ray with <c>dir.Z</c> exactly
///         zero, and map brushes sit at round heights, so the coincidence lands often enough to
///         measure: see <see cref="SameFloorRays_NeverDisagreeWithTheOracle" />.
///     </para>
/// </summary>
public class SlabDegeneracyTests
{
    // Eye height above a player's feet. Two players on one floor share it exactly, which is what
    // makes dir.Z exactly zero rather than merely small.
    private const float EyeZ = 64f;

    /// <summary>
    ///     One triangle, one ray, no map required: the whole defect in isolation.
    ///     <para>
    ///         The blocker's lowest edge sits exactly at the ray's height, so the single-leaf root's
    ///         <c>bmin.Z</c> equals the ray origin's Z. That is not a contrived coincidence, it is
    ///         forced: a node's <c>bmin.Z</c> is the minimum over its triangles, so for it to equal
    ///         the ray height at all, some triangle in that node must reach exactly that height.
    ///     </para>
    /// </summary>
    [Test]
    public async Task AxisAlignedRayGrazingASlabPlane_IsNotLostToNaN()
    {
        // Blocker in the plane X = 0, spanning Y -8..8 at its base and rising to Z 80.
        float[] vertices =
        [
            0f, -8f, EyeZ,
            0f, 8f, EyeZ,
            0f, 0f, 80f
        ];

        Vector3 a = new(-100f, 0f, EyeZ);
        Vector3 b = new(100f, 0f, EyeZ);
        Vector3 delta = b - a;
        float len = delta.Length();
        Vector3 dir = delta / len;

        // The premise of the whole test: the degeneracy is real, not approximated.
        await Assert.That(dir.Z).IsEqualTo(0f).Because("both endpoints share an exact eye height");
        await Assert.That(1f / dir.Z).IsEqualTo(float.PositiveInfinity);

        bool oracle = BruteForceOracle.AnyHit(vertices, 1, a, dir, len, 0.1f);
        await Assert.That(oracle).IsTrue()
            .Because("the ray passes through the blocker's base edge, which is a hit");

        // The frozen copy carries the defect. If this ever stops being true the harness has silently
        // stopped comparing anything, so it is asserted rather than assumed.
        bool legacy = LegacyTriangleBvh.Build(vertices, 1).AnyHit(a, dir, len, 0.1f);
        await Assert.That(legacy).IsFalse()
            .Because("the pre-fix traversal reports clear through solid geometry here");

        bool current = TriangleBvh.Build(vertices, 1).AnyHit(a, dir, len, 0.1f);
        await Assert.That(current).IsEqualTo(oracle)
            .Because("a zero-direction axis must constrain nothing, not reject the node");
    }

    /// <summary>
    ///     The same ray tilted half a unit is unaffected, which is what identifies this as an
    ///     axis-aligned degeneracy rather than a tolerance problem. Without this, widening some
    ///     epsilon would look like a fix.
    /// </summary>
    [Test]
    public async Task TheSameRayTilted_WasNeverAffected()
    {
        float[] vertices =
        [
            0f, -8f, EyeZ,
            0f, 8f, EyeZ,
            0f, 0f, 80f
        ];

        Vector3 a = new(-100f, 0f, EyeZ + 0.5f);
        Vector3 b = new(100f, 0f, EyeZ + 0.5f);
        Vector3 delta = b - a;
        float len = delta.Length();
        Vector3 dir = delta / len;

        bool oracle = BruteForceOracle.AnyHit(vertices, 1, a, dir, len, 0.1f);
        bool legacy = LegacyTriangleBvh.Build(vertices, 1).AnyHit(a, dir, len, 0.1f);
        bool current = TriangleBvh.Build(vertices, 1).AnyHit(a, dir, len, 0.1f);

        await Assert.That(oracle).IsTrue();
        await Assert.That(legacy).IsEqualTo(oracle).Because("half a unit off the plane, the old code was right");
        await Assert.That(current).IsEqualTo(oracle);
    }

    /// <summary>
    ///     A ray parallel to an axis but outside the slab must still be rejected. The fix must not
    ///     buy correctness by accepting every degenerate ray, which would be slower and would hide a
    ///     later regression behind a conservative answer.
    /// </summary>
    [Test]
    public async Task AxisAlignedRayOutsideTheSlab_IsStillRejected()
    {
        float[] vertices =
        [
            0f, -8f, EyeZ,
            0f, 8f, EyeZ,
            0f, 0f, 80f
        ];

        // Same horizontal ray, well below the blocker's base.
        Vector3 a = new(-100f, 0f, EyeZ - 40f);
        Vector3 b = new(100f, 0f, EyeZ - 40f);
        Vector3 delta = b - a;
        float len = delta.Length();
        Vector3 dir = delta / len;

        await Assert.That(BruteForceOracle.AnyHit(vertices, 1, a, dir, len, 0.1f)).IsFalse();
        await Assert.That(TriangleBvh.Build(vertices, 1).AnyHit(a, dir, len, 0.1f)).IsFalse()
            .Because("a miss must stay a miss; the fix is not 'accept everything degenerate'");
    }

    /// <summary>
    ///     The real-geometry corpus: same-floor eye-to-eye rays over a baked map, the exact shape a
    ///     duel produces. Every ray whose verdict changed is adjudicated against the brute-force
    ///     oracle, and the live traversal has to be the one the oracle backs. Every time.
    ///     <para>
    ///         This does NOT gate on observing a divergence. Whether a given bake happens to put a
    ///         brush plane at a sampled eye height is a property of that map, not of the fix: the rate
    ///         is 72 in 6000 on de_dust2 and 1 in 6000 on de_nuke. The existence proof is
    ///         <see cref="AxisAlignedRayGrazingASlabPlane_IsNotLostToNaN" />, which is deterministic
    ///         and needs no bake at all, and the observation gate is
    ///         <see cref="TheCorpusStillObservesTheDefect" />.
    ///     </para>
    /// </summary>
    /// <param name="map">Map whose <c>collision.tris</c> to load.</param>
    [Test]
    [Arguments("de_nuke")]
    [Arguments("de_dust2")]
    [Category("RealAsset")]
    public async Task SameFloorRays_NeverDisagreeWithTheOracle(string map)
    {
        (float[] vertices, int triangleCount) = LoadBake(map);

        List<(Vector3, Vector3)> corpus = BuildSameFloorCorpus(vertices, triangleCount, tilt: 0f);
        RayDifferentialHarness.Result result =
            RayDifferentialHarness.Run(vertices, triangleCount, corpus);

        // Printed, not just asserted: the scope of a behaviour change is a number someone will want
        // to quote later, and it is cheaper to read it off a passing run than to reconstruct it.
        Console.WriteLine($"[slab-degeneracy] {map}: {result.Describe()}");

        await Assert.That(result.CurrentWrong.Count).IsEqualTo(0)
            .Because("every changed verdict must be the one the oracle backs. " + result.Describe());
    }

    /// <summary>
    ///     The observation gate: a differential harness that never observes anything proves nothing,
    ///     so one map has to keep finding the case. de_dust2 carries it at 72 in 6000, which is a
    ///     comfortable margin; de_nuke would be gating on a single ray.
    /// </summary>
    [Test]
    [Category("RealAsset")]
    public async Task TheCorpusStillObservesTheDefect()
    {
        (float[] vertices, int triangleCount) = LoadBake("de_dust2");

        List<(Vector3, Vector3)> corpus = BuildSameFloorCorpus(vertices, triangleCount, tilt: 0f);
        RayDifferentialHarness.Result result =
            RayDifferentialHarness.Run(vertices, triangleCount, corpus);

        await Assert.That(result.Divergences.Count).IsGreaterThan(0)
            .Because("if the corpus stops reaching the degeneracy, this harness has quietly become "
                     + "decoration and every later step's 'no behaviour change' proof weakens with it");
    }

    /// <summary>
    ///     The same corpus tilted off the axis diverges nowhere, confirming the fix touches only the
    ///     degenerate case and leaves every ordinary ray's answer alone.
    /// </summary>
    /// <param name="map">Map whose <c>collision.tris</c> to load.</param>
    [Test]
    [Arguments("de_nuke")]
    [Category("RealAsset")]
    public async Task TiltedRays_DivergeNowhere(string map)
    {
        (float[] vertices, int triangleCount) = LoadBake(map);

        List<(Vector3, Vector3)> corpus = BuildSameFloorCorpus(vertices, triangleCount, tilt: 0.5f);
        RayDifferentialHarness.Result result =
            RayDifferentialHarness.Run(vertices, triangleCount, corpus);

        Console.WriteLine($"[slab-degeneracy] {map} tilted: {result.Describe()}");

        await Assert.That(result.Divergences.Count).IsEqualTo(0)
            .Because("off the slab plane the old traversal was already correct. " + result.Describe());
    }

    // Seeded eye-to-eye segments swept across many floor heights. Deterministic: the same corpus
    // every run, so a divergence count is comparable between runs and a regression reads as a number
    // rather than a flake.
    //
    // Sweeping heights rather than picking one is what makes this representative. A map has floors at
    // many elevations and the degeneracy needs the eye height to coincide with a node's slab plane,
    // so a single-height corpus finds the case on one map and misses it on the next for no reason
    // that means anything.
    private static List<(Vector3, Vector3)> BuildSameFloorCorpus(
        float[] vertices, int triangleCount, float tilt)
    {
        TriangleBvh bvh = TriangleBvh.Build(vertices, triangleCount);
        Vector3 lo = bvh.Min, hi = bvh.Max;
        List<(Vector3, Vector3)> segments = new(6000);

        // Integer world coordinates on purpose. Map brushes sit on round numbers, so an integer grid
        // is where an origin coincides exactly with a slab plane; a float-jittered grid would mostly
        // miss the case this exists to find.
        Random rng = new(20260910);
        for (int level = 0; level < 20; level++)
        {
            float floorZ = MathF.Round(lo.Z + ((hi.Z - lo.Z) * level / 20f));
            float z = floorZ + EyeZ + tilt;
            for (int i = 0; i < 300; i++)
            {
                Vector3 a = new(
                    MathF.Round(lo.X + ((hi.X - lo.X) * (float)rng.NextDouble())),
                    MathF.Round(lo.Y + ((hi.Y - lo.Y) * (float)rng.NextDouble())),
                    z);
                Vector3 b = new(
                    MathF.Round(lo.X + ((hi.X - lo.X) * (float)rng.NextDouble())),
                    MathF.Round(lo.Y + ((hi.Y - lo.Y) * (float)rng.NextDouble())),
                    z);
                segments.Add((a, b));
            }
        }

        return segments;
    }

    private static (float[] Vertices, int TriangleCount) LoadBake(string map)
    {
        string? dir = Environment.GetEnvironmentVariable(CollisionAssetLocator.EnvVar);
        if (string.IsNullOrWhiteSpace(dir))
        {
            throw new SkipTestException(
                $"Set {CollisionAssetLocator.EnvVar} to a directory holding <map>/collision.tris "
                + "(the app checkout's assets/ directory is one) to run the real-geometry corpus.");
        }

        string path = Path.Combine(dir, map, "collision.tris");
        if (!File.Exists(path))
        {
            throw new SkipTestException($"No bake for {map}: looked for {path}");
        }

        CollisionTris.Data data = CollisionTris.Load(path);
        return (data.Vertices, data.TriangleCount);
    }
}
