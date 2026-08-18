#region

using System.Collections.Immutable;
using CS2DemoKit.Analysis.Rules.Ast;
using CS2DemoKit.Analysis.Rules.Scopes;

#endregion

namespace CS2DemoKit.Analysis.Rules.Checking;

/// <summary>
///     The resolver + typed checker (spec §3/§4). Resolves every reference left-to-right
///     against the slot's scope environment, synthesizes language-level types bottom-up,
///     enforces the §3.2 coercion rules (int→float, one-way int→duration/instant, and
///     nothing else), the duration/instant algebra, the §3.4 list restrictions, and the
///     §3.3 null-literal rules. Collects multiple diagnostics per run; type names in
///     messages are always language-level (spec §8).
/// </summary>
public static class ExpressionChecker
{
    private static readonly RulesType _errorType = new(RulesTypeKind.None);

    /// <summary>Resolves and type-checks a (normalized) expression AST.</summary>
    /// <param name="root">The AST root — normalize first; the checked AST is the hashing form.</param>
    /// <param name="scope">The slot's scope environment.</param>
    /// <param name="expectedType">The slot's required result type, when the slot demands one (e.g. bool for <c>when:</c>).</param>
    /// <returns>The checked expression, or all collected diagnostics.</returns>
    public static LanguageResult<CheckedExpression> Check(ExpressionNode root, IScopeEnvironment scope,
        RulesType? expectedType = null)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(scope);

        Session session = new(scope);
        RulesType resultType = session.Visit(root);

        if (session.Diagnostics.Count == 0 && expectedType is { } expected && !IsAssignable(resultType, expected))
        {
            session.Diagnostics.Add(new Diagnostic(DiagnosticCodes.ExpectedType,
                $"this {scope.SlotName} expression must be {expected}, but it is {resultType}",
                root.Span, root.CanonicalText));
        }

