#region

using System.Globalization;
using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Analysis.Config;
using CS2DemoKit.Analysis.RulesetsV2.Model;
using CS2DemoKit.Parser;

#endregion

namespace CS2DemoKit.Analysis.Output;

/// <summary>
///     Projects one user-declared <see cref="OutputDef" /> (the YAML <c>outputs:</c> schema) into a
///     <see cref="MetricTable" /> named after the output id. Configured outputs are <b>additive</b>
///     — the three built-in tables always emit; this projector only adds tables.
///     <para>
///         Scope picks the sampling strategy, mirroring the built-in projectors' semantics:
///         <see cref="OutputScope.PerPlayerPerGame" /> samples the final snapshot (the
///         <c>player_game_stats</c> shape), <see cref="OutputScope.PerPlayerPerRound" /> samples the
///         last snapshot of each live round via the engine's <c>round_number</c> counter (the
///         <c>player_round_stats</c> shape), and <see cref="OutputScope.PerEvent" /> logs timeline
///         rising edges filtered to the declared chains (the <c>rule_chain_events</c> shape,
///         <c>_chain_{id}</c> join-key discipline).
///     </para>
///     <para>
///         Metric references resolve per player through
///         <see cref="PerPlayerNodeTemplate.MaterializedPlayer.NodesByRuleId" /> first (bare ids and
///         <c>chain.rule</c> qualified aliases), then fall back to the build's game-scoped
///         <c>GameNodesByRuleId</c> — so a per-player table can include game-scoped metrics (same
///         value on every row). References were validated at build time; an unresolvable node here
///         (e.g. a rule skipped by <c>requires:</c> on this demo's profile) reads as null, matching
///         the columns convention. Only the declared dimensions are emitted, in declared order;
///         <c>match_id</c> is omitted per row when <see cref="MatchId" /> is null (the built-ins'
///         convention).
///     </para>
/// </summary>
public sealed class ConfiguredOutputProjector : IOutputProjector
{
    private const string ChainKeyPrefix = "_chain_";

    private const string DimMatchId = "match_id";
    private const string DimMap = "map";
    private const string DimRoundNumber = "round_number";
    private const string DimPlayerSlot = "player_slot";
    private const string DimPlayerName = "player_name";
    private const string DimTeam = "team";
    private const string DimTick = "tick";
    private const string DimFrameIndex = "frame_index";
    private const string DimChain = "chain";
    private const string DimSide = "side";
    private const string DimSlots = "slots";

    private readonly IReadOnlyDictionary<string, StateNode>? _gameNodesByRuleId;
    private readonly OutputDef _output;

    /// <param name="output">The validated output declaration to project.</param>
    /// <param name="gameNodesByRuleId">
    ///     The build's game-scoped rule-id → node map (<c>BuildResult.GameNodesByRuleId</c>), used as
    ///     the fallback when a metric reference is not per-player. Optional — without it only
    ///     per-player references resolve.
    /// </param>
    public ConfiguredOutputProjector(
        OutputDef output,
        IReadOnlyDictionary<string, StateNode>? gameNodesByRuleId = null)
    {
        ArgumentNullException.ThrowIfNull(output);
        _output = output;
        _gameNodesByRuleId = gameNodesByRuleId;
    }

    /// <summary>
    ///     The per-side node maps of a build's <c>for: each_team</c> rulesets
    ///     (<c>BuildResult.TeamNodesByRuleId</c>), which a team table's columns resolve against.
    /// </summary>
    public IReadOnlyDictionary<int, IReadOnlyDictionary<string, StateNode>>? TeamNodesByRuleId { get; init; }

    /// <summary>The per-side roster nodes (<c>BuildResult.TeamRosterNodes</c>) a team table's <c>slots</c> reads.</summary>
    public IReadOnlyDictionary<int, StateNode>? TeamRosterNodes { get; init; }

