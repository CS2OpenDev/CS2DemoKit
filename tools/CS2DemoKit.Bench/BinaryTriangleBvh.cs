#region

using System.Numerics;

#endregion

namespace CS2DemoKit.Bench;

/// <summary>
///     The binary median-split tree CS2DemoKit.Analysis shipped before the eight-wide rebuild,
///     copied here so the rays verb can report the old topology and the eight-wide one side by side
///     on the same corpus. The topology, the median split and the leaf gather through an order array
///     are that tree's. Three things are not, and all three arrived with the rebuild:
///     <list type="bullet">
///         <item><see cref="AnyHitCounted" />, which tallies nodes popped and triangles tested.</item>
///         <item>
///             <c>_leafOf</c> and the hinted
///             <see cref="AnyHit(Vector3, Vector3, float, float, ref int)" /> overload, which tests
///             the last occluder's leaf before entering the tree and short-circuits on a hit.
///         </item>
///         <item>
///             <c>MaxNoNaN</c>/<c>MinNoNaN</c> in the slab window. The old tree used
///             <c>Math.Max</c>/<c>Math.Min</c>, which propagate the NaN an on-plane ray produces and
///             report clear through solid geometry.
///         </item>
///     </list>
///     So a rays row is old topology against new topology with the hint and the NaN fix held
///     constant on both sides. That isolates the topology change, which is what the row is for, and
///     it is not an end-to-end before-and-after of the shipped engine: the old engine had neither
///     addition, so its real cost per ray was higher than this arm's.
///     Nothing pins this copy against <c>TriangleBvh</c>, so a later change to the shipped
///     traversal will not fail a test here and the two can drift apart unnoticed. That is the point
///     of a frozen arm rather than a defect in it, but it does mean a row describes this file and
///     not whatever the library shipped last.
///     Not shipped; a measurement arm only.
/// </summary>
internal sealed class BinaryTriangleBvh
{
    private const int LeafSize = 4;
    private const int MaxDepth = 64; // median split so ~log2(n/LeafSize); 64 is a safe traversal-stack bound.
    private readonly Node[] _nodes;
    private readonly int[] _order; // triangle indices grouped by leaf
    private readonly int[] _leafOf; // leaf node index per triangle, for the hinted test

    private readonly float[] _v; // 9 floats per triangle (world verts), original order

    private BinaryTriangleBvh(float[] v, int[] order, int[] leafOf, Node[] nodes, int triangleCount)
    {
        _v = v;
        _order = order;
        _leafOf = leafOf;
        _nodes = nodes;
        TriangleCount = triangleCount;
    }

    public int TriangleCount { get; }

    /// <summary>Nodes in the tree, so the two topologies' sizes can be read off one build row.</summary>
    public int NodeCount => _nodes.Length;

    public Vector3 Min => _nodes.Length > 0 ? _nodes[0].Min : Vector3.Zero;
    public Vector3 Max => _nodes.Length > 0 ? _nodes[0].Max : Vector3.Zero;

    /// <summary>Builds the BVH over <paramref name="count" /> triangles packed as 9 floats each in <paramref name="v" />.</summary>
    public static BinaryTriangleBvh Build(float[] v, int count)
    {
        int[] order = new int[count];
        int[] leafOf = new int[count];
        float[] cx = new float[count];
        float[] cy = new float[count];
        float[] cz = new float[count];
        Vector3[] tmin = new Vector3[count];
        Vector3[] tmax = new Vector3[count];

        for (int i = 0; i < count; i++)
        {
            order[i] = i;
            int b = i * 9;
            Vector3 a = new(v[b], v[b + 1], v[b + 2]);
            Vector3 bb = new(v[b + 3], v[b + 4], v[b + 5]);
            Vector3 c = new(v[b + 6], v[b + 7], v[b + 8]);
            Vector3 lo = Vector3.Min(a, Vector3.Min(bb, c));
            Vector3 hi = Vector3.Max(a, Vector3.Max(bb, c));
            tmin[i] = lo;
            tmax[i] = hi;
            Vector3 ctr = (a + bb + c) / 3f;
            cx[i] = ctr.X;
            cy[i] = ctr.Y;
            cz[i] = ctr.Z;
        }

        List<Node> nodes = new(Math.Max(1, count / 2));
        if (count == 0)
        {
            nodes.Add(new Node
            {
                Min = Vector3.Zero,
                Max = Vector3.Zero,
                Left = -1,
                Start = 0,
                Count = 0
            });
        }
        else
        {
            BuildRecursive(0, count, order, leafOf, cx, cy, cz, tmin, tmax, nodes);
        }

        return new BinaryTriangleBvh(v, order, leafOf, nodes.ToArray(), count);
    }

