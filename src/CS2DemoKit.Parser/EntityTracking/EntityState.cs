#region

using System.Runtime.CompilerServices;

#endregion

namespace CS2DemoKit.Parser.EntityTracking;

/// <summary>
///     Backing store for a single networked entity instance.
///     <para>
///         Schema Lens V1 lane storage: the canonical state for each entity is
///         three typed lanes (<c>int[]</c>,
///         <c>float[]</c>, <c>object?[]</c>) plus a per-lane <c>_seen[]</c> bitvector
///         and a string-keyed fallback dictionary. Until a per-class
///         <see cref="ClassShape" /> is bound, every write routes
///         through the fallback dict and the lane arrays stay empty — the
///         <see cref="Fields" /> projection then matches the old three-dict merge
///         bit-for-bit. The lane arrays fill as
///         <see cref="EntityTracker" /> binds shapes.
///     </para>
/// </summary>
public sealed class EntityState
{
    // The fallback dict carries the boxed value for any field whose path is
    // NOT covered by the bound ClassShape (no shape bound: every field; shape
    // bound: array elements, unmapped paths). Lazily allocated to keep cold
    // entities cheap.
    private Dictionary<string, object?>? _fallback;
    private float[]? _floatLane;

    private ulong[]? _floatSeen;
    // ── Lane storage (empty until BindShape() is called) ──────────────────────
    //
    // Allocated lazily by BindShape(). With no ClassShape bound the entity runs
    // in all-fallback mode: every write routes through _fallback, and the lanes
    // remain null.

    private int[]? _intLane;

    // Per-lane _seen bitvectors. Required so the projection can distinguish
    // "not received yet" from a default 0 / 0.0 / null write — otherwise every
    // shape slot would appear in Fields even before its first wire update.
    private ulong[]? _intSeen;
    private object?[]? _objectLane;
    private ulong[]? _objectSeen;

    // Bound per-class shape. Null in all-fallback mode; populated when
    // EntityTracker calls BindShape after slot-map construction.

    internal EntityState(string className, int serial)
    {
        ClassName = className;
        Serial = serial;
    }

    /// <summary>The runtime entity class name (e.g. "C_CSPlayerPawn").</summary>
    public string ClassName { get; }

    /// <summary>
    ///     Returns a snapshot of all received fields (path → value), merging the lane
    ///     storage (filtered by <c>_seen[]</c>) and the fallback dictionary. Allocates
    ///     a new dictionary on every call — intended for display and read-only
    ///     queries, not the hot decode path.
    ///     <para>
    ///         The projection is the load-bearing compatibility surface: it must
    ///         exactly match the pre-Schema-Lens three-dict merge so that
    ///         <c>SchemaKeysAssertionTests</c> and every analysis consumer
    ///         (<c>PlayerSnapshotBuilder</c> et al.) keep working unchanged.
    ///     </para>
    /// </summary>
    public IReadOnlyDictionary<string, object?> Fields
    {
        get
        {
            int fallbackCount = _fallback?.Count ?? 0;
            int laneCapacityHint = Shape is { } shape
                ? shape.IntSlotPaths.Length + shape.FloatSlotPaths.Length + shape.ObjectSlotPaths.Length
                : 0;
            Dictionary<string, object?> merged = new(fallbackCount + laneCapacityHint);

            // Order matches the old three-dict merge order:
            //   1. _fields (object)  → _objectLane
            //   2. _intFields        → _intLane
            //   3. _floatFields      → _floatLane
            // The fallback dict is merged FIRST so any lane write of the same key
            // wins on collisions. With no shape bound only the fallback dict is
            // populated; with shapes bound, lane writes dominate.
            if (_fallback is { } fb)
            {
                foreach (KeyValuePair<string, object?> kv in fb)
                {
                    merged[kv.Key] = kv.Value;
                }
            }

            if (Shape is { } s)
            {
                ProjectLane(merged, s.ObjectSlotPaths, _objectLane, _objectSeen);
                ProjectIntLane(merged, s.IntSlotPaths, _intLane, _intSeen);
                ProjectFloatLane(merged, s.FloatSlotPaths, _floatLane, _floatSeen);
            }

            return merged;
        }
    }

