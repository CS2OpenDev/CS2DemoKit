#region

using System.Globalization;
using System.Text.RegularExpressions;
using CS2DemoKit.Analysis.Plugins;

#endregion

namespace CS2DemoKit.Analysis.Building;

/// <summary>
///     A single structured entity check — one row of the scope-aware breakpoint editor — comparing a
///     subject's per-player provider value to a literal.
/// </summary>
/// <param name="Subject">
///     A <c>*Slot</c> event field (e.g. <c>VictimSlot</c>) or <c>player</c> (the filter's selected player).
/// </param>
/// <param name="Provider">A registered provider Name (e.g. <c>entity.pawn.health</c>).</param>
/// <param name="Op">A comparison operator — one of <c>== != &lt;= &gt;= &lt; &gt;</c>.</param>
/// <param name="Value">The literal RHS: a number, or the <em>unquoted</em> text for a string provider.</param>
public sealed record EntityCheckRow(string Subject, string Provider, string Op, string Value);

/// <summary>
///     A breakpoint condition decomposed into an editor-friendly shape: a free-text <see cref="EventMatch" />
///     (event-field / state logic, kept flexible) plus structured entity-check <see cref="Rows" />. The
///     condition <em>string</em> stays the canonical, persisted, compiled form — this is a bidirectional
///     <em>view</em> over it (<see cref="Compose" /> writes the string, <see cref="Parse" /> reads it back).
///     <para>
///         <see cref="Decomposed" /> is <c>false</c> when the string can't be safely split into AND-clauses
///         (a top-level <c>||</c>, or anything the row grammar doesn't recognise) — the whole condition then
///         lives in <see cref="EventMatch" /> and the editor shows it as plain free text. The round-trip is
///         <b>lossless</b>: <c>Parse(Compose(x)) ≈ x</c>, and any string survives <c>Compose(Parse(s))</c>
///         semantically unchanged (only entity clauses re-formatted to the canonical spacing).
///     </para>
/// </summary>
/// <param name="EventMatch">The free-text remainder (event fields, state, anything non-row). May be empty.</param>
/// <param name="Rows">The entity checks lifted out as structured rows.</param>
/// <param name="Decomposed"><c>true</c> when the string split cleanly into AND-clauses; <c>false</c> = free-text only.</param>
public sealed record StructuredCondition(string EventMatch, IReadOnlyList<EntityCheckRow> Rows, bool Decomposed)
{
    /// <summary>The comparison operators the editor offers, longest-first so matching is unambiguous.</summary>
    public static readonly IReadOnlyList<string> NumericOps = ["==", "!=", "<=", ">=", "<", ">"];

    /// <summary>Operators valid for a string provider (e.g. <c>active_weapon_class</c>).</summary>
    public static readonly IReadOnlyList<string> TextOps = ["==", "!="];

    // A clause that is exactly `dotted.identifier <op> literal` — the only shape we lift to a row. The LHS
    // is a pure path (no spaces/arithmetic), so `victim.health + 5 < 20` never matches and stays free-text.
    private static readonly Regex _clausePattern =
        new(@"^(?<lhs>[A-Za-z0-9_.]+)\s*(?<op>==|!=|<=|>=|<|>)\s*(?<val>.+)$", RegexOptions.Compiled);

    /// <summary>The operators valid for a provider, keyed off its value type (text ⇒ only equality).</summary>
    public static IReadOnlyList<string> OpsFor(IPerPlayerEntityValueProvider provider) =>
        provider.ValueType == typeof(string) ? TextOps : NumericOps;

    /// <summary>
    ///     Composes the canonical condition string from a free-text event match and the entity-check rows,
    ///     AND-joined. <paramref name="slotPrefix" /> is <c>input.&lt;event&gt;.</c> for a node input
    ///     condition and <c>""</c> for an edge (the only node/edge difference); the <c>player</c> subject
    ///     needs no prefix. A string provider's value is quoted.
    /// </summary>
    public static string Compose(
        string eventMatch,
        IReadOnlyList<EntityCheckRow> rows,
        string slotPrefix,
        PerPlayerEntityValueProviderRegistry providers)
    {
        List<string> parts = [];
        if (!string.IsNullOrWhiteSpace(eventMatch))
        {
            parts.Add(eventMatch.Trim());
        }

        foreach (EntityCheckRow row in rows)
        {
            parts.Add(ComposeRow(row, slotPrefix, providers));
        }

        return string.Join(" && ", parts);
    }

