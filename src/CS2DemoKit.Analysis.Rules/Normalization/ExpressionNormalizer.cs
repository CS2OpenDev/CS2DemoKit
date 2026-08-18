#region

using System.Collections.Immutable;
using CS2DemoKit.Analysis.Rules.Ast;
using CS2DemoKit.Analysis.Rules.Lexing;

#endregion

namespace CS2DemoKit.Analysis.Rules.Normalization;

/// <summary>
///     The spec §5 normalizer — deliberately conservative, token-level and structural only:
///     duration literals fold to int tick constants (row 3), <c>define:</c>s inline at their
///     use sites (row 4), and <c>match:</c> bindings lower to their <c>where:</c>-equivalent
///     comparisons in fixed key order (row 5). Whitespace/word forms vanished at lexing and
///     parentheses at parsing (rows 1–2). There is <em>no</em> constant arithmetic folding,
///     operand reordering, or De Morgan rewriting (row 6): hash-equal must mean behaviorally
///     interchangeable under reference-identity node sharing. The one extra rewrite is sign
///     folding of a literal operand of unary minus (mirroring the parser, so
///     <c>-0.5s</c> ≡ <c>-32</c>) — sign folding is spelling-level, not arithmetic.
/// </summary>
public static class ExpressionNormalizer
{
    /// <summary>Normalizes an expression AST. The result is the hashing form of spec §6 row 5.</summary>
    /// <param name="root">The parsed AST root.</param>
    /// <param name="options">Environment inputs; null uses <see cref="NormalizerOptions.Default" />.</param>
    /// <returns>The normalized AST, or diagnostics (define cycles / misused defines).</returns>
    public static LanguageResult<ExpressionNode> Normalize(ExpressionNode root, NormalizerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(root);
        options ??= NormalizerOptions.Default;

        List<Diagnostic> diagnostics = [];
        List<string> activeDefines = [];
        ExpressionNode? normalized = Rewrite(root, options, diagnostics, activeDefines);

        return diagnostics.Count > 0 || normalized is null
            ? LanguageResult.Fail<ExpressionNode>(diagnostics)
            : LanguageResult.Ok(normalized);
    }

    /// <summary>
    ///     Builds the canonical condition AST for a slot that combines structured
    ///     <c>match:</c> bindings with an optional free-form <c>where:</c> expression
    ///     (spec §5 row 5). Bindings are lowered through
    ///     <see cref="NormalizerOptions.MatchBindingLowering" />, ordered by the view's fixed
    ///     catalog key order, conjoined left-associatively, and the <c>where:</c> expression
    ///     is appended as the final conjunct — so the structured and free-form spellings of
    ///     the same constraint produce the identical node.
    /// </summary>
    /// <param name="bindings">The structured bindings; may be empty.</param>
    /// <param name="whereExpression">The free-form condition, or null when the slot has none.</param>
    /// <param name="options">Environment inputs; must carry a lowering hook when bindings are present.</param>
    /// <returns>The normalized conjunction, or diagnostics.</returns>
    /// <exception cref="InvalidOperationException">
    ///     Bindings were supplied without a <see cref="NormalizerOptions.MatchBindingLowering" /> hook
    ///     (programmer misuse — the loader always knows the view's catalog).
    /// </exception>
    /// <exception cref="ArgumentException">Neither bindings nor a where-expression were supplied.</exception>
    public static LanguageResult<ExpressionNode> NormalizeMatchBindings(IReadOnlyList<MatchBinding> bindings,
        ExpressionNode? whereExpression, NormalizerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        options ??= NormalizerOptions.Default;

        if (bindings.Count == 0)
        {
            return whereExpression is null
                ? throw new ArgumentException("need at least one match: binding or a where: expression",
                    nameof(bindings))
                : Normalize(whereExpression, options);
        }

        IMatchBindingLowering lowering = options.MatchBindingLowering
                                         ?? throw new InvalidOperationException(
                                             "match: bindings need NormalizerOptions.MatchBindingLowering (the view's catalog key table)");

        List<Diagnostic> diagnostics = [];
        List<(int Order, ExpressionNode Lowered)> lowered = [];
        foreach (MatchBinding binding in bindings)
        {
            if (!lowering.TryLower(binding, out ExpressionNode? comparison, out int order))
            {
                diagnostics.Add(new Diagnostic(DiagnosticCodes.UnknownMatchKey,
                    $"unknown match: key '{binding.Key}' for this view", binding.Value.Span, binding.Key));
                continue;
            }

            lowered.Add((order, comparison));
        }

        if (diagnostics.Count > 0)
        {
            return LanguageResult.Fail<ExpressionNode>(diagnostics);
        }

        ExpressionNode? conjunction = null;
        foreach ((_, ExpressionNode comparison) in lowered.OrderBy(entry => entry.Order))
        {
            LanguageResult<ExpressionNode> normalized = Normalize(comparison, options);
            if (!normalized.Success)
            {
                return normalized;
            }

            ExpressionNode next = normalized.Require();
            conjunction = conjunction is null ? next : new BinaryNode(BinaryOperator.And, conjunction, next);
        }

        if (whereExpression is not null)
        {
            LanguageResult<ExpressionNode> normalizedWhere = Normalize(whereExpression, options);
            if (!normalizedWhere.Success)
            {
                return normalizedWhere;
            }

            ExpressionNode where = normalizedWhere.Require();
            conjunction = conjunction is null ? where : new BinaryNode(BinaryOperator.And, conjunction, where);
        }

        return LanguageResult.Ok(conjunction!);
    }

