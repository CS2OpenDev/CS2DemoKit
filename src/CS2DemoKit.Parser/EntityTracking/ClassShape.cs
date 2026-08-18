#region

using System.Diagnostics.CodeAnalysis;

#endregion

namespace CS2DemoKit.Parser.EntityTracking;

/// <summary>
///     Which Schema Lens lane (typed storage array on <see cref="EntityState" />)
///     a given field's value lives in.
/// </summary>
[SuppressMessage("Naming", "CA1720:Identifier contains type name",
    Justification = "Lane names mirror the underlying CLR storage type (int[], float[], object?[]) and are intentionally short for the hot decode path.")]
public enum LaneKind : byte
{
    /// <summary>String-keyed fallback dictionary (array elements, unmapped paths).</summary>
    Fallback = 0,

    /// <summary>The <c>int[]</c> lane. Bools land here too (0 / 1).</summary>
    Int = 1,

    /// <summary>The <c>float[]</c> lane.</summary>
    Float = 2,

    /// <summary>The <c>object?[]</c> lane (vectors, strings, handles, sub-entities).</summary>
    Object = 3
}

/// <summary>
///     A lane + slot index pair identifying where a field's value lives on an
///     <see cref="EntityState" />.
/// </summary>
public readonly record struct SlotAddr(LaneKind Lane, int Slot)
{
    /// <summary>Sentinel address representing "no slot bound — read/write through the fallback dict".</summary>
    public static SlotAddr Fallback => new(LaneKind.Fallback, -1);
}

/// <summary>
///     Immutable per-class plan describing which dotted-path leaf fields map to
///     which slot in which lane on <see cref="EntityState" />. Built once per
///     serializer name by <see cref="EntityTracker" /> during the first
///     <c>BuildFieldDescs</c> walk and bound to every <see cref="EntityState" />
///     for that class via <see cref="EntityState.BindShape" />.
///     <para>
///         Both directions are stored: <see cref="PathToSlot" /> for the read
///         API (<c>state[path]</c>, <c>state.Get&lt;T&gt;</c>), and the per-lane
///         <c>SlotPaths</c> arrays for the <see cref="EntityState.Fields" />
///         projection to rebuild the exact string keys that today's three-dict
///         merge produced (load-bearing for <c>SchemaKeysAssertionTests</c>).
///     </para>
/// </summary>
public sealed class ClassShape
{
    /// <summary>
    ///     Constructs a shape directly from precomputed arrays. Prefer
    ///     <see cref="ClassShapeBuilder" /> for incremental construction during
    ///     descriptor walks.
    /// </summary>
    public ClassShape(
        string className,
        IReadOnlyDictionary<string, SlotAddr> pathToSlot,
        string[] intSlotPaths,
        string[] floatSlotPaths,
        string[] objectSlotPaths,
        object?[]? intDefaults = null,
        object?[]? floatDefaults = null,
        object?[]? objectDefaults = null,
        LensTransform[]? intTransforms = null,
        LensTransform[]? floatTransforms = null,
        LensTransform[]? objectTransforms = null)
    {
        ClassName = className;
        PathToSlot = pathToSlot;
        IntSlotPaths = intSlotPaths;
        FloatSlotPaths = floatSlotPaths;
        ObjectSlotPaths = objectSlotPaths;
        IntDefaults = intDefaults;
        FloatDefaults = floatDefaults;
        ObjectDefaults = objectDefaults;
        IntTransforms = intTransforms;
        FloatTransforms = floatTransforms;
        ObjectTransforms = objectTransforms;
    }

    /// <summary>The serializer (class) name this shape describes (e.g. <c>CCSPlayerPawn</c>).</summary>
    public string ClassName { get; }

    /// <summary>
    ///     Dotted leaf path → (lane, slot) for every shape-mapped field. Lookups
    ///     not present here fall through to the fallback dict.
    /// </summary>
    public IReadOnlyDictionary<string, SlotAddr> PathToSlot { get; }

    /// <summary>slot index → dotted leaf path for each int-lane slot (used by the projection).</summary>
    public string[] IntSlotPaths { get; }

    /// <summary>slot index → dotted leaf path for each float-lane slot (used by the projection).</summary>
    public string[] FloatSlotPaths { get; }

    /// <summary>slot index → dotted leaf path for each object-lane slot (used by the projection).</summary>
    public string[] ObjectSlotPaths { get; }

    /// <summary>
    ///     Optional pre-populated int-lane defaults (the forward-compat path):
    ///     when an entry is non-null, <see cref="EntityState.BindShape" /> writes
    ///     <c>(int)IntDefaults[i]</c> into the int lane at slot <c>i</c> and marks
    ///     the seen-bit so the projection includes the canonical path even when
    ///     no wire update ever arrives. <c>null</c> means "no Lens default for
    ///     this slot — leave the slot unseen until the first wire write".
    /// </summary>
    public object?[]? IntDefaults { get; }