    /// <summary>
    ///     The match identifier used in the <c>match_id</c> dimension (typically the demo filename).
    ///     Optional — when null the dimension is omitted, matching the built-in projectors.
    /// </summary>
    public string? MatchId { get; init; }

    /// <inheritdoc />
    public IReadOnlyList<MetricTable> Project(EvaluationResult result, DemoDescriptor demo)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(demo);

        if (_output.Scope == OutputScope.PerEvent)
        {
            return [WithColumnClocks(ProjectPerEvent(result, demo), _output)];
        }

        return Project(ProjectionSource.FromSnapshots(result), demo);
    }

    /// <summary>
    ///     Projects a state-sampled table (every scope but <see cref="OutputScope.PerEvent" />) from a
    ///     <see cref="ProjectionSource" />: the snapshot table of a snapshot run, or what a
    ///     <see cref="ConfiguredOutputRecorder" /> sampled at the round boundaries of a forward run.
    ///     The two agree row for row; the rows are built by the same code from the same rule.
    /// </summary>
    internal IReadOnlyList<MetricTable> Project(ProjectionSource source, DemoDescriptor demo)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(demo);

        MetricTable table = _output.Scope switch
        {
            OutputScope.PerPlayerPerGame => ProjectPerPlayerPerGame(source, demo),
            OutputScope.PerPlayerPerRound => ProjectPerPlayerPerRound(source, demo),
            OutputScope.PerMatch => ProjectPerMatch(source, demo),
            OutputScope.PerTeamPerRound => ProjectPerTeam(source, demo, true),
            OutputScope.PerTeamPerGame => ProjectPerTeam(source, demo, false),
            _ => throw new InvalidOperationException(
                $"output '{_output.Id}' logs timeline events, which only a snapshot run records.")
        };

        return [WithColumnClocks(table, _output)];
    }

    /// <summary>
    ///     Stamps <see cref="MetricTable.ColumnClocks" /> from the output's metric refs: one entry per
    ///     bare tick column, labelled with the clock the resolver classified it on.
    /// </summary>
    internal static MetricTable WithColumnClocks(MetricTable table, OutputDef output)
    {
        Dictionary<string, string> clocks = new(StringComparer.Ordinal);
        foreach (MetricRef metric in output.Metrics)
        {
            switch (metric.Clock)
            {
                case TickClock.Frame:
                    clocks[metric.Label] = MetricTable.FrameClock;
                    break;
                case TickClock.Server:
                    clocks[metric.Label] = MetricTable.ServerClock;
                    break;
            }
        }

        return clocks.Count == 0 ? table : table with { ColumnClocks = clocks };
    }

    // ── per_match: a single match-level row of game-scoped metrics ─────────

    /// <summary>
    ///     Projects a single match-level row (a <c>for: match</c> ruleset's <c>show: tables</c>,
    ///     <c>per: match</c>). Every metric resolves against the build's game-scoped node map
    ///     (<see cref="_gameNodesByRuleId" />) and is read from the final state — the same
    ///     final-snapshot sampling <see cref="ProjectPerPlayerPerGame" /> uses, minus the per-player
    ///     fan-out. When there are no game-scoped nodes (no <see cref="_gameNodesByRuleId" />) the table
    ///     is still emitted, with null value cells (the columns convention).
    /// </summary>
    private MetricTable ProjectPerMatch(ProjectionSource source, DemoDescriptor demo)
    {
        List<string> valueColumns = _output.Metrics.Select(m => m.Label).ToList();
        Func<StateNode, object?>? final = source.Final;

        Dictionary<string, object?> dimensions = new(StringComparer.Ordinal);
        foreach (string dimension in _output.Dimensions)
        {
            switch (dimension)
            {
                case DimMatchId:
                    if (MatchId is not null)
                    {
                        dimensions[DimMatchId] = MatchId;
                    }

                    break;
                case DimMap:
                    dimensions[DimMap] = demo.MapName;
                    break;
            }
        }

        Dictionary<string, object?> values = new(StringComparer.Ordinal);
        foreach (MetricRef metric in _output.Metrics)
        {
            StateNode? node = _gameNodesByRuleId is not null
                              && _gameNodesByRuleId.TryGetValue(metric.RuleRef, out StateNode? gameNode)
                ? gameNode
                : null;
            values[metric.Label] = node is not null && final is not null
                ? StatValues.ApplyColumnFormat(final(node), metric.Format, demo.TickRate)
                : null;
        }

        return new MetricTable(_output.Id, _output.Dimensions, valueColumns, [new MetricRow(dimensions, values)]);
    }

    // ── per_team: one row per side, per round or for the match ─────────────

    /// <summary>
    ///     Projects a <c>for: each_team</c> ruleset's table: one row per side (2 then 3), for each live
    ///     round (sampled like <see cref="ProjectPerPlayerPerRound" />: the last state holding the
    ///     round) or once at the final state. Columns resolve against the side's node map; the
    ///     <c>slots</c> dimension is the side's roster node read from the same state.
    /// </summary>
    private MetricTable ProjectPerTeam(ProjectionSource source, DemoDescriptor demo, bool perRound)
    {
        List<string> valueColumns = _output.Metrics.Select(m => m.Label).ToList();
        List<MetricRow> rows = [];

        List<(int? Round, Func<StateNode, object?> Read)> samples = perRound
            ? source.Rounds.Select(r => ((int?)r.RoundNumber, r.Read)).ToList()
            : source.Final is { } final
                ? [(null, final)]
                : [];

        foreach ((int? round, Func<StateNode, object?> read) in samples)
        {
            foreach (int side in (int[])[2, 3])
            {
                rows.Add(BuildTeamRow(side, demo, read, round));
            }
        }

        return new MetricTable(_output.Id, _output.Dimensions, valueColumns, rows);
    }

    private MetricRow BuildTeamRow(int side, DemoDescriptor demo, Func<StateNode, object?> read, int? roundNumber)
    {
        IReadOnlyDictionary<string, StateNode>? sideNodes =
            TeamNodesByRuleId is not null && TeamNodesByRuleId.TryGetValue(side, out IReadOnlyDictionary<string, StateNode>? map)
                ? map
                : null;

        Dictionary<string, object?> dimensions = new(StringComparer.Ordinal);
        foreach (string dimension in _output.Dimensions)
        {
            switch (dimension)
            {
                case DimMatchId:
                    if (MatchId is not null)
                    {
                        dimensions[DimMatchId] = MatchId;
                    }

                    break;
                case DimMap:
                    dimensions[DimMap] = demo.MapName;
                    break;
                case DimRoundNumber:
                    if (roundNumber is { } round)
                    {
                        dimensions[DimRoundNumber] = round;
                    }

                    break;
                case DimSide:
                    dimensions[DimSide] = side;
                    break;
                case DimSlots:
                    dimensions[DimSlots] = TeamRosterNodes is not null && TeamRosterNodes.TryGetValue(side, out StateNode? roster)
                        ? read(roster)
                        : null;
                    break;
            }
        }

        Dictionary<string, object?> values = new(StringComparer.Ordinal);
        foreach (MetricRef metric in _output.Metrics)
        {
            StateNode? node = sideNodes is not null && sideNodes.TryGetValue(metric.RuleRef, out StateNode? sideNode)
                ? sideNode
                : _gameNodesByRuleId is not null && _gameNodesByRuleId.TryGetValue(metric.RuleRef, out StateNode? gameNode)
                    ? gameNode
                    : null;
            values[metric.Label] = node is not null
                ? StatValues.ApplyColumnFormat(read(node), metric.Format, demo.TickRate)
                : null;
        }

        return new MetricRow(dimensions, values);
    }

    // ── per_player_per_game: final-state sampling ─────────────────────────

    private MetricTable ProjectPerPlayerPerGame(ProjectionSource source, DemoDescriptor demo)
    {
        List<string> valueColumns = _output.Metrics.Select(m => m.Label).ToList();

        List<List<PerPlayerNodeTemplate.MaterializedPlayer>> playerGroups = GroupBySlot(source.Players);
        List<MetricRow> rows = new(playerGroups.Count);
        if (source.Final is { } final)
        {
            foreach (List<PerPlayerNodeTemplate.MaterializedPlayer> group in playerGroups)
            {
                rows.Add(BuildPlayerRow(group, demo, final, null));
            }
        }

        return new MetricTable(_output.Id, _output.Dimensions, valueColumns, rows);
    }

    // ── per_player_per_round: last-state-per-live-round sampling ──────────

    private MetricTable ProjectPerPlayerPerRound(ProjectionSource source, DemoDescriptor demo)
    {
        List<string> valueColumns = _output.Metrics.Select(m => m.Label).ToList();

        List<List<PerPlayerNodeTemplate.MaterializedPlayer>> playerGroups = GroupBySlot(source.Players);
        List<MetricRow> rows = new(source.Rounds.Count * Math.Max(1, playerGroups.Count));
        foreach ((int roundNumber, Func<StateNode, object?> read) in source.Rounds)
        {
            foreach (List<PerPlayerNodeTemplate.MaterializedPlayer> group in playerGroups)
            {
                rows.Add(BuildPlayerRow(group, demo, read, roundNumber));
            }
        }

        return new MetricTable(_output.Id, _output.Dimensions, valueColumns, rows);
    }

    // ── per_event: timeline rising edges filtered to the declared chains ─

    private MetricTable ProjectPerEvent(EvaluationResult result, DemoDescriptor demo)
    {
        HashSet<string> wantedChains = new(_output.Chains ?? [], StringComparer.Ordinal);
        bool wantRound = _output.Dimensions.Contains(DimRoundNumber, StringComparer.Ordinal);
        Dictionary<int, int> roundByFrame = wantRound
            ? BuildRoundByFrame(result, demo)
            : [];

        List<MetricRow> rows = new();
        foreach (RuleChainEvent ev in result.Timeline.Events)
        {
            if (!ev.ChainName.StartsWith(ChainKeyPrefix, StringComparison.Ordinal))
            {
                continue; // internal logic-node rising edge, not a chain satisfaction
            }

            string chainId = ev.ChainName[ChainKeyPrefix.Length..];
            if (!wantedChains.Contains(chainId))
            {
                continue;
            }

            Dictionary<string, object?> dimensions = new(StringComparer.Ordinal);
            foreach (string dimension in _output.Dimensions)
            {
                switch (dimension)
                {
                    case DimMatchId:
                        if (MatchId is not null)
                        {
                            dimensions[DimMatchId] = MatchId;
                        }

                        break;
                    case DimMap:
                        dimensions[DimMap] = demo.MapName;
                        break;
                    case DimChain:
                        dimensions[DimChain] = chainId;
                        break;
                    case DimRoundNumber:
                        dimensions[DimRoundNumber] = roundByFrame.GetValueOrDefault(ev.FrameIndex, 0);
                        break;
                    case DimFrameIndex:
                        dimensions[DimFrameIndex] = ev.FrameIndex;
                        break;
                    case DimTick:
                        dimensions[DimTick] = ev.Tick;
                        break;
                }
            }

            rows.Add(new MetricRow(dimensions, new Dictionary<string, object?>(StringComparer.Ordinal)));
        }

        return new MetricTable(_output.Id, _output.Dimensions, [], rows);
    }

    // ── Shared row/cell helpers ───────────────────────────────────────────

    /// <summary>
    ///     Groups the materialized players by slot, preserving first-occurrence order. A v1 config
    ///     and a v2 ruleset materialize the SAME human as SEPARATE template players (one per
    ///     template — the evaluator's MaterializedPlayers count doubles when both are in play). A
    ///     per-player table is keyed by the human, so the groups — not the raw materializations —
    ///     are its rows; each metric resolves against whichever member's template declared it.
    ///     Single-template builds produce groups of one and project byte-identically. Ungrouped,
    ///     every player rendered one half-empty row per template (measured: a 10-player demo
    ///     produced 20 rows).
    /// </summary>
    private static List<List<PerPlayerNodeTemplate.MaterializedPlayer>> GroupBySlot(
        IReadOnlyList<PerPlayerNodeTemplate.MaterializedPlayer> players)
    {
        List<List<PerPlayerNodeTemplate.MaterializedPlayer>> groups = new();
        Dictionary<int, List<PerPlayerNodeTemplate.MaterializedPlayer>> bySlot = new();
        foreach (PerPlayerNodeTemplate.MaterializedPlayer mp in players)
        {
            if (!bySlot.TryGetValue(mp.PlayerSlot, out List<PerPlayerNodeTemplate.MaterializedPlayer>? group))
            {
                bySlot[mp.PlayerSlot] = group = new List<PerPlayerNodeTemplate.MaterializedPlayer>();
                groups.Add(group);
            }

            group.Add(mp);
        }

        return groups;
    }

    private MetricRow BuildPlayerRow(
        List<PerPlayerNodeTemplate.MaterializedPlayer> group,
        DemoDescriptor demo,
        Func<StateNode, object?> read,
        int? roundNumber)
    {
        // Identity dimensions come from the first materialization — slot is identical across the
        // group by construction, and the first member's name matches what the ungrouped
        // projection led with.
        PerPlayerNodeTemplate.MaterializedPlayer mp = group[0];
        Dictionary<string, object?> dimensions = new(StringComparer.Ordinal);
        foreach (string dimension in _output.Dimensions)
        {
            switch (dimension)
            {
                case DimMatchId:
                    if (MatchId is not null)
                    {
                        dimensions[DimMatchId] = MatchId;
                    }

                    break;
                case DimMap:
                    dimensions[DimMap] = demo.MapName;
                    break;
                case DimRoundNumber:
                    if (roundNumber is { } round)
                    {
                        dimensions[DimRoundNumber] = round;
                    }

                    break;
                case DimPlayerSlot:
                    dimensions[DimPlayerSlot] = mp.PlayerSlot;
                    break;
                case DimPlayerName:
                    dimensions[DimPlayerName] = mp.PlayerName;
                    break;
                case DimTeam:
                    dimensions[DimTeam] = demo.Players.TryGetValue(mp.PlayerSlot, out PlayerInfo? pi) ? pi.Team : 0;
                    break;
            }
        }

        Dictionary<string, object?> values = new(StringComparer.Ordinal);
        foreach (MetricRef metric in _output.Metrics)
        {
            StateNode? node = ResolveMetricNode(group, metric.RuleRef);
            values[metric.Label] = node is not null
                ? StatValues.ApplyColumnFormat(read(node), metric.Format, demo.TickRate)
                : null;
        }

        return new MetricRow(dimensions, values);
    }

    /// <summary>
    ///     Per-player lookup first (bare ids + qualified aliases) — across every template
    ///     materialization of this player, in materialization order — then the game-scope
    ///     fallback. The cross-member walk is what lets one output row mix v1-chain and
    ///     v2-ruleset metrics: each resolves on the template that declared it.
    /// </summary>
    private StateNode? ResolveMetricNode(
        List<PerPlayerNodeTemplate.MaterializedPlayer> group, string ruleRef)
    {
        foreach (PerPlayerNodeTemplate.MaterializedPlayer mp in group)
        {
            if (mp.NodesByRuleId is not null && mp.NodesByRuleId.TryGetValue(ruleRef, out StateNode? playerNode))
            {
                return playerNode;
            }
        }

        if (_gameNodesByRuleId is not null && _gameNodesByRuleId.TryGetValue(ruleRef, out StateNode? gameNode))
        {
            return gameNode;
        }

        return null;
    }

    // ── Round-boundary sampling (borrowed from PlayerRoundStatsProjector) ─

    /// <summary>
    ///     Walk the snapshots once, recording for each distinct live round value
    ///     (<c>round_number &gt;= 1</c>) the last snapshot index at which it was held. Returns the
    ///     samples ordered by round number. Round-scoped nodes reset on the <i>next</i> round's
    ///     freeze-end, so the last index holding round <c>r</c> captures its end-of-round values.
    /// </summary>
    internal static List<RoundSample> CollectRoundSamples(SnapshotTable snapshots, int roundNumberIndex)
    {
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

    // ── Event round attribution (borrowed from RuleChainEventProjector) ──

    /// <summary>
    ///     One pass over the messages: the live <c>round_number</c> value at each message's frame
    ///     index (later messages in a frame win). Frames between messages inherit nothing; lookups
    ///     default to 0 (warmup/unknown).
    /// </summary>
    private static Dictionary<int, int> BuildRoundByFrame(EvaluationResult result, DemoDescriptor demo)
    {
        Dictionary<int, int> roundByFrame = new();

        int roundIdx = StatValues.FindRoundNumberIndex(result.FinalTrackedNodes);
        if (roundIdx < 0 || result.Messages.Count == 0)
        {
            return roundByFrame;
        }

        int currentRound = 0;
        for (int m = 0; m < result.Messages.Count; m++)
        {
            if (result.MessageSnapshots[m, roundIdx] is { IsActive: true, NumericValue: { } rn })
            {
                currentRound = (int)rn;
            }

            roundByFrame[result.Messages[m].FrameIndex] = currentRound;
        }

        return roundByFrame;
    }

    internal readonly record struct RoundSample(int RoundNumber, int SnapshotIndex);
}

