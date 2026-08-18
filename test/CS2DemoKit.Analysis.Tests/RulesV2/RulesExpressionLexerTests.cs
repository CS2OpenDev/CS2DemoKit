#region

using CS2DemoKit.Analysis.Rules;
using CS2DemoKit.Analysis.Rules.Lexing;

#endregion

namespace CS2DemoKit.Analysis.Tests.RulesV2;

/// <summary>
///     Semantic-core lexer battery for the v2 expression language, pinned to the
///     spec §1 lexical grammar: every token kind, duration literals with suffix adjacency,
///     string escapes, and the hard EOF rule (unknown characters error with their column —
///     the v1 tokenizer silently skipped them). Pure in-memory; no demo.
/// </summary>
[Category("Unit")]
public class RulesExpressionLexerTests
{
    private static IReadOnlyList<Token> Lex(string source)
    {
        LanguageResult<IReadOnlyList<Token>> result = ExpressionLexer.Tokenize(source);
        return result.Require();
    }

    private static Diagnostic FirstError(string source)
    {
        LanguageResult<IReadOnlyList<Token>> result = ExpressionLexer.Tokenize(source);
        return result.Diagnostics[0];
    }

    // ── Every token kind ─────────────────────────────────────────────────────────

    /// <summary>One source string covering every producible token kind, in order.</summary>
    [Test]
    public async Task Tokenize_KitchenSink_ProducesEveryTokenKind()
    {
        IReadOnlyList<Token> tokens = Lex(
            "name 12 3.5 10s 500ms \"hi\" true false null and or not in == != > >= < <= + - * / % ( ) [ ] , .");

        TokenKind[] kinds = tokens.Select(t => t.Kind).ToArray();
        TokenKind[] expected =
        [
            TokenKind.Identifier, TokenKind.IntLiteral, TokenKind.FloatLiteral, TokenKind.DurationLiteral,
            TokenKind.DurationLiteral, TokenKind.StringLiteral, TokenKind.True, TokenKind.False, TokenKind.Null,
            TokenKind.And, TokenKind.Or, TokenKind.Not, TokenKind.In,
            TokenKind.Equal, TokenKind.NotEqual, TokenKind.Greater, TokenKind.GreaterOrEqual,
            TokenKind.Less, TokenKind.LessOrEqual,
            TokenKind.Plus, TokenKind.Minus, TokenKind.Star, TokenKind.Slash, TokenKind.Percent,
            TokenKind.LeftParen, TokenKind.RightParen, TokenKind.LeftBracket, TokenKind.RightBracket,
            TokenKind.Comma, TokenKind.Dot, TokenKind.EndOfInput
        ];

        await Assert.That(kinds).IsEquivalentTo(expected);
    }

    /// <summary>Symbolic and word forms lex to the same operator kinds (spec §1 word-form rule).</summary>
    [Test]
    public async Task Tokenize_SymbolicForms_MatchWordForms()
    {
        IReadOnlyList<Token> symbolic = Lex("a && b || ! c");
        IReadOnlyList<Token> words = Lex("a and b or not c");

        await Assert.That(symbolic.Select(t => t.Kind).ToArray())
            .IsEquivalentTo(words.Select(t => t.Kind).ToArray());
    }

    /// <summary>Literal token values are parsed: int, float, and spans are exact.</summary>
    [Test]
    public async Task Tokenize_SimpleComparison_TokenValuesAndSpans()
    {
        IReadOnlyList<Token> tokens = Lex("kills > 42");

        await Assert.That(tokens[0].Text).IsEqualTo("kills");
        await Assert.That(tokens[0].Span).IsEqualTo(new SourceSpan(0, 5, 1, 1));
        await Assert.That(tokens[1].Kind).IsEqualTo(TokenKind.Greater);
        await Assert.That(tokens[1].Span).IsEqualTo(new SourceSpan(6, 1, 1, 7));
        await Assert.That(tokens[2].IntegerValue).IsEqualTo(42L);
        await Assert.That(tokens[2].Span).IsEqualTo(new SourceSpan(8, 2, 1, 9));
    }

