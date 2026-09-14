#region

using System.Numerics;

#endregion

namespace CS2DemoKit.Analysis.Tests;

/// <summary>
///     Seeded ray corpora over a bake's bounds, shared by the tree differential and tier tests so
///     every one of them judges the same rays. Deterministic by construction: the same seed and
///     bounds give the same segments on every run, so a divergence count is a number that can be
///     compared between runs and quoted.
/// </summary>
internal static class RayCorpora
{
    /// <summary>Eye height above a player's feet; two players on one floor share it exactly.</summary>
    public const float EyeZ = 64f;

    /// <summary>Random segments between two uniform points in the bounds: the general case, mostly long.</summary>
    /// <param name="lo">Lower corner.</param>
    /// <param name="hi">Upper corner.</param>
    /// <param name="count">Segments to produce.</param>
    /// <param name="seed">Generator seed.</param>
    public static List<(Vector3 A, Vector3 B)> Random(Vector3 lo, Vector3 hi, int count, int seed)
    {
        Random rng = new(seed);
        List<(Vector3, Vector3)> segments = new(count);
        for (int i = 0; i < count; i++)
        {
            segments.Add((Point(rng, lo, hi), Point(rng, lo, hi)));
        }

        return segments;
    }

    /// <summary>
    ///     Sightlines of a few hundred to a couple of thousand units that drift a few units per
    ///     step, the shape consecutive samples of one viewer-target pair take.
    /// </summary>
    /// <param name="lo">Lower corner.</param>
    /// <param name="hi">Upper corner.</param>
    /// <param name="walks">Independent sightlines.</param>
    /// <param name="steps">Samples per sightline.</param>
    /// <param name="seed">Generator seed.</param>
    public static List<(Vector3 A, Vector3 B)> Walked(Vector3 lo, Vector3 hi, int walks, int steps, int seed)
    {
        Random rng = new(seed);
        List<(Vector3, Vector3)> segments = new(walks * steps);
        for (int walk = 0; walk < walks; walk++)
        {
            Vector3 a = Point(rng, lo, hi);
            Vector3 b = a + new Vector3(
                (float)((rng.NextDouble() * 1600.0) - 800.0),
                (float)((rng.NextDouble() * 1600.0) - 800.0),
                (float)((rng.NextDouble() * 200.0) - 100.0));
            for (int step = 0; step < steps; step++)
            {
                a += Step(rng);
                b += Step(rng);
                segments.Add((a, b));
            }
        }

        return segments;
    }

    /// <summary>
    ///     Same-floor eye-to-eye segments on an integer grid across twenty floor heights, the corpus
    ///     that reaches the axis-aligned slab degeneracy: integer coordinates land origins exactly on
    ///     brush planes, and a shared eye height makes <c>dir.Z</c> exactly zero. The same
    ///     construction as <c>SlabDegeneracyTests</c>, kept identical on purpose.
    /// </summary>
    /// <param name="lo">Lower corner.</param>
    /// <param name="hi">Upper corner.</param>
    /// <param name="tilt">Added to the eye height; zero keeps the degeneracy, half a unit removes it.</param>
    /// <param name="seed">Generator seed.</param>
    public static List<(Vector3 A, Vector3 B)> SameFloor(Vector3 lo, Vector3 hi, float tilt, int seed)
    {
        Random rng = new(seed);
        List<(Vector3, Vector3)> segments = new(6000);
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

    private static Vector3 Step(Random rng) => new(
        (float)((rng.NextDouble() * 12.0) - 6.0),
        (float)((rng.NextDouble() * 12.0) - 6.0),
        (float)((rng.NextDouble() * 2.0) - 1.0));

    private static Vector3 Point(Random rng, Vector3 lo, Vector3 hi) => new(
        lo.X + ((hi.X - lo.X) * (float)rng.NextDouble()),
        lo.Y + ((hi.Y - lo.Y) * (float)rng.NextDouble()),
        lo.Z + ((hi.Z - lo.Z) * (float)rng.NextDouble()));
}
