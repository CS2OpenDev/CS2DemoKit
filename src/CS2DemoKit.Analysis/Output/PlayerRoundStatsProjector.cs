#region

using System.Globalization;
using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Parser;

#endregion

namespace CS2DemoKit.Analysis.Output;

/// <summary>
///     Built-in projector that emits one <see cref="MetricRow" /> per (player, round): it samples each
///     materialized player's column-node values at the end of every live round.
///     <para>
///         <b>Round-boundary signal (reused, not invented):</b> the engine already maintains a
///         <c>round_number</c> counter node (see <c>Building/BuiltinContexts.cs</c>: a
///         <c>Counter</c> gated on the <c>match_live</c> parent, incremented on <c>$round_freeze_end</c>).
///         Because it is parented on <c>match_live</c> it stays at 0 during warmup and only advances
///         during live play, and its trigger (<c>$round_freeze_end</c>) is profile-resolved — so this
///         projector works identically on GOTV and HLTV demos without hardcoding any terminal event
///         (<c>round_officially_ended</c> / <c>cs_win_panel_match</c> / <c>cs_pre_restart</c>).
///     </para>
///     <para>
///         For each distinct live round value <c>r &gt;= 1</c> we take the <i>last</i> snapshot index at
///         which <c>round_number == r</c>. Round-scoped per-player nodes reset on the <i>next</i> round's
///         freeze-end, so the last index holding round <c>r</c> captures the end-of-round values before
///         that reset — equivalent to sampling at the round-end message the design doc describes, but
///         derived from the engine's own counter rather than re-detecting rounds from raw events.
///     </para>
/// </summary>
public sealed class PlayerRoundStatsProjector : IOutputProjector
{
    /// <summary>The <see cref="MetricTable.Name" /> emitted by this projector.</summary>
    public const string TableName = "player_round_stats";

    // A StateNode's .Name is the rule's DISPLAY name (the 3rd RuleDef arg), not its rule id — e.g.
    // RuleDef("round_number", Counter, "RoundNumber", ...) in Building/BuiltinContexts.cs surfaces a node
    // named "RoundNumber". (The same convention is asserted by EntityIntegrationTests, which finds the
    // gameplay_phase node by "GameplayPhase".) We match the display name first and keep the rule id as a
    // defensive fallback so a synthetic test graph using either name still resolves.
    private const string RoundNumberNodeName = "RoundNumber";
    private const string RoundNumberRuleId = "round_number";

    // Dimension column keys (snake_case, matching the GoldenStats key convention).
    private const string DimMatchId = "match_id";
    private const string DimMap = "map";
    private const string DimRoundNumber = "round_number";
    private const string DimPlayerSlot = "player_slot";
    private const string DimPlayerName = "player_name";
    private const string DimTeam = "team";

    /// <summary>
    ///     The match identifier used in the <c>match_id</c> dimension (typically the demo filename).
    ///     Optional — when null the dimension is omitted, matching the design doc's "demo filename or
    ///     header" sourcing left to the caller.
    /// </summary>
    public string? MatchId { get; init; }

    /// <inheritdoc />
    public IReadOnlyList<MetricTable> Project(EvaluationResult result, ParsedDemo demo)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(demo);

        // The mirror image of the game projector's exclusion: this table carries ONLY round-scoped
        // columns. Including game-lifetime columns here showed running totals per round
        // ("round 6 aggregates rounds 1-6") and collided display names with the genuine
        // per-round columns (TotalK and Kills both render as "K").
        List<string> valueColumns = StatValues.UnionValueColumns(result.MaterializedPlayers, IsRoundScoped);

        string[] dimensionColumns =
        [
            DimMatchId, DimMap, DimRoundNumber, DimPlayerSlot, DimPlayerName, DimTeam
        ];

        Dictionary<StateNode, int> nodeIndex = StatValues.BuildNodeIndex(result.FinalTrackedNodes);

        int roundNumberIndex = FindRoundNumberIndex(result.FinalTrackedNodes);

        // Last snapshot index per distinct live round value (round_number >= 1), in round order.
        List<RoundSample> roundSamples = roundNumberIndex >= 0
            ? CollectRoundSamples(result.MessageSnapshots, roundNumberIndex)
            : [];

        IReadOnlyList<StatValues.MergedPlayer> mergedPlayers = StatValues.MergeBySlot(result.MaterializedPlayers);
        List<MetricRow> rows = new(roundSamples.Count * Math.Max(1, mergedPlayers.Count));
        foreach (RoundSample round in roundSamples)
        {
            NodeSnapshot[] snapshot = result.MessageSnapshots.MaterializeRow(round.SnapshotIndex);

            foreach (StatValues.MergedPlayer mp in mergedPlayers)
            {
                int team = demo.Players.TryGetValue(mp.Slot, out PlayerInfo? pi) ? pi.Team : 0;

                Dictionary<string, object?> dimensions = new(StringComparer.Ordinal)
                {
                    [DimMap] = demo.MapName,
                    [DimRoundNumber] = round.RoundNumber,
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
                    if (!IsRoundScoped(col))
                    {
                        continue;
                    }

                    values[col.ColumnName] = StatValues.ApplyColumnFormat(
                        StatValues.ReadColumnValue(snapshot, nodeIndex, col.Node), col.Format, demo.TickRate);
                }

                rows.Add(new MetricRow(dimensions, values));
            }
        }

        return [new MetricTable(TableName, dimensionColumns, valueColumns, rows)];
    }

    private static int FindRoundNumberIndex(IReadOnlyList<StateNode> trackedNodes)
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
    ///     Walk the snapshots once, recording for each distinct live round value (<c>round_number &gt;= 1</c>)
    ///     the last snapshot index at which it was held. Returns the samples ordered by round number.
    /// </summary>
    private static List<RoundSample> CollectRoundSamples(SnapshotTable snapshots, int roundNumberIndex)
    {
        // round value → last snapshot index seen holding it. round_number is monotonic across a match
        // (including overtime), but we don't assume that — we just keep the max index per value.
        Dictionary<int, int> lastIndexByRound = new();

        for (int m = 0; m < snapshots.Count; m++)
        {
            NodeSnapshot rn = snapshots[m, roundNumberIndex];
            if (!rn.IsActive)
            {
                continue;
            }

            int? round = ParseRound(rn);
            if (round is null or < 1)
            {
                continue;
            }

            lastIndexByRound[round.Value] = m;
        }

        List<RoundSample> samples = new(lastIndexByRound.Count);
        foreach (KeyValuePair<int, int> kv in lastIndexByRound)
        {
            samples.Add(new RoundSample(kv.Key, kv.Value));
        }

        samples.Sort(static (a, b) => a.RoundNumber.CompareTo(b.RoundNumber));
        return samples;
    }

    private static int? ParseRound(NodeSnapshot snap)
    {
        if (snap.NumericValue is { } numeric)
        {
            return (int)numeric;
        }

        if (snap.DisplayValue is { } display
            && int.TryParse(display, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
        {
            return parsed;
        }

        return null;
    }

    /// <summary>
    ///     Only per-round-resetting columns belong in the per-round table. Uses the builder-stamped
    ///     flag, NOT a node-type check: wrapper-reset logic nodes (e.g. kast.yaml's has_kast) are
    ///     round-scoped without implementing <see cref="IRoundScopedNode" />.
    /// </summary>
    private static bool IsRoundScoped(PerPlayerColumnAssignment col) => col.IsRoundScoped;

    private readonly record struct RoundSample(int RoundNumber, int SnapshotIndex);
}
