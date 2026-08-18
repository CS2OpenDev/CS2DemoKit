#region

using System.Numerics;

#endregion

namespace CS2DemoKit.Analysis.Visibility;

/// <summary>
///     VRF-free 3D line-of-sight engine: given the baked world-collision triangles it answers
///     "is point B visible from point A?" (segment clear of geometry) — the primitive behind the
///     "time enemy was visible" stat. Recomputes true line of sight from map geometry (engine-fidelity),
///     NOT the demo's <c>spotted</c> bit. Build once (BVH), query per ray. Thread-safe for concurrent
///     queries (immutable after construction). See <c>docs/3d-visibility/3d-visibility-plan.md</c>.
/// </summary>
public sealed class VisibilityEngine
{
    // Exclude occluders within this many world units of either endpoint (endpoints are free-space eye /
    // hitbox anchors, so this only guards numeric coincidence, never real cover).
    private const float SegmentEps = 0.1f;
    private const float RayDownEps = 1e-3f;

    private readonly TriangleBvh _bvh;

    private VisibilityEngine(TriangleBvh bvh) => _bvh = bvh;

    public int TriangleCount => _bvh.TriangleCount;
    public Vector3 Min => _bvh.Min;
    public Vector3 Max => _bvh.Max;

    /// <summary>Loads a baked <c>collision.tris</c> and builds the BVH. Do this off the UI thread (BVH build is O(seconds)).</summary>
    public static VisibilityEngine Load(string trisPath)
    {
        CollisionTris.Data d = CollisionTris.Load(trisPath);
        return FromTriangles(d.Vertices, d.TriangleCount);
    }

    /// <summary>Builds from an in-memory triangle soup (9 floats/triangle in <paramref name="vertices" />).</summary>
    public static VisibilityEngine FromTriangles(float[] vertices, int triangleCount) =>
        new(TriangleBvh.Build(vertices, triangleCount));

    /// <summary>
    ///     True iff the straight segment <paramref name="a" />→<paramref name="b" /> is clear of collision
    ///     geometry (the two points are mutually visible). Coincident points are trivially visible.
    /// </summary>
    public bool IsVisible(Vector3 a, Vector3 b)
    {
        Vector3 d = b - a;
        float len = d.Length();
        if (len <= 2f * SegmentEps)
        {
            return true;
        }

        Vector3 dir = d / len;
        return !_bvh.AnyHit(a, dir, len, SegmentEps);
    }

    /// <summary>
    ///     Distance straight down from <paramref name="origin" /> to the nearest collision triangle within
    ///     <paramref name="maxDrop" /> units, or false if none. Used by the coordinate-frame gate.
    /// </summary>
    public bool RayDownDistance(Vector3 origin, float maxDrop, out float distance) =>
        _bvh.NearestHit(origin, new Vector3(0f, 0f, -1f), maxDrop, RayDownEps, out distance);

    /// <summary>General nearest-hit raycast (unit <paramref name="dir" />; <c>t</c> in world units).</summary>
    public bool Raycast(Vector3 origin, Vector3 dir, float maxDist, out float distance) =>
        _bvh.NearestHit(origin, Vector3.Normalize(dir), maxDist, RayDownEps, out distance);
}
