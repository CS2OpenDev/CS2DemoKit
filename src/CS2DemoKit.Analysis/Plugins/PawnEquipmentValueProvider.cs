#region

using CS2DemoKit.Analysis.Abstractions;
using CS2OpenDev.Sdk.Entities;
using CS2DemoKit.Parser.EntityTracking;
using SchemaNames = CS2OpenSchema.SchemaNames;

#endregion

namespace CS2DemoKit.Analysis.Plugins;

/// <summary>
///     Reads <c>m_unCurrentEquipmentValue</c> (the player's live equipment value — primary/secondary
///     + armor + utility) off the <c>CCSPlayerPawn</c> for a given slot. Exposed to YAML as
///     <c>player.entity.pawn.equipment_value</c> for economy/buy-profile stats sampled at
///     <c>round_freeze_end</c>.
///     <para>
///         Unlike <see cref="PawnHealthProvider" />, a value of <c>0</c> is a legitimate
///         observation (an eco / save round), so it is emitted rather than treated as "absent" —
///         the round still counts toward an average. Slot↔pawn resolution and the codegen typed
///         wrapper come from the shared <see cref="PawnLookup" /> path.
///     </para>
/// </summary>
public sealed class PawnEquipmentValueProvider : IPerPlayerEntityValueProvider
{
    /// <inheritdoc />
    public void CaptureAllSlots(EntityStateLayer layer, Action<int, object> emit)
    {
        EntityTracker tracker = layer.Tracker;
        PawnLookup.ForEachLivePawn(tracker, (slot, pawn) =>
            emit(slot, ReadForPawn(tracker, SdkEntityWorlds.Wrap<CSPlayerPawn>(tracker, pawn)!)!));
    }

    /// <inheritdoc />
    // An equipment value of 0 (eco / save round) is a legitimate observation, so this never
    // returns null — the emit gate is "always emit", unchanged from the old CaptureAllSlots body.
    public object? ReadForPawn(EntityTracker tracker, CSPlayerPawn pawn) => pawn.CurrentEquipmentValue;

    /// <inheritdoc />
    public string EntityClass => "CCSPlayerPawn";

    /// <inheritdoc />
    public string FieldName => SchemaNames.CCSPlayerPawn.CurrentEquipmentValue;

    /// <inheritdoc />
    public string Name => "entity.pawn.equipment_value";

    /// <inheritdoc />
    public object? Read(EntityStateLayer layer, int playerSlot)
    {
        EntityState? pawn = PawnLookup.ResolvePawn(layer.Tracker, playerSlot);
        return pawn is null ? null : SdkEntityWorlds.Wrap<CSPlayerPawn>(layer.Tracker, pawn)!.CurrentEquipmentValue;
    }

    /// <inheritdoc />
    public Type ValueType => typeof(int);
}
