#region

using System.Globalization;
using System.Numerics;
using CS2OpenDev.Sdk.Entities;
using CS2DemoKit.Parser.EntityTracking;

#endregion

namespace CS2DemoKit.Parser.Entities.SdkAbstractions;

/// <summary>
///     DVN's implementation of the SDK entity read contract: an
///     <see cref="IEntityFieldReader" /> over one <see cref="EntityState" />, addressed through
///     a <see cref="LensOrdinalMap" /> translation table.
///     <para>
///         Presence semantics are the runtime's own: a read reports a value exactly when
///         <see cref="EntityState.Fields" /> would contain the field — a seen lane slot wins,
///         an unseen slot falls through to the fallback dictionary, and "never received"
///         returns <c>false</c> from every <c>TryRead*</c>. That is the contract's
///         absent-vs-received-default asymmetry, and DVN's <c>_seen[]</c> bitvectors are what
///         make it satisfiable at all.
///     </para>
///     <para>
///         Deliberate CLR-conversion decisions (recorded in
///         <c>docs/upstream/sdk6-adapter-findings.md</c>):
///     </para>
///     <list type="bullet">
///         <item>
///             Bools are stored as int 0/1 on the int lane (DVN wire-encoding convention);
///             <see cref="TryReadBool" /> reads the lane and compares against zero.
///         </item>
///         <item>
///             Entity handles are stored boxed on the object lane, usually as
///             <see cref="ulong" /> (the CHandle wire decodes via the uint64 raw path);
///             <see cref="TryReadEntityHandle" /> width-folds to <see cref="uint" /> with an
///             unchecked cast and performs NO masking, NO sentinel interpretation — the raw
///             packed value crosses the seam undecoded, as the contract requires.
///         </item>
///         <item>
///             QAngle-typed fields decode into <see cref="Vector3" /> (pitch, yaw, roll) in
///             DVN's storage; <see cref="TryReadQAngle" /> reinterprets the components. The
///             storage cannot distinguish an angle from a position, so cross-shape reads
///             (<see cref="TryReadVector3" /> on an angle field) succeed here where the
///             reference reader would refuse — the discrimination lives in which accessor the
///             emitted wrapper property calls.
///         </item>
///         <item>
///             Everything else mirrors the reference <c>DictionaryEntityReader</c>'s conversion
///             rule: exact type match first, then <see cref="Convert.ChangeType(object, Type)" />
///             under the invariant culture, with cast/format/overflow failures reporting as
///             absent.
///         </item>
///     </list>
/// </summary>
public sealed class LensBoundReader : IEntityFieldReader
{
    private readonly LensOrdinalMap _map;
    private readonly EntityState _state;

    /// <summary>Wraps <paramref name="state" /> with a prebuilt translation table.</summary>
    public LensBoundReader(EntityState state, LensOrdinalMap map)
    {
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _map = map ?? throw new ArgumentNullException(nameof(map));
    }

    /// <summary>
    ///     Convenience overload: builds the translation table from the state's currently bound
    ///     <see cref="ClassShape" /> (or all-fallback when none is bound). Prefer the
    ///     table-taking overload on hot paths — <see cref="TrackerEntityWorld" /> caches the
    ///     table per class.
    /// </summary>
    public LensBoundReader(EntityState state, EntityClassBinding binding)
        : this(state, LensOrdinalMap.Build(binding, (state ?? throw new ArgumentNullException(nameof(state))).Shape))
    {
    }

    /// <summary>
    ///     The state's own class name — so an entity read through a base class's binding (a
    ///     subclass serializer bound to, say, the <c>CCSPlayerPawn</c> manifest) still reports
    ///     what it actually is, matching the reference reader's override case.
    /// </summary>
    public string EngineClassName => _state.ClassName;

    /// <inheritdoc />
    public bool TryReadInt32(int ordinal, out int value)
    {
        if (_map.TryGetResolved(ordinal, out SlotAddr addr, out _)
            && addr.Lane == LaneKind.Int
            && _state.TryGetIntSlot(addr.Slot, out value))
        {
            return true;
        }

        return TryReadConverted(ordinal, out value);
    }

    /// <inheritdoc />
    public bool TryReadUInt64(int ordinal, out ulong value)
        // No ulong lane exists: wide ints land boxed on the object lane under the
        // honour-the-wire rule (m_steamID, m_nButtons), so this is always a boxed read.
        => TryReadConverted(ordinal, out value);

    /// <inheritdoc />
    public bool TryReadSingle(int ordinal, out float value)
    {
        if (_map.TryGetResolved(ordinal, out SlotAddr addr, out _)
            && addr.Lane == LaneKind.Float
            && _state.TryGetFloatSlot(addr.Slot, out value))
        {
            return true;
        }

        return TryReadConverted(ordinal, out value);
    }

    /// <inheritdoc />
    public bool TryReadBool(int ordinal, out bool value)
    {
        // DVN convention: bool wires land on the int lane as 0/1.
        if (_map.TryGetResolved(ordinal, out SlotAddr addr, out _)
            && addr.Lane == LaneKind.Int
            && _state.TryGetIntSlot(addr.Slot, out int lane))
        {
            value = lane != 0;
            return true;
        }

        // Boxed path — the reference reader's exact acceptance set.
        if (!TryReadRaw(ordinal, out object? raw) || raw is null)
        {
            value = default;
            return false;
        }

        switch (raw)
        {
            case bool b:
                value = b;
                return true;
            case int i:
                value = i != 0;
                return true;
            case long l:
                value = l != 0;
                return true;
            case uint u:
                value = u != 0;
                return true;
            case ulong ul:
                value = ul != 0;
                return true;
            default:
                value = default;
                return false;
        }
    }

