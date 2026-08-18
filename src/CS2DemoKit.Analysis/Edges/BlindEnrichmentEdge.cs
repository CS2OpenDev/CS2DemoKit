#region

using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Analysis.Building;
using CS2DemoKit.Analysis.Nodes;
using CS2DemoKit.Parser.GameEvents;

#endregion

namespace CS2DemoKit.Analysis.Edges;

/// <summary>
///     Enrichment edge that fires on <see cref="PlayerBlindEvent" />. Classifies
///     the flash as enemy/team and records blind state in PlayerContextIndex.
/// </summary>
public sealed class BlindEnrichmentEdge(
    StateNode source,
    PlayerContextIndex playerContext,
    TransientBoolNode wasEnemyFlash,
    TransientValueNode<double> duration) : StateEdge(source)
{
    /// <inheritdoc />
    public override IReadOnlyList<StateNode>? AdditionalWrittenNodes => [duration];

    /// <inheritdoc />
    public override EdgeEffect? DeclaredEffect => EdgeEffect.Activate;

    /// <inheritdoc />
    public override Type MessageType => typeof(PlayerBlindEvent);

    /// <inheritdoc />
    public override StateNode? WrittenNode => wasEnemyFlash;

    /// <inheritdoc />
    public override bool TryApply(EvaluationContext context)
    {
        if (context.Message is not GameEventMessage gem)
        {
            return false;
        }

        if (gem.DecodedEvent.Payload is not PlayerBlindEvent blind)
        {
            return false;
        }

        int attackerTeam = GetTeam(blind.Attacker);
        int victimTeam = GetTeam(blind.UserId);

        if (attackerTeam > 1 && attackerTeam != victimTeam && victimTeam > 1)
        {
            wasEnemyFlash.Activate();
            duration.SetValue(blind.BlindDuration);
            playerContext.RecordBlind(blind.UserId, blind.Attacker, context.Fire!.GameTick);
        }

        return true;
    }

    private int GetTeam(int slot) =>
        playerContext.TryGet(slot, out PlayerContextIndex.PlayerContext? ctx) ? ctx!.Team : 0;
}
