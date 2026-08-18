#region

using CS2DemoKit.Analysis.Abstractions;
using CS2OpenDev.Sdk.Entities;
using CS2DemoKit.Parser.EntityTracking;
using SchemaNames = CS2OpenSchema.SchemaNames;

#endregion

namespace CS2DemoKit.Analysis.Plugins;

/// <summary>
///     Reads <c>m_iHealth</c> off the <c>CCSPlayerPawn</c> for a given player slot. Used by
///     <c>HurtTeamEnrichmentEdge</c> to compute pre-hit HP from entity ground-truth rather than
///     event-tracked cache state.
///     <para>
///         Slot ↔ pawn resolution and handle decoding are delegated to
///         <see cref="PawnLookup" /> — both are subtle (forward <c>m_hPawn</c> is unreliable;
///         entity handles arrive as widely varying numeric types) and shared with every other
///         per-player provider.
///     </para>
///     <para>
///         <b>Typed-wrapper read path:</b> reads HP via the SDK-emitted typed wrapper
///         <see cref="CSPlayerPawn.Health" />, not <c>pawn.Fields["m_iHealth"]</c>. The wrapper
///         routes to the int lane at the codegen-pinned slot constant — no string hash, no
///         boxing on the read path. <c>EntityStateLayer.BootstrapTracker</c> binds the
///         Schema Lens resolver and registers the entity-factory registry that backs
///         <see cref="EntityTracker.Get{T}" />.
///     </para>
/// </summary>
public sealed class PawnHealthProvider : IPerPlayerEntityValueProvider
{
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
        // Emit gate preserved exactly: the old CaptureAllSlots skipped null and hp <= 0
        // (ReadHealthFromPawn already mapped hp == 0 → null), so the live emit set is hp > 0.
        int hp = pawn.Health;
        return hp > 0 ? hp : null;
    }

    /// <inheritdoc />
    public string EntityClass => "CCSPlayerPawn";

    /// <inheritdoc />
    public string FieldName => SchemaNames.CBaseEntity.Health;

    /// <inheritdoc />
    public string Name => "entity.pawn.health";

    /// <inheritdoc />
    public object? Read(EntityStateLayer layer, int playerSlot)
    {
        EntityState? pawn = PawnLookup.ResolvePawn(layer.Tracker, playerSlot);
        if (pawn is null)
        {
            return null;
        }

        return ReadHealthFromPawn(layer.Tracker, pawn);
    }

    /// <inheritdoc />
    public Type ValueType => typeof(int);

    /// <summary>
    ///     Returns the pawn's <c>m_iHealth</c> via the SDK typed wrapper. Equivalent to
    ///     <c>pawn.Fields[SchemaNames.CBaseEntity.Health]</c> but goes through the
    ///     lane-indexed read path (no string hash); the SDK wrapper verification battery A/Bs
    ///     the two paths against each other on real demo data.
    /// </summary>
    private static int? ReadHealthFromPawn(EntityTracker tracker, EntityState pawn)
    {
        CSPlayerPawn wrapper = SdkEntityWorlds.Wrap<CSPlayerPawn>(tracker, pawn)!;
        // The lane storage initializes int slots to 0 and only flips the _seen bit
        // when the wire delivers a value. A pawn that has never received an HP
        // update reports 0 here, matching the prior "absent key" path's outcome
        // (TryGetValue returned false, TryUnboxInt returned null, caller treated
        // it as no value). We treat 0 as "no value" identically.
        int hp = wrapper.Health;
        return hp == 0 ? null : hp;
    }
}
