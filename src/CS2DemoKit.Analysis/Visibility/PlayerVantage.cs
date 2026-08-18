#region

using System.Numerics;

#endregion

namespace CS2DemoKit.Analysis.Visibility;

/// <summary>
///     Pure eye / view-direction / hitbox-anchor geometry for the visibility stat — the pieces most prone to
///     convention bugs (angle order, degrees vs radians, duck scaling, lateral axis), kept dependency-free so
///     they're exact-value unit-testable without a demo. All inputs are plain values (feet origin, Source
///     <c>QAngle</c>, duck amount); the EntityState→values extraction is thin glue done by the caller.
///     World is Z-up, units are Source units. See <c>docs/3d-visibility/3d-visibility-plan.md</c> §2–3.
/// </summary>
public static class PlayerVantage
{
    // Eye height above feet: standing 64u, fully crouched 46u (CS2). Interpolated by the live duck amount.
    public const float EyeStanding = 64f;
    public const float EyeCrouched = 46f;

    private const float DegToRad = MathF.PI / 180f;

    // Hitbox anchor template (standing heights above feet). Chest first so "any-clear" exposed early-exits on
    // the most-likely-visible point. Crouch scales heights toward the crouched eye ratio; lateral (shoulder)
    // offset is a fixed world width (body width ~constant regardless of facing/crouch).
    private const float ChestZ = 48f;
    private const float HeadZ = 64f;
    private const float PelvisZ = 32f;
    private const float KneeZ = 16f;
    private const float ShoulderZ = 54f;
    private const float ShoulderHalfWidth = 16f;

    /// <summary>Max anchors <see cref="BuildAnchors" /> can emit (size the caller's span to this).</summary>
    public const int MaxAnchors = 6;

    /// <summary>Eye world position: feet + interpolated eye height by <paramref name="duckAmount" /> (0=stand..1=crouch).</summary>
    public static Vector3 Eye(Vector3 feet, float duckAmount)
    {
        float t = Math.Clamp(duckAmount, 0f, 1f);
        return new Vector3(feet.X, feet.Y, feet.Z + Lerp(EyeStanding, EyeCrouched, t));
    }

    /// <summary>
    ///     Forward unit vector from a Source <c>QAngle</c> (<paramref name="pitchDeg" /> = X, <paramref name="yawDeg" />
    ///     = Y, roll ignored), degrees. Matches the engine's <c>AngleVectors</c>:
    ///     <c>(cos p·cos y, cos p·sin y, −sin p)</c> — pitch is nose-down-positive, so looking down gives −Z.
    /// </summary>
    public static Vector3 Forward(float pitchDeg, float yawDeg)
    {
        float p = pitchDeg * DegToRad;
        float y = yawDeg * DegToRad;
        float cp = MathF.Cos(p);
        return new Vector3(cp * MathF.Cos(y), cp * MathF.Sin(y), -MathF.Sin(p));
    }

    /// <summary>
    ///     Fills <paramref name="anchors" /> with up to <see cref="MaxAnchors" /> world points sampling the
    ///     target's body (centre spine + lateral shoulders). Shoulders are offset perpendicular to the
    ///     <b>horizontal</b> <paramref name="viewerEye" />→target sightline (the axis a corner-peek sliver
    ///     appears on), NOT the target's facing. Heights scale toward crouched by <paramref name="duckAmount" />.
    ///     Returns the count written.
    /// </summary>
    public static int BuildAnchors(Vector3 feet, float duckAmount, Vector3 viewerEye, Span<Vector3> anchors)
    {
        float f = Lerp(1f, EyeCrouched / EyeStanding, Math.Clamp(duckAmount, 0f, 1f)); // height compression

        // Lateral axis: perpendicular to the horizontal sightline, in the XY plane.
        float dx = feet.X - viewerEye.X, dy = feet.Y - viewerEye.Y;
        float hlen = MathF.Sqrt(dx * dx + dy * dy);
        Vector3 lateral = hlen > 1e-3f
            ? new Vector3(-dy / hlen, dx / hlen, 0f) // rotate horizontal sightline 90° in XY
            : new Vector3(0f, 1f, 0f); // degenerate (viewer directly above/below) — arbitrary

        int n = 0;
        anchors[n++] = new Vector3(feet.X, feet.Y, feet.Z + ChestZ * f);
        anchors[n++] = new Vector3(feet.X, feet.Y, feet.Z + HeadZ * f);
        anchors[n++] = new Vector3(feet.X, feet.Y, feet.Z + PelvisZ * f);
        anchors[n++] = new Vector3(feet.X, feet.Y, feet.Z + KneeZ * f);
        Vector3 shoulder = new(feet.X, feet.Y, feet.Z + ShoulderZ * f);
        anchors[n++] = shoulder - lateral * ShoulderHalfWidth;
        anchors[n++] = shoulder + lateral * ShoulderHalfWidth;
        return n;
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;
}

/// <summary>
///     A rectangular view frustum (yaw × pitch half-angles) approximating what is on a player's screen — used
///     for the "could-see" stat (LOS + on-screen). Rectangular, not a cone: a cone that fits the ~106°
///     horizontal 16:9 FOV would set vertical FOV to 106° too (real ≈ 73°), over-counting vertical sightlines
///     — wrong for exactly the multi-level maps (nuke/vertigo) the 3D-native principle exists to serve.
///     Defaults: 106° horizontal / 74° vertical (16:9 Hor+ of the 90° base) ⇒ ±53° / ±37° half-angles.
/// </summary>
public readonly struct ViewFrustum
{
    private readonly Vector3 _eye;
    private readonly Vector3 _fwd;
    private readonly Vector3 _right;
    private readonly Vector3 _up;
    private readonly float _tanYaw;
    private readonly float _tanPitch;

    public ViewFrustum(Vector3 eye, Vector3 forward, float yawHalfDeg = 53f, float pitchHalfDeg = 37f)
    {
        _eye = eye;
        _fwd = Normalize(forward, new Vector3(1, 0, 0));

        // Camera basis. right = fwd × worldUp; degenerate only when looking near-vertical.
        Vector3 right = Vector3.Cross(_fwd, new Vector3(0, 0, 1));
        _right = right.LengthSquared() > 1e-6f
            ? Vector3.Normalize(right)
            : Vector3.Normalize(Vector3.Cross(_fwd, new Vector3(0, 1, 0)));
        _up = Vector3.Cross(_right, _fwd);

        _tanYaw = MathF.Tan(yawHalfDeg * (MathF.PI / 180f));
        _tanPitch = MathF.Tan(pitchHalfDeg * (MathF.PI / 180f));
    }

    /// <summary>True iff <paramref name="point" /> is in front of the eye and within the yaw &amp; pitch half-angles.</summary>
    public bool Contains(Vector3 point)
    {
        Vector3 d = point - _eye;
        float z = Vector3.Dot(d, _fwd);
        if (z <= 0f)
        {
            return false; // behind the camera
        }

        float x = Vector3.Dot(d, _right);
        float y = Vector3.Dot(d, _up);
        return MathF.Abs(x) <= _tanYaw * z && MathF.Abs(y) <= _tanPitch * z;
    }

    private static Vector3 Normalize(Vector3 v, Vector3 fallback)
    {
        float len = v.Length();
        return len > 1e-6f ? v / len : fallback;
    }
}
