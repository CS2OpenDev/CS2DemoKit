#region

using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Analysis.Nodes;
using CS2DemoKit.Parser;

#endregion

namespace CS2DemoKit.Analysis.Output;

/// <summary>
///     Built-in projector for keyed nodes — <see cref="KeyedCounterNode" /> (per-key
///     <c>bucket:</c> counters/sums) and <see cref="KeyedRatioNode" /> (per-key <c>rate:</c>
///     ratios): emits <b>one <see cref="MetricTable" /> per keyed rule id</b>, named
///     <c>player_&lt;rule id&gt;</c> (e.g. <c>player_kills_by_weapon</c>), with one row per
///     (player, observed key) pair.
///     <para>
///         Keyed nodes are excluded from snapshots (<see cref="Abstractions.ISnapshotExcludedNode" />),
///         so unlike the snapshot-sampling projectors this one reads the <b>live</b> nodes after
///         evaluation — valid because keyed counters sample per-game only (end-of-eval state IS
///         the per-game total, and a rate divides two such totals). Nodes are discovered by type on
///         <see cref="PerPlayerNodeTemplate.MaterializedPlayer.Nodes" />.
///     </para>
///     <para>
///         Schema per table: dimensions (<c>match_id</c> [omitted when unset], <c>map</c>,
///         <c>player_slot</c>, <c>player_name</c>, <c>team</c>, <c>key</c>) and a single value
///         column — the rule's column label when a <c>columns:</c> entry maps the keyed rule,
///         otherwise the rule id. Rows are emitted in player-materialization order, keys sorted
///         ordinally within a player for deterministic output; players with no observed keys
///         contribute no rows. Whole-number <b>counter</b> buckets are emitted as <see cref="int" /> so
///         CSV/JSON render counts without a decimal point; <b>ratio</b> buckets stay
///         <see cref="double" /> (0.5 stays 0.5), never coerced to int.
///     </para>
/// </summary>
public sealed class KeyedStatsProjector : IOutputProjector
{
    // Dimension column keys (snake_case, matching the other built-in projectors).
    private const string DimMatchId = "match_id";
    private const string DimMap = "map";
    private const string DimPlayerSlot = "player_slot";
    private const string DimPlayerName = "player_name";
    private const string DimTeam = "team";
    private const string DimKey = "key";

    /// <summary>
    ///     The match identifier used in the <c>match_id</c> dimension (typically the demo filename).
    ///     Optional — when null the dimension is omitted.
    /// </summary>
    public string? MatchId { get; init; }

    /// <inheritdoc />
    public IReadOnlyList<MetricTable> Project(EvaluationResult result, ParsedDemo demo)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(demo);

        string[] dimensionColumns = [DimMatchId, DimMap, DimPlayerSlot, DimPlayerName, DimTeam, DimKey];

        // Group keyed nodes by rule id across players, preserving first-seen rule order so the
        // table list is stable (rule declaration order, since players share one template).
        List<string> ruleOrder = new();
        Dictionary<string, List<(PerPlayerNodeTemplate.MaterializedPlayer Player, KeyedNodeInfo Info)>>
            byRule = new(StringComparer.Ordinal);
        Dictionary<string, string> valueColumnByRule = new(StringComparer.Ordinal);

        foreach (PerPlayerNodeTemplate.MaterializedPlayer mp in result.MaterializedPlayers)
        {
            foreach (KeyedNodeInfo keyed in EnumerateKeyedNodes(mp))
            {
                if (!byRule.TryGetValue(keyed.RuleId,
                        out List<(PerPlayerNodeTemplate.MaterializedPlayer Player, KeyedNodeInfo Info)>? list))
                {
                    list = new List<(PerPlayerNodeTemplate.MaterializedPlayer, KeyedNodeInfo)>();
                    byRule[keyed.RuleId] = list;
                    ruleOrder.Add(keyed.RuleId);
                    valueColumnByRule[keyed.RuleId] = keyed.ColumnLabel ?? keyed.RuleId;
                }

                list.Add((mp, keyed));
            }
        }

        List<MetricTable> tables = new(ruleOrder.Count);
        foreach (string ruleId in ruleOrder)
        {
            string valueColumn = valueColumnByRule[ruleId];
            List<MetricRow> rows = new();

            foreach ((PerPlayerNodeTemplate.MaterializedPlayer mp, KeyedNodeInfo info) in byRule[ruleId])
            {
                int team = demo.Players.TryGetValue(mp.PlayerSlot, out PlayerInfo? pi) ? pi.Team : 0;

                foreach ((string key, double bucket) in info.Buckets.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                {
                    Dictionary<string, object?> dimensions = new(StringComparer.Ordinal)
                    {
                        [DimMap] = demo.MapName,
                        [DimPlayerSlot] = mp.PlayerSlot,
                        [DimPlayerName] = mp.PlayerName,
                        [DimTeam] = team,
                        [DimKey] = key
                    };
                    if (MatchId is not null)
                    {
                        dimensions[DimMatchId] = MatchId;
                    }

                    // Whole-number COUNTER buckets (the common case: kill/damage counts) render as ints
                    // so CSV/JSON emit counts without a decimal point. RATIO buckets (rate:, G3) are
                    // never coerced — a 0.5 headshot rate must stay 0.5, not collapse to 0.
                    bool wholeNumber = info.CoerceWholeNumbers
                                       && bucket >= int.MinValue && bucket <= int.MaxValue
                                       && bucket == Math.Floor(bucket);
                    Dictionary<string, object?> values = new(StringComparer.Ordinal)
                    {
                        [valueColumn] = wholeNumber ? (int)bucket : bucket
                    };

                    rows.Add(new MetricRow(dimensions, values));
                }
            }

            tables.Add(new MetricTable($"player_{ruleId}", dimensionColumns, [valueColumn], rows));
        }

        return tables;
    }

    private static IEnumerable<KeyedNodeInfo> EnumerateKeyedNodes(
        PerPlayerNodeTemplate.MaterializedPlayer mp)
    {
        foreach (StateNode node in mp.Nodes)
        {
            // A KeyedCounterNode (bucket:) coerces whole-number values to int; a KeyedRatioNode (rate:)
            // keeps its float ratios. Both expose a RuleId + per-key double Buckets.
            (string ruleId, IReadOnlyDictionary<string, double> buckets, bool coerce) = node switch
            {
                KeyedCounterNode counter => (counter.RuleId, counter.Buckets, true),
                KeyedRatioNode ratio => (ratio.RuleId, ratio.Buckets, false),
                _ => (null!, null!, false)
            };

            if (ruleId is null)
            {
                continue;
            }

            // A columns: entry mapping the keyed rule supplies the value-column label.
            string? label = null;
            foreach (PerPlayerColumnAssignment col in mp.ColumnAssignments)
            {
                if (ReferenceEquals(col.Node, node))
                {
                    label = col.ColumnName;
                    break;
                }
            }

            yield return new KeyedNodeInfo(ruleId, buckets, label, coerce);
        }
    }

    private readonly record struct KeyedNodeInfo(
        string RuleId,
        IReadOnlyDictionary<string, double> Buckets,
        string? ColumnLabel,
        bool CoerceWholeNumbers);
}
