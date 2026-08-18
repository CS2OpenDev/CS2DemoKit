namespace CS2DemoKit.Analysis.RulesetsV2.Model;

/// <summary>
///     One <c>highlights:</c> entry: an explicit satisfaction with a
///     <c>when:</c> expression, a rising-edge <c>per:</c> scope (default <c>round</c>), and a
///     <c>title:</c> template rendered into the Highlights view. Its automatic <c>&lt;id&gt;.count</c>
///     node is always match-scoped, produced by the planner. A highlight carrying
///     <c>for_each:</c> is multiplied by stage-1 Expand.
/// </summary>
/// <param name="Id">The highlight's id, unique within the ruleset's shared id namespace (post-expansion).</param>
/// <param name="When">The <c>when:</c> expression text; unparsed in 2.2a.</param>
/// <param name="Per">The rising-edge reset scope (default <c>round</c>).</param>
/// <param name="Title">The <c>title:</c> template with <c>{ref}</c> holes; well-formedness validated in 2.2a.</param>
/// <param name="ForEach">The <c>for_each:</c> axes, when present; <c>null</c> after expansion.</param>
/// <param name="Score">The raw authored <c>score:</c> (0–100); <c>null</c> when unspecified (resolver defaults it).</param>
/// <param name="Kind">
///     The raw authored <c>kind:</c> text (<c>highlight</c>|<c>funny</c>|<c>lowlight</c>); <c>null</c>
///     when unspecified. Kept as raw text (unvalidated in the mapper) so the enum resolution and its
///     diagnostic live in the resolver — this keeps the Yaml project free of an Abstractions dependency.
/// </param>
/// <param name="Group">
///     The raw authored <c>group:</c> supersession family (e.g. <c>multikill</c>); <c>null</c> when
///     unspecified. Highlights sharing a group collapse at the surfacing layer to the single
///     highest-scored firing per player+round, so a tiered family (3K/4K/ace) yields only its top tier.
/// </param>
/// <param name="Position">The document-absolute position of the highlight.</param>
public sealed record HighlightDef(
    string Id,
    string When,
    PerScope Per,
    string Title,
    IReadOnlyList<ForEachAxis>? ForEach,
    int? Score,
    string? Kind,
    string? Group,
    SourcePosition Position);
