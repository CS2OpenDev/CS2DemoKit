#region

using System.Globalization;
using System.Text;
using CS2DemoKit.Analysis.Rules.Ast;

#endregion

namespace CS2DemoKit.Analysis.RulesetsV2.Compile;

/// <summary>
///     Serializes a checked v2 expression AST back into the <b>v1 infix condition/value grammar</b>
///     that <c>ExpressionCompiler</c> parses. This is the planner's runtime lowering seam: rather
///     than re-implement an interpreter over the AST, the planner writes the resolved expression
///     out as a v1 string and feeds it to the same <c>CompileEventCondition</c> /
///     <c>CompileEventValueSelector</c> path v1 uses — so a v2 node evaluates byte-identically to
///     the v1 rule it replaces (the pilot-golden parity guarantee). The v2 reference paths already
///     resolve to v1-compatible names (<c>enrich.kill.was_enemy_kill</c>, <c>event.Attacker</c>,
///     <c>player.slot</c>) because the resolver lowered facets through
///     <c>CatalogScopeAdapter.FacetRead</c>; the only rewrites needed are the loader-injected
///     instants (<c>event.tick</c> has no wire field — it maps to the event's <c>ServerTick</c>,
///     the exact field v1 captured).
/// </summary>
public static class V1ExpressionWriter
{
    /// <summary>
    ///     Writes a checked expression AST as a v1 infix string. Reference paths are emitted
    ///     verbatim except the injected instants, which are rewritten to their v1 wire fields.
    /// </summary>
    /// <param name="node">The (normalized) AST root.</param>
    /// <returns>The v1-grammar expression text.</returns>
    public static string Write(ExpressionNode node) => Write(node, null);

    /// <summary>
    ///     Writes a checked expression AST as a v1 infix string, optionally lowering context reference
    ///     paths to their v1 rule ids first. <paramref name="contextV2ToV1" /> is the planner's catalog
    ///     v2Name→ruleId table: a reference whose path is a key (e.g. <c>player.survived</c>,
    ///     <c>round.enemies.alive</c>) is emitted as the bare v1 rule id (<c>survived</c>,
    ///     <c>round_enemies_alive</c>) so <c>ExpressionCompiler</c> resolves it through its node-lookup
    ///     fallback against the subject slot's per-player node — the seam that lets a per-player
    ///     context / B6 aggregate be read inside a <c>where:</c> event-condition (pre-freeze gap G1,
    ///     event-gated per-player aggregate reads). Passing <c>null</c> preserves the verbatim v1
    ///     behaviour (the v1 rule path never remaps).
    /// </summary>
    /// <param name="node">The (normalized) AST root.</param>
    /// <param name="contextV2ToV1">Catalog v2Name→ruleId map, or <c>null</c> for no context lowering.</param>
    /// <returns>The v1-grammar expression text.</returns>
    public static string Write(ExpressionNode node, IReadOnlyDictionary<string, string>? contextV2ToV1)
    {
        ArgumentNullException.ThrowIfNull(node);
        StringBuilder sb = new();
        Write(node, sb, contextV2ToV1);
        return sb.ToString();
    }

