#region

using CS2DemoKit.Analysis.RulesetsV2.Model;

#endregion

namespace CS2DemoKit.Analysis.RulesetsV2.Resolve;

/// <summary>
///     The product of one <see cref="RulesetResolver.Resolve" /> run: the
///     <see cref="CheckedRuleset" /> IR when resolution succeeded, plus the resolution/checking
///     diagnostics (empty on success). Diagnostics travel as data — never exceptions — and fold
///     into the shared <c>RuleConfigLoadResult</c> alongside v1 chain errors, so the
///     shipped-hard-fail / user-tier-containment behaviour applies uniformly.
///     Coverage skips are <b>not</b> errors: they ride <see cref="CheckedRuleset.Coverage" /> on a
///     successful result.
/// </summary>
/// <param name="Ruleset">The checked ruleset IR, or <c>null</c> when resolution failed.</param>
/// <param name="Diagnostics">The resolution/checking errors; empty when <see cref="Ruleset" /> is non-null.</param>
public sealed record RulesetResolveResult(CheckedRuleset? Ruleset, IReadOnlyList<RulesetDiagnostic> Diagnostics)
{
    /// <summary>True when resolution produced a checked ruleset.</summary>
    public bool Success => Ruleset is not null;
}
