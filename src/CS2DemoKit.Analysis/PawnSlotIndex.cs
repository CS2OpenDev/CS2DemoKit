#region

using System.Runtime.InteropServices;
using CS2DemoKit.Parser.EntityTracking;

#endregion

namespace CS2DemoKit.Analysis;

/// <summary>
///     The pawn entity indices, kept in ascending order off the tracker's create events, so the
///     per-frame pawn sweep visits a dozen slots instead of every live entity. Same walk as
///     <see cref="PawnLookup.ForEachLivePawn{TState}" />, same checks, same order; only the
///     candidate set is narrowed. Bound to one tracker; stale slots are pruned on each sync.
/// </summary>
internal sealed class PawnSlotIndex
{
    private readonly List<int> _slots = new(16);
    private EntityTracker? _tracker;

    public ReadOnlySpan<int> Slots => CollectionsMarshal.AsSpan(_slots);

    public void Sync(EntityTracker tracker)
    {
        ArgumentNullException.ThrowIfNull(tracker);
        if (!ReferenceEquals(tracker, _tracker))
        {
            Bind(tracker);
        }

        Prune(tracker);
    }

    /// <summary>The live-pawn walk over the index: a pawn with a live controller yields its slot.</summary>
    public void ForEachLivePawn<TState>(EntityTracker tracker, TState state, Action<TState, int, EntityState> onPawn)
    {
        Sync(tracker);
        EntitySet entities = tracker.CurrentEntities;
        foreach (int index in Slots)
        {
            EntityState? ent = entities[index];
            if (ent is null)
            {
                continue;
            }

            object? hv = ent["m_hController"];
            if (hv is null)
            {
                continue;
            }

            int ctrlIdx = PawnLookup.IndexOf(PawnLookup.TryUnboxHandle(hv));
            int slot = ctrlIdx - 1;
            if (slot < 0)
            {
                continue;
            }

            EntityState? ctrl = entities[ctrlIdx];
            if (ctrl is null || !ctrl.ClassName.Contains("PlayerController", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            onPawn(state, slot, ent);
        }
    }

    internal static bool IsPawn(string className) =>
        className.Contains("PlayerPawn", StringComparison.OrdinalIgnoreCase);

    private void Bind(EntityTracker tracker)
    {
        if (_tracker is not null)
        {
            _tracker.EntityCreated -= OnCreated;
        }

        _tracker = tracker;
        tracker.EntityCreated += OnCreated;
        _slots.Clear();
        foreach ((int index, EntityState entity) in tracker.CurrentEntities.AllIndexed())
        {
            if (IsPawn(entity.ClassName))
            {
                _slots.Add(index);
            }
        }
    }

    private void OnCreated(int index, EntityState state)
    {
        if (!IsPawn(state.ClassName))
        {
            return;
        }

        int at = _slots.BinarySearch(index);
        if (at < 0)
        {
            _slots.Insert(~at, index);
        }
    }

    private void Prune(EntityTracker tracker)
    {
        int keep = 0;
        for (int read = 0; read < _slots.Count; read++)
        {
            int index = _slots[read];
            EntityState? entity = tracker.CurrentEntities[index];
            if (entity is not null && IsPawn(entity.ClassName))
            {
                _slots[keep++] = index;
            }
        }

        _slots.RemoveRange(keep, _slots.Count - keep);
    }
}
