#region

using System.Diagnostics;
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

    /// <summary>
    ///     Loads a baked <c>collision.tris</c> and builds the BVH on up to one thread per processor.
    ///     Do this off the UI thread: the build is tenths of a second on the large bakes, and the
    ///     calling thread works alongside the pool for all of it. A caller that wants the build
    ///     kept to fewer threads loads the bake with <see cref="CollisionTris.Load(string)" /> and passes
    ///     the degree to <see cref="FromTriangles(float[], int, int)" />.
    /// </summary>
    public static VisibilityEngine Load(string trisPath)
    {
        // Split rather than one span around the pair: reading 35 MB off disk and building a tree over
        // it are different problems with different fixes (a cache for the first, a better builder for
        // the second), and a single number cannot tell you which one you have.
        long readStart = Stopwatch.GetTimestamp();
        CollisionTris.Data d = CollisionTris.Load(trisPath);
        long built = Stopwatch.GetTimestamp();
        VisibilityEngine engine = FromTriangles(d.Vertices, d.TriangleCount);
        VisibilityCounters.RecordBake(built - readStart, Stopwatch.GetTimestamp() - built, d.TriangleCount);
        return engine;
    }

    /// <summary>Builds from an in-memory triangle soup (9 floats/triangle in <paramref name="vertices" />) on up to one thread per processor.</summary>
    public static VisibilityEngine FromTriangles(float[] vertices, int triangleCount) =>
        FromTriangles(vertices, triangleCount, 0);

    /// <summary>
    ///     <see cref="FromTriangles(float[], int)" /> on at most <paramref name="maxDegreeOfParallelism" />
    ///     threads. The tree is the same at every degree; only the wall-clock changes. Pass one
    ///     from a caller that is already saturating the pool (an analysis run's parallel scan,
    ///     say) and wants the build kept to its own thread.
    /// </summary>
    /// <param name="vertices">Triangle soup, 9 floats per triangle.</param>
    /// <param name="triangleCount">Triangles packed in <paramref name="vertices" />.</param>
    /// <param name="maxDegreeOfParallelism">Threads the build may use; zero or negative means <see cref="Environment.ProcessorCount" />.</param>
    public static VisibilityEngine FromTriangles(float[] vertices, int triangleCount, int maxDegreeOfParallelism) =>
        new(TriangleBvh.Build(vertices, triangleCount, maxDegreeOfParallelism));

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
    ///     <see cref="IsVisible(Vector3, Vector3)" /> with a last-occluder hint, for a caller that
    ///     asks about the same sightline again and again: <paramref name="hint" /> is the triangle
    ///     that blocked it last time (or -1), it is tested before the BVH is entered, and the
    ///     triangle that blocks it this time is written back. The answer is identical to the
    ///     hint-free overload's on every ray, whatever the hint holds, see
    ///     <see cref="TriangleBvh.AnyHit(Vector3, Vector3, float, float, ref int)" />; only the
    ///     work changes.
    /// </summary>
    /// <param name="a">Segment start (the viewer's eye).</param>
    /// <param name="b">Segment end (the body anchor).</param>
    /// <param name="hint">In: the triangle to try first, or -1. Out: the blocking triangle when not visible, else unchanged.</param>
    public bool IsVisible(Vector3 a, Vector3 b, ref int hint) => IsVisible(a, b, ref hint, out _);

    // shortCircuited is true when the hinted triangle decided the ray without a traversal; it
    // exists for the ray budget counters and feeds nothing else.
    internal bool IsVisible(Vector3 a, Vector3 b, ref int hint, out bool shortCircuited)
    {
        shortCircuited = false;
        Vector3 d = b - a;
        float len = d.Length();
        if (len <= 2f * SegmentEps)
        {
            return true;
        }

        Vector3 dir = d / len;
        return !_bvh.AnyHit(a, dir, len, SegmentEps, ref hint, out shortCircuited);
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
