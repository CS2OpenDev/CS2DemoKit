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
    /// <summary>
    ///     How wide, in world units along the ray, the double-precision window of a float-rejected
    ///     box may be, in either direction, and the rejection still count as rounding: the float
    ///     slab and the float triangle test each carry a handful of ulp, about 5e-4 at map
    ///     coordinates, so a true miss of under a thousandth of a unit with a float triangle hit
    ///     inside, or a true pass that narrow the float slab closed, is the two disagreeing about a
    ///     hair, not a wrong tree. A box the ray truly passes through by more than this, rejected in
    ///     float, is a defect, and so is one it truly misses by more than this with a float triangle
    ///     hit inside; the bound is two-sided so that a slab test rejecting real boxes cannot hide
    ///     as the boundary class.
    /// </summary>
    public const double BoundaryTolerance = 1e-3;

    /// <summary>One ray on which the frozen and live traversals disagreed.</summary>
    /// <param name="Origin">Ray origin.</param>
    /// <param name="Direction">Unit direction.</param>
    /// <param name="Distance">Segment length in world units.</param>
    /// <param name="LegacyOccluded">What the frozen traversal said.</param>
    /// <param name="CurrentOccluded">What the live traversal said.</param>
    /// <param name="OracleOccluded">What the brute-force oracle said.</param>
    /// <param name="OracleTriangle">Nearest triangle the oracle hit, or -1.</param>
    /// <param name="OracleDistance">The oracle's nearest hit <c>t</c>, or <see cref="float.MaxValue" />.</param>
    /// <param name="Loss">
    ///     Why the live traversal missed the oracle's triangle, or the unexplained sentinel
    ///     (<c>FailingLane</c> -1) when there is no lane to blame: an agreeing ray, or a live
    ///     OCCLUDED against an oracle CLEAR, which no box rejection explains.
    /// </param>
    internal readonly record struct Divergence(
        Vector3 Origin,
        Vector3 Direction,
        float Distance,
        bool LegacyOccluded,
        bool CurrentOccluded,
        bool OracleOccluded,
        int OracleTriangle,
        float OracleDistance,
        LossClassification Loss)
    {
        /// <summary>Whether the live traversal is the one the oracle agrees with.</summary>
        public bool CurrentIsCorrect => CurrentOccluded == OracleOccluded;

        /// <summary>
        ///     A wrong live verdict that rounding explains: a box on the path to the oracle's
        ///     triangle was rejected by the float slab test while the same box in double precision
        ///     admits the ray, or misses it by under <see cref="BoundaryTolerance" />. Counted and
        ///     reported, never a pass by itself.
        /// </summary>
        public bool IsBoundaryClass => !CurrentIsCorrect && Loss.IsRounding;

        /// <summary>A one-line description naming the ray and all three verdicts.</summary>
        public override string ToString() =>
            $"origin=({Origin.X:F3},{Origin.Y:F3},{Origin.Z:F3}) "
            + $"dir=({Direction.X:F5},{Direction.Y:F5},{Direction.Z:F5}) len={Distance:F3} "
            + $"legacy={(LegacyOccluded ? "OCCLUDED" : "CLEAR")} "
            + $"current={(CurrentOccluded ? "OCCLUDED" : "CLEAR")} "
            + $"oracle={(OracleOccluded ? "OCCLUDED" : "CLEAR")} tri={OracleTriangle} t={OracleDistance:F4}"
            + (CurrentIsCorrect ? string.Empty : $" {Loss}");
    }

    /// <summary>
    ///     Where and why the live traversal lost a triangle the oracle hits: the first lane on the
    ///     root-to-leaf path whose float slab test rejects the ray, and how far the same box, in
    ///     double precision, admits the ray (positive: the ray truly passes through it and the float
    ///     rejection is rounding) or misses it (negative). No failing lane means every box on the
    ///     path admits the ray and the traversal still did not test the triangle, which no rounding
    ///     explains.
    /// </summary>
    /// <param name="FailingLane">Lane index that rejected the ray, or -1 when none did.</param>
    /// <param name="DoubleMargin">Width of the double-precision slab window at that lane; NaN when no lane failed.</param>
    /// <param name="Depth">Lanes from the root to the failing lane, or the leaf when none failed.</param>
    internal readonly record struct LossClassification(int FailingLane, double DoubleMargin, int Depth)
    {
        /// <summary>
        ///     Whether the rejected box's double-precision window is within
        ///     <see cref="BoundaryTolerance" /> of empty on either side: a box the ray truly
        ///     passes through by a wide margin is not rounding whichever precision rejected it.
        /// </summary>
        public bool IsRounding => FailingLane >= 0 && Math.Abs(DoubleMargin) < BoundaryTolerance;

        /// <inheritdoc />
        public override string ToString() => FailingLane < 0
            ? $"NO BOX REJECTED THE RAY (path depth {Depth}): unexplained"
            : $"lane {FailingLane} at depth {Depth} rejected it; double-precision window {DoubleMargin:E2}"
              + (IsRounding ? " (rounding, boundary class)" : " (NOT rounding: unexplained)");
    }

    /// <summary>The outcome of running one corpus.</summary>
    /// <param name="RayCount">
    ///     Rays actually cast. Segments at or under twice the endpoint exclusion are skipped, the way
    ///     <c>VisibilityEngine</c> skips them, so this is not the corpus size: a caller that gates on
    ///     "did this run test anything" has to be told what ran, not what was handed in.
    /// </param>
    /// <param name="Divergences">Every ray on which the two traversals disagreed.</param>
    internal readonly record struct Result(int RayCount, IReadOnlyList<Divergence> Divergences)
    {
        /// <summary>Divergences where the live traversal disagrees with the oracle.</summary>
        public IReadOnlyList<Divergence> CurrentWrong =>
            [.. Divergences.Where(d => !d.CurrentIsCorrect)];

        /// <summary>Divergences where the frozen traversal disagrees with the oracle.</summary>
        public IReadOnlyList<Divergence> LegacyWrong =>
            [.. Divergences.Where(d => d.LegacyOccluded != d.OracleOccluded)];

        /// <summary>Live-wrong divergences that rounding at a box edge or the window explains.</summary>
        public IReadOnlyList<Divergence> CurrentWrongBoundaryClass =>
            [.. Divergences.Where(d => d.IsBoundaryClass)];

        /// <summary>Live-wrong divergences that nothing explains. Must always be empty.</summary>
        public IReadOnlyList<Divergence> CurrentWrongOutsideBoundary =>
            [.. Divergences.Where(d => !d.CurrentIsCorrect && !d.IsBoundaryClass)];

        /// <summary>
        ///     A report naming the counts, then every live-wrong ray with the lane that rejected it
        ///     and its double-precision margin (so a "boundary class" claim is shown, not asserted),
        ///     then the first few rays where the live tree is the right one.
        /// </summary>
        public string Describe(int sample = 5) =>
            $"{RayCount} rays, {Divergences.Count} divergent, {LegacyWrong.Count} where legacy is wrong, "
            + $"{CurrentWrong.Count} where current is wrong ({CurrentWrongBoundaryClass.Count} boundary class, "
            + $"{CurrentWrongOutsideBoundary.Count} unexplained)"
            + (Divergences.Count == 0
                ? string.Empty
                : Environment.NewLine + string.Join(Environment.NewLine,
                    Divergences.Where(d => !d.CurrentIsCorrect).Concat(Divergences.Where(d => d.CurrentIsCorrect).Take(sample))));
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
        int[]? parents = null;
        int rays = 0;

        for (int i = 0; i < segments.Count; i++)
        {
            (Vector3 a, Vector3 b) = segments[i];
            Vector3 delta = b - a;
            float len = delta.Length();
            if (len <= 2f * eps)
            {
                continue; // VisibilityEngine calls these trivially visible without casting.
            }

            rays++;
            Vector3 dir = delta / len;
            bool legacyHit = legacy.AnyHit(a, dir, len, eps);
            bool currentHit = current.AnyHit(a, dir, len, eps);
            if (legacyHit == currentHit)
            {
                continue;
            }

            // The oracle's window is the same open interval (eps, len - eps) AnyHit uses.
            bool oracleHit = BruteForceOracle.NearestHit(
                vertices, triangleCount, a, dir, len - eps, eps, out float oracleT, out int oracleTriangle);

            // Unexplained until a lane says otherwise. A live verdict of OCCLUDED where the oracle
            // says CLEAR has no lane to blame — no box rejected anything, the traversal reached a
            // triangle and called it a hit that the same body over the whole soup does not — so
            // there is nothing to classify and nothing rounding can plead. Leaving it at `default`
            // would give it lane 0 and a zero margin, which IsRounding reads as the boundary class,
            // and a phantom wall would be counted as a hair.
            LossClassification loss = new(-1, double.NaN, 0);
            if (currentHit != oracleHit && oracleHit)
            {
                parents ??= ParentLanes(current);
                loss = Classify(current, parents, oracleTriangle, a, dir, eps, len - eps);
            }

            divergences.Add(new Divergence(
                a, dir, len, legacyHit, currentHit, oracleHit, oracleTriangle, oracleT, loss));
        }

        return new Result(rays, divergences);
    }

    /// <summary>For every node, the lane index in its parent that references it; -1 for the root.</summary>
    /// <param name="bvh">The live tree.</param>
    public static int[] ParentLanes(TriangleBvh bvh)
    {
        ArgumentNullException.ThrowIfNull(bvh);
        int[] parents = new int[Math.Max(1, bvh.NodeCount)];
        Array.Fill(parents, -1);
        for (int lane = 0; lane < bvh.NodeCount * TriangleBvh.Width; lane++)
        {
            int child = bvh.LaneChild(lane);
            if (child >= 0)
            {
                parents[child] = lane;
            }
        }

        return parents;
    }

    /// <summary>
    ///     Walks the lanes from the root down to the leaf holding <paramref name="triangle" /> and
    ///     finds the first one the float slab test rejects over <c>[lo, hi]</c>; then measures that
    ///     box's window in double precision. See <see cref="LossClassification" />.
    /// </summary>
    /// <param name="bvh">The live tree.</param>
    /// <param name="parents">From <see cref="ParentLanes" />.</param>
    /// <param name="triangle">The triangle the oracle hit.</param>
    /// <param name="origin">Ray origin.</param>
    /// <param name="dir">Unit direction.</param>
    /// <param name="lo">Window start.</param>
    /// <param name="hi">Window end.</param>
    public static LossClassification Classify(
        TriangleBvh bvh, int[] parents, int triangle, Vector3 origin, Vector3 dir, float lo, float hi)
    {
        ArgumentNullException.ThrowIfNull(bvh);
        ArgumentNullException.ThrowIfNull(parents);
        List<int> path = [];
        int lane = bvh.LaneOfTriangle(triangle);
        while (lane >= 0)
        {
            path.Add(lane);
            lane = parents[lane / TriangleBvh.Width];
        }

        path.Reverse();
        for (int i = 0; i < path.Count; i++)
        {
            if (bvh.LanePasses(path[i], origin, dir, lo, hi))
            {
                continue;
            }

            (Vector3 min, Vector3 max) = bvh.LaneBounds(path[i]);
            double tNear = lo, tFar = hi;
            Axis(min.X, max.X, origin.X, dir.X, ref tNear, ref tFar);
            Axis(min.Y, max.Y, origin.Y, dir.Y, ref tNear, ref tFar);
            Axis(min.Z, max.Z, origin.Z, dir.Z, ref tNear, ref tFar);
            return new LossClassification(path[i], tFar - tNear, i + 1);
        }

        return new LossClassification(-1, double.NaN, path.Count);
    }

    // One axis of a double-precision slab test. A zero direction component constrains the window
    // only through whether the origin lies inside the slab.
    private static void Axis(float min, float max, float o, float d, ref double tNear, ref double tFar)
    {
        if (d == 0f)
        {
            if (o < min || o > max)
            {
                tNear = double.PositiveInfinity;
            }

            return;
        }

        double inv = 1.0 / d;
        double t0 = (min - (double)o) * inv;
        double t1 = (max - (double)o) * inv;
        if (t0 > t1)
        {
            (t0, t1) = (t1, t0);
        }

        tNear = Math.Max(tNear, t0);
        tFar = Math.Min(tFar, t1);
    }

}