    /// <summary>
    ///     Parses a condition string into the editor view. Splits on top-level <c>&amp;&amp;</c> (paren- and
    ///     quote-aware) and lifts each clause that is exactly a recognised entity read
    ///     (<c>[slotPrefix]&lt;Slot&gt;.&lt;provider&gt; &lt;op&gt; &lt;literal&gt;</c> or
    ///     <c>player.&lt;provider&gt; …</c>) into a row; every other clause stays in
    ///     <see cref="EventMatch" />. A top-level <c>||</c> (mixed precedence) or an empty condition yields a
    ///     free-text-only result. <paramref name="slotFields" /> are the trigger event's <c>*Slot</c> field
    ///     names; <paramref name="slotPrefix" /> matches <see cref="Compose" />.
    /// </summary>
    public static StructuredCondition Parse(
        string? condition,
        string slotPrefix,
        IReadOnlySet<string> slotFields,
        PerPlayerEntityValueProviderRegistry providers)
    {
        if (string.IsNullOrWhiteSpace(condition))
        {
            return new StructuredCondition("", [], true);
        }

        List<string>? clauses = SplitTopLevelAnd(condition.Trim());
        if (clauses is null)
        {
            return new StructuredCondition(condition.Trim(), [], false); // top-level OR → free-text only
        }

        List<string> eventMatchClauses = [];
        List<EntityCheckRow> rows = [];
        foreach (string clause in clauses)
        {
            EntityCheckRow? row = TryMatchRow(clause, slotPrefix, slotFields, providers);
            if (row is not null)
            {
                rows.Add(row);
            }
            else
            {
                eventMatchClauses.Add(clause);
            }
        }

        return new StructuredCondition(string.Join(" && ", eventMatchClauses), rows, true);
    }

    private static string ComposeRow(
        EntityCheckRow row, string slotPrefix, PerPlayerEntityValueProviderRegistry providers)
    {
        string lhs = row.Subject == "player"
            ? $"player.{row.Provider}"
            : $"{slotPrefix}{row.Subject}.{row.Provider}";

        IPerPlayerEntityValueProvider? provider = providers.Get(row.Provider);
        bool isText = provider is not null && provider.ValueType == typeof(string);
        string value = isText ? $"\"{row.Value}\"" : row.Value;
        return $"{lhs} {row.Op} {value}";
    }

    // Lifts a clause to a row ONLY when it matches a recognised entity read exactly (so Compose of the row
    // reproduces it) — otherwise returns null and the caller keeps it as free text. Conservative by design.
    private static EntityCheckRow? TryMatchRow(
        string clause, string slotPrefix, IReadOnlySet<string> slotFields, PerPlayerEntityValueProviderRegistry providers)
    {
        Match m = _clausePattern.Match(clause);
        if (!m.Success)
        {
            return null;
        }

        string lhs = m.Groups["lhs"].Value;
        string op = m.Groups["op"].Value;
        string val = m.Groups["val"].Value.Trim();

        foreach (IPerPlayerEntityValueProvider provider in providers.All)
        {
            if (lhs == $"player.{provider.Name}")
            {
                return BuildRow("player", provider, op, val);
            }

            foreach (string slot in slotFields)
            {
                if (lhs == $"{slotPrefix}{slot}.{provider.Name}")
                {
                    return BuildRow(slot, provider, op, val);
                }
            }
        }

        return null;
    }

    // Builds a row only when the literal's type matches the provider's (quoted for text, numeric otherwise),
    // so a malformed / cross-entity RHS stays free text rather than silently becoming a row.
    private static EntityCheckRow? BuildRow(string subject, IPerPlayerEntityValueProvider provider, string op, string val)
    {
        if (provider.ValueType == typeof(string))
        {
            if (val.Length < 2 || val[0] != '"' || val[^1] != '"' || val.IndexOf('"', 1) != val.Length - 1)
            {
                return null; // not a single quoted literal
            }

            return new EntityCheckRow(subject, provider.Name, op, val[1..^1]);
        }

        return double.TryParse(val, NumberStyles.Float | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _)
            ? new EntityCheckRow(subject, provider.Name, op, val)
            : null; // not a clean number literal
    }

    // Splits on top-level "&&", aware of parens and double-quoted strings. Returns null when a top-level
    // "||" is present (its precedence would make AND-splitting unsound — fall back to free text).
    private static List<string>? SplitTopLevelAnd(string expr)
    {
        List<string> clauses = [];
        int depth = 0;
        bool inString = false;
        int start = 0;

        for (int i = 0; i < expr.Length; i++)
        {
            char c = expr[i];
            if (c == '"')
            {
                inString = !inString;
                continue;
            }

            if (inString)
            {
                continue;
            }

            switch (c)
            {
                case '(':
                    depth++;
                    break;
                case ')':
                    depth--;
                    break;
                default:
                    if (depth == 0 && i + 1 < expr.Length)
                    {
                        if (c == '|' && expr[i + 1] == '|')
                        {
                            return null; // top-level OR → not safely decomposable
                        }

                        if (c == '&' && expr[i + 1] == '&')
                        {
                            clauses.Add(expr[start..i]);
                            i++; // consume the second '&'
                            start = i + 1;
                        }
                    }

                    break;
            }
        }

        clauses.Add(expr[start..]);
        return clauses.Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
    }
}
