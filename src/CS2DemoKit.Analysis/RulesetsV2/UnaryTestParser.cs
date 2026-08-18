#region

using CS2DemoKit.Analysis.Rules;
using CS2DemoKit.Analysis.Rules.Lexing;
using CS2DemoKit.Analysis.RulesetsV2.Model;

#endregion

namespace CS2DemoKit.Analysis.RulesetsV2;

/// <summary>
///     Parses a <c>match:</c> value's source text into one of the four <see cref="UnaryTest" />
///     forms: a bare-literal equality (<c>enemy: true</c>), a list-ref
///     membership (<c>weapon: in rifles</c>), an inline-list membership (<c>weapon: in [ak47]</c>),
///     a single comparison (<c>damage: "&gt;= 5"</c>), or an inclusive integer range
///     (<c>count: [2..5]</c>). The facet on the left is implicit (it is the map key), so this
///     parses only the right-hand shape — the standard expression parser cannot, since it needs a
///     left operand. Reuses the semantic-core lexer (spec §1) for tokenization; desugaring to a
///     comparison against the resolved facet is the resolver's job, not this one.
/// </summary>
public static class UnaryTestParser
{
    /// <summary>
    ///     Parses a match value's source text into a <see cref="UnaryTest" />. On malformed input
    ///     it appends a <see cref="RulesetDiagnostic" /> and returns <c>null</c> so the caller can
    ///     continue collecting errors.
    /// </summary>
    /// <param name="text">The raw match value text (a scalar, or a range reconstructed as <c>[lo..hi]</c>).</param>
    /// <param name="position">The document-absolute position of the value node.</param>
    /// <param name="diagnostics">The collection to append any diagnostic to.</param>
    /// <returns>The parsed unary test, or <c>null</c> when the input was malformed.</returns>
    public static UnaryTest? Parse(string text, SourcePosition position, ICollection<RulesetDiagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(diagnostics);

        LanguageResult<IReadOnlyList<Token>> lexed = ExpressionLexer.Tokenize(text);
        if (!lexed.Success)
        {
            return Fail(diagnostics, position,
                $"'{text.Trim()}' is not a valid match value: {lexed.Diagnostics[0].Message}");
        }

        IReadOnlyList<Token> tokens = lexed.Require();
        if (tokens.Count == 1) // only EndOfInput
        {
            return Fail(diagnostics, position, "a match value must not be empty");
        }

        Token first = tokens[0];
        return first.Kind switch
        {
            TokenKind.LeftBracket => ParseRange(text, tokens, position, diagnostics),
            TokenKind.In => ParseInList(text, tokens, position, diagnostics),
            TokenKind.Equal or TokenKind.NotEqual or TokenKind.Greater or TokenKind.GreaterOrEqual
                or TokenKind.Less or TokenKind.LessOrEqual => ParseComparison(text, tokens, position, diagnostics),
            _ => ParseLiteral(text, tokens, position, diagnostics)
        };
    }

    private static UnaryTest? ParseRange(
        string text, IReadOnlyList<Token> tokens, SourcePosition position, ICollection<RulesetDiagnostic> diagnostics)
    {
        // Expected token shape: '[' int '.' '.' int ']' EndOfInput.
        if (tokens.Count == 7
            && tokens[1].Kind == TokenKind.IntLiteral
            && tokens[2].Kind == TokenKind.Dot
            && tokens[3].Kind == TokenKind.Dot
            && tokens[4].Kind == TokenKind.IntLiteral
            && tokens[5].Kind == TokenKind.RightBracket
            && tokens[6].Kind == TokenKind.EndOfInput)
        {
            long low = tokens[1].IntegerValue;
            long high = tokens[4].IntegerValue;
            return low <= high
                ? new RangeTest(low, high, position)
                : Fail(diagnostics, position,
                    $"range '{text.Trim()}' is inverted — the low bound must not exceed the high bound");
        }

        return Fail(diagnostics, position,
            $"'{text.Trim()}' is not a valid unary test — a bracketed value must be an inclusive integer range '[lo..hi]' (for a list membership write 'in [...]')");
    }