    /// <summary>Optional pre-populated float-lane defaults — see <see cref="IntDefaults" />.</summary>
    public object?[]? FloatDefaults { get; }

    /// <summary>Optional pre-populated object-lane defaults — see <see cref="IntDefaults" />.</summary>
    public object?[]? ObjectDefaults { get; }

    /// <summary>
    ///     Per-slot transform on the int lane, indexed parallel to
    ///     <see cref="IntSlotPaths" />. <see cref="LensTransform.None" /> for slots
    ///     allocated by the plain decoder-kind classifier. <c>null</c> means
    ///     no transforms were ever assigned (pre-Lens shape).
    /// </summary>
    public LensTransform[]? IntTransforms { get; }

    /// <summary>Per-slot transform on the float lane — see <see cref="IntTransforms" />.</summary>
    public LensTransform[]? FloatTransforms { get; }

    /// <summary>Per-slot transform on the object lane — see <see cref="IntTransforms" />.</summary>
    public LensTransform[]? ObjectTransforms { get; }
}

/// <summary>
///     Incremental builder for <see cref="ClassShape" />. Allocates one slot per
///     call to <see cref="Allocate" /> in the requested lane and remembers the
///     dotted path that produced the slot. <see cref="EntityTracker" /> calls
///     this from inside the recursive <c>BuildFieldDescs</c> walk for every
///     scalar leaf descriptor on the non-array spine of a class.
/// </summary>
internal sealed class ClassShapeBuilder
{
    private readonly string _className;
    private readonly HashSet<int> _floatCodegenSlots = new();
    private readonly List<object?> _floatDefaults = new();
    private readonly List<string> _floatSlotPaths = new();
    private readonly List<LensTransform> _floatTransforms = new();
    private readonly HashSet<int> _intCodegenSlots = new();
    private readonly List<object?> _intDefaults = new();
    private readonly List<string> _intSlotPaths = new();
    private readonly List<LensTransform> _intTransforms = new();
    private readonly HashSet<int> _objectCodegenSlots = new();
    private readonly List<object?> _objectDefaults = new();
    private readonly List<string> _objectSlotPaths = new();
    private readonly List<LensTransform> _objectTransforms = new();
    private readonly Dictionary<string, SlotAddr> _pathToSlot = new();
    private bool _hasAnyDefault;
    private bool _hasAnyTransform;

    internal ClassShapeBuilder(string className) => _className = className;

    /// <summary>
    ///     Pre-reserves a codegen-emitted Lens slot in the given lane *before*
    ///     <see cref="Allocate" /> is called for any field. Adds the slot index
    ///     to the per-lane <c>codegenSlots</c> HashSet so the auto-increment
    ///     branch of <see cref="Allocate" /> skips over it.
    ///     <para>
    ///         The pre-pass over a serializer's spine (in
    ///         <see cref="EntityTracker" />) calls this for every Lens-pinned leaf
    ///         before the real <c>BuildFieldDescs</c> walk allocates anything.
    ///         Without this pre-reservation, an auto-incrementing non-Lens field
    ///         walked before a Lens pin could claim the Lens-reserved slot, and
    ///         the later Lens pin would silently overwrite the auto-inc field's
    ///         path metadata — dropping its key from the projection.
    ///     </para>
    ///     <para>
    ///         Negative slot indices are no-ops (the "no codegen slot" sentinel —
    ///         <see cref="Allocate" /> will auto-increment as usual).
    ///     </para>
    /// </summary>
    internal void ReserveLensSlot(LaneKind lane, int lensSlot)
    {
        if (lensSlot < 0)
        {
            return;
        }

        switch (lane)
        {
            case LaneKind.Int:
                _intCodegenSlots.Add(lensSlot);
                break;
            case LaneKind.Float:
                _floatCodegenSlots.Add(lensSlot);
                break;
            case LaneKind.Object:
                _objectCodegenSlots.Add(lensSlot);
                break;
        }
    }

