namespace CS2DemoKit.Analysis.Rules.Lexing;

/// <summary>
///     Token kinds of the spec §1 lexical grammar. Word-form operators lex to the same kinds
///     as their symbolic forms (<c>and</c> ≡ <c>&amp;&amp;</c> → <see cref="And" />), so the
///     spelling difference vanishes before parsing (spec §5 row 1).
/// </summary>
public enum TokenKind
{
    /// <summary>Unset. Never produced by the lexer.</summary>
    None = 0,

    /// <summary>An identifier: reference head or member segment.</summary>
    Identifier,

    /// <summary>An integer literal (64-bit signed).</summary>
    IntLiteral,

    /// <summary>A float literal (<c>digits.digits</c>).</summary>
    FloatLiteral,

    /// <summary>A duration literal: number immediately followed by <c>s</c> or <c>ms</c> (spec §1).</summary>
    DurationLiteral,

    /// <summary>A double-quoted string literal with <c>\"</c> <c>\\</c> <c>\n</c> <c>\t</c> escapes.</summary>
    StringLiteral,

    /// <summary>The reserved literal keyword <c>true</c>.</summary>
    True,

    /// <summary>The reserved literal keyword <c>false</c>.</summary>
    False,

    /// <summary>The reserved literal keyword <c>null</c>.</summary>
    Null,

    /// <summary>Logical and: <c>&amp;&amp;</c> or the reserved word <c>and</c>.</summary>
    And,

    /// <summary>Logical or: <c>||</c> or the reserved word <c>or</c>.</summary>
    Or,

    /// <summary>Logical not: <c>!</c> or the reserved word <c>not</c>.</summary>
    Not,

    /// <summary>The membership operator, reserved word <c>in</c> (no symbolic form).</summary>
    In,

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
    LessOrEqual,

    /// <summary><c>+</c>.</summary>
    Plus,

    /// <summary><c>-</c>.</summary>
    Minus,

    /// <summary><c>*</c>.</summary>
    Star,

    /// <summary><c>/</c>.</summary>
    Slash,

    /// <summary><c>%</c>.</summary>
    Percent,

    /// <summary><c>(</c>.</summary>
    LeftParen,

    /// <summary><c>)</c>.</summary>
    RightParen,

    /// <summary><c>[</c>.</summary>
    LeftBracket,

    /// <summary><c>]</c>.</summary>
    RightBracket,

    /// <summary><c>,</c>.</summary>
    Comma,

    /// <summary><c>.</c> (member access).</summary>
    Dot,

    /// <summary>Synthetic end-of-input marker; always the last token of a successful lex.</summary>
    EndOfInput
}

/// <summary>The unit a duration literal was written in.</summary>
public enum DurationUnit
{
    /// <summary>Not a duration token.</summary>
    None = 0,

    /// <summary>The <c>s</c> suffix.</summary>
    Seconds,

    /// <summary>The <c>ms</c> suffix.</summary>
    Milliseconds
}
