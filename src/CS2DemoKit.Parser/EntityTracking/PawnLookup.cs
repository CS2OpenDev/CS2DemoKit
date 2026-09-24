namespace CS2DemoKit.Parser.EntityTracking;

/// <summary>
///     Shared slot ↔ pawn ↔ entity-handle utilities: the "whose pawn is this" half of positional
///     work, paired with <see cref="PositionUtil" />'s "where is it". Reused, not duplicated,
///     because the slot→pawn reverse-lookup and handle-decoding are subtle (forward
///     <c>controller.m_hPawn</c> is unreliable; entity handles arrive as ulong/int/short on
///     the wire and must be coerced).
/// </summary>
public static class PawnLookup
{
    /// <summary>
    ///     CS2 entity-handle encoding: lower 14 bits = entity index.
    ///     <para>
    ///         Masking with this alone does not give you an index. Use <see cref="IndexOf" />, or
    ///         <see cref="EntityHandle" /> directly, both of which also reject the invalid handle
    ///         that masks to <c>0x3FFF</c>.
    ///     </para>
    /// </summary>
    public const uint EntityIndexMask = EntityHandle.IndexMask;

    /// <summary>
    ///     Entity index of a networked handle, or <c>-1</c> when the handle points at nothing.
    ///     Prefer this to masking directly.
    /// </summary>
    /// <remarks>
    ///     <see cref="EntityHandle" /> owns the wire rule. This adds the zero convention and the
    ///     <c>-1</c> sentinel, which are this layer's, not the wire's.
    /// </remarks>
    public static int IndexOf(uint handle)
    {
        EntityHandle h = new(handle);
        return handle == 0 || !h.IsValid ? -1 : h.Index;
    }

    /// <summary>
    ///     Invokes <paramref name="onPawn" /> once for each player pawn bound to a controller, dead
    ///     or alive, paired with its resolved player slot. A dead player's pawn keeps its controller
    ///     handle and keeps coming through for the rest of the round, so a caller that wants only
    ///     the living filters with <see cref="IsAlive" />. Skips pawns with no controller handle
    ///     (just-spawned, not yet bound to a controller). One sweep of the entity set, so a caller
    ///     wanting several values per pawn should read them all inside the callback rather than
    ///     sweep per value.
    /// </summary>
    public static void ForEachLivePawn(EntityTracker tracker, Action<int, EntityState> onPawn) =>
        ForEachLivePawn(tracker, onPawn, static (callback, slot, pawn) => callback(slot, pawn));

    /// <summary>
    ///     The <see cref="ForEachLivePawn(EntityTracker, Action{int, EntityState})" /> sweep with a
    ///     caller-supplied state argument, so a per-frame caller can pass a static callback and
    ///     allocate no closure per sweep. Yields the same pawns, dead ones included.
    /// </summary>
    /// <typeparam name="TState">The state handed back to every callback.</typeparam>
    /// <param name="tracker">The tracker whose entity set to sweep.</param>
    /// <param name="state">Passed through unchanged to every <paramref name="onPawn" /> call.</param>
    /// <param name="onPawn">Invoked once per controller-bound pawn, dead or alive, with the state, the player slot and the pawn.</param>
    public static void ForEachLivePawn<TState>(EntityTracker tracker, TState state, Action<TState, int, EntityState> onPawn)
    {
        foreach ((int _, EntityState ent) in tracker.CurrentEntities.AllIndexed())
        {
            if (!ent.ClassName.Contains("PlayerPawn", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Unseen and present-null both mean no controller, as the Fields projection reads them.
            if (!TryReadHandle(ent, "m_hController", out uint hv))
            {
                continue;
            }

            int ctrlIdx = IndexOf(hv);
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

            onPawn(state, slot, ent);
        }
    }

    /// <summary>
    ///     Whether <paramref name="pawn" /> is a living player: <c>m_lifeState</c> is
    ///     <c>LIFE_ALIVE</c> (0) and <c>m_iHealth</c> is above zero. The wire also carries
    ///     <c>LIFE_DYING</c> (1) and <c>LIFE_DEAD</c> (2), both of which read false, and a pawn
    ///     with either field unseen reads false. This is the filter for the dead pawns
    ///     <see cref="ForEachLivePawn(EntityTracker, Action{int, EntityState})" /> yields.
    ///     <para>
    ///         Measured on the sample and five matchmaking demos, joined by frame index: no pawn
    ///         reads alive on or after the frame carrying its <c>player_death</c>. The reverse does
    ///         not hold everywhere: a player who disconnects leaves a pawn that reads dead with
    ///         health above zero and no <c>player_death</c>, so this can say dead where an
    ///         event-derived alive flag says alive.
    ///     </para>
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="pawn" /> is null.</exception>
    public static bool IsAlive(EntityState pawn)
    {
        ArgumentNullException.ThrowIfNull(pawn);
        return pawn.TryGet<int>("m_lifeState") is 0 && pawn.TryGet<int>("m_iHealth") is > 0;
    }

    /// <summary>
    ///     Resolves an entity-handle value to the live entity it points to. Returns <c>null</c>
    ///     when the handle points at nothing (see <see cref="IndexOf" />) or the slot is empty.
    /// </summary>
    public static EntityState? ResolveHandle(EntityTracker tracker, object? handleValue) =>
        ResolveHandle(tracker, TryUnboxHandle(handleValue));

    /// <summary>The same resolution for a handle already read as its 32-bit wire value.</summary>
    public static EntityState? ResolveHandle(EntityTracker tracker, uint handle)
    {
        int index = IndexOf(handle);
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

            if (!TryReadHandle(ent, "m_hController", out uint hv))
            {
                continue;
            }

            if (IndexOf(hv) == targetControllerIdx)
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
    /// <summary>
    ///     Reads a handle field as its 32-bit wire value without boxing: straight off the long lane
    ///     when the field lives there, else through the boxed read and <see cref="TryUnboxHandle" />.
    ///     False when the field is unseen or present with no value.
    /// </summary>
    public static bool TryReadHandle(EntityState entity, string path, out uint handle)
    {
        if (entity.Shape is { } shape && shape.PathToSlot.TryGetValue(path, out SlotAddr addr) && addr.Lane == LaneKind.Long)
        {
            if (entity.TryGetLongSlot(addr.Slot, out ulong wide))
            {
                handle = unchecked((uint)wide);
                return true;
            }

            handle = 0;
            return false;
        }

        if (entity.TryGetValue(path, out object? boxed) && boxed is not null)
        {
            handle = TryUnboxHandle(boxed);
            return true;
        }

        handle = 0;
        return false;
    }

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
