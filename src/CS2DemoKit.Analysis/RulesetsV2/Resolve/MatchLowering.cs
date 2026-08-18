#region

using System.Collections.Immutable;
using System.Globalization;
using CS2DemoKit.Analysis.Rules.Ast;
using CS2DemoKit.Analysis.RulesetsV2.Model;

#endregion

namespace CS2DemoKit.Analysis.RulesetsV2.Resolve;

/// <summary>
///     Lowers a structured <c>match:</c> unary test to its <c>where:</c>-equivalent comparison AST
///     (spec §5 row 5): the facet's resolved underlying read (from
///     <see cref="CatalogScopeAdapter.FacetRead" />) becomes the left operand and the test's shape
///     becomes the operator + right operand. So <c>match: { enemy: true }</c> and
///     <c>where: "enemy == true"</c> canonicalize to the identical node.
/// </summary>
public static class MatchLowering
{
    /// <summary>Lowers a unary test against a facet read into a comparison expression.</summary>
    /// <param name="facetRead">The facet's underlying read (LHS).</param>
    /// <param name="test">The parsed unary test (RHS shape).</param>
    /// <returns>The comparison AST.</returns>
    /// <exception cref="InvalidOperationException">
    ///     The unary test carries an unparseable literal (structurally impossible
    ///     after 2.2a).
    /// </exception>
    public static ExpressionNode Lower(ExpressionNode facetRead, UnaryTest test)
    {
        ArgumentNullException.ThrowIfNull(facetRead);
        ArgumentNullException.ThrowIfNull(test);

        return test switch
        {
            LiteralTest literal =>
                new BinaryNode(BinaryOperator.Equal, facetRead, Scalar(literal.RawText, literal.Kind)),
            ComparisonTest comparison =>
                new BinaryNode(Operator(comparison.Operator), facetRead,
                    Scalar(comparison.LiteralRawText, comparison.LiteralKind)),
            RangeTest range =>
                new BinaryNode(BinaryOperator.And,
                    new BinaryNode(BinaryOperator.GreaterOrEqual, facetRead, new IntLiteralNode(range.Low)),
                    new BinaryNode(BinaryOperator.LessOrEqual, facetRead, new IntLiteralNode(range.High))),
            InListRefTest listRef =>
                new BinaryNode(BinaryOperator.In, facetRead, new ReferenceNode([listRef.ListRef])),
            InListLiteralTest listLiteral =>
                new BinaryNode(BinaryOperator.In, facetRead, ListLiteral(listLiteral.Items)),
            _ => throw new InvalidOperationException($"unhandled unary test {test.GetType().Name}")
        };
    }

    private static ListLiteralNode ListLiteral(IReadOnlyList<string> items)
    {
        ImmutableArray<ExpressionNode>.Builder builder = ImmutableArray.CreateBuilder<ExpressionNode>(items.Count);
        foreach (string item in items)
        {
            builder.Add(ScalarFromText(item));
        }

        return new ListLiteralNode(builder.MoveToImmutable());
    }

    private static ExpressionNode Scalar(string rawText, ScalarKind kind) =>
        kind switch
        {
            ScalarKind.Bool => new BoolLiteralNode(string.Equals(rawText, "true", StringComparison.Ordinal)),
            ScalarKind.Int => new IntLiteralNode(long.Parse(rawText, CultureInfo.InvariantCulture)),
            ScalarKind.Float => new FloatLiteralNode(double.Parse(rawText, CultureInfo.InvariantCulture)),
            _ => new StringLiteralNode(StripQuotes(rawText))
        };

    /// <summary>Infers a scalar literal from an inline-list element's raw text (int → float → bool → string).</summary>
    private static ExpressionNode ScalarFromText(string rawText)
    {
        if (long.TryParse(rawText, NumberStyles.Integer, CultureInfo.InvariantCulture, out long asLong))
        {
            return new IntLiteralNode(asLong);
        }

        if (double.TryParse(rawText, NumberStyles.Float, CultureInfo.InvariantCulture, out double asDouble))
        {
            return new FloatLiteralNode(asDouble);
        }

        return rawText switch
        {
            "true" => new BoolLiteralNode(true),
            "false" => new BoolLiteralNode(false),
            _ => new StringLiteralNode(StripQuotes(rawText))
        };
    }

    private static string StripQuotes(string text) =>
        text.Length >= 2 && text[0] == '"' && text[^1] == '"' ? text[1..^1] : text;

    private static BinaryOperator Operator(ComparisonOperator op) =>
        op switch
        {
            ComparisonOperator.Equal => BinaryOperator.Equal,
            ComparisonOperator.NotEqual => BinaryOperator.NotEqual,
            ComparisonOperator.Greater => BinaryOperator.Greater,
            ComparisonOperator.GreaterOrEqual => BinaryOperator.GreaterOrEqual,
            ComparisonOperator.Less => BinaryOperator.Less,
            ComparisonOperator.LessOrEqual => BinaryOperator.LessOrEqual,
            _ => BinaryOperator.Equal
        };
}