    /// <summary>True while this entity is inside the transmitted PVS.</summary>
    public bool IsInPvs { get; internal set; }

    /// <summary>Entity serial number from the network stream.</summary>
    public int Serial { get; internal set; }

    /// <summary>The bound per-class shape, or <c>null</c> if no shape has been bound.</summary>
    internal ClassShape? Shape { get; private set; }

    // ── Read API ──────────────────────────────────────────────────────────────

    /// <summary>Returns the raw boxed field value, or <c>null</c> if not yet received.</summary>
    public object? this[string path]
    {
        get
        {
            // Shape-mapped paths read from the lane. With no shape bound,
            // PathToSlot is empty and we fall through to the dict.
            if (Shape is { } s && s.PathToSlot.TryGetValue(path, out SlotAddr addr))
            {
                switch (addr.Lane)
                {
                    case LaneKind.Int:
                        if (IsSeen(_intSeen, addr.Slot))
                        {
                            return _intLane![addr.Slot];
                        }

                        break;
                    case LaneKind.Float:
                        if (IsSeen(_floatSeen, addr.Slot))
                        {
                            return _floatLane![addr.Slot];
                        }

                        break;
                    case LaneKind.Object:
                        if (IsSeen(_objectSeen, addr.Slot))
                        {
                            return _objectLane![addr.Slot];
                        }

                        break;
                }
            }

            return _fallback is { } fb ? fb.GetValueOrDefault(path) : null;
        }
    }

    /// <summary>
    ///     Seen-gated single-key lookup with the exact semantics of a
    ///     <see cref="Fields" /> <c>TryGetValue</c> — but without building the dictionary.
    ///     <para>
    ///         This exists because the indexer is <b>not</b> a drop-in for
    ///         <c>Fields.TryGetValue</c>: it collapses "absent" and "present, but null" into the
    ///         same <c>null</c>, so a caller whose control flow distinguishes those two (return
    ///         early on absent, fall through on present-null) cannot use it. Callers that need the
    ///         distinction previously had no choice but to touch <see cref="Fields" />, which
    ///         allocates a fresh merged dictionary of EVERY field on the entity — measured at
    ///         ~62 MiB of dictionary entries plus ~28 MiB of boxed ints across one demo, to read a
    ///         single handle per projectile per frame.
    ///     </para>
    ///     <para>
    ///         Resolution order mirrors <see cref="Fields" /> exactly: a seen lane slot wins over a
    ///         fallback entry of the same path (the projection merges the fallback first and lets
    ///         lane writes overwrite it), and an unseen lane slot falls through to the fallback.
    ///     </para>
    /// </summary>
    /// <param name="path">Field path to look up.</param>
    /// <param name="value">The received value, or <c>null</c> when the field is absent.</param>
    /// <returns><c>true</c> iff <see cref="Fields" /> would contain <paramref name="path" />.</returns>
    public bool TryGetValue(string path, out object? value)
    {
        if (Shape is { } s && s.PathToSlot.TryGetValue(path, out SlotAddr addr))
        {
            switch (addr.Lane)
            {
                case LaneKind.Int:
                    if (IsSeen(_intSeen, addr.Slot))
                    {
                        value = _intLane![addr.Slot];
                        return true;
                    }

                    break;
                case LaneKind.Float:
                    if (IsSeen(_floatSeen, addr.Slot))
                    {
                        value = _floatLane![addr.Slot];
                        return true;
                    }

                    break;
                case LaneKind.Object:
                    if (IsSeen(_objectSeen, addr.Slot))
                    {
                        value = _objectLane![addr.Slot];
                        return true;
                    }

                    break;
            }
        }

        if (_fallback is { } fb)
        {
            return fb.TryGetValue(path, out value);
        }

        value = null;
        return false;
    }

