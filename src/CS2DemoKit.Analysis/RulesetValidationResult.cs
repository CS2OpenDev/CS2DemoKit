#region

using CS2DemoKit.Analysis.RulesetsV2.Resolve;
using CS2DemoKit.Analysis.Yaml;

#endregion

namespace CS2DemoKit.Analysis;

/// <summary>
///     The product of <see cref="DemoAnalysis.ValidateRulesets(IReadOnlyList{RulesetsV2.Model.RulesetDoc})" />:
///     everything wrong with a set of ruleset documents, as data, with no demo involved.
///     <para>
///         Shaped for an upload-validation endpoint — a service that stores user-authored rules
///         needs to answer "can I save this?" and, when not, "which ruleset, where, and why" in a
///         form it can serialize. Both diagnostic lists carry stable machine-readable codes, so a
///         client can key behaviour on them rather than on message text.
///     </para>
///     <para>
///         Load and composition failures stay in separate lists on purpose: a
///         <see cref="RuleConfigError" /> from the YAML tier has no diagnostic code (a syntax error
///         has no ruleset yet), so folding the two would lose information the endpoint wants.
///     </para>
/// </summary>
/// <param name="LoadErrors">
///     Errors from parsing the YAML into documents — syntax errors, non-<c>ruleset:</c> files, the
///     retired v1 format, duplicate ids. Always empty for the overload that takes already-loaded
///     documents.
/// </param>
/// <param name="Diagnostics">
///     Every composition diagnostic — cross-ruleset reference errors, identifier resolution
///     failures, type errors, within- and cross-ruleset cycles — attributed to its ruleset, in
///     composition order.
/// </param>
/// <param name="Excluded">
///     The rulesets that failed to compose, each with the diagnostics explaining why. A subset view
///     of <paramref name="Diagnostics" />, grouped for a per-ruleset "this one cannot be saved"
///     verdict.
/// </param>
/// <param name="ValidatedRulesetIds">
///     The ids that composed cleanly, in dependency order (used-before-user). On a partial failure
///     these are the rulesets that WOULD build.
/// </param>
public sealed record RulesetValidationResult(
    IReadOnlyList<RuleConfigError> LoadErrors,
    IReadOnlyList<RulesetCompositionDiagnostic> Diagnostics,
    IReadOnlyList<ExcludedRuleset> Excluded,
    IReadOnlyList<string> ValidatedRulesetIds)
{
    /// <summary>True when nothing failed to load and nothing failed to compose.</summary>
    public bool Success => LoadErrors.Count == 0 && Diagnostics.Count == 0;
}
