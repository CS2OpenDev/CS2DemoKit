#region

using CS2DemoKit.Analysis.Abstractions;
using CS2OpenDev.Sdk.Entities;
using CS2DemoKit.Parser.EntityTracking;
using SchemaNames = CS2OpenSchema.SchemaNames;

#endregion

namespace CS2DemoKit.Analysis.Plugins;

/// <summary>
///     Reads the <c>ClassName</c> of the weapon entity currently equipped by a player.
///     Two-hop resolution: slot → pawn (via <see cref="PawnLookup.ResolvePawn" />), then
///     <c>pawn.ActiveWeaponHandle</c> → weapon entity. Returns a string like <c>"CWeaponAK47"</c>
///     or <c>null</c> when the pawn has no active weapon (dead, mid-respawn, or not yet
///     bound to a controller).
///     <para>
///         Returning <c>ClassName</c> rather than <c>m_iItemDefinitionIndex</c> avoids the
///         extra field read on the weapon entity, is robust across schema-version changes
///         (the entity class is the entity's identity, not a networked field), and matches
///         the pattern already used in the UI snapshot path
///         (<c>MainViewModel</c>'s demo-load path).
///     </para>
///     <para>
///         <b>Typed-wrapper read path:</b> reads the handle via the SDK-emitted typed wrapper
///         <see cref="CSPlayerPawn.ActiveWeaponHandle" /> (raw int — V1 HandleIndex transform
///         emits the unmodified wire int; cross-class resolution via
///         <see cref="PawnLookup.ResolveHandle" /> is kept because concrete weapon classes
///         like <c>CWeaponAK47</c> are not in the curated wrapper set, so the runtime's
///         factory dispatch would return null for them). The lane-indexed read replaces
///         the prior <c>pawn.Fields[dottedPath]</c> string-hash dict lookup; the second
///         hop and ClassName read are unchanged.
///     </para>
/// </summary>
public sealed class ActiveWeaponProvider : IPerPlayerEntityValueProvider
{
    // Kept for the IPerPlayerEntityValueProvider.FieldName surface contract; the read
    // path no longer hits Fields[path] directly.
    private static readonly string _dottedFieldName =
        SchemaNames.CBasePlayerPawn.WeaponServices + "." + SchemaNames.CPlayerWeaponServices.ActiveWeapon;

    /// <inheritdoc />
    public void CaptureAllSlots(EntityStateLayer layer, Action<int, object> emit)
    {
        EntityTracker tracker = layer.Tracker;
        PawnLookup.ForEachLivePawn(tracker, (slot, pawn) =>
        {
            object? value = ReadForPawn(tracker, SdkEntityWorlds.Wrap<CSPlayerPawn>(tracker, pawn)!);
            if (value is not null)
            {
                emit(slot, value);
            }
        });
    }

    /// <inheritdoc />
    public object? ReadForPawn(EntityTracker tracker, CSPlayerPawn pawn)
    {
        // Emit gate preserved: null (no weapon / unresolved handle) skips the slot exactly as
        // the old CaptureAllSlots did. Uses the shared wrapper (no second wrapper alloc).
        uint handle = pawn.ActiveWeaponHandle;
        if (handle == 0)
        {
            return null;
        }

        EntityState? weapon = PawnLookup.ResolveHandle(tracker, handle);
        return weapon?.ClassName;
    }

    /// <inheritdoc />
    public string EntityClass => "CCSPlayerPawn";

    /// <inheritdoc />
    public string FieldName => _dottedFieldName;

    /// <inheritdoc />
    public string Name => "entity.pawn.active_weapon_class";

    /// <inheritdoc />
    public object? Read(EntityStateLayer layer, int playerSlot)
    {
        EntityState? pawn = PawnLookup.ResolvePawn(layer.Tracker, playerSlot);
        if (pawn is null)
        {
            return null;
        }

        return ReadActiveWeaponClass(layer.Tracker, pawn);
    }

    /// <inheritdoc />
    public Type ValueType => typeof(string);

    /// <summary>
    ///     Reads the active-weapon handle off the pawn via the typed wrapper (lane-indexed,
    ///     no string hash), then resolves through <see cref="PawnLookup.ResolveHandle" /> to
    ///     the live weapon <see cref="EntityState" /> and returns its <c>ClassName</c>.
    ///     Returns <c>null</c> for the zero handle (no weapon equipped) or when the resolved
    ///     slot is empty.
    /// </summary>
    private static string? ReadActiveWeaponClass(EntityTracker tracker, EntityState pawn)
    {
        CSPlayerPawn wrapper = SdkEntityWorlds.Wrap<CSPlayerPawn>(tracker, pawn)!;
        uint handle = wrapper.ActiveWeaponHandle;
        if (handle == 0)
        {
            return null;
        }

        // PawnLookup.ResolveHandle takes a boxed object?; pass the int as-is. It coerces
        // to uint internally to match the historical wire-typed unbox behaviour (which
        // varied across UInt32 / UInt64 depending on the field).
        EntityState? weapon = PawnLookup.ResolveHandle(tracker, handle);
        return weapon?.ClassName;
    }
}