        return session.Diagnostics.Count > 0
            ? LanguageResult.Fail<CheckedExpression>(session.Diagnostics)
            : LanguageResult.Ok(new CheckedExpression(root, resultType, session.References, session.ByNode));
    }

    /// <summary>One-way assignability into a slot's required type (spec §3.2).</summary>
    private static bool IsAssignable(RulesType actual, RulesType expected) =>
        actual == expected
        || actual.Kind == RulesTypeKind.Int
        && expected.Kind is RulesTypeKind.Float or RulesTypeKind.Duration or RulesTypeKind.Instant;

    /// <summary>Mutable state of one check run: diagnostics, per-node resolutions, and the read set.</summary>
    private sealed class Session(IScopeEnvironment scope)
    {
        private readonly HashSet<string> _seenPaths = new(StringComparer.Ordinal);
        internal List<Diagnostic> Diagnostics { get; } = [];

        internal Dictionary<ReferenceNode, ResolvedReference> ByNode { get; } = new(ReferenceEqualityComparer.Instance);

        internal List<ResolvedReference> References { get; } = [];

        private RulesType Fail(string code, string message, SourceSpan span, string offendingText,
            IReadOnlyList<string>? didYouMean = null)
        {
            Diagnostics.Add(new Diagnostic(code, message, span, offendingText, didYouMean));
            return _errorType;
        }

        private static bool IsError(RulesType type) => type.Kind == RulesTypeKind.None;

        private static bool IsContainer(RulesType type) =>
            type.Kind is RulesTypeKind.List or RulesTypeKind.Map;

        internal RulesType Visit(ExpressionNode node) =>
            node switch
            {
                IntLiteralNode => RulesType.Int,
                FloatLiteralNode => RulesType.Float,
                StringLiteralNode => RulesType.String,
                BoolLiteralNode => RulesType.Bool,
                NullLiteralNode => RulesType.Null,
                DurationLiteralNode => RulesType.Duration, // robust pre-normalization; folded to int by §5 row 3
                ReferenceNode reference => VisitReference(reference),
                ListLiteralNode list => VisitList(list),
                MapLiteralNode map => VisitMap(map),
                MemberAccessNode member => VisitMember(member),
                IndexAccessNode index => VisitIndex(index),
                UnaryNode unary => VisitUnary(unary),
                BinaryNode binary => VisitBinary(binary),
                CallNode call => VisitCall(call),
                _ => throw new InvalidOperationException($"unknown AST node type {node.GetType().Name}")
            };

        // ── References (spec §4) ─────────────────────────────────────────────────

        private RulesType VisitReference(ReferenceNode reference)
        {
            ImmutableArray<string> segments = reference.Segments;
            string head = segments[0];

            if (!scope.TryGetRoot(head, out IScopeSymbol? symbol))
            {
                IReadOnlyList<string> suggestions = NameSuggestions.Suggest(head, scope.RootNames);
                string roots = string.Join(", ", scope.RootNames.OrderBy(n => n, StringComparer.Ordinal));
                string hint = suggestions.Count > 0 ? $" — did you mean '{suggestions[0]}'?" : "";
                return Fail(DiagnosticCodes.UnknownRoot,
                    $"unknown name '{head}' in the {scope.SlotName} slot — available roots: {roots}{hint}",
                    reference.Span, head, suggestions);
            }

            IScopeSymbol? statSymbol = symbol.Kind == ScopeSymbolKind.Stat ? symbol : null;
            int statDepth = statSymbol is null ? 0 : 1;
            RulesType? pseudoType = null;

            for (int i = 1; i < segments.Length; i++)
            {
                string segment = segments[i];
                string prefix = string.Join('.', segments.Take(i));

                if (pseudoType is not null)
                {
                    return Fail(DiagnosticCodes.UnknownMember,
                        $"'{prefix}' has no member '{segment}'", reference.Span, segment);
                }

                if (symbol.TryGetMember(segment, out IScopeSymbol? member))
                {
                    symbol = member;
                    if (member.Kind == ScopeSymbolKind.Stat)
                    {
                        statSymbol = member;
                        statDepth = i + 1;
                    }

                    continue;
                }

                // Pseudo-members (spec §3.4/§3.5): .count and .set on lists, .set on scalar captures.
                bool isList = symbol.ValueType is { Kind: RulesTypeKind.List };
                if (segment == "count" && isList)
                {
                    pseudoType = RulesType.Int;
                    continue;
                }

                if (segment == "set")
                {
                    if (isList || symbol.SupportsSetTest)
                    {
                        pseudoType = RulesType.Bool;
                        continue;
                    }

                    return Fail(DiagnosticCodes.SetNotSupported,
                        $"'.set' is only available on capture stats and list stats — '{prefix}' is a plain value",
                        reference.Span, segment);
                }

                List<string> candidates = [.. symbol.MemberNames];
                if (isList)
                {
                    candidates.Add("count");
                    candidates.Add("set");
                }
                else if (symbol.SupportsSetTest)
                {
                    candidates.Add("set");
                }

                IReadOnlyList<string> suggestions = NameSuggestions.Suggest(segment, candidates);
                string hint = suggestions.Count > 0 ? $" — did you mean '{suggestions[0]}'?" : "";
                return Fail(DiagnosticCodes.UnknownMember,
                    $"'{segment}' is not a member of '{prefix}'{hint}", reference.Span, segment, suggestions);
            }

            RulesType resultType;
            if (pseudoType is { } pseudo)
            {
                resultType = pseudo;
            }
            else if (symbol.ValueType is { } valueType)
            {
                resultType = valueType;
            }
            else
            {
                string members = string.Join(", ", symbol.MemberNames.OrderBy(n => n, StringComparer.Ordinal));
                return Fail(DiagnosticCodes.NotAValue,
                    $"'{reference.Path}' is not a value — its members are: {members}", reference.Span, reference.Path);
            }

            bool isStat = statSymbol is not null;
            ResolvedReference resolved = new(reference, reference.Path, resultType,
                statSymbol ?? symbol, isStat,
                isStat ? string.Join('.', segments.Take(statDepth)) : null,
                isStat ? [.. segments.Skip(statDepth)] : []);

            ByNode[reference] = resolved;
            if (_seenPaths.Add(reference.Path))
            {
                References.Add(resolved);
            }

            return resultType;
        }

        // ── Literals and containers ──────────────────────────────────────────────

        private RulesType VisitList(ListLiteralNode list)
        {
            if (list.Items.Length == 0)
            {
                return RulesType.ListOf(RulesTypeKind.None); // empty: element type unknown, 'in' is vacuously false
            }

            RulesTypeKind unified = RulesTypeKind.None;
            foreach (ExpressionNode item in list.Items)
            {
                RulesType itemType = Visit(item);
                if (IsError(itemType))
                {
                    return _errorType;
                }

                RulesTypeKind? merged = MergeElementKinds(unified, itemType.Kind);
                if (merged is null)
                {
                    return Fail(DiagnosticCodes.MixedListLiteral,
                        $"list literal mixes {new RulesType(unified)} and {itemType} elements — all elements must be the same type",
                        list.Span, list.CanonicalText);
                }

                unified = merged.Value;
            }

            return RulesType.ListOf(unified);
        }

        private RulesType VisitMap(MapLiteralNode map)
        {
            if (map.Entries.Length == 0)
            {
                return RulesType.MapOf(RulesTypeKind.None); // empty: value type unknown, every lookup is null
            }

            RulesTypeKind unified = RulesTypeKind.None;
            foreach (MapEntry entry in map.Entries)
            {
                RulesType valueType = Visit(entry.Value);
                if (IsError(valueType))
                {
                    return _errorType;
                }

                // Map values are all-numbers or all-strings (spec §3.4); bool/null/mixed is a structural
                // error. MergeElementKinds accepts the numeric int→float widening and rejects the rest.
                RulesTypeKind? merged = valueType.Kind is RulesTypeKind.Int or RulesTypeKind.Float or RulesTypeKind.String
                    ? MergeElementKinds(unified, valueType.Kind)
                    : null;
                if (merged is null)
                {
                    return Fail(DiagnosticCodes.MixedMapLiteral,
                        $"map define value '{entry.Key}' is {valueType} — all map values must be the same type (all numbers or all strings)",
                        map.Span, map.CanonicalText);
                }

                unified = merged.Value;
            }

            return RulesType.MapOf(unified);
        }

        private static RulesTypeKind? MergeElementKinds(RulesTypeKind current, RulesTypeKind next)
        {
            if (current is RulesTypeKind.None || current == next)
            {
                return next;
            }

            return (current, next) switch
            {
                (RulesTypeKind.Int, RulesTypeKind.Float) or (RulesTypeKind.Float, RulesTypeKind.Int) =>
                    RulesTypeKind.Float,
                (RulesTypeKind.Int, RulesTypeKind.Duration) or (RulesTypeKind.Duration, RulesTypeKind.Int) =>
                    RulesTypeKind.Duration,
                _ => null
            };
        }

        private RulesType VisitMember(MemberAccessNode member)
        {
            RulesType targetType = Visit(member.Target);
            if (IsError(targetType))
            {
                return _errorType;
            }

            if (targetType.Kind == RulesTypeKind.List)
            {
                if (member.MemberName == "count")
                {
                    return RulesType.Int;
                }

                if (member.MemberName == "set")
                {
                    return RulesType.Bool;
                }
            }

            return member.MemberName == "set"
                ? Fail(DiagnosticCodes.SetNotSupported,
                    "'.set' is only available on capture stats and list stats", member.Span, member.MemberName)
                : Fail(DiagnosticCodes.UnknownMember,
                    $"a value of type {targetType} has no member '{member.MemberName}'", member.Span,
                    member.MemberName);
        }

        private RulesType VisitIndex(IndexAccessNode index)
        {
            RulesType targetType = Visit(index.Target);
            RulesType indexType = Visit(index.Index);
            if (IsError(targetType) || IsError(indexType))
            {
                return _errorType;
            }

            switch (targetType.Kind)
            {
                case RulesTypeKind.List when indexType.Kind == RulesTypeKind.Int:
                    // Bounds-checked element read; out of range evaluates to null (spec §3.4).
                    return targetType.ElementKind == RulesTypeKind.None
                        ? RulesType.Null // any element of the empty list literal is null
                        : new RulesType(targetType.ElementKind);

                case RulesTypeKind.List:
                    return Fail(DiagnosticCodes.IndexType,
                        $"list index must be int, got {indexType}", index.Index.Span, index.Index.CanonicalText);

                case RulesTypeKind.Map when indexType.Kind == RulesTypeKind.String:
                    return new RulesType(targetType.ElementKind);

                case RulesTypeKind.Map:
                    return Fail(DiagnosticCodes.IndexType,
                        $"map lookup key must be string, got {indexType}", index.Index.Span,
                        index.Index.CanonicalText);

                default:
                    return Fail(DiagnosticCodes.NotIndexable,
                        $"a value of type {targetType} cannot be indexed", index.Span, index.Target.CanonicalText);
            }
        }

        // ── Operators ────────────────────────────────────────────────────────────

        private RulesType VisitUnary(UnaryNode unary)
        {
            RulesType operandType = Visit(unary.Operand);
            if (IsError(operandType))
            {
                return _errorType;
            }

            if (unary.Operator == UnaryOperator.Not)
            {
                // null is treated as false by logical operators (spec §3.3).
                return operandType.Kind is RulesTypeKind.Bool or RulesTypeKind.Null
                    ? RulesType.Bool
                    : Fail(DiagnosticCodes.TypeMismatch,
                        $"'not' expects a bool operand, got {operandType}", unary.Span, unary.Operand.CanonicalText);
            }

            return operandType.Kind switch
            {
                RulesTypeKind.Int or RulesTypeKind.Float or RulesTypeKind.Duration => operandType,
                RulesTypeKind.Instant => Fail(DiagnosticCodes.TypeMismatch,
                    "an instant (a tick position) cannot be negated — subtract two instants to get a duration",
                    unary.Span, unary.Operand.CanonicalText),
                _ => Fail(DiagnosticCodes.TypeMismatch,
                    $"'-' cannot negate a value of type {operandType}", unary.Span, unary.Operand.CanonicalText)
            };
        }

        private RulesType VisitBinary(BinaryNode binary)
        {
            if (binary.Operator == BinaryOperator.In)
            {
                return VisitIn(binary);
            }

            RulesType left = Visit(binary.Left);
            RulesType right = Visit(binary.Right);
            if (IsError(left) || IsError(right))
            {
                return _errorType;
            }

            string display = OperatorText.Display(binary.Operator);

            // Whole list/map values never combine with operators (spec §3.4).
            if (IsContainer(left) || IsContainer(right))
            {
                RulesType container = IsContainer(left) ? left : right;
                string usage = container.Kind == RulesTypeKind.List
                    ? "use .count, [n], or .set"
                    : "use [key] lookup";
                return Fail(DiagnosticCodes.ListOperand,
                    $"a {container} value cannot be used with '{display}' — {usage}", binary.Span,
                    (IsContainer(left) ? binary.Left : binary.Right).CanonicalText);
            }

            switch (binary.Operator)
            {
                case BinaryOperator.And:
                case BinaryOperator.Or:
                {
                    if (left.Kind is not (RulesTypeKind.Bool or RulesTypeKind.Null))
                    {
                        return Fail(DiagnosticCodes.TypeMismatch,
                            $"'{display}' expects bool operands, but the left operand is {left}",
                            binary.Left.Span, binary.Left.CanonicalText);
                    }

                    if (right.Kind is not (RulesTypeKind.Bool or RulesTypeKind.Null))
                    {
                        return Fail(DiagnosticCodes.TypeMismatch,
                            $"'{display}' expects bool operands, but the right operand is {right}",
                            binary.Right.Span, binary.Right.CanonicalText);
                    }

                    return RulesType.Bool;
                }

                case BinaryOperator.Equal:
                case BinaryOperator.NotEqual:
                {
                    // The explicit null literal is the presence test (spec §3.3).
                    if (left.Kind == RulesTypeKind.Null || right.Kind == RulesTypeKind.Null)
                    {
                        return RulesType.Bool;
                    }

                    return EqualityComparable(left, right)
                        ? RulesType.Bool
                        : Fail(DiagnosticCodes.TypeMismatch,
                            $"'{display}' cannot compare {left} with {right}", binary.Span, binary.CanonicalText);
                }

                case BinaryOperator.Greater:
                case BinaryOperator.GreaterOrEqual:
                case BinaryOperator.Less:
                case BinaryOperator.LessOrEqual:
                {
                    if (left.Kind == RulesTypeKind.Null || right.Kind == RulesTypeKind.Null)
                    {
                        return Fail(DiagnosticCodes.NullUsage,
                            "null can only be tested with == or !=", binary.Span, binary.CanonicalText);
                    }

                    return OrderingComparable(left, right)
                        ? RulesType.Bool
                        : Fail(DiagnosticCodes.TypeMismatch,
                            $"'{display}' cannot compare {left} with {right}", binary.Span, binary.CanonicalText);
                }

                default:
                    return VisitArithmetic(binary, left, right, display);
            }
        }

        private RulesType VisitArithmetic(BinaryNode binary, RulesType left, RulesType right, string display)
        {
            if (left.Kind == RulesTypeKind.Null || right.Kind == RulesTypeKind.Null)
            {
                return Fail(DiagnosticCodes.NullUsage,
                    $"null cannot be used with '{display}' — test presence with == null / != null instead",
                    binary.Span, binary.CanonicalText);
            }

            RulesType? result = ArithmeticResult(binary.Operator, left.Kind, right.Kind);
            if (result is { } resolved)
            {
                return resolved;
            }

            // Targeted messages for the instructive failures.
            if (binary.Operator == BinaryOperator.Add
                && left.Kind == RulesTypeKind.Instant && right.Kind == RulesTypeKind.Instant)
            {
                return Fail(DiagnosticCodes.TypeMismatch,
                    "cannot add instant and instant — subtract two instants to get a duration, or add a duration to an instant",
                    binary.Span, binary.CanonicalText);
            }

            if (left.Kind == RulesTypeKind.String && right.Kind == RulesTypeKind.String
                                                  && binary.Operator == BinaryOperator.Add)
            {
                return Fail(DiagnosticCodes.TypeMismatch,
                    "strings cannot be concatenated with '+'", binary.Span, binary.CanonicalText);
            }

            return Fail(DiagnosticCodes.TypeMismatch,
                $"'{display}' cannot combine {left} and {right}", binary.Span, binary.CanonicalText);
        }

        /// <summary>
        ///     The spec §3.1/§3.2 arithmetic matrix. Int coerces one-way to float (numeric mix)
        ///     and to duration when mixed with time types in + and −; instant×/÷ and
        ///     duration-by-float scaling are type errors. Null = invalid combination.
        /// </summary>
        private static RulesType? ArithmeticResult(BinaryOperator op, RulesTypeKind left, RulesTypeKind right)
        {
            bool numeric = IsNumeric(left) && IsNumeric(right);
            RulesType numericResult = left == RulesTypeKind.Float || right == RulesTypeKind.Float
                ? RulesType.Float
                : RulesType.Int;

            switch (op)
            {
                case BinaryOperator.Add:
                    if (numeric)
                    {
                        return numericResult;
                    }

                    return (left, right) switch
                    {
                        (RulesTypeKind.Duration, RulesTypeKind.Duration) => RulesType.Duration,
                        (RulesTypeKind.Duration, RulesTypeKind.Int) => RulesType.Duration,
                        (RulesTypeKind.Int, RulesTypeKind.Duration) => RulesType.Duration,
                        (RulesTypeKind.Instant, RulesTypeKind.Duration) => RulesType.Instant,
                        (RulesTypeKind.Duration, RulesTypeKind.Instant) => RulesType.Instant,
                        (RulesTypeKind.Instant, RulesTypeKind.Int) => RulesType.Instant,
                        (RulesTypeKind.Int, RulesTypeKind.Instant) => RulesType.Instant,
                        _ => null
                    };

                case BinaryOperator.Subtract:
                    if (numeric)
                    {
                        return numericResult;
                    }

                    return (left, right) switch
                    {
                        (RulesTypeKind.Duration, RulesTypeKind.Duration) => RulesType.Duration,
                        (RulesTypeKind.Duration, RulesTypeKind.Int) => RulesType.Duration,
                        (RulesTypeKind.Int, RulesTypeKind.Duration) => RulesType.Duration,
                        (RulesTypeKind.Instant, RulesTypeKind.Instant) => RulesType.Duration,
                        (RulesTypeKind.Instant, RulesTypeKind.Duration) => RulesType.Instant,
                        (RulesTypeKind.Instant, RulesTypeKind.Int) => RulesType.Instant,
                        _ => null
                    };

                case BinaryOperator.Multiply:
                    if (numeric)
                    {
                        return numericResult;
                    }

                    // duration × / ÷ int scalar only (spec §3.1) — no float scaling.
                    return (left, right) switch
                    {
                        (RulesTypeKind.Duration, RulesTypeKind.Int) => RulesType.Duration,
                        (RulesTypeKind.Int, RulesTypeKind.Duration) => RulesType.Duration,
                        _ => null
                    };

                case BinaryOperator.Divide:
                    if (numeric)
                    {
                        return numericResult; // division by zero evaluates to null at runtime (spec §3.3)
                    }

                    return (left, right) switch
                    {
                        (RulesTypeKind.Duration, RulesTypeKind.Int) => RulesType.Duration,
                        _ => null
                    };

                case BinaryOperator.Modulo:
                    return numeric ? numericResult : null;

                default:
                    return null;
            }
        }

        private static bool IsNumeric(RulesTypeKind kind) => kind is RulesTypeKind.Int or RulesTypeKind.Float;

        private static bool EqualityComparable(RulesType left, RulesType right) =>
            IsNumeric(left.Kind) && IsNumeric(right.Kind)
            || left.Kind == right.Kind && left.Kind is RulesTypeKind.String or RulesTypeKind.Bool
                or RulesTypeKind.Duration or RulesTypeKind.Instant
            || TimeWithInt(left.Kind, right.Kind);

        private static bool OrderingComparable(RulesType left, RulesType right) =>
            IsNumeric(left.Kind) && IsNumeric(right.Kind)
            || left.Kind == right.Kind && left.Kind is RulesTypeKind.Duration or RulesTypeKind.Instant
            || TimeWithInt(left.Kind, right.Kind);

        private static bool TimeWithInt(RulesTypeKind left, RulesTypeKind right) =>
            left is RulesTypeKind.Duration or RulesTypeKind.Instant && right == RulesTypeKind.Int
            || left == RulesTypeKind.Int && right is RulesTypeKind.Duration or RulesTypeKind.Instant;

        private RulesType VisitIn(BinaryNode binary)
        {
            RulesType left = Visit(binary.Left);
            if (IsError(left))
            {
                // Still visit the right side so its diagnostics surface too.
                Visit(binary.Right);
                return _errorType;
            }

            if (IsContainer(left))
            {
                return Fail(DiagnosticCodes.ListOperand,
                    $"the left side of 'in' must be a scalar, got {left}", binary.Left.Span,
                    binary.Left.CanonicalText);
            }

            if (left.Kind == RulesTypeKind.Null)
            {
                return Fail(DiagnosticCodes.NullUsage,
                    "null can only be tested with == or !=", binary.Span, binary.CanonicalText);
            }

            RulesType right = Visit(binary.Right);
            if (IsError(right))
            {
                return _errorType;
            }

            if (right.Kind != RulesTypeKind.List)
            {
                return Fail(DiagnosticCodes.TypeMismatch,
                    $"the right side of 'in' must be a list, got {right}", binary.Right.Span,
                    binary.Right.CanonicalText);
            }

            if (right.ElementKind == RulesTypeKind.None)
            {
                return RulesType.Bool; // the empty list: 'in' is vacuously false at runtime
            }

            RulesType element = new(right.ElementKind);
            return EqualityComparable(left, element)
                ? RulesType.Bool
                : Fail(DiagnosticCodes.TypeMismatch,
                    $"'in' cannot look for a {left} value in a list of {element}", binary.Span,
                    binary.CanonicalText);
        }

        // ── Functions (spec §3.7) ────────────────────────────────────────────────

        private RulesType VisitCall(CallNode call)
        {
            string name = OperatorText.Name(call.Function);
            RulesType[] argumentTypes = new RulesType[call.Arguments.Length];
            for (int i = 0; i < call.Arguments.Length; i++)
            {
                argumentTypes[i] = Visit(call.Arguments[i]);
                if (IsError(argumentTypes[i]))
                {
                    return _errorType;
                }

                if (argumentTypes[i].Kind == RulesTypeKind.Null)
                {
                    return Fail(DiagnosticCodes.NullUsage,
                        $"null cannot be an argument of {name}()", call.Arguments[i].Span,
                        call.Arguments[i].CanonicalText);
                }
            }

            switch (call.Function)
            {
                case RuleFunction.Min:
                case RuleFunction.Max:
                {
                    RulesType a = argumentTypes[0];
                    RulesType b = argumentTypes[1];
                    if (IsNumeric(a.Kind) && IsNumeric(b.Kind))
                    {
                        return a.Kind == RulesTypeKind.Float || b.Kind == RulesTypeKind.Float
                            ? RulesType.Float
                            : RulesType.Int;
                    }

                    if ((a.Kind == RulesTypeKind.Duration || b.Kind == RulesTypeKind.Duration)
                        && a.Kind is RulesTypeKind.Duration or RulesTypeKind.Int
                        && b.Kind is RulesTypeKind.Duration or RulesTypeKind.Int)
                    {
                        return RulesType.Duration;
                    }

                    if (a.Kind == RulesTypeKind.Instant || b.Kind == RulesTypeKind.Instant)
                    {
                        return Fail(DiagnosticCodes.TypeMismatch,
                            $"{name}() does not accept instants (spec §3.7 signature: int, float, or duration)",
                            call.Span, call.CanonicalText);
                    }

                    return Fail(DiagnosticCodes.TypeMismatch,
                        $"{name}() expects two int, float, or duration arguments, got {a} and {b}",
                        call.Span, call.CanonicalText);
                }

                case RuleFunction.Abs:
                {
                    RulesType a = argumentTypes[0];
                    return a.Kind is RulesTypeKind.Int or RulesTypeKind.Float or RulesTypeKind.Duration
                        ? a
                        : Fail(DiagnosticCodes.TypeMismatch,
                            $"abs() expects an int, float, or duration argument, got {a}", call.Span,
                            call.CanonicalText);
                }

                case RuleFunction.Floor:
                {
                    RulesType a = argumentTypes[0];
                    return a.Kind is RulesTypeKind.Int or RulesTypeKind.Float or RulesTypeKind.Duration
                        ? a
                        : Fail(DiagnosticCodes.TypeMismatch,
                            $"floor() expects an int, float, or duration argument, got {a}", call.Span,
                            call.CanonicalText);
                }

                case RuleFunction.Contains:
                case RuleFunction.StartsWith:
                {
                    RulesType a = argumentTypes[0];
                    RulesType b = argumentTypes[1];
                    return a.Kind == RulesTypeKind.String && b.Kind == RulesTypeKind.String
                        ? RulesType.Bool
                        : Fail(DiagnosticCodes.TypeMismatch,
                            $"{name}() expects (string, string), got ({a}, {b})", call.Span, call.CanonicalText);
                }

                default:
                    throw new InvalidOperationException($"unknown function {call.Function}");
            }
        }
    }
}
