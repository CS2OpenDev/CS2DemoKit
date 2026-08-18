namespace CS2DemoKit.Analysis.RulesetsV2.Model;

/// <summary>
///     A structured v2 trigger: an event/view reference (<c>on:</c>) plus
///     optional structured bindings (<c>match:</c>), an expression escape hatch (<c>where:</c>),
///     and a state gate (<c>while:</c>). Triggers appear on stats (<c>on:</c>), inside
///     trigger-bodied defines, and as <c>off:</c> deactivators. The mapper builds the structure
///     only; the resolver lowers <c>match:</c> and binds the view per demo-source
///     profile.
/// </summary>
/// <param name="On">
///     The event/view/define/raw/net reference the trigger fires on. <c>null</c> only for a
///     stat-level <c>match:</c> that refines the kind-argument trigger (e.g. <c>count: kill</c> +
///     <c>match: {enemy: true}</c>) — the resolver supplies the trigger from the kind
///     argument in that case.
/// </param>
/// <param name="Match">Structured facet bindings, in source order (empty when none).</param>
/// <param name="Actor">
///     The reserved <c>actor:</c> key, when present. Its only legal v2.0 value is <c>any</c>
///     (validated structurally); it suppresses the view's implicit per-player actor binding.
/// </param>
/// <param name="Where">Optional <c>where:</c> expression text (about the event); unparsed in 2.2a.</param>
/// <param name="While">Optional <c>while:</c> state-gate reference text; unparsed in 2.2a.</param>
/// <param name="Position">The document-absolute position of the trigger.</param>
public sealed record TriggerDef(
    TriggerRef? On,
    IReadOnlyList<MatchBinding> Match,
    string? Actor,
    string? Where,
    string? While,
    SourcePosition Position);

/// <summary>One <c>match:</c> entry — a facet/field key bound to a unary test.</summary>
/// <param name="Key">The facet or field name (the map key).</param>
/// <param name="Test">The parsed unary test the facet must satisfy.</param>
/// <param name="Position">The document-absolute position of the key.</param>
public sealed record MatchBinding(string Key, UnaryTest Test, SourcePosition Position);

/// <summary>
///     A parsed <c>on:</c> / <c>off:</c> reference. The four forms carry distinct namespace
///     semantics that the resolver dispatches on: a bare name is a view or a define (indistinguishable
///     pre-catalog), <c>raw.&lt;event&gt;</c> is a wire event with no actor convention,
///     <c>net.&lt;Message&gt;</c> is a live net-message trigger, and <c>this</c> is the reserved
///     self-reference.
/// </summary>
/// <param name="Kind">Which of the four reference forms this is.</param>
/// <param name="Name">The reference name with the sigil stripped (e.g. <c>player_death</c> for <c>raw.player_death</c>).</param>
/// <param name="Position">The document-absolute position of the reference.</param>
public sealed record TriggerRef(TriggerRefKind Kind, string Name, SourcePosition Position);

/// <summary>
///     The four <c>on:</c> reference forms (<c>this</c> / <c>net.*</c> / <c>raw.*</c> among them).
/// </summary>
public enum TriggerRefKind
{
    /// <summary>Unset. Never produced by the mapper.</summary>
    None = 0,

    /// <summary>A bare name: a curated view or a <c>define:</c> reference (resolved per catalog in 2.2b).</summary>
    ViewOrDefine,

    /// <summary><c>raw.&lt;event&gt;</c> — a wire game event with no actor convention.</summary>
    Raw,

    /// <summary><c>net.&lt;Message&gt;</c> — a live net-message trigger (payload matching reserved).</summary>
    Net,

    /// <summary>The reserved <c>this</c> self-reference.</summary>
    This
}