    private static void Write(ExpressionNode node, StringBuilder sb,
        IReadOnlyDictionary<string, string>? contextV2ToV1)
    {
        switch (node)
        {
            case ReferenceNode reference:
                sb.Append(MapReferencePath(
                    contextV2ToV1 is not null && contextV2ToV1.TryGetValue(reference.Path, out string? v1Id)
                        ? v1Id
                        : reference.Path));
                break;
            case IntLiteralNode intLiteral:
                sb.Append(intLiteral.Value.ToString(CultureInfo.InvariantCulture));
                break;
            case FloatLiteralNode floatLiteral:
                sb.Append(floatLiteral.Value.ToString("R", CultureInfo.InvariantCulture));
                break;
            case BoolLiteralNode boolLiteral:
                sb.Append(boolLiteral.Value ? "true" : "false");
                break;
            case StringLiteralNode stringLiteral:
                sb.Append('"').Append(stringLiteral.Value).Append('"');
                break;
            case NullLiteralNode:
                sb.Append("null");
                break;
            case UnaryNode unary:
                sb.Append(unary.Operator == UnaryOperator.Not ? "!" : "-").Append('(');
                Write(unary.Operand, sb, contextV2ToV1);
                sb.Append(')');
                break;
            case BinaryNode binary:
                sb.Append('(');
                Write(binary.Left, sb, contextV2ToV1);
                sb.Append(' ').Append(OperatorText(binary.Operator)).Append(' ');
                Write(binary.Right, sb, contextV2ToV1);
                sb.Append(')');
                break;
            case ListLiteralNode list:
                sb.Append('[');
                for (int i = 0; i < list.Items.Length; i++)
                {
                    if (i > 0)
                    {
                        sb.Append(", ");
                    }

                    Write(list.Items[i], sb, contextV2ToV1);
                }

                sb.Append(']');
                break;
            case MapLiteralNode map:
                // A string-keyed lookup table (inlined map define). Emitted as `{"k": v, …}` — the v1
                // ExpressionCompiler parses this into a dictionary and evaluates the enclosing `[key]`
                // subscript (IndexAccessNode) as a lookup returning the value or null on a miss.
                sb.Append('{');
                for (int i = 0; i < map.Entries.Length; i++)
                {
                    if (i > 0)
                    {
                        sb.Append(", ");
                    }

                    sb.Append('"').Append(map.Entries[i].Key).Append("\": ");
                    Write(map.Entries[i].Value, sb, contextV2ToV1);
                }

                sb.Append('}');
                break;
            case IndexAccessNode index:
                Write(index.Target, sb, contextV2ToV1);
                sb.Append('[');
                Write(index.Index, sb, contextV2ToV1);
                sb.Append(']');
                break;
            case CallNode call:
                sb.Append(FunctionText(call.Function)).Append('(');
                for (int i = 0; i < call.Arguments.Length; i++)
                {
                    if (i > 0)
                    {
                        sb.Append(", ");
                    }

                    Write(call.Arguments[i], sb, contextV2ToV1);
                }

                sb.Append(')');
                break;
            case MemberAccessNode member:
                Write(member.Target, sb, contextV2ToV1);
                sb.Append('.').Append(member.MemberName);
                break;
            default:
                throw new InvalidOperationException(
                    $"V1ExpressionWriter: unhandled AST node {node.GetType().Name}");
        }
    }

    /// <summary>
    ///     Rewrites a resolved reference path to its v1 wire spelling. The loader-injected
    ///     <c>event.tick</c> instant has no catalog field, so it maps to <c>event.ServerTick</c> —
    ///     the true server tick v1's <c>pp_plant_tick</c>/<c>pp_kill_tick_N</c> captured. Everything
    ///     else (enrichment outputs, event fields, <c>player.slot</c>) already carries its v1 name.
    /// </summary>
    /// <param name="path">The resolved dotted reference path.</param>
    /// <returns>The v1 reference spelling.</returns>
    public static string MapReferencePath(string path) =>
        path switch
        {
            "event.tick" => "event.ServerTick",
            _ => path
        };

    private static string OperatorText(BinaryOperator op) =>
        op switch
        {
            BinaryOperator.Or => "||",
            BinaryOperator.And => "&&",
            BinaryOperator.Equal => "==",
            BinaryOperator.NotEqual => "!=",
            BinaryOperator.Greater => ">",
            BinaryOperator.GreaterOrEqual => ">=",
            BinaryOperator.Less => "<",
            BinaryOperator.LessOrEqual => "<=",
            BinaryOperator.Add => "+",
            BinaryOperator.Subtract => "-",
            BinaryOperator.Multiply => "*",
            BinaryOperator.Divide => "/",
            BinaryOperator.Modulo => "%",
            BinaryOperator.In => "in",
            _ => throw new InvalidOperationException($"V1ExpressionWriter: unhandled operator {op}")
        };

    private static string FunctionText(RuleFunction function) =>
        function switch
        {
            RuleFunction.Min => "min",
            RuleFunction.Max => "max",
            RuleFunction.Abs => "abs",
            RuleFunction.Contains => "contains",
            RuleFunction.StartsWith => "startswith",
            RuleFunction.Floor => "floor",
            _ => throw new InvalidOperationException($"V1ExpressionWriter: unhandled function {function}")
        };
}