    private static ExpressionNode? Rewrite(ExpressionNode node, NormalizerOptions options,
        List<Diagnostic> diagnostics, List<string> activeDefines)
    {
        switch (node)
        {
            case DurationLiteralNode duration:
                return new IntLiteralNode(FoldTicks(duration, options), duration.Span);

            case ReferenceNode reference:
                return RewriteReference(reference, options, diagnostics, activeDefines);

            case UnaryNode unary:
            {
                ExpressionNode? operand = Rewrite(unary.Operand, options, diagnostics, activeDefines);
                if (operand is null)
                {
                    return null;
                }

                if (unary.Operator == UnaryOperator.Negate)
                {
                    // Sign folding on literals (mirrors the parser): '-(0.5s)' ≡ '-32'. Not
                    // arithmetic folding — the value written is the value stored.
                    switch (operand)
                    {
                        case IntLiteralNode i:
                            return new IntLiteralNode(-i.Value, unary.Span);
                        case FloatLiteralNode f:
                            return new FloatLiteralNode(-f.Value, unary.Span);
                    }
                }

                return ReferenceEquals(operand, unary.Operand)
                    ? unary
                    : new UnaryNode(unary.Operator, operand, unary.Span);
            }

            case BinaryNode binary:
            {
                ExpressionNode? left = Rewrite(binary.Left, options, diagnostics, activeDefines);
                ExpressionNode? right = Rewrite(binary.Right, options, diagnostics, activeDefines);
                if (left is null || right is null)
                {
                    return null;
                }

                return ReferenceEquals(left, binary.Left) && ReferenceEquals(right, binary.Right)
                    ? binary
                    : new BinaryNode(binary.Operator, left, right, binary.Span);
            }

            case MemberAccessNode member:
            {
                ExpressionNode? target = Rewrite(member.Target, options, diagnostics, activeDefines);
                if (target is null)
                {
                    return null;
                }

                return ReferenceEquals(target, member.Target)
                    ? member
                    : new MemberAccessNode(target, member.MemberName, member.Span);
            }

            case IndexAccessNode index:
            {
                ExpressionNode? target = Rewrite(index.Target, options, diagnostics, activeDefines);
                ExpressionNode? indexExpression = Rewrite(index.Index, options, diagnostics, activeDefines);
                if (target is null || indexExpression is null)
                {
                    return null;
                }

                return ReferenceEquals(target, index.Target) && ReferenceEquals(indexExpression, index.Index)
                    ? index
                    : new IndexAccessNode(target, indexExpression, index.Span);
            }

            case ListLiteralNode list:
            {
                ImmutableArray<ExpressionNode>.Builder items = ImmutableArray.CreateBuilder<ExpressionNode>(list.Items.Length);
                bool changed = false;
                foreach (ExpressionNode item in list.Items)
                {
                    ExpressionNode? rewritten = Rewrite(item, options, diagnostics, activeDefines);
                    if (rewritten is null)
                    {
                        return null;
                    }

                    changed |= !ReferenceEquals(rewritten, item);
                    items.Add(rewritten);
                }

                return changed ? new ListLiteralNode(items.MoveToImmutable(), list.Span) : list;
            }

            case MapLiteralNode map:
            {
                ImmutableArray<MapEntry>.Builder entries = ImmutableArray.CreateBuilder<MapEntry>(map.Entries.Length);
                bool changed = false;
                foreach (MapEntry entry in map.Entries)
                {
                    ExpressionNode? rewritten = Rewrite(entry.Value, options, diagnostics, activeDefines);
                    if (rewritten is null)
                    {
                        return null;
                    }

                    changed |= !ReferenceEquals(rewritten, entry.Value);
                    entries.Add(entry with
                    {
                        Value = rewritten
                    });
                }

                return changed ? new MapLiteralNode(entries.MoveToImmutable(), map.Span) : map;
            }

            case CallNode call:
            {
                ImmutableArray<ExpressionNode>.Builder arguments = ImmutableArray.CreateBuilder<ExpressionNode>(call.Arguments.Length);
                bool changed = false;
                foreach (ExpressionNode argument in call.Arguments)
                {
                    ExpressionNode? rewritten = Rewrite(argument, options, diagnostics, activeDefines);
                    if (rewritten is null)
                    {
                        return null;
                    }

                    changed |= !ReferenceEquals(rewritten, argument);
                    arguments.Add(rewritten);
                }

                return changed ? new CallNode(call.Function, arguments.MoveToImmutable(), call.Span) : call;
            }

            default:
                return node; // scalar literals are already canonical
        }
    }

