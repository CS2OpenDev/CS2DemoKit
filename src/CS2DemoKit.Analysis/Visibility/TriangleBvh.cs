#region

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

#endregion

namespace CS2DemoKit.Analysis.Visibility;

/// <summary>
///     A bounding-volume hierarchy over a world-collision triangle soup, built once and queried per ray.
///     Binned surface-area-heuristic build, collapsed into eight-wide nodes. Two query modes:
///     <see cref="AnyHit(Vector3, Vector3, float, float)" /> (early-exit occlusion, the hot path for
///     line-of-sight) and <see cref="NearestHit(Vector3, Vector3, float, float, out float)" /> (closest surface, used by the ray-down frame gate).
///     Directions are <b>unit vectors</b> and <c>t</c> is in <b>world units</b>, so hit distances read
///     directly. Pure geometry; parser-blind; allocation-free per query (stackalloc traversal stack).
///     <para>
///         Layout. A node is eight lanes, each a child box and a child reference. The boxes are
///         stored structure-of-arrays: six float arrays (minimum and maximum per axis) in which node
///         <c>n</c>'s eight lanes are the eight consecutive entries from <c>n * 8</c>, so a SIMD tier
///         can load one node's boxes as one register per array. A lane's reference is a node index
///         (an inner child), a leaf encoding (a run of triangle slots) or <see cref="EmptyLane" />.
///         Leaf triangles live contiguously in <see cref="_tri" /> as (vertex, edge, edge), in the
///         order the build left them, so a leaf is tested with sequential reads and no indirection.
///     </para>
///     <para>
///         Empty lanes carry an inverted box (positive infinity as minimum, negative infinity as
///         maximum). The slab test picks each axis's near face by the sign of the ray's inverse
///         direction, without a min/max swap, and the inverted box therefore fails it for every ray:
///         the entry time is positive infinity and the exit time negative infinity whichever way the
///         ray points. On a real box the sign-selected form takes exactly the two products the swap
///         form takes and differs from it in one verdict class only: a direction component of
///         negative zero (inverse negative infinity) with the origin exactly on that axis's minimum
///         face. There the product for that face is NaN and the swap form, seeing NaN against
///         negative infinity, keeps them in place and collapses the window; the selected form puts
///         the NaN on the far side, where the compare-select ignores it, and admits the box, which
///         is what the same ray with positive zero gets from either form. The swap form was wrong
///         there, and <c>TriangleBvh8Tests</c> pins the flip against the oracle. The engine's
///         decoded coordinates do not produce that input, but the arithmetic that forms a direction
///         can (<c>-0.0f - +0.0f</c> is negative zero), so it is pinned rather than assumed away. The
///         swap form would also accept the inverted box for every ray and dereference the empty
///         lane's reference; <c>TriangleBvh8Tests</c> mutates both ways and watches the traversal
///         fail. What the inverted box does not survive is a ray that is not finite: a NaN
///         component makes every comparison false, and a direction infinite on all three axes makes
///         every product NaN, so in either case no axis constrains the window and every lane is
///         admitted, empty ones included. Both traversals reject a non-finite ray up front; the
///         binary tree answered false on the same input after walking its whole tree.
///     </para>
///     <para>
///         Lane order. Lanes are assigned by octant: a child sits in the lane whose bits say, per
///         axis, whether the child's centre lies on the positive side of the node's centre. A ray
///         whose inverse direction is non-negative on every axis enters from the all-negative corner,
///         so its nearest child is lane 0; for a general ray the nearest lane is the complement of its
///         positive-direction mask. The traversal pushes lanes far to near so the near one pops
///         first. That order changes only the work: <see cref="AnyHit(Vector3, Vector3, float, float)" />
///         is true iff some reached leaf holds a hit, whichever leaf is reached first.
///     </para>
///     <para>
///         Tiers. The slab test over a node's eight lanes is a policy type (<see cref="ISlab" />)
///         the traversal is specialised on: <see cref="ScalarSlab" /> tests one lane at a time,
///         <see cref="Vector128Slab" /> a node as two halves and <see cref="Vector256Slab" /> a node
///         as one register per array. All three run literally the same IEEE operations in the same
///         order (subtract, multiply, compare, select, compare), which is why the scalar tier is
///         written as explicit compare-select rather than <c>Math.Min</c> and the vector tiers as
///         <c>ConditionalSelect</c> over a comparison rather than <c>Vector256.Min</c>: the
///         library minimum propagates a NaN and orders the signed zeros, and the traversal's verdict
///         on an origin-on-face ray depends on neither. The scalar tier stops at the first axis that
///         closes the window and the vector tiers evaluate all three, which cannot change a verdict:
///         the window start only rises and the end only falls, so a window closed after one axis is
///         still closed after three, and a lane that passes has run every axis on every tier.
///         <c>TraversalTierTests</c> holds every tier to the scalar one bit for bit over a
///         million-ray real-bake corpus. No tier shuffles lanes: which face is near is decided per
///         axis by the ray, uniformly across a node, so it is a whole-register select between the
///         minimum and maximum arrays, never a cross-lane permute; anyone adding one should know
///         that <c>Vector256.Shuffle</c> permutes across the full register while
///         <c>Avx2.Shuffle</c> permutes within each 128-bit half.
///     </para>
/// </summary>
public sealed class TriangleBvh
{
    /// <summary>Lanes per node.</summary>
    internal const int Width = 8;

    /// <summary>
    ///     A lane holding nothing. Its box is inverted, so no ray ever reads this reference. It is
    ///     also, by the leaf encoding below, the leaf of zero slots starting at zero, so a stray -1
    ///     that did reach the stack would pop as a leaf loop of no iterations rather than as node
    ///     index -1; that is a net under the guarantee, not the guarantee, and
    ///     <c>TriangleBvh8Tests</c> pins it so a change to the encoding cannot remove it silently.
    /// </summary>
    internal const int EmptyLane = -1;

    // A leaf reference is the bitwise complement of (count << StartBits | start), which is a negative
    // number other than -1 for every count of at least one. Start addresses 2^26 slots.
    private const int StartBits = 26;
    private const int StartMask = (1 << StartBits) - 1;
    private const int MaxSlots = 1 << StartBits;

    // Build parameters. A leaf holds at most MaxLeaf triangles; the SAH decides below that. Binned
    // on all three axes with Bins bins per axis. Past MaxBinaryDepth the build halves ranges by
    // index instead of by cost, so a pathological soup cannot make the tree, and with it the
    // traversal stack, arbitrarily deep.
    private const int MaxLeaf = 8;
    private const int Bins = 16;
    private const int MaxBinaryDepth = 64;
    private const float NodeCost = 1f;

    private readonly int[] _child; // nodeCount * 8, see the class doc for the encoding
    private readonly int[] _laneOfSlot; // slot -> lane index (node * 8 + lane) of the leaf holding it
    private readonly float[] _maxX;
    private readonly float[] _maxY;
    private readonly float[] _maxZ;
    private readonly float[] _minX; // nodeCount * 8, lane boxes
    private readonly float[] _minY;
    private readonly float[] _minZ;
    private readonly int[] _perm; // slot -> caller-facing triangle index
    private readonly int[] _slotOf; // caller-facing triangle index -> slot
    private readonly float[] _tri; // 9 floats per slot: vertex a, edge b - a, edge c - a

    private TriangleBvh(
        float[] minX, float[] minY, float[] minZ, float[] maxX, float[] maxY, float[] maxZ, int[] child,
        float[] tri, int[] perm, int[] slotOf, int[] laneOfSlot, int nodeCount, int stackCapacity, int depth,
        Vector3 min, Vector3 max, int triangleCount, int peakBuildWorkers)
    {
        PeakBuildWorkers = peakBuildWorkers;
        _minX = minX;
        _minY = minY;
        _minZ = minZ;
        _maxX = maxX;
        _maxY = maxY;
        _maxZ = maxZ;
        _child = child;
        _tri = tri;
        _perm = perm;
        _slotOf = slotOf;
        _laneOfSlot = laneOfSlot;
        NodeCount = nodeCount;
        StackCapacity = stackCapacity;
        Depth = depth;
        Min = min;
        Max = max;
        TriangleCount = triangleCount;
    }

    /// <summary>Number of triangles the tree was built over.</summary>
    public int TriangleCount { get; }

    /// <summary>Lower corner of the bounds of every triangle, or zero for an empty mesh.</summary>
    public Vector3 Min { get; }

    /// <summary>Upper corner of the bounds of every triangle, or zero for an empty mesh.</summary>
    public Vector3 Max { get; }

    /// <summary>Eight-wide nodes in the tree; zero for an empty mesh.</summary>
    internal int NodeCount { get; }

    /// <summary>Longest root-to-leaf path in nodes; zero for an empty mesh.</summary>
    internal int Depth { get; }

    /// <summary>
    ///     Traversal stack entries a query needs, computed from the tree: one plus, along the deepest
    ///     path, the number of sibling lanes left waiting at each node. A query allocates exactly this.
    /// </summary>
    internal int StackCapacity { get; }

    /// <summary>
    ///     The most threads the build was observed running on at once: one for a build that never
    ///     forked, and never more than the degree it was given. The bench prints it and
    ///     <c>TriangleBvhParallelBuildTests</c> holds it to the degree.
    /// </summary>
    internal int PeakBuildWorkers { get; }

    /// <summary>
    ///     Builds the BVH over <paramref name="count" /> triangles packed as 9 floats each in
    ///     <paramref name="v" />, using up to one thread per processor. The tree is the same one
    ///     <see cref="Build(float[], int, int)" /> gives at every degree of parallelism.
    /// </summary>
    /// <param name="v">Triangle soup, 9 floats per triangle (three vertices), not retained.</param>
    /// <param name="count">Number of triangles packed in <paramref name="v" />.</param>
    public static TriangleBvh Build(float[] v, int count) => Build(v, count, 0);

    /// <summary>
    ///     Builds the BVH over <paramref name="count" /> triangles packed as 9 floats each in
    ///     <paramref name="v" /> on at most <paramref name="maxDegreeOfParallelism" /> threads. The
    ///     tree does not depend on the degree: the build is deterministic by construction (see the
    ///     builder), and <c>TriangleBvhParallelBuildTests</c> holds every degree to the digests
    ///     <c>TriangleBvhBuildIdentityTests</c> pins per bake. One means the calling thread alone
    ///     (no loop is entered, not even with one worker), which is what a caller that is already
    ///     saturating the pool should pass; the app's engine cache builds on a pool thread with the
    ///     default, and a build overlapping another parallel loop there shares the pool with it
    ///     rather than waiting for it, as any two such loops do.
    ///     <para>
    ///         A throw inside the build reaches the caller as the original exception, not as the
    ///         <see cref="AggregateException" /> a worker's throw is delivered in, and leaves
    ///         nothing behind: the builder is private to the call and the tree is constructed only
    ///         at the end. Only several distinct failures at once arrive aggregated.
    ///     </para>
    /// </summary>
    /// <param name="v">Triangle soup, 9 floats per triangle (three vertices), not retained.</param>
    /// <param name="count">Number of triangles packed in <paramref name="v" />.</param>
    /// <param name="maxDegreeOfParallelism">Threads the build may use; zero or negative means <see cref="Environment.ProcessorCount" />.</param>
    public static TriangleBvh Build(float[] v, int count, int maxDegreeOfParallelism)
    {
        ArgumentNullException.ThrowIfNull(v);
        if (count < 0 || (long)count * 9 > v.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(count), count, "the soup does not hold that many triangles");
        }

        if (count > MaxSlots)
        {
            throw new ArgumentOutOfRangeException(nameof(count), count, $"leaf references address at most {MaxSlots} triangles");
        }

        if (count == 0)
        {
            return new TriangleBvh([], [], [], [], [], [], [], [], [], [], [], 0, 0, 0, Vector3.Zero, Vector3.Zero, 0, 1);
        }

