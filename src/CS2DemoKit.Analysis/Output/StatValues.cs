#region

using System.Globalization;
using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Analysis.RulesetsV2.Model;

#endregion

namespace CS2DemoKit.Analysis.Output;

/// <summary>
///     Shared snapshot-reading helpers for the built-in projectors — column-schema union, tracked-node
///     index, active-gated cell reads, and display-string → CLR-value coercion. One implementation so
///     the per-round and per-game tables can never disagree on read semantics.
/// </summary>
internal static class StatValues
{
    // A StateNode's .Name is the rule's DISPLAY name ("RoundNumber", from BuiltinContexts), not its
    // rule id — keep the id as a defensive fallback so synthetic test graphs resolve either way.
    private const string RoundNumberNodeName = "RoundNumber";
    private const string RoundNumberRuleId = "round_number";

    /// <summary>
    ///     The value-column schema: the union of every materialized player's column names, in
    ///     first-seen order. A fixed ordered list keeps the table stable regardless of which player
    ///     is missing a column (they get a null cell instead of a ragged row).
    ///     <paramref name="filter" /> restricts the union (e.g. the match scoreboard excludes
    ///     round-scoped columns).
    /// </summary>
    internal static List<string> UnionValueColumns(
        IReadOnlyList<PerPlayerNodeTemplate.MaterializedPlayer> players,
        Func<PerPlayerColumnAssignment, bool>? filter = null)
    {
        List<string> columns = new();
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (PerPlayerNodeTemplate.MaterializedPlayer mp in players)
        {
            foreach (PerPlayerColumnAssignment col in mp.ColumnAssignments)
            {
                if (filter is not null && !filter(col))
                {
                    continue;
                }

                if (seen.Add(col.ColumnName))
                {
                    columns.Add(col.ColumnName);
                }
            }
        }

        return columns;
    }

    /// <summary>
    ///     Collapses <see cref="EvaluationResult.MaterializedPlayers" /> to ONE logical player per slot,
    ///     unioning each materialization's column assignments (first-seen slot order). Rulesets v2 builds
    ///     a per-player template PER RULESET, so a slot is materialized once per <c>for: each_player</c>
    ///     ruleset; without this a multi-ruleset load (the shipped set) projects a duplicate row per player
    ///     — one per ruleset, empty where that ruleset has no columns for the board. v1 (one shared
    ///     template) has a single materialization per slot, so this is a no-op there.
    /// </summary>
    internal static IReadOnlyList<MergedPlayer> MergeBySlot(
        IReadOnlyList<PerPlayerNodeTemplate.MaterializedPlayer> players)
    {
        List<int> order = new();
        Dictionary<int, List<PerPlayerColumnAssignment>> columnsBySlot = new();
        Dictionary<int, string> nameBySlot = new();
        foreach (PerPlayerNodeTemplate.MaterializedPlayer mp in players)
        {
            if (!columnsBySlot.TryGetValue(mp.PlayerSlot, out List<PerPlayerColumnAssignment>? list))
            {
                list = new List<PerPlayerColumnAssignment>();
                columnsBySlot[mp.PlayerSlot] = list;
                nameBySlot[mp.PlayerSlot] = mp.PlayerName;
                order.Add(mp.PlayerSlot);
            }

            list.AddRange(mp.ColumnAssignments);
        }

        return order.Select(s => new MergedPlayer(s, nameBySlot[s], columnsBySlot[s])).ToList();
    }

    /// <summary>
    ///     Reference-identity map from tracked node → its index into every
    ///     <c>MessageSnapshots[m]</c> vector. There is no NodeTrackedIndex field on the nodes, so
    ///     identity-lookup into <see cref="EvaluationResult.FinalTrackedNodes" /> is the seam.
    /// </summary>
    internal static Dictionary<StateNode, int> BuildNodeIndex(IReadOnlyList<StateNode> trackedNodes)
    {
        Dictionary<StateNode, int> index = new(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < trackedNodes.Count; i++)
        {
            index[trackedNodes[i]] = i;
        }

        return index;
    }

