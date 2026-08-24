#region

using CS2DemoKit.Analysis.Abstractions;
using CS2OpenDev.Sdk.Entities;
using CS2DemoKit.Parser.EntityTracking;

#endregion

namespace CS2DemoKit.Analysis.Plugins;

/// <summary>
///     Declarative description of an entity-field read: what the five
///     hand-written providers encode in C#, as data. Adding an entity read becomes a spec (and,
///     with the catalog follow-up, a data-file line) instead of a class. The emit knobs encode
///     each shipped provider's exact snapshot semantics — the parity gate
///     (<c>ProviderDigestParityTests</c>) proves byte-identical digests against the hand-written
///     originals.
/// </summary>
/// <param name="Name">Stable provider name (e.g. <c>entity.pawn.health</c>).</param>
/// <param name="EntityClass">CS2 entity class the read targets (e.g. <c>CCSPlayerPawn</c>).</param>
/// <param name="Path">Dotted networked field path, read through the seen-gated indexer.</param>
/// <param name="ValueType">Declared value type (<c>int</c>/<c>bool</c>/<c>string</c>).</param>
/// <param name="PositiveOnly">
///     Emit gate: values ≤ 0 (and unseen) read as null. Health semantics — 0 means dead or
///     never-networked, both "no value".
/// </param>
/// <param name="UnseenAsDefault">
///     Map an unseen field (indexer null) to <c>default</c> of the value type instead of null.
///     Matches the typed-wrapper lane semantics the armor/equipment providers shipped with
///     (lanes initialize to 0; the wrapper never distinguishes unseen from 0).
/// </param>
/// <param name="ViaHandleToClassName">
///     Single-hop handle follow: when set, <see cref="Path" /> is ignored,
///     the handle at THIS path is read instead, resolved via <see cref="PawnLookup.ResolveHandle" />,
///     and the target entity's <c>ClassName</c> is the value (the active-weapon pattern).
/// </param>
/// <param name="ViaHandleToField">
///     Handle-then-field follow (Tier C ammo read): when set, <see cref="Path" /> is ignored;
///     the handle at <see cref="HandleFieldHop.HandlePath" /> is read off the subject entity,
///     resolved via <see cref="PawnLookup.ResolveHandle" />, and
///     <see cref="HandleFieldHop.TargetField" /> is read on the RESOLVED entity through its
///     seen-gated indexer, then coerced/gated like a direct read. At most one of
///     <see cref="ViaHandleToClassName" /> / <see cref="ViaHandleToField" /> may be set.
/// </param>
public sealed record ProviderSpec(
    string Name,
    string EntityClass,
    string Path,
    Type ValueType,
    bool PositiveOnly = false,
    bool UnseenAsDefault = false,
    string? ViaHandleToClassName = null,
    HandleFieldHop? ViaHandleToField = null);

/// <summary>
///     A two-hop read plan for <see cref="ProviderSpec.ViaHandleToField" />: follow the CHandle at
///     <paramref name="HandlePath" /> on the provider's <see cref="ProviderSpec.EntityClass" />,
///     then read <paramref name="TargetField" /> on the resolved entity (seen-gated indexer).
///     The resolved entity's concrete class varies at runtime (e.g. <c>CWeaponAK47</c> vs
///     <c>CWeaponGlock</c> for the active-weapon hop), so <paramref name="TargetField" /> is
///     validated by construction against the shared base schema, not the demo's per-class
///     descriptors — see <c>EntityChangeScanner.TryValidateProviderSchema</c>.
/// </summary>
/// <param name="HandlePath">Dotted networked path of the CHandle field on the subject entity.</param>
/// <param name="TargetField">Dotted networked path read on the entity the handle resolves to.</param>
public sealed record HandleFieldHop(string HandlePath, string TargetField);

/// <summary>
///     Providers that carry constructor state implement this so the scanner's parallel-decode
///     worker clone gets a proper fresh instance — the historical clone path was
///     <c>Activator.CreateInstance(type)</c>, which assumes a parameterless constructor
///     (the scanner's own clone comment anticipated the hook).
/// </summary>
/// <typeparam name="T">The provider contract being cloned.</typeparam>
public interface IWorkerCloneable<out T>
{
    /// <summary>A fresh instance for a parallel decode worker (no shared mutable state).</summary>
    T CloneForWorker();
}