    /// <summary>Expressions are YAML scalars and may span lines; line/column track newlines.</summary>
    [Test]
    public async Task Tokenize_MultiLine_TracksLineAndColumn()
    {
        IReadOnlyList<Token> tokens = Lex("a >\n  1");

        await Assert.That(tokens[2].Kind).IsEqualTo(TokenKind.IntLiteral);
        await Assert.That(tokens[2].Span.Line).IsEqualTo(2);
        await Assert.That(tokens[2].Span.Column).IsEqualTo(3);
        await Assert.That(tokens[2].Span.Offset).IsEqualTo(6);
    }

    // ── Duration literals ────────────────────────────────────────────────────────

    /// <summary>All three spec §1 duration forms carry magnitude and unit.</summary>
    [Test]
    public async Task Tokenize_DurationLiterals_MagnitudeAndUnit()
    {
        IReadOnlyList<Token> tokens = Lex("10s 0.5s 500ms");

        await Assert.That(tokens[0].Kind).IsEqualTo(TokenKind.DurationLiteral);
        await Assert.That(tokens[0].DurationMagnitude).IsEqualTo(10.0);
        await Assert.That(tokens[0].Unit).IsEqualTo(DurationUnit.Seconds);

        await Assert.That(tokens[1].DurationMagnitude).IsEqualTo(0.5);
        await Assert.That(tokens[1].Unit).IsEqualTo(DurationUnit.Seconds);

        await Assert.That(tokens[2].DurationMagnitude).IsEqualTo(500.0);
        await Assert.That(tokens[2].Unit).IsEqualTo(DurationUnit.Milliseconds);
    }

    /// <summary>The suffix must be immediately adjacent: '10 s' is a number and an identifier, not a duration.</summary>
    [Test]
    public async Task Tokenize_SpaceBeforeSuffix_IsNotADuration()
    {
        IReadOnlyList<Token> tokens = Lex("10 s");

        await Assert.That(tokens[0].Kind).IsEqualTo(TokenKind.IntLiteral);
        await Assert.That(tokens[1].Kind).IsEqualTo(TokenKind.Identifier);
        await Assert.That(tokens[1].Text).IsEqualTo("s");
    }

    /// <summary>A non-duration suffix is a lexical error naming the token, never two silent tokens.</summary>
    [Test]
    public async Task Tokenize_InvalidNumericSuffix_Errors()
    {
        Diagnostic error = FirstError("10sec");

        await Assert.That(error.Code).IsEqualTo(DiagnosticCodes.InvalidNumberSuffix);
        await Assert.That(error.OffendingText).IsEqualTo("10sec");
        await Assert.That(error.Message).Contains("'ms'");
    }

    /// <summary>Trailing garbage after a valid suffix ('10s5') is also a suffix error.</summary>
    [Test]
    public async Task Tokenize_SuffixWithTrailingGarbage_Errors()
    {
        Diagnostic error = FirstError("10s5");

        await Assert.That(error.Code).IsEqualTo(DiagnosticCodes.InvalidNumberSuffix);
        await Assert.That(error.OffendingText).IsEqualTo("10s5");
    }

    // ── String literals ──────────────────────────────────────────────────────────

    /// <summary>All four supported escapes unescape into the token's string value.</summary>
    [Test]
    public async Task Tokenize_StringEscapes_Unescape()
    {
        IReadOnlyList<Token> tokens = Lex("\"a\\\"b\" \"a\\\\b\" \"a\\nb\" \"a\\tb\"");

        await Assert.That(tokens[0].StringValue).IsEqualTo("a\"b");
        await Assert.That(tokens[1].StringValue).IsEqualTo("a\\b");
        await Assert.That(tokens[2].StringValue).IsEqualTo("a\nb");
        await Assert.That(tokens[3].StringValue).IsEqualTo("a\tb");
    }

