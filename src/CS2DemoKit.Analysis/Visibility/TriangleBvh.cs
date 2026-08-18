#region

using System.Numerics;

#endregion

namespace CS2DemoKit.Analysis.Visibility;

/// <summary>
///     A bounding-volume hierarchy over a world-collision triangle soup, built once and queried per ray.
///     Median-split on the largest-spread centroid axis (awpy's construction). Two query modes:
///     <see cref="AnyHit" /> (early-exit occlusion — the hot path for line-of-sight) and
///     <see cref="NearestHit" /> (closest surface — used by the ray-down frame gate). Directions are
///     <b>unit vectors</b> and <c>t</c> is in <b>world units</b>, so hit distances read directly.
///     Pure geometry; parser-blind; allocation-free per query (stackalloc traversal stack).
/// </summary>
public sealed class TriangleBvh
{
    private const int LeafSize = 4;
    private const int MaxDepth = 64; // median split ⇒ ~log2(n/LeafSize); 64 is a safe traversal-stack bound.
    private readonly Node[] _nodes;
    private readonly int[] _order; // triangle indices grouped by leaf

    private readonly float[] _v; // 9 floats per triangle (world verts), original order

    private TriangleBvh(float[] v, int[] order, Node[] nodes, int triangleCount)
    {
        _v = v;
        _order = order;
        _nodes = nodes;
        TriangleCount = triangleCount;
    }

    public int TriangleCount { get; }
    public Vector3 Min => _nodes.Length > 0 ? _nodes[0].Min : Vector3.Zero;
    public Vector3 Max => _nodes.Length > 0 ? _nodes[0].Max : Vector3.Zero;

    /// <summary>Builds the BVH over <paramref name="count" /> triangles packed as 9 floats each in <paramref name="v" />.</summary>
    public static TriangleBvh Build(float[] v, int count)
    {
        int[] order = new int[count];
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
            BuildRecursive(0, count, order, cx, cy, cz, tmin, tmax, nodes);
        }

        return new TriangleBvh(v, order, nodes.ToArray(), count);
    }

    private static int BuildRecursive(int start, int count, int[] order,
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
            return nodeIdx;
        }

        // Split on the widest centroid axis at the median.
        Vector3 spread = cmax - cmin;
        int axis = spread.X >= spread.Y && spread.X >= spread.Z ? 0 : spread.Y >= spread.Z ? 1 : 2;
        float[] key = axis == 0 ? cx : axis == 1 ? cy : cz;

        if (spread.X <= 0 && spread.Y <= 0 && spread.Z <= 0)
        {
            // Degenerate: all centroids coincide — make a leaf rather than recurse forever.
            nodes[nodeIdx] = new Node
            {
                Min = bmin,
                Max = bmax,
                Left = -1,
                Start = start,
                Count = count
            };
            return nodeIdx;
        }

        Array.Sort(order, start, count, Comparer<int>.Create((p, q) => key[p].CompareTo(key[q])));
        int mid = count / 2;

        int left = BuildRecursive(start, mid, order, cx, cy, cz, tmin, tmax, nodes);
        int right = BuildRecursive(start + mid, count - mid, order, cx, cy, cz, tmin, tmax, nodes);
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
    ///     True iff some triangle is hit by the ray <c>origin + t·dir</c> for <c>t</c> in
    ///     <c>(eps, tMax − eps)</c>. Early-exits on the first hit — the occlusion test for line-of-sight.
    /// </summary>
    public bool AnyHit(Vector3 origin, Vector3 dir, float tMax, float eps)
    {
        if (_nodes.Length == 0)
        {
            return false;
        }

        Vector3 inv = new(1f / dir.X, 1f / dir.Y, 1f / dir.Z);
        float lo = eps, hi = tMax - eps;
        if (hi <= lo)
        {
            return false;
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
    ///     Nearest triangle hit along <c>origin + t·dir</c> for <c>t</c> in <c>(eps, tMax)</c>. Returns the
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

    // Ray/AABB slab test over parameter window [lo,hi]. inv = 1/dir per component (±Inf if dir component 0,
    // which the min/max ordering handles correctly for axis-aligned rays).
    private static bool SlabHit(Vector3 bmin, Vector3 bmax, Vector3 o, Vector3 inv, float lo, float hi)
    {
        float t0 = (bmin.X - o.X) * inv.X, t1 = (bmax.X - o.X) * inv.X;
        if (t0 > t1)
        {
            (t0, t1) = (t1, t0);
        }

        lo = Math.Max(lo, t0);
        hi = Math.Min(hi, t1);
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

        lo = Math.Max(lo, t0);
        hi = Math.Min(hi, t1);
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

        lo = Math.Max(lo, t0);
        hi = Math.Min(hi, t1);
        return lo <= hi;
    }

    // Möller-Trumbore, general direction (need not be unit, but callers pass unit ⇒ t in world units).
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
