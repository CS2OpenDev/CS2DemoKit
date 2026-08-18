#region

using CS2DemoKit.Analysis.RulesetsV2.Model;

#endregion

namespace CS2DemoKit.Analysis.RulesetsV2.Resolve;

/// <summary>
///     The <b>load-time</b> product of the resolve → canonicalize → check pipeline across the
///     load-vs-build boundary. At directory load the pipeline runs demo-less —
///     64 ticks/s, symbolic params, no source profile — purely for diagnostics (shipped-tier
///     hard-fail / user-tier containment via the shared <c>RuleConfigLoadResult</c>). The draft
///     caches the document + adapter so the <b>build-time</b> re-pass (<see cref="Build" />) can
///     re-resolve with the demo's real tick rate, the active source profile (concrete-event
///     resolution + coverage skips), and the install's param values bound to literals before the
///     planner hashes. The two runs are deliberately separate: a non-64 demo never hashes
///     64-folded constants, and load never binds an install's params.
/// </summary>
public sealed class CheckedRulesetDraft
{
    private readonly CatalogScopeAdapter _adapter;
    private readonly RulesetDoc _doc;
    private readonly RulesetExportGraph? _exports;

    private CheckedRulesetDraft(RulesetDoc doc, CatalogScopeAdapter adapter, RulesetExportGraph? exports,
        CheckedRuleset? demoless, IReadOnlyList<RulesetDiagnostic> diagnostics)
    {
        _doc = doc;
        _adapter = adapter;
        _exports = exports;
        DemolessRuleset = demoless;
        Diagnostics = diagnostics;
    }

    /// <summary>The demo-less checked ruleset produced at load (64/s, symbolic params), or null when load failed.</summary>
    public CheckedRuleset? DemolessRuleset { get; }

    /// <summary>The load-time resolution/checking diagnostics (including within-ruleset stat-reference cycles).</summary>
    public IReadOnlyList<RulesetDiagnostic> Diagnostics { get; }

    /// <summary>True when the demo-less load produced a checked ruleset with no diagnostics.</summary>
    public bool Success => DemolessRuleset is not null && Diagnostics.Count == 0;

    /// <summary>The ruleset's compiler-internal id.</summary>
    public RulesetId Id => new(_doc.Id, _doc.For);

    /// <summary>Runs the demo-less load pass over a document, producing the draft.</summary>
    /// <param name="doc">The expanded, structurally-valid document.</param>
    /// <param name="adapter">The Catalog scope-environment adapter.</param>
    /// <param name="exports">The cross-ruleset export graph, or null for single-document resolution.</param>
    /// <returns>The load-time draft.</returns>
    public static CheckedRulesetDraft Load(RulesetDoc doc, CatalogScopeAdapter adapter,
        RulesetExportGraph? exports = null)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(adapter);

        RulesetResolveResult result = ResolveWithCycleCheck(doc, adapter, ResolveContext.Draft, exports);
        return new CheckedRulesetDraft(doc, adapter, exports, result.Ruleset, result.Diagnostics);
    }

    /// <summary>
    ///     Re-resolves this draft for a specific demo at graph-build time: the demo's real tick rate,
    ///     the active source profile, and the install's param values. Coverage skips are decided here
    ///     (profile-dependent) and ride <see cref="CheckedRuleset.Coverage" />. This is the planner's
    ///     input.
    /// </summary>
    /// <param name="ticksPerSecond">The demo's <c>ParsedDemo.TickRate</c>.</param>
    /// <param name="profileId">The active demo-source profile id (e.g. <c>Cs2GotvProfile</c>).</param>
    /// <param name="paramValues">The install's param values, or null for all declared defaults.</param>
    /// <returns>The built checked ruleset IR, or build-time diagnostics.</returns>
    public RulesetResolveResult Build(double ticksPerSecond, string profileId,
        IReadOnlyDictionary<string, object?>? paramValues = null)
    {
        ArgumentNullException.ThrowIfNull(profileId);
        return ResolveWithCycleCheck(_doc, _adapter,
            ResolveContext.Build(ticksPerSecond, profileId, paramValues), _exports);
    }

    private static RulesetResolveResult ResolveWithCycleCheck(RulesetDoc doc, CatalogScopeAdapter adapter,
        ResolveContext context, RulesetExportGraph? exports)
    {
        RulesetResolveResult result = RulesetResolver.Resolve(doc, adapter, context, exports);
        if (result.Ruleset is not { } ruleset)
        {
            return result;
        }

        IReadOnlyList<RulesetDiagnostic> cycles = StatReferenceCycleDetector.Detect(ruleset);
        return cycles.Count == 0
            ? result
            : new RulesetResolveResult(null, [.. result.Diagnostics, .. cycles]);
    }
}