    private static ExpressionNode? RewriteReference(ReferenceNode reference, NormalizerOptions options,
        List<Diagnostic> diagnostics, List<string> activeDefines)
    {
        string head = reference.Segments[0];
        ExpressionNode? body = options.DefineLookup?.Invoke(head);
        if (body is null)
        {
            return reference; // not a define — resolves against the scope environment later
        }

        if (activeDefines.Contains(head, StringComparer.Ordinal))
        {
            diagnostics.Add(new Diagnostic(DiagnosticCodes.DefineCycle,
                $"define '{head}' expands through itself — the chain {string.Join(" -> ", activeDefines)} -> {head} is a cycle",
                reference.Span, head));
            return null;
        }

        activeDefines.Add(head);

        try
        {
            if (reference.Segments.Length == 1)
            {
                return Rewrite(body, options, diagnostics, activeDefines);
            }

            // 'mydefine.member' works only when the define's body is itself a reference —
            // then the segments splice and resolution continues from the spliced path.
            if (body is ReferenceNode bodyReference)
            {
                ReferenceNode spliced = new(
                    bodyReference.Segments.AddRange(reference.Segments.RemoveAt(0)), reference.Span);
                return Rewrite(spliced, options, diagnostics, activeDefines);
            }

            diagnostics.Add(new Diagnostic(DiagnosticCodes.DefineMemberAccess,
                $"define '{head}' expands to an expression, so '{reference.Path}' cannot reach members through it",
                reference.Span, reference.Path));
            return null;
        }
        finally
        {
            activeDefines.RemoveAt(activeDefines.Count - 1);
        }
    }

    private static long FoldTicks(DurationLiteralNode duration, NormalizerOptions options)
    {
        double ticksPerUnit = duration.Unit == DurationUnit.Milliseconds
            ? options.TicksPerSecond / 1000.0
            : options.TicksPerSecond;
        return (long)Math.Round(duration.Magnitude * ticksPerUnit, MidpointRounding.AwayFromZero);
    }
}
