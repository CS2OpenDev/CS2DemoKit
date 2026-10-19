#region

using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Parser.EntityTracking;
using CS2OpenDev.Sdk.Entities;

#endregion

namespace CS2DemoKit.Analysis.Plugins;

/// <summary>
///     <c>entity.pawn.is_alive</c>: <see cref="PawnLookup.IsAlive" /> for every controller-bound pawn,
///     the same flag <see cref="PositionSample.IsAlive" /> carries. False is a value, never "no
///     value", so a dying pawn writes false into the pre-frame snapshot instead of leaving the
///     last true in place, which is what a read of <c>entity.pawn.health</c> (emitted only above
///     zero) cannot do.
///     <para>
///         Entity state, not the event-derived <c>player.alive</c> context: a player who
///         disconnects reads dead here with no <c>player_death</c>.
///     </para>
/// </summary>
public sealed class PawnAliveProvider
    : IPerPlayerEntityValueProvider, IPawnStateReader, IPawnIntCellReader, IDerivedPawnValue
{
    /// <inheritdoc />
    public string EntityClass => "CCSPlayerPawn";

    /// <inheritdoc />
    // m_lifeState is uint8 on the wire and the value is bool, hence IDerivedPawnValue.
    public string FieldName => "m_lifeState";

    /// <inheritdoc />
    public string Name => "entity.pawn.is_alive";

    /// <inheritdoc />
    public Type ValueType => typeof(bool);

    /// <inheritdoc />
    public void CaptureAllSlots(EntityStateLayer layer, Action<int, object> emit)
    {
        ArgumentNullException.ThrowIfNull(layer);
        ArgumentNullException.ThrowIfNull(emit);
        PawnLookup.ForEachLivePawn(layer.Tracker, (slot, pawn) => emit(slot, PawnLookup.IsAlive(pawn)));
    }

    /// <inheritdoc />
    public object? ReadForPawnState(EntityTracker tracker, EntityState pawn) => PawnLookup.IsAlive(pawn);

    /// <inheritdoc />
    public bool TryReadInt(PawnReadContext context, out int value)
    {
        ArgumentNullException.ThrowIfNull(context);
        value = PawnLookup.IsAlive(context.Pawn) ? 1 : 0;
        return true;
    }

    /// <inheritdoc />
    /// <exception cref="NotSupportedException">Always. Read through <see cref="IPawnStateReader" />.</exception>
    public object? ReadForPawn(EntityTracker tracker, CSPlayerPawn pawn) =>
        throw new NotSupportedException(
            $"provider '{Name}' reads the raw entity state; call ReadForPawnState "
            + "(IPawnStateReader) instead of ReadForPawn.");

    /// <inheritdoc />
    public object? Read(EntityStateLayer layer, int playerSlot)
    {
        ArgumentNullException.ThrowIfNull(layer);
        EntityState? pawn = PawnLookup.ResolvePawn(layer.Tracker, playerSlot);
        return pawn is null ? null : PawnLookup.IsAlive(pawn);
    }
}