    /// <summary>
    ///     Allocates the next slot in <paramref name="lane" /> and records its
    ///     dotted <paramref name="path" />. If the path was already assigned a
    ///     slot (idempotent re-walks via baseline replay etc. should not
    ///     happen during build, but guard against it), returns the existing
    ///     address. Optional <paramref name="transform" /> and
    ///     <paramref name="fallbackDefault" /> are captured per-slot for
    ///     <see cref="EntityState.BindShape" /> to consume.
    /// </summary>
    internal SlotAddr Allocate(
        LaneKind lane,
        string path,
        LensTransform transform = LensTransform.None,
        object? fallbackDefault = null,
        int lensSlot = -1)
    {
        if (_pathToSlot.TryGetValue(path, out SlotAddr existing))
        {
            return existing;
        }

        SlotAddr addr = lane switch
        {
            LaneKind.Int => Append(_intSlotPaths, _intTransforms, _intDefaults, _intCodegenSlots,
                LaneKind.Int, path, transform, fallbackDefault, lensSlot),
            LaneKind.Float => Append(_floatSlotPaths, _floatTransforms, _floatDefaults, _floatCodegenSlots,
                LaneKind.Float, path, transform, fallbackDefault, lensSlot),
            LaneKind.Object => Append(_objectSlotPaths, _objectTransforms, _objectDefaults, _objectCodegenSlots,
                LaneKind.Object, path, transform, fallbackDefault, lensSlot),
            _ => SlotAddr.Fallback
        };
        if (addr.Lane != LaneKind.Fallback)
        {
            _pathToSlot[path] = addr;
            if (transform != LensTransform.None)
            {
                _hasAnyTransform = true;
            }

            if (fallbackDefault is not null)
            {
                _hasAnyDefault = true;
            }
        }

        return addr;
    }

    /// <summary>Finalises the builder into an immutable <see cref="ClassShape" />.</summary>
    internal ClassShape Build()
        => new(_className,
            _pathToSlot,
            _intSlotPaths.ToArray(),
            _floatSlotPaths.ToArray(),
            _objectSlotPaths.ToArray(),
            _hasAnyDefault ? _intDefaults.ToArray() : null,
            _hasAnyDefault ? _floatDefaults.ToArray() : null,
            _hasAnyDefault ? _objectDefaults.ToArray() : null,
            _hasAnyTransform ? _intTransforms.ToArray() : null,
            _hasAnyTransform ? _floatTransforms.ToArray() : null,
            _hasAnyTransform ? _objectTransforms.ToArray() : null);

    private SlotAddr Append(
        List<string> paths,
        List<LensTransform> transforms,
        List<object?> defaults,
        HashSet<int> codegenSlots,
        LaneKind lane,
        string path,
        LensTransform transform,
        object? fallbackDefault,
        int lensSlot)
    {
        int slot;
        if (lensSlot >= 0)
        {
            // Codegen-pinned slot. Use the exact index the codegen wrapper
            // expects regardless of the current paths.Count; grow the per-lane
            // parallel arrays with placeholder slots so the slot index is
            // addressable.
            //
            // The slot pre-pass populates codegenSlots before any Allocate
            // runs, so the auto-increment branch below skips over this index.
            // Defensive guard: if paths[lensSlot] is already non-empty when we
            // arrive here, something has gone wrong (either an auto-inc field
            // raced ahead of the pre-pass, or two LensSlotRule entries claim
            // the same (class, lane, slot)). Surface the conflict with
            // class+lane+slot+both-paths in the message.
            if (paths.Count > lensSlot && !string.IsNullOrEmpty(paths[lensSlot]))
            {
                throw new InvalidOperationException(
                    $"Schema Lens slot collision in class '{_className}' on lane {lane} at slot {lensSlot}: " +
                    $"existing path '{paths[lensSlot]}', new path '{path}'. Either two LensSlotRule entries " +
                    $"claim the same slot, or an auto-increment field grabbed it before the Lens pre-pass " +
                    $"reserved it — regenerate codegen.");
            }

            codegenSlots.Add(lensSlot);

            while (paths.Count <= lensSlot)
            {
                paths.Add(string.Empty);
                transforms.Add(LensTransform.None);
                defaults.Add(null);
            }

            slot = lensSlot;
            paths[slot] = path;
            transforms[slot] = transform;
            defaults[slot] = fallbackDefault;
        }
        else
        {
            // Auto-increment path. Skip past any Lens-reserved slot (pinned
            // either by an earlier Allocate on this builder or by the pre-pass
            // populating codegenSlots) by inserting empty placeholder entries
            // until paths.Count lands on an unreserved index. Then claim it
            // as the tail of paths (paths.Count is the next free slot).
            //
            // This preserves the "auto-inc never goes backwards from paths.Count"
            // invariant relied on by LensSlotHonoringTests, while the pre-pass
            // ensures every Lens pin is reflected in codegenSlots before any
            // auto-inc walk runs (so auto-inc never overlaps a Lens slot).
            while (codegenSlots.Contains(paths.Count))
            {
                paths.Add(string.Empty);
                transforms.Add(LensTransform.None);
                defaults.Add(null);
            }

            slot = paths.Count;
            paths.Add(path);
            transforms.Add(transform);
            defaults.Add(fallbackDefault);
        }

        return new SlotAddr(lane, slot);
    }
}
