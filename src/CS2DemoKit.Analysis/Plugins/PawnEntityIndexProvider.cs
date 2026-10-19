#region

using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Parser.EntityTracking;
using CS2OpenDev.Sdk.Entities;

#endregion

namespace CS2DemoKit.Analysis.Plugins;

/// <summary>
///     <c>entity.pawn.entity_index</c>: the entity index of the slot's controller-bound pawn, the
///     order <see cref="PawnLookup.ForEachLivePawn(EntityTracker, Action{int, EntityState})" /> and
///     so <see cref="PositionSampler.Walk" /> visit pawns in. A frame observer sorts the slots it
///     reads by this to replay the walk's per-frame order. Changes only when a slot gets a new pawn.
/// </summary>
public sealed class PawnEntityIndexProvider
    : IPerPlayerEntityValueProvider, IPawnStateReader, IPawnIntCellReader, IDerivedPawnValue
{
    /// <inheritdoc />
    public string EntityClass => "CCSPlayerPawn";

    /// <inheritdoc />
    // The binding leaf: only a pawn with a controller handle has a slot to carry an index.
    public string FieldName => "m_hController";

    /// <inheritdoc />
    public string Name => "entity.pawn.entity_index";

    /// <inheritdoc />
    public Type ValueType => typeof(int);

    /// <inheritdoc />
    public void CaptureAllSlots(EntityStateLayer layer, Action<int, object> emit)
    {
        ArgumentNullException.ThrowIfNull(layer);
        ArgumentNullException.ThrowIfNull(emit);
        EntityTracker tracker = layer.Tracker;
        PawnLookup.ForEachLivePawn(tracker, (slot, pawn) => emit(slot, Find(tracker, pawn)));
    }

    /// <inheritdoc />
    public object? ReadForPawnState(EntityTracker tracker, EntityState pawn) =>
        Find(tracker, pawn) is var index and >= 0 ? index : null;

    /// <inheritdoc />
    public bool TryReadInt(PawnReadContext context, out int value)
    {
        ArgumentNullException.ThrowIfNull(context);
        value = context.EntityIndex;
        if (value >= 0)
        {
            return true;
        }

        value = 0;
        return false;
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
        return pawn is null ? null : ReadForPawnState(layer.Tracker, pawn);
    }

    // A scan of the live set, for the callers whose sweep did not hand the index over. The digest's
    // pawn sweep does, so the per-frame path never takes this.
    internal static int Find(EntityTracker tracker, EntityState pawn)
    {
        ArgumentNullException.ThrowIfNull(tracker);
        ArgumentNullException.ThrowIfNull(pawn);
        foreach ((int index, EntityState entity) in tracker.CurrentEntities.AllIndexed())
        {
            if (ReferenceEquals(entity, pawn))
            {
                return index;
            }
        }

        return -1;
    }
}
