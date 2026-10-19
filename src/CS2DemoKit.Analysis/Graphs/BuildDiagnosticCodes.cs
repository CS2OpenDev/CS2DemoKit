namespace CS2DemoKit.Analysis.Graphs;

/// <summary>
///     Stable diagnostic codes <see cref="Building.RuleChainBuilder.Build" /> reports on
///     <see cref="BuildResult.ExcludedRulesets" />, for what only lowering can detect. Codes are part
///     of the contract: tools may key on them, so renames are breaking changes.
/// </summary>
public static class BuildDiagnosticCodes
{
    /// <summary>The ruleset resolved and type-checked but its stats could not be lowered to graph edges.</summary>
    public const string LoweringFailed = "build.lowering-failed";
}
