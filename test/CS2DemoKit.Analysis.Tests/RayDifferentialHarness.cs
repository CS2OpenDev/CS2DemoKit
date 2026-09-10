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

    /// <summary>One ray on which the hinted and plain traversals of the live BVH disagreed.</summary>
    /// <param name="Origin">Ray origin.</param>
    /// <param name="Direction">Unit direction.</param>
    /// <param name="Distance">Segment length in world units.</param>
    /// <param name="PlainOccluded">What the traversal said with no hint.</param>
    /// <param name="HintedOccluded">What it said with the carried hint.</param>
    /// <param name="OracleOccluded">What the brute-force oracle said.</param>
    /// <param name="HintBefore">The hint handed in, or -1.</param>
    /// <param name="HintAfter">The hint handed back.</param>
    internal readonly record struct HintDivergence(
        Vector3 Origin,
        Vector3 Direction,
        float Distance,
        bool PlainOccluded,
        bool HintedOccluded,
        bool OracleOccluded,
        int HintBefore,
        int HintAfter)
    {
        /// <summary>Whether the hinted traversal is the one the oracle agrees with.</summary>
        public bool HintedIsCorrect => HintedOccluded == OracleOccluded;

        /// <summary>A one-line description naming the ray, the hints and all three verdicts.</summary>
        public override string ToString() =>
            $"origin=({Origin.X:F3},{Origin.Y:F3},{Origin.Z:F3}) "
            + $"dir=({Direction.X:F5},{Direction.Y:F5},{Direction.Z:F5}) len={Distance:F3} "
            + $"plain={(PlainOccluded ? "OCCLUDED" : "CLEAR")} "
            + $"hinted={(HintedOccluded ? "OCCLUDED" : "CLEAR")} "
            + $"oracle={(OracleOccluded ? "OCCLUDED" : "CLEAR")} hint={HintBefore}->{HintAfter}";
    }

    /// <summary>The outcome of running one keyed corpus with hints.</summary>
    /// <param name="RayCount">Rays evaluated.</param>
    /// <param name="Occluded">Rays the plain traversal called occluded.</param>
    /// <param name="ShortCircuited">Rays the hint decided without a traversal.</param>
    /// <param name="BadHints">Occluded rays after which the hint named a triangle the oracle says does not block. Must be zero.</param>
    /// <param name="Divergences">Every ray on which hinted and plain disagreed, up to the adjudication cap.</param>
    /// <param name="Truncated">
    ///     True when the run stopped at the cap: the corpus is already proven broken and adjudicating
    ///     every remaining ray against the O(n) oracle would only delay the failure.
    /// </param>
    internal readonly record struct HintResult(
        int RayCount, int Occluded, int ShortCircuited, int BadHints, IReadOnlyList<HintDivergence> Divergences,
        bool Truncated)
    {
        /// <summary>Divergences where the hinted traversal disagrees with the oracle. Must always be empty.</summary>
        public IReadOnlyList<HintDivergence> HintedWrong => [.. Divergences.Where(d => !d.HintedIsCorrect)];

        /// <summary>Short-circuited rays as a share of all rays.</summary>
        public double HitRate => RayCount > 0 ? ShortCircuited / (double)RayCount : 0.0;

        /// <summary>A report naming the counts, the hit rate and the first few diverging rays.</summary>
        public string Describe(int sample = 5) =>
            $"{RayCount} rays, {Occluded} occluded, {ShortCircuited} short-circuited ({HitRate:P2} of rays, "
            + $"{(Occluded > 0 ? ShortCircuited / (double)Occluded : 0.0):P2} of occluded), {BadHints} bad hints, "
            + $"{Divergences.Count} divergent, {HintedWrong.Count} where hinted is wrong"
            + (Truncated ? " (STOPPED at the adjudication cap; counts are partial)" : string.Empty)
            + (Divergences.Count == 0
                ? string.Empty
                : Environment.NewLine + string.Join(Environment.NewLine, Divergences.Take(sample)));
    }

    /// <summary>
    ///     Evaluates a keyed corpus on the live BVH twice, without a hint and with one hint carried
    ///     per <c>Key</c> in corpus order (the way the scanner carries one per (viewer, target,
    ///     anchor) across samples), and reports every ray on which the two disagreed, adjudicated by
    ///     the oracle. After every occluded hinted ray the hint handed back is checked against the
    ///     oracle as well: a hint that names a non-blocking triangle is a defect even when the
    ///     verdict happened to be right.
    /// </summary>
    /// <param name="vertices">Triangle soup, 9 floats per triangle.</param>
    /// <param name="triangleCount">Number of triangles.</param>
    /// <param name="segments">Endpoint pairs to test, in order, each with the hint key it shares.</param>
    /// <param name="eps">Endpoint exclusion. Matches <c>VisibilityEngine.SegmentEps</c>.</param>
    /// <param name="maxDivergences">
    ///     Stop after this many divergences. Each one costs an oracle pass over every triangle, so
    ///     a broken traversal on a million-ray corpus must fail in seconds, not hours.
    /// </param>
    public static HintResult RunWithHints(
        float[] vertices,
        int triangleCount,
        IReadOnlyList<(Vector3 A, Vector3 B, int Key)> segments,
        float eps = 0.1f,
        int maxDivergences = 200)
    {
        ArgumentNullException.ThrowIfNull(vertices);
        ArgumentNullException.ThrowIfNull(segments);

        TriangleBvh bvh = TriangleBvh.Build(vertices, triangleCount);
        Dictionary<int, int> hints = new();
        List<HintDivergence> divergences = [];
        int rays = 0, occluded = 0, shortCircuited = 0, badHints = 0;
        bool truncated = false;

        for (int i = 0; i < segments.Count; i++)
        {
            if (divergences.Count >= maxDivergences)
            {
                truncated = true;
                break;
            }

            (Vector3 a, Vector3 b, int key) = segments[i];
            Vector3 delta = b - a;
            float len = delta.Length();
            if (len <= 2f * eps)
            {
                continue; // VisibilityEngine calls these trivially visible without casting.
            }

            Vector3 dir = delta / len;
            rays++;
            bool plainHit = bvh.AnyHit(a, dir, len, eps);
            int hint = hints.GetValueOrDefault(key, -1);
            int before = hint;
            bool hintedHit = bvh.AnyHit(a, dir, len, eps, ref hint, out bool sc);
            hints[key] = hint;
            if (plainHit)
            {
                occluded++;
            }

            if (sc)
            {
                shortCircuited++;
            }

            if (hintedHit && !BruteForceOracle.Blocks(vertices, hint, a, dir, len, eps))
            {
                badHints++;
            }

            if (plainHit == hintedHit)
            {
                continue;
            }

            divergences.Add(new HintDivergence(
                a, dir, len, plainHit, hintedHit,
                BruteForceOracle.AnyHit(vertices, triangleCount, a, dir, len, eps),
                before, hint));
        }

        return new HintResult(rays, occluded, shortCircuited, badHints, divergences, truncated);
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