/// <summary>
///     Opt-in capability for per-player providers that must read the raw <see cref="EntityState" />
///     rather than the SDK wrapper. Callers that hold both prefer this over
///     <see cref="IPerPlayerEntityValueProvider.ReadForPawn" /> when a provider implements it.
///     <para>
///         The SDK's typed accessors resolve through the Lens lane only, so a leaf that is not
///         lens-curated on the tracker at hand reads null through the wrapper while
///         <see cref="EntityState" />'s seen-gated indexer still returns it. The pawn's
///         <c>CBodyComponent</c> cell/offset pair is the shipped case: <c>CSPlayerPawn.Origin</c>
///         comes back null on trackers where <c>PositionUtil.CellToWorld</c> reconstructs a
///         position, so the position providers read state directly.
///     </para>
/// </summary>
public interface IPawnStateReader
{
    /// <summary>
    ///     Reads this provider's value off the pawn's raw entity state, with the same emit gate as
    ///     <see cref="IPerPlayerEntityValueProvider.ReadForPawn" />: <c>null</c> emits nothing.
    /// </summary>
    object? ReadForPawnState(EntityTracker tracker, EntityState pawn);
}

/// <summary>
///     Generic per-player entity-field provider: reads a <see cref="ProviderSpec" />
///     through the seen-gated <see cref="EntityState" /> indexer (lane-mapped and fallback
///     fields read identically), replacing one hand-written class per field.
/// </summary>
public sealed class GenericPerPlayerFieldProvider(ProviderSpec spec)
    : IPerPlayerEntityValueProvider, IWorkerCloneable<IPerPlayerEntityValueProvider>
{
    /// <summary>The spec this provider reads.</summary>
    public ProviderSpec Spec { get; } = spec.ViaHandleToClassName is not null && spec.ViaHandleToField is not null
        ? throw new ArgumentException(
            $"provider spec '{spec.Name}': ViaHandleToClassName and ViaHandleToField are mutually "
            + "exclusive — a spec follows the handle to a class name OR to a field, not both.",
            nameof(spec))
        : spec;

    /// <inheritdoc />
    public string EntityClass => Spec.EntityClass;

    /// <inheritdoc />
    public string FieldName => Spec.ViaHandleToClassName ?? Spec.ViaHandleToField?.HandlePath ?? Spec.Path;

    /// <inheritdoc />
    public string Name => Spec.Name;

    /// <inheritdoc />
    public Type ValueType => Spec.ValueType;

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
        if (Spec.ViaHandleToClassName is { } handlePath)
        {
            // Single-hop handle follow: pawn → handle field → target entity → ClassName.
            // ResolveHandle owns the wire-type variance (handles arrive as int/uint/ulong)
            // and returns null for the zero handle / empty slot.
            object? handleValue = pawn[handlePath];
            return handleValue is null ? null : PawnLookup.ResolveHandle(tracker, handleValue)?.ClassName;
        }

        if (Spec.ViaHandleToField is { } hop)
        {
            // Handle-then-field follow: pawn → handle field → target entity → target field.
            // Null at any hop (no handle / unresolved slot / unseen target field) reads as
            // null — the emit gate skips the slot, exactly like the ClassName hop above.
            object? hopHandle = pawn[hop.HandlePath];
            if (hopHandle is null)
            {
                return null;
            }

            EntityState? target = PawnLookup.ResolveHandle(tracker, hopHandle);
            return target is null ? null : Gate(Coerce(target[hop.TargetField]));
        }

        return Gate(Coerce(pawn[Spec.Path]));
    }

    /// <inheritdoc />
    public object? Read(EntityStateLayer layer, int playerSlot)
    {
        EntityState? pawn = PawnLookup.ResolvePawn(layer.Tracker, playerSlot);
        return pawn is null ? null : ReadForPawn(layer.Tracker, SdkEntityWorlds.Wrap<CSPlayerPawn>(layer.Tracker, pawn)!);
    }

    /// <inheritdoc />
    public IPerPlayerEntityValueProvider CloneForWorker() => new GenericPerPlayerFieldProvider(Spec);

    // CS2 networks ints as varints with wire-type variance; the indexer surfaces whatever the
    // lane/fallback stored. Mirrors FreezePeriodProvider's coercion discipline.
    private object? Coerce(object? raw)
    {
        if (raw is null)
        {
            return null;
        }

        if (Spec.ValueType == typeof(int))
        {
            return raw switch
            {
                int i => i,
                uint u => (int)u,
                long l => (int)l,
                ulong ul => (int)ul,
                _ => null
            };
        }

        if (Spec.ValueType == typeof(bool))
        {
            return raw switch
            {
                bool b => b,
                int i => i != 0,
                uint u => u != 0,
                _ => null
            };
        }

        if (Spec.ValueType == typeof(string))
        {
            return raw as string;
        }

        if (Spec.ValueType == typeof(float))
        {
            return raw switch
            {
                float f => f,
                double d => (float)d,
                _ => null
            };
        }

        return null;
    }

    private object? Gate(object? coerced)
    {
        if (Spec.PositiveOnly)
        {
            return coerced is int i and > 0 ? i : null;
        }

        if (coerced is null && Spec.UnseenAsDefault)
        {
            // Typed-wrapper lane parity: unseen reads as the lane default (0/false), exactly
            // what the hand-written armor/equipment providers observed through their wrappers.
            return Spec.ValueType.IsValueType ? Activator.CreateInstance(Spec.ValueType) : null;
        }

        return coerced;
    }
}

