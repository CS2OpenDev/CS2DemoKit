#region

using System.Collections.Immutable;
using CS2DemoKit.Analysis.Rules.Ast;

#endregion

namespace CS2DemoKit.Analysis.RulesetsV2.Resolve;

/// <summary>
///     Binds <c>params:</c> references to their literal values before hashing (spec §5
///     row 4). Runs as a resolver-side AST pre-pass over the parsed expression,
///     before the semantic-core normalizer. It rewrites every reference spelled <c>params.&lt;name&gt;</c>
///     (the pilot's qualified form) or bare <c>&lt;name&gt;</c> where the name is a declared param
///     (the spec §4 namespace-tree form) into the param's literal node — so two installs of a
///     blueprint with different param values produce different canonical ASTs, as the spec requires.
///     <para>
///         This substitutes for the contract's stated "via <c>NormalizerOptions.DefineLookup</c>"
///         mechanism, which cannot express the qualified <c>params.&lt;name&gt;</c> spelling: the
///         normalizer's define lookup is keyed on a reference's <b>head</b> segment, and a param
///         literal cannot take the <c>.min_kills</c> member tail a <c>params</c>-headed reference
///         carries. A dedicated pre-pass achieves decision 2's effect (literals before hashing)
///         where the stated mechanism cannot.
///     </para>
/// </summary>
public static class ParamInliner
{
    /// <summary>Rewrites param references in an AST to their literal values.</summary>
    /// <param name="node">The parsed AST root.</param>
    /// <param name="paramLiterals">The param name → literal node substitution table.</param>
    /// <returns>The rewritten AST (structurally shared where nothing changed).</returns>
    public static ExpressionNode Inline(ExpressionNode node, IReadOnlyDictionary<string, ExpressionNode> paramLiterals)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(paramLiterals);
        return paramLiterals.Count == 0 ? node : Rewrite(node, paramLiterals);
    }

    private static ExpressionNode Rewrite(ExpressionNode node, IReadOnlyDictionary<string, ExpressionNode> paramLiterals)
    {
        switch (node)
        {
            case ReferenceNode reference:
                return ResolveParam(reference, paramLiterals) ?? reference;

            case UnaryNode unary:
            {
                ExpressionNode operand = Rewrite(unary.Operand, paramLiterals);
                return ReferenceEquals(operand, unary.Operand)
                    ? unary
                    : new UnaryNode(unary.Operator, operand, unary.Span);
            }

            case BinaryNode binary:
            {
                ExpressionNode left = Rewrite(binary.Left, paramLiterals);
                ExpressionNode right = Rewrite(binary.Right, paramLiterals);
                return ReferenceEquals(left, binary.Left) && ReferenceEquals(right, binary.Right)
                    ? binary
                    : new BinaryNode(binary.Operator, left, right, binary.Span);
            }

            case MemberAccessNode member:
            {
                ExpressionNode target = Rewrite(member.Target, paramLiterals);
                return ReferenceEquals(target, member.Target)
                    ? member
                    : new MemberAccessNode(target, member.MemberName, member.Span);
            }

            case IndexAccessNode index:
            {
                ExpressionNode target = Rewrite(index.Target, paramLiterals);
                ExpressionNode indexExpression = Rewrite(index.Index, paramLiterals);
                return ReferenceEquals(target, index.Target) && ReferenceEquals(indexExpression, index.Index)
                    ? index
                    : new IndexAccessNode(target, indexExpression, index.Span);
            }

            case ListLiteralNode list:
            {
                ImmutableArray<ExpressionNode>.Builder items =
                    ImmutableArray.CreateBuilder<ExpressionNode>(list.Items.Length);
                bool changed = false;
                foreach (ExpressionNode item in list.Items)
                {
                    ExpressionNode rewritten = Rewrite(item, paramLiterals);
                    changed |= !ReferenceEquals(rewritten, item);
                    items.Add(rewritten);
                }

                return changed ? new ListLiteralNode(items.MoveToImmutable(), list.Span) : list;
            }

            case CallNode call:
            {
                ImmutableArray<ExpressionNode>.Builder arguments =
                    ImmutableArray.CreateBuilder<ExpressionNode>(call.Arguments.Length);
                bool changed = false;
                foreach (ExpressionNode argument in call.Arguments)
                {
                    ExpressionNode rewritten = Rewrite(argument, paramLiterals);
                    changed |= !ReferenceEquals(rewritten, argument);
                    arguments.Add(rewritten);
                }

                return changed ? new CallNode(call.Function, arguments.MoveToImmutable(), call.Span) : call;
            }

            default:
                return node;
        }
    }

    private static ExpressionNode? ResolveParam(ReferenceNode reference,
        IReadOnlyDictionary<string, ExpressionNode> paramLiterals)
    {
        ImmutableArray<string> segments = reference.Segments;

        // Qualified form: params.<name> (exactly two segments).
        if (segments.Length == 2 && string.Equals(segments[0], "params", StringComparison.Ordinal)
                                 && paramLiterals.TryGetValue(segments[1], out ExpressionNode? qualified))
        {
            return qualified;
        }

        // Bare form: <name> where <name> is a declared param (spec §4 namespace tree).
        return segments.Length == 1 && paramLiterals.TryGetValue(segments[0], out ExpressionNode? bare)
            ? bare
            : null;
    }
}
