namespace CS2DemoKit.Parser.Entities.SchemaLens;

/// <summary>
///     The wire type of a field as declared in the Schema Lens migration file.
///     Determines which lane (int / float / object) a field slot is allocated in.
/// </summary>
public enum WireType
{
    /// <summary>Integer wire type — stored in the int lane. Corresponds to <c>"int"</c> in migration JSON.</summary>
    IntLane,

    /// <summary>Float wire type — stored in the float lane. Corresponds to <c>"float"</c> in migration JSON.</summary>
    FloatLane,

    /// <summary>
    ///     Object wire type — stored in the object lane (arrays, strings, structs). Corresponds to <c>"object"</c> in
    ///     migration JSON.
    /// </summary>
    ObjectLane
}

/// <summary>
///     A transform applied to a field's value at the typed-wrapper getter layer.
///     V1 decision (R5): transforms are declared on the Lens entry but only the typed-wrapper
///     getter applies them. The raw wire value is stored unchanged in the lane.
/// </summary>
public enum LensTransform
{
    /// <summary>No transform. The lane value is exposed directly.</summary>
    None,

    /// <summary>
    ///     Handle field: the raw wire integer stays in the lane — no <c>&amp; 0x3FFF</c>, no
    ///     sentinel interpretation (masking and sentinel policy belong to the runtime's
    ///     handle resolution). Load-bearing in two places: the conformance binding builder
    ///     marks handle ordinals with it, and the decoder's lane-drift gate exempts it.
    /// </summary>
    HandleIndex
}

/// <summary>
///     Describes how a single canonical engine field is exposed by the Schema Lens:
///     which storage lane it occupies and (for handles) the <see cref="LensTransform" />
///     marker. Lanes are HONEST — they state the lane the decoder's honour-the-wire
///     routing actually uses, so there is no downstream "effective lane" correction.
///     (The retired migration-era members — targetProperty, fallbackDefault,
///     firstSeenBuild — fed the deleted local wrapper codegen and dead pre-population
///     plumbing; nullability truth lives in seen bits, not rule defaults.)
/// </summary>
/// <param name="WireType">The lane the field's value occupies at runtime.</param>
/// <param name="Transform">Handle marker; see <see cref="LensTransform.HandleIndex" />.</param>
/// <param name="LensSlot">
///     The codegen-emitted slot index for this canonical field on its declared lane,
///     or <c>-1</c> when no slot has been assigned. Deterministic (R1): within a
///     (class, lane), slots are 0..N-1 in ordinal order of the canonical field name.
///     Excluded from the canonical-form hash — a derived function of the rest of the
///     state, not an independent fact.
/// </param>
public sealed record FieldRule(
    WireType WireType,
    LensTransform Transform,
    int LensSlot = -1);

/// <summary>
///     The lane-binding lens state, derived at codegen time from the pinned
///     <c>CS2OpenDev.Sdk.Entities</c> package and reconstructed at runtime by
///     <c>GeneratedLensRegistry.Load()</c>. Built once per process at <c>EntityTracker</c>
///     construction time.
/// </summary>
/// <remarks>
///     <para>
///         <see cref="AliasMap" /><c>[className][engineFieldName]</c> resolves to the canonical
///         engine field name. The replay flattens alias chains so resolution is always one
///         dictionary lookup, never a loop.
///     </para>
///     <para>
///         <see cref="Fields" /><c>[className][canonicalFieldName]</c> contains the
///         <see cref="FieldRule" /> for that field, including its lane kind, transform, and default.
///     </para>
///     <para>
///         <see cref="Classes" /> tracks which class names are active (not removed). Removed
///         classes are dropped from the set; their alias entries are retained for older-demo
///         back-compat.
///     </para>
///     <para>
///         <see cref="CanonicalHash" /> is the sha256 of the canonical-form serialization
///         (see <see cref="SchemaLensCanonicalForm" />). Stamped by the codegen deriver;
///         the test suite recomputes it from the generated registry to catch emit drift.
///     </para>
/// </remarks>
public sealed class LensState
{
    /// <summary>
    ///     Per-class alias map: <c>engine_name → canonical_name</c>.
    ///     Includes both the canonical name itself and any historical aliases, so a
    ///     single lookup resolves both current and renamed fields.
    ///     Outer key: CS2 serializer class name (e.g. <c>"CCSPlayerPawn"</c>).
    ///     Inner key: any engine field name ever associated with this canonical field.
    /// </summary>
    public Dictionary<string, Dictionary<string, string>> AliasMap { get; } = new();

    /// <summary>
    ///     Per-class field rules: <c>canonical_name → FieldRule</c>.
    ///     Only contains currently-active fields (removed fields are dropped on <c>removeField</c>).
    ///     Outer key: CS2 serializer class name.
    ///     Inner key: canonical engine field name.
    /// </summary>
    public Dictionary<string, Dictionary<string, FieldRule>> Fields { get; } = new();

    /// <summary>
    ///     The set of class names that are currently active (i.e., have been added via
    ///     <c>addClass</c> and not subsequently removed via <c>removeClass</c>).
    /// </summary>
    public HashSet<string> Classes { get; } = new();

    /// <summary>
    ///     The sha256 of the canonical-form serialization of this <see cref="LensState" />,
    ///     stamped by the codegen deriver and embedded as <c>GeneratedLensRegistry.LensHash</c>.
    /// </summary>
    public string CanonicalHash { get; internal set; } = string.Empty;
}
