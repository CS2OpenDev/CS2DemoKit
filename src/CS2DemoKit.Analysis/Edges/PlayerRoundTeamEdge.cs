#region

using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Analysis.Building;
using CS2DemoKit.Analysis.Nodes;
using CS2DemoKit.Parser.GameEvents;

#endregion

namespace CS2DemoKit.Analysis.Edges;

/// <summary>
///     Latches a player's team at each <c>round_freeze_end</c>, the team a <c>player_round</c> row
///     reports. The round's sample is taken just before the next freeze end, after a halftime swap, so
///     the live team would put the last round of a half on the wrong side.
/// </summary>
internal sealed class PlayerRoundTeamEdge(
    StateNode source,
    PlayerContextIndex playerContext,
    int slot,
    GenericRoundScopedValueNode<int> roundTeam) : StateEdge(source)
{
    /// <inheritdoc />
    public override EdgeEffect? DeclaredEffect => EdgeEffect.SetValue;

    /// <inheritdoc />
    public override Type MessageType => typeof(RoundFreezeEndEvent);

    /// <inheritdoc />
    public override StateNode? WrittenNode => roundTeam;

    /// <inheritdoc />
    public override bool TryApply(EvaluationContext context)
    {
        if (context.Message is not GameEventMessage { DecodedEvent.Payload: RoundFreezeEndEvent })
        {
            return false;
        }

        roundTeam.SetValue(playerContext.GetCurrentTeam(slot));
        return true;
    }
}
