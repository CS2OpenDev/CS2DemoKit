#region

using System.Numerics;
using CS2DemoKit.Parser.EntityTracking;
using SchemaNames = CS2OpenSchema.SchemaNames;

#endregion

namespace CS2DemoKit.Analysis.Plugins;

/// <summary>
///     Which field family a demo networks aim punch through. CS2 changed the shape mid-life and
///     both spellings are in circulation, so every read has to ask the demo rather than assume.
/// </summary>
public enum AimPunchLayout
{
    /// <summary>Neither family resolved: this demo does not network aim punch at all.</summary>
    None,

    /// <summary>
    ///     Build 22894272 and later. A <c>CCSPlayer_AimPunchServices</c> sub-entity reached
    ///     through <c>m_pAimPunchServices</c>.
    /// </summary>
    Services,

    /// <summary>
    ///     Build 22880072 and earlier. Four flat fields directly on <c>CCSPlayerPawn</c>. Still
    ///     the shape of every matchmaking demo recorded before the change, which is most of the
    ///     corpus users actually have.
    /// </summary>
    Flat
}

/// <summary>
///     A pawn's aim-punch spring, in the one shape the rest of the codebase works in.
///     <para>
///         Recoil kick is not networked as a resolved angle. It is a damped spring SAMPLED AT A
///         BASE TICK, so the punch applied at an arbitrary tick is this state integrated forward.
///         Reading <see cref="BaseAngle" /> alone is correct only AT <see cref="BaseTick" /> and
///         is a plausible, wrong number away from it. The integration is a separate piece of work;
///         this record exists so that when it lands it has one input shape to consume rather than
///         two.
///     </para>
/// </summary>
/// <param name="BaseAngle">Spring position at <paramref name="BaseTick" />, degrees (pitch, yaw, roll).</param>
/// <param name="BaseAngleVel">Spring velocity at <paramref name="BaseTick" />, degrees per second.</param>
/// <param name="BaseTick">The tick the sample was taken at.</param>
/// <param name="BaseTickFraction">Sub-tick offset of the sample, 0 to 1.</param>
public readonly record struct AimPunchState(
    Vector3 BaseAngle,
    Vector3 BaseAngleVel,
    int BaseTick,
    float BaseTickFraction);

/// <summary>
///     Resolves aim punch across the two CS2 field families and hands back a single
///     <see cref="AimPunchState" />, so downstream logic never branches on schema version.
///     <para>
///         <b>Why this exists.</b> At build 22894272 Valve replaced the flat
///         <c>m_aimPunchAngle</c> quartet on <c>CCSPlayerPawn</c> with a
///         <c>CCSPlayer_AimPunchServices</c> component. Both shapes are live in the wild: demos
///         recorded before the change carry the flat fields and nothing else. A provider that
///         names only one family throws at prime time on half the corpus, which is a hard abort
///         during precompute rather than a degraded column, and it aborts on exactly the
///         matchmaking demos that carry the richest aim data.
///     </para>
///     <para>
///         The SDK's <c>SchemaNames</c> is generated from a current schema and knows only the
///         services spelling, so the flat paths are literals here. That is deliberate: they name
///         fields the current game no longer has, so there is nothing to generate them from, and
///         they must not silently disappear on the next SDK pin bump.
///     </para>
/// </summary>
public static class AimPunchSchema
{
    /// <summary>Services-family angle path, used as the probe for that layout.</summary>
    public static readonly string ServicesAngle =
        SchemaNames.CCSPlayerPawn.AimPunchServices + "."
        + SchemaNames.CCSPlayerAimPunchServices.PredictableBaseAngle;

    private static readonly string _servicesAngleVel =
        SchemaNames.CCSPlayerPawn.AimPunchServices + "."
        + SchemaNames.CCSPlayerAimPunchServices.PredictableBaseAngleVel;

    private static readonly string _servicesTick =
        SchemaNames.CCSPlayerPawn.AimPunchServices + "."
        + SchemaNames.CCSPlayerAimPunchServices.PredictableBaseTick;

    private static readonly string _servicesTickFrac =
        SchemaNames.CCSPlayerPawn.AimPunchServices + "."
        + SchemaNames.CCSPlayerAimPunchServices.PredictableBaseTickInterpAmount;

    /// <summary>Flat-family angle path, used as the probe for that layout.</summary>
    public const string FlatAngle = "m_aimPunchAngle";

    private const string _flatAngleVel = "m_aimPunchAngleVel";
    private const string _flatTick = "m_aimPunchTickBase";
    private const string _flatTickFrac = "m_aimPunchTickFraction";

    /// <summary>The class both families hang off.</summary>
    public const string PawnClass = "CCSPlayerPawn";

    /// <summary>
    ///     Every angle path this resolver knows, newest family first. A provider declares these
    ///     as its candidates so schema validation can accept the demo that resolves ANY of them
    ///     and still throw when none do.
    /// </summary>
    public static IReadOnlyList<string> CandidateAnglePaths { get; } = [ServicesAngle, FlatAngle];