    private static int BuildRecursive(int start, int count, int[] order, int[] leafOf,
        float[] cx, float[] cy, float[] cz, Vector3[] tmin, Vector3[] tmax, List<Node> nodes)
    {
        // Reserve this node's slot BEFORE recursing (children are appended after it).
        int nodeIdx = nodes.Count;
        nodes.Add(default);

        // Bounds = union of member triangle AABBs; also track centroid spread for the split axis.
        Vector3 bmin = new(float.MaxValue);
        Vector3 bmax = new(float.MinValue);
        Vector3 cmin = new(float.MaxValue);
        Vector3 cmax = new(float.MinValue);
        for (int i = start; i < start + count; i++)
        {
            int t = order[i];
            bmin = Vector3.Min(bmin, tmin[t]);
            bmax = Vector3.Max(bmax, tmax[t]);
            Vector3 ctr = new(cx[t], cy[t], cz[t]);
            cmin = Vector3.Min(cmin, ctr);
            cmax = Vector3.Max(cmax, ctr);
        }

        if (count <= LeafSize)
        {
            nodes[nodeIdx] = new Node
            {
                Min = bmin,
                Max = bmax,
                Left = -1,
                Start = start,
                Count = count
            };
            for (int i = start; i < start + count; i++)
            {
                leafOf[order[i]] = nodeIdx;
            }

            return nodeIdx;
        }

        // Split on the widest centroid axis at the median.
        Vector3 spread = cmax - cmin;
        int axis = spread.X >= spread.Y && spread.X >= spread.Z ? 0 : spread.Y >= spread.Z ? 1 : 2;
        float[] key = axis == 0 ? cx : axis == 1 ? cy : cz;

        if (spread.X <= 0 && spread.Y <= 0 && spread.Z <= 0)
        {
            // Degenerate, all centroids coincide: make a leaf rather than recurse forever.
            nodes[nodeIdx] = new Node
            {
                Min = bmin,
                Max = bmax,
                Left = -1,
                Start = start,
                Count = count
            };
            for (int i = start; i < start + count; i++)
            {
                leafOf[order[i]] = nodeIdx;
            }

            return nodeIdx;
        }

        Array.Sort(order, start, count, Comparer<int>.Create((p, q) => key[p].CompareTo(key[q])));
        int mid = count / 2;

        int left = BuildRecursive(start, mid, order, leafOf, cx, cy, cz, tmin, tmax, nodes);
        int right = BuildRecursive(start + mid, count - mid, order, leafOf, cx, cy, cz, tmin, tmax, nodes);
        nodes[nodeIdx] = new Node
        {
            Min = bmin,
            Max = bmax,
            Left = left,
            Right = right,
            Start = 0,
            Count = 0
        };
        return nodeIdx;
    }

    /// <summary>
    ///     True iff some triangle is hit by the ray <c>origin + t*dir</c> for <c>t</c> in
    ///     <c>(eps, tMax - eps)</c>. Early-exits on the first hit: the occlusion test for line-of-sight.
    /// </summary>
    public bool AnyHit(Vector3 origin, Vector3 dir, float tMax, float eps)
    {
        int hint = -1;
        return AnyHit(origin, dir, tMax, eps, ref hint, out _);
    }

    /// <summary>
    ///     <see cref="AnyHit(Vector3, Vector3, float, float)" /> with a last-occluder hint: the
    ///     triangle at index <paramref name="hint" /> is tested before the tree is entered, and
    ///     whichever triangle ends up blocking the ray is written back to it. Pass -1 (or any index
    ///     outside <c>0..TriangleCount-1</c>) for no hint.
    ///     <para>
    ///         The verdict is identical to the plain overload's on every ray, by construction and
    ///         not only in practice. The hinted test is the hinted triangle's leaf box through
    ///         <c>SlabHit</c> and then the triangle through <c>RayTriangle</c>, with the same
    ///         <c>inv</c>, the same <c>lo</c> and the same <c>hi</c> the traversal below uses. The
    ///         traversal reaches a leaf iff the leaf box and every box above it pass the slab test,
    ///         and every box above contains the leaf box, so a passing leaf means passing ancestors:
    ///         the slab test is monotone in the bounds for a finite or positive-infinite <c>inv</c>
    ///         component (a wider face moves its crossing outward, and a face on the origin's plane
    ///         leaves the window as it was), and with a negative-infinite one the only leaf that can
    ///         pass while a box above it fails is flat on the origin's plane, whose triangles a ray
    ///         lying in that plane cannot hit. A hinted triangle that hits inside a passing leaf is
    ///         therefore one the traversal would have tested with the identical predicate, so a
    ///         hinted true is a traversal true; a hinted miss runs the traversal unchanged.
    ///     </para>
    ///     <para>
    ///         The leaf test is load-bearing, not decoration. The slab crossing time for a node face
    ///         and Moller-Trumbore's hit time for a triangle on that face come from different float
    ///         arithmetic and can land a few ulp apart, so the traversal rejects a node whose
    ///         triangle the ray really hits when the segment's far limit falls in that gap. Testing
    ///         the triangle alone would answer occluded there: the geometrically right answer, but
    ///         not the traversal's, and every fixture pins the traversal.
    ///         <c>OccluderHintTests.HintOnATriangleTheTraversalWouldMiss_TakesTheTraversal</c>
    ///         constructs a ray of that class. A replacement tree (a wider or SIMD one) must keep
    ///         this leaf test, with its own slab arithmetic, for the identity to survive.
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
        shortCircuited = false;
        if (_nodes.Length == 0)
        {
            return false;
        }

