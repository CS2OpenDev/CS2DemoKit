#region

using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Parser;

#endregion

namespace CS2DemoKit.Analysis.Output;

/// <summary>
///     Built-in projector that emits one <see cref="MetricRow" /> per player: the end-of-match
///     scoreboard. Values are sampled from the <i>final</i> snapshot vector, where game-scoped nodes
///     hold their accumulated totals (kills, ADR, HLTV rating, …).
///     <para>
///         Round-scoped columns (<see cref="IRoundScopedNode" />, e.g. kast.yaml's per-round
///         Kills/Deaths) are <b>excluded</b>: at the final snapshot they hold only the LAST round's
///         value, which a match scoreboard would silently present as a total (the exact
///         wrong-Kills/Deaths/Assists bug this exclusion fixed). The per-round projector is the
///         surface for those columns.
///     </para>
///     <para>
///         Dimension schema matches <see cref="PlayerRoundStatsProjector" /> (minus
///         <c>round_number</c>) so multi-demo datasets can join the two tables on
///         (match_id, player_slot).
///     </para>
/// </summary>
public sealed class PlayerGameStatsProjector : IOutputProjector
{
    /// <summary>The <see cref="MetricTable.Name" /> emitted by this projector.</summary>
    public const string TableName = "player_game_stats";

    // Dimension column keys (snake_case, matching the GoldenStats key convention).
    private const string DimMatchId = "match_id";
    private const string DimMap = "map";
    private const string DimPlayerSlot = "player_slot";
    private const string DimPlayerName = "player_name";
    private const string DimTeam = "team";

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

        List<string> valueColumns = StatValues.UnionValueColumns(result.MaterializedPlayers, IsGameLifetime);

        string[] dimensionColumns = [DimMatchId, DimMap, DimPlayerSlot, DimPlayerName, DimTeam];

        Dictionary<StateNode, int> nodeIndex = StatValues.BuildNodeIndex(result.FinalTrackedNodes);

        // The final snapshot is the end-of-match state. A snapshot-less result (zero messages)
        // yields schema-only output — same contract as the round projector with no live rounds.
        NodeSnapshot[]? finalSnapshot = result.MessageSnapshots.Count > 0
            ? result.MessageSnapshots.MaterializeRow(result.MessageSnapshots.Count - 1)
            : null;

        IReadOnlyList<StatValues.MergedPlayer> mergedPlayers = StatValues.MergeBySlot(result.MaterializedPlayers);
        List<MetricRow> rows = new(mergedPlayers.Count);
        if (finalSnapshot is not null)
        {
            foreach (StatValues.MergedPlayer mp in mergedPlayers)
            {
                int team = demo.Players.TryGetValue(mp.Slot, out PlayerInfo? pi) ? pi.Team : 0;

                Dictionary<string, object?> dimensions = new(StringComparer.Ordinal)
                {
                    [DimMap] = demo.MapName,
                    [DimPlayerSlot] = mp.Slot,
                    [DimPlayerName] = mp.Name,
                    [DimTeam] = team
                };
                if (MatchId is not null)
                {
                    dimensions[DimMatchId] = MatchId;
                }

                Dictionary<string, object?> values = new(StringComparer.Ordinal);
                foreach (PerPlayerColumnAssignment col in mp.Columns)
                {
                    if (!IsGameLifetime(col))
                    {
                        continue;
                    }

                    values[col.ColumnName] = StatValues.ApplyColumnFormat(
                        StatValues.ReadColumnValue(finalSnapshot, nodeIndex, col.Node), col.Format, demo.TickRate);
                }

                rows.Add(new MetricRow(dimensions, values));
            }
        }

        return [new MetricTable(TableName, dimensionColumns, valueColumns, rows)];
    }

    /// <summary>
    ///     A column belongs on the match scoreboard only if its node accumulates for the whole game.
    ///     Uses the builder-stamped flag, NOT a node-type check: wrapper-reset logic nodes (e.g.
    ///     kast.yaml's has_kast) reset each round without implementing <see cref="IRoundScopedNode" />
    ///     and must not leak last-round values onto the scoreboard.
    /// </summary>
    private static bool IsGameLifetime(PerPlayerColumnAssignment col) => !col.IsRoundScoped;
}
