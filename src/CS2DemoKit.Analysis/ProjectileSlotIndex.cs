#region

using System.Runtime.InteropServices;
using CS2DemoKit.Analysis.Visibility;
using CS2DemoKit.Parser.EntityTracking;

#endregion

namespace CS2DemoKit.Analysis;

/// <summary>
///     The entity slots that currently hold a projectile the digest reads (a smoke or a molotov),
///     so the per-frame extraction visits those few slots instead of every live entity. A demo
///     keeps two to four hundred entities live and at most a handful of them are projectiles, and
///     the walk that found them by elimination was costing more than a third of the decode it
///     rode on; this keeps the same answer in the same order for the price of a slot check.
///     <para>
///         Membership comes from two sources and the second is what makes the first safe: the
///         tracker's <see cref="EntityTracker.EntityCreated" /> adds a slot the moment a
///         projectile enters it, and a full walk seeds the set whenever the index meets a tracker
///         it has not seen, so a worker primed from a mid-demo checkpoint, or a layer reset under
///         a live index, starts from the projectiles already alive rather than from the ones it
///         happened to watch appear. Removal is lazy: a slot whose entity is gone, or now holds
///         another class, is dropped the next time the index is synchronised, which is once per
///         frame and before it is read.
///     </para>
///     <para>
///         Slots are kept ascending, so a consumer iterating them visits entities in the order the
///         full walk did and the digest it builds is byte-identical to the walked one; that
///         equivalence is pinned by the parity tests, and the walk itself stays in
///         <see cref="EntityDigestExtractor" /> as the oracle they compare against.
///     </para>
///     <para>
///         One index per tracker owner and never shared: it subscribes to a tracker event, and the
///         parallel producer gives each chunk worker its own layer, so each gets its own index.
///     </para>
/// </summary>
internal sealed class ProjectileSlotIndex
{
    /// <summary>The molotov projectile class; the synthesized throw event is one per creation of it.</summary>
    internal const string MolotovClass = "CMolotovProjectile";

    private readonly List<int> _slots = new(8);
    private EntityTracker? _tracker;

    /// <summary>Ascending slot indices holding a projectile as of the last <see cref="Sync" />.</summary>
    public ReadOnlySpan<int> Slots => CollectionsMarshal.AsSpan(_slots);

    /// <summary>How many times a full walk seeded the set; one per tracker the index has been bound to.</summary>
    public int SeedWalks { get; private set; }

    /// <summary>
    ///     Brings the set up to date for a read against <paramref name="tracker" />: binds and seeds
    ///     on a tracker the index has not seen, then drops every slot that no longer holds a
    ///     projectile. Call once per frame after the layer has been advanced and before reading
    ///     <see cref="Slots" />.
    /// </summary>
    /// <param name="tracker">The tracker whose entity set the slots index.</param>
    public void Sync(EntityTracker tracker)
    {
        ArgumentNullException.ThrowIfNull(tracker);
        if (!ReferenceEquals(tracker, _tracker))
        {
            Bind(tracker);
        }

        Prune(tracker);
    }

    /// <summary>
    ///     Whether <paramref name="className" /> is one of the two projectile classes the digest
    ///     reads. The smoke half defers to <see cref="VisibilityAnalyzer.SmokeClass" /> rather than
    ///     naming the class again: the gate that decides whether a cloud reaches the digest is
    ///     <see cref="VisibilityAnalyzer.TryActiveSmoke" />, and an index that admitted a narrower
    ///     set than that gate accepts would drop clouds the walk oracle keeps.
    /// </summary>
    internal static bool IsProjectile(string className) =>
        className == VisibilityAnalyzer.SmokeClass || className == MolotovClass;

    private void Bind(EntityTracker tracker)
    {
        if (_tracker is not null)
        {
            _tracker.EntityCreated -= OnCreated;
        }

        _tracker = tracker;
        tracker.EntityCreated += OnCreated;

        // Everything already alive, in ascending slot order, which is what AllIndexed yields.
        _slots.Clear();
        foreach ((int index, EntityState entity) in tracker.CurrentEntities.AllIndexed())
        {
            if (IsProjectile(entity.ClassName))
            {
                _slots.Add(index);
            }
        }

        SeedWalks++;
    }

    private void OnCreated(int index, EntityState state)
    {
        if (!IsProjectile(state.ClassName))
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
        // In-place compaction keeps the ascending order without a second list.
        int keep = 0;
        for (int read = 0; read < _slots.Count; read++)
        {
            int index = _slots[read];
            EntityState? entity = tracker.CurrentEntities[index];
            if (entity is not null && IsProjectile(entity.ClassName))
            {
                _slots[keep++] = index;
            }
        }

        _slots.RemoveRange(keep, _slots.Count - keep);
    }
}
