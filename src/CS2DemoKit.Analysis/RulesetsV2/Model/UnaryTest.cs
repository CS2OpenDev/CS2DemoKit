#region

using System.Diagnostics.CodeAnalysis;

#endregion

namespace CS2DemoKit.Analysis.RulesetsV2.Model;

/// <summary>
///     A parsed <c>match:</c> value — one of the four FEEL/ZEN-style unary-test forms.
///     The facet/field on the left is implicit (it is the map key), so a unary
///     test carries only the right-hand shape. These are structural only at the mapping stage: the
///     resolver desugars each to a comparison against the resolved facet value.
/// </summary>
public abstract record UnaryTest
{
    /// <summary>Creates a unary test at the given source position.</summary>
    /// <param name="position">The document-absolute position of the test's value node.</param>
    protected UnaryTest(SourcePosition position) => Position = position;

    /// <summary>The document-absolute position of the offending value node (diagnostics).</summary>
    public SourcePosition Position { get; }
}

/// <summary>An equality test against a bare scalar literal: <c>enemy: true</c>, <c>map: de_dust2</c>.</summary>
/// <param name="RawText">The literal's exact source text.</param>
/// <param name="Kind">The lexical category of the literal.</param>
/// <param name="Pos">The value node's position.</param>
public sealed record LiteralTest(string RawText, ScalarKind Kind, SourcePosition Pos) : UnaryTest(Pos);

/// <summary>
///     A membership test against a named list: <c>weapon: in rifles</c> (the right side is a <c>define:</c> list
///     ref).
/// </summary>
/// <param name="ListRef">The referenced list's name.</param>
/// <param name="Pos">The value node's position.</param>
public sealed record InListRefTest(string ListRef, SourcePosition Pos) : UnaryTest(Pos);

/// <summary>A membership test against an inline literal list: <c>weapon: in [ak47, m4a1]</c>.</summary>
/// <param name="Items">The list elements' exact source texts, in order.</param>
/// <param name="Pos">The value node's position.</param>
public sealed record InListLiteralTest(IReadOnlyList<string> Items, SourcePosition Pos) : UnaryTest(Pos);

/// <summary>A single comparison against a literal: <c>damage: "&gt;= 5"</c> (one operator, one literal).</summary>
/// <param name="Operator">The comparison operator.</param>
/// <param name="LiteralRawText">The right-hand literal's exact source text.</param>
/// <param name="LiteralKind">The lexical category of the literal.</param>
/// <param name="Pos">The value node's position.</param>
public sealed record ComparisonTest(
    ComparisonOperator Operator,
    string LiteralRawText,
    ScalarKind LiteralKind,
    SourcePosition Pos) : UnaryTest(Pos);

/// <summary>An inclusive integer range: <c>count: [2..5]</c> (low and high both included).</summary>
/// <param name="Low">The inclusive lower bound.</param>
/// <param name="High">The inclusive upper bound.</param>
/// <param name="Pos">The value node's position.</param>
public sealed record RangeTest(long Low, long High, SourcePosition Pos) : UnaryTest(Pos);

/// <summary>The lexical category of a unary-test scalar literal.</summary>
[SuppressMessage("Naming", "CA1720:Identifier contains type name",
    Justification = "These are the published spec §3.1 language type names (int/float/string); the vocabulary must render verbatim.")]
public enum ScalarKind
{
    /// <summary>Unset. Never produced by the parser.</summary>
    None = 0,

    /// <summary>A boolean keyword (<c>true</c> / <c>false</c>).</summary>
    Bool,

    /// <summary>An integer literal.</summary>
    Int,

    /// <summary>A float literal.</summary>
    Float,

    /// <summary>A string — a quoted string or a bare identifier treated as a wire identifier.</summary>
    String
}

/// <summary>The six comparison operators legal in a unary comparison test (spec §2).</summary>
public enum ComparisonOperator
{
    /// <summary>Unset. Never produced by the parser.</summary>
    None = 0,

    /// <summary><c>==</c>.</summary>
    Equal,

    /// <summary><c>!=</c>.</summary>
    NotEqual,

    /// <summary><c>&gt;</c>.</summary>
    Greater,

    /// <summary><c>&gt;=</c>.</summary>
    GreaterOrEqual,

    /// <summary><c>&lt;</c>.</summary>
    Less,

    /// <summary><c>&lt;=</c>.</summary>
    LessOrEqual
}
