#region

using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Analysis.Rules.Checking;
using CS2DemoKit.Analysis.Rules.Hashing;
using CS2DemoKit.Analysis.RulesetsV2.Model;

#endregion

namespace CS2DemoKit.Analysis.RulesetsV2.Resolve;

/// <summary>
///     One resolved-and-checked v2 highlight — the planner's input for a highlight
///     emitter plus its automatic <c>&lt;id&gt;.count</c> node. The <see cref="When" /> conjunction
///     is checked bool; its rising edge auto-produces the timeline <c>RuleChainEvent</c> in the
///     evaluator. The <c>.count</c> node is
///     <b>
///         always
///         match-scoped
///     </b>
///     (spec §6 highlight row): <see cref="CountScope" /> is <c>match</c> for a
///     match ruleset and <c>player_match</c> for an <c>each_player</c> ruleset, regardless of the
///     highlight's own <see cref="Scope" />.
/// </summary>
/// <param name="Ruleset">The owning ruleset's compiler-internal id.</param>
/// <param name="HighlightId">The highlight's id (unique in the ruleset's shared id namespace).</param>
/// <param name="Scope">The rising-edge node's compound <c>(For × Per)</c> scope axis (default per-round).</param>
/// <param name="CountScope">
///     The auto <c>.count</c> node's scope — always match-scoped (<c>match</c> or <c>player_match</c>
///     ).
/// </param>
/// <param name="When">The checked (normalized) <c>when:</c> conjunction (bool).</param>
/// <param name="Title">
///     The <c>title:</c> template — rendered at firing time by the surfacing layer
///     (<c>HighlightTitleRenderer</c>, into <c>HighlightFired.RenderedTitle</c> — A1 rich
///     emission). Outside node identity: the canonical hash preimage never reads it, so title
///     edits change no hashes.
/// </param>
/// <param name="Score">The resolved ranking weight (0–100), defaulted to 50 when <c>score:</c> was unspecified.</param>
/// <param name="Kind">The resolved editorial track, defaulted to <see cref="HighlightKind.Highlight" />.</param>
/// <param name="Group">
///     The supersession family (trimmed <c>group:</c>), or <c>null</c>. At the surfacing layer, firings
///     sharing a group collapse to the single highest-scored one per player+round — so a tiered family
///     (e.g. 3K/4K/ace) surfaces only its top tier instead of every threshold it crossed.
/// </param>
/// <param name="DeclaredReads">The statically enumerable read set of <see cref="When" /> (A1 <c>DeclaredReads</c>).</param>
/// <param name="EntityReads">The subset of reads that lower to entity-provider pre-frame reads.</param>
/// <param name="Position">The document-absolute position of the highlight.</param>
public sealed record CheckedHighlight(
    RulesetId Ruleset,
    string HighlightId,
    ScopeAxis Scope,
    ScopeAxis CountScope,
    CheckedExpression When,
    string Title,
    int Score,
    HighlightKind Kind,
    string? Group,
    IReadOnlyList<string> DeclaredReads,
    IReadOnlyList<EntityProviderReference> EntityReads,
    SourcePosition Position)
{
    /// <summary>
    ///     The stat path of the auto <c>.count</c> node (<c>&lt;id&gt;.count</c>) — the referent a
    ///     <c>show: scoreboard</c> highlight ref binds to and a key in the stat-reference cycle
    ///     graph (the cycle detector walks highlight <c>.count</c> reads).
    /// </summary>
    public string CountNodeId => $"{HighlightId}.count";
}