    /// <summary>
    ///     Decides which family this demo uses by asking the tracker for descriptors. Cheap
    ///     enough to call per read (a dictionary lookup plus a path descent), but callers that
    ///     read per pawn per frame should latch the result once their tracker has descriptors.
    /// </summary>
    /// <param name="tracker">The tracker whose schema to probe.</param>
    /// <returns>The resolved layout, or <see cref="AimPunchLayout.None" /> when neither exists.</returns>
    public static AimPunchLayout Resolve(EntityTracker tracker)
    {
        ArgumentNullException.ThrowIfNull(tracker);

        if (tracker.GetFieldMeta(PawnClass, ServicesAngle) is not null)
        {
            return AimPunchLayout.Services;
        }

        if (tracker.GetFieldMeta(PawnClass, FlatAngle) is not null)
        {
            return AimPunchLayout.Flat;
        }

        return AimPunchLayout.None;
    }

    /// <summary>
    ///     Reads the full spring state for one pawn under an already-resolved layout.
    /// </summary>
    /// <param name="pawn">The pawn entity state.</param>
    /// <param name="layout">The layout from <see cref="Resolve" />.</param>
    /// <returns>
    ///     The state, or null when the pawn has never networked a sample. A pawn that has not
    ///     fired since spawning has no spring sample, which is not the same fact as zero punch,
    ///     so the slot is skipped rather than emitting a fabricated zero.
    /// </returns>
    public static AimPunchState? Read(EntityState pawn, AimPunchLayout layout)
    {
        ArgumentNullException.ThrowIfNull(pawn);

        (string angle, string vel, string tick, string frac) = layout switch
        {
            AimPunchLayout.Services => (ServicesAngle, _servicesAngleVel, _servicesTick, _servicesTickFrac),
            AimPunchLayout.Flat => (FlatAngle, _flatAngleVel, _flatTick, _flatTickFrac),
            _ => (string.Empty, string.Empty, string.Empty, string.Empty)
        };

        if (angle.Length == 0 || pawn.TryGet<Vector3>(angle) is not { } baseAngle)
        {
            return null;
        }

        return new AimPunchState(baseAngle, ReadVelocity(pawn, vel), ToInt32(pawn[tick]), ToSingle(pawn[frac]));
    }

    /// <summary>
    ///     Reads only the base angle, for callers that need nothing else. Present or absent exactly
    ///     as <see cref="Read" />'s result is, because the angle is the one field that decides that;
    ///     it just does not read the velocity, tick and fraction the caller would discard, which on
    ///     the digest path were three boxed reads per pawn-frame per punch column.
    /// </summary>
    /// <param name="pawn">The pawn entity state.</param>
    /// <param name="layout">The layout from <see cref="Resolve" />.</param>
    /// <param name="baseAngle">The spring position, degrees (pitch, yaw, roll), or default when absent.</param>
    /// <returns><c>true</c> when the pawn carries a spring sample.</returns>
    public static bool TryReadBaseAngle(EntityState pawn, AimPunchLayout layout, out Vector3 baseAngle)
    {
        ArgumentNullException.ThrowIfNull(pawn);

        string angle = layout switch
        {
            AimPunchLayout.Services => ServicesAngle,
            AimPunchLayout.Flat => FlatAngle,
            _ => string.Empty
        };

        if (angle.Length == 0 || pawn.TryGet<Vector3>(angle) is not { } found)
        {
            baseAngle = default;
            return false;
        }

        baseAngle = found;
        return true;
    }

    private static Vector3 ReadVelocity(EntityState pawn, string vel)
    {

        // Only the angle is required. The other three are what the integration will need; a demo
        // that networks the angle but not the velocity still yields a usable at-base-tick sample,
        // and defaulting them is honest because BaseTick 0 cannot be mistaken for a real sample.
        //
        // The tick and fraction are read through the indexer and converted rather than through
        // TryGet<T>, which casts strictly. The tick field's WIRE type reads int32 but the decoder
        // lands it on a wider lane, so it comes back boxed as UInt64 on real demos and a strict
        // TryGet<int> throws InvalidCastException deep inside the parallel digest producer. The
        // declared wire type is not a promise about the CLR type of the boxed value.
        return pawn.TryGet<Vector3>(vel) ?? Vector3.Zero;
    }

    // Width-tolerant numeric reads. A missing field is 0, which the caller documents as
    // distinguishable from a real sample; a present field of any integer width converts. Anything
    // genuinely non-numeric falls through to 0 rather than throwing, because by this point the
    // angle has already resolved, so the pawn HAS aim punch and losing the base tick should
    // degrade the future integration rather than abort the whole analysis.
    private static int ToInt32(object? raw) => raw switch
    {
        null => 0,
        int v => v,
        uint v => (int)v,
        long v => (int)v,
        ulong v => (int)v,
        short v => v,
        ushort v => v,
        _ => 0
    };

    private static float ToSingle(object? raw) => raw switch
    {
        null => 0f,
        float v => v,
        double v => (float)v,
        _ => 0f
    };
}
