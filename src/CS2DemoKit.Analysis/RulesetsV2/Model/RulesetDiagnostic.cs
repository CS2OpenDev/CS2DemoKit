#region

using System.Globalization;

#endregion

namespace CS2DemoKit.Analysis.RulesetsV2.Model;

/// <summary>
///     One user-facing problem found while mapping, expanding, or structurally validating a v2
///     ruleset document. Like the semantic core's <c>Diagnostic</c>, these travel as
///     data — never as exceptions — and each names what was written and where
///     (<see cref="Position" />). The v2 loader folds them into the shared
///     <c>RuleConfigLoadResult</c> alongside v1 chain errors so the shipped-hard-fail /
///     user-tier-containment behaviour applies uniformly.
/// </summary>
/// <param name="Code">Stable machine-readable code, one of <see cref="RulesetDiagnosticCodes" />.</param>
/// <param name="Message">Human-readable message naming what was written and what was expected.</param>
/// <param name="Position">The document-absolute position of the offending element.</param>
public sealed record RulesetDiagnostic(string Code, string Message, SourcePosition Position)
{
    /// <summary>Formats as <c>file(line,col): message [code]</c> for logs and test failure output.</summary>
    /// <returns>The formatted diagnostic line.</returns>
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Position}: {Message} [{Code}]");
}

/// <summary>
///     The stable diagnostic codes emitted by the document mapper, <c>for_each:</c>
///     expander, and structural validator. Codes are part of the contract: tools may key on them,
///     so renames are breaking changes.
/// </summary>
public static class RulesetDiagnosticCodes
{
    /// <summary>A top-level or nested key the v2 schema does not recognise.</summary>
    public const string UnknownKey = "ruleset.unknown-key";

    /// <summary>A YAML node whose shape is wrong for its slot (scalar where a map was expected, etc.).</summary>
    public const string WrongShape = "ruleset.wrong-shape";

    /// <summary>A required key is missing (a ruleset with no <c>ruleset:</c> id, a stat with no kind, …).</summary>
    public const string Missing = "ruleset.missing";

    /// <summary>
    ///     An enum-valued key carrying a string outside its closed set (<c>for:</c>, <c>per:</c>, <c>keep:</c>, param
    ///     <c>type:</c>).
    /// </summary>
    public const string BadEnum = "ruleset.bad-enum";

    /// <summary>A stat carrying zero or more than one kind discriminator, or a kind outside the eight.</summary>
    public const string BadKind = "ruleset.bad-kind";

    /// <summary><c>keep:</c> present on a stat whose kind is not <c>capture:</c> (spec §1.3).</summary>
    public const string KeepNotOnCapture = "ruleset.keep-not-on-capture";

    /// <summary>A duplicate id in the shared stat/highlight/param/define namespace (checked post-expansion).</summary>
    public const string DuplicateId = "ruleset.duplicate-id";

    /// <summary>An <c>exports:</c> id that is not a declared stat or highlight (advisory lint).</summary>
    public const string UnknownExport = "ruleset.unknown-export";

    /// <summary>A <c>param:</c> whose default, min, or max is inconsistent with its declared type or with each other.</summary>
    public const string ParamRange = "ruleset.param-range";

    /// <summary>A <c>title:</c> template with unbalanced or empty <c>{}</c> holes.</summary>
    public const string BadTitleTemplate = "ruleset.bad-title-template";

    /// <summary>A <c>match:</c> value that is not one of the four unary-test forms (literal / in-list / comparison / range).</summary>
    public const string BadUnaryTest = "ruleset.bad-unary-test";

    /// <summary>The reserved <c>actor:</c> key carrying anything other than the keyword <c>any</c>.</summary>
    public const string BadActor = "ruleset.bad-actor";

    /// <summary>A <c>for_each:</c> axis with no values, or a malformed axis map.</summary>
    public const string BadForEach = "ruleset.bad-for-each";

    /// <summary>
    ///     A kind-specific structural argument is missing or on the wrong kind: a <c>tally:</c> with no
    ///     <c>thresholds:</c> (or no source), a <c>thresholds:</c> on a non-tally, a <c>bucket:</c> with
    ///     no <c>key:</c>, or a <c>streak:</c>/<c>bucket:</c> arg on a kind that does not take it.
    /// </summary>
    public const string BadKindArgs = "ruleset.bad-kind-args";

    /// <summary>
    ///     A map-valued <c>define:</c> whose values are not uniform — all values must be numbers or all strings (spec
    ///     §3.4).
    /// </summary>
    public const string MixedMapDefine = "ruleset.mixed-map-define";
}
