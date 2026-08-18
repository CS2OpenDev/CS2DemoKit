#region

using CS2DemoKit.Analysis.Abstractions;

#endregion

namespace CS2DemoKit.Analysis.Edges;

/// <summary>
///     At round end, reads a source value node and increments the first target counter
///     whose threshold is met. Thresholds are checked in descending order (highest first)
///     so a 5-kill round only increments the 5K bucket, not 4K/3K/2K.
///     One instance per concrete event in the active profile's <c>$round_end</c>
///     binding; non-idempotent — uses a shared first-wins-per-round suppression
///     guard so only the first concrete event per round increments the bucket.
/// </summary>
public sealed class ThresholdTallyEdge(
    StateNode gateSource,
    ValueNode<int> source,
    (int Threshold, ValueNode<int> Target)[] thresholds,
    Type messageType,
    BoolNode? suppressionGuard = null) : StateEdge(gateSource)
{
    private IReadOnlyList<StateNode>? _writtenNodes;

    /// <inheritdoc />
    public override EdgeEffect? DeclaredEffect => EdgeEffect.SetValue;

    /// <summary>
    ///     All bucket targets (plus the shared guard) declared as potentially written — only one
    ///     fires per round, but undeclared writes freeze the 2K–5K snapshot columns at 0 (same
    ///     class as the ComputeOnRoundEndEdge scoreboard bug); over-marking just re-reads a few
    ///     unchanged nodes into the next snapshot row.
    /// </summary>
    public override IReadOnlyList<StateNode>? AdditionalWrittenNodes =>
        _writtenNodes ??= suppressionGuard is null
            ? [.. thresholds.Select(t => (StateNode)t.Target)]
            : [.. thresholds.Select(t => (StateNode)t.Target), suppressionGuard];

    /// <inheritdoc />
    public override Type MessageType => messageType;

    /// <inheritdoc />
    public override bool TryApply(EvaluationContext context) => false;

    /// <inheritdoc />
    public override bool TryApplyDirect(object payload, EvaluationContext context)
    {
        if (suppressionGuard?.IsActive == true)
        {
            return false;
        }

        if (!source.IsActive)
        {
            return false;
        }

        int value = source.Value;
        foreach ((int threshold, ValueNode<int> target) in thresholds)
        {
            if (value >= threshold)
            {
                target.SetValue(target.Value + 1);
                suppressionGuard?.Activate();
                return true;
            }
        }

        return false;
    }
}
