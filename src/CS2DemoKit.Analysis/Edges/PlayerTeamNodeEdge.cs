#region

using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Analysis.Events;
using CS2DemoKit.Analysis.Nodes;
using CS2DemoKit.Parser.GameEvents;

#endregion

namespace CS2DemoKit.Analysis.Edges;

/// <summary>
///     Marks a <see cref="PlayerTeamNode" /> written when its slot's team can change: a
///     <c>player_team</c> for the slot, or the entity-observed team that seeds it. The node reads the
///     index itself; the write is what re-evaluates a <c>when:</c> or live <c>compute:</c> over it.
/// </summary>
internal sealed class PlayerTeamNodeEdge(StateNode source, PlayerTeamNode node, bool observed) : StateEdge(source)
{
    /// <inheritdoc />
    public override EdgeEffect? DeclaredEffect => EdgeEffect.SetValue;

    /// <inheritdoc />
    public override Type MessageType => observed ? typeof(PlayerTeamObservedEvent) : typeof(PlayerTeamEvent);

    /// <inheritdoc />
    public override StateNode? WrittenNode => node;

    /// <inheritdoc />
    public override bool TryApply(EvaluationContext context)
    {
        if (context.Message is not GameEventMessage gem)
        {
            return false;
        }

        return observed
            ? gem.DecodedEvent is PlayerTeamObservedEvent seen && seen.PlayerSlot == node.Slot
            : gem.DecodedEvent.Payload is PlayerTeamEvent team && team.UserId == node.Slot;
    }
}
