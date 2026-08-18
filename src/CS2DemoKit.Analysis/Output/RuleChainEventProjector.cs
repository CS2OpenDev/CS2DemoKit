#region

using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Parser;

#endregion

namespace CS2DemoKit.Analysis.Output;

/// <summary>
///     Built-in projector that emits one <see cref="MetricRow" /> per chain satisfaction (rising
///     edge) — the achievement/event log the output design names (step 5): "chain
///     'deagle_hs_round' satisfied in round 7 at tick N". Rows are dimensions-only (there is no
///     measured value; the row's existence is the datum). Per-player satisfactions carry
///     <c>player_slot</c> / <c>player_name</c> from the evaluator's materialization-time stamp;
///     game-scoped chains have no owning player and omit both (same convention as <c>match_id</c>).
///     <para>
///         The timeline records every rising logic node; this projector keeps only real chain
///         satisfactions (the <c>_chain_{id}</c> join-key discipline — bare logic-rule names are
///         internal wiring) and strips the prefix for the <c>chain</c> dimension. Round attribution
///         walks the snapshots once, recording the live <c>round_number</c> at each message's frame;
///         an event maps to the last round value seen at-or-before its frame (0 = warmup/unknown).
///     </para>
/// </summary>
public sealed class RuleChainEventProjector : IOutputProjector
{
    /// <summary>The <see cref="MetricTable.Name" /> emitted by this projector.</summary>
    public const string TableName = "rule_chain_events";

    private const string ChainKeyPrefix = "_chain_";

    private const string DimMatchId = "match_id";
    private const string DimMap = "map";
    private const string DimChain = "chain";
    private const string DimPlayerSlot = "player_slot";
    private const string DimPlayerName = "player_name";
    private const string DimRoundNumber = "round_number";
    private const string DimFrameIndex = "frame_index";
    private const string DimTick = "tick";

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

        string[] dimensionColumns =
            [DimMatchId, DimMap, DimChain, DimPlayerSlot, DimPlayerName, DimRoundNumber, DimFrameIndex, DimTick];

        Dictionary<int, int> roundByFrame = BuildRoundByFrame(result, demo);

        List<MetricRow> rows = new();
        foreach (RuleChainEvent ev in result.Timeline.Events)
        {
            if (!ev.ChainName.StartsWith(ChainKeyPrefix, StringComparison.Ordinal))
            {
                continue; // internal logic-node rising edge, not a chain satisfaction
            }

            Dictionary<string, object?> dimensions = new(StringComparer.Ordinal)
            {
                [DimMap] = demo.MapName,
                [DimChain] = ev.ChainName[ChainKeyPrefix.Length..],
                [DimRoundNumber] = roundByFrame.GetValueOrDefault(ev.FrameIndex, 0),
                [DimFrameIndex] = ev.FrameIndex,
                [DimTick] = ev.Tick
            };
            if (MatchId is not null)
            {
                dimensions[DimMatchId] = MatchId;
            }

            if (ev.PlayerSlot is { } playerSlot)
            {
                dimensions[DimPlayerSlot] = playerSlot;
            }

            if (ev.PlayerName is { } playerName)
            {
                dimensions[DimPlayerName] = playerName;
            }

            rows.Add(new MetricRow(dimensions, new Dictionary<string, object?>(StringComparer.Ordinal)));
        }

        return [new MetricTable(TableName, dimensionColumns, [], rows)];
    }

    /// <summary>
    ///     One pass over the messages: the live <c>round_number</c> value at each message's frame
    ///     index (later messages in a frame win — matching how the evaluator stamps events during
    ///     the frame). Frames between messages inherit nothing; lookups default to 0.
    /// </summary>
    private static Dictionary<int, int> BuildRoundByFrame(EvaluationResult result, ParsedDemo demo)
    {
        Dictionary<int, int> roundByFrame = new();

        int roundIdx = StatValues.FindRoundNumberIndex(result.FinalTrackedNodes);
        if (roundIdx < 0 || result.Messages.Count == 0)
        {
            return roundByFrame;
        }

        Dictionary<DemoFrame, int> frameIndexByFrame = new(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < demo.Frames.Count; i++)
        {
            frameIndexByFrame[demo.Frames[i]] = i;
        }

        int currentRound = 0;
        for (int m = 0; m < result.Messages.Count; m++)
        {
            if (result.MessageSnapshots[m, roundIdx] is { IsActive: true, NumericValue: { } rn })
            {
                currentRound = (int)rn;
            }

            if (frameIndexByFrame.TryGetValue(result.Messages[m].Frame, out int fi))
            {
                roundByFrame[fi] = currentRound;
            }
        }

        return roundByFrame;
    }
}
