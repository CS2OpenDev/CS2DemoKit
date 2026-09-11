#region

using System.Numerics;

#endregion

namespace CS2DemoKit.Analysis.Tests;

/// <summary>
///     The ground truth a disputed ray is adjudicated against: every triangle tested, no acceleration
///     structure, no early exit.
///     <para>
///         Exact by construction, and deliberately so. A BVH can only be wrong by rejecting a node it
///         should have entered, so an oracle that shares the BVH's traversal would share its blind
///         spot. This shares only the intersection body and the <c>(lo, hi)</c> predicate, which are
///         the parts whose agreement we actually want.
///     </para>
///     <para>
///         O(n) per ray against up to a million triangles, so it is for corpora of thousands of rays,
///         never for a demo.
///     </para>
/// </summary>
internal static class BruteForceOracle
{
    /// <summary>
    ///     True iff some triangle is hit for <c>t</c> in <c>(lo, hi)</c>, with the identical
    ///     Moller-Trumbore body and open-interval predicate <c>TriangleBvh.AnyHit</c> uses.
    /// </summary>
    /// <param name="vertices">Triangle soup, 9 floats per triangle.</param>
    /// <param name="triangleCount">Number of triangles packed in <paramref name="vertices" />.</param>
    /// <param name="origin">Ray origin.</param>
    /// <param name="dir">Unit ray direction.</param>
    /// <param name="tMax">Far limit, in world units.</param>
    /// <param name="eps">Endpoint exclusion, matching the caller's.</param>
    public static bool AnyHit(
        float[] vertices, int triangleCount, Vector3 origin, Vector3 dir, float tMax, float eps)
    {
        ArgumentNullException.ThrowIfNull(vertices);
        float lo = eps, hi = tMax - eps;
        if (hi <= lo)
        {
            return false;
        }

        for (int tri = 0; tri < triangleCount; tri++)
        {
            if (RayTriangle(vertices, origin, dir, tri, out float t) && t > lo && t < hi)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    ///     Index of the first triangle hit in <c>(lo, hi)</c>, or -1. Reported alongside a disputed
    ///     verdict so a failure names the geometry rather than only the ray.
    /// </summary>
    /// <param name="vertices">Triangle soup, 9 floats per triangle.</param>
    /// <param name="triangleCount">Number of triangles packed in <paramref name="vertices" />.</param>
    /// <param name="origin">Ray origin.</param>
    /// <param name="dir">Unit ray direction.</param>
    /// <param name="tMax">Far limit, in world units.</param>
    /// <param name="eps">Endpoint exclusion, matching the caller's.</param>
    public static int FirstHitTriangle(
        float[] vertices, int triangleCount, Vector3 origin, Vector3 dir, float tMax, float eps)
    {
        ArgumentNullException.ThrowIfNull(vertices);
        float lo = eps, hi = tMax - eps;
        for (int tri = 0; tri < triangleCount; tri++)
        {
            if (RayTriangle(vertices, origin, dir, tri, out float t) && t > lo && t < hi)
            {
                return tri;
            }
        }

        return -1;
    }

    /// <summary>
    ///     True iff <paramref name="triangle" /> alone is hit for <c>t</c> in <c>(lo, hi)</c>: the
    ///     check a last-occluder hint is held to after an occluded ray, since the hint is only
    ///     allowed to name a triangle that blocks the ray it was written for.
    /// </summary>
    /// <param name="vertices">Triangle soup, 9 floats per triangle.</param>
    /// <param name="triangle">Index of the triangle to test.</param>
    /// <param name="origin">Ray origin.</param>
    /// <param name="dir">Unit ray direction.</param>
    /// <param name="tMax">Far limit, in world units.</param>
    /// <param name="eps">Endpoint exclusion, matching the caller's.</param>
    public static bool Blocks(
        float[] vertices, int triangle, Vector3 origin, Vector3 dir, float tMax, float eps)
    {
        ArgumentNullException.ThrowIfNull(vertices);
        float lo = eps, hi = tMax - eps;
        return hi > lo && RayTriangle(vertices, origin, dir, triangle, out float t) && t > lo && t < hi;
    }

    /// <summary>
    ///     The smallest hit <c>t</c> in <c>(eps, tMax)</c> over every triangle, with the identical
    ///     body and predicate <c>TriangleBvh.NearestHit</c> uses per triangle, and the triangle that
    ///     produced it. False, <see cref="float.MaxValue" /> and -1 when nothing is hit.
    /// </summary>
    /// <param name="vertices">Triangle soup, 9 floats per triangle.</param>
    /// <param name="triangleCount">Number of triangles packed in <paramref name="vertices" />.</param>
    /// <param name="origin">Ray origin.</param>
    /// <param name="dir">Unit ray direction.</param>
    /// <param name="tMax">Far limit, in world units.</param>
    /// <param name="eps">Near exclusion, matching the caller's.</param>
    /// <param name="distance">The nearest hit's <c>t</c>.</param>
    /// <param name="triangle">The triangle hit at <paramref name="distance" />, or -1.</param>
    public static bool NearestHit(
        float[] vertices, int triangleCount, Vector3 origin, Vector3 dir, float tMax, float eps,
        out float distance, out int triangle)
    {
        ArgumentNullException.ThrowIfNull(vertices);
        distance = float.MaxValue;
        triangle = -1;
        for (int tri = 0; tri < triangleCount; tri++)
        {
            if (RayTriangle(vertices, origin, dir, tri, out float t) && t > eps && t < tMax && t < distance)
            {
                distance = t;
                triangle = tri;
            }
        }

        return triangle >= 0;
    }

    // Moller-Trumbore, copied from TriangleBvh.RayTriangle so the two cannot disagree about what a
    // hit is. Only the storage differs (a plain array rather than the BVH's field).
    private static bool RayTriangle(float[] v, Vector3 o, Vector3 d, int tri, out float t)
    {
        t = 0;
        int b = tri * 9;
        Vector3 a = new(v[b], v[b + 1], v[b + 2]);
        Vector3 e1 = new(v[b + 3] - a.X, v[b + 4] - a.Y, v[b + 5] - a.Z);
        Vector3 e2 = new(v[b + 6] - a.X, v[b + 7] - a.Y, v[b + 8] - a.Z);

        Vector3 p = Vector3.Cross(d, e2);
        float det = Vector3.Dot(e1, p);
        if (det is > -1e-8f and < 1e-8f)
        {
            return false;
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
}
