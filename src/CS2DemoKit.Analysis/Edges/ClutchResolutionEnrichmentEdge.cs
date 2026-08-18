#region

using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Analysis.Building;
using CS2DemoKit.Analysis.Nodes;

#endregion

namespace CS2DemoKit.Analysis.Edges;

/// <summary>
///     Fires on a round-end event (one instance per concrete event in the
///     active profile's <c>$round_end</c> binding). Checks all players for
///     clutch wins (IsInClutch AND IsAlive at round end). Idempotent: writes
///     transients only, so multi-event subscription is safe without a guard.
/// </summary>
public sealed class ClutchResolutionEnrichmentEdge(
    StateNode source,
    PlayerContextIndex playerContext,
    TransientBoolNode clutchWon,
    TransientValueNode<int> winnerSlot,
    Type messageType) : StateEdge(source)
{
    /// <inheritdoc />
    public override IReadOnlyList<StateNode>? AdditionalWrittenNodes => [winnerSlot];

    /// <inheritdoc />
    public override EdgeEffect? DeclaredEffect => EdgeEffect.Activate;

    /// <inheritdoc />
    public override Type MessageType => messageType;

    /// <inheritdoc />
    public override StateNode? WrittenNode => clutchWon;

    /// <inheritdoc />
    public override bool TryApply(EvaluationContext context) => false;

    /// <inheritdoc />
    public override bool TryApplyDirect(object payload, EvaluationContext context)
    {
        foreach (PlayerContextIndex.PlayerContext player in playerContext.AllPlayers)
        {
            if (player is { IsInClutch: true, IsAlive: true })
            {
                clutchWon.Activate();
                winnerSlot.SetValue(player.Slot);
                return true;
            }
        }

        return false;
    }
}
