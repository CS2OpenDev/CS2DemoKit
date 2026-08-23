#region

using System.Numerics;
using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Parser.EntityTracking;
using CS2OpenDev.Sdk.Entities;

#endregion

namespace CS2DemoKit.Analysis.Plugins;

/// <summary>One world-space axis of a pawn's reconstructed origin.</summary>
public enum PawnPositionAxis
{
    /// <summary>World X.</summary>
    X,

    /// <summary>World Y.</summary>
    Y,

    /// <summary>World Z (height).</summary>
    Z
}

/// <summary>
///     Reads one axis of a pawn's world position, exposed as <c>entity.pawn.pos_x</c> /
///     <c>pos_y</c> / <c>pos_z</c>. There is no <c>m_vecOrigin</c> leaf on a pawn: position is
///     cell indices plus an in-cell offset on <c>CBodyComponent</c>, reconstructed as
///     <c>(cell - 32) * 512 + offset</c>, which <see cref="PositionUtil.CellToWorldVector" />
///     owns as this repo's oracle-verified home for the constant.
///     <para>
///         Read through <see cref="IPawnStateReader" />, not the SDK wrapper.
///         <c>CSPlayerPawn.Origin</c> resolves only through the Lens lane and returns null on the
///         shipped decode path, where the state indexer still reads the pair;
///         <c>PawnPositionMatchesPositionUtil</c> pins the two against each other so that stops
///         being folklore, and fails if either side's formula drifts.
///     </para>
///     <para>
///         <b>Cost.</b> Unlike every other shipped per-player provider, this one changes on
///         almost every frame for almost every pawn, so it defeats the digest's delta encoding
///         for the column it occupies (see <c>EntityFrameDigest.PerPawn</c>). That is why it is
///         three separate axis providers rather than one vector: a ruleset that only needs
///         height reads <c>pos_z</c> and pays for one column instead of three. Providers are
///         gated in by name, so a ruleset that reads none of them pays nothing at all.
///     </para>
/// </summary>
public sealed class PawnPositionProvider(PawnPositionAxis axis)
    : IPerPlayerEntityValueProvider, IWorkerCloneable<IPerPlayerEntityValueProvider>, IPawnStateReader
{
    /// <summary>The axis this instance reads.</summary>
    public PawnPositionAxis Axis => axis;

    /// <inheritdoc />
    public string EntityClass => "CCSPlayerPawn";

    /// <inheritdoc />
    // The in-cell offset half of the pair. Declared because it is the leaf whose wire type
    // (CNetworkedQuantizedFloat) matches ValueType; the cell index is an int on its own lane and
    // would fail the scanner's declared-type check. Schema drift on either leaf surfaces as a
    // null read, and drift on this one throws at prime time like any other provider.
    public string FieldName => axis switch
    {
        PawnPositionAxis.X => "CBodyComponent.m_vecX",
        PawnPositionAxis.Y => "CBodyComponent.m_vecY",
        _ => "CBodyComponent.m_vecZ"
    };

    /// <inheritdoc />
    public string Name => axis switch
    {
        PawnPositionAxis.X => "entity.pawn.pos_x",
        PawnPositionAxis.Y => "entity.pawn.pos_y",
        _ => "entity.pawn.pos_z"
    };

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
    // Null (slot skipped) when the origin is unresolvable: a pre-spawn or dormant pawn has not
    // networked all six leaves. A resolved 0 is a real coordinate and emits.
    public object? ReadForPawnState(EntityTracker tracker, EntityState pawn) =>
        PositionUtil.CellToWorldVector(pawn) is { } origin ? Select(origin) : null;

    /// <inheritdoc />
    /// <exception cref="NotSupportedException">Always. Read through <see cref="IPawnStateReader" />.</exception>
    // CSPlayerPawn.Origin resolves only through the Lens lane and comes back null on trackers where
    // the state indexer still reads the CBodyComponent pair, so honouring this signature would mean
    // emitting nothing for every pawn on those trackers. Failing loudly beats a column of nulls that
    // reads as "player has no position" and quietly gates every positional rule off.
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
    public IPerPlayerEntityValueProvider CloneForWorker() => new PawnPositionProvider(axis);

    private float Select(Vector3 origin) => axis switch
    {
        PawnPositionAxis.X => origin.X,
        PawnPositionAxis.Y => origin.Y,
        _ => origin.Z
    };
}