/// <summary>
///     What a state-sampled configured table is projected from: one reader per live round (the state
///     that last held the round), a reader of the final state, and the players materialized over the
///     run. A reader maps a node to the cell value it had in that state, or null when the node was not
///     tracked, inactive, or absent.
/// </summary>
/// <param name="Rounds">Each live round's state, ordered by round number.</param>
/// <param name="Final">The final state, or null when the run dispatched nothing.</param>
/// <param name="Players">Every materialized player, in materialization order.</param>
internal sealed record ProjectionSource(
    IReadOnlyList<(int RoundNumber, Func<StateNode, object?> Read)> Rounds,
    Func<StateNode, object?>? Final,
    IReadOnlyList<PerPlayerNodeTemplate.MaterializedPlayer> Players)
{
    /// <summary>
    ///     The snapshot run's source: rounds sampled at the last snapshot holding each live round
    ///     number, the final state at the last snapshot, cells read off the snapshot rows.
    /// </summary>
    public static ProjectionSource FromSnapshots(EvaluationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        Dictionary<StateNode, int> nodeIndex = StatValues.BuildNodeIndex(result.FinalTrackedNodes);

        Func<StateNode, object?> Reader(int row)
        {
            NodeSnapshot[]? snapshot = null;
            return node =>
            {
                snapshot ??= result.MessageSnapshots.MaterializeRow(row);
                return StatValues.ReadColumnValue(snapshot, nodeIndex, node);
            };
        }

        List<(int, Func<StateNode, object?>)> rounds = [];
        int roundNumberIndex = StatValues.FindRoundNumberIndex(result.FinalTrackedNodes);
        if (roundNumberIndex >= 0)
        {
            foreach (ConfiguredOutputProjector.RoundSample round in
                     ConfiguredOutputProjector.CollectRoundSamples(result.MessageSnapshots, roundNumberIndex))
            {
                rounds.Add((round.RoundNumber, Reader(round.SnapshotIndex)));
            }
        }

        Func<StateNode, object?>? final = result.MessageSnapshots.Count > 0
            ? Reader(result.MessageSnapshots.Count - 1)
            : null;

        return new ProjectionSource(rounds, final, result.MaterializedPlayers);
    }
}