    /// <summary>An escape outside the closed set is a lexical error naming the sequence.</summary>
    [Test]
    public async Task Tokenize_UnsupportedEscape_Errors()
    {
        Diagnostic error = FirstError("\"a\\qb\"");

        await Assert.That(error.Code).IsEqualTo(DiagnosticCodes.InvalidEscape);
        await Assert.That(error.OffendingText).IsEqualTo("\\q");
    }

    /// <summary>A string with no closing quote before end of input errors.</summary>
    [Test]
    public async Task Tokenize_UnterminatedStringAtEof_Errors()
    {
        Diagnostic error = FirstError("\"abc");

        await Assert.That(error.Code).IsEqualTo(DiagnosticCodes.UnterminatedString);
    }

    /// <summary>A raw newline terminates (and fails) a string literal — string-char excludes newline (spec §1).</summary>
    [Test]
    public async Task Tokenize_NewlineInsideString_Errors()
    {
        Diagnostic error = FirstError("\"ab\ncd\"");

        await Assert.That(error.Code).IsEqualTo(DiagnosticCodes.UnterminatedString);
    }

    // ── Hard EOF rule: unknown characters ────────────────────────────────────────

    /// <summary>
    ///     The v1 regression pin: '@' mid-expression is a lexical error with the exact column,
    ///     not a silently skipped character (spec §1 hard EOF rule).
    /// </summary>
    [Test]
    public async Task Tokenize_AtSignMidExpression_ErrorsWithColumn()
    {
        LanguageResult<IReadOnlyList<Token>> result = ExpressionLexer.Tokenize("a @ b");

        await Assert.That(result.Success).IsFalse();
        Diagnostic error = result.Diagnostics[0];
        await Assert.That(error.Code).IsEqualTo(DiagnosticCodes.UnknownCharacter);
        await Assert.That(error.OffendingText).IsEqualTo("@");
        await Assert.That(error.Span.Column).IsEqualTo(3);
        await Assert.That(error.Span.Offset).IsEqualTo(2);
        await Assert.That(error.Message).Contains("'@'");
        await Assert.That(error.Message).Contains("column 3");
    }

    /// <summary>A lone '&amp;' is an unknown character (with a helpful hint), not a silent skip.</summary>
    [Test]
    public async Task Tokenize_SingleAmpersand_ErrorsWithHint()
    {
        Diagnostic error = FirstError("a & b");

        await Assert.That(error.Code).IsEqualTo(DiagnosticCodes.UnknownCharacter);
        await Assert.That(error.Message).Contains("&&");
    }

    /// <summary>Unknown characters error on later lines with the correct line number.</summary>
    [Test]
    public async Task Tokenize_UnknownCharacterOnSecondLine_NamesLineAndColumn()
    {
        Diagnostic error = FirstError("a and\n b # c");

        await Assert.That(error.Code).IsEqualTo(DiagnosticCodes.UnknownCharacter);
        await Assert.That(error.Span.Line).IsEqualTo(2);
        await Assert.That(error.Span.Column).IsEqualTo(4);
    }

    // ── Reserved words ───────────────────────────────────────────────────────────

    /// <summary>Reserved words lex as operators/literals, and only in lowercase (spec §1).</summary>
    [Test]
    public async Task Tokenize_ReservedWords_CaseSensitive()
    {
        IReadOnlyList<Token> lower = Lex("and or not in true false null");
        await Assert.That(lower.Select(t => t.Kind).ToArray()).IsEquivalentTo(
        [
            TokenKind.And, TokenKind.Or, TokenKind.Not, TokenKind.In,
            TokenKind.True, TokenKind.False, TokenKind.Null, TokenKind.EndOfInput
        ]);

        IReadOnlyList<Token> mixed = Lex("And OR Not IN True FALSE Null");
        await Assert.That(mixed.Take(7).All(t => t.Kind == TokenKind.Identifier)).IsTrue();
    }
}
