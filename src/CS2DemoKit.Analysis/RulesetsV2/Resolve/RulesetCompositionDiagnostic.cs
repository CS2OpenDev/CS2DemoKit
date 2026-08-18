#region

using System.Globalization;
using CS2DemoKit.Analysis.Rules;
using CS2DemoKit.Analysis.RulesetsV2.Model;

#endregion

namespace CS2DemoKit.Analysis.RulesetsV2.Resolve;

/// <summary>
///     One <see cref="RulesetComposition" /> diagnostic, attributed to the ruleset that produced it.
///     The attributed form is what <c>BuildResult.RulesetDiagnostics</c> and
///     <c>DemoAnalysis.ValidateRulesets</c> surface: a bare <see cref="RulesetDiagnostic" /> carries
///     only <c>file(line,col)</c>, which is enough for a CLI but not for a service that stores rules
///     per ruleset id and has to answer "which of the uploaded rulesets is broken".
///     <para>
///         This is a lossless 1:1 wrapper, in composition order, over
///         <c>RulesetComposition.Result.Diagnostics</c> — same count, same order, same
///         <see cref="ToString" /> rendering — so a consumer can swap one for the other without
///         changing what it reports.
///     </para>
/// </summary>
/// <param name="RulesetId">
///     The ruleset the diagnostic belongs to, or <c>null</c> for a directory-level diagnostic that
///     spans rulesets (a cross-ruleset cycle) — those name the participating qualified
///     <c>{ruleset}.{stat}</c> ids in <paramref name="Message" /> instead.
/// </param>
/// <param name="Severity">
///     Always <see cref="DiagnosticSeverity.Error" /> today — composition emits no warnings. Carried
///     so a future lint tier is additive rather than a breaking shape change.
/// </param>
/// <param name="Code">The stable machine-readable code (<see cref="ResolveDiagnosticCodes" /> / <see cref="RulesetDiagnosticCodes" />).</param>
/// <param name="Message">Human-readable message naming what was written and what was expected.</param>
/// <param name="Position">The document-absolute position, when the diagnostic has one.</param>
public sealed record RulesetCompositionDiagnostic(
    string? RulesetId,
    DiagnosticSeverity Severity,
    string Code,
    string Message,
    SourcePosition Position)
{
    /// <summary>
    ///     Wraps a raw composition diagnostic, attributing it to <paramref name="rulesetId" />.
    /// </summary>
    /// <param name="diagnostic">The diagnostic the composition pipeline produced.</param>
    /// <param name="rulesetId">The owning ruleset id, or <c>null</c> when it spans rulesets.</param>
    /// <returns>The attributed diagnostic.</returns>
    public static RulesetCompositionDiagnostic From(RulesetDiagnostic diagnostic, string? rulesetId)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);
        return new RulesetCompositionDiagnostic(rulesetId, DiagnosticSeverity.Error,
            diagnostic.Code, diagnostic.Message, diagnostic.Position);
    }

    /// <summary>
    ///     Formats as <c>file(line,col): message [code]</c> — byte-identical to
    ///     <see cref="RulesetDiagnostic.ToString" />, which <c>rules check</c>'s CLI output depends on.
    /// </summary>
    /// <returns>The formatted diagnostic line.</returns>
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Position}: {Message} [{Code}]");
}

/// <summary>
///     A ruleset the composition dropped, and every diagnostic that explains why. Composition is
///     tolerant — a document that fails cross-reference validation, resolution, or the cycle
///     pre-pass is excluded while its siblings still build — so without this list the drop is
///     invisible to the caller ("silently-missing feats": a leaderboard whose rule stopped scoring
///     because its ruleset stopped composing, with nothing in the result saying so).
/// </summary>
/// <param name="Id">The excluded ruleset's <c>ruleset:</c> id.</param>
/// <param name="SourceFile">
///     The document's source file, when it had one (<c>null</c> for in-memory / database-sourced
///     documents loaded through <c>YamlConfigLoader.LoadDocuments</c> with a bare label).
/// </param>
/// <param name="Diagnostics">Why it was excluded, in composition order; never empty.</param>
public sealed record ExcludedRuleset(
    string Id,
    string? SourceFile,
    IReadOnlyList<RulesetCompositionDiagnostic> Diagnostics);
