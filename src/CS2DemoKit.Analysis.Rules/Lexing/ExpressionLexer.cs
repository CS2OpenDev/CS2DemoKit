#region

using System.Globalization;
using System.Text;

#endregion

namespace CS2DemoKit.Analysis.Rules.Lexing;

/// <summary>
///     The spec §1 lexer. Enforces the hard EOF rule: every character of the input must be
///     consumed by a token — an unrecognized character is a lexical error naming the
///     character and its column, never a silent skip (the v1 tokenizer regression). Lexing
///     stops at the first lexical error.
/// </summary>
public static class ExpressionLexer
{
    /// <summary>
    ///     Tokenizes an expression source string. On success the token list always ends with
    ///     a <see cref="TokenKind.EndOfInput" /> token.
    /// </summary>
    /// <param name="source">The expression source (a YAML scalar; may span multiple lines).</param>
    /// <returns>The token list, or the first lexical error.</returns>
    public static LanguageResult<IReadOnlyList<Token>> Tokenize(string source)
    {
        ArgumentNullException.ThrowIfNull(source);

        List<Token> tokens = [];
        int offset = 0;
        int line = 1;
        int column = 1;

        while (offset < source.Length)
        {
            char c = source[offset];

            if (c is ' ' or '\t' or '\r')
            {
                offset++;
                column++;
                continue;
            }

            if (c == '\n')
            {
                offset++;
                line++;
                column = 1;
                continue;
            }

            if (IsIdentifierStart(c))
            {
                int start = offset;
                while (offset < source.Length && IsIdentifierPart(source[offset]))
                {
                    offset++;
                }

                string text = source[start..offset];
                SourceSpan span = new(start, text.Length, line, column);
                column += text.Length;
                tokens.Add(new Token(ClassifyWord(text), text, span));
                continue;
            }

            if (IsDigit(c))
            {
                LanguageResult<Token> number = ReadNumber(source, ref offset, line, ref column);
                if (!number.Success)
                {
                    return LanguageResult.Fail<IReadOnlyList<Token>>(number.Diagnostics);
                }

                tokens.Add(number.Require());
                continue;
            }

            if (c == '"')
            {
                LanguageResult<Token> str = ReadString(source, ref offset, line, ref column);
                if (!str.Success)
                {
                    return LanguageResult.Fail<IReadOnlyList<Token>>(str.Diagnostics);
                }

                tokens.Add(str.Require());
                continue;
            }

            LanguageResult<Token> op = ReadOperator(source, ref offset, line, ref column);
            if (!op.Success)
            {
                return LanguageResult.Fail<IReadOnlyList<Token>>(op.Diagnostics);
            }

            tokens.Add(op.Require());
        }

        tokens.Add(new Token(TokenKind.EndOfInput, "", new SourceSpan(offset, 0, line, column)));
        return LanguageResult.Ok<IReadOnlyList<Token>>(tokens);
    }

    private static TokenKind ClassifyWord(string text) =>
        text switch
        {
            // Reserved words are case-sensitive: only the lowercase forms are reserved (spec §1).
            "and" => TokenKind.And,
            "or" => TokenKind.Or,
            "not" => TokenKind.Not,
            "in" => TokenKind.In,
            "true" => TokenKind.True,
            "false" => TokenKind.False,
            "null" => TokenKind.Null,
            _ => TokenKind.Identifier
        };

    private static LanguageResult<Token> ReadNumber(string source, ref int offset, int line, ref int column)
    {
        int start = offset;
        int startColumn = column;
        while (offset < source.Length && IsDigit(source[offset]))
        {
            offset++;
        }

        bool isFloat = false;
        if (offset + 1 < source.Length && source[offset] == '.' && IsDigit(source[offset + 1]))
        {
            isFloat = true;
            offset++; // '.'
            while (offset < source.Length && IsDigit(source[offset]))
            {
                offset++;
            }
        }

        string numberText = source[start..offset];

        // Duration suffix must be immediately adjacent (spec §1). Any other adjacent
        // identifier run is a lexical error naming the suffix — never two silent tokens.
        int suffixStart = offset;
        while (offset < source.Length && IsIdentifierPart(source[offset]))
        {
            offset++;
        }

        string suffix = source[suffixStart..offset];
        string fullText = source[start..offset];
        SourceSpan span = new(start, fullText.Length, line, startColumn);
        column += fullText.Length;

        switch (suffix)
        {
            case "":
                if (isFloat)
                {
                    return double.TryParse(numberText, NumberStyles.Float, CultureInfo.InvariantCulture,
                        out double floatValue)
                        ? LanguageResult.Ok(new Token(TokenKind.FloatLiteral, fullText, span)
                        {
                            FloatValue = floatValue
                        })
                        : Error(DiagnosticCodes.InvalidNumber,
                            $"'{fullText}' is not a representable float literal", span, fullText);
                }

                return long.TryParse(numberText, NumberStyles.None, CultureInfo.InvariantCulture, out long intValue)
                    ? LanguageResult.Ok(new Token(TokenKind.IntLiteral, fullText, span)
                    {
                        IntegerValue = intValue
                    })
                    : Error(DiagnosticCodes.InvalidNumber,
                        $"'{fullText}' does not fit a 64-bit integer literal", span, fullText);

            case "s" or "ms":
                return double.TryParse(numberText, NumberStyles.Float, CultureInfo.InvariantCulture,
                    out double magnitude)
                    ? LanguageResult.Ok(new Token(TokenKind.DurationLiteral, fullText, span)
                    {
                        DurationMagnitude = magnitude,
                        Unit = suffix == "s" ? DurationUnit.Seconds : DurationUnit.Milliseconds
                    })
                    : Error(DiagnosticCodes.InvalidNumber,
                        $"'{fullText}' is not a representable duration literal", span, fullText);

            default:
                return Error(DiagnosticCodes.InvalidNumberSuffix,
                    $"'{fullText}' is not a valid number — the only numeric suffixes are the duration units 's' and 'ms'",
                    span, fullText);
        }
    }

