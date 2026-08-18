#region

using System.Globalization;

#endregion

namespace CS2DemoKit.Analysis.Rules;

/// <summary>Severity of a <see cref="Diagnostic" />. The v2 semantic core currently emits errors only.</summary>
public enum DiagnosticSeverity
{
    /// <summary>Unset. Never produced by the semantic core.</summary>
    None = 0,

    /// <summary>The input is invalid; no value is produced.</summary>
    Error,

    /// <summary>The input is legal but suspicious. Reserved for future lints.</summary>
    Warning
}

/// <summary>
///     One user-facing problem found while lexing, parsing, normalizing, resolving, or
///     checking an expression. Diagnostics — never exceptions — are how the semantic core
///     reports user-input errors (spec §8): each carries the in-expression position
///     (line, column, offset, length), the offending source text, and a message that names
///     what was written and what was expected, using language-level type names
///     (<c>duration</c>, <c>instant</c>), never CLR names. Exceptions are reserved for
///     programmer misuse of the API.
/// </summary>
public sealed record Diagnostic
{
    /// <summary>
    ///     Creates a diagnostic.
    /// </summary>
    /// <param name="code">Stable machine-readable code, one of <see cref="DiagnosticCodes" />.</param>
    /// <param name="message">Human-readable message per the spec §8 contract.</param>
    /// <param name="span">Position of the offending text within the expression source.</param>
    /// <param name="offendingText">The exact source text the diagnostic is about (may be empty at end-of-input).</param>
    /// <param name="didYouMean">Ranked near-miss candidates (Levenshtein distance ≤ 2), empty when not applicable.</param>
    public Diagnostic(string code, string message, SourceSpan span, string offendingText,
        IReadOnlyList<string>? didYouMean = null)
    {
        ArgumentNullException.ThrowIfNull(code);
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(offendingText);
        Code = code;
        Message = message;
        Span = span;
        OffendingText = offendingText;
        DidYouMean = didYouMean ?? [];
    }

    /// <summary>Stable machine-readable code, one of <see cref="DiagnosticCodes" />.</summary>
    public string Code { get; }

    /// <summary>Human-readable message naming what was written and what was expected.</summary>
    public string Message { get; }

    /// <summary>Position of the offending text within the expression source string.</summary>
    public SourceSpan Span { get; }

    /// <summary>The exact source text the diagnostic is about (empty at end-of-input).</summary>
    public string OffendingText { get; }

    /// <summary>Ranked near-miss candidates for resolution errors; empty when not applicable.</summary>
    public IReadOnlyList<string> DidYouMean { get; }

    /// <summary>Severity. Always <see cref="DiagnosticSeverity.Error" /> in the current core.</summary>
    public DiagnosticSeverity Severity { get; init; } = DiagnosticSeverity.Error;

    /// <summary>Formats as <c>(line,col): message</c> for logs and test failure output.</summary>
    /// <returns>The formatted diagnostic line.</returns>
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture,
            $"({Span.Line},{Span.Column}): {Message} [{Code}]");
}

/// <summary>
///     The stable diagnostic codes emitted by the semantic core, grouped by pipeline stage.
///     Codes are part of the public contract: tools may key behavior on them, so renames are
///     breaking changes.
/// </summary>
public static class DiagnosticCodes
{
    /// <summary>An input character no token can start with (the §1 hard-EOF rule; v1 silently skipped these).</summary>
    public const string UnknownCharacter = "lex.unknown-character";

    /// <summary>A string literal with no closing quote before end-of-line or end-of-input.</summary>
    public const string UnterminatedString = "lex.unterminated-string";

    /// <summary>A string escape other than <c>\"</c>, <c>\\</c>, <c>\n</c>, <c>\t</c>.</summary>
    public const string InvalidEscape = "lex.invalid-escape";

    /// <summary>A number carrying a suffix other than the duration suffixes <c>s</c> / <c>ms</c>.</summary>
    public const string InvalidNumberSuffix = "lex.invalid-number-suffix";

    /// <summary>A numeric literal that does not fit the language's 64-bit integer / IEEE double range.</summary>
    public const string InvalidNumber = "lex.invalid-number";

    /// <summary>The parser expected one construct and found another.</summary>
    public const string UnexpectedToken = "parse.unexpected-token";

    /// <summary>Chained comparison (<c>a &lt; b &lt; c</c>) — one comparison per level (spec §2).</summary>
    public const string ChainedComparison = "parse.chained-comparison";

    /// <summary>Tokens remain after a complete expression (the §1 hard-EOF rule; v1 silently truncated).</summary>
    public const string TrailingTokens = "parse.trailing-tokens";

    /// <summary>A call to a name outside the closed five-function set (spec §2).</summary>
    public const string UnknownFunction = "parse.unknown-function";

    /// <summary>A call with the wrong number of arguments for its function.</summary>
    public const string WrongArity = "parse.wrong-arity";

    /// <summary>A list literal mixing element categories (numbers, strings, bools).</summary>
    public const string MixedListLiteral = "parse.mixed-list-literal";

    /// <summary>A map-valued <c>define:</c> mixing value categories (values must be all numbers or all strings).</summary>
    public const string MixedMapLiteral = "check.mixed-map-literal";

    /// <summary>A token that is not a scalar literal inside a list literal (e.g. <c>null</c>, a reference).</summary>
    public const string InvalidListElement = "parse.invalid-list-element";

    /// <summary>The right side of <c>in</c> is neither a reference nor a list literal.</summary>
    public const string InvalidInOperand = "parse.invalid-in-operand";

    /// <summary>A <c>define:</c> chain that reaches itself while inlining (spec §6 cycle rule analogue).</summary>
    public const string DefineCycle = "normalize.define-cycle";

    /// <summary>Member access on a define whose body is not itself a reference.</summary>
    public const string DefineMemberAccess = "normalize.define-member-access";

    /// <summary>A <c>match:</c> key the binding-lowering hook does not recognize.</summary>
    public const string UnknownMatchKey = "normalize.unknown-match-key";

    /// <summary>A reference head that is not a root of the slot's scope environment (spec §4).</summary>
    public const string UnknownRoot = "resolve.unknown-root";

    /// <summary>A member segment that does not exist under the resolved prefix.</summary>
    public const string UnknownMember = "resolve.unknown-member";

    /// <summary>A reference that resolves to a namespace, not a readable value.</summary>
    public const string NotAValue = "resolve.not-a-value";

    /// <summary>Operand or argument types that the operator/function does not accept (spec §3.2).</summary>
    public const string TypeMismatch = "check.type-mismatch";

    /// <summary>A whole list value used where only <c>.count</c>, <c>[n]</c>, or <c>.set</c> are legal (spec §3.4).</summary>
    public const string ListOperand = "check.list-operand";

    /// <summary>The <c>null</c> literal used outside an <c>==</c> / <c>!=</c> presence test (spec §3.3).</summary>
    public const string NullUsage = "check.null-usage";

    /// <summary>An index expression of the wrong type (list wants <c>int</c>, map wants <c>string</c>).</summary>
    public const string IndexType = "check.index-type";

    /// <summary>Indexing a value that is neither a list nor a map.</summary>
    public const string NotIndexable = "check.not-indexable";

    /// <summary><c>.set</c> on something that is neither a capture stat nor a list stat (spec §3.5).</summary>
    public const string SetNotSupported = "check.set-not-supported";

    /// <summary>The whole expression's type does not fit what its slot requires.</summary>
    public const string ExpectedType = "check.expected-type";
}