    /// <summary>
    ///     Read a column node's value from a snapshot, gated on the node being active — mirroring the
    ///     working GoldenStats export read (<c>col.Node.IsActive ? col.Node.GetDisplayValue() : null</c>).
    ///     An inactive node, or one not present in this (possibly late-materialized) snapshot, yields null.
    /// </summary>
    internal static object? ReadColumnValue(
        NodeSnapshot[] snapshot, Dictionary<StateNode, int> nodeIndex, StateNode node)
    {
        if (!nodeIndex.TryGetValue(node, out int idx) || idx >= snapshot.Length)
        {
            return null;
        }

        NodeSnapshot snap = snapshot[idx];
        if (!snap.IsActive)
        {
            return null;
        }

        // Bool nodes (incl. auto-activate conjunctions/disjunctions) have no display string —
        // their STATE is the value. Without this an achieved/HasKAST column projects null forever.
        if (node is BoolNode)
        {
            return true;
        }

        return ParseStatValue(snap.DisplayValue);
    }

    /// <summary>
    ///     Applies a column's <c>as:</c> display formatting (v2 <c>show:</c>) to a projected value.
    ///     <see cref="ColumnValueFormat.None" /> (every v1 column) and a null value pass through
    ///     unchanged — byte-identical to the pre-<c>as:</c> projection. A non-numeric value also passes
    ///     through (formatting only reshapes a tick-valued number). Otherwise the value is treated as a
    ///     tick count and reshaped at <paramref name="ticksPerSecond" />: <c>ticks</c> → the integer
    ///     tick value; <c>seconds</c> → ticks / rate (a double); <c>time</c> → an <c>m:ss</c> string.
    /// </summary>
    internal static object? ApplyColumnFormat(object? value, ColumnValueFormat format, int ticksPerSecond)
    {
        if (format == ColumnValueFormat.None || value is null)
        {
            return value;
        }

        double ticks;
        switch (value)
        {
            case int i:
                ticks = i;
                break;
            case long l:
                ticks = l;
                break;
            case double d:
                ticks = d;
                break;
            default:
                if (!double.TryParse(value.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out ticks))
                {
                    return value; // non-numeric (e.g. a captured string / list) — leave as-is
                }

                break;
        }

        return format switch
        {
            ColumnValueFormat.Ticks => (long)Math.Round(ticks, MidpointRounding.AwayFromZero),
            ColumnValueFormat.Seconds => ticksPerSecond > 0 ? ticks / ticksPerSecond : ticks,
            ColumnValueFormat.Time => FormatClock(ticks, ticksPerSecond),
            _ => value
        };
    }

    /// <summary>Renders a tick count as an <c>m:ss</c> string at the given rate (floored to whole seconds).</summary>
    private static string FormatClock(double ticks, int ticksPerSecond)
    {
        double totalSeconds = ticksPerSecond > 0 ? ticks / ticksPerSecond : ticks;
        if (totalSeconds < 0)
        {
            totalSeconds = 0;
        }

        int whole = (int)Math.Floor(totalSeconds);
        int minutes = whole / 60;
        int seconds = whole % 60;
        return string.Create(CultureInfo.InvariantCulture, $"{minutes}:{seconds:D2}");
    }

    /// <summary>
    ///     Finds the tracked-node index of the engine's <c>round_number</c> counter (the shared
    ///     round-boundary signal), or -1 when absent (bare synthetic graphs).
    /// </summary>
    internal static int FindRoundNumberIndex(IReadOnlyList<StateNode> trackedNodes)
    {
        for (int i = 0; i < trackedNodes.Count; i++)
        {
            string name = trackedNodes[i].Name;
            if (string.Equals(name, RoundNumberNodeName, StringComparison.Ordinal)
                || string.Equals(name, RoundNumberRuleId, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    ///     Coerce a node's display string into the tightest CLR type (int → double → bool → string),
    ///     so downstream formatters can render numbers as numbers. Null/empty → null. Invariant culture.
    /// </summary>
    internal static object? ParseStatValue(string? raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return null;
        }

        if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int i))
        {
            return i;
        }

        if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
        {
            return d;
        }

        if (bool.TryParse(raw, out bool b))
        {
            return b;
        }

        return raw;
    }

    /// <summary>One logical player row, merged across every per-player template that materialized the slot.</summary>
    internal sealed record MergedPlayer(int Slot, string Name, IReadOnlyList<PerPlayerColumnAssignment> Columns);
}
