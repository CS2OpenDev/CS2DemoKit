#region

using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Analysis.Building;
using CS2DemoKit.Analysis.Events;
using CS2DemoKit.Parser.GameEvents;

#endregion

namespace CS2DemoKit.Analysis.Edges;

/// <summary>
///     Latches the server's verdict on the round into <see cref="PlayerContextIndex" /> when the
///     synthesized <c>round_decided</c> fires, so <see cref="RoundEndEnrichmentEdge" /> can report it at
///     the round's close instead of deriving a winner from bomb state and alive counts. The latch
///     clears with the rest of the round state at the next freeze end.
/// </summary>
public sealed class RoundDecidedEdge(StateNode source, PlayerContextIndex playerContext) : StateEdge(source)
{
    /// <inheritdoc />
    public override EdgeEffect? DeclaredEffect => null;

    /// <inheritdoc />
    public override Type MessageType => typeof(RoundDecidedEvent);

    /// <inheritdoc />
    public override bool TryApply(EvaluationContext context) =>
        context.Message is GameEventMessage { DecodedEvent: RoundDecidedEvent decided } && Latch(decided);

    /// <inheritdoc />
    public override bool TryApplyDirect(object payload, EvaluationContext context) =>
        payload is RoundDecidedEvent decided && Latch(decided);

    private bool Latch(RoundDecidedEvent decided)
    {
        playerContext.DecidedWinnerSide = decided.Winner;
        playerContext.DecidedReason = decided.Reason;

        // Nothing graph-visible is written: the latch is read by the round-end enrichment edge.
        return false;
    }
}
