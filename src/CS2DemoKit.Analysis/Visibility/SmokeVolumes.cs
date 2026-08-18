#region

using System.Numerics;

#endregion

namespace CS2DemoKit.Analysis.Visibility;

/// <summary>
///     Dynamic <b>smoke</b> occluders for the "could-see" (vision) branch of the visibility stat. A CS2 smoke
///     is modelled as a solid sphere (centre = <c>m_vSmokeDetonationPos</c>, radius <see cref="DefaultRadius" />);
///     a sightline that passes through any active sphere is treated as vision-blocked.
///     <para>
///         <b>Why vision-only:</b> smoke blocks <i>sight</i> but not bullets, so it belongs on could-see (what
///         a player can actually SEE), never on <c>exposed</c> (geometric line of fire). exposed is a triangle
///         raycast against static map collision, in which smoke doesn't exist, so exposed is smoke-blind by
///         construction and this helper is only ever consulted for could-see. The observable consequence — a
///         smoked-off enemy reads <b>exposed but not seen</b> — is the intended, faithful model (it is exactly
///         why through-smoke kills happen), and is stated in <c>docs/3d-visibility/3d-visibility-plan.md</c>.
///     </para>
///     <para>
///         Pure <c>System.Numerics</c> (no BVH, no map dependency); the sphere test is a couple of dot products
///         per smoke, dwarfed by the triangle raycast it augments.
///     </para>
/// </summary>
public static class SmokeVolumes
{
    /// <summary>
    ///     The standard CS2 smoke blocking radius, in world units. Shared so the stat and the 2D overlay
    ///     can't drift apart on the number.
    /// </summary>
    public const float DefaultRadius = 144f;

    /// <summary>
    ///     True iff the segment <paramref name="a" />→<paramref name="b" /> passes within a sphere's radius of
    ///     its centre (i.e. the sightline enters the smoke). Endpoints inside a sphere count as blocked (a
    ///     player standing in smoke can't see out). Each sphere is <c>(centre.x, centre.y, centre.z, radius)</c>.
    /// </summary>
    public static bool SegmentBlocked(Vector3 a, Vector3 b, ReadOnlySpan<Vector4> spheres)
    {
        if (spheres.IsEmpty)
        {
            return false;
        }

        Vector3 ab = b - a;
        float abLen2 = ab.LengthSquared();

        foreach (Vector4 s in spheres)
        {
            Vector3 centre = new(s.X, s.Y, s.Z);
            float r = s.W;
            // Closest point on the segment to the sphere centre (t clamped to the segment, so endpoints
            // inside the sphere are handled — degenerate zero-length segment falls back to endpoint a).
            float t = abLen2 > 1e-6f ? Math.Clamp(Vector3.Dot(centre - a, ab) / abLen2, 0f, 1f) : 0f;
            Vector3 closest = a + ab * t;
            if (Vector3.DistanceSquared(closest, centre) <= r * r)
            {
                return true;
            }
        }

        return false;
    }
}
