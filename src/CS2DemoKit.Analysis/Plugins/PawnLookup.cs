#region

using CS2DemoKit.Parser.EntityTracking;

#endregion

namespace CS2DemoKit.Analysis.Plugins;

/// <summary>
///     Shared slot ↔ pawn ↔ entity-handle utilities used by every
///     <see cref="IPerPlayerEntityValueProvider" /> implementation. Reused, not duplicated,
///     because the slot→pawn reverse-lookup and handle-decoding are subtle (forward
///     <c>controller.m_hPawn</c> is unreliable; entity handles arrive as ulong/int/short on
///     the wire and must be coerced).
/// </summary>
public static class PawnLookup
{
    /// <summary>
    ///     CS2 entity-handle encoding: lower 14 bits = entity index. Used for every
    ///     handle-typed networked field including <c>m_hController</c>, <c>m_hActiveWeapon</c>,
    ///     <c>m_hPawn</c>, etc.
    ///     <para>
    ///         Masking with this alone is not enough to get an index: see <see cref="IndexOf" />,
    ///         which also rejects the invalid handle that masks to <c>0x3FFF</c>.
    ///     </para>
    /// </summary>
    public const uint EntityIndexMask = 0x3FFF;

    /// <summary>
    ///     Entity index of a networked handle, or <c>-1</c> when the handle points at nothing.
    ///     Prefer this to masking directly.
    /// </summary>
    /// <remarks>
    ///     A networked ehandle is index bits plus serial bits, and invalid is all-ones at that
    ///     serialized width, not <c>0xFFFFFFFF</c>: Valve's <c>INVALID_NETWORKED_EHANDLE_VALUE</c>,
    ///     which is <c>0xFFFFFF</c> for CS2's 14 index + 10 serial. Every such width folds to index
    ///     <c>0x3FFF</c>, which is reserved and never a live entity, so folding the index catches
    ///     all of them at once where testing raw sentinel values does not.
    /// </remarks>
    public static int IndexOf(uint handle)
    {
        int index = (int)(handle & EntityIndexMask);
        return handle == 0 || index == (int)EntityIndexMask ? -1 : index;
    }

    /// <summary>
    ///     Per-pawn enumeration helper used by <c>CaptureAllSlots</c> implementations.
    ///     Invokes <paramref name="onPawn" /> once for each live player pawn paired with
    ///     its resolved player slot. Skips pawns with no controller handle (just-spawned,
    ///     not yet bound to a controller).
    /// </summary>
    public static void ForEachLivePawn(EntityTracker tracker, Action<int, EntityState> onPawn)
    {
        foreach ((int _, EntityState ent) in tracker.CurrentEntities.AllIndexed())
        {
            if (!ent.ClassName.Contains("PlayerPawn", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Allocation-free direct lane read. EntityState.Fields rebuilds an entire per-entity
            // dictionary projection on every access, so reading it once per entity per frame inside
            // this walk was the dominant entity-tracking allocation (profiled: ~40 GiB /
            // 227 KB per ForEachLivePawn call). The indexer reads the single m_hController slot
            // directly; m_hController is object-lane, so an unseen slot returns null — byte-identical
            // to the .Fields projection, which excludes unseen slots.
            object? hv = ent["m_hController"];
            if (hv is null)
            {
                continue;
            }

            int ctrlIdx = IndexOf(TryUnboxHandle(hv));
            int slot = ctrlIdx - 1;
            if (slot < 0)
            {
                continue;
            }

            // Identity check, not a bounds check: IndexOf already folds the invalid handle a dead
            // pawn reports. A live index can still name a recycled non-controller, and the slot
            // mapping must not trust it.
            EntityState? ctrl = tracker.CurrentEntities[ctrlIdx];
            if (ctrl is null || !ctrl.ClassName.Contains("PlayerController", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            onPawn(slot, ent);
        }
    }

    /// <summary>
    ///     Resolves an entity-handle value to the live entity it points to. Returns <c>null</c>
    ///     when the handle points at nothing (see <see cref="IndexOf" />) or the slot is empty.
    /// </summary>
    public static EntityState? ResolveHandle(EntityTracker tracker, object? handleValue)
    {
        int index = IndexOf(TryUnboxHandle(handleValue));
        return index < 0 ? null : tracker.CurrentEntities[index];
    }

    /// <summary>
    ///     Resolves a player slot to their live pawn entity. Iterates pawns and decodes
    ///     their <c>m_hController</c> handle — the reverse path is ground-truth because the
    ///     forward path (controller.m_hPawn) yields stale indices across pawn lifecycle
    ///     events. Returns <c>null</c> when no pawn matches the slot at the current tick.
    /// </summary>
    public static EntityState? ResolvePawn(EntityTracker tracker, int playerSlot)
    {
        if (playerSlot < 0)
        {
            return null;
        }

        int targetControllerIdx = playerSlot + 1;

        foreach ((int _, EntityState ent) in tracker.CurrentEntities.AllIndexed())
        {
            if (!ent.ClassName.Contains("PlayerPawn", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Allocation-free direct lane read. EntityState.Fields rebuilds an entire per-entity
            // dictionary projection on every access, so reading it once per entity per frame inside
            // this walk was the dominant entity-tracking allocation (profiled: ~40 GiB /
            // 227 KB per ForEachLivePawn call). The indexer reads the single m_hController slot
            // directly; m_hController is object-lane, so an unseen slot returns null — byte-identical
            // to the .Fields projection, which excludes unseen slots.
            object? hv = ent["m_hController"];
            if (hv is null)
            {
                continue;
            }

            if (IndexOf(TryUnboxHandle(hv)) == targetControllerIdx)
            {
                return ent;
            }
        }

        return null;
    }

    /// <summary>
    ///     Unboxes a networked entity-handle into a 32-bit uint regardless of the runtime
    ///     numeric type the field decoder produced. Empirically observed types so far:
    ///     <c>System.UInt64</c> for <c>m_hController</c>, <c>System.UInt32</c> for
    ///     <c>m_hActiveWeapon</c>. Covers every integral .NET numeric to be safe.
    ///     Returns <c>0</c> for non-numeric or null values — callers should treat zero
    ///     as "no live handle" (the wire sentinel).
    /// </summary>
    public static uint TryUnboxHandle(object? value) => value switch
    {
        null => 0u,
        uint u => u,
        int i => unchecked((uint)i),
        long l => unchecked((uint)l),
        ulong u => unchecked((uint)u),
        short s => unchecked((uint)s),
        ushort u => u,
        byte b => b,
        sbyte s => unchecked((uint)s),
        _ => 0u
    };
}
