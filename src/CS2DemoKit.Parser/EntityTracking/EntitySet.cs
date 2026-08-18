namespace CS2DemoKit.Parser.EntityTracking;

/// <summary>
///     The live entity table: a fixed 16 384-slot array (matching Source's MAX_EDICTS) of
///     <see cref="EntityState" /> instances, indexed by entity handle index.
/// </summary>
public sealed class EntitySet
{
    // Source uses indices 0..16383 for networked entities.
    private const int MaxEntities = 1 << 14; // 16 384

    // Live-entity index: the slot indices currently occupied in _slots, kept SORTED
    // ASCENDING so iteration matches a 0..MaxEntities array scan exactly. Lets the enumeration APIs
    // visit only the ~250 live entities instead of scanning all 16,384 slots every call — the
    // per-frame entity sweeps (PawnLookup.ForEachLivePawn, DetectMolotovThrows) were the load-time
    // hot cost. Mirrors _slots EXACTLY: mutated only at the null↔occupied transitions in GetOrCreate
    // / Remove / Clear (genuine entity lifecycle, which is rare relative to field updates), so O(n)
    // insert/remove shifts are negligible while iteration stays contiguous and cache-friendly.
    private readonly List<int> _occupied = new(256);

    private readonly EntityState?[] _slots = new EntityState?[MaxEntities];

    /// <summary>Returns the entity at <paramref name="index" />, or <c>null</c> if the slot is empty.</summary>
    public EntityState? this[int index]
        => (uint)index < MaxEntities ? _slots[index] : null;

    /// <summary>Enumerate all currently live entities.</summary>
    public IEnumerable<EntityState> All()
    {
        // Walk the live index (sorted ascending) — same order/result as scanning all 16,384 slots,
        // but only the ~250 occupied ones. Null check is a desync belt: a stale index entry degrades
        // to a skip (old behaviour) rather than throwing; a missing entry still surfaces as a diff.
        foreach (int i in _occupied)
        {
            if (_slots[i] is { } e)
            {
                yield return e;
            }
        }
    }

    /// <summary>Enumerate only entities currently inside the PVS.</summary>
    public IEnumerable<EntityState> AllInPvs()
        => All().Where(e => e.IsInPvs);

    /// <summary>Enumerate in-PVS entities with their slot index.</summary>
    public IEnumerable<(int Index, EntityState Entity)> AllInPvsIndexed()
        => AllIndexed().Where(t => t.Entity.IsInPvs);

    /// <summary>Enumerate all currently live entities with their slot index.</summary>
    public IEnumerable<(int Index, EntityState Entity)> AllIndexed()
    {
        // Live index (sorted ascending) → identical (index, entity) order to a full 0..MaxEntities
        // scan. See All() for the null-check desync belt.
        foreach (int i in _occupied)
        {
            if (_slots[i] is { } e)
            {
                yield return (i, e);
            }
        }
    }

    /// <summary>Enumerate all currently live entities of a given class name.</summary>
    public IEnumerable<EntityState> OfClass(string className)
    {
        foreach (int i in _occupied)
        {
            if (_slots[i] is { } e && e.ClassName == className)
            {
                yield return e;
            }
        }
    }

    /// <summary>Snapshot all live entity fields: slotIndex → {fieldKey → value}.</summary>
    public Dictionary<int, Dictionary<string, object?>> Snapshot()
    {
        Dictionary<int, Dictionary<string, object?>> snap = new();
        for (int i = 0; i < MaxEntities; i++)
        {
            if (_slots[i] is not { } e)
            {
                continue;
            }

            Dictionary<string, object?> copy = new(e.Fields.Count);
            foreach (KeyValuePair<string, object?> kv in e.Fields)
            {
                copy[kv.Key] = kv.Value;
            }

            snap[i] = copy;
        }

        return snap;
    }

    internal void Clear()
    {
        for (int i = 0; i < MaxEntities; i++)
        {
            _slots[i] = null;
        }

        _occupied.Clear();
    }

    // ── Mutation (internal) ───────────────────────────────────────────────────

    internal EntityState GetOrCreate(int index, string className, int serial)
    {
        if ((uint)index >= MaxEntities)
        {
            throw new ArgumentOutOfRangeException(nameof(index), index, $"Entity index {index} out of range [0, {MaxEntities}).");
        }

        // Reuse the existing slot only when class identity matches. ClassName is immutable on
        // EntityState, so a slot reused with a different class would otherwise return a state
        // pointing at the wrong serializer — ReadEntityFields would consume the wrong number
        // of bits from the wire and silently misalign the rest of the packet.
        if (_slots[index] is { } existing && existing.ClassName == className)
        {
            existing.Serial = serial;
            existing.Clear();
            return existing;
        }

        // Index maintenance: only a null→occupied transition adds to the live index. A different-class
        // replacement leaves the slot occupied (it was already in the index), so no insert is needed.
        bool wasEmpty = _slots[index] is null;
        EntityState state = new(className, serial);
        _slots[index] = state;
        if (wasEmpty)
        {
            int p = _occupied.BinarySearch(index);
            if (p < 0)
            {
                _occupied.Insert(~p, index);
            }
        }

        return state;
    }

    internal void Remove(int index)
    {
        if ((uint)index < MaxEntities)
        {
            if (_slots[index] is not null)
            {
                int p = _occupied.BinarySearch(index);
                if (p >= 0)
                {
                    _occupied.RemoveAt(p);
                }
            }

            _slots[index] = null;
        }
    }
}
