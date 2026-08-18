#region

using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Analysis.Building;
using CS2DemoKit.Analysis.Nodes;
using CS2DemoKit.Parser.GameEvents;

#endregion

namespace CS2DemoKit.Analysis.Edges;

/// <summary>
///     Fires on <see cref="PlayerDeathEvent" /> AFTER <see cref="KillTeamEnrichmentEdge" />
///     (which marks the victim dead). Checks if any surviving player is now in a 1vN clutch.
/// </summary>
public sealed class ClutchEnrichmentEdge(
    StateNode source,
    PlayerContextIndex playerContext,
    TransientBoolNode clutchDetected,
    TransientValueNode<int> clutchPlayerSlot) : StateEdge(source)
{
    /// <inheritdoc />
    public override IReadOnlyList<StateNode>? AdditionalWrittenNodes => [clutchPlayerSlot];

    /// <inheritdoc />
    public override EdgeEffect? DeclaredEffect => EdgeEffect.Activate;

    /// <inheritdoc />
    public override Type MessageType => typeof(PlayerDeathEvent);

    /// <inheritdoc />
    public override StateNode? WrittenNode => clutchDetected;

    /// <inheritdoc />
    public override bool TryApply(EvaluationContext context)
    {
        if (context.Message is not GameEventMessage gem)
        {
            return false;
        }

        if (gem.DecodedEvent.Payload is not PlayerDeathEvent death)
        {
            return false;
        }

        if (death.Attacker == death.UserId)
        {
            return false;
        }

        if (!playerContext.TryGet(death.UserId, out PlayerContextIndex.PlayerContext? victimCtx))
        {
            return false;
        }

        int victimTeam = victimCtx!.Team;
        if (victimTeam < 2)
        {
            return false;
        }

        int loneSlot = playerContext.FindLoneAlive(victimTeam);
        if (loneSlot < 0)
        {
            return false;
        }

        int enemyTeam = victimTeam == 2 ? 3 : 2;
        int enemiesAlive = playerContext.CountAlive(enemyTeam);
        if (enemiesAlive <= 0)
        {
            return false;
        }

        if (playerContext.TryGet(loneSlot, out PlayerContextIndex.PlayerContext? survivorCtx) && !survivorCtx!.IsInClutch)
        {
            survivorCtx.IsInClutch = true;
            survivorCtx.ClutchOpponents = enemiesAlive; // the N of the 1vN, captured at clutch entry
            clutchDetected.Activate();
            clutchPlayerSlot.SetValue(loneSlot);
            return true;
        }

        return false;
    }
}
