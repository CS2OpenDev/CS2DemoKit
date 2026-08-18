#region

using System.Globalization;
using CS2DemoKit.Analysis.RulesetsV2.Model;

#endregion

namespace CS2DemoKit.Analysis.RulesetsV2.Resolve;

/// <summary>
///     A per-profile coverage skip: a stat/highlight whose
///     view does not bind on the demo's active source profile (the view's per-profile concrete
///     event set is empty) is dropped from the built ruleset and this diagnostic is emitted —
///     <b>never a silent zero</b>. It rides the optional <c>BuildResult</c> coverage list; the App
///     surfaces it (<c>ComputeRuleDiagnostics</c> maps it to a <c>RuleDiagnostic</c> row) and
///     <c>rules check --demo</c> prints it. Distinct from a <see cref="RulesetDiagnostic" />, which
///     is a load-time structural/resolution error: coverage skips are a legitimate build-time
///     outcome on a source that lacks the wire event.
/// </summary>
/// <param name="Ruleset">The ruleset whose node was skipped.</param>
/// <param name="NodeId">The stat/highlight id that was dropped.</param>
/// <param name="ViewName">The view that failed to bind on this profile.</param>
/// <param name="ProfileId">The active demo-source profile the view was unavailable on.</param>
/// <param name="Message">Human-readable explanation naming the node, view, and profile.</param>
/// <param name="Position">The document-absolute position of the skipped node.</param>
public sealed record RulesetCoverageDiagnostic(
    RulesetId Ruleset,
    string NodeId,
    string ViewName,
    string ProfileId,
    string Message,
    SourcePosition Position)
{
    /// <summary>Formats as <c>file(line,col): message</c> for logs and test output.</summary>
    /// <returns>The formatted diagnostic line.</returns>
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Position}: {Message}");
}
