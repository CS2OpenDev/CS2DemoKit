#region

using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Analysis.Building;
using CS2DemoKit.Analysis.Nodes;
using CS2DemoKit.Parser.GameEvents;

#endregion

namespace CS2DemoKit.Analysis.Edges;

/// <summary>
///     Enrichment edge that fires on every <see cref="PlayerDeathEvent" /> and populates
///     shared transient nodes: team classification, trade detection, flash kill detection.
///     Also records death and marks victim dead in <see cref="PlayerContextIndex" />.
/// </summary>
public sealed class KillTeamEnrichmentEdge(
    StateNode source,
    PlayerContextIndex playerContext,
    TransientBoolNode wasEnemyKill,
    TransientBoolNode wasTeamKill,
    TransientBoolNode wasSelfKill,
    TransientBoolNode wasTradeKill,
    TransientValueNode<int> tradedPlayerSlot,
    TransientBoolNode wasFlashKill,
    TransientValueNode<int> flashAttackerSlot,
    TransientBoolNode wasEnemyAssist) : StateEdge(source)
{
    private const int FlashKillWindowTicks = 320;

    /// <inheritdoc />
    public override IReadOnlyList<StateNode>? AdditionalWrittenNodes =>
        [wasTeamKill, wasSelfKill, wasTradeKill, tradedPlayerSlot, wasFlashKill, flashAttackerSlot, wasEnemyAssist];

    /// <inheritdoc />
    public override EdgeEffect? DeclaredEffect => EdgeEffect.Activate;

    /// <inheritdoc />
    public override Type MessageType => typeof(PlayerDeathEvent);

    /// <inheritdoc />
    public override StateNode? WrittenNode => wasEnemyKill;

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

        int currentTick = gem.DecodedEvent.GameTick;

        // Trade detection (before recording this death)
        int tradedSlot = playerContext.FindTradedPlayer(
            death.UserId, death.Attacker, currentTick);
        if (tradedSlot >= 0)
        {
            wasTradeKill.Activate();
            tradedPlayerSlot.SetValue(tradedSlot);
        }

        // Flash kill detection (check if victim was blinded by a teammate of the killer)
        if (death.Attacker != death.UserId &&
            playerContext.TryGet(death.UserId, out PlayerContextIndex.PlayerContext? victimCtx) && victimCtx!.BlindedBySlot >= 0 &&
            currentTick - victimCtx.BlindedAtTick <= FlashKillWindowTicks)
        {
            int flasherTeam = GetTeam(victimCtx.BlindedBySlot);
            int killerTeam = GetTeam(death.Attacker);
            if (flasherTeam > 1 && flasherTeam == killerTeam)
            {
                wasFlashKill.Activate();
                flashAttackerSlot.SetValue(victimCtx.BlindedBySlot);
            }

            playerContext.ClearBlind(death.UserId);
        }

        // Record death and mark dead
        playerContext.RecordDeath(death.UserId, death.Attacker, death.Assister, currentTick);
        playerContext.MarkDead(death.UserId);

        int vTeam = GetTeam(death.UserId);

        // Assister relationship (S7 totalAssists fix): the assist view's `enemy` facet must test
        // the ASSISTER against the victim — was_enemy_kill tests killer-vs-victim, which
        // miscounts team-damage assists (assister on the victim's own team, killed by an enemy)
        // and drops enemy assists on teamkills. Computed independently of the kill-shape
        // classification below so teamkills still classify the assister correctly; suicide
        // exclusion stays at the view level (the assist view bakes Attacker != UserId,
        // matching the Leetify oracle — verified on the nuke bench demo, tick 89840).
        int aTeam = GetTeam(death.Assister);
        if (aTeam > 1 && vTeam > 1 && aTeam != vTeam)
        {
            wasEnemyAssist.Activate();
        }

        // Team classification
        if (death.Attacker == death.UserId)
        {
            wasSelfKill.Activate();
            return true;
        }

        int kTeam = GetTeam(death.Attacker);

        if (kTeam > 1 && kTeam == vTeam)
        {
            wasTeamKill.Activate();
        }
        else
        {
            wasEnemyKill.Activate();
        }

        return true;
    }

    private int GetTeam(int slot) =>
        playerContext.TryGet(slot, out PlayerContextIndex.PlayerContext? ctx) ? ctx!.Team : 0;
}