/// <summary>
///     Generic singleton entity-field provider: the data-driven form of
///     <see cref="FreezePeriodProvider" /> — polls one field on a singleton entity through the
///     seen-gated indexer; the scanner synthesizes change events per <see cref="EmitOn" />.
/// </summary>
/// <remarks>
///     <paramref name="markerType" /> and <paramref name="defaultValue" /> stay constructor
///     arguments (not spec/catalog data) deliberately: the dispatch pipeline keys synthesized
///     change events off a compile-time marker type per provider, and how catalog-defined
///     singletons mint markers (dynamic types vs a keyed dispatch extension) is the one
///     open design point, deliberately deferred. Until then, singleton specs are declared in
///     code next to their marker.
/// </remarks>
public sealed class GenericSingletonFieldProvider(
    ProviderSpec spec,
    ChangeDirection emitOn,
    Type markerType,
    object? defaultValue) : IEntityValueProvider, IWorkerCloneable<IEntityValueProvider>
{
    // Entity index cache: singletons live at a stable index once seen; the class-name scan is
    // the slow path. Per-instance state — the reason CloneForWorker exists. Index-keyed (not
    // reference-keyed) exactly like FreezePeriodProvider: the EntityState at an index can be
    // replaced across full packets, so the cache re-validates ClassName each read.
    private int _cachedEntityIndex = -1;

    /// <summary>The spec this provider reads.</summary>
    public ProviderSpec Spec { get; } = spec;

    /// <inheritdoc />
    public string ContextName => Spec.Name;

    /// <inheritdoc />
    public ChangeDirection EmitOn { get; } = emitOn;

    /// <inheritdoc />
    public string EntityClass => Spec.EntityClass;

    /// <inheritdoc />
    public string FieldName => Spec.Path;

    /// <inheritdoc />
    public Type ValueType => Spec.ValueType;

    /// <inheritdoc />
    public object? DefaultValue => defaultValue;

    /// <inheritdoc />
    public Type MarkerType => markerType;

    /// <inheritdoc />
    public object? Read(EntityStateLayer layer)
    {
        EntityState? entity = ResolveEntity(layer.Tracker);
        if (entity is null)
        {
            return null;
        }

        object? v = entity[Spec.Path];
        return v switch
        {
            null => null,
            bool b when Spec.ValueType == typeof(bool) => b,
            int i when Spec.ValueType == typeof(bool) => i != 0,
            uint u when Spec.ValueType == typeof(bool) => u != 0,
            int i when Spec.ValueType == typeof(int) => i,
            uint u when Spec.ValueType == typeof(int) => (int)u,
            string s when Spec.ValueType == typeof(string) => s,
            _ => null
        };
    }

    /// <inheritdoc />
    public IEntityValueProvider CloneForWorker() =>
        new GenericSingletonFieldProvider(Spec, EmitOn, markerType, defaultValue);

    private EntityState? ResolveEntity(EntityTracker tracker)
    {
        if (_cachedEntityIndex >= 0)
        {
            EntityState? cached = tracker.CurrentEntities[_cachedEntityIndex];
            if (cached is not null && cached.ClassName == Spec.EntityClass)
            {
                return cached;
            }

            _cachedEntityIndex = -1;
        }

        foreach ((int idx, EntityState ent) in tracker.CurrentEntities.AllIndexed())
        {
            if (ent.ClassName != Spec.EntityClass)
            {
                continue;
            }

            _cachedEntityIndex = idx;
            return ent;
        }

        return null;
    }
}
