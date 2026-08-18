#region

using System.Collections.Immutable;
using System.Globalization;
using CS2DemoKit.Analysis.Rules.Ast;
using CS2DemoKit.Analysis.Rules.Lexing;

#endregion

namespace CS2DemoKit.Analysis.Rules.Parsing;

/// <summary>
///     The spec §2 recursive-descent parser. Builds the canonical AST (parentheses vanish;
///     word-form operators already vanished at lexing). Enforces the grammar's deliberate
///     restrictions as loud errors: comparisons do not chain, the function set is closed and
///     arity-checked, list literals are homogeneous scalars, and — the hard EOF rule —
///     trailing tokens after a complete expression fail instead of being silently dropped
///     (the v1 truncation regression). Parsing stops at the first error.
/// </summary>
public static class ExpressionParser
{
    private static readonly Dictionary<string, (RuleFunction Function, int Arity)> _functions = new(StringComparer.Ordinal)
    {
        ["min"] = (RuleFunction.Min, 2),
        ["max"] = (RuleFunction.Max, 2),
        ["abs"] = (RuleFunction.Abs, 1),
        ["contains"] = (RuleFunction.Contains, 2),
        ["startswith"] = (RuleFunction.StartsWith, 2),
        ["floor"] = (RuleFunction.Floor, 1)
    };

    /// <summary>Lexes and parses an expression source string.</summary>
    /// <param name="source">The expression source (a YAML scalar; may span multiple lines).</param>
    /// <returns>The AST root, or the first lexical/parse error.</returns>
    public static LanguageResult<ExpressionNode> Parse(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        LanguageResult<IReadOnlyList<Token>> lexed = ExpressionLexer.Tokenize(source);
        return lexed.Success ? Parse(lexed.Require()) : LanguageResult.Fail<ExpressionNode>(lexed.Diagnostics);
    }

    /// <summary>Parses a token list produced by <see cref="ExpressionLexer.Tokenize" />.</summary>
    /// <param name="tokens">The tokens, ending with <see cref="TokenKind.EndOfInput" />.</param>
    /// <returns>The AST root, or the first parse error.</returns>
    public static LanguageResult<ExpressionNode> Parse(IReadOnlyList<Token> tokens)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        ParserState state = new(tokens);
        ExpressionNode? root = state.ParseExpression();
        if (root is null)
        {
            return LanguageResult.Fail<ExpressionNode>(state.Error!);
        }

        if (state.Current.Kind != TokenKind.EndOfInput)
        {
            // The hard EOF rule (spec §1): every token must be consumed.
            Token trailing = state.Current;
            return LanguageResult.Fail<ExpressionNode>(new Diagnostic(
                DiagnosticCodes.TrailingTokens,
                $"unexpected '{trailing.Text}' after the end of the expression",
                trailing.Span, trailing.Text));
        }