        float lo = eps, hi = tMax - eps;
        if (hi <= lo)
        {
            return false;
        }

        Vector3 inv = new(1f / dir.X, 1f / dir.Y, 1f / dir.Z);

        // The hinted test: the hinted triangle's leaf through the same SlabHit, then the triangle
        // through the same RayTriangle, against the same inv, lo and hi as the traversal below. See
        // the public overload's doc for why that, and only that, makes a hinted true a traversal true.
        if ((uint)hint < (uint)TriangleCount)
        {
            ref Node leaf = ref _nodes[_leafOf[hint]];
            if (SlabHit(leaf.Min, leaf.Max, origin, inv, lo, hi)
                && RayTriangle(origin, dir, hint, out float hintT) && hintT > lo && hintT < hi)
            {
                shortCircuited = true;
                return true;
            }
        }

        Span<int> stack = stackalloc int[MaxDepth];
        int sp = 0;
        stack[sp++] = 0;
        while (sp > 0)
        {
            ref Node n = ref _nodes[stack[--sp]];
            if (!SlabHit(n.Min, n.Max, origin, inv, lo, hi))
            {
                continue;
            }

            if (n.Count > 0)
            {
                for (int i = n.Start; i < n.Start + n.Count; i++)
                {
                    if (RayTriangle(origin, dir, _order[i], out float t) && t > lo && t < hi)
                    {
                        hint = _order[i];
                        return true;
                    }
                }
            }
            else
            {
                stack[sp++] = n.Left;
                stack[sp++] = n.Right;
            }
        }

