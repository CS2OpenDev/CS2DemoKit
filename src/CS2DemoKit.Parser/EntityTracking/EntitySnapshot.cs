namespace CS2DemoKit.Parser.EntityTracking;

/// <summary>
///     A fully-frozen, value-derived snapshot of one networked entity, produced by the
///     <see cref="EntityTracker.Snapshot{T}" /> / <see cref="EntityTracker.SnapshotNode" />
///     paths. Unlike a live wrapper, an <see cref="EntitySnapshot" /> holds <b>no</b> live
///     <see cref="EntityState" /> or <see cref="EntityTracker" /> reference — every value
///     is captured at freeze time — so it is safe to carry across threads, store in
///     records, or queue on <c>await</c> boundaries.
///     <para>
///         This is the generic node form: it carries the entity's <see cref="ClassName" />
///         verbatim (so consumers that classify by class — e.g. utility-count-by-weapon —
///         keep working off a frozen node without re-reading the live entity table) plus
///         the flat <see cref="Fields" /> projection.
///     </para>
///     <para>
///         Historical note: until the SDK cutover this node also carried a <c>Nested</c>
///         map of recursively-frozen handle targets, populated by the local generated
///         wrappers' <c>SnapshotInto</c> overrides. That layer is retired (SDK wrappers
///         resolve handles live through <c>IEntityWorld</c> instead), so the nested-freeze
///         machinery was removed with it rather than kept as permanently-empty API.
///     </para>
/// </summary>
public sealed class EntitySnapshot
{
    internal EntitySnapshot(
        string className,
        int serial,
        IReadOnlyDictionary<string, object?> fields)
    {
        ClassName = className;
        Serial = serial;
        Fields = fields;
    }

    /// <summary>The runtime entity class name (e.g. "CCSPlayerPawn") captured at freeze time.</summary>
    public string ClassName { get; }

    /// <summary>The entity serial number captured at freeze time.</summary>
    public int Serial { get; }

    /// <summary>
    ///     The flat field projection (path → boxed value) captured at freeze time — a
    ///     frozen clone of the live <see cref="EntityState.Fields" /> projection (R5: handle
    ///     fields appear here as their raw boxed wire int, exactly as on the live path).
    /// </summary>
    public IReadOnlyDictionary<string, object?> Fields { get; }
}
