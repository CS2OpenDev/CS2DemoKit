#region

using System.Diagnostics.CodeAnalysis;
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
///     the handle at THIS path is read instead, resolved via <see cref="PawnLookup.ResolveHandle(EntityTracker, object?)" />,
///     and the target entity's <c>ClassName</c> is the value (the active-weapon pattern).
/// </param>
/// <param name="ViaHandleToField">
///     Handle-then-field follow (Tier C ammo read): when set, <see cref="Path" /> is ignored;
///     the handle at <see cref="HandleFieldHop.HandlePath" /> is read off the subject entity,
///     resolved via <see cref="PawnLookup.ResolveHandle(EntityTracker, object?)" />, and
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
///     Opt-in for a provider whose field moved between CS2 schema versions, so its declared
///     <see cref="IPerPlayerEntityValueProvider.FieldName" /> is only one of several spellings a
///     demo might carry.
///     <para>
///         Schema validation normally judges the single declared path and throws when it is
///         missing, which is the right default: a typo or a drifted field has to be loud. But a
///         field Valve RENAMED has two correct spellings depending on when the demo was recorded,
///         and both are in circulation. Declaring one of them makes every demo of the other
///         vintage throw at prime time, which aborts the whole analysis rather than degrading a
///         single column.
///     </para>
///     <para>
///         A provider implementing this is validated against <see cref="CandidateFieldNames" />
///         instead: at least one must exist and be type-compatible. The gate stays loud, because
///         a demo carrying NONE of them still throws.
///     </para>
/// </summary>
public interface IMultiSchemaFieldProvider
{
    /// <summary>
    ///     Every path this provider can read, in preference order. Validation passes when any one
    ///     of them resolves on the demo at hand.
    /// </summary>
    IReadOnlyList<string> CandidateFieldNames { get; }
}

