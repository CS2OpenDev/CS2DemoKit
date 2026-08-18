#region

using CS2DemoKit.Analysis.RulesetsV2.Model;

#endregion

namespace CS2DemoKit.Analysis.RulesetsV2;

/// <summary>
///     Stage-1 Expand: multiplies each <c>stats:</c> / <c>highlights:</c>
///     entry carrying a <c>for_each:</c> axis into one entry per value combination, substituting
///     <c>{key}</c> into the entry's <b>ids, labels, expression texts, and title templates</b> —
///     those four surfaces only. Parsed <c>match:</c> unary tests are <b>not</b>
///     substituted (only raw-text slots are), so an author varies a trigger via <c>where:</c> or a
///     kind argument, never a <c>match:</c> value.
///     <para>
///         Expansion precedes hashing (spec §6) and duplicate-id checking: the
///         expanded ids can collide, so the validator must see the post-expansion document.
///     </para>
/// </summary>
public static class ForEachExpander
{
    /// <summary>
    ///     Returns a copy of <paramref name="doc" /> with every <c>for_each:</c>-carrying stat and
    ///     highlight multiplied out and its <c>for_each:</c> cleared. Entries without a
    ///     <c>for_each:</c> pass through unchanged, preserving source order.
    /// </summary>
    /// <param name="doc">The mapped ruleset document.</param>
    /// <returns>The expanded document (the same instance when nothing carried a <c>for_each:</c>).</returns>
    public static RulesetDoc Expand(RulesetDoc doc)
    {
        ArgumentNullException.ThrowIfNull(doc);

        bool anyStat = doc.Stats.Any(s => HasAxes(s.ForEach));
        bool anyHighlight = doc.Highlights.Any(h => HasAxes(h.ForEach));
        if (!anyStat && !anyHighlight)
        {
            return doc;
        }

        List<StatDef> stats = [];
        foreach (StatDef stat in doc.Stats)
        {
            if (!HasAxes(stat.ForEach))
            {
                stats.Add(stat);
                continue;
            }

            foreach (Dictionary<string, string> combo in Combinations(stat.ForEach!))
            {
                stats.Add(ExpandStat(stat, combo));
            }
        }

        List<HighlightDef> highlights = [];
        foreach (HighlightDef highlight in doc.Highlights)
        {
            if (!HasAxes(highlight.ForEach))
            {
                highlights.Add(highlight);
                continue;
            }

            foreach (Dictionary<string, string> combo in Combinations(highlight.ForEach!))
            {
                highlights.Add(ExpandHighlight(highlight, combo));
            }
        }

        return doc with
        {
            Stats = stats,
            Highlights = highlights
        };
    }

    private static bool HasAxes(IReadOnlyList<ForEachAxis>? axes) => axes is { Count: > 0 };

    private static StatDef ExpandStat(StatDef stat, IReadOnlyDictionary<string, string> combo) =>
        stat with
        {
            Id = Substitute(stat.Id, combo)!,
            KindArg = Substitute(stat.KindArg, combo),
            Label = Substitute(stat.Label, combo),
            // bucket key: / value: are raw expression texts, so {key} substitutes into them like any
            // other expression slot.
            BucketKey = Substitute(stat.BucketKey, combo),
            BucketKeys = stat.BucketKeys is null
                ? null
                : [.. stat.BucketKeys.Select(part => Substitute(part, combo)!)],
            BucketValue = Substitute(stat.BucketValue, combo),
            Trigger = ExpandTrigger(stat.Trigger, combo),
            OffTrigger = ExpandTrigger(stat.OffTrigger, combo),
            ForEach = null
        };

    private static HighlightDef ExpandHighlight(HighlightDef highlight, IReadOnlyDictionary<string, string> combo) =>
        highlight with
        {
            Id = Substitute(highlight.Id, combo)!,
            When = Substitute(highlight.When, combo)!,
            Title = Substitute(highlight.Title, combo)!,
            ForEach = null
        };

    private static TriggerDef? ExpandTrigger(TriggerDef? trigger, IReadOnlyDictionary<string, string> combo) =>
        trigger is null
            ? null
            : trigger with
            {
                Where = Substitute(trigger.Where, combo),
                While = Substitute(trigger.While, combo)
            };

    private static string? Substitute(string? text, IReadOnlyDictionary<string, string> combo)
    {
        if (text is null || text.Length == 0 || !text.Contains('{', StringComparison.Ordinal))
        {
            return text;
        }

        string result = text;
        foreach ((string key, string value) in combo)
        {
            result = result.Replace("{" + key + "}", value, StringComparison.Ordinal);
        }

        return result;
    }

    /// <summary>Enumerates the Cartesian product of the axes as key → value maps, in row-major (last-axis-fastest) order.</summary>
    private static List<Dictionary<string, string>> Combinations(IReadOnlyList<ForEachAxis> axes)
    {
        List<Dictionary<string, string>> rows = [new(StringComparer.Ordinal)];
        foreach (ForEachAxis axis in axes)
        {
            List<Dictionary<string, string>> next = [];
            foreach (Dictionary<string, string> row in rows)
            {
                foreach (string value in axis.Values)
                {
                    Dictionary<string, string> extended = new(row, StringComparer.Ordinal)
                    {
                        [axis.Key] = value
                    };
                    next.Add(extended);
                }
            }

            rows = next;
        }

        return rows;
    }
}