    /// <inheritdoc />
    public bool TryReadEntityHandle(int ordinal, out uint rawHandle)
    {
        if (!TryReadRaw(ordinal, out object? raw) || raw is null)
        {
            rawHandle = default;
            return false;
        }

        // Width normalization only — a fold, not a decode. DVN boxes CHandle wires as ulong
        // (uint64 raw decode path); older storage and fixtures may carry narrower widths. The
        // unchecked casts keep every meaningful bit of the 32-bit packed handle, including the
        // 0xFFFFFFFF "invalid" sentinel when it arrives boxed as int -1. No mask, no
        // index/serial split, no sentinel interpretation happens here.
        switch (raw)
        {
            case uint u:
                rawHandle = u;
                return true;
            case ulong ul:
                rawHandle = unchecked((uint)ul);
                return true;
            case int i:
                rawHandle = unchecked((uint)i);
                return true;
            case long l:
                rawHandle = unchecked((uint)l);
                return true;
            case ushort us:
                rawHandle = us;
                return true;
            case byte b:
                rawHandle = b;
                return true;
            default:
                rawHandle = default;
                return false;
        }
    }

    /// <inheritdoc />
    public bool TryReadVector3(int ordinal, out Vector3 value)
    {
        if (TryReadRaw(ordinal, out object? raw) && raw is Vector3 v)
        {
            value = v;
            return true;
        }

        value = default;
        return false;
    }

    /// <inheritdoc />
    public bool TryReadQAngle(int ordinal, out QAngle value)
    {
        if (TryReadRaw(ordinal, out object? raw))
        {
            switch (raw)
            {
                case QAngle q:
                    value = q;
                    return true;
                case Vector3 v:
                    // DVN's angle decoders produce Vector3(pitch, yaw, roll) — a component
                    // reinterpretation, not a coordinate conversion.
                    value = new QAngle(v.X, v.Y, v.Z);
                    return true;
            }
        }

        value = default;
        return false;
    }

    /// <inheritdoc />
    public bool TryReadObject(int ordinal, out object? value) => TryReadRaw(ordinal, out value);

    /// <inheritdoc />
    public bool TryReadByEnginePath(string enginePath, out object? value)
    {
        ArgumentNullException.ThrowIfNull(enginePath);

        // Exact wire spelling — curated or not. This is the contract's escape hatch to fields
        // nobody curated, served straight off the state's seen-gated lookup.
        if (_state.TryGetValue(enginePath, out value))
        {
            return true;
        }

        // Any spelling the binding knows (canonical or historical) routes through the
        // ordinal's candidate walk, so canonical-vs-wire spelling mismatches resolve in both
        // directions — the reference reader only needs the alias→canonical direction because
        // its storage is canonical-keyed; ours is wire-keyed.
        if (_map.TryGetOrdinal(enginePath, out int ordinal))
        {
            return TryReadRaw(ordinal, out value);
        }

        value = null;
        return false;
    }

    // ── Core reads ────────────────────────────────────────────────────────────

    /// <summary>
    ///     Boxed read with the exact presence semantics of
    ///     <see cref="EntityState.TryGetValue" />: seen lane slot wins; an unseen slot or a
    ///     fallback-routed ordinal probes every known spelling against the fallback dictionary.
    /// </summary>
    private bool TryReadRaw(int ordinal, out object? value)
    {
        if (!_map.TryGetResolved(ordinal, out SlotAddr addr, out string[] candidates))
        {
            value = null;
            return false;
        }

        switch (addr.Lane)
        {
            case LaneKind.Int:
                if (_state.TryGetIntSlot(addr.Slot, out int i))
                {
                    value = i;
                    return true;
                }

                break;
            case LaneKind.Float:
                if (_state.TryGetFloatSlot(addr.Slot, out float f))
                {
                    value = f;
                    return true;
                }

                break;
            case LaneKind.Object:
                if (_state.TryGetObjectSlot(addr.Slot, out value))
                {
                    return true;
                }

                break;
        }

        foreach (string candidate in candidates)
        {
            if (_state.TryGetValue(candidate, out value))
            {
                return true;
            }
        }

        value = null;
        return false;
    }

    /// <summary>
    ///     Mirror of the reference reader's conversion rule: exact match, else
    ///     <see cref="Convert.ChangeType(object, Type)" /> under the invariant culture;
    ///     cast/format/overflow failures report as absent — the reader has no value of the
    ///     requested type to hand back.
    /// </summary>
    private bool TryReadConverted<T>(int ordinal, out T value) where T : struct
    {
        if (!TryReadRaw(ordinal, out object? raw) || raw is null)
        {
            value = default;
            return false;
        }

        if (raw is T typed)
        {
            value = typed;
            return true;
        }

        try
        {
            value = (T)Convert.ChangeType(raw, typeof(T), CultureInfo.InvariantCulture);
            return true;
        }
        catch (Exception ex) when (ex is InvalidCastException or FormatException or OverflowException)
        {
            value = default;
            return false;
        }
    }
}
