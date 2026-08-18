#region

using CS2DemoKit.Analysis.RulesetsV2.Model;

#endregion

namespace CS2DemoKit.Analysis.Yaml;

/// <summary>One rule-config load error, attributed to its source as precisely as possible.</summary>
/// <param name="FilePath">Absolute path of the offending file; <c>null</c> for in-memory YAML.</param>
/// <param name="Message">Human-readable description of what is wrong and (when known) how to fix it.</param>
/// <param name="ChainId">The ruleset the error belongs to, when identifiable (historical name).</param>
/// <param name="RuleId">The stat/rule the error belongs to, when identifiable.</param>
/// <param name="Line">1-based line number, when the YAML parser supplied one (syntax / unknown-key errors).</param>
/// <param name="Column">1-based column number, when the YAML parser supplied one.</param>
public sealed record RuleConfigError(
    string? FilePath,
    string Message,
    string? ChainId = null,
    string? RuleId = null,
    int? Line = null,
    int? Column = null)
{
    /// <summary>Formats the error as a single diagnostic line: <c>file(line,col): chain 'x', rule 'y': message</c>.</summary>
    public override string ToString()
    {
        string location = FilePath is null ? "<inline yaml>" : Path.GetFileName(FilePath);
        if (Line is not null)
        {
            location += $"({Line},{Column ?? 0})";
        }

        string scope = (ChainId, RuleId) switch
        {
            (null, null) => "",
            (not null, null) => $" chain '{ChainId}':",
            (null, not null) => $" rule '{RuleId}':",
            _ => $" chain '{ChainId}', rule '{RuleId}':"
        };

        return $"{location}:{scope} {Message}";
    }
}

/// <summary>
///     The outcome of a tolerant rule-config load: every parseable ruleset plus every error
///     encountered. A file that fails to load contributes its errors and no rulesets; files are
///     otherwise independent.
/// </summary>
/// <param name="Errors">Every error across all files, in file-name order.</param>
/// <param name="LoadedFiles">Files that contributed at least one ruleset with no errors.</param>
/// <param name="FailedFiles">Files that produced at least one error.</param>
public sealed record RuleConfigLoadResult(
    IReadOnlyList<RuleConfigError> Errors,
    IReadOnlyList<string> LoadedFiles,
    IReadOnlyList<string> FailedFiles)
{
    /// <summary>True when no file produced any error.</summary>
    public bool Success => Errors.Count == 0;

    /// <summary>
    ///     The v2 ruleset documents mapped from <c>ruleset:</c> files, in file-name order.
    ///     Empty when the directory holds no ruleset files.
    /// </summary>
    public IReadOnlyList<RulesetDoc> Rulesets { get; init; } = [];
}

/// <summary>
///     Thrown by the strict load paths (<see cref="YamlConfigLoader.LoadWithOverlay" />'s shipped
///     tier) when any rule-config error exists. Carries the full attributed error list; the message
///     enumerates all of them (not just the first) so a single failed load reports every problem at
///     once.
/// </summary>
public sealed class RuleConfigException : Exception
{
    /// <summary>Creates the exception from the collected error list.</summary>
    public RuleConfigException(IReadOnlyList<RuleConfigError> errors)
        : base(FormatMessage(errors)) =>
        Errors = errors;

    /// <summary>Every attributed error the load produced.</summary>
    public IReadOnlyList<RuleConfigError> Errors { get; }

    private static string FormatMessage(IReadOnlyList<RuleConfigError> errors) =>
        $"{errors.Count} rule-config error(s):\n  - {string.Join("\n  - ", errors)}";
}
