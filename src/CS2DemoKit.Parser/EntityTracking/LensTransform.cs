namespace CS2DemoKit.Parser.EntityTracking;

/// <summary>
///     Schema Lens transform applied at the descriptor level during
///     <see cref="EntityTracker" /> bootstrap (per the V1 locked decisions; the living
///     overview is <c>docs/entity-stack.md</c>).
///     <para>
///         This enum is intentionally a local copy of the one declared in
///         <c>CS2DemoKit.Parser.Entities.SchemaLens</c>. EntityTracking sits below
///         the <c>Entities</c> project in the dependency graph (Entities →
///         EntityTracking builds on EntityTracking types like <see cref="EntityState" />),
///         so EntityTracking cannot name the Entities-side enum. The values mirror
///         it 1:1 and the <see cref="LensResolver" /> contract is the only seam
///         that crosses the boundary.
///     </para>
/// </summary>
public enum LensTransform : byte
{
    /// <summary>No transform — lane value exposed directly.</summary>
    None = 0,

    /// <summary>
    ///     Bool coercion. V1 stores the raw wire int (0 / 1) on the int lane so
    ///     <c>Fields["m_bFreezePeriod"]</c> continues to return an int. The typed
    ///     wrapper getter does the <c>!= 0</c> cast.
    /// </summary>
    BoolFromInt = 1,

    /// <summary>
    ///     Handle index. V1 keeps the raw wire integer on the lane (locked
    ///     decision). Masking + sentinel checks happen in the wrapper / in
    ///     <c>EntityTracker.ResolveHandle&lt;T&gt;</c>.
    /// </summary>
    HandleIndex = 2,

    /// <summary>Coerce decoded scalar to float. Used by <c>typeShift</c> cases.</summary>
    CastToFloat = 3,

    /// <summary>Coerce decoded scalar to int. Used by <c>typeShift</c> cases.</summary>
    CastToInt = 4,

    /// <summary>
    ///     Coerce decoded scalar to uint64 (boxed). Used by <c>typeShift</c> cases
    ///     (e.g. button bitmask promotion). Stored on the object lane.
    /// </summary>
    CastToUInt64 = 5
}

/// <summary>
///     The information a <see cref="LensResolver" /> returns when it recognises a
///     <c>(serializerName, path)</c> pair. Tells <see cref="EntityTracker" /> which
///     lane to allocate the slot in, which transform to apply to the wire value
///     before lane write, and which fallback default to seed the lane with at
///     <see cref="EntityState.BindShape" /> time for the forward-compat path.
/// </summary>
/// <param name="Lane">
///     The lane the descriptor's decoded value should land in. Must be one of
///     <see cref="LaneKind.Int" /> / <see cref="LaneKind.Float" /> /
///     <see cref="LaneKind.Object" />; <see cref="LaneKind.Fallback" /> here is
///     treated identically to a <c>null</c> rule.
/// </param>
/// <param name="Transform">
///     The transform baked into the descriptor at bootstrap. See
///     <see cref="LensTransform" /> for the per-value semantics.
/// </param>
/// <param name="FallbackDefault">
///     The value seeded into the lane slot at <see cref="EntityState.BindShape" />
///     time. <c>null</c> means "don't pre-populate" — the slot is left unseen
///     until the first wire write.
/// </param>
/// <param name="LensSlot">
///     The codegen-emitted slot index for this field, or <c>-1</c> (default) when no
///     codegen slot is supplied and the <see cref="ClassShapeBuilder" /> should
///     auto-increment (zero-behavior change for the backward-compat
///     path). When non-negative, the allocator uses this exact index — this is the
///     mechanism by which the codegen-emitted wrapper layout becomes the
///     authoritative slot layout.
/// </param>
public readonly record struct LensSlotRule(
    LaneKind Lane,
    LensTransform Transform,
    object? FallbackDefault,
    int LensSlot = -1);

/// <summary>
///     Bootstrap-time hook injected into <see cref="EntityTracker" /> by a caller
///     that can see the full Schema Lens (e.g. a test, an analysis bootstrap,
///     or a future production wire). Returns the slot rule for the given
///     <c>(serializerName, enginePath)</c>, or <c>null</c> when the path is not
///     Lens-mapped (the runtime falls through to plain <c>DecoderKind</c>
///     classification).
///     <para>
///         The resolver is called exactly once per leaf descriptor on the
///         non-array spine of a class, during the first
///         <see cref="EntityTracker" /> walk of that serializer. There is no
///         per-tick / per-packet Lens cost.
///     </para>
/// </summary>
/// <param name="serializerName">The CS2 serializer (class) name, e.g. <c>"CCSPlayerPawn"</c>.</param>
/// <param name="enginePath">
///     The dotted engine field path, e.g. <c>"m_iHealth"</c> or
///     <c>"m_pWeaponServices.m_hActiveWeapon"</c>.
/// </param>
/// <returns>The Lens rule for this path, or <c>null</c> if unmapped.</returns>
public delegate LensSlotRule? LensResolver(string serializerName, string enginePath);
