#region

using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Analysis.Building;
using CS2DemoKit.Parser.GameEvents;

#endregion

namespace CS2DemoKit.Analysis.Edges;

/// <summary>
///     Resets all player health to 100 at the start of each round (round_freeze_end).
/// </summary>
public sealed class HealthResetEdge(StateNode source, PlayerContextIndex playerContext) : StateEdge(source)
{
    /// <inheritdoc />
    public override EdgeEffect? DeclaredEffect => null;

    /// <inheritdoc />
    public override Type MessageType => typeof(RoundFreezeEndEvent);

    /// <inheritdoc />
    public override bool TryApply(EvaluationContext context)
    {
        if (context.Message is not GameEventMessage gem)
        {
            return false;
        }

        if (gem.DecodedEvent.Payload is not RoundFreezeEndEvent)
        {
            return false;
        }

        playerContext.RoundNumber++;
        playerContext.ResetRoundState();
        return false;
    }
}