    /// <summary>
    ///     Produces a fully-detached deep copy of this state for the eager
    ///     <see cref="EntityTracker.Snapshot{T}" /> path. The copy clones the
    ///     three lanes, the <c>_seen[]</c> bitvectors, and the fallback dict so that a
    ///     later wire update mutating the live state cannot bleed into the frozen tree.
    ///     The immutable per-class <see cref="ClassShape" /> is shared by reference (it is
    ///     never mutated after construction). The boxed lane / fallback values are
    ///     themselves treated as immutable (wire-decoded scalars, strings, typed array
    ///     views) so a shallow element copy is a correct freeze.
    /// </summary>
    internal EntityState FreezeCopy()
    {
        EntityState copy = new(ClassName, Serial)
        {
            IsInPvs = IsInPvs,
            Shape = Shape,
            _intLane = _intLane is { } il ? (int[])il.Clone() : null,
            _floatLane = _floatLane is { } fl ? (float[])fl.Clone() : null,
            _objectLane = _objectLane is { } ol ? (object?[])ol.Clone() : null,
            _intSeen = _intSeen is { } isn ? (ulong[])isn.Clone() : null,
            _floatSeen = _floatSeen is { } fsn ? (ulong[])fsn.Clone() : null,
            _objectSeen = _objectSeen is { } osn ? (ulong[])osn.Clone() : null,
            _fallback = _fallback is { } fb ? new Dictionary<string, object?>(fb) : null
        };

        return copy;
    }

    /// <summary>Gets a field value, throwing if the field is absent or the wrong type.</summary>
    public T Get<T>(string path)
    {
        if (Shape is { } s && s.PathToSlot.TryGetValue(path, out SlotAddr addr))
        {
            switch (addr.Lane)
            {
                case LaneKind.Int when typeof(T) == typeof(int):
                    if (IsSeen(_intSeen, addr.Slot))
                    {
                        return (T)(object)_intLane![addr.Slot];
                    }

                    break;
                case LaneKind.Float when typeof(T) == typeof(float):
                    if (IsSeen(_floatSeen, addr.Slot))
                    {
                        return (T)(object)_floatLane![addr.Slot];
                    }

                    break;
                case LaneKind.Object:
                    if (IsSeen(_objectSeen, addr.Slot))
                    {
                        return (T)_objectLane![addr.Slot]!;
                    }

                    break;
            }
        }

        if (_fallback is { } fb && fb.TryGetValue(path, out object? v))
        {
            return (T)v!;
        }

        // Mirror the old behaviour of throwing on missing key by indexing the
        // (possibly null) fallback dict.
        return (T)(_fallback ?? throw new KeyNotFoundException(path))[path]!;
    }

    /// <summary>Gets a nullable value-type field; returns <c>null</c> if the field has not been received.</summary>
    public T? TryGet<T>(string path) where T : struct
    {
        if (Shape is { } s && s.PathToSlot.TryGetValue(path, out SlotAddr addr))
        {
            switch (addr.Lane)
            {
                case LaneKind.Int when typeof(T) == typeof(int):
                    if (IsSeen(_intSeen, addr.Slot))
                    {
                        return (T)(object)_intLane![addr.Slot];
                    }

                    return null;
                case LaneKind.Float when typeof(T) == typeof(float):
                    if (IsSeen(_floatSeen, addr.Slot))
                    {
                        return (T)(object)_floatLane![addr.Slot];
                    }

                    return null;
                case LaneKind.Object:
                    if (IsSeen(_objectSeen, addr.Slot))
                    {
                        return (T?)_objectLane![addr.Slot];
                    }

                    return null;
            }
        }

        return _fallback is { } fb && fb.TryGetValue(path, out object? v) ? (T?)v : null;
    }

