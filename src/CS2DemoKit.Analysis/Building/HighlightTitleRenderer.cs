#region

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using CS2DemoKit.Analysis.Abstractions;

#endregion

namespace CS2DemoKit.Analysis.Building;

/// <summary>
///     Renders a v2 highlight's <c>title:</c> template at the firing instant — the previously
///     missing surfacing layer named in <c>CheckedHighlight.Title</c>'s doc (until
///     A1, <c>Title</c> was never rendered anywhere at runtime). Called from the highlight's
///     rising-edge emission closure with the materialization-time lookups, so every <c>{…}</c>
///     hole resolves against LIVE node values at the moment the highlight fires.
///     <para>
///         Resolution rules, per hole (first match wins):
///         <list type="number">
///             <item><c>{player.name}</c> — the subject player's RAW in-demo name (the closure's player).</item>
///             <item>
///                 <c>{round.number}</c> (or the v1 spelling <c>{round_number}</c>) — the live
///                 round number handed in by the caller (the same value stamped into
///                 <see cref="HighlightFired.RoundNumber" />), invariant-formatted.
///             </item>
///             <item>
///                 Any other hole resolves to a graph node: the template's local lookup first
///                 (bare stat ids, per-player contexts, inherited game contexts), then the
///                 catalog's v2→v1 context table re-probed against the local lookup
///                 (<c>{player.survived}</c> → <c>survived</c>), then the template's qualified
///                 <c>{ruleset}.{stat}</c> map. A resolved <c>BoolNode</c> renders
///                 <c>true</c>/<c>false</c> from its active state; a value node renders its
///                 display value (the same string the graph UI shows).
///             </item>
///             <item>
///                 Anything unresolvable — unknown id, node with no value yet, unterminated
///                 brace — renders as the literal hole text. Rendering NEVER throws at runtime.
///             </item>
///         </list>
///     </para>
/// </summary>
internal static class HighlightTitleRenderer
{
    /// <summary>Renders <paramref name="template" />'s <c>{…}</c> holes against live values.</summary>
    /// <param name="template">The raw <c>title:</c> template text.</param>
    /// <param name="playerName">The subject player's RAW in-demo name (<c>{player.name}</c>).</param>
    /// <param name="roundNumber">The live round number (<c>{round.number}</c>).</param>
    /// <param name="localLookup">The materialization's local node lookup (bare ids + contexts).</param>
    /// <param name="contextV2ToV1">The catalog's v2 path → v1 rule-id table (context holes).</param>
    /// <param name="nodesByRuleId">The template's qualified <c>{ruleset}.{stat}</c> node map.</param>
    /// <returns>The rendered title; unresolvable holes stay literal.</returns>
    internal static string Render(
        string template,
        string playerName,
        int roundNumber,
        IReadOnlyDictionary<string, StateNode> localLookup,
        IReadOnlyDictionary<string, string> contextV2ToV1,
        IReadOnlyDictionary<string, StateNode> nodesByRuleId)
    {
        if (!template.Contains('{', StringComparison.Ordinal))
        {
            return template;
        }

        StringBuilder rendered = new(template.Length + 16);
        int pos = 0;
        while (pos < template.Length)
        {
            int open = template.IndexOf('{', pos);
            if (open < 0)
            {
                rendered.Append(template, pos, template.Length - pos);
                break;
            }

            rendered.Append(template, pos, open - pos);
            int close = template.IndexOf('}', open + 1);
            if (close < 0)
            {
                // Unterminated hole — keep the rest literal.
                rendered.Append(template, open, template.Length - open);
                break;
            }

            string hole = template[(open + 1)..close].Trim();
            rendered.Append(ResolveHole(hole, playerName, roundNumber, localLookup, contextV2ToV1, nodesByRuleId)
                            ?? template[open..(close + 1)]);
            pos = close + 1;
        }

        return rendered.ToString();
    }

    /// <summary>Resolves one hole to its rendered text, or <c>null</c> to keep the literal.</summary>
    private static string? ResolveHole(
        string hole,
        string playerName,
        int roundNumber,
        IReadOnlyDictionary<string, StateNode> localLookup,
        IReadOnlyDictionary<string, string> contextV2ToV1,
        IReadOnlyDictionary<string, StateNode> nodesByRuleId)
    {
        if (string.Equals(hole, "player.name", StringComparison.OrdinalIgnoreCase))
        {
            return playerName;
        }

        if (string.Equals(hole, "round.number", StringComparison.OrdinalIgnoreCase)
            || string.Equals(hole, "round_number", StringComparison.OrdinalIgnoreCase))
        {
            return roundNumber.ToString(CultureInfo.InvariantCulture);
        }

        StateNode? node = null;
        if (localLookup.TryGetValue(hole, out StateNode? direct))
        {
            node = direct;
        }
        else if (TryGetContextMapping(contextV2ToV1, hole, out string? v1Id)
                 && localLookup.TryGetValue(v1Id, out StateNode? context))
        {
            node = context;
        }
        else if (nodesByRuleId.TryGetValue(hole, out StateNode? qualified))
        {
            node = qualified;
        }

        return node switch
        {
            null => null,
            BoolNode b => b.IsActive ? "true" : "false",
            // A value node's display string (the same text the graph UI shows); null while the
            // node has no value yet → keep the hole literal rather than invent a default.
            _ => node.GetDisplayValue()
        };
    }

    /// <summary>
    ///     The context table arrives with the planner's default (Ordinal) comparer — probe it
    ///     case-insensitively like every OTHER resolution tier, so <c>{Player.Survived}</c>
    ///     resolves exactly as <c>{player.survived}</c> does. The exact-case fast path first;
    ///     the table is catalog-context-sized, so the fallback scan is cheap.
    /// </summary>
    private static bool TryGetContextMapping(
        IReadOnlyDictionary<string, string> contextV2ToV1, string hole,
        [NotNullWhen(true)] out string? v1Id)
    {
        if (contextV2ToV1.TryGetValue(hole, out v1Id))
        {
            return true;
        }

        foreach ((string key, string value) in contextV2ToV1)
        {
            if (string.Equals(key, hole, StringComparison.OrdinalIgnoreCase))
            {
                v1Id = value;
                return true;
            }
        }

        return false;
    }
}