/// <summary>
///     Generic per-player entity-field provider: reads a <see cref="ProviderSpec" />
///     through the seen-gated <see cref="EntityState" /> indexer (lane-mapped and fallback
///     fields read identically), replacing one hand-written class per field.
/// </summary>
public sealed class GenericPerPlayerFieldProvider(ProviderSpec spec)
    : IPerPlayerEntityValueProvider, IWorkerCloneable<IPerPlayerEntityValueProvider>,
        IPawnIntCellReader, IPawnFloatCellReader, IPawnStringCellReader
{
    // Where the spec's leaf lives on the last class shape each was resolved against: the pawn
    // side (the direct path, or the handle path of a hop) and the hop target's field. Per
    // instance, and instances are per worker, so no sharing.
    private readonly LaneCursor _pawnCursor = new();
    private readonly LaneCursor _targetCursor = new();

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

    // ── Typed cell readers ─────────────────────────────────────────────────────
    //
    // Each is ReadForPawn for one declared value type with the box removed: the leaf is read off
    // its lane when the exact path is a seen lane slot, and through the same boxed indexer
    // ReadForPawn uses otherwise (that read owns the fallback dictionary and the wrapper's alias
    // walk, so a path the state does not carry under its exact spelling still resolves). The
    // coercion and gate are the boxed ones restated on typed values; PawnCellReaderParityTests
    // holds the two together on a real demo.

    /// <inheritdoc />
    public bool TryReadInt(PawnReadContext context, out int value)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (Spec.ValueType == typeof(bool))
        {
            bool hasBool = TryReadBoolCell(context, out bool b);
            value = b ? 1 : 0;
            return hasBool;
        }

        RequireKind(typeof(int));
        if (!TryReadLeaf(context, out LaneHit hit, out int lane, out _, out object? boxed))
        {
            value = 0;
            return false;
        }

        bool has;
        switch (hit)
        {
            case LaneHit.Int:
                value = lane;
                has = true;
                break;
            case LaneHit.Float:
                value = 0;
                has = false;
                break;
            default:
                has = PawnCellCoercion.TryCoerceInt(boxed, out value);
                break;
        }

        if (Spec.PositiveOnly)
        {
            return has && value > 0;
        }

        if (!has && Spec.UnseenAsDefault)
        {
            value = 0;
            return true;
        }

        return has;
    }

    /// <inheritdoc />
    public bool TryReadFloat(PawnReadContext context, out float value)
    {
        ArgumentNullException.ThrowIfNull(context);
        RequireKind(typeof(float));
        if (!TryReadLeaf(context, out LaneHit hit, out _, out float lane, out object? boxed))
        {
            value = 0f;
            return false;
        }

        bool has;
        switch (hit)
        {
            case LaneHit.Float:
                value = lane;
                has = true;
                break;
            case LaneHit.Int:
                value = 0f;
                has = false;
                break;
            default:
                has = PawnCellCoercion.TryCoerceFloat(boxed, out value);
                break;
        }

        if (Spec.PositiveOnly)
        {
            // The gate admits only a positive int; a float never is one.
            return false;
        }

        if (!has && Spec.UnseenAsDefault)
        {
            value = 0f;
            return true;
        }

        return has;
    }

    /// <inheritdoc />
    public bool TryReadString(PawnReadContext context, [NotNullWhen(true)] out string? value)
    {
        ArgumentNullException.ThrowIfNull(context);
        RequireKind(typeof(string));
        if (Spec.ViaHandleToClassName is { } handlePath)
        {
            // The ClassName hop is neither coerced nor gated, exactly as in ReadForPawn.
            value = TryResolveHop(context, handlePath, out EntityState? target) ? target.ClassName : null;
            return value is not null;
        }

        if (!TryReadLeaf(context, out LaneHit hit, out _, out _, out object? boxed))
        {
            value = null;
            return false;
        }

        value = hit is LaneHit.Int or LaneHit.Float ? null : boxed as string;

        // PositiveOnly admits only a positive int, and UnseenAsDefault defaults value types only,
        // so a string column has no value under either gate when the read produced none.
        return value is not null && !Spec.PositiveOnly;
    }

    private bool TryReadBoolCell(PawnReadContext context, out bool value)
    {
        if (!TryReadLeaf(context, out LaneHit hit, out int lane, out _, out object? boxed))
        {
            value = false;
            return false;
        }

        bool has;
        switch (hit)
        {
            case LaneHit.Int:
                value = lane != 0;
                has = true;
                break;
            case LaneHit.Float:
                value = false;
                has = false;
                break;
            default:
                has = PawnCellCoercion.TryCoerceBool(boxed, out value);
                break;
        }

        if (Spec.PositiveOnly)
        {
            // The gate admits only a positive int; a bool never is one.
            return false;
        }

        if (!has && Spec.UnseenAsDefault)
        {
            value = false;
            return true;
        }

        return has;
    }

    // The spec's leaf for the bound pawn: on the pawn itself, or on the entity its handle hop
    // resolves to. Returns false when the hop resolved to nothing: ReadForPawn reports that as
    // null BEFORE Coerce and Gate run, so no gate may default or reject it, and the typed readers
    // return "no value" without consulting the gate. Otherwise a lane hit carries the typed value
    // and a miss carries the boxed read (possibly null), which the gate then judges.
    private bool TryReadLeaf(PawnReadContext context, out LaneHit hit, out int intValue, out float floatValue, out object? boxed)
    {
        if (Spec.ViaHandleToClassName is not null)
        {
            throw new InvalidOperationException(
                $"provider spec '{Spec.Name}' follows a handle to a class name, which is a string, but declares "
                + $"{Spec.ValueType.Name}");
        }

        if (Spec.ViaHandleToField is { } hop)
        {
            if (!TryResolveHop(context, hop.HandlePath, out EntityState? target))
            {
                hit = LaneHit.Miss;
                intValue = 0;
                floatValue = 0f;
                boxed = null;
                return false;
            }

            hit = PawnCellCoercion.Probe(target, hop.TargetField, _targetCursor, out intValue, out floatValue, out boxed);
            if (hit == LaneHit.Miss)
            {
                boxed = target[hop.TargetField];
            }

            return true;
        }

        hit = PawnCellCoercion.Probe(context.Pawn, Spec.Path, _pawnCursor, out intValue, out floatValue, out boxed);
        if (hit == LaneHit.Miss)
        {
            boxed = context.Wrapper[Spec.Path];
        }

        return true;
    }

    // ReadForPawn's handle follow with the handle kept unboxed: a lane hit goes straight to
    // IndexOf as the uint ResolveHandle would have unboxed it to.
    private bool TryResolveHop(PawnReadContext context, string handlePath, [NotNullWhen(true)] out EntityState? target)
    {
        if (!PawnCellCoercion.TryProbeHandle(context.Pawn, handlePath, _pawnCursor, out uint handle))
        {
            object? read = context.Wrapper[handlePath];
            if (read is null)
            {
                target = null;
                return false;
            }

            handle = PawnLookup.TryUnboxHandle(read);
        }

        int index = PawnLookup.IndexOf(handle);
        target = index < 0 ? null : context.Tracker.CurrentEntities[index];
        return target is not null;
    }

    private void RequireKind(Type expected)
    {
        if (Spec.ValueType != expected)
        {
            throw new InvalidOperationException(
                $"provider spec '{Spec.Name}' declares {Spec.ValueType.Name}; it cannot be read as a {expected.Name} column");
        }
    }

    // CS2 networks ints as varints with wire-type variance; the indexer surfaces whatever the
    // lane/fallback stored. Mirrors FreezePeriodProvider's coercion discipline. The typed cell
    // readers apply the same PawnCellCoercion functions and restate Gate on typed values;
    // PawnCellReaderParityTests holds the two together, gate arm by gate arm, on a real demo.
    private object? Coerce(object? raw)
    {
        if (raw is null)
        {
            return null;
        }

        if (Spec.ValueType == typeof(int))
        {
            return PawnCellCoercion.TryCoerceInt(raw, out int i) ? i : null;
        }

        if (Spec.ValueType == typeof(bool))
        {
            return PawnCellCoercion.TryCoerceBool(raw, out bool b) ? b : null;
        }

        if (Spec.ValueType == typeof(string))
        {
            return raw as string;
        }

        if (Spec.ValueType == typeof(float))
        {
            return PawnCellCoercion.TryCoerceFloat(raw, out float f) ? f : null;
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
