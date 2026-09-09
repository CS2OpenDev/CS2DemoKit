#region

using System.Numerics;
using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Parser.EntityTracking;
using CS2OpenDev.Sdk.Entities;
using SchemaNames = CS2OpenSchema.SchemaNames;

#endregion

namespace CS2DemoKit.Analysis.Plugins;

/// <summary>One component of a networked <c>QAngle</c>, in degrees.</summary>
public enum PawnAngleAxis
{
    /// <summary>Pitch: the QAngle's X component. Negative is up in CS2's convention.</summary>
    Pitch,

    /// <summary>Yaw: the QAngle's Y component, counter-clockwise from world +X.</summary>
    Yaw
}

/// <summary>
///     Reads one component of a pawn's networked eye angle, exposed as
///     <c>entity.pawn.eye_pitch</c> / <c>entity.pawn.eye_yaw</c>. <c>m_angEyeAngles</c> is a
///     <c>QAngle</c> on the wire (encoder <c>qangle_precise</c>, so finer than the default
///     1/64-degree quantisation and not the limiting error term on any angular metric built
///     from it), and the parser decodes it to a <see cref="Vector3" /> of
///     (pitch, yaw, roll) degrees.
///     <para>
///         <b>Why two providers and not one angle.</b> The rules type vocabulary has no vector
///         type and no member access, so a <see cref="Vector3" /> value could not be declared or
///         compared in YAML. Splitting into scalar components is what the language forces, and it
///         is the same shape <see cref="PawnPositionProvider" /> takes for position. Roll is not
///         exposed: it is always zero on a player pawn, so a third column would be a column of
///         zeros that defeats the digest's delta encoding for nothing.
///     </para>
///     <para>
///         Read through <see cref="IPawnStateReader" />, not the SDK wrapper, for the same reason
///         <see cref="PawnPositionProvider" /> does: the typed accessor resolves through the Lens
///         lane only, and a lane-unmapped tracker would read null through the wrapper while
///         <see cref="EntityState" />'s seen-gated indexer still returns the boxed vector.
///     </para>
///     <para>
///         <b>Cost.</b> Like position, this changes on almost every frame for almost every pawn,
///         so it defeats the digest's delta encoding for the column it occupies. Providers are
///         gated in by name, so a ruleset that reads neither component pays nothing.
///     </para>
/// </summary>
public sealed class PawnEyeAngleProvider(PawnAngleAxis axis)
    : IPerPlayerEntityValueProvider, IWorkerCloneable<IPerPlayerEntityValueProvider>, IPawnStateReader
{
    /// <summary>The angle component this instance reads.</summary>
    public PawnAngleAxis Axis => axis;

    /// <inheritdoc />
    public string EntityClass => "CCSPlayerPawn";

    /// <inheritdoc />
    // The composite QAngle itself, not a scalar leaf: unlike the position pair there is no
    // per-component leaf to name. That is why EntityChangeScanner.IsWireTypeCompatible admits
    // QAngle under a float-declared provider; without it this throws at prime time.
    public string FieldName => SchemaNames.CCSPlayerPawn.EyeAngles;

    /// <inheritdoc />
    public string Name => axis == PawnAngleAxis.Pitch ? "entity.pawn.eye_pitch" : "entity.pawn.eye_yaw";

    /// <inheritdoc />
    public Type ValueType => typeof(float);

    /// <inheritdoc />
    public void CaptureAllSlots(EntityStateLayer layer, Action<int, object> emit)
    {
        EntityTracker tracker = layer.Tracker;
        PawnLookup.ForEachLivePawn(tracker, (slot, pawn) =>
        {
            if (ReadForPawnState(tracker, pawn) is { } value)
            {
                emit(slot, value);
            }
        });
    }

    /// <inheritdoc />
    // Null (slot skipped) when the angle has never been networked for this pawn: a pre-spawn or
    // dormant pawn has no eye angle, which is not the same fact as "looking at 0 degrees".
    public object? ReadForPawnState(EntityTracker tracker, EntityState pawn) =>
        pawn.TryGet<Vector3>(FieldName) is { } angle ? Select(angle) : null;

    /// <inheritdoc />
    /// <exception cref="NotSupportedException">Always. Read through <see cref="IPawnStateReader" />.</exception>
    // The SDK wrapper's angle accessor resolves only through the Lens lane and comes back null on
    // trackers where the state indexer still holds the decoded QAngle, so honouring this signature
    // would mean emitting nothing for every pawn on those trackers. Failing loudly beats a column
    // of nulls that reads as "this player was never looking anywhere".
    public object? ReadForPawn(EntityTracker tracker, CSPlayerPawn pawn) =>
        throw new NotSupportedException(
            $"provider '{Name}' reads the raw entity state; call ReadForPawnState "
            + "(IPawnStateReader) instead of ReadForPawn.");

    /// <inheritdoc />
    public object? Read(EntityStateLayer layer, int playerSlot)
    {
        EntityState? pawn = PawnLookup.ResolvePawn(layer.Tracker, playerSlot);
        return pawn is null ? null : ReadForPawnState(layer.Tracker, pawn);
    }

    /// <inheritdoc />
    public IPerPlayerEntityValueProvider CloneForWorker() => new PawnEyeAngleProvider(axis);

    private float Select(Vector3 angle) => axis == PawnAngleAxis.Pitch ? angle.X : angle.Y;
}