    private static LanguageResult<Token> ReadString(string source, ref int offset, int line, ref int column)
    {
        int start = offset;
        int startColumn = column;
        offset++; // opening quote
        StringBuilder content = new();

        while (true)
        {
            if (offset >= source.Length || source[offset] is '\n' or '\r')
            {
                SourceSpan span = new(start, offset - start, line, startColumn);
                column += offset - start;
                return Error(DiagnosticCodes.UnterminatedString,
                    "unterminated string literal — expected a closing '\"'", span, source[start..offset]);
            }

            char c = source[offset];
            if (c == '"')
            {
                offset++;
                string text = source[start..offset];
                SourceSpan span = new(start, text.Length, line, startColumn);
                column += text.Length;
                return LanguageResult.Ok(new Token(TokenKind.StringLiteral, text, span)
                {
                    StringValue = content.ToString()
                });
            }

            if (c == '\\')
            {
                if (offset + 1 >= source.Length)
                {
                    SourceSpan span = new(start, source.Length - start, line, startColumn);
                    column += source.Length - start;
                    return Error(DiagnosticCodes.UnterminatedString,
                        "unterminated string literal — expected a closing '\"'", span, source[start..]);
                }

                char escaped = source[offset + 1];
                char? resolved = escaped switch
                {
                    '"' => '"',
                    '\\' => '\\',
                    'n' => '\n',
                    't' => '\t',
                    _ => null
                };

                if (resolved is null)
                {
                    int escapeColumn = startColumn + (offset - start);
                    SourceSpan span = new(offset, 2, line, escapeColumn);
                    return Error(DiagnosticCodes.InvalidEscape,
                        $"unsupported escape sequence '\\{escaped}' — supported escapes are \\\" \\\\ \\n \\t",
                        span, source.Substring(offset, 2));
                }

                content.Append(resolved.Value);
                offset += 2;
                continue;
            }

            content.Append(c);
            offset++;
        }
    }

    private static LanguageResult<Token> ReadOperator(string source, ref int offset, int line, ref int column)
    {
        char c = source[offset];
        char next = offset + 1 < source.Length ? source[offset + 1] : '\0';

        (TokenKind Kind, int Length)? match = (c, next) switch
        {
            ('=', '=') => (TokenKind.Equal, 2),
            ('!', '=') => (TokenKind.NotEqual, 2),
            ('>', '=') => (TokenKind.GreaterOrEqual, 2),
            ('<', '=') => (TokenKind.LessOrEqual, 2),
            ('&', '&') => (TokenKind.And, 2),
            ('|', '|') => (TokenKind.Or, 2),
            ('>', _) => (TokenKind.Greater, 1),
            ('<', _) => (TokenKind.Less, 1),
            ('!', _) => (TokenKind.Not, 1),
            ('+', _) => (TokenKind.Plus, 1),
            ('-', _) => (TokenKind.Minus, 1),
            ('*', _) => (TokenKind.Star, 1),
            ('/', _) => (TokenKind.Slash, 1),
            ('%', _) => (TokenKind.Percent, 1),
            ('(', _) => (TokenKind.LeftParen, 1),
            (')', _) => (TokenKind.RightParen, 1),
            ('[', _) => (TokenKind.LeftBracket, 1),
            (']', _) => (TokenKind.RightBracket, 1),
            (',', _) => (TokenKind.Comma, 1),
            ('.', _) => (TokenKind.Dot, 1),
            _ => null
        };

        if (match is null)
        {
            string hint = c switch
            {
                '&' => " — did you mean '&&'?",
                '|' => " — did you mean '||'?",
                '=' => " — did you mean '=='?",
                _ => ""
            };

            SourceSpan errorSpan = new(offset, 1, line, column);
            return Error(DiagnosticCodes.UnknownCharacter,
                $"unrecognized character '{c}' at line {line.ToString(CultureInfo.InvariantCulture)}, column {column.ToString(CultureInfo.InvariantCulture)}{hint}",
                errorSpan, c.ToString());
        }

        (TokenKind kind, int length) = match.Value;
        string text = source.Substring(offset, length);
        SourceSpan span = new(offset, length, line, column);
        offset += length;
        column += length;
        return LanguageResult.Ok(new Token(kind, text, span));
    }

    private static LanguageResult<Token> Error(string code, string message, SourceSpan span, string offendingText) =>
        LanguageResult.Fail<Token>(new Diagnostic(code, message, span, offendingText));

    private static bool IsIdentifierStart(char c) => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or '_';

    private static bool IsIdentifierPart(char c) => IsIdentifierStart(c) || IsDigit(c);

    private static bool IsDigit(char c) => c is >= '0' and <= '9';
}
