#region

using CS2OpenDev.Sdk.Entities;
using CS2DemoKit.Parser.EntityTracking;

#endregion

namespace CS2DemoKit.Parser.Entities.SdkAbstractions;

/// <summary>
///     DVN's implementation of the SDK contract's <see cref="IEntityWorld" /> — cross-entity
///     handle resolution over an <see cref="EntityTracker" />.
///     <para>
///         <see cref="Resolve{T}" /> delegates to
///         <see cref="EntityTracker.ResolveHandle{T}" />, reusing the tracker's existing
///         sentinel checks (<c>0</c> and <c>0xFFFFFFFF</c> are "no entity"), its 14-bit index
///         mask, slot lookup and factory dispatch. Nothing about handle decoding is
///         re-implemented on this side of the seam; the <c>uint</c>/<c>int</c> width difference
///         is folded with one unchecked cast, exactly the adapter the accepted SDK#6 proposal
///         (§8.2, the world-over-tracker mapping) described.
///     </para>
///     <para>
///         Wrapper construction goes through the same registry the tracker already dispatches:
///         <see cref="RegisterWrapper" /> installs a per-class factory via
///         <see cref="EntityTracker.RegisterEntityFactory" /> that binds a
///         <see cref="LensBoundReader" /> over the target's live <see cref="EntityState" />.
///         Production registers the SDK package's own <c>EntityWrapperRegistry</c> factories
///         (the Analysis layer's <c>SdkEntityWorlds</c> is the wiring point). Registering a
///         class REPLACES any previously registered factory for it — the tracker keeps one
///         factory per class by design.
///     </para>
/// </summary>
public sealed class TrackerEntityWorld : IEntityWorld
{
    private readonly object _gate = new();

    // Per-class translation-table cache. A class's ClassShape is immutable and shared by
    // reference across every EntityState of that class within one tracker, so caching by
    // (shape reference) is exact: a rebind (new demo through the same tracker) produces a new
    // shape reference and transparently rebuilds the table.
    private readonly Dictionary<string, (ClassShape? Shape, LensOrdinalMap Map)> _mapCache = new(StringComparer.Ordinal);

    private readonly EntityTracker _tracker;

    /// <summary>Creates a world over <paramref name="tracker" />'s live entity table.</summary>
    public TrackerEntityWorld(EntityTracker tracker)
        => _tracker = tracker ?? throw new ArgumentNullException(nameof(tracker));

    /// <inheritdoc />
    public T? Resolve<T>(uint rawHandle) where T : EntityWrapper
        => _tracker.ResolveHandle<T>(unchecked((int)rawHandle));

    /// <summary>
    ///     Registers a wrapper factory for <paramref name="binding" />'s engine class. Every
    ///     wrapper the tracker subsequently constructs for that class — via
    ///     <see cref="EntityTracker.Get{T}" />, <see cref="EntityTracker.Snapshot{T}" /> or
    ///     handle resolution — is built over a <see cref="LensBoundReader" /> and this world.
    /// </summary>
    public void RegisterWrapper(
        EntityClassBinding binding,
        Func<IEntityFieldReader, IEntityWorld, EntityWrapper> factory)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(factory);

        _tracker.RegisterEntityFactory(binding.EngineClass,
            (state, _) => factory(CreateReader(binding, state), this));
    }

    /// <summary>
    ///     Binds a reader over one entity's state using the cached per-class translation
    ///     table. Exposed so consumers can read an entity through the seam without registering
    ///     a wrapper class (probes, inspectors).
    /// </summary>
    public LensBoundReader CreateReader(EntityClassBinding binding, EntityState state)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(state);

        return new LensBoundReader(state, MapFor(binding, state));
    }

    private LensOrdinalMap MapFor(EntityClassBinding binding, EntityState state)
    {
        ClassShape? shape = state.Shape;
        lock (_gate)
        {
            if (_mapCache.TryGetValue(binding.EngineClass, out (ClassShape? Shape, LensOrdinalMap Map) entry)
                && ReferenceEquals(entry.Shape, shape))
            {
                return entry.Map;
            }

            LensOrdinalMap map = LensOrdinalMap.Build(binding, shape);
            _mapCache[binding.EngineClass] = (shape, map);
            return map;
        }
    }
}
