#region

using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
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
///         fail. A NaN origin or direction component makes every comparison false and would admit
///         every lane, empty ones included, so both traversals reject such a ray up front; the
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
        Vector3 min, Vector3 max, int triangleCount)
    {
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

    /// <summary>Builds the BVH over <paramref name="count" /> triangles packed as 9 floats each in <paramref name="v" />.</summary>
    /// <param name="v">Triangle soup, 9 floats per triangle (three vertices), not retained.</param>
    /// <param name="count">Number of triangles packed in <paramref name="v" />.</param>
    public static TriangleBvh Build(float[] v, int count)
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
            return new TriangleBvh([], [], [], [], [], [], [], [], [], [], [], 0, 0, 0, Vector3.Zero, Vector3.Zero, 0);
        }

        return new Builder(v, count).Finish();
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
        if (hi <= lo || HasNaN(origin, dir))
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
        if (NodeCount == 0 || HasNaN(origin, dir))
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

    // A NaN component makes every slab comparison false, which the compare-select reads as "this
    // axis constrains nothing": every lane passes, the empty ones included, and eight references go
    // on a stack sized for the real lanes. No triangle test can succeed on such a ray (its t is NaN
    // or its barycentrics are), so a miss is the only verdict it could have had, and the binary tree
    // gave that verdict. Infinite components need no guard: the slab products are then infinite or
    // NaN in the combinations that reject, never in one that admits an empty lane.
    private static bool HasNaN(Vector3 origin, Vector3 dir) =>
        float.IsNaN(origin.X) || float.IsNaN(origin.Y) || float.IsNaN(origin.Z)
        || float.IsNaN(dir.X) || float.IsNaN(dir.Y) || float.IsNaN(dir.Z);

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
    ///     The build, in two passes. First a binary tree by binned SAH over an index array, which
    ///     partitions that array in place so every binary leaf is a contiguous run of slots. Then the
    ///     binary tree is collapsed into eight-wide nodes: starting from a binary node's two children,
    ///     the inner child with the largest box is repeatedly replaced by its own two children until
    ///     eight remain or none is inner, and the eight are laid into lanes by octant.
    ///     <para>
    ///         Serial and deterministic: no sort, no comparer, no parallelism; the same soup gives the
    ///         same tree on every run and every machine.
    ///     </para>
    /// </summary>
    private sealed class Builder
    {
        private readonly List<BinaryNode> _binary;
        private readonly int[] _binCount = new int[3 * Bins];
        private readonly Vector3[] _binCentroidMax = new Vector3[3 * Bins];
        private readonly Vector3[] _binCentroidMin = new Vector3[3 * Bins];
        private readonly Vector3[] _binMax = new Vector3[3 * Bins];
        private readonly Vector3[] _binMin = new Vector3[3 * Bins];
        private readonly int _count;
        private readonly int[] _order;
        private readonly TriangleRecord[] _rec;
        private readonly float[] _rightArea = new float[Bins];
        private readonly int[] _rightCount = new int[Bins];
        private readonly float[] _v;
        private int[] _child = [];
        private int _depth;
        private int[] _laneOfSlot = [];
        private float[] _maxX = [];
        private float[] _maxY = [];
        private float[] _maxZ = [];
        private float[] _minX = [];
        private float[] _minY = [];
        private float[] _minZ = [];
        private int _stackCapacity;
        private int _wideCount;

        public Builder(float[] v, int count)
        {
            _v = v;
            _count = count;
            _order = new int[count];
            _rec = new TriangleRecord[count];
            _binary = new List<BinaryNode>(Math.Max(1, count / 2));

            for (int i = 0; i < count; i++)
            {
                _order[i] = i;
                int b = i * 9;
                Vector3 a = new(v[b], v[b + 1], v[b + 2]);
                Vector3 bb = new(v[b + 3], v[b + 4], v[b + 5]);
                Vector3 c = new(v[b + 6], v[b + 7], v[b + 8]);
                Vector3 lo = Vector3.Min(a, Vector3.Min(bb, c));
                Vector3 hi = Vector3.Max(a, Vector3.Max(bb, c));
                // Binned on the box centre, which is what the SAH literature calls the centroid.
                _rec[i] = new TriangleRecord(lo, hi, (lo + hi) * 0.5f);
            }
        }

        public TriangleBvh Finish()
        {
            // The root's bounds are the one scan the build does over a whole range; every child's
            // come out of its parent's bin sweep.
            Vector3 bmin = new(float.MaxValue), bmax = new(float.MinValue);
            Vector3 cmin = new(float.MaxValue), cmax = new(float.MinValue);
            for (int i = 0; i < _count; i++)
            {
                Fold(in _rec[i], ref bmin, ref bmax, ref cmin, ref cmax);
            }

            BuildBinary(0, _count, bmin, bmax, cmin, cmax, 0);

            // The lane arrays are sized exactly, by a pass that collapses the binary tree the same
            // way the layout pass will and only counts. Sizing them to the binary node count instead
            // would allocate about eight times the final tree and copy it down: on a map bake that
            // is a few hundred megabytes of short-lived arrays for a saving of one cheap walk.
            int lanes = CountWide(0) * Width;
            _minX = new float[lanes];
            _minY = new float[lanes];
            _minZ = new float[lanes];
            _maxX = new float[lanes];
            _maxY = new float[lanes];
            _maxZ = new float[lanes];
            _child = new int[lanes];
            _laneOfSlot = new int[_count];
            BuildWide(0, 0, 1);
            Debug.Assert(_wideCount * Width == lanes, "the counting pass and the layout pass collapsed differently");

            // Leaf storage in slot order: vertex a and the two edges, formed exactly as the
            // soup-order triangle test formed them.
            float[] tri = new float[_count * 9];
            int[] slotOf = new int[_count];
            float[] v = _v;
            for (int s = 0; s < _count; s++)
            {
                int t = _order[s];
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

            BinaryNode root = _binary[0];
            return new TriangleBvh(
                _minX, _minY, _minZ, _maxX, _maxY, _maxZ, _child, tri, _order, slotOf, _laneOfSlot,
                _wideCount, _stackCapacity, _depth, root.Min, root.Max, _count);
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

        private float Centroid(int axis, int position)
        {
            ref TriangleRecord r = ref _rec[position];
            return axis == 0 ? r.Centroid.X : axis == 1 ? r.Centroid.Y : r.Centroid.Z;
        }

        private int BuildBinary(
            int start, int count, Vector3 bmin, Vector3 bmax, Vector3 cmin, Vector3 cmax, int depth)
        {
            int idx = _binary.Count;
            _binary.Add(default);
            if (count == 1)
            {
                _binary[idx] = BinaryNode.Leaf(bmin, bmax, start, count);
                return idx;
            }

            Vector3 cext = cmax - cmin;
            float parentArea = Area(bmin, bmax);
            float bestCost = float.MaxValue;
            int bestAxis = -1, bestBin = -1;
            float bestScale = 0f;

            if (depth < MaxBinaryDepth)
            {
                bool binX = cext.X > 0f, binY = cext.Y > 0f, binZ = cext.Z > 0f;
                float scaleX = binX ? Bins / cext.X : 0f;
                float scaleY = binY ? Bins / cext.Y : 0f;
                float scaleZ = binZ ? Bins / cext.Z : 0f;
                for (int b = 0; b < 3 * Bins; b++)
                {
                    _binCount[b] = 0;
                    _binMin[b] = new Vector3(float.MaxValue);
                    _binMax[b] = new Vector3(float.MinValue);
                    _binCentroidMin[b] = new Vector3(float.MaxValue);
                    _binCentroidMax[b] = new Vector3(float.MinValue);
                }

                // One pass over the range feeds every axis's bins; each fold is an exact min or
                // max, so the bins hold what three separate passes would have put in them.
                for (int i = start; i < start + count; i++)
                {
                    ref TriangleRecord r = ref _rec[i];
                    if (binX)
                    {
                        int b = BinIndex(r.Centroid.X, cmin.X, scaleX);
                        _binCount[b]++;
                        Fold(in r, ref _binMin[b], ref _binMax[b], ref _binCentroidMin[b], ref _binCentroidMax[b]);
                    }

                    if (binY)
                    {
                        int b = Bins + BinIndex(r.Centroid.Y, cmin.Y, scaleY);
                        _binCount[b]++;
                        Fold(in r, ref _binMin[b], ref _binMax[b], ref _binCentroidMin[b], ref _binCentroidMax[b]);
                    }

                    if (binZ)
                    {
                        int b = (2 * Bins) + BinIndex(r.Centroid.Z, cmin.Z, scaleZ);
                        _binCount[b]++;
                        Fold(in r, ref _binMin[b], ref _binMax[b], ref _binCentroidMin[b], ref _binCentroidMax[b]);
                    }
                }

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
                        rcount += _binCount[binBase + b];
                        rmin = Vector3.Min(rmin, _binMin[binBase + b]);
                        rmax = Vector3.Max(rmax, _binMax[binBase + b]);
                        _rightCount[b - 1] = rcount;
                        _rightArea[b - 1] = Area(rmin, rmax);
                    }

                    // Left-to-right: fold the left side and price each split.
                    Vector3 lmin = new(float.MaxValue), lmax = new(float.MinValue);
                    int lcount = 0;
                    for (int k = 0; k < Bins - 1; k++)
                    {
                        lcount += _binCount[binBase + k];
                        lmin = Vector3.Min(lmin, _binMin[binBase + k]);
                        lmax = Vector3.Max(lmax, _binMax[binBase + k]);
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
                    _binary[idx] = BinaryNode.Leaf(bmin, bmax, start, count);
                    return idx;
                }

                return HalfSplit(idx, start, count, bmin, bmax, depth);
            }

            if (count <= MaxLeaf && parentArea * count <= bestCost)
            {
                _binary[idx] = BinaryNode.Leaf(bmin, bmax, start, count);
                return idx;
            }

            // Partition the range in place around the chosen bin, records and indices together;
            // the children's bounds and centroid bounds come from the bins, not from a rescan.
            float splitMin = bestAxis == 0 ? cmin.X : bestAxis == 1 ? cmin.Y : cmin.Z;
            int lo = start, hi = start + count - 1;
            while (lo <= hi)
            {
                if (BinIndex(Centroid(bestAxis, lo), splitMin, bestScale) <= bestBin)
                {
                    lo++;
                }
                else
                {
                    (_order[lo], _order[hi]) = (_order[hi], _order[lo]);
                    (_rec[lo], _rec[hi]) = (_rec[hi], _rec[lo]);
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
                if (_binCount[bestBase + b] == 0)
                {
                    continue;
                }

                if (b <= bestBin)
                {
                    lbmin = Vector3.Min(lbmin, _binMin[bestBase + b]);
                    lbmax = Vector3.Max(lbmax, _binMax[bestBase + b]);
                    lcmin = Vector3.Min(lcmin, _binCentroidMin[bestBase + b]);
                    lcmax = Vector3.Max(lcmax, _binCentroidMax[bestBase + b]);
                }
                else
                {
                    rbmin = Vector3.Min(rbmin, _binMin[bestBase + b]);
                    rbmax = Vector3.Max(rbmax, _binMax[bestBase + b]);
                    rcmin = Vector3.Min(rcmin, _binCentroidMin[bestBase + b]);
                    rcmax = Vector3.Max(rcmax, _binCentroidMax[bestBase + b]);
                }
            }

            int left = BuildBinary(start, leftCount, lbmin, lbmax, lcmin, lcmax, depth + 1);
            int right = BuildBinary(mid, rightCount, rbmin, rbmax, rcmin, rcmax, depth + 1);
            _binary[idx] = BinaryNode.Inner(bmin, bmax, left, right);
            return idx;
        }

        // Halves the range by index. Only for a range the SAH cannot split (coincident centroids
        // past the leaf size) or one past the depth cap; the children are scanned for their bounds
        // because no bin sweep priced them.
        private int HalfSplit(int idx, int start, int count, Vector3 bmin, Vector3 bmax, int depth)
        {
            int leftCount = count / 2;
            Vector3 lbmin = new(float.MaxValue), lbmax = new(float.MinValue);
            Vector3 lcmin = new(float.MaxValue), lcmax = new(float.MinValue);
            for (int i = start; i < start + leftCount; i++)
            {
                Fold(in _rec[i], ref lbmin, ref lbmax, ref lcmin, ref lcmax);
            }

            Vector3 rbmin = new(float.MaxValue), rbmax = new(float.MinValue);
            Vector3 rcmin = new(float.MaxValue), rcmax = new(float.MinValue);
            for (int i = start + leftCount; i < start + count; i++)
            {
                Fold(in _rec[i], ref rbmin, ref rbmax, ref rcmin, ref rcmax);
            }

            int left = BuildBinary(start, leftCount, lbmin, lbmax, lcmin, lcmax, depth + 1);
            int right = BuildBinary(start + leftCount, count - leftCount, rbmin, rbmax, rcmin, rcmax, depth + 1);
            _binary[idx] = BinaryNode.Inner(bmin, bmax, left, right);
            return idx;
        }

        // The children of the wide node that stands for the binary subtree at binaryIdx: the two
        // binary children, then repeatedly the inner one with the largest box replaced by its own
        // two, until eight remain or none is inner. Returns how many; the binary root itself is the
        // one child when it is a leaf. The counting pass and the layout pass both call this, so the
        // count cannot drift from the layout.
        private int Collapse(int binaryIdx, Span<int> children)
        {
            BinaryNode node = _binary[binaryIdx];
            if (node.IsLeaf)
            {
                children[0] = binaryIdx;
                return 1;
            }

            children[0] = node.Left;
            children[1] = node.Right;
            int n = 2;
            while (n < Width)
            {
                int pick = -1;
                float pickArea = float.MinValue;
                for (int k = 0; k < n; k++)
                {
                    BinaryNode c = _binary[children[k]];
                    if (c.IsLeaf)
                    {
                        continue;
                    }

                    float area = Area(c.Min, c.Max);
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

                BinaryNode expanded = _binary[children[pick]];
                children[pick] = expanded.Left;
                children[n++] = expanded.Right;
            }

            return n;
        }

        // Wide nodes the subtree at binaryIdx collapses into. Sizes the lane arrays before the
        // layout pass writes them.
        private int CountWide(int binaryIdx)
        {
            Span<int> children = stackalloc int[Width];
            int n = Collapse(binaryIdx, children);
            int wide = 1;
            for (int k = 0; k < n; k++)
            {
                if (!_binary[children[k]].IsLeaf)
                {
                    wide += CountWide(children[k]);
                }
            }

            return wide;
        }

        // Lays the binary subtree at binaryIdx out as one wide node and returns its index.
        // pathWaiting is the number of sibling lanes that can be waiting on the stack above this
        // node along the current path; it sizes the traversal stack exactly.
        private int BuildWide(int binaryIdx, int pathWaiting, int depth)
        {
            Span<int> children = stackalloc int[Width];
            int n = Collapse(binaryIdx, children);
            BinaryNode node = _binary[binaryIdx];

            int wide = _wideCount++;
            int laneBase = wide * Width;
            for (int l = 0; l < Width; l++)
            {
                _minX[laneBase + l] = float.PositiveInfinity;
                _minY[laneBase + l] = float.PositiveInfinity;
                _minZ[laneBase + l] = float.PositiveInfinity;
                _maxX[laneBase + l] = float.NegativeInfinity;
                _maxY[laneBase + l] = float.NegativeInfinity;
                _maxZ[laneBase + l] = float.NegativeInfinity;
                _child[laneBase + l] = EmptyLane;
            }

            int waiting = pathWaiting + (n - 1);
            _stackCapacity = Math.Max(_stackCapacity, 1 + waiting);
            _depth = Math.Max(_depth, depth);

            Span<int> laneOf = stackalloc int[Width];
            AssignLanes(children[..n], node, laneOf);

            for (int k = 0; k < n; k++)
            {
                int lane = laneBase + laneOf[k];
                BinaryNode c = _binary[children[k]];
                _minX[lane] = c.Min.X;
                _minY[lane] = c.Min.Y;
                _minZ[lane] = c.Min.Z;
                _maxX[lane] = c.Max.X;
                _maxY[lane] = c.Max.Y;
                _maxZ[lane] = c.Max.Z;
                if (c.IsLeaf)
                {
                    _child[lane] = EncodeLeaf(c.Start, c.Count);
                    for (int s = c.Start; s < c.Start + c.Count; s++)
                    {
                        _laneOfSlot[s] = lane;
                    }
                }
                else
                {
                    _child[lane] = BuildWide(children[k], waiting, depth + 1);
                }
            }

            return wide;
        }

        // Lays children into lanes by octant. Each child's preferred lane has a bit set per axis on
        // which its centre lies on the positive side of the node's; when two children want the same
        // lane, the assignment is the greedy best-score one over (child, lane) pairs, with offsets
        // scaled by the node's extent so no axis dominates by unit alone. Ties fall to the lower
        // child index, then the lower lane, so the layout is deterministic.
        private void AssignLanes(ReadOnlySpan<int> children, BinaryNode node, Span<int> laneOf)
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
                BinaryNode c = _binary[children[k]];
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

        // One triangle's box and box centre, interleaved so a fold reads one contiguous record.
        private readonly struct TriangleRecord(Vector3 min, Vector3 max, Vector3 centroid)
        {
            public readonly Vector3 Min = min;
            public readonly Vector3 Max = max;
            public readonly Vector3 Centroid = centroid;
        }

        private readonly struct BinaryNode
        {
            public readonly Vector3 Min;
            public readonly Vector3 Max;
            public readonly int Left; // inner: left child; leaf: -1
            public readonly int Right; // inner: right child
            public readonly int Start; // leaf: first slot
            public readonly int Count; // leaf: slots; 0 on an inner node

            private BinaryNode(Vector3 min, Vector3 max, int left, int right, int start, int count)
            {
                Min = min;
                Max = max;
                Left = left;
                Right = right;
                Start = start;
                Count = count;
            }

            public bool IsLeaf => Left < 0;

            public static BinaryNode Leaf(Vector3 min, Vector3 max, int start, int count) =>
                new(min, max, -1, -1, start, count);

            public static BinaryNode Inner(Vector3 min, Vector3 max, int left, int right) =>
                new(min, max, left, right, 0, 0);
        }
    }
}
