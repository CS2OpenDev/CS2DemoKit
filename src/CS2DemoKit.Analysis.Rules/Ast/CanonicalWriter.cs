#region

using System.Globalization;
using System.Text;
using CS2DemoKit.Analysis.Rules.Lexing;

#endregion

namespace CS2DemoKit.Analysis.Rules.Ast;

/// <summary>
///     Deterministic canonical serialization of the AST: a parenthesized prefix form with
///     culture-invariant number formatting. This text is the node-equality witness and the
///     spec §6 row 5 hash payload (the resolved-identity hasher swaps stat references for
///     their referenced node hashes on top of this form — see <c>ExpressionHasher</c>).
/// </summary>
internal static class CanonicalWriter
{
    /// <summary>Serializes a node to its canonical text.</summary>
    /// <param name="node">The node to serialize.</param>
    /// <returns>The canonical prefix form.</returns>
    internal static string Write(ExpressionNode node)
    {
        StringBuilder text = new();
        Append(text, node);
        return text.ToString();
    }

    /// <summary>Appends a node's canonical form to a builder (shared with the hasher's variant writer).</summary>
    /// <param name="text">The target builder.</param>
    /// <param name="node">The node to serialize.</param>
    /// <param name="writeReference">
    ///     Optional replacement writer for <see cref="ReferenceNode" /> — the resolved-identity
    ///     hasher uses it to substitute stat hashes for stat reference names (spec §6 row 6).
    ///     Null uses the plain <c>(ref path)</c> form.
    /// </param>
    internal static void Append(StringBuilder text, ExpressionNode node,
        Action<StringBuilder, ReferenceNode>? writeReference = null)
    {
        switch (node)
        {
            case IntLiteralNode i:
                text.Append(CultureInfo.InvariantCulture, $"(int {i.Value})");
                break;

            case FloatLiteralNode f:
                text.Append("(float ").Append(f.Value.ToString(CultureInfo.InvariantCulture)).Append(')');
                break;

            case StringLiteralNode s:
                text.Append("(str \"").Append(Escape(s.Value)).Append("\")");
                break;

            case BoolLiteralNode b:
                text.Append(b.Value ? "(bool true)" : "(bool false)");
                break;

            case NullLiteralNode:
                text.Append("(null)");
                break;

            case DurationLiteralNode d:
                text.Append("(dur ").Append(d.Magnitude.ToString(CultureInfo.InvariantCulture))
                    .Append(d.Unit == DurationUnit.Milliseconds ? " ms)" : " s)");
                break;

            case ReferenceNode r:
                if (writeReference is null)
                {
                    text.Append("(ref ").Append(r.Path).Append(')');
                }
                else
                {
                    writeReference(text, r);
                }

                break;

            case MemberAccessNode m:
                text.Append("(member ");
                Append(text, m.Target, writeReference);
                text.Append(' ').Append(m.MemberName).Append(')');
                break;

            case IndexAccessNode x:
                text.Append("(index ");
                Append(text, x.Target, writeReference);
                text.Append(' ');
                Append(text, x.Index, writeReference);
                text.Append(')');
                break;

            case UnaryNode u:
                text.Append(u.Operator == UnaryOperator.Not ? "(not " : "(neg ");
                Append(text, u.Operand, writeReference);
                text.Append(')');
                break;

            case BinaryNode b:
                text.Append('(').Append(OperatorText.CanonicalTag(b.Operator)).Append(' ');
                Append(text, b.Left, writeReference);
                text.Append(' ');
                Append(text, b.Right, writeReference);
                text.Append(')');
                break;

            case ListLiteralNode l:
                text.Append("(list");
                foreach (ExpressionNode item in l.Items)
                {
                    text.Append(' ');
                    Append(text, item, writeReference);
                }

                text.Append(')');
                break;

            case MapLiteralNode m:
                // Entries sorted by key so a map's identity is independent of author key order
                // ({a:1, b:2} ≡ {b:2, a:1}); values ride along as canonical literals.
                text.Append("(map");
                foreach (MapEntry entry in m.Entries.OrderBy(e => e.Key, StringComparer.Ordinal))
                {
                    text.Append(" (entry \"").Append(Escape(entry.Key)).Append("\" ");
                    Append(text, entry.Value, writeReference);
                    text.Append(')');
                }

                text.Append(')');
                break;

            case CallNode c:
                text.Append("(call ").Append(OperatorText.Name(c.Function));
                foreach (ExpressionNode argument in c.Arguments)
                {
                    text.Append(' ');
                    Append(text, argument, writeReference);
                }

                text.Append(')');
                break;

            default:
                throw new InvalidOperationException($"unknown AST node type {node.GetType().Name}");
        }
    }

    private static string Escape(string value) =>
        value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\t", "\\t", StringComparison.Ordinal);
}