        return false;
    }

    /// <summary>
    ///     <see cref="AnyHit(Vector3, Vector3, float, float)" /> with a tally of nodes popped (each one slab
    ///     test) and triangles tested, for the rays verb.
    /// </summary>
    public bool AnyHitCounted(Vector3 origin, Vector3 dir, float tMax, float eps, ref long nodes, ref long triangles)
    {
        if (_nodes.Length == 0)
        {
            return false;
        }

        float lo = eps, hi = tMax - eps;
        if (hi <= lo)
        {
            return false;
        }

        Vector3 inv = new(1f / dir.X, 1f / dir.Y, 1f / dir.Z);
        Span<int> stack = stackalloc int[MaxDepth];
        int sp = 0;
        stack[sp++] = 0;
        while (sp > 0)
        {
            ref Node n = ref _nodes[stack[--sp]];
            nodes++;
            if (!SlabHit(n.Min, n.Max, origin, inv, lo, hi))
            {
                continue;
            }

            if (n.Count > 0)
            {
                for (int i = n.Start; i < n.Start + n.Count; i++)
                {
                    triangles++;
                    if (RayTriangle(origin, dir, _order[i], out float t) && t > lo && t < hi)
                    {
                        return true;
                    }
                }
            }
            else
            {
                stack[sp++] = n.Left;
                stack[sp++] = n.Right;
            }
        }

        return false;
    }

    /// <summary>
    ///     Nearest triangle hit along <c>origin + t*dir</c> for <c>t</c> in <c>(eps, tMax)</c>. Returns the
    ///     smallest such <c>t</c>, or false if nothing is hit. Used by the coordinate-frame ray-down gate.
    /// </summary>
    public bool NearestHit(Vector3 origin, Vector3 dir, float tMax, float eps, out float distance)
    {
        distance = float.MaxValue;
        if (_nodes.Length == 0)
        {
            return false;
        }

        Vector3 inv = new(1f / dir.X, 1f / dir.Y, 1f / dir.Z);
        bool hit = false;

        Span<int> stack = stackalloc int[MaxDepth];
        int sp = 0;
        stack[sp++] = 0;
        while (sp > 0)
        {
            ref Node n = ref _nodes[stack[--sp]];
            // Prune against the current best distance as it tightens.
            if (!SlabHit(n.Min, n.Max, origin, inv, eps, hit ? distance : tMax))
            {
                continue;
            }

            if (n.Count > 0)
            {
                for (int i = n.Start; i < n.Start + n.Count; i++)
                {
                    if (RayTriangle(origin, dir, _order[i], out float t) && t > eps && t < tMax && t < distance)
                    {
                        distance = t;
                        hit = true;
                    }
                }
            }
            else
            {
                stack[sp++] = n.Left;
                stack[sp++] = n.Right;
            }
        }

        return hit;
    }

    // Ray/AABB slab test over parameter window [lo,hi]. inv = 1/dir per component, so a dir component of
    // zero gives +/-Inf. That is fine on its own: the products run off to +/-Inf in the right direction and
    // the slab constrains nothing, which is correct for a ray that never moves along that axis.
    //
    // It is NOT fine when the numerator is also zero, i.e. the origin lies exactly on one of that axis's
    // slab planes. Inf * 0 is NaN, System.Math.Max and System.Math.Min PROPAGATE NaN, every later
    // comparison against it is false, and the node is rejected: the ray reports clear through solid
    // geometry. Two players on one floor produce an eye-to-eye ray with dir.Z exactly zero, and map
    // brushes sit at round heights, so this lands often enough to measure (12 of 3000 sampled same-floor
    // rays on de_nuke before the fix). See SlabDegeneracyTests.
    //
    // MaxNoNaN/MinNoNaN keep the window unchanged on a NaN instead, which is the right answer for the only
    // case that produces one. They also match SSE minps/maxps operand semantics, so a vectorised slab test
    // can replace this one without changing a single verdict.
    private static bool SlabHit(Vector3 bmin, Vector3 bmax, Vector3 o, Vector3 inv, float lo, float hi)
    {
        float t0 = (bmin.X - o.X) * inv.X, t1 = (bmax.X - o.X) * inv.X;
        if (t0 > t1)
        {
            (t0, t1) = (t1, t0);
        }

        lo = MaxNoNaN(lo, t0);
        hi = MinNoNaN(hi, t1);
        if (lo > hi)
        {
            return false;
        }

        t0 = (bmin.Y - o.Y) * inv.Y;
        t1 = (bmax.Y - o.Y) * inv.Y;
        if (t0 > t1)
        {
            (t0, t1) = (t1, t0);
        }

        lo = MaxNoNaN(lo, t0);
        hi = MinNoNaN(hi, t1);
        if (lo > hi)
        {
            return false;
        }

        t0 = (bmin.Z - o.Z) * inv.Z;
        t1 = (bmax.Z - o.Z) * inv.Z;
        if (t0 > t1)
        {
            (t0, t1) = (t1, t0);
        }

        lo = MaxNoNaN(lo, t0);
        hi = MinNoNaN(hi, t1);
        return lo <= hi;
    }

    // Window updates that ignore a NaN candidate rather than propagating it. Written as a plain
    // comparison on purpose: `b > a` is false when b is NaN, so the incumbent survives. Math.Max would
    // return the NaN. See the SlabHit comment for why a NaN gets here at all.
    private static float MaxNoNaN(float a, float b) => b > a ? b : a;

    private static float MinNoNaN(float a, float b) => b < a ? b : a;

    // Möller-Trumbore, general direction (need not be unit, but callers pass unit so t in world units).
    private bool RayTriangle(Vector3 o, Vector3 d, int tri, out float t)
    {
        t = 0;
        int b = tri * 9;
        float[] v = _v;
        Vector3 a = new(v[b], v[b + 1], v[b + 2]);
        Vector3 e1 = new(v[b + 3] - a.X, v[b + 4] - a.Y, v[b + 5] - a.Z);
        Vector3 e2 = new(v[b + 6] - a.X, v[b + 7] - a.Y, v[b + 8] - a.Z);

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

    private struct Node
    {
        public Vector3 Min;
        public Vector3 Max;
        public int Left; // internal: left child node index; leaf: -1
        public int Right; // internal: right child node index; leaf: unused
        public int Start; // leaf: first index into _order
        public int Count; // leaf: triangle count (0 on internal nodes)
    }
}