    private static UnaryTest? ParseInList(
        string text, IReadOnlyList<Token> tokens, SourcePosition position, ICollection<RulesetDiagnostic> diagnostics)
    {
        // 'in' <identifier>  →  list ref.
        if (tokens is [_, { Kind: TokenKind.Identifier } refToken, { Kind: TokenKind.EndOfInput }])
        {
            return new InListRefTest(refToken.Text, position);
        }

        // 'in' '[' scalar { ',' scalar } ']'  →  inline literal list.
        if (tokens.Count >= 4 && tokens[1].Kind == TokenKind.LeftBracket)
        {
            List<string> items = [];
            int i = 2;
            bool expectItem = true;
            while (i < tokens.Count && tokens[i].Kind != TokenKind.RightBracket)
            {
                Token t = tokens[i];
                if (expectItem)
                {
                    if (!IsScalarToken(t.Kind))
                    {
                        return Fail(diagnostics, position,
                            $"'{text.Trim()}' has an invalid list element '{t.Text}' — list elements must be scalar literals");
                    }

                    items.Add(t.Text);
                    expectItem = false;
                }
                else if (t.Kind != TokenKind.Comma)
                {
                    return Fail(diagnostics, position,
                        $"'{text.Trim()}' is a malformed list — expected ',' or ']' after an element");
                }
                else
                {
                    expectItem = true;
                }

                i++;
            }

            bool closed = i < tokens.Count && tokens[i].Kind == TokenKind.RightBracket
                                           && i + 1 < tokens.Count && tokens[i + 1].Kind == TokenKind.EndOfInput;
            if (closed && items.Count > 0)
            {
                return new InListLiteralTest(items, position);
            }
        }

        return Fail(diagnostics, position,
            $"'{text.Trim()}' is not a valid 'in' test — the right side must be a list name or a non-empty '[...]' list literal");
    }

    private static UnaryTest? ParseComparison(
        string text, IReadOnlyList<Token> tokens, SourcePosition position, ICollection<RulesetDiagnostic> diagnostics)
    {
        ComparisonOperator op = tokens[0].Kind switch
        {
            TokenKind.Equal => ComparisonOperator.Equal,
            TokenKind.NotEqual => ComparisonOperator.NotEqual,
            TokenKind.Greater => ComparisonOperator.Greater,
            TokenKind.GreaterOrEqual => ComparisonOperator.GreaterOrEqual,
            TokenKind.Less => ComparisonOperator.Less,
            TokenKind.LessOrEqual => ComparisonOperator.LessOrEqual,
            _ => ComparisonOperator.None
        };

        // '<op>' <scalar> EndOfInput  (with an optional leading '-' on a number).
        int i = 1;
        bool negative = i < tokens.Count && tokens[i].Kind == TokenKind.Minus;
        if (negative)
        {
            i++;
        }

        if (i + 1 < tokens.Count
            && IsScalarToken(tokens[i].Kind)
            && tokens[i + 1].Kind == TokenKind.EndOfInput
            && !(negative && tokens[i].Kind is not (TokenKind.IntLiteral or TokenKind.FloatLiteral)))
        {
            ScalarKind kind = ScalarKindOf(tokens[i].Kind);
            string literal = negative ? "-" + tokens[i].Text : tokens[i].Text;
            return new ComparisonTest(op, literal, kind, position);
        }

        return Fail(diagnostics, position,
            $"'{text.Trim()}' is not a valid comparison — expected exactly one comparison operator followed by one literal");
    }

    private static UnaryTest? ParseLiteral(
        string text, IReadOnlyList<Token> tokens, SourcePosition position, ICollection<RulesetDiagnostic> diagnostics)
    {
        // A bare scalar literal (optionally negated) is an equality test against that value.
        int i = 0;
        bool negative = tokens[i].Kind == TokenKind.Minus;
        if (negative)
        {
            i++;
        }

        if (i + 1 < tokens.Count
            && IsScalarToken(tokens[i].Kind)
            && tokens[i + 1].Kind == TokenKind.EndOfInput
            && !(negative && tokens[i].Kind is not (TokenKind.IntLiteral or TokenKind.FloatLiteral)))
        {
            ScalarKind kind = ScalarKindOf(tokens[i].Kind);
            string raw = negative ? "-" + tokens[i].Text : tokens[i].Text;
            return new LiteralTest(raw, kind, position);
        }

        return Fail(diagnostics, position,
            $"'{text.Trim()}' is not a valid unary test — expected a literal, 'in <list>', a comparison, or a range '[lo..hi]'");
    }

    private static bool IsScalarToken(TokenKind kind) =>
        kind is TokenKind.True or TokenKind.False or TokenKind.IntLiteral or TokenKind.FloatLiteral
            or TokenKind.StringLiteral or TokenKind.Identifier;

    private static ScalarKind ScalarKindOf(TokenKind kind) =>
        kind switch
        {
            TokenKind.True or TokenKind.False => ScalarKind.Bool,
            TokenKind.IntLiteral => ScalarKind.Int,
            TokenKind.FloatLiteral => ScalarKind.Float,
            _ => ScalarKind.String // StringLiteral and Identifier both present as string
        };

    private static UnaryTest? Fail(ICollection<RulesetDiagnostic> diagnostics, SourcePosition position, string message)
    {
        diagnostics.Add(new RulesetDiagnostic(RulesetDiagnosticCodes.BadUnaryTest, message, position));
        return null;
    }
}
