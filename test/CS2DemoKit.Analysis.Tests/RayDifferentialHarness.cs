#region

using System.Numerics;
using CS2DemoKit.Analysis.Visibility;

#endregion

namespace CS2DemoKit.Analysis.Tests;

/// <summary>
///     Runs a ray corpus through the frozen <see cref="LegacyTriangleBvh" />, the live
///     <see cref="TriangleBvh" /> and <see cref="BruteForceOracle" />, and reports every ray whose
///     verdict differs between the two BVHs.
///     <para>
///         This is the instrument the whole visibility performance programme is measured with, so it
///         is built once and reused by every later step. The contract it enforces is not "the two
///         agree" but "wherever they disagree, the live one is the one the oracle backs". A
///         performance change is allowed to be faster; it is not allowed to change an answer, and
///         when an answer does change, the change has to be adjudicated rather than accepted because
///         the suite went green.
///     </para>
/// </summary>
internal static class RayDifferentialHarness
{
    /// <summary>One ray on which the frozen and live traversals disagreed.</summary>
    /// <param name="Origin">Ray origin.</param>
    /// <param name="Direction">Unit direction.</param>
    /// <param name="Distance">Segment length in world units.</param>
    /// <param name="LegacyOccluded">What the frozen traversal said.</param>
    /// <param name="CurrentOccluded">What the live traversal said.</param>
    /// <param name="OracleOccluded">What the brute-force oracle said.</param>
    /// <param name="OracleTriangle">First triangle the oracle hit, or -1.</param>
    internal readonly record struct Divergence(
        Vector3 Origin,
        Vector3 Direction,
        float Distance,
        bool LegacyOccluded,
        bool CurrentOccluded,
        bool OracleOccluded,
        int OracleTriangle)
    {
        /// <summary>Whether the live traversal is the one the oracle agrees with.</summary>
        public bool CurrentIsCorrect => CurrentOccluded == OracleOccluded;

        /// <summary>A one-line description naming the ray and all three verdicts.</summary>
        public override string ToString() =>
            $"origin=({Origin.X:F3},{Origin.Y:F3},{Origin.Z:F3}) "
            + $"dir=({Direction.X:F5},{Direction.Y:F5},{Direction.Z:F5}) len={Distance:F3} "
            + $"legacy={(LegacyOccluded ? "OCCLUDED" : "CLEAR")} "
            + $"current={(CurrentOccluded ? "OCCLUDED" : "CLEAR")} "
            + $"oracle={(OracleOccluded ? "OCCLUDED" : "CLEAR")} tri={OracleTriangle}";
    }

    /// <summary>The outcome of running one corpus.</summary>
    /// <param name="RayCount">Rays evaluated.</param>
    /// <param name="Divergences">Every ray on which the two traversals disagreed.</param>
    internal readonly record struct Result(int RayCount, IReadOnlyList<Divergence> Divergences)
    {
        /// <summary>Divergences where the live traversal disagrees with the oracle. Must always be empty.</summary>
        public IReadOnlyList<Divergence> CurrentWrong =>
            [.. Divergences.Where(d => !d.CurrentIsCorrect)];

        /// <summary>A report naming the counts and the first few diverging rays.</summary>
        public string Describe(int sample = 5) =>
            $"{RayCount} rays, {Divergences.Count} divergent, {CurrentWrong.Count} where current is wrong"
            + (Divergences.Count == 0
                ? string.Empty
                : Environment.NewLine + string.Join(Environment.NewLine, Divergences.Take(sample)));
    }

    /// <summary>
    ///     Evaluates <paramref name="segments" /> against both traversals and the oracle.
    /// </summary>
    /// <param name="vertices">Triangle soup, 9 floats per triangle.</param>
    /// <param name="triangleCount">Number of triangles.</param>
    /// <param name="segments">Endpoint pairs to test, in world space.</param>
    /// <param name="eps">
    ///     Endpoint exclusion. Matches <c>VisibilityEngine.SegmentEps</c> so the corpus exercises the
    ///     predicate the visibility stats actually run.
    /// </param>
    public static Result Run(
        float[] vertices,
        int triangleCount,
        IReadOnlyList<(Vector3 A, Vector3 B)> segments,
        float eps = 0.1f)
    {
        ArgumentNullException.ThrowIfNull(vertices);
        ArgumentNullException.ThrowIfNull(segments);

        LegacyTriangleBvh legacy = LegacyTriangleBvh.Build(vertices, triangleCount);
        TriangleBvh current = TriangleBvh.Build(vertices, triangleCount);
        List<Divergence> divergences = [];

        for (int i = 0; i < segments.Count; i++)
        {
            (Vector3 a, Vector3 b) = segments[i];
            Vector3 delta = b - a;
            float len = delta.Length();
            if (len <= 2f * eps)
            {
                continue; // VisibilityEngine calls these trivially visible without casting.
            }

            Vector3 dir = delta / len;
            bool legacyHit = legacy.AnyHit(a, dir, len, eps);
            bool currentHit = current.AnyHit(a, dir, len, eps);
            if (legacyHit == currentHit)
            {
                continue;
            }

            divergences.Add(new Divergence(
                a, dir, len, legacyHit, currentHit,
                BruteForceOracle.AnyHit(vertices, triangleCount, a, dir, len, eps),
                BruteForceOracle.FirstHitTriangle(vertices, triangleCount, a, dir, len, eps)));
        }

        return new Result(segments.Count, divergences);
    }
}