        return LanguageResult.Ok(root);
    }

    /// <summary>Mutable cursor + first-error state for one parse run.</summary>
    private sealed class ParserState(IReadOnlyList<Token> tokens)
    {
        private int _position;
        internal Diagnostic? Error { get; private set; }

        internal Token Current => tokens[Math.Min(_position, tokens.Count - 1)];

        private void Advance() => _position++;

        private ExpressionNode? Fail(string code, string message, SourceSpan span, string offendingText,
            IReadOnlyList<string>? didYouMean = null)
        {
            Error ??= new Diagnostic(code, message, span, offendingText, didYouMean);
            return null;
        }

        private ExpressionNode? UnexpectedToken(string expected)
        {
            Token token = Current;
            string found = token.Kind == TokenKind.EndOfInput
                ? "the end of the expression"
                : $"'{token.Text}'";
            return Fail(DiagnosticCodes.UnexpectedToken, $"expected {expected}, found {found}", token.Span, token.Text);
        }

        // expression = or-expr
        internal ExpressionNode? ParseExpression() => ParseOr();

        private ExpressionNode? ParseOr()
        {
            ExpressionNode? left = ParseAnd();
            while (left is not null && Current.Kind == TokenKind.Or)
            {
                Advance();
                ExpressionNode? right = ParseAnd();
                left = right is null
                    ? null
                    : new BinaryNode(BinaryOperator.Or, left, right, SourceSpan.Cover(left.Span, right.Span));
            }

            return left;
        }

        private ExpressionNode? ParseAnd()
        {
            ExpressionNode? left = ParseNot();
            while (left is not null && Current.Kind == TokenKind.And)
            {
                Advance();
                ExpressionNode? right = ParseNot();
                left = right is null
                    ? null
                    : new BinaryNode(BinaryOperator.And, left, right, SourceSpan.Cover(left.Span, right.Span));
            }

            return left;
        }

        // not-expr = ("!" | "not") not-expr | comparison — 'not' binds looser than comparison (spec §2).
        private ExpressionNode? ParseNot()
        {
            if (Current.Kind != TokenKind.Not)
            {
                return ParseComparison();
            }

            Token notToken = Current;
            Advance();
            ExpressionNode? operand = ParseNot();
            return operand is null
                ? null
                : new UnaryNode(UnaryOperator.Not, operand, SourceSpan.Cover(notToken.Span, operand.Span));
        }

        // comparison = additive [ comp-op additive ] | additive "in" list-operand — no chaining.
        private ExpressionNode? ParseComparison()
        {
            ExpressionNode? left = ParseAdditive();
            if (left is null)
            {
                return null;
            }

            BinaryOperator op = ComparisonOperator(Current.Kind);
            if (op == BinaryOperator.None)
            {
                return left;
            }

            Advance();
            ExpressionNode? right = op == BinaryOperator.In ? ParseInOperand() : ParseAdditive();
            if (right is null)
            {
                return null;
            }

            BinaryNode comparison = new(op, left, right, SourceSpan.Cover(left.Span, right.Span));
            if (ComparisonOperator(Current.Kind) != BinaryOperator.None)
            {
                // a < b < c — one optional comparison per level (spec §2); never the silent-truth trap.
                Token chained = Current;
                return Fail(DiagnosticCodes.ChainedComparison,
                    $"comparisons cannot be chained — write two comparisons joined with 'and' instead of '{chained.Text}'",
                    chained.Span, chained.Text);
            }

            return comparison;
        }

        private static BinaryOperator ComparisonOperator(TokenKind kind) =>
            kind switch
            {
                TokenKind.Equal => BinaryOperator.Equal,
                TokenKind.NotEqual => BinaryOperator.NotEqual,
                TokenKind.Greater => BinaryOperator.Greater,
                TokenKind.GreaterOrEqual => BinaryOperator.GreaterOrEqual,
                TokenKind.Less => BinaryOperator.Less,
                TokenKind.LessOrEqual => BinaryOperator.LessOrEqual,
                TokenKind.In => BinaryOperator.In,
                _ => BinaryOperator.None
            };

        // list-operand = reference | list-literal
        private ExpressionNode? ParseInOperand()
        {
            if (Current.Kind == TokenKind.LeftBracket)
            {
                return ParseListLiteral();
            }

            ExpressionNode? operand = ParsePostfix();
            if (operand is null)
            {
                return null;
            }

            return operand is ReferenceNode
                ? operand
                : Fail(DiagnosticCodes.InvalidInOperand,
                    "the right side of 'in' must be a list reference or a [ ... ] list literal",
                    operand.Span, operand.CanonicalText);
        }

        private ExpressionNode? ParseListLiteral()
        {
            Token open = Current;
            Advance(); // '['
            ImmutableArray<ExpressionNode>.Builder items = ImmutableArray.CreateBuilder<ExpressionNode>();

            if (Current.Kind != TokenKind.RightBracket)
            {
                while (true)
                {
                    ExpressionNode? item = ParseScalarLiteral();
                    if (item is null)
                    {
                        return null;
                    }

                    items.Add(item);
                    if (Current.Kind != TokenKind.Comma)
                    {
                        break;
                    }

                    Advance();
                }
            }

            if (Current.Kind != TokenKind.RightBracket)
            {
                return UnexpectedToken("',' or ']' in the list literal");
            }

            Token close = Current;
            Advance();
            SourceSpan span = SourceSpan.Cover(open.Span, close.Span);

            ListLiteralNode literal = new(items.ToImmutable(), span);
            string? mixError = MixedCategoryError(items);
            return mixError is null
                ? literal
                : Fail(DiagnosticCodes.MixedListLiteral, mixError, span, literal.CanonicalText);
        }

        private static string? MixedCategoryError(ImmutableArray<ExpressionNode>.Builder items)
        {
            static string Category(ExpressionNode item)
            {
                return item switch
                {
                    IntLiteralNode or FloatLiteralNode or DurationLiteralNode => "number",
                    StringLiteralNode => "string",
                    _ => "bool"
                };
            }

            for (int i = 1; i < items.Count; i++)
            {
                string first = Category(items[0]);
                string current = Category(items[i]);
                if (!string.Equals(first, current, StringComparison.Ordinal))
                {
                    return $"list literal mixes {first} and {current} elements — all elements must be the same type";
                }
            }

            return null;
        }

        // scalar-literal = number | duration | string | true | false | "-" number
        private ExpressionNode? ParseScalarLiteral()
        {
            Token token = Current;
            switch (token.Kind)
            {
                case TokenKind.IntLiteral:
                    Advance();
                    return new IntLiteralNode(token.IntegerValue, token.Span);

                case TokenKind.FloatLiteral:
                    Advance();
                    return new FloatLiteralNode(token.FloatValue, token.Span);

                case TokenKind.DurationLiteral:
                    Advance();
                    return new DurationLiteralNode(token.DurationMagnitude, token.Unit, token.Span);

                case TokenKind.StringLiteral:
                    Advance();
                    return new StringLiteralNode(token.StringValue ?? "", token.Span);

                case TokenKind.True:
                case TokenKind.False:
                    Advance();
                    return new BoolLiteralNode(token.Kind == TokenKind.True, token.Span);

                case TokenKind.Minus:
                {
                    Advance();
                    Token number = Current;
                    if (number.Kind == TokenKind.IntLiteral)
                    {
                        Advance();
                        return new IntLiteralNode(-number.IntegerValue, SourceSpan.Cover(token.Span, number.Span));
                    }

                    if (number.Kind == TokenKind.FloatLiteral)
                    {
                        Advance();
                        return new FloatLiteralNode(-number.FloatValue, SourceSpan.Cover(token.Span, number.Span));
                    }

                    return Fail(DiagnosticCodes.InvalidListElement,
                        "'-' in a list literal must be followed by a number",
                        number.Span, number.Text);
                }

                case TokenKind.Null:
                    return Fail(DiagnosticCodes.InvalidListElement,
                        "'null' is not allowed in a list literal", token.Span, token.Text);

                default:
                    return Fail(DiagnosticCodes.InvalidListElement,
                        $"expected a literal in the list, found {(token.Kind == TokenKind.EndOfInput ? "the end of the expression" : $"'{token.Text}'")}",
                        token.Span, token.Text);
            }
        }

        private ExpressionNode? ParseAdditive()
        {
            ExpressionNode? left = ParseMultiplicative();
            while (left is not null && Current.Kind is TokenKind.Plus or TokenKind.Minus)
            {
                BinaryOperator op = Current.Kind == TokenKind.Plus ? BinaryOperator.Add : BinaryOperator.Subtract;
                Advance();
                ExpressionNode? right = ParseMultiplicative();
                left = right is null ? null : new BinaryNode(op, left, right, SourceSpan.Cover(left.Span, right.Span));
            }

            return left;
        }

        private ExpressionNode? ParseMultiplicative()
        {
            ExpressionNode? left = ParseUnary();
            while (left is not null && Current.Kind is TokenKind.Star or TokenKind.Slash or TokenKind.Percent)
            {
                BinaryOperator op = Current.Kind switch
                {
                    TokenKind.Star => BinaryOperator.Multiply,
                    TokenKind.Slash => BinaryOperator.Divide,
                    _ => BinaryOperator.Modulo
                };
                Advance();
                ExpressionNode? right = ParseUnary();
                left = right is null ? null : new BinaryNode(op, left, right, SourceSpan.Cover(left.Span, right.Span));
            }

            return left;
        }

        // unary = "-" unary | postfix — unary minus is legal (a v1 parse error, fixed per spec §2).
        private ExpressionNode? ParseUnary()
        {
            if (Current.Kind != TokenKind.Minus)
            {
                return ParsePostfix();
            }

            Token minus = Current;
            Advance();
            ExpressionNode? operand = ParseUnary();
            if (operand is null)
            {
                return null;
            }

            SourceSpan span = SourceSpan.Cover(minus.Span, operand.Span);

            // Sign folding on literals only (never arithmetic folding): '-99' is the literal −99,
            // matching the list-literal grammar where '-' number is itself a scalar literal.
            return operand switch
            {
                IntLiteralNode i => new IntLiteralNode(-i.Value, span),
                FloatLiteralNode f => new FloatLiteralNode(-f.Value, span),
                DurationLiteralNode d => new DurationLiteralNode(-d.Magnitude, d.Unit, span),
                _ => new UnaryNode(UnaryOperator.Negate, operand, span)
            };
        }

        // postfix = primary { member-access | index-access }
        private ExpressionNode? ParsePostfix()
        {
            ExpressionNode? node = ParsePrimary();
            while (node is not null)
            {
                if (Current.Kind == TokenKind.Dot)
                {
                    Advance();
                    if (Current.Kind != TokenKind.Identifier)
                    {
                        return UnexpectedToken("a member name after '.'");
                    }

                    Token member = Current;
                    Advance();
                    SourceSpan span = SourceSpan.Cover(node.Span, member.Span);
                    node = node is ReferenceNode reference
                        ? reference.Append(member.Text, span)
                        : new MemberAccessNode(node, member.Text, span);
                    continue;
                }

                if (Current.Kind == TokenKind.LeftBracket)
                {
                    Advance();
                    ExpressionNode? index = ParseExpression();
                    if (index is null)
                    {
                        return null;
                    }

                    if (Current.Kind != TokenKind.RightBracket)
                    {
                        return UnexpectedToken("']' to close the index");
                    }

                    Token close = Current;
                    Advance();
                    node = new IndexAccessNode(node, index, SourceSpan.Cover(node.Span, close.Span));
                    continue;
                }

                break;
            }

            return node;
        }

        private ExpressionNode? ParsePrimary()
        {
            Token token = Current;
            switch (token.Kind)
            {
                case TokenKind.IntLiteral:
                    Advance();
                    return new IntLiteralNode(token.IntegerValue, token.Span);

                case TokenKind.FloatLiteral:
                    Advance();
                    return new FloatLiteralNode(token.FloatValue, token.Span);

                case TokenKind.DurationLiteral:
                    Advance();
                    return new DurationLiteralNode(token.DurationMagnitude, token.Unit, token.Span);

                case TokenKind.StringLiteral:
                    Advance();
                    return new StringLiteralNode(token.StringValue ?? "", token.Span);

                case TokenKind.True:
                case TokenKind.False:
                    Advance();
                    return new BoolLiteralNode(token.Kind == TokenKind.True, token.Span);

                case TokenKind.Null:
                    Advance();
                    return new NullLiteralNode(token.Span);

                case TokenKind.LeftParen:
                {
                    Advance();
                    ExpressionNode? inner = ParseExpression();
                    if (inner is null)
                    {
                        return null;
                    }

                    if (Current.Kind != TokenKind.RightParen)
                    {
                        return UnexpectedToken("')' to close the parenthesized expression");
                    }

                    Advance();
                    return inner; // parentheses vanish: the AST is the precedence (spec §5 row 2)
                }

                case TokenKind.Identifier:
                    return ParseIdentifierPrimary();

                default:
                    return UnexpectedToken("an expression");
            }
        }

        private ExpressionNode? ParseIdentifierPrimary()
        {
            Token identifier = Current;
            Advance();

            if (Current.Kind != TokenKind.LeftParen)
            {
                return new ReferenceNode([identifier.Text], identifier.Span);
            }

            // identifier '(' — a function call; the function set is closed (spec §2).
            if (!_functions.TryGetValue(identifier.Text, out (RuleFunction Function, int Arity) function))
            {
                IReadOnlyList<string> suggestions = NameSuggestions.Suggest(identifier.Text, _functions.Keys);
                string hint = suggestions.Count > 0 ? $" — did you mean '{suggestions[0]}'?" : "";
                return Fail(DiagnosticCodes.UnknownFunction,
                    $"unknown function '{identifier.Text}' — the functions are min, max, abs, contains, startswith{hint}",
                    identifier.Span, identifier.Text, suggestions);
            }

            Advance(); // '('
            ImmutableArray<ExpressionNode>.Builder arguments = ImmutableArray.CreateBuilder<ExpressionNode>();
            if (Current.Kind != TokenKind.RightParen)
            {
                while (true)
                {
                    ExpressionNode? argument = ParseExpression();
                    if (argument is null)
                    {
                        return null;
                    }

                    arguments.Add(argument);
                    if (Current.Kind != TokenKind.Comma)
                    {
                        break;
                    }

                    Advance();
                }
            }

            if (Current.Kind != TokenKind.RightParen)
            {
                return UnexpectedToken($"')' to close the call to {identifier.Text}");
            }

            Token close = Current;
            Advance();
            SourceSpan span = SourceSpan.Cover(identifier.Span, close.Span);

            if (arguments.Count != function.Arity)
            {
                return Fail(DiagnosticCodes.WrongArity,
                    $"{identifier.Text} expects {function.Arity.ToString(CultureInfo.InvariantCulture)} argument{(function.Arity == 1 ? "" : "s")}, got {arguments.Count.ToString(CultureInfo.InvariantCulture)}",
                    span, identifier.Text);
            }

            return new CallNode(function.Function, arguments.ToImmutable(), span);
        }
    }
}
