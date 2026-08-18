#region

using CS2DemoKit.Analysis.RulesetsV2.Model;

#endregion

namespace CS2DemoKit.Analysis.RulesetsV2.Resolve;

/// <summary>
///     The <b>CheckedRuleset IR</b> — the planner's input. It is the
///     product of the resolve → canonicalize → check pipeline over one
///     <see cref="RulesetDoc" />: defines inlined, params bound to literals, views resolved per
///     demo-source profile, every expression slot normalized and typed. It carries resolved
///     concrete-event sets, canonical ASTs, compound scope axes, keep-specs, declared reads, and
///     entity-provider references — but <b>no hashes and no graph</b>. The planner constructs the
///     <c>StateNode</c>/<c>StateEdge</c>/<c>BuildResult</c> and runs the resolved-identity hasher;
///     this IR only feeds it.
/// </summary>
/// <param name="Id">The ruleset's compiler-internal id (its <see cref="RulesetId.JoinKey" /> keys the shared BuildResult).</param>
/// <param name="Title">The optional display title (outside node identity).</param>
/// <param name="For">The materialization scope (<c>for: match | each_player</c>).</param>
/// <param name="Stats">The checked stats, in document order.</param>
/// <param name="Highlights">The checked highlights, in document order.</param>
/// <param name="Coverage">
///     Per-profile coverage skips: nodes whose view did not bind
///     on the active profile, dropped rather than silently zeroed. Empty at demo-less load and on
///     a profile that binds every view.
/// </param>
/// <param name="Show">The raw <c>show:</c> surfacing block, carried through for the surfacing layer.</param>
public sealed record CheckedRuleset(
    RulesetId Id,
    string? Title,
    RulesetScope For,
    IReadOnlyList<CheckedStat> Stats,
    IReadOnlyList<CheckedHighlight> Highlights,
    IReadOnlyList<RulesetCoverageDiagnostic> Coverage,
    ShowDef? Show);
