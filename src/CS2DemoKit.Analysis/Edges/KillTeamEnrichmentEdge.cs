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
    TransientValueNode<int> killTicksSinceSpot,
    TransientBoolNode wasEnemyAssist) : StateEdge(source)
{
    private const int FlashKillWindowTicks = 320;

    /// <inheritdoc />
    public override IReadOnlyList<StateNode>? AdditionalWrittenNodes =>
        [
            wasTeamKill, wasSelfKill, wasTradeKill, tradedPlayerSlot, wasFlashKill, flashAttackerSlot,
            wasEnemyAssist, killTicksSinceSpot
        ];

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

        // Ticks from the killer's last enemy contact to this kill, for time-to-kill.
        //
        // The FRAME HEADER tick, not this event's own GameTick. Both are the frame clock, but they
        // are not the same INSTANT: the server stamps an event during its simulation and the frame
        // carrying it can be the next one (measured on the bundled sample, EnemySpottedEvent's clock
        // note: 812 of 3,006 parsed events sit one tick below their frame's header, player_death 21
        // of 22). LastSpotTick is latched from a SYNTHESIZED enemy_spotted, which has no simulation
        // stamp and lands exactly on its frame, so the frame header is the instant it is comparable
        // against. Differencing the event tick instead reads every interval one tick (15.6 ms at
        // 64-tick) short, and a killer who spots and kills inside the same frame lands at -1, fails
        // the guard and is dropped to the sentinel — the fastest time-to-kill in the demo, removed
        // from the population rather than recorded as 0. AimShotContextEdge.EmitSpotPairing measures
        // enrich.shot.ticks_since_spot off the same anchor on the same clock, so the shot-anchored
        // and kill-anchored intervals stay comparable.
        //
        // The trade window, the flash window and RecordDeath keep currentTick: their anchors
        // (LastDeathTick, written here; BlindedAtTick, written by BlindEnrichmentEdge from the
        // blind event's own GameTick) are parsed-event stamps, so those three are already
        // self-consistent and moving them would introduce the mismatch this line removes.
        int frameTick = context.Frame.ServerTick;
        killTicksSinceSpot.SetValue(
            playerContext.TryGet(death.Attacker, out PlayerContextIndex.PlayerContext? killer)
            && killer!.LastSpotTick >= 0
            && frameTick >= killer.LastSpotTick
                ? frameTick - killer.LastSpotTick
                : AimShotContextEdge.NoSpotSentinel);

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
        // matching the reference goldens — verified on the nuke bench demo, tick 89840).
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
