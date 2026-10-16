#region

using System.Globalization;
using System.Reflection;
using CS2DemoKit.Analysis.Rules;
using CS2DemoKit.Analysis.Rules.Ast;
using CS2DemoKit.Analysis.Rules.Parsing;
using CS2DemoKit.Parser.GameEvents;

#endregion

namespace CS2DemoKit.RulesCatalog;

/// <summary>
///     Folds a view's <c>baked:</c> filters over wire events, so <c>--measure</c> can count what a
///     view fires rather than what its event fires. The filters are the v2 expressions the resolver
///     conjoins into every trigger on the view; this is the small subset an editorial filter needs
///     (<c>event.&lt;Field&gt;</c>, literals, comparisons, <c>in</c>, <c>and</c>/<c>or</c>/<c>not</c>),
///     evaluated against the event payload's properties. Anything else fails the generator rather
///     than counting wrong.
/// </summary>
public static class BakedFilter
{
    /// <summary>Compiles <paramref name="baked" /> into one predicate over a wire event.</summary>
    /// <param name="baked">The view's filters, in <c>views.yaml</c> order; every one must hold.</param>
    /// <returns>The predicate.</returns>
    /// <exception cref="InvalidOperationException">A filter does not parse or uses a form this fold does not cover.</exception>
    public static Func<GameEvent, bool> Compile(IReadOnlyList<string> baked)
    {
        ArgumentNullException.ThrowIfNull(baked);
        List<ExpressionNode> filters = [];
        foreach (string text in baked)
        {
            LanguageResult<ExpressionNode> parsed = ExpressionParser.Parse(text);
            if (!parsed.Success || parsed.Value is null)
            {
                throw new InvalidOperationException(
                    $"baked filter '{text}' does not parse: {string.Join("; ", parsed.Diagnostics.Select(d => d.Message))}");
            }

            Check(parsed.Value);
            filters.Add(parsed.Value);
        }

        return gameEvent => filters.All(f => Truth(Evaluate(f, gameEvent), f));
    }

    // Rejects a form at compile, so a regen trips on it rather than the first --measure.
    private static void Check(ExpressionNode node)
    {
        switch (node)
        {
            case ReferenceNode { Segments: [ "event", _ ] }:
            case IntLiteralNode or FloatLiteralNode or StringLiteralNode or BoolLiteralNode:
                return;
            case UnaryNode { Operator: UnaryOperator.Not } u:
                Check(u.Operand);
                return;
            case BinaryNode { Operator: BinaryOperator.In, Right: ListLiteralNode list } b:
                Check(b.Left);
                foreach (ExpressionNode item in list.Items)
                {
                    Check(item);
                }

                return;
            case BinaryNode
            {
                Operator: BinaryOperator.And or BinaryOperator.Or or BinaryOperator.Equal or BinaryOperator.NotEqual
                or BinaryOperator.Greater or BinaryOperator.GreaterOrEqual or BinaryOperator.Less or BinaryOperator.LessOrEqual
            } b:
                Check(b.Left);
                Check(b.Right);
                return;
            default:
                throw Unsupported(node);
        }
    }

    private static object? Evaluate(ExpressionNode node, GameEvent gameEvent) =>
        node switch
        {
            ReferenceNode { Segments: [ "event", var field ] } => Field(gameEvent, field),
            IntLiteralNode i => (double)i.Value,
            FloatLiteralNode f => f.Value,
            StringLiteralNode s => s.Value,
            BoolLiteralNode b => b.Value,
            UnaryNode { Operator: UnaryOperator.Not } u => !Truth(Evaluate(u.Operand, gameEvent), u),
            BinaryNode { Operator: BinaryOperator.And } b =>
                Truth(Evaluate(b.Left, gameEvent), b) && Truth(Evaluate(b.Right, gameEvent), b),
            BinaryNode { Operator: BinaryOperator.Or } b =>
                Truth(Evaluate(b.Left, gameEvent), b) || Truth(Evaluate(b.Right, gameEvent), b),
            BinaryNode { Operator: BinaryOperator.In, Right: ListLiteralNode list } b =>
                list.Items.Any(item => Same(Evaluate(b.Left, gameEvent), Evaluate(item, gameEvent))),
            BinaryNode b => Compare(b.Operator, Evaluate(b.Left, gameEvent), Evaluate(b.Right, gameEvent), b),
            _ => throw Unsupported(node)
        };

    private static object? Compare(BinaryOperator op, object? left, object? right, ExpressionNode at) =>
        op switch
        {
            BinaryOperator.Equal => Same(left, right),
            BinaryOperator.NotEqual => !Same(left, right),
            BinaryOperator.Greater => Number(left, at) > Number(right, at),
            BinaryOperator.GreaterOrEqual => Number(left, at) >= Number(right, at),
            BinaryOperator.Less => Number(left, at) < Number(right, at),
            BinaryOperator.LessOrEqual => Number(left, at) <= Number(right, at),
            _ => throw Unsupported(at)
        };

    // A payload property reads as the number, string or bool a rule compares against.
    private static object? Field(GameEvent gameEvent, string field)
    {
        object payload = gameEvent.Payload
                         ?? throw new InvalidOperationException($"event '{gameEvent.Name}' has no payload to read '{field}' from");
        PropertyInfo property = payload.GetType().GetProperty(field, BindingFlags.Public | BindingFlags.Instance)
                                ?? throw new InvalidOperationException($"event '{gameEvent.Name}' has no field '{field}'");
        return property.GetValue(payload) switch
        {
            null => null,
            string s => s,
            bool b => b,
            IConvertible c => c.ToDouble(CultureInfo.InvariantCulture),
            var other => other
        };
    }

    private static bool Same(object? left, object? right) =>
        (left, right) switch
        {
            (double a, double b) => a == b,
            (string a, string b) => string.Equals(a, b, StringComparison.Ordinal),
            (bool a, bool b) => a == b,
            _ => Equals(left, right)
        };

    private static double Number(object? value, ExpressionNode at) =>
        value is double d ? d : throw new InvalidOperationException($"baked filter compares a non-number at '{at}'");

    private static bool Truth(object? value, ExpressionNode at) =>
        value is bool b ? b : throw new InvalidOperationException($"baked filter is not a bool at '{at}'");

    private static InvalidOperationException Unsupported(ExpressionNode node) =>
        new($"--measure cannot fold the baked filter form '{node.GetType().Name}' at {node.Span}; the fold covers "
            + "event fields, literals, comparisons, in over a list, and, or and not");
}