        int parallelism = maxDegreeOfParallelism > 0 ? maxDegreeOfParallelism : Environment.ProcessorCount;
        try
        {
            return new Builder(v, count, parallelism).Finish();
        }
        catch (AggregateException e)
        {
            AggregateException flat = e.Flatten();
            if (flat.InnerExceptions.Count == 1)
            {
                ExceptionDispatchInfo.Throw(flat.InnerExceptions[0]);
            }

            throw flat;
        }
    }

    /// <summary>
    ///     True iff some triangle is hit by the ray <c>origin + t * dir</c> for <c>t</c> in
    ///     <c>(eps, tMax - eps)</c>. Early-exits on the first hit: the occlusion test for line-of-sight.
    /// </summary>
    /// <param name="origin">Ray origin.</param>
    /// <param name="dir">Unit ray direction.</param>
    /// <param name="tMax">Segment length in world units.</param>
    /// <param name="eps">Endpoint exclusion in world units.</param>
    public bool AnyHit(Vector3 origin, Vector3 dir, float tMax, float eps)
    {
        int hint = -1;
        NoStats stats = default;
        return AnyHitCore(origin, dir, tMax, eps, ref hint, out _, ref stats);
    }

    /// <summary>
    ///     <see cref="AnyHit(Vector3, Vector3, float, float)" /> with a last-occluder hint: the
    ///     triangle at index <paramref name="hint" /> is tested before the tree is entered, and
    ///     whichever triangle ends up blocking the ray is written back to it. Pass -1 (or any index
    ///     outside <c>0..TriangleCount-1</c>) for no hint. Indices are the caller's: positions in the
    ///     soup the tree was built from, never the tree's internal slot order.
    ///     <para>
    ///         The verdict is identical to the plain overload's on every ray, by construction and
    ///         not only in practice. The hinted test is the box of the leaf lane holding the hinted
    ///         triangle through <c>LaneHit</c> and then the triangle through <c>RayTriangle</c>, with
    ///         the same <c>inv</c>, the same <c>lo</c> and the same <c>hi</c> the traversal below
    ///         uses. The traversal reaches a leaf lane iff that lane's box and the box of every lane
    ///         above it pass the slab test (the root itself has no box; it is entered
    ///         unconditionally), and every box above contains the leaf box, so a passing leaf means
    ///         passing ancestors: the slab test is monotone in the bounds for every inverse-direction
    ///         component, finite or infinite. A wider face moves its crossing outward, and a face on
    ///         the origin's plane produces a NaN the compare-select ignores, leaving the window as it
    ///         was, whichever sign the infinity carries. A hinted triangle that hits inside a passing
    ///         leaf is therefore one the traversal would have tested with the identical predicate, so
    ///         a hinted true is a traversal true; a hinted miss runs the traversal unchanged.
    ///     </para>
    ///     <para>
    ///         The leaf test is load-bearing, not decoration. The slab crossing time for a box face
    ///         and Moller-Trumbore's hit time for a triangle on that face come from different float
    ///         arithmetic and can land a few ulp apart, so the traversal rejects a lane whose
    ///         triangle the ray really hits when the segment's far limit falls in that gap. Testing
    ///         the triangle alone would answer occluded there: the geometrically right answer, but
    ///         not the traversal's, and every fixture pins the traversal.
    ///         <c>OccluderHintTests.HintOnATriangleTheTraversalWouldMiss_TakesTheTraversal</c>
    ///         constructs a ray of that class. Every tier keeps this leaf test in its own slab
    ///         arithmetic: the hinted lane goes through the same <see cref="ISlab" /> policy the
    ///         traversal below is specialised on, so the identity holds per tier by construction
    ///         and not only because the tiers agree.
    ///     </para>
    ///     <para>
    ///         What it is for: nearly every line-of-sight ray in a demo is occluded, and consecutive
    ///         samples of the same sightline are usually stopped by the same surface, so the hinted
    ///         test answers most rays for the price of one triangle test instead of a traversal. On a
    ///         clear ray the hint is left alone: the surface that blocked the sightline a moment ago
    ///         is still the likeliest one to block it next.
    ///     </para>
    /// </summary>
    /// <param name="origin">Ray origin.</param>
    /// <param name="dir">Unit ray direction.</param>
    /// <param name="tMax">Segment length in world units.</param>
    /// <param name="eps">Endpoint exclusion in world units.</param>
    /// <param name="hint">
    ///     In: a triangle index to try first, or -1. Out: the index of the triangle that blocked the
    ///     ray when the result is true, else unchanged.
    /// </param>
    public bool AnyHit(Vector3 origin, Vector3 dir, float tMax, float eps, ref int hint) =>
        AnyHit(origin, dir, tMax, eps, ref hint, out _);

    // The one traversal body behind every AnyHit overload. shortCircuited reports whether the
    // hinted triangle decided the ray, for the ray budget counters; nothing else reads it.
    internal bool AnyHit(Vector3 origin, Vector3 dir, float tMax, float eps, ref int hint, out bool shortCircuited)
    {
        NoStats stats = default;
        return AnyHitCore(origin, dir, tMax, eps, ref hint, out shortCircuited, ref stats);
    }

    /// <summary>
    ///     The hinted <see cref="AnyHit(Vector3, Vector3, float, float, ref int)" /> with a work
    ///     report threaded through. Same verdict, same hint; only <paramref name="stats" /> differs.
    /// </summary>
    /// <typeparam name="TStats">The reporting policy; <see cref="NoStats" /> is what production runs.</typeparam>
    /// <param name="origin">Ray origin.</param>
    /// <param name="dir">Unit ray direction.</param>
    /// <param name="tMax">Segment length in world units.</param>
    /// <param name="eps">Endpoint exclusion in world units.</param>
    /// <param name="hint">The last-occluder hint, in and out.</param>
    /// <param name="shortCircuited">Whether the hint decided the ray without a traversal.</param>
    /// <param name="stats">Receives the traversal's work.</param>
    internal bool AnyHit<TStats>(
        Vector3 origin, Vector3 dir, float tMax, float eps, ref int hint, out bool shortCircuited, ref TStats stats)
        where TStats : struct, ITraversalStats =>
        AnyHitCore(origin, dir, tMax, eps, ref hint, out shortCircuited, ref stats);

    /// <summary>
    ///     Nearest triangle hit along <c>origin + t * dir</c> for <c>t</c> in <c>(eps, tMax)</c>. Returns
    ///     the smallest such <c>t</c>, or false if nothing is hit. Used by the coordinate-frame ray-down gate.
    /// </summary>
    /// <param name="origin">Ray origin.</param>
    /// <param name="dir">Unit ray direction.</param>
    /// <param name="tMax">Far limit in world units.</param>
    /// <param name="eps">Near exclusion in world units.</param>
    /// <param name="distance">The nearest hit's <c>t</c>, or <see cref="float.MaxValue" /> on a miss.</param>
    public bool NearestHit(Vector3 origin, Vector3 dir, float tMax, float eps, out float distance)
    {
        NoStats stats = default;
        return NearestHitCore(origin, dir, tMax, eps, out distance, ref stats);
    }

    /// <summary><see cref="NearestHit(Vector3, Vector3, float, float, out float)" /> with a work report threaded through.</summary>
    /// <typeparam name="TStats">The reporting policy.</typeparam>
    /// <param name="origin">Ray origin.</param>
    /// <param name="dir">Unit ray direction.</param>
    /// <param name="tMax">Far limit in world units.</param>
    /// <param name="eps">Near exclusion in world units.</param>
    /// <param name="distance">The nearest hit's <c>t</c>, or <see cref="float.MaxValue" /> on a miss.</param>
    /// <param name="stats">Receives the traversal's work.</param>
    internal bool NearestHit<TStats>(
        Vector3 origin, Vector3 dir, float tMax, float eps, out float distance, ref TStats stats)
        where TStats : struct, ITraversalStats =>
        NearestHitCore(origin, dir, tMax, eps, out distance, ref stats);

    /// <summary>
    ///     The tiers this machine accelerates, the scalar tier always first. A vector tier is listed
    ///     only where its register width is hardware accelerated; the software fallback the runtime
    ///     would otherwise supply computes the same answers but is not a tier worth running.
    /// </summary>
    internal static ReadOnlySpan<TraversalTier> AvailableTiers => Tiers;

    /// <summary>
    ///     The tier the plain entry points run: the widest one this machine accelerates. The
    ///     dispatch tests the same JIT-time constants, so production pays no branch for it.
    /// </summary>
    internal static TraversalTier DefaultTier =>
        Vector256.IsHardwareAccelerated ? TraversalTier.Vector256
        : Vector128.IsHardwareAccelerated ? TraversalTier.Vector128
        : TraversalTier.Scalar;

    private static readonly TraversalTier[] Tiers =
        Vector256.IsHardwareAccelerated ? [TraversalTier.Scalar, TraversalTier.Vector128, TraversalTier.Vector256]
        : Vector128.IsHardwareAccelerated ? [TraversalTier.Scalar, TraversalTier.Vector128]
        : [TraversalTier.Scalar];

    /// <summary>The hinted <see cref="AnyHit(Vector3, Vector3, float, float, ref int)" /> on a named tier.</summary>
    /// <param name="origin">Ray origin.</param>
    /// <param name="dir">Unit ray direction.</param>
    /// <param name="tMax">Segment length in world units.</param>
    /// <param name="eps">Endpoint exclusion in world units.</param>
    /// <param name="hint">The last-occluder hint, in and out.</param>
    /// <param name="shortCircuited">Whether the hint decided the ray without a traversal.</param>
    /// <param name="tier">The traversal implementation to run.</param>
    internal bool AnyHit(
        Vector3 origin, Vector3 dir, float tMax, float eps, ref int hint, out bool shortCircuited, TraversalTier tier)
    {
        NoStats stats = default;
        return AnyHit(origin, dir, tMax, eps, ref hint, out shortCircuited, ref stats, tier);
    }

    /// <summary>The hinted <see cref="AnyHit(Vector3, Vector3, float, float, ref int)" /> on a named tier, with a work report.</summary>
    /// <typeparam name="TStats">The reporting policy.</typeparam>
    /// <param name="origin">Ray origin.</param>
    /// <param name="dir">Unit ray direction.</param>
    /// <param name="tMax">Segment length in world units.</param>
    /// <param name="eps">Endpoint exclusion in world units.</param>
    /// <param name="hint">The last-occluder hint, in and out.</param>
    /// <param name="shortCircuited">Whether the hint decided the ray without a traversal.</param>
    /// <param name="stats">Receives the traversal's work.</param>
    /// <param name="tier">The traversal implementation to run.</param>
    internal bool AnyHit<TStats>(
        Vector3 origin, Vector3 dir, float tMax, float eps, ref int hint, out bool shortCircuited, ref TStats stats,
        TraversalTier tier)
        where TStats : struct, ITraversalStats
    {
        switch (tier)
        {
            case TraversalTier.Scalar:
                return AnyHitCore<TStats, ScalarSlab>(origin, dir, tMax, eps, ref hint, out shortCircuited, ref stats);
            case TraversalTier.Vector128:
                return AnyHitCore<TStats, Vector128Slab>(origin, dir, tMax, eps, ref hint, out shortCircuited, ref stats);
            case TraversalTier.Vector256:
                return AnyHitCore<TStats, Vector256Slab>(origin, dir, tMax, eps, ref hint, out shortCircuited, ref stats);
            default:
                throw new ArgumentOutOfRangeException(nameof(tier), tier, "not a traversal tier");
        }
    }

    /// <summary><see cref="NearestHit(Vector3, Vector3, float, float, out float)" /> on a named tier.</summary>
    /// <param name="origin">Ray origin.</param>
    /// <param name="dir">Unit ray direction.</param>
    /// <param name="tMax">Far limit in world units.</param>
    /// <param name="eps">Near exclusion in world units.</param>
    /// <param name="distance">The nearest hit's <c>t</c>, or <see cref="float.MaxValue" /> on a miss.</param>
    /// <param name="tier">The traversal implementation to run.</param>
    internal bool NearestHit(Vector3 origin, Vector3 dir, float tMax, float eps, out float distance, TraversalTier tier)
    {
        NoStats stats = default;
        return NearestHit(origin, dir, tMax, eps, out distance, ref stats, tier);
    }

    /// <summary><see cref="NearestHit(Vector3, Vector3, float, float, out float)" /> on a named tier, with a work report.</summary>
    /// <typeparam name="TStats">The reporting policy.</typeparam>
    /// <param name="origin">Ray origin.</param>
    /// <param name="dir">Unit ray direction.</param>
    /// <param name="tMax">Far limit in world units.</param>
    /// <param name="eps">Near exclusion in world units.</param>
    /// <param name="distance">The nearest hit's <c>t</c>, or <see cref="float.MaxValue" /> on a miss.</param>
    /// <param name="stats">Receives the traversal's work.</param>
    /// <param name="tier">The traversal implementation to run.</param>
    internal bool NearestHit<TStats>(
        Vector3 origin, Vector3 dir, float tMax, float eps, out float distance, ref TStats stats, TraversalTier tier)
        where TStats : struct, ITraversalStats
    {
        switch (tier)
        {
            case TraversalTier.Scalar:
                return NearestHitCore<TStats, ScalarSlab>(origin, dir, tMax, eps, out distance, ref stats);
            case TraversalTier.Vector128:
                return NearestHitCore<TStats, Vector128Slab>(origin, dir, tMax, eps, out distance, ref stats);
            case TraversalTier.Vector256:
                return NearestHitCore<TStats, Vector256Slab>(origin, dir, tMax, eps, out distance, ref stats);
            default:
                throw new ArgumentOutOfRangeException(nameof(tier), tier, "not a traversal tier");
        }
    }

    /// <summary>The lane index (<c>node * 8 + lane</c>) of the leaf holding <paramref name="triangle" />.</summary>
    /// <param name="triangle">Caller-facing triangle index.</param>
    internal int LaneOfTriangle(int triangle) => _laneOfSlot[_slotOf[triangle]];

    /// <summary>The box stored in lane <paramref name="laneIndex" />; inverted for an empty lane.</summary>
    /// <param name="laneIndex"><c>node * 8 + lane</c>.</param>
    internal (Vector3 Min, Vector3 Max) LaneBounds(int laneIndex) =>
        (new Vector3(_minX[laneIndex], _minY[laneIndex], _minZ[laneIndex]),
            new Vector3(_maxX[laneIndex], _maxY[laneIndex], _maxZ[laneIndex]));

    /// <summary>
    ///     Whether the traversal's slab test admits lane <paramref name="laneIndex" /> for this ray
    ///     over the window <c>[lo, hi]</c>, with the inverse direction and sign selection formed
    ///     exactly as the traversal forms them, through the scalar tier's lane test. For the
    ///     differential harness, which needs to know which box on a path rejected a ray the oracle
    ///     says hits; the tiers agree bit for bit, so the scalar answer is every tier's answer.
    /// </summary>
    /// <param name="laneIndex"><c>node * 8 + lane</c>.</param>
    /// <param name="origin">Ray origin.</param>
    /// <param name="dir">Unit ray direction.</param>
    /// <param name="lo">Window start.</param>
    /// <param name="hi">Window end.</param>
    internal bool LanePasses(int laneIndex, Vector3 origin, Vector3 dir, float lo, float hi)
    {
        Vector3 inv = new(1f / dir.X, 1f / dir.Y, 1f / dir.Z);
        return LaneHit(laneIndex, origin, inv, inv.X < 0f, inv.Y < 0f, inv.Z < 0f, lo, hi, out _);
    }

    /// <summary>The reference stored in lane <paramref name="laneIndex" />: a node index, a leaf encoding or <see cref="EmptyLane" />.</summary>
    /// <param name="laneIndex"><c>node * 8 + lane</c>.</param>
    internal int LaneChild(int laneIndex) => _child[laneIndex];

    /// <summary>Whether a lane reference is a leaf encoding.</summary>
    /// <param name="child">A lane reference.</param>
    internal static bool IsLeaf(int child) => child < EmptyLane;

    /// <summary>The slot run a leaf reference denotes.</summary>
    /// <param name="child">A leaf reference, per <see cref="IsLeaf" />.</param>
    internal static (int Start, int Count) DecodeLeaf(int child)
    {
        int x = ~child;
        return (x & StartMask, x >> StartBits);
    }

    /// <summary>The caller-facing index of the triangle in <paramref name="slot" />.</summary>
    /// <param name="slot">A leaf slot.</param>
    internal int TriangleOfSlot(int slot) => _perm[slot];

    private static int EncodeLeaf(int start, int count) => ~((count << StartBits) | start);

    /// <summary>
    ///     FNV-1a over everything a caller can observe about the tree, in a fixed order: node count,
    ///     depth, stack capacity, triangle count, the root bounds, then every lane's box bits and
    ///     child reference, then every slot's triangle and lane. The leaf storage is not hashed
    ///     because it is a pure function of the slot order and the soup. Two trees with the same
    ///     digest are the same tree, so a builder change that keeps the digest keeps every ray's
    ///     answer without a differential run; the bench's <c>build</c> verb prints it and the
    ///     identity tests pin it per bake.
    /// </summary>
    internal ulong StructuralDigest()
    {
        ulong h = 14695981039346656037UL;
        Mix(ref h, (uint)NodeCount);
        Mix(ref h, (uint)Depth);
        Mix(ref h, (uint)StackCapacity);
        Mix(ref h, (uint)TriangleCount);
        Mix(ref h, (uint)BitConverter.SingleToInt32Bits(Min.X));
        Mix(ref h, (uint)BitConverter.SingleToInt32Bits(Min.Y));
        Mix(ref h, (uint)BitConverter.SingleToInt32Bits(Min.Z));
        Mix(ref h, (uint)BitConverter.SingleToInt32Bits(Max.X));
        Mix(ref h, (uint)BitConverter.SingleToInt32Bits(Max.Y));
        Mix(ref h, (uint)BitConverter.SingleToInt32Bits(Max.Z));
        int lanes = NodeCount * Width;
        for (int lane = 0; lane < lanes; lane++)
        {
            Mix(ref h, (uint)BitConverter.SingleToInt32Bits(_minX[lane]));
            Mix(ref h, (uint)BitConverter.SingleToInt32Bits(_minY[lane]));
            Mix(ref h, (uint)BitConverter.SingleToInt32Bits(_minZ[lane]));
            Mix(ref h, (uint)BitConverter.SingleToInt32Bits(_maxX[lane]));
            Mix(ref h, (uint)BitConverter.SingleToInt32Bits(_maxY[lane]));
            Mix(ref h, (uint)BitConverter.SingleToInt32Bits(_maxZ[lane]));
            Mix(ref h, (uint)_child[lane]);
        }

        for (int slot = 0; slot < TriangleCount; slot++)
        {
            Mix(ref h, (uint)_perm[slot]);
            Mix(ref h, (uint)_laneOfSlot[slot]);
        }

        return h;

        static void Mix(ref ulong h, uint v)
        {
            h ^= v;
            h *= 1099511628211UL;
        }
    }

    // The plain entry points run the widest accelerated tier. IsHardwareAccelerated is a JIT-time
    // constant, so each of these compiles to a direct call of one specialisation.
    private bool AnyHitCore<TStats>(
        Vector3 origin, Vector3 dir, float tMax, float eps, ref int hint, out bool shortCircuited, ref TStats stats)
        where TStats : struct, ITraversalStats
    {
        if (Vector256.IsHardwareAccelerated)
        {
            return AnyHitCore<TStats, Vector256Slab>(origin, dir, tMax, eps, ref hint, out shortCircuited, ref stats);
        }

        if (Vector128.IsHardwareAccelerated)
        {
            return AnyHitCore<TStats, Vector128Slab>(origin, dir, tMax, eps, ref hint, out shortCircuited, ref stats);
        }

        return AnyHitCore<TStats, ScalarSlab>(origin, dir, tMax, eps, ref hint, out shortCircuited, ref stats);
    }

    private bool NearestHitCore<TStats>(
        Vector3 origin, Vector3 dir, float tMax, float eps, out float distance, ref TStats stats)
        where TStats : struct, ITraversalStats
    {
        if (Vector256.IsHardwareAccelerated)
        {
            return NearestHitCore<TStats, Vector256Slab>(origin, dir, tMax, eps, out distance, ref stats);
        }

        if (Vector128.IsHardwareAccelerated)
        {
            return NearestHitCore<TStats, Vector128Slab>(origin, dir, tMax, eps, out distance, ref stats);
        }

        return NearestHitCore<TStats, ScalarSlab>(origin, dir, tMax, eps, out distance, ref stats);
    }

    private bool AnyHitCore<TStats, TSlab>(
        Vector3 origin, Vector3 dir, float tMax, float eps, ref int hint, out bool shortCircuited, ref TStats stats)
        where TStats : struct, ITraversalStats
        where TSlab : struct, ISlab
    {
        shortCircuited = false;
        if (NodeCount == 0)
        {
            return false;
        }

        float lo = eps, hi = tMax - eps;
        if (hi <= lo || NotFinite(origin, dir))
        {
            return false;
        }

        Vector3 inv = new(1f / dir.X, 1f / dir.Y, 1f / dir.Z);
        bool negX = inv.X < 0f, negY = inv.Y < 0f, negZ = inv.Z < 0f;

        // The hinted test: the hinted triangle's leaf lane through this tier's slab test, then the
        // triangle through the same RayTriangle, against the same inv, lo and hi as the traversal
        // below. See the public overload's doc for why that, and only that, makes a hinted true a
        // traversal true.
        if ((uint)hint < (uint)TriangleCount)
        {
            int slot = _slotOf[hint];
            if (TSlab.Lane(this, _laneOfSlot[slot], origin, inv, negX, negY, negZ, lo, hi)
                && RayTriangle(origin, dir, slot, out float hintT) && hintT > lo && hintT < hi)
            {
                shortCircuited = true;
                return true;
            }
        }

        int nearLane = ((negX ? 0 : 1) | (negY ? 0 : 2) | (negZ ? 0 : 4)) ^ (Width - 1);
        Span<int> stack = stackalloc int[StackCapacity];
        int sp = 0;
        stack[sp++] = 0;
        while (sp > 0)
        {
            int r = stack[--sp];
            if (r >= 0)
            {
                stats.VisitNode(r);
                int node = r << 3;
                int passing = TSlab.Node(this, node, origin, inv, negX, negY, negZ, lo, hi);
                // Far lanes first, so the near lane is pushed last and pops first.
                for (int p = Width - 1; p >= 0; p--)
                {
                    int l = p ^ nearLane;
                    if ((passing & (1 << l)) == 0)
                    {
                        continue;
                    }

                    int lane = node + l;
                    int child = _child[lane];
                    Debug.Assert(child != EmptyLane, "an empty lane passed the slab test");
                    stats.EnterLane(lane);
                    stack[sp++] = child;
                }

                continue;
            }

            (int start, int count) = DecodeLeaf(r);
            int end = start + count;
            Debug.Assert(count > 0 && end <= _perm.Length, "a leaf reference points outside the slot array");
            for (int s = start; s < end; s++)
            {
                stats.TestSlot(s);
                if (RayTriangle(origin, dir, s, out float t) && t > lo && t < hi)
                {
                    hint = _perm[s];
                    return true;
                }
            }
        }

        return false;
    }

    private bool NearestHitCore<TStats, TSlab>(
        Vector3 origin, Vector3 dir, float tMax, float eps, out float distance, ref TStats stats)
        where TStats : struct, ITraversalStats
        where TSlab : struct, ISlab
    {
        distance = float.MaxValue;
        if (NodeCount == 0 || NotFinite(origin, dir))
        {
            return false;
        }

        Vector3 inv = new(1f / dir.X, 1f / dir.Y, 1f / dir.Z);
        bool negX = inv.X < 0f, negY = inv.Y < 0f, negZ = inv.Z < 0f;
        int nearLane = ((negX ? 0 : 1) | (negY ? 0 : 2) | (negZ ? 0 : 4)) ^ (Width - 1);
        bool hit = false;

        // Each entry carries the entry time its lane box was pushed with, so a lane pushed before
        // a nearer hit tightened the window is skipped on pop. Strictly greater: a lane whose entry
        // time equals the best distance is still visited, which is what the slab window (eps, best)
        // admits, so the pop-time check only ever repeats the push-time test against the tighter
        // bound and never rejects a lane the push-time test would have passed. entry[l] is read
        // only for a lane whose bit passed: for a failing lane the tiers leave different values
        // there (the scalar tier stops at the axis that closed the window, the vector tiers run all
        // three), so a read of a failing lane's entry would break tier identity. ISlab says so.
        Span<int> refs = stackalloc int[StackCapacity];
        Span<float> near = stackalloc float[StackCapacity];
        Span<float> entry = stackalloc float[Width];
        int sp = 0;
        refs[sp] = 0;
        near[sp++] = eps;
        while (sp > 0)
        {
            sp--;
            int r = refs[sp];
            float best = hit ? distance : tMax;
            if (near[sp] > best)
            {
                continue;
            }

            if (r >= 0)
            {
                stats.VisitNode(r);
                int node = r << 3;
                int passing = TSlab.Node(this, node, origin, inv, negX, negY, negZ, eps, best, entry);
                for (int p = Width - 1; p >= 0; p--)
                {
                    int l = p ^ nearLane;
                    if ((passing & (1 << l)) == 0)
                    {
                        continue;
                    }

                    int lane = node + l;
                    int child = _child[lane];
                    Debug.Assert(child != EmptyLane, "an empty lane passed the slab test");
                    stats.EnterLane(lane);
                    refs[sp] = child;
                    near[sp++] = entry[l];
                }

                continue;
            }

            (int start, int count) = DecodeLeaf(r);
            int end = start + count;
            Debug.Assert(count > 0 && end <= _perm.Length, "a leaf reference points outside the slot array");
            for (int s = start; s < end; s++)
            {
                stats.TestSlot(s);
                if (RayTriangle(origin, dir, s, out float t) && t > eps && t < tMax && t < distance)
                {
                    distance = t;
                    hit = true;
                }
            }
        }

        return hit;
    }

    // The ray has to be finite, on every component of both vectors, or the empty lanes stop
    // rejecting and the traversal pushes eight references onto a stack sized for the real lanes.
    //
    // A NaN component makes every slab comparison false, which the compare-select reads as "this
    // axis constrains nothing", so every lane passes. An infinite direction does the same thing one
    // axis at a time: inv is zero there, and the inverted box's two products are (+inf - o) * 0 and
    // (-inf - o) * 0, both NaN, so that axis constrains nothing either. One or two infinite
    // components still leave a finite axis to reject on, which is why this guard was once written
    // for NaN alone; a direction infinite on all three leaves none, and an all-infinite direction
    // is what Vector3.Normalize returns for any direction whose length-squared underflows to zero,
    // so VisibilityEngine.Raycast reaches it from an ordinary caller (TriangleBvh8Tests).
    //
    // Miss is the verdict such a ray would have had anyway, which is what makes rejecting it up
    // front a fix rather than a policy: a real lane's finite box is crossed at t = 0 on an infinite
    // axis, below the near exclusion, and RayTriangle yields NaN barycentrics and a NaN t for any
    // non-finite ray. The binary tree answered false on the same input after walking its whole tree.
    //
    // A non-finite origin never reached the overrun on its own: an infinite origin coordinate
    // against a finite face gives an infinite crossing, which closes the window rather than leaving
    // it open. It is rejected here with the rest because the verdict is the same miss and "the ray
    // must be finite" is one rule instead of a case analysis over which mixtures happen to survive.
    private static bool NotFinite(Vector3 origin, Vector3 dir) =>
        !float.IsFinite(origin.X) || !float.IsFinite(origin.Y) || !float.IsFinite(origin.Z)
        || !float.IsFinite(dir.X) || !float.IsFinite(dir.Y) || !float.IsFinite(dir.Z);

    // Ray/box slab test for one lane over the parameter window [lo, hi]. inv = 1/dir per component,
    // so a zero direction component gives an infinite inv. The near face on each axis is the minimum
    // when inv is non-negative and the maximum when it is negative, which is the same pair of
    // products the swap form computes on a real box, and which rejects an inverted (empty-lane) box
    // for every ray: see the class doc.
    //
    // When the origin lies exactly on a face of a zero-direction axis the product is inf * 0 = NaN.
    // The window updates are written as plain comparisons on purpose: `t0 > lo` is false when t0 is
    // NaN, so the incumbent survives and the axis constrains nothing, which is the right answer for
    // a ray that never moves along it. Math.Max and Math.Min would propagate the NaN and reject the
    // lane, sending a same-floor eye-to-eye ray through a wall (SlabDegeneracyTests). The vector
    // tiers write the same select as ConditionalSelect over the same comparison, so they match this
    // verdict for verdict; Vector256.Min would propagate the NaN exactly as Math.Min does.
    //
    // Written as explicit compare-select rather than Math.Min/Max so that every tier runs literally
    // the same IEEE operations. This is the scalar tier's lane test and the harness's LanePasses.
    // Inlined on request: the JIT declines it on size (nine arguments and an out), and as a call
    // it cost the scalar tier more than the arithmetic did (23 to 37 lane tests per ray).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool LaneHit(
        int lane, Vector3 o, Vector3 inv, bool negX, bool negY, bool negZ, float lo, float hi, out float tNear)
    {
        float nearX = negX ? _maxX[lane] : _minX[lane];
        float farX = negX ? _minX[lane] : _maxX[lane];
        float t0 = (nearX - o.X) * inv.X;
        float t1 = (farX - o.X) * inv.X;
        lo = t0 > lo ? t0 : lo;
        hi = t1 < hi ? t1 : hi;
        if (lo > hi)
        {
            tNear = lo;
            return false;
        }

        float nearY = negY ? _maxY[lane] : _minY[lane];
        float farY = negY ? _minY[lane] : _maxY[lane];
        t0 = (nearY - o.Y) * inv.Y;
        t1 = (farY - o.Y) * inv.Y;
        lo = t0 > lo ? t0 : lo;
        hi = t1 < hi ? t1 : hi;
        if (lo > hi)
        {
            tNear = lo;
            return false;
        }

        float nearZ = negZ ? _maxZ[lane] : _minZ[lane];
        float farZ = negZ ? _minZ[lane] : _maxZ[lane];
        t0 = (nearZ - o.Z) * inv.Z;
        t1 = (farZ - o.Z) * inv.Z;
        lo = t0 > lo ? t0 : lo;
        hi = t1 < hi ? t1 : hi;
        tNear = lo;
        return lo <= hi;
    }

    /// <summary>
    ///     The slab test over a node, as a policy the traversal is specialised on. Bit <c>l</c> of
    ///     a returned mask is set iff lane <c>l</c> of the node passes over the window
    ///     <c>[lo, hi]</c>. Every implementation must run the arithmetic of <see cref="LaneHit" />
    ///     operation for operation: the class doc says why and <c>TraversalTierTests</c> checks it.
    /// </summary>
    private interface ISlab
    {
        /// <summary>Whether one lane passes. The hinted leaf test.</summary>
        static abstract bool Lane(
            TriangleBvh bvh, int lane, Vector3 o, Vector3 inv, bool negX, bool negY, bool negZ, float lo, float hi);

        /// <summary>The mask of passing lanes for the node whose first lane is <paramref name="laneBase" />.</summary>
        static abstract int Node(
            TriangleBvh bvh, int laneBase, Vector3 o, Vector3 inv, bool negX, bool negY, bool negZ, float lo, float hi);

        /// <summary>
        ///     The mask of passing lanes, and each lane's entry time in <paramref name="tNear" />
        ///     (eight entries; only a passing lane's entry is meaningful).
        /// </summary>
        static abstract int Node(
            TriangleBvh bvh, int laneBase, Vector3 o, Vector3 inv, bool negX, bool negY, bool negZ, float lo, float hi,
            Span<float> tNear);
    }

    /// <summary>One lane at a time through <see cref="LaneHit" />. The reference tier.</summary>
    private struct ScalarSlab : ISlab
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool Lane(
            TriangleBvh bvh, int lane, Vector3 o, Vector3 inv, bool negX, bool negY, bool negZ, float lo, float hi) =>
            bvh.LaneHit(lane, o, inv, negX, negY, negZ, lo, hi, out _);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int Node(
            TriangleBvh bvh, int laneBase, Vector3 o, Vector3 inv, bool negX, bool negY, bool negZ, float lo, float hi)
        {
            int mask = 0;
            for (int l = 0; l < Width; l++)
            {
                if (bvh.LaneHit(laneBase + l, o, inv, negX, negY, negZ, lo, hi, out _))
                {
                    mask |= 1 << l;
                }
            }

            return mask;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int Node(
            TriangleBvh bvh, int laneBase, Vector3 o, Vector3 inv, bool negX, bool negY, bool negZ, float lo, float hi,
            Span<float> tNear)
        {
            int mask = 0;
            for (int l = 0; l < Width; l++)
            {
                if (bvh.LaneHit(laneBase + l, o, inv, negX, negY, negZ, lo, hi, out float t))
                {
                    mask |= 1 << l;
                    tNear[l] = t;
                }
            }

            return mask;
        }
    }

    /// <summary>
    ///     A node as two registers of four lanes. The two halves are independent dependency chains,
    ///     so an out-of-order core overlaps them. The near face on each axis is a whole-register
    ///     select between the minimum and maximum arrays, decided by the ray, so no lane moves.
    /// </summary>
    private struct Vector128Slab : ISlab
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool Lane(
            TriangleBvh bvh, int lane, Vector3 o, Vector3 inv, bool negX, bool negY, bool negZ, float lo, float hi)
        {
            int half = lane & ~(Vector128<float>.Count - 1);
            Vector128<float> vlo = Window(bvh, half, o, inv, negX, negY, negZ, lo, hi, out Vector128<float> vhi);
            uint mask = Vector128.LessThanOrEqual(vlo, vhi).ExtractMostSignificantBits();
            return ((mask >> (lane - half)) & 1u) != 0u;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int Node(
            TriangleBvh bvh, int laneBase, Vector3 o, Vector3 inv, bool negX, bool negY, bool negZ, float lo, float hi)
        {
            Vector128<float> lo0 = Window(bvh, laneBase, o, inv, negX, negY, negZ, lo, hi, out Vector128<float> hi0);
            Vector128<float> lo1 = Window(bvh, laneBase + Vector128<float>.Count, o, inv, negX, negY, negZ, lo, hi, out Vector128<float> hi1);
            uint m0 = Vector128.LessThanOrEqual(lo0, hi0).ExtractMostSignificantBits();
            uint m1 = Vector128.LessThanOrEqual(lo1, hi1).ExtractMostSignificantBits();
            return (int)(m0 | (m1 << Vector128<float>.Count));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int Node(
            TriangleBvh bvh, int laneBase, Vector3 o, Vector3 inv, bool negX, bool negY, bool negZ, float lo, float hi,
            Span<float> tNear)
        {
            Vector128<float> lo0 = Window(bvh, laneBase, o, inv, negX, negY, negZ, lo, hi, out Vector128<float> hi0);
            Vector128<float> lo1 = Window(bvh, laneBase + Vector128<float>.Count, o, inv, negX, negY, negZ, lo, hi, out Vector128<float> hi1);
            lo0.CopyTo(tNear);
            lo1.CopyTo(tNear[Vector128<float>.Count..]);
            uint m0 = Vector128.LessThanOrEqual(lo0, hi0).ExtractMostSignificantBits();
            uint m1 = Vector128.LessThanOrEqual(lo1, hi1).ExtractMostSignificantBits();
            return (int)(m0 | (m1 << Vector128<float>.Count));
        }

        // The window [lo, hi] after all three axes for the four lanes from laneBase.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector128<float> Window(
            TriangleBvh bvh, int laneBase, Vector3 o, Vector3 inv, bool negX, bool negY, bool negZ, float lo, float hi,
            out Vector128<float> vhi)
        {
            Debug.Assert(laneBase >= 0 && laneBase + Vector128<float>.Count <= bvh._minX.Length, "a lane load outside the node arrays");
            nuint at = (nuint)laneBase;
            Vector128<float> minX = Vector128.LoadUnsafe(ref MemoryMarshal.GetArrayDataReference(bvh._minX), at);
            Vector128<float> maxX = Vector128.LoadUnsafe(ref MemoryMarshal.GetArrayDataReference(bvh._maxX), at);
            Vector128<float> minY = Vector128.LoadUnsafe(ref MemoryMarshal.GetArrayDataReference(bvh._minY), at);
            Vector128<float> maxY = Vector128.LoadUnsafe(ref MemoryMarshal.GetArrayDataReference(bvh._maxY), at);
            Vector128<float> minZ = Vector128.LoadUnsafe(ref MemoryMarshal.GetArrayDataReference(bvh._minZ), at);
            Vector128<float> maxZ = Vector128.LoadUnsafe(ref MemoryMarshal.GetArrayDataReference(bvh._maxZ), at);
            Vector128<float> vlo = Vector128.Create(lo);
            vhi = Vector128.Create(hi);
            Axis(negX ? maxX : minX, negX ? minX : maxX, o.X, inv.X, ref vlo, ref vhi);
            Axis(negY ? maxY : minY, negY ? minY : maxY, o.Y, inv.Y, ref vlo, ref vhi);
            Axis(negZ ? maxZ : minZ, negZ ? minZ : maxZ, o.Z, inv.Z, ref vlo, ref vhi);
            return vlo;
        }

        // One axis of LaneHit, four lanes wide: the same two products and the same two selects.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void Axis(
            Vector128<float> near, Vector128<float> far, float o, float inv, ref Vector128<float> lo, ref Vector128<float> hi)
        {
            Vector128<float> vo = Vector128.Create(o);
            Vector128<float> vinv = Vector128.Create(inv);
            Vector128<float> t0 = (near - vo) * vinv;
            Vector128<float> t1 = (far - vo) * vinv;
            lo = Vector128.ConditionalSelect(Vector128.GreaterThan(t0, lo), t0, lo);
            hi = Vector128.ConditionalSelect(Vector128.LessThan(t1, hi), t1, hi);
        }
    }

    /// <summary>A node as one register of eight lanes. The same operations as <see cref="Vector128Slab" />, once.</summary>
    private struct Vector256Slab : ISlab
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool Lane(
            TriangleBvh bvh, int lane, Vector3 o, Vector3 inv, bool negX, bool negY, bool negZ, float lo, float hi)
        {
            int laneBase = lane & ~(Width - 1);
            return ((Node(bvh, laneBase, o, inv, negX, negY, negZ, lo, hi) >> (lane - laneBase)) & 1) != 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int Node(
            TriangleBvh bvh, int laneBase, Vector3 o, Vector3 inv, bool negX, bool negY, bool negZ, float lo, float hi)
        {
            Vector256<float> vlo = Window(bvh, laneBase, o, inv, negX, negY, negZ, lo, hi, out Vector256<float> vhi);
            return (int)Vector256.LessThanOrEqual(vlo, vhi).ExtractMostSignificantBits();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int Node(
            TriangleBvh bvh, int laneBase, Vector3 o, Vector3 inv, bool negX, bool negY, bool negZ, float lo, float hi,
            Span<float> tNear)
        {
            Vector256<float> vlo = Window(bvh, laneBase, o, inv, negX, negY, negZ, lo, hi, out Vector256<float> vhi);
            vlo.CopyTo(tNear);
            return (int)Vector256.LessThanOrEqual(vlo, vhi).ExtractMostSignificantBits();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector256<float> Window(
            TriangleBvh bvh, int laneBase, Vector3 o, Vector3 inv, bool negX, bool negY, bool negZ, float lo, float hi,
            out Vector256<float> vhi)
        {
            Debug.Assert(laneBase >= 0 && laneBase + Width <= bvh._minX.Length, "a lane load outside the node arrays");
            nuint at = (nuint)laneBase;
            Vector256<float> minX = Vector256.LoadUnsafe(ref MemoryMarshal.GetArrayDataReference(bvh._minX), at);
            Vector256<float> maxX = Vector256.LoadUnsafe(ref MemoryMarshal.GetArrayDataReference(bvh._maxX), at);
            Vector256<float> minY = Vector256.LoadUnsafe(ref MemoryMarshal.GetArrayDataReference(bvh._minY), at);
            Vector256<float> maxY = Vector256.LoadUnsafe(ref MemoryMarshal.GetArrayDataReference(bvh._maxY), at);
            Vector256<float> minZ = Vector256.LoadUnsafe(ref MemoryMarshal.GetArrayDataReference(bvh._minZ), at);
            Vector256<float> maxZ = Vector256.LoadUnsafe(ref MemoryMarshal.GetArrayDataReference(bvh._maxZ), at);
            Vector256<float> vlo = Vector256.Create(lo);
            vhi = Vector256.Create(hi);
            Axis(negX ? maxX : minX, negX ? minX : maxX, o.X, inv.X, ref vlo, ref vhi);
            Axis(negY ? maxY : minY, negY ? minY : maxY, o.Y, inv.Y, ref vlo, ref vhi);
            Axis(negZ ? maxZ : minZ, negZ ? minZ : maxZ, o.Z, inv.Z, ref vlo, ref vhi);
            return vlo;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void Axis(
            Vector256<float> near, Vector256<float> far, float o, float inv, ref Vector256<float> lo, ref Vector256<float> hi)
        {
            Vector256<float> vo = Vector256.Create(o);
            Vector256<float> vinv = Vector256.Create(inv);
            Vector256<float> t0 = (near - vo) * vinv;
            Vector256<float> t1 = (far - vo) * vinv;
            lo = Vector256.ConditionalSelect(Vector256.GreaterThan(t0, lo), t0, lo);
            hi = Vector256.ConditionalSelect(Vector256.LessThan(t1, hi), t1, hi);
        }
    }

    // Moller-Trumbore, general direction (need not be unit, but callers pass unit, so t is in world
    // units). The edges were formed at build time with the same subtraction the soup-order test used,
    // so the arithmetic from here on is unchanged.
    //
    // Known limitation, not watertight. Two triangles sharing an edge compute u and v from their own
    // e1 and e2 and their own f = 1/det, so the barycentric test on the shared edge is a different
    // float expression on each side. A ray crossing exactly at a seam can therefore be rejected by
    // both, and AnyHit then reports clear through a closed surface. The error is ulp-scale and the
    // fix is a different formulation (a watertight Woop-style test with a canonical edge ordering),
    // not a tolerance on this one. A differential run cannot observe it: BruteForceOracle.RayTriangle
    // is a verbatim copy of this body, so the oracle agrees on the crack and reports the same clear.
    private bool RayTriangle(Vector3 o, Vector3 d, int slot, out float t)
    {
        t = 0;
        int b = slot * 9;
        float[] tri = _tri;
        Vector3 a = new(tri[b], tri[b + 1], tri[b + 2]);
        Vector3 e1 = new(tri[b + 3], tri[b + 4], tri[b + 5]);
        Vector3 e2 = new(tri[b + 6], tri[b + 7], tri[b + 8]);

        Vector3 p = Vector3.Cross(d, e2);
        float det = Vector3.Dot(e1, p);
        if (det is > -1e-8f and < 1e-8f)
        {
            return false; // parallel
        }

        float f = 1f / det;
        Vector3 s = o - a;
        float u = f * Vector3.Dot(s, p);
        if (u is < 0f or > 1f)
        {
            return false;
        }

        Vector3 q = Vector3.Cross(s, e1);
        float vv = f * Vector3.Dot(d, q);
        if (vv < 0f || u + vv > 1f)
        {
            return false;
        }

        t = f * Vector3.Dot(e2, q);
        return true;
    }

    /// <summary>
    ///     The build, in one pass. Every binary node a binned-SAH build would have produced is
    ///     decided exactly once (a leaf, or a split of its slot range into two child ranges with
    ///     their bounds), but only when the eight-wide layout needs to know it, and the decision
    ///     lives in the frontier of the wide node being laid out rather than in a binary tree that is
    ///     built whole and collapsed afterwards. A wide node starts from its binary root's two
    ///     children, decides each, and repeatedly replaces the inner child with the largest box by
    ///     that child's own two (deciding those) until eight remain or none is inner; the eight are
    ///     laid into lanes by octant, and an inner one becomes a wide node of its own with its split
    ///     already decided. The SAH partitions an index array in place, so every leaf is a
    ///     contiguous run of slots.
    ///     <para>
    ///         A node's decision depends only on its own range and bounds, so deciding nodes in
    ///         layout order instead of depth-first gives the same decisions, and the lane arrays,
    ///         the slot order and the leaf contents come out identical to the two-pass build this
    ///         replaced: <c>TriangleBvhBuildIdentityTests</c> pins the digest per bake. What the fold
    ///         removes is the binary tree as a whole (a million forty-byte nodes on a large bake,
    ///         in a list that doubled twice) and the two walks that collapsed it.
    ///     </para>
    ///     <para>
    ///         Parallel, and deterministic by construction rather than by observation: the tree is
    ///         a pure function of the soup, the same on one thread as on thirty-two, and the identity
    ///         tests hold every degree of parallelism to the same digests. The work is cut into
    ///         <em>segments</em>, each built into private lane arrays numbered from zero in its own
    ///         preorder. A wide node whose range holds more than <see cref="DeferBelow" /> triangles
    ///         is a segment of one node (a <em>top</em> node): it is collapsed and its lanes laid out,
    ///         and each inner child becomes a pending segment of its own instead of a recursion. A
    ///         node at or below the threshold is a segment of the whole subtree under it, built
    ///         depth first exactly as the serial build builds it. The top nodes are built level by
    ///         level, every node of a level concurrently, and the subtree segments afterwards on the
    ///         pool, largest first; within a top node, a bin sweep over a range of at least twice
    ///         <see cref="SweepChunkMin" /> triangles runs as chunks of the range when the level has
    ///         fewer nodes than there are threads to give them.
    ///     </para>
    ///     <para>
    ///         Why the numbering cannot depend on scheduling: the serial build numbers nodes in
    ///         preorder, and a segment's nodes are contiguous in that order, so a walk over the
    ///         segment tree (a segment's own nodes, then its pending children in collapse order,
    ///         recursively) hands every segment the index its first node would have had, from the
    ///         segment sizes alone. The relocation copies each segment into the final arrays at
    ///         that index, adds it to the segment's inner references, points each pending lane at
    ///         its child segment's index and writes the slot-to-lane map from the leaves. The walk
    ///         runs after every segment is built and reads nothing a thread wrote out of order.
    ///     </para>
    ///     <para>
    ///         Why a chunked sweep is the serial sweep: each chunk folds its part of the range into
    ///         its own bins, and the node's bins are the fold of the chunk bins in chunk order. The
    ///         bins hold counts (exact) and minima and maxima, and on this runtime
    ///         <see cref="Vector3.Min" /> and <see cref="Vector3.Max" /> are the IEEE 754 minimum
    ///         and maximum: a NaN propagates from either side and negative zero orders below
    ///         positive zero, in optimised and unoptimised code alike. Each fold is then
    ///         associative and idempotent bit for bit, so folding chunk results equals folding the
    ///         triangles in index order. The one thing the runtime does not fix is which of two
    ///         NaNs a fold keeps (see <c>CanonicalNaN</c>), so the records carry a single NaN
    ///         payload and the question never arises. Nothing sums a float. The partition of a
    ///         range stays serial: its output order is the slot order, and it runs once per node
    ///         on the thread that owns the node. The per-triangle passes (records, root bounds,
    ///         leaf pack) are chunked the same way; the root bounds are the one other fold, and
    ///         the same argument covers it.
    ///     </para>
    ///     <para>
    ///         The minimum's NaN and signed-zero rules are a fact about the runtime (verified on
    ///         net10.0, the engine's target, in both JIT tiers), not a contract of the type on
    ///         every runtime: earlier vector minimums were the hardware <c>minps</c>, which returns
    ///         its second operand when either is NaN or both are zero, and under that rule a NaN
    ///         in the middle of a range resets the serial fold but not the chunk fold, so the two
    ///         trees differ. <c>TriangleBvhParallelBuildTests</c> pins the rule directly (NaN from
    ///         either side, both signed zeros) so a retarget fails there by name rather than as a
    ///         moved digest, and holds a soup of mixed NaN payloads to the tree of the same soup
    ///         with every NaN canonical.
    ///     </para>
    ///     <para>
    ///         Threads: every loop carries the degree as its cap, and the one nested pair (a
    ///         level's top nodes, each sweeping in chunks) stays within it because a level of
    ///         <c>n</c> nodes hands each node <c>degree / n</c> chunks, and a chunk loop has that
    ///         many iterations. Each body counts the thread it runs on in <see cref="PeakWorkers" />
    ///         (a chunk on the node's own thread counts once), which the bench prints and the
    ///         tests hold to the degree. With a degree of one no loop is entered at all: every
    ///         parallel site is skipped rather than run with one worker, so the calling thread is
    ///         the only thread the build touches, which is what a caller saturating the pool wants.
    ///     </para>
    ///     <para>
    ///         What one thread sees is the old serial build: with a degree of parallelism of one
    ///         (or a soup no larger than the threshold) the whole tree is one subtree segment built
    ///         on the calling thread, and the relocation is the copy into exact-size arrays that
    ///         the trim used to be. It allocates what the serial build allocated, to the array:
    ///         <c>TriangleBvhBuildAllocationTests</c> holds a build to that budget, after a closure
    ///         in the decision path was found costing a heap object per decision (see
    ///         <c>Segment.SweepChunked</c>).
    ///     </para>
    /// </summary>
    private sealed class Builder
    {
        // Wide nodes a subtree segment's lane arrays are first sized for, as a fraction of its
        // triangle count: the real bakes come out between one node per 6.7 and one per 8.6
        // triangles, so this covers them with slack and a denser soup grows the arrays by doubling.
        // Every segment is copied into exact-size arrays at the end, so the guess costs nothing
        // retained.
        private const int TrianglesPerWideNodeGuess = 6;

        // The hot methods below are compiled optimised on first call rather than tiered up. A
        // bake is built once per map load, so the build runs cold, and tier-0 instrumented code
        // is several times slower under thirty-two threads than serially (its counters share
        // cache lines): the bench measured the cold parallel build at two to three times its warm
        // time on the small bakes before this, and close to it after.

        // A wide node over more than this many triangles is a top node, built as a segment of one;
        // at or below it the whole subtree is one segment. Above it are the top few binary levels,
        // whose nodes are few and large; below it a real bake has hundreds of independent subtrees.
        private const int DeferBelow = 16384;

        // A chunk of a parallel bin sweep or per-triangle pass covers at least this many triangles,
        // so the fork is amortised over real work.
        private const int SweepChunkMin = 8192;
        private const int PassChunkMin = 32768;

        // A top node's lane whose child is a pending segment, until the relocation resolves it to
        // that segment's first node. Never a leaf encoding (a leaf holds at most MaxLeaf slots) and
        // never a node index; the relocation resolves from the pending list, not from this value.
        private const int PendingLane = int.MinValue;

        // How many bodies of this build the current thread is inside; see EnterBody.
        [ThreadStatic]
        private static int _nesting;

        private readonly int _count;
        private readonly int[] _laneOfSlot;
        private readonly ParallelOptions _options;
        private readonly int[] _order;
        private readonly int _parallelism;
        private readonly float[] _v;
        private int _activeWorkers;
        private int _peakWorkers;
        private TriangleRecord[] _rec; // released once every decision is made, before the final arrays

        public Builder(float[] v, int count, int parallelism)
        {
            _v = v;
            _count = count;
            _parallelism = parallelism;
            _options = new ParallelOptions { MaxDegreeOfParallelism = parallelism };
            _order = new int[count];
            _rec = new TriangleRecord[count];
            _laneOfSlot = new int[count];

            RunChunks(Chunks(count, PassChunkMin), count, (_, from, to) =>
            {
                for (int i = from; i < to; i++)
                {
                    _order[i] = i;
                    int b = i * 9;
                    Vector3 a = new(v[b], v[b + 1], v[b + 2]);
                    Vector3 bb = new(v[b + 3], v[b + 4], v[b + 5]);
                    Vector3 c = new(v[b + 6], v[b + 7], v[b + 8]);
                    Vector3 lo = CanonicalNaN(Vector3.Min(a, Vector3.Min(bb, c)));
                    Vector3 hi = CanonicalNaN(Vector3.Max(a, Vector3.Max(bb, c)));
                    // Binned on the box centre, which is what the SAH literature calls the centroid.
                    _rec[i] = new TriangleRecord(lo, hi, (lo + hi) * 0.5f);
                }
            });
        }

        // Every NaN a record carries is float.NaN, whatever payload the soup had. A fold of two
        // NaNs keeps one of them, and which one is not fixed by the runtime: the vector minimum
        // keeps the left payload in unoptimised code and the right in optimised code, and a sum
        // of two NaNs keeps whichever operand the JIT placed first (observed on net10.0 with a
        // harness over both tiers). A soup with two NaN payloads therefore built a different tree
        // in Debug than in Release, and could in principle build a different one once a method
        // tiered up. With one payload in play the choice is invisible: the records are where soup
        // floats enter the build's folds, and everything downstream (bins, bounds, centroids)
        // derives from them, so the tree is a function of the soup in every tier and at every
        // degree. The leaf storage keeps the soup's own floats; no verdict reads a payload from it.
        // The identity pins do not move: no bake carries a NaN, and the pinned soups' NaNs are
        // float.NaN already.
        private static Vector3 CanonicalNaN(Vector3 v)
        {
            Vector128<float> x = v.AsVector128();
            return Vector128.ConditionalSelect(Vector128.Equals(x, x), x, Vector128.Create(float.NaN)).AsVector3();
        }

        public TriangleBvh Finish()
        {
            // The root's bounds are the one scan the build does over a whole range; every child's
            // come out of its parent's bin sweep. Chunked partial folds, combined in chunk order.
            int chunks = Chunks(_count, PassChunkMin);
            Vector3[] partial = new Vector3[chunks * 4];
            RunChunks(chunks, _count, (c, from, to) =>
            {
                Vector3 pbmin = new(float.MaxValue), pbmax = new(float.MinValue);
                Vector3 pcmin = new(float.MaxValue), pcmax = new(float.MinValue);
                for (int i = from; i < to; i++)
                {
                    Fold(in _rec[i], ref pbmin, ref pbmax, ref pcmin, ref pcmax);
                }

                partial[c * 4] = pbmin;
                partial[(c * 4) + 1] = pbmax;
                partial[(c * 4) + 2] = pcmin;
                partial[(c * 4) + 3] = pcmax;
            });

            Vector3 bmin = new(float.MaxValue), bmax = new(float.MinValue);
            Vector3 cmin = new(float.MaxValue), cmax = new(float.MinValue);
            for (int c = 0; c < chunks; c++)
            {
                bmin = Vector3.Min(bmin, partial[c * 4]);
                bmax = Vector3.Max(bmax, partial[(c * 4) + 1]);
                cmin = Vector3.Min(cmin, partial[(c * 4) + 2]);
                cmax = Vector3.Max(cmax, partial[(c * 4) + 3]);
            }

            // The root segment: one node of the top when there is a pool to hand its children to,
            // otherwise the whole tree on this thread.
            bool top = _parallelism > 1 && _count > DeferBelow;
            Segment root = new(this, top, top ? _parallelism : 1, top ? 1 : Math.Max(1, _count / TrianglesPerWideNodeGuess));
            Decision rootDecision = root.Decide(new Range(0, _count, 0, bmin, bmax, cmin, cmax));
            root.BuildWide(in rootDecision, 0, 1);

            // The top, level by level: every node of a level concurrently, each with a share of the
            // threads for its sweeps. Subtree segments are only created here and built below.
            List<Segment> segments = [root];
            List<Segment> subtrees = [];
            List<Segment> level = [root];
            while (level.Count > 0)
            {
                List<Segment> next = [];
                foreach (Segment s in level)
                {
                    foreach (Pending p in s.Pending)
                    {
                        bool topChild = p.Root.Self.Count > DeferBelow;
                        p.Built = new Segment(this, topChild, 1, topChild ? 1 : Math.Max(1, p.Root.Self.Count / TrianglesPerWideNodeGuess));
                        segments.Add(p.Built);
                        (topChild ? next : subtrees).Add(p.Built);
                    }
                }

                if (next.Count > 0)
                {
                    // Each node's chunked sweeps get a share of the degree, and the shares sum to
                    // at most the degree, so the nested loops together stay within it.
                    int sweepChunks = Math.Max(1, _parallelism / next.Count);
                    Parallel.ForEach(next, _options, s =>
                    {
                        EnterBody();
                        try
                        {
                            s.BuildTop(sweepChunks);
                        }
                        finally
                        {
                            LeaveBody();
                        }
                    });
                }

                level = next;
            }

            if (subtrees.Count > 0)
            {
                // Largest first, one at a time per worker, so a few big subtrees do not trail the
                // rest; the order changes nothing but the wall-clock, since each is built into its
                // own arrays and placed by the walk below.
                subtrees.Sort((a, b) => b.Job.Root.Self.Count.CompareTo(a.Job.Root.Self.Count));
                Parallel.ForEach(Partitioner.Create(subtrees, EnumerablePartitionerOptions.NoBuffering), _options, s =>
                {
                    EnterBody();
                    try
                    {
                        s.BuildSubtree();
                    }
                    finally
                    {
                        LeaveBody();
                    }
                });
            }

            // Every decision is made, so the records are dead from here; nothing below reads them.
            // Dropping them before the final arrays exist keeps the build's live set at the
            // segment arrays plus the final ones, which is under the finished tree's own size, as
            // the serial build's was. (The bench's build --live flag is what sees this; allocation
            // and retained bytes do not move.)
            _rec = [];

            // The serial numbering, from the segment sizes alone: preorder over the segment tree.
            int nodeCount = 0, depth = 0, stackCapacity = 0;
            Place(root, ref nodeCount);
            foreach (Segment s in segments)
            {
                depth = Math.Max(depth, s.Depth);
                stackCapacity = Math.Max(stackCapacity, s.StackCapacity);
            }

            int lanes = nodeCount * Width;
            float[] minX = new float[lanes], minY = new float[lanes], minZ = new float[lanes];
            float[] maxX = new float[lanes], maxY = new float[lanes], maxZ = new float[lanes];
            int[] child = new int[lanes];
            if (segments.Count == 1)
            {
                // One segment (a degree of one, or a soup under the deferral size): no fork.
                Relocate(root, minX, minY, minZ, maxX, maxY, maxZ, child);
            }
            else
            {
                Parallel.ForEach(segments, _options, s =>
                {
                    EnterBody();
                    try
                    {
                        Relocate(s, minX, minY, minZ, maxX, maxY, maxZ, child);
                    }
                    finally
                    {
                        LeaveBody();
                    }
                });
            }

            // The segments are done with; dropping them before the leaf pack keeps that array
            // from sitting on top of them at the build's high-water mark.
            segments.Clear();
            subtrees.Clear();
            root = null!;

            // Leaf storage in slot order: vertex a and the two edges, formed exactly as the
            // soup-order triangle test formed them.
            float[] tri = new float[_count * 9];
            int[] slotOf = new int[_count];
            float[] v = _v;
            int[] order = _order;
            RunChunks(Chunks(_count, PassChunkMin), _count, (_, from, to) =>
            {
                for (int s = from; s < to; s++)
                {
                    int t = order[s];
                    slotOf[t] = s;
                    int src = t * 9;
                    int dst = s * 9;
                    float ax = v[src], ay = v[src + 1], az = v[src + 2];
                    tri[dst] = ax;
                    tri[dst + 1] = ay;
                    tri[dst + 2] = az;
                    tri[dst + 3] = v[src + 3] - ax;
                    tri[dst + 4] = v[src + 4] - ay;
                    tri[dst + 5] = v[src + 5] - az;
                    tri[dst + 6] = v[src + 6] - ax;
                    tri[dst + 7] = v[src + 7] - ay;
                    tri[dst + 8] = v[src + 8] - az;
                }
            });

            return new TriangleBvh(
                minX, minY, minZ, maxX, maxY, maxZ, child, tri, _order, slotOf, _laneOfSlot,
                nodeCount, stackCapacity, depth, rootDecision.Self.Min, rootDecision.Self.Max, _count, _peakWorkers);
        }

        /// <summary>The most threads observed inside this build's bodies at once; one for a build that never forked.</summary>
        public int PeakWorkers => _peakWorkers;

        // Every loop body, parallel or not, brackets itself with these. A thread counts itself on
        // its outermost body only, so a chunk sweep run inline on the thread that owns the top
        // node adds nothing, and the peak is threads rather than bodies. The bracket is per body,
        // not per triangle: a build enters a few thousand at most.
        private void EnterBody()
        {
            if (_nesting++ != 0)
            {
                return;
            }

            int active = Interlocked.Increment(ref _activeWorkers);
            int peak = _peakWorkers;
            while (active > peak && Interlocked.CompareExchange(ref _peakWorkers, active, peak) != peak)
            {
                peak = _peakWorkers;
            }
        }

        private void LeaveBody()
        {
            if (--_nesting == 0)
            {
                Interlocked.Decrement(ref _activeWorkers);
            }
        }

        // Preorder over the segment tree: a segment's own nodes take the next indices, then each
        // pending child's segment in collapse order. A subtree segment has no pending children, so
        // its block is its local numbering shifted by its offset.
        private static void Place(Segment s, ref int next)
        {
            s.Offset = next;
            next += s.WideCount;
            foreach (Pending p in s.Pending)
            {
                Place(p.Built!, ref next);
            }
        }

        // Copies a segment's nodes into the final arrays at their serial indices, offsets its inner
        // references, points each pending lane at its child segment, and points each leaf's run of
        // slots at its final lane. Segments cover disjoint index and slot ranges, so this runs for
        // all of them at once.
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private void Relocate(
            Segment s, float[] minX, float[] minY, float[] minZ, float[] maxX, float[] maxY, float[] maxZ, int[] child)
        {
            int offset = s.Offset;
            int n = s.WideCount * Width;
            int at = offset * Width;
            Array.Copy(s.MinX, 0, minX, at, n);
            Array.Copy(s.MinY, 0, minY, at, n);
            Array.Copy(s.MinZ, 0, minZ, at, n);
            Array.Copy(s.MaxX, 0, maxX, at, n);
            Array.Copy(s.MaxY, 0, maxY, at, n);
            Array.Copy(s.MaxZ, 0, maxZ, at, n);

            for (int lane = 0; lane < n; lane++)
            {
                int c = s.Child[lane];
                if (c >= 0)
                {
                    child[at + lane] = c + offset;
                }
                else if (c == EmptyLane || c == PendingLane)
                {
                    child[at + lane] = c;
                }
                else
                {
                    child[at + lane] = c;
                    (int start, int run) = DecodeLeaf(c);
                    for (int slot = start; slot < start + run; slot++)
                    {
                        _laneOfSlot[slot] = at + lane;
                    }
                }
            }

            foreach (Pending p in s.Pending)
            {
                Debug.Assert(child[at + p.Lane] == PendingLane, "a pending lane holds something other than the sentinel");
                child[at + p.Lane] = p.Built!.Offset;
            }

            // The segment's arrays are dead the moment they are copied, and they are released here
            // rather than by letting the segments go out of reach: a segment object stays
            // reachable from this frame until Finish returns whatever is nulled (a weak reference
            // to the root survived a forced collection after every field and local naming it was
            // cleared; the JIT may keep a reference in a slot it does not track), and with the
            // arrays still attached the leaf pack landed on top of them, 37 MB over the finished
            // tree on de_ancient. Released per segment, the live set peaks at the tree's own size,
            // as the serial build's did; the bench's build --live flag is what sees it.
            s.ReleaseArrays();
        }

        // Chunks for a pass over count items: as many as the parallelism allows with at least
        // minPerChunk items each, and one when there is nothing to fork for.
        private int Chunks(int count, int minPerChunk) =>
            _parallelism <= 1 ? 1 : (int)Math.Clamp(count / minPerChunk, 1, _parallelism);

        // Runs body over the chunk ranges of [0, count): chunk c is [c * count / chunks,
        // (c + 1) * count / chunks), and body gets c with its range. Serial when there is one chunk.
        private void RunChunks(int chunks, int count, Action<int, int, int> body)
        {
            if (chunks <= 1)
            {
                EnterBody();
                try
                {
                    body(0, 0, count);
                }
                finally
                {
                    LeaveBody();
                }

                return;
            }

            Parallel.For(0, chunks, _options, c =>
            {
                EnterBody();
                try
                {
                    body(c, (int)(((long)c * count) / chunks), (int)(((long)(c + 1) * count) / chunks));
                }
                finally
                {
                    LeaveBody();
                }
            });
        }

        private static void Fold(in TriangleRecord r, ref Vector3 bmin, ref Vector3 bmax, ref Vector3 cmin, ref Vector3 cmax)
        {
            bmin = Vector3.Min(bmin, r.Min);
            bmax = Vector3.Max(bmax, r.Max);
            cmin = Vector3.Min(cmin, r.Centroid);
            cmax = Vector3.Max(cmax, r.Centroid);
        }

        private static float Area(Vector3 min, Vector3 max)
        {
            Vector3 e = max - min;
            return 2f * ((e.X * e.Y) + (e.Y * e.Z) + (e.Z * e.X));
        }

        // One expression, used by the binning pass and the partition pass, so both put a triangle
        // in the same bin.
        private static int BinIndex(float c, float cmin, float scale)
        {
            int bin = (int)((c - cmin) * scale);
            return bin >= Bins ? Bins - 1 : bin < 0 ? 0 : bin;
        }

        /// <summary>
        ///     One segment's build state: the bins the SAH sweeps into, lane arrays numbered from
        ///     zero in the segment's own preorder, and for a top node the children it left pending.
        ///     A top node also owns chunk bins for its parallel sweeps.
        /// </summary>
        private sealed class Segment
        {
            private readonly BinSet _bins = new();
            private readonly Builder _owner;
            private readonly float[] _rightArea = new float[Bins];
            private readonly int[] _rightCount = new int[Bins];
            private readonly bool _top;
            private int _capacity; // wide nodes the lane arrays hold
            private BinSet[] _chunkBins = [];
            private int _sweepChunks = 1;

            public Segment(Builder owner, bool top, int sweepChunks, int initialCapacity)
            {
                _owner = owner;
                _top = top;
                SweepChunks = sweepChunks;
                _capacity = initialCapacity;
                int lanes = _capacity * Width;
                MinX = new float[lanes];
                MinY = new float[lanes];
                MinZ = new float[lanes];
                MaxX = new float[lanes];
                MaxY = new float[lanes];
                MaxZ = new float[lanes];
                Child = new int[lanes];
            }

            /// <summary>The subtree this segment stands for, as its parent left it; unset for the root, which is decided in place.</summary>
            public Pending Job { get; set; } = null!;

            /// <summary>The children a top node left for segments of their own, in collapse order.</summary>
            public List<Pending> Pending { get; } = [];

            /// <summary>The serial index of this segment's first node, once placed.</summary>
            public int Offset { get; set; }

            /// <summary>Drops the lane arrays once they are copied into the final ones; see <c>Relocate</c>.</summary>
            public void ReleaseArrays()
            {
                MinX = [];
                MinY = [];
                MinZ = [];
                MaxX = [];
                MaxY = [];
                MaxZ = [];
                Child = [];
            }

            public int WideCount { get; private set; }

            public int Depth { get; private set; }

            public int StackCapacity { get; private set; }

            public float[] MinX { get; private set; }

            public float[] MinY { get; private set; }

            public float[] MinZ { get; private set; }

            public float[] MaxX { get; private set; }

            public float[] MaxY { get; private set; }

            public float[] MaxZ { get; private set; }

            public int[] Child { get; private set; }

            // Chunks a sweep of this segment's nodes may split into; one means serial sweeps.
            //
            // A share of one allocates nothing: Decide clamps its chunk count to this share, so a
            // one-chunk segment always takes the serial branch and never reads the chunk bins. That
            // is every subtree segment, of which a real bake has hundreds, and a BinSet is five
            // arrays over all three axes' bins.
            private int SweepChunks
            {
                set
                {
                    _sweepChunks = Math.Max(1, value);
                    if (_sweepChunks > 1 && _chunkBins.Length < _sweepChunks)
                    {
                        _chunkBins = new BinSet[_sweepChunks];
                        for (int c = 0; c < _chunkBins.Length; c++)
                        {
                            _chunkBins[c] = new BinSet();
                        }
                    }
                }
            }

            /// <summary>Builds a top node from its job: one wide node, its inner children left pending.</summary>
            /// <param name="sweepChunks">Threads this node may use for its sweeps.</param>
            public void BuildTop(int sweepChunks)
            {
                Debug.Assert(_top, "BuildTop on a subtree segment");
                SweepChunks = sweepChunks;
                Decision root = Job.Root;
                BuildWide(in root, Job.Waiting, Job.Depth);
            }

            /// <summary>Builds a subtree segment from its job: the whole subtree, depth first.</summary>
            public void BuildSubtree()
            {
                Debug.Assert(!_top, "BuildSubtree on a top node");
                Decision root = Job.Root;
                BuildWide(in root, Job.Waiting, Job.Depth);
            }

            // Decides one binary node: a leaf, or a split with the two child ranges and their bounds.
            // The bin sweep and the partition are exactly the two-pass build's; only the destination
            // of the answer changed.
            [MethodImpl(MethodImplOptions.AggressiveOptimization)]
            public Decision Decide(in Range r)
            {
                int start = r.Start, count = r.Count, depth = r.Depth;
                Vector3 bmin = r.Min, bmax = r.Max, cmin = r.CentroidMin, cmax = r.CentroidMax;
                if (count == 1)
                {
                    return Decision.Leaf(in r);
                }

                Vector3 cext = cmax - cmin;
                float parentArea = Area(bmin, bmax);
                float bestCost = float.MaxValue;
                int bestAxis = -1, bestBin = -1;
                float bestScale = 0f;
                TriangleRecord[] rec = _owner._rec;

                if (depth < MaxBinaryDepth)
                {
                    bool binX = cext.X > 0f, binY = cext.Y > 0f, binZ = cext.Z > 0f;
                    float scaleX = binX ? Bins / cext.X : 0f;
                    float scaleY = binY ? Bins / cext.Y : 0f;
                    float scaleZ = binZ ? Bins / cext.Z : 0f;
                    int chunks = Math.Clamp(count / SweepChunkMin, 1, _sweepChunks);
                    if (chunks == 1)
                    {
                        _bins.Reset();
                        _bins.Sweep(rec, start, start + count, cmin, scaleX, scaleY, scaleZ, binX, binY, binZ);
                    }
                    else
                    {
                        SweepChunked(rec, start, count, chunks, cmin, scaleX, scaleY, scaleZ, binX, binY, binZ);
                    }

                    int[] binCount = _bins.Count;
                    Vector3[] binMin = _bins.Min;
                    Vector3[] binMax = _bins.Max;
                    for (int axis = 0; axis < 3; axis++)
                    {
                        bool active = axis == 0 ? binX : axis == 1 ? binY : binZ;
                        if (!active)
                        {
                            continue;
                        }

                        float scale = axis == 0 ? scaleX : axis == 1 ? scaleY : scaleZ;
                        int binBase = axis * Bins;

                        // Right-to-left: the cost of everything right of a split after bin k.
                        Vector3 rmin = new(float.MaxValue), rmax = new(float.MinValue);
                        int rcount = 0;
                        for (int b = Bins - 1; b >= 1; b--)
                        {
                            rcount += binCount[binBase + b];
                            rmin = Vector3.Min(rmin, binMin[binBase + b]);
                            rmax = Vector3.Max(rmax, binMax[binBase + b]);
                            _rightCount[b - 1] = rcount;
                            _rightArea[b - 1] = Area(rmin, rmax);
                        }

                        // Left-to-right: fold the left side and price each split.
                        Vector3 lmin = new(float.MaxValue), lmax = new(float.MinValue);
                        int lcount = 0;
                        for (int k = 0; k < Bins - 1; k++)
                        {
                            lcount += binCount[binBase + k];
                            lmin = Vector3.Min(lmin, binMin[binBase + k]);
                            lmax = Vector3.Max(lmax, binMax[binBase + k]);
                            if (lcount == 0 || _rightCount[k] == 0)
                            {
                                continue;
                            }

                            float cost = (NodeCost * parentArea) + (Area(lmin, lmax) * lcount) + (_rightArea[k] * _rightCount[k]);
                            if (cost < bestCost)
                            {
                                bestCost = cost;
                                bestAxis = axis;
                                bestBin = k;
                                bestScale = scale;
                            }
                        }
                    }
                }

                if (bestAxis < 0)
                {
                    // No split can separate the centroids (all coincide), or the depth cap was reached.
                    if (count <= MaxLeaf)
                    {
                        return Decision.Leaf(in r);
                    }

                    return HalfSplit(in r);
                }

                if (count <= MaxLeaf && parentArea * count <= bestCost)
                {
                    return Decision.Leaf(in r);
                }

                // Partition the range in place around the chosen bin, records and indices together;
                // the children's bounds and centroid bounds come from the bins, not from a rescan.
                int[] order = _owner._order;
                float splitMin = bestAxis == 0 ? cmin.X : bestAxis == 1 ? cmin.Y : cmin.Z;
                int lo = start, hi = start + count - 1;
                while (lo <= hi)
                {
                    if (BinIndex(Centroid(rec, bestAxis, lo), splitMin, bestScale) <= bestBin)
                    {
                        lo++;
                    }
                    else
                    {
                        (order[lo], order[hi]) = (order[hi], order[lo]);
                        (rec[lo], rec[hi]) = (rec[hi], rec[lo]);
                        hi--;
                    }
                }

                int mid = lo;
                int leftCount = mid - start;
                int rightCount = count - leftCount;
                Debug.Assert(leftCount > 0 && rightCount > 0, "a priced split left one side empty");

                int bestBase = bestAxis * Bins;
                Vector3 lbmin = new(float.MaxValue), lbmax = new(float.MinValue);
                Vector3 lcmin = new(float.MaxValue), lcmax = new(float.MinValue);
                Vector3 rbmin = new(float.MaxValue), rbmax = new(float.MinValue);
                Vector3 rcmin = new(float.MaxValue), rcmax = new(float.MinValue);
                for (int b = 0; b < Bins; b++)
                {
                    if (_bins.Count[bestBase + b] == 0)
                    {
                        continue;
                    }

                    if (b <= bestBin)
                    {
                        lbmin = Vector3.Min(lbmin, _bins.Min[bestBase + b]);
                        lbmax = Vector3.Max(lbmax, _bins.Max[bestBase + b]);
                        lcmin = Vector3.Min(lcmin, _bins.CentroidMin[bestBase + b]);
                        lcmax = Vector3.Max(lcmax, _bins.CentroidMax[bestBase + b]);
                    }
                    else
                    {
                        rbmin = Vector3.Min(rbmin, _bins.Min[bestBase + b]);
                        rbmax = Vector3.Max(rbmax, _bins.Max[bestBase + b]);
                        rcmin = Vector3.Min(rcmin, _bins.CentroidMin[bestBase + b]);
                        rcmax = Vector3.Max(rcmax, _bins.CentroidMax[bestBase + b]);
                    }
                }

                return Decision.Inner(
                    in r,
                    new Range(start, leftCount, depth + 1, lbmin, lbmax, lcmin, lcmax),
                    new Range(mid, rightCount, depth + 1, rbmin, rbmax, rcmin, rcmax));
            }

            // Lays the binary subtree rooted at node out as one wide node and returns its index in
            // this segment. A subtree segment recurses into its inner children; a top node leaves
            // them pending. pathWaiting is the number of sibling lanes that can be waiting on the
            // stack above this node along the current path; it sizes the traversal stack exactly.
            [MethodImpl(MethodImplOptions.AggressiveOptimization)]
            public int BuildWide(in Decision node, int pathWaiting, int depth)
            {
                Span<Decision> children = stackalloc Decision[Width];
                int n = Collapse(in node, children);

                int wide = WideCount++;
                if (wide == _capacity)
                {
                    Grow();
                }

                int laneBase = wide * Width;
                for (int l = 0; l < Width; l++)
                {
                    MinX[laneBase + l] = float.PositiveInfinity;
                    MinY[laneBase + l] = float.PositiveInfinity;
                    MinZ[laneBase + l] = float.PositiveInfinity;
                    MaxX[laneBase + l] = float.NegativeInfinity;
                    MaxY[laneBase + l] = float.NegativeInfinity;
                    MaxZ[laneBase + l] = float.NegativeInfinity;
                    Child[laneBase + l] = EmptyLane;
                }

                int waiting = pathWaiting + (n - 1);
                StackCapacity = Math.Max(StackCapacity, 1 + waiting);
                Depth = Math.Max(Depth, depth);

                Span<int> laneOf = stackalloc int[Width];
                AssignLanes(children[..n], in node.Self, laneOf);

                for (int k = 0; k < n; k++)
                {
                    int lane = laneBase + laneOf[k];
                    ref Decision c = ref children[k];
                    MinX[lane] = c.Self.Min.X;
                    MinY[lane] = c.Self.Min.Y;
                    MinZ[lane] = c.Self.Min.Z;
                    MaxX[lane] = c.Self.Max.X;
                    MaxY[lane] = c.Self.Max.Y;
                    MaxZ[lane] = c.Self.Max.Z;
                    if (c.IsLeaf)
                    {
                        Child[lane] = EncodeLeaf(c.Self.Start, c.Self.Count);
                    }
                    else if (_top)
                    {
                        Pending.Add(new Pending(in c, waiting, depth + 1, lane));
                        Child[lane] = PendingLane;
                    }
                    else
                    {
                        // Into a local first: the recursion can grow the lane arrays, and an element
                        // assignment evaluates its array reference before its right-hand side.
                        int child = BuildWide(in c, waiting, depth + 1);
                        Child[lane] = child;
                    }
                }

                return wide;
            }

            // The chunked sweep of Decide, in a method of its own on purpose: the loop body closes
            // over its arguments, and the compiler allocates a closure's captured variables when
            // their scope is entered, not when the lambda is reached. Written inline in Decide,
            // that was one heap object per decision (about seventy bytes, over a million times on
            // de_ancient, whether or not the chunked branch was taken), which the allocation
            // budget test now holds against. Here it is one per chunked sweep, a few dozen per
            // build. Each chunk sweeps its part of the range into its own bins; the node's bins
            // are then the fold of the chunk bins in chunk order, which the class doc argues is
            // the serial sweep's answer bit for bit. The loop has as many iterations as this
            // segment's share of the degree allows, so it occupies at most that many threads
            // whatever the options would permit.
            [MethodImpl(MethodImplOptions.AggressiveOptimization)]
            private void SweepChunked(
                TriangleRecord[] rec, int start, int count, int chunks, Vector3 cmin, float scaleX, float scaleY, float scaleZ,
                bool binX, bool binY, bool binZ)
            {
                Debug.Assert(chunks > 1 && chunks <= _sweepChunks, "a chunked sweep outside its segment's share");
                BinSet[] chunkBins = _chunkBins;
                Builder owner = _owner;
                Parallel.For(0, chunks, owner._options, c =>
                {
                    owner.EnterBody();
                    try
                    {
                        int from = start + (int)(((long)c * count) / chunks);
                        int to = start + (int)(((long)(c + 1) * count) / chunks);
                        chunkBins[c].Reset();
                        chunkBins[c].Sweep(rec, from, to, cmin, scaleX, scaleY, scaleZ, binX, binY, binZ);
                    }
                    finally
                    {
                        owner.LeaveBody();
                    }
                });

                _bins.Reset();
                for (int c = 0; c < chunks; c++)
                {
                    _bins.Absorb(chunkBins[c]);
                }
            }

            private static float Centroid(TriangleRecord[] rec, int axis, int position)
            {
                ref TriangleRecord r = ref rec[position];
                return axis == 0 ? r.Centroid.X : axis == 1 ? r.Centroid.Y : r.Centroid.Z;
            }

            // Halves the range by index. Only for a range the SAH cannot split (coincident centroids
            // past the leaf size) or one past the depth cap; the children are scanned for their bounds
            // because no bin sweep priced them.
            [MethodImpl(MethodImplOptions.AggressiveOptimization)]
            private Decision HalfSplit(in Range r)
            {
                TriangleRecord[] rec = _owner._rec;
                int start = r.Start, count = r.Count;
                int leftCount = count / 2;
                Vector3 lbmin = new(float.MaxValue), lbmax = new(float.MinValue);
                Vector3 lcmin = new(float.MaxValue), lcmax = new(float.MinValue);
                for (int i = start; i < start + leftCount; i++)
                {
                    Fold(in rec[i], ref lbmin, ref lbmax, ref lcmin, ref lcmax);
                }

                Vector3 rbmin = new(float.MaxValue), rbmax = new(float.MinValue);
                Vector3 rcmin = new(float.MaxValue), rcmax = new(float.MinValue);
                for (int i = start + leftCount; i < start + count; i++)
                {
                    Fold(in rec[i], ref rbmin, ref rbmax, ref rcmin, ref rcmax);
                }

                return Decision.Inner(
                    in r,
                    new Range(start, leftCount, r.Depth + 1, lbmin, lbmax, lcmin, lcmax),
                    new Range(start + leftCount, count - leftCount, r.Depth + 1, rbmin, rbmax, rcmin, rcmax));
            }

            // The children of the wide node that stands for the binary subtree rooted at node: its two
            // binary children, then repeatedly the inner one with the largest box replaced by its own
            // two, until eight remain or none is inner. Each child is decided as it enters the frontier,
            // which is the only time its bin sweep runs. Returns how many; the root itself is the one
            // child when it is a leaf.
            [MethodImpl(MethodImplOptions.AggressiveOptimization)]
            private int Collapse(in Decision node, Span<Decision> children)
            {
                if (node.IsLeaf)
                {
                    children[0] = node;
                    return 1;
                }

                children[0] = Decide(in node.Left);
                children[1] = Decide(in node.Right);
                int n = 2;
                while (n < Width)
                {
                    int pick = -1;
                    float pickArea = float.MinValue;
                    for (int k = 0; k < n; k++)
                    {
                        ref Decision c = ref children[k];
                        if (c.IsLeaf)
                        {
                            continue;
                        }

                        float area = Area(c.Self.Min, c.Self.Max);
                        if (area > pickArea)
                        {
                            pickArea = area;
                            pick = k;
                        }
                    }

                    if (pick < 0)
                    {
                        break;
                    }

                    Decision expanded = children[pick];
                    children[pick] = Decide(in expanded.Left);
                    children[n++] = Decide(in expanded.Right);
                }

                return n;
            }

            private void Grow()
            {
                _capacity *= 2;
                int lanes = _capacity * Width;
                MinX = Resized(MinX, lanes);
                MinY = Resized(MinY, lanes);
                MinZ = Resized(MinZ, lanes);
                MaxX = Resized(MaxX, lanes);
                MaxY = Resized(MaxY, lanes);
                MaxZ = Resized(MaxZ, lanes);
                Child = Resized(Child, lanes);
            }

            private static T[] Resized<T>(T[] array, int length)
            {
                Array.Resize(ref array, length);
                return array;
            }

            // Lays children into lanes by octant. Each child's preferred lane has a bit set per axis on
            // which its centre lies on the positive side of the node's; when two children want the same
            // lane, the assignment is the greedy best-score one over (child, lane) pairs, with offsets
            // scaled by the node's extent so no axis dominates by unit alone. Ties fall to the lower
            // child index, then the lower lane, so the layout is deterministic.
            [MethodImpl(MethodImplOptions.AggressiveOptimization)]
            private static void AssignLanes(ReadOnlySpan<Decision> children, in Range node, Span<int> laneOf)
            {
                int n = children.Length;
                Vector3 centre = (node.Min + node.Max) * 0.5f;
                Vector3 extent = node.Max - node.Min;
                Vector3 scale = new(
                    extent.X > 0f ? 1f / extent.X : 0f,
                    extent.Y > 0f ? 1f / extent.Y : 0f,
                    extent.Z > 0f ? 1f / extent.Z : 0f);

                Span<Vector3> offset = stackalloc Vector3[Width];
                for (int k = 0; k < n; k++)
                {
                    ref readonly Range c = ref children[k].Self;
                    offset[k] = (((c.Min + c.Max) * 0.5f) - centre) * scale;
                }

                Span<float> scores = stackalloc float[Width * Width];
                for (int k = 0; k < n; k++)
                {
                    for (int lane = 0; lane < Width; lane++)
                    {
                        scores[(k * Width) + lane] = ((lane & 1) != 0 ? offset[k].X : -offset[k].X)
                                                     + ((lane & 2) != 0 ? offset[k].Y : -offset[k].Y)
                                                     + ((lane & 4) != 0 ? offset[k].Z : -offset[k].Z);
                    }
                }

                int assignedChildren = 0, usedLanes = 0;
                for (int k = 0; k < n; k++)
                {
                    laneOf[k] = -1;
                }

                for (int round = 0; round < n; round++)
                {
                    int bestChild = -1, bestLane = -1;
                    float bestScore = float.MinValue;
                    for (int k = 0; k < n; k++)
                    {
                        if ((assignedChildren & (1 << k)) != 0)
                        {
                            continue;
                        }

                        for (int lane = 0; lane < Width; lane++)
                        {
                            if ((usedLanes & (1 << lane)) != 0)
                            {
                                continue;
                            }

                            float score = scores[(k * Width) + lane];
                            if (score > bestScore)
                            {
                                bestScore = score;
                                bestChild = k;
                                bestLane = lane;
                            }
                        }
                    }

                    if (bestChild < 0)
                    {
                        // Every remaining score is NaN (a NaN vertex in the soup). The layout is only
                        // an ordering heuristic, so any free lane will do; take the first of each.
                        bestChild = BitOperations.TrailingZeroCount(~assignedChildren);
                        bestLane = BitOperations.TrailingZeroCount(~usedLanes);
                    }

                    laneOf[bestChild] = bestLane;
                    assignedChildren |= 1 << bestChild;
                    usedLanes |= 1 << bestLane;
                }
            }
        }

        /// <summary>
        ///     The three axes' bins for one sweep: per bin, how many triangles fell in it, the fold
        ///     of their boxes and the fold of their centroids. Every fold is a min or a max, so a
        ///     set can absorb another (a chunk of the same range) and hold what one sweep over both
        ///     ranges would have held.
        /// </summary>
        private sealed class BinSet
        {
            public readonly Vector3[] CentroidMax = new Vector3[3 * Bins];
            public readonly Vector3[] CentroidMin = new Vector3[3 * Bins];
            public readonly int[] Count = new int[3 * Bins];
            public readonly Vector3[] Max = new Vector3[3 * Bins];
            public readonly Vector3[] Min = new Vector3[3 * Bins];

            [MethodImpl(MethodImplOptions.AggressiveOptimization)]
            public void Reset()
            {
                for (int b = 0; b < 3 * Bins; b++)
                {
                    Count[b] = 0;
                    Min[b] = new Vector3(float.MaxValue);
                    Max[b] = new Vector3(float.MinValue);
                    CentroidMin[b] = new Vector3(float.MaxValue);
                    CentroidMax[b] = new Vector3(float.MinValue);
                }
            }

            // One pass over [from, to) feeds every axis's bins; each fold is an exact min or max,
            // so the bins hold what three separate passes would have put in them.
            [MethodImpl(MethodImplOptions.AggressiveOptimization)]
            public void Sweep(
                TriangleRecord[] rec, int from, int to, Vector3 cmin, float scaleX, float scaleY, float scaleZ,
                bool binX, bool binY, bool binZ)
            {
                for (int i = from; i < to; i++)
                {
                    ref TriangleRecord r = ref rec[i];
                    if (binX)
                    {
                        int b = BinIndex(r.Centroid.X, cmin.X, scaleX);
                        Count[b]++;
                        Fold(in r, ref Min[b], ref Max[b], ref CentroidMin[b], ref CentroidMax[b]);
                    }

                    if (binY)
                    {
                        int b = Bins + BinIndex(r.Centroid.Y, cmin.Y, scaleY);
                        Count[b]++;
                        Fold(in r, ref Min[b], ref Max[b], ref CentroidMin[b], ref CentroidMax[b]);
                    }

                    if (binZ)
                    {
                        int b = (2 * Bins) + BinIndex(r.Centroid.Z, cmin.Z, scaleZ);
                        Count[b]++;
                        Fold(in r, ref Min[b], ref Max[b], ref CentroidMin[b], ref CentroidMax[b]);
                    }
                }
            }

            // Folds another set's bins into these, bin by bin. An empty bin there holds the
            // initial values, which the fold leaves alone.
            [MethodImpl(MethodImplOptions.AggressiveOptimization)]
            public void Absorb(BinSet other)
            {
                for (int b = 0; b < 3 * Bins; b++)
                {
                    Count[b] += other.Count[b];
                    Min[b] = Vector3.Min(Min[b], other.Min[b]);
                    Max[b] = Vector3.Max(Max[b], other.Max[b]);
                    CentroidMin[b] = Vector3.Min(CentroidMin[b], other.CentroidMin[b]);
                    CentroidMax[b] = Vector3.Max(CentroidMax[b], other.CentroidMax[b]);
                }
            }
        }

        /// <summary>
        ///     A child a top node left for a segment of its own: its decided root, the stack-sizing
        ///     arguments the serial recursion would have passed, the lane of the top node that
        ///     references it, and the segment once created.
        /// </summary>
        private sealed class Pending
        {
            public Pending(in Decision root, int waiting, int depth, int lane)
            {
                Root = root;
                Waiting = waiting;
                Depth = depth;
                Lane = lane;
            }

            public Decision Root { get; }

            public int Waiting { get; }

            public int Depth { get; }

            public int Lane { get; }

            public Segment? Built
            {
                get => _built;
                set
                {
                    _built = value;
                    if (value is not null)
                    {
                        value.Job = this;
                    }
                }
            }

            private Segment? _built;
        }

        // One triangle's box and box centre, interleaved so a fold reads one contiguous record.
        private readonly struct TriangleRecord(Vector3 min, Vector3 max, Vector3 centroid)
        {
            public readonly Vector3 Min = min;
            public readonly Vector3 Max = max;
            public readonly Vector3 Centroid = centroid;
        }

        // A binary node before it is decided: its slot range, its depth (for the cap), its box and
        // its centroid box (the bin sweep's domain).
        private readonly struct Range(int start, int count, int depth, Vector3 min, Vector3 max, Vector3 centroidMin, Vector3 centroidMax)
        {
            public readonly int Start = start;
            public readonly int Count = count;
            public readonly int Depth = depth;
            public readonly Vector3 Min = min;
            public readonly Vector3 Max = max;
            public readonly Vector3 CentroidMin = centroidMin;
            public readonly Vector3 CentroidMax = centroidMax;
        }

        // A decided binary node: a leaf over Self, or a split of Self into Left and Right, which are
        // undecided until they enter a frontier.
        private readonly struct Decision
        {
            public readonly Range Self;
            public readonly Range Left;
            public readonly Range Right;
            public readonly bool IsLeaf;

            private Decision(in Range self, in Range left, in Range right, bool isLeaf)
            {
                Self = self;
                Left = left;
                Right = right;
                IsLeaf = isLeaf;
            }

            public static Decision Leaf(in Range self) => new(in self, default, default, true);

            public static Decision Inner(in Range self, in Range left, in Range right) => new(in self, in left, in right, false);
        }
    }
}
