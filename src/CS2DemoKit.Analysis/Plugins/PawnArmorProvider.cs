#region

using CS2DemoKit.Analysis.Abstractions;
using CS2OpenDev.Sdk.Entities;
using CS2DemoKit.Parser.EntityTracking;
using SchemaNames = CS2OpenSchema.SchemaNames;

#endregion

namespace CS2DemoKit.Analysis.Plugins;

/// <summary>
///     Reads <c>m_ArmorValue</c> off the <c>CCSPlayerPawn</c> for a given slot, exposed to YAML as
///     <c>player.entity.pawn.armor</c>. Sampled at <c>round_freeze_end</c> for buy-quality stats
///     (e.g. rounds the player bought armor). <c>0</c> (no armor) is a real observation and is
///     emitted, not treated as absent.
/// </summary>
public sealed class PawnArmorProvider : IPerPlayerEntityValueProvider
{
    /// <inheritdoc />
    public void CaptureAllSlots(EntityStateLayer layer, Action<int, object> emit)
    {
        EntityTracker tracker = layer.Tracker;
        PawnLookup.ForEachLivePawn(tracker, (slot, pawn) =>
            emit(slot, ReadForPawn(tracker, SdkEntityWorlds.Wrap<CSPlayerPawn>(tracker, pawn)!)!));
    }

    /// <inheritdoc />
    // Armor of 0 is a real observation (the round still counts), so this never returns null —
    // the emit gate is "always emit", unchanged from the old CaptureAllSlots body.
    public object? ReadForPawn(EntityTracker tracker, CSPlayerPawn pawn) => pawn.ArmorValue;

    /// <inheritdoc />
    public string EntityClass => "CCSPlayerPawn";

    /// <inheritdoc />
    public string FieldName => SchemaNames.CCSPlayerPawn.ArmorValue;

    /// <inheritdoc />
    public string Name => "entity.pawn.armor";

    /// <inheritdoc />
    public object? Read(EntityStateLayer layer, int playerSlot)
    {
        EntityState? pawn = PawnLookup.ResolvePawn(layer.Tracker, playerSlot);
        return pawn is null ? null : SdkEntityWorlds.Wrap<CSPlayerPawn>(layer.Tracker, pawn)!.ArmorValue;
    }

    /// <inheritdoc />
    public Type ValueType => typeof(int);
}