    /// <summary>
    ///     Binds a per-class <see cref="ClassShape" />. Idempotent for the same shape
    ///     reference; rebinding to a different shape re-allocates the lane arrays.
    ///     Called once at slot creation by <see cref="EntityTracker" />.
    ///     <para>
    ///         Note: lane slots are NOT pre-populated with shape-supplied defaults
    ///         here. The <c>_seen[]</c> bitvector promise is "not received yet" vs
    ///         "received default 0/0.0/null" — and seeding every Lens-supplied default at
    ///         <c>BindShape</c> time would erase that distinction by reporting phantom
    ///         zero-valued fields on every freshly-created entity before any wire update
    ///         arrives. The forward-compat default path only applies to
    ///         <em>schema-missing</em> fields (Lens declares the path but the
    ///         FlattenedSerializer never names it), and the runtime can't classify
    ///         "schema-missing" without inspecting the per-class walk result — that
    ///         logic lives in <see cref="EntityTracker" /> not here.
    ///     </para>
    /// </summary>
    internal void BindShape(ClassShape shape)
    {
        if (ReferenceEquals(Shape, shape))
        {
            return;
        }

        Shape = shape;
        _intLane = shape.IntSlotPaths.Length > 0 ? new int[shape.IntSlotPaths.Length] : null;
        _floatLane = shape.FloatSlotPaths.Length > 0 ? new float[shape.FloatSlotPaths.Length] : null;
        _objectLane = shape.ObjectSlotPaths.Length > 0 ? new object?[shape.ObjectSlotPaths.Length] : null;
        _intSeen = AllocSeen(shape.IntSlotPaths.Length);
        _floatSeen = AllocSeen(shape.FloatSlotPaths.Length);
        _objectSeen = AllocSeen(shape.ObjectSlotPaths.Length);
    }

    /// <summary>
    ///     Resets every lane, the <c>_seen[]</c> bitvectors, and the fallback dict.
    ///     Clearing _seen is mandatory — otherwise a freshly-cleared slot would
    ///     still report 0 / 0.0 / null as "received" through the projection.
    /// </summary>
    internal void Clear()
    {
        if (_intLane is { } il)
        {
            Array.Clear(il);
        }

        if (_floatLane is { } fl)
        {
            Array.Clear(fl);
        }

        if (_objectLane is { } ol)
        {
            Array.Clear(ol);
        }

        if (_intSeen is { } isn)
        {
            Array.Clear(isn);
        }

        if (_floatSeen is { } fsn)
        {
            Array.Clear(fsn);
        }

        if (_objectSeen is { } osn)
        {
            Array.Clear(osn);
        }

        _fallback?.Clear();
        // NOTE: _shape stays bound. EntitySet.GetOrCreate only reuses a slot when
        // (className == existing.ClassName), so the shape is structurally the
        // same across Clear() calls — re-binding on every reuse would re-allocate
        // the lane arrays needlessly.
    }

    // ── Write API (internal — only EntityTracker writes) ──────────────────────

    /// <summary>Writes an integer to the int lane at <paramref name="slot" /> and marks it seen.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void SetIntSlot(int slot, int value)
    {
        _intLane![slot] = value;
        MarkSeen(_intSeen!, slot);
    }

    /// <summary>Writes a float to the float lane at <paramref name="slot" /> and marks it seen.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void SetFloatSlot(int slot, float value)
    {
        _floatLane![slot] = value;
        MarkSeen(_floatSeen!, slot);
    }

    /// <summary>Writes a boxed value to the object lane at <paramref name="slot" /> and marks it seen.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void SetObjectSlot(int slot, object? value)
    {
        _objectLane![slot] = value;
        MarkSeen(_objectSeen!, slot);
    }

    /// <summary>Reads the raw int from the lane (no <c>_seen</c> check; caller is responsible).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal int GetIntSlot(int slot) => _intLane![slot];

    /// <summary>Reads the raw float from the lane (no <c>_seen</c> check; caller is responsible).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal float GetFloatSlot(int slot) => _floatLane![slot];

    /// <summary>Reads the raw boxed value from the lane (no <c>_seen</c> check; caller is responsible).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal object? GetObjectSlot(int slot) => _objectLane![slot];

    // ── Seen-aware lane reads ─────────────────────────────────────────────────
    //
    // The plain GetXSlot trio above returns the lane value unconditionally, so a
    // slot that was never written is indistinguishable from one that received a
    // default 0 / 0.0 / null. That is correct for legitimate sentinel-0 fields
    // (HP, handles — where 0 IS "no value") but wrong for fields where 0 is a
    // meaningful received value distinct from "absent" (m_lifeState, where 0 ==
    // LIFE_ALIVE). These TryGet variants consult the per-lane _seen[] bitvector
    // so codegen can emit seen-aware typed getters that return null/false
    // for "never received". This is what lets the dict-path projection (Fields,
    // this[path], Get<T>(path) — all already seen-aware) and the lane-indexed
    // wrapper getters agree on the absent-vs-zero distinction.

    /// <summary>
    ///     Reads the int lane at <paramref name="slot" /> only if it has been written
    ///     (the <c>_seen</c> bit is set). Returns <c>false</c> and a default
    ///     <paramref name="value" /> when the slot has never received a wire update —
    ///     letting the caller distinguish "absent" from a received default <c>0</c>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool TryGetIntSlot(int slot, out int value)
    {
        if (IsSeen(_intSeen, slot))
        {
            value = _intLane![slot];
            return true;
        }

        value = 0;
        return false;
    }

    /// <summary>
    ///     Reads the float lane at <paramref name="slot" /> only if it has been written
    ///     (the <c>_seen</c> bit is set). Returns <c>false</c> and a default
    ///     <paramref name="value" /> when the slot has never received a wire update.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool TryGetFloatSlot(int slot, out float value)
    {
        if (IsSeen(_floatSeen, slot))
        {
            value = _floatLane![slot];
            return true;
        }

        value = 0f;
        return false;
    }

    /// <summary>
    ///     Reads the object lane at <paramref name="slot" /> only if it has been written
    ///     (the <c>_seen</c> bit is set). Returns <c>false</c> and a <c>null</c>
    ///     <paramref name="value" /> when the slot has never received a wire update —
    ///     distinguishing "absent" from a received <c>null</c> object.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool TryGetObjectSlot(int slot, out object? value)
    {
        if (IsSeen(_objectSeen, slot))
        {
            value = _objectLane![slot];
            return true;
        }

        value = null;
        return false;
    }

    /// <summary>Writes a string-keyed value to the fallback dict. Lazily allocates the dict.</summary>
    internal void SetFallback(string path, object? value)
    {
        _fallback ??= new Dictionary<string, object?>();
        _fallback[path] = value;
    }

    // ── Legacy write API (EntityTracker.ReadAndTrace still calls these; ───────
    //     lane-indexed call sites bypass them.) ──────────────────────────────

    /// <summary>Legacy boxed write — routes through the fallback dict.</summary>
    internal void Set(string path, object? value) => SetFallback(path, value);

    /// <summary>Legacy int write — boxes through the fallback dict.</summary>
    internal void SetInt(string path, int value) => SetFallback(path, value);

    /// <summary>Legacy float write — boxes through the fallback dict.</summary>
    internal void SetFloat(string path, float value) => SetFallback(path, value);

    // ── Lane helpers ──────────────────────────────────────────────────────────

    private static ulong[]? AllocSeen(int slotCount)
        => slotCount > 0 ? new ulong[slotCount + 63 >>> 6] : null;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsSeen(ulong[]? bits, int slot)
        => bits is not null && (bits[slot >>> 6] & 1UL << (slot & 63)) != 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void MarkSeen(ulong[] bits, int slot)
        => bits[slot >>> 6] |= 1UL << (slot & 63);

    private static void ProjectLane(Dictionary<string, object?> merged, string[] paths, object?[]? lane, ulong[]? seen)
    {
        if (lane is null || seen is null)
        {
            return;
        }

        for (int i = 0; i < paths.Length; i++)
        {
            if ((seen[i >>> 6] & 1UL << (i & 63)) != 0)
            {
                merged[paths[i]] = lane[i];
            }
        }
    }

    private static void ProjectIntLane(Dictionary<string, object?> merged, string[] paths, int[]? lane, ulong[]? seen)
    {
        if (lane is null || seen is null)
        {
            return;
        }

        for (int i = 0; i < paths.Length; i++)
        {
            if ((seen[i >>> 6] & 1UL << (i & 63)) != 0)
            {
                merged[paths[i]] = lane[i];
            }
        }
    }

    private static void ProjectFloatLane(Dictionary<string, object?> merged, string[] paths, float[]? lane, ulong[]? seen)
    {
        if (lane is null || seen is null)
        {
            return;
        }

        for (int i = 0; i < paths.Length; i++)
        {
            if ((seen[i >>> 6] & 1UL << (i & 63)) != 0)
            {
                merged[paths[i]] = lane[i];
            }
        }
    }
}
