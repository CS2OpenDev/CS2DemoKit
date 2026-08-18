#region

using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Analysis.Config;
using CS2DemoKit.Analysis.Edges;
using CS2DemoKit.Analysis.Nodes;
using CS2DemoKit.Analysis.Plugins;
using CS2DemoKit.Analysis.Profiles;
using CS2DemoKit.Analysis.Registry;

#endregion

namespace CS2DemoKit.Analysis.Building;

/// <summary>
///     Provides the framework's built-in enrichment infrastructure and context rules — the shared
///     transient nodes and rule chains that every demo source builds on. Consumed by
///     <see cref="RuleChainBuilder" />.
/// </summary>
public static class BuiltinContexts
{
    /// <summary>
    ///     Creates the shared enrichment infrastructure (transient nodes + enrichment edges)
    ///     for team-based event classification. These are game-scoped and shared across all
    ///     players. <paramref name="resolver" /> is used to resolve <c>$round_end</c>; each
    ///     round-end enrichment edge is instantiated once per concrete event in the active
    ///     profile's binding so it covers GOTV, HLTV, and end-of-demo uniformly.
    /// </summary>
    public static EnrichmentInfrastructure CreateEnrichment(
        StateNode graphRoot, PlayerContextIndex playerContext,
        EventRegistry registry, LogicalEventResolver resolver,
        EntityChangeScanner? entityScanner = null,
        IPerPlayerEntityValueProvider? pawnHealthProvider = null,
        IPerPlayerEntityValueProvider? activeWeaponProvider = null)
    {
        // Kill enrichment bools
        TransientBoolNode killEnemyKill = new("enrich.kill.was_enemy_kill");
        TransientBoolNode killTeamKill = new("enrich.kill.was_team_kill");
        TransientBoolNode killSelfKill = new("enrich.kill.was_self_kill");
        // S7 fix: assister-vs-victim enmity (the assist view's `enemy` facet reads this,
        // NOT was_enemy_kill, which tests killer-vs-victim).
        TransientBoolNode killEnemyAssist = new("enrich.kill.was_enemy_assist");

        // Trade enrichment
        TransientBoolNode wasTradeKill = new("enrich.kill.was_trade_kill");
        TransientValueNode<int> tradedPlayerSlot = new("enrich.kill.traded_player_slot", -1);

        // Hurt enrichment bools
        TransientBoolNode hurtEnemyDamage = new("enrich.hurt.was_enemy_damage");
        TransientBoolNode hurtTeamDamage = new("enrich.hurt.was_team_damage");
        TransientBoolNode hurtSelfDamage = new("enrich.hurt.was_self_damage");

        // Hurt enrichment values (health + capped damage + attacker weapon)
        TransientValueNode<int> victimHealthBefore = new("enrich.hurt.victim_health_before", 100);
        TransientValueNode<int> cappedDamage = new("enrich.hurt.capped_damage");
        TransientValueNode<string> attackerActiveWeapon = new("enrich.hurt.attacker_active_weapon", "");

        // Flash kill enrichment
        TransientBoolNode wasFlashKill = new("enrich.kill.was_flash_kill");
        TransientValueNode<int> flashAttackerSlot = new("enrich.kill.flash_attacker_slot", -1);

        // Blind enrichment
        TransientBoolNode blindWasEnemyFlash = new("enrich.blind.was_enemy_flash");
        TransientValueNode<double> blindDuration = new("enrich.blind.duration");

        // Clutch enrichment
        TransientBoolNode clutchDetected = new("enrich.kill.clutch_detected");
        TransientValueNode<int> clutchPlayerSlot = new("enrich.kill.clutch_player_slot", -1);
        TransientBoolNode clutchWon = new("enrich.clutch.was_clutch_won");
        TransientValueNode<int> clutchWinnerSlot = new("enrich.clutch.winner_slot", -1);

        // Edges (order matters: kill enrichment first, clutch after since it reads alive state)
        KillTeamEnrichmentEdge killEnrichEdge = new(
            graphRoot, playerContext, killEnemyKill, killTeamKill, killSelfKill,
            wasTradeKill, tradedPlayerSlot, wasFlashKill, flashAttackerSlot,
            killEnemyAssist);
        ClutchEnrichmentEdge clutchEnrichEdge = new(
            graphRoot, playerContext, clutchDetected, clutchPlayerSlot);
        HurtTeamEnrichmentEdge hurtEnrichEdge = new(
            graphRoot, playerContext, hurtEnemyDamage, hurtTeamDamage, hurtSelfDamage,
            victimHealthBefore, cappedDamage, attackerActiveWeapon,
            entityScanner, pawnHealthProvider, activeWeaponProvider);
        BlindEnrichmentEdge blindEnrichEdge = new(
            graphRoot, playerContext, blindWasEnemyFlash, blindDuration);
        // Round end enrichment
        TransientBoolNode roundHasWinner = new("enrich.round.has_winner");
        TransientValueNode<int> roundWinnerTeam = new("enrich.round.winner_team");
        TransientValueNode<int> roundWinnerSide = new("enrich.round.winner_side");

        TransientBoolNode weaponFireIsBullet = new("enrich.weapon_fire.is_bullet");
        WeaponFireEnrichmentEdge weaponFireEnrichEdge = new(graphRoot, weaponFireIsBullet);
        TransientBoolNode hurtIsBullet = new("enrich.hurt.is_bullet");
        HurtBulletEnrichmentEdge hurtBulletEnrichEdge = new(graphRoot, playerContext, hurtIsBullet);

        // Shot enrichment (Tier C aim highlights): per-attacker bullet_damage derivations —
        // flick angle deltas and spray-run tracking — plus the player_death spray-kill
        // correlation. State lives on PlayerContext (round-reset); see ShotEnrichmentEdge.
        TransientValueNode<double> shotTurnDegrees = new("enrich.shot.turn_degrees");
        TransientValueNode<int> shotTicksSinceLast = new(
            "enrich.shot.ticks_since_last_shot", ShotEnrichmentEdge.NoPreviousShotSentinel);
        TransientValueNode<int> shotSprayShots = new("enrich.shot.spray_shots");
        TransientValueNode<int> shotSprayVictims = new("enrich.shot.spray_victims");
        TransientValueNode<int> killSprayKills = new("enrich.kill.spray_kills");
        TransientValueNode<int> killSprayShotsAtKill = new("enrich.kill.spray_shots_at_kill");
        ShotEnrichmentEdge shotEnrichEdge = new(
            graphRoot, playerContext, shotTurnDegrees, shotTicksSinceLast,
            shotSprayShots, shotSprayVictims);
        SprayKillEnrichmentEdge sprayKillEnrichEdge = new(
            graphRoot, playerContext, killSprayKills, killSprayShotsAtKill);

        BombPlantedEdge bombPlantedEdge = new(graphRoot, playerContext);
        BombDefusedEdge bombDefusedEdge = new(graphRoot, playerContext);
        BombExplodedEdge bombExplodedEdge = new(graphRoot, playerContext);
        HealthResetEdge healthResetEdge = new(graphRoot, playerContext);
        PlayerTeamEdge playerTeamEdge = new(graphRoot, playerContext);
        // Connectivity lifecycle (disconnect-ghost defect fix): maintain PlayerContext.Connected so
        // ResetRoundState no longer resurrects a mid-match-disconnected player as alive. Connected
        // gates CountAlive / FindLoneAlive (clutch) and the B6 team aggregates.
        PlayerDisconnectEdge playerDisconnectEdge = new(graphRoot, playerContext);
        PlayerConnectEdge playerConnectEdge = new(graphRoot, playerContext);
        PlayerSpawnConnectivityEdge playerSpawnConnectivityEdge = new(graphRoot, playerContext);

        // ── Round-end enrichment (multi-event $round_end) ─────────────────
        // Profile binds $round_end to multiple concrete events with FirstWins
        // semantics. We instantiate each enrichment edge once per concrete
        // event so they fire correctly across GOTV (round_officially_ended +
        // cs_win_panel_match) and HLTV (cs_pre_restart + cs_win_panel_match).
        // Both edges are idempotent (write to transients); no first-wins
        // guard needed.
        List<StateEdge> roundEndEdges = new();
        LogicalEventBinding? roundEndBinding = resolver.Resolve("round_end");
        if (roundEndBinding is not null)
        {
            foreach (string concreteEvent in roundEndBinding.ConcreteEventNames)
            {
                if (!registry.TryResolve(concreteEvent, out Type? eventType))
                {
                    continue;
                }

                roundEndEdges.Add(new RoundEndEnrichmentEdge(
                    graphRoot, playerContext, roundHasWinner, roundWinnerTeam, roundWinnerSide,
                    eventType));
                roundEndEdges.Add(new ClutchResolutionEnrichmentEdge(
                    graphRoot, playerContext, clutchWon, clutchWinnerSlot,
                    eventType));
            }
        }

        List<StateEdge> allEdges = new()
        {
            killEnrichEdge,
            clutchEnrichEdge,
            hurtEnrichEdge,
            blindEnrichEdge,
            bombPlantedEdge,
            bombDefusedEdge,
            bombExplodedEdge,
            healthResetEdge,
            playerTeamEdge,
            playerDisconnectEdge,
            playerConnectEdge,
            playerSpawnConnectivityEdge,
            weaponFireEnrichEdge,
            hurtBulletEnrichEdge,
            shotEnrichEdge,
            sprayKillEnrichEdge
        };
        allEdges.AddRange(roundEndEdges);

        return new EnrichmentInfrastructure(
            [
                killEnemyKill, killTeamKill, killSelfKill, killEnemyAssist, wasTradeKill, tradedPlayerSlot,
                wasFlashKill, flashAttackerSlot,
                hurtEnemyDamage, hurtTeamDamage, hurtSelfDamage,
                victimHealthBefore, cappedDamage, attackerActiveWeapon,
                blindWasEnemyFlash, blindDuration,
                clutchDetected, clutchPlayerSlot, clutchWon, clutchWinnerSlot,
                roundHasWinner, roundWinnerTeam, roundWinnerSide,
                weaponFireIsBullet, hurtIsBullet,
                shotTurnDegrees, shotTicksSinceLast, shotSprayShots, shotSprayVictims,
                killSprayKills, killSprayShotsAtKill
            ],
            allEdges,
            new Dictionary<string, StateNode>(StringComparer.OrdinalIgnoreCase)
            {
                ["enrich.kill.was_enemy_kill"] = killEnemyKill,
                ["enrich.kill.was_team_kill"] = killTeamKill,
                ["enrich.kill.was_self_kill"] = killSelfKill,
                ["enrich.kill.was_enemy_assist"] = killEnemyAssist,
                ["enrich.kill.was_trade_kill"] = wasTradeKill,
                ["enrich.kill.traded_player_slot"] = tradedPlayerSlot,
                ["enrich.kill.was_flash_kill"] = wasFlashKill,
                ["enrich.kill.flash_attacker_slot"] = flashAttackerSlot,
                ["enrich.hurt.was_enemy_damage"] = hurtEnemyDamage,
                ["enrich.hurt.was_team_damage"] = hurtTeamDamage,
                ["enrich.hurt.was_self_damage"] = hurtSelfDamage,
                ["enrich.hurt.victim_health_before"] = victimHealthBefore,
                ["enrich.hurt.capped_damage"] = cappedDamage,
                ["enrich.hurt.attacker_active_weapon"] = attackerActiveWeapon,
                ["enrich.blind.was_enemy_flash"] = blindWasEnemyFlash,
                ["enrich.blind.duration"] = blindDuration,
                ["enrich.kill.clutch_detected"] = clutchDetected,
                ["enrich.kill.clutch_player_slot"] = clutchPlayerSlot,
                ["enrich.clutch.was_clutch_won"] = clutchWon,
                ["enrich.clutch.winner_slot"] = clutchWinnerSlot,
                ["enrich.round.has_winner"] = roundHasWinner,
                ["enrich.round.winner_team"] = roundWinnerTeam,
                ["enrich.round.winner_side"] = roundWinnerSide,
                ["enrich.weapon_fire.is_bullet"] = weaponFireIsBullet,
                ["enrich.hurt.is_bullet"] = hurtIsBullet,
                ["enrich.shot.turn_degrees"] = shotTurnDegrees,
                ["enrich.shot.ticks_since_last_shot"] = shotTicksSinceLast,
                ["enrich.shot.spray_shots"] = shotSprayShots,
                ["enrich.shot.spray_victims"] = shotSprayVictims,
                ["enrich.kill.spray_kills"] = killSprayKills,
                ["enrich.kill.spray_shots_at_kill"] = killSprayShotsAtKill
            }
        );
    }

    /// <summary>
    ///     Returns the built-in context-rule chains (one game-scoped, one per-player) that wire
    ///     standard concepts like <c>round_phase</c>, <c>gameplay_phase</c>, and
    ///     <c>regulation_status</c> into every analysis graph.
    /// </summary>
    public static List<RuleChainDef> GenerateContextRules()
    {
        // Built-in context rules use $logical triggers so the same rule set
        // works across demo sources (GOTV, HLTV, FACEIT, …). The rule-builder
        // expands $logical into the active profile's concrete events. All
        // bool activate/deactivate triggers below are idempotent, so multi-
        // event $round_end / $match_end chains don't double-trigger.
        RuleChainDef gameContext = new(
            "_builtin_game_context",
            ChainScope.Game,
            [
                new RuleDef("match_live", RuleType.Bool, "MatchLive",
                    Triggers:
                    [
                        new TriggerDef("$match_start"),
                        new TriggerDef("$match_end", TriggerAction.Deactivate)
                    ]),

                new RuleDef("round_active", RuleType.Bool, "RoundActive",
                    Parents: new ParentsDef(ParentMode.All, [new ParentRef("match_live")]),
                    Triggers:
                    [
                        new TriggerDef("$round_freeze_end"),
                        new TriggerDef("$round_end", TriggerAction.Deactivate)
                    ]),

                new RuleDef("round_number", RuleType.Counter, "RoundNumber",
                    "int",
                    Parents: new ParentsDef(ParentMode.All, [new ParentRef("match_live")]),
                    Triggers:
                    [
                        new TriggerDef("$round_freeze_end", TriggerAction.Set,
                            Value: "node.value + 1"),
                        // Match restart (a repeated $match_start — server restarts after a
                        // warmup/knife round): the restarted match's first freeze-end must count
                        // as round 1 again. On the common single-start demo this is a no-op —
                        // $match_start precedes the first freeze-end, so the counter is still 0.
                        new TriggerDef("$match_start", TriggerAction.Set, Value: "0")
                    ]),

                // Halftime gating counter. CS2 has no `halftime` event
                // in the demo stream (0× empirical across both MM and
                // HLTV). The cleanest signal is
                // `round_announce_last_round_half`, which fires 1× per
                // match ~1280 ticks before round 12's freeze_end.
                //
                // Counter starts at -1 (inert). Announce sets it to 0.
                // `round_freeze_end` increments only when value >= 0, so
                // the gate stays at -1 before the announce. Round 12's
                // freeze_end takes the counter to 1 — the conditional
                // Halftime triggers on `gameplay_phase` and `half_state`
                // fire when round 12's cs_pre_restart / round_officially_ended
                // arrives with the counter at 1. Subsequent rounds
                // increment the counter to 2, 3, ... so the gate
                // naturally closes.
                //
                // A bool-based gate was tried first but raced: the bool
                // would deactivate at round 12's freeze_end (start of
                // the last-half round) before round 12's cs_pre_restart
                // (the actual halftime signal) ever fired. The
                // topological sort doesn't know about Condition reads
                // so it can't enforce read-then-deactivate ordering.
                new RuleDef("rounds_after_half_announce", RuleType.Counter, "RoundsAfterHalfAnnounce",
                    "int",
                    -1,
                    Triggers:
                    [
                        new TriggerDef("round_announce_last_round_half", TriggerAction.Set, Value: "0"),
                        new TriggerDef("round_freeze_end", TriggerAction.Set,
                            Value: "node.value + 1",
                            Condition: "rounds_after_half_announce >= 0"),
                        // Match restart: back to the inert -1 gate. ("0 - 1", not "-1": unary
                        // minus is a parse error in the v1 expression language.) No-op on
                        // single-start demos — the announce can't have fired pre-match.
                        new TriggerDef("$match_start", TriggerAction.Set, Value: "0 - 1")
                    ]),

                // Gameplay phase state machine. Replaces the old combat_active
                // bool with a richer enum-as-string ValueNode, queryable from
                // YAML rules via context.round.gameplay_phase. Concrete events
                // (not $logical) used because the transitions are foundational
                // and we want explicit cross-profile behaviour.
                //
                // Trigger choices reflect empirical event-firing on the bench
                // demos (see docs/Demo-Event-Compatibility.md):
                //   - FreezeTime ← round_prestart       (MM only; HLTV gap)
                //   - PostRound  ← cs_round_final_beep  (MM 18 / HLTV 24)
                //   - PreRound   ← round_officially_ended | cs_pre_restart
                //                  (one fires per profile)
                //   - Halftime   ← round-end events ABOVE, gated on
                //                  `rounds_after_half_announce == 1`. Triggers
                //                  appear AFTER the unconditional PreRound
                //                  triggers so they overwrite when the bool
                //                  is true. Falls back to canonical
                //                  `halftime` event (fires 0× empirically
                //                  but kept for completeness).
                new RuleDef("gameplay_phase", RuleType.Value, "GameplayPhase",
                    "string",
                    "WarmUp",
                    Triggers:
                    [
                        new TriggerDef("warmup_end", TriggerAction.Set, Value: "\"PreMatch\""),
                        new TriggerDef("round_announce_match_start", TriggerAction.Set, Value: "\"PreMatch\""),
                        new TriggerDef("round_prestart", TriggerAction.Set, Value: "\"FreezeTime\""),
                        // Entity-state backup for HLTV demos where round_prestart fires 0×.
                        // Reads the rising edge of m_bFreezePeriod on CCSGameRules; on MM the
                        // event trigger above wins by ordering and this is a no-op.
                        new TriggerDef("entity.game.freeze_period", TriggerAction.Set, Value: "\"FreezeTime\"",
                            Condition: "entity.game.freeze_period == true"),
                        new TriggerDef("round_freeze_end", TriggerAction.Set, Value: "\"ActiveWithBuy\""),
                        new TriggerDef("buytime_ended", TriggerAction.Set, Value: "\"ActivePostBuy\""),
                        new TriggerDef("cs_round_final_beep", TriggerAction.Set, Value: "\"PostRound\""),
                        new TriggerDef("round_officially_ended", TriggerAction.Set, Value: "\"PreRound\""),
                        new TriggerDef("cs_pre_restart", TriggerAction.Set, Value: "\"PreRound\""),
                        // Halftime detection: conditional overrides of the
                        // PreRound transitions above. Order matters — these
                        // fire after, so when the gate is true they win.
                        new TriggerDef("round_officially_ended", TriggerAction.Set, Value: "\"Halftime\"",
                            Condition: "rounds_after_half_announce == 1"),
                        new TriggerDef("cs_pre_restart", TriggerAction.Set, Value: "\"Halftime\"",
                            Condition: "rounds_after_half_announce == 1"),
                        new TriggerDef("halftime", TriggerAction.Set, Value: "\"Halftime\""),
                        new TriggerDef("cs_intermission", TriggerAction.Set, Value: "\"Intermission\""),
                        new TriggerDef("cs_win_panel_match", TriggerAction.Set, Value: "\"PostMatch\"")
                    ]),

                // Bomb status state machine, round-scoped. Resets to NotInPlay
                // each round. Abort transitions return to the prior state
                // (abortplant → Carried, abortdefuse → Planted) since the bomb
                // entity persists through aborts.
                new RuleDef("bomb_status", RuleType.Value, "BombStatus",
                    "string",
                    "NotInPlay",
                    true,
                    Triggers:
                    [
                        new TriggerDef("bomb_pickup", TriggerAction.Set, Value: "\"Carried\""),
                        new TriggerDef("bomb_dropped", TriggerAction.Set, Value: "\"Dropped\""),
                        new TriggerDef("bomb_beginplant", TriggerAction.Set, Value: "\"Planting\""),
                        new TriggerDef("bomb_planted", TriggerAction.Set, Value: "\"Planted\""),
                        new TriggerDef("bomb_abortplant", TriggerAction.Set, Value: "\"Carried\""),
                        new TriggerDef("bomb_begindefuse", TriggerAction.Set, Value: "\"Defusing\""),
                        new TriggerDef("bomb_defused", TriggerAction.Set, Value: "\"Defused\""),
                        new TriggerDef("bomb_abortdefuse", TriggerAction.Set, Value: "\"Planted\""),
                        new TriggerDef("bomb_exploded", TriggerAction.Set, Value: "\"Detonated\"")
                    ]),

                // Sticky per-round bomb-planted gate (Rulesets v2 §6 obligation 9). A bool that
                // latches true on `bomb_planted` and clears at the next round's freeze-end —
                // deliberately NOT `bomb_status == "Planted"`, which flips to Defused/Detonated.
                // This is what a v2 `while: round.bomb.was_planted` gate reads, so kills between a
                // later defuse/detonation and round end still count as post-plant (the reason v1's
                // post-plant-double rule hand-rolled `pp_bomb_planted` as a dedicated per-player
                // bool). The reset is an EXPLICIT $round_freeze_end deactivate, NOT `reset: round`:
                // game-scoped round-scoped NODES are not registered for the evaluator's per-round
                // reset (only per-player nodes and reset-edges are), so a ResetOnRound bool here
                // would latch true forever. $round_freeze_end is the exact boundary the per-player
                // reset fires on, so the gate's live window matches v1's per-player pp_bomb_planted
                // tick-for-tick. v2Name `round.bomb.was_planted` (CatalogBuilder ContextV2Names);
                // the 2.2b adapter injected the path as a type-level stand-in so rulesets RESOLVED,
                // this real context is what makes them EVALUATE.
                new RuleDef("bomb_was_planted", RuleType.Bool, "BombWasPlanted",
                    Triggers:
                    [
                        new TriggerDef("$round_freeze_end", TriggerAction.Deactivate),
                        new TriggerDef("bomb_planted")
                    ]),

                // Regulation vs Overtime. cs_intermission fires at multiple
                // boundaries in CS2 (regulation halftime, OT entry, OT half
                // boundaries) and the event itself carries no fields to
                // distinguish them. As a pragmatic default we transition to
                // "Overtime" on the first cs_intermission AFTER halftime — but
                // expression-language can't reference halftime state directly,
                // so today we use cs_match_end_restart, which fires at the
                // regulation→OT boundary specifically and is a more reliable
                // OT-entry signal. Untested on a real OT demo; verify when
                // one is available.
                new RuleDef("regulation_status", RuleType.Value, "RegulationStatus",
                    "string",
                    "Regulation",
                    Triggers:
                    [
                        new TriggerDef("cs_match_end_restart", TriggerAction.Set,
                            Value: "\"Overtime\"")
                    ]),

                // Half state. Flips to SecondHalf at regulation halftime.
                // Combine with regulation_status to discriminate OT halves
                // when OT detection is wired. Same pattern as
                // gameplay_phase Halftime detection — `last_round_of_half`
                // gates conditional Set on round-end events.
                new RuleDef("half_state", RuleType.Value, "HalfState",
                    "string",
                    "FirstHalf",
                    Triggers:
                    [
                        new TriggerDef("halftime", TriggerAction.Set, Value: "\"SecondHalf\""),
                        new TriggerDef("round_officially_ended", TriggerAction.Set, Value: "\"SecondHalf\"",
                            Condition: "rounds_after_half_announce == 1"),
                        new TriggerDef("cs_pre_restart", TriggerAction.Set, Value: "\"SecondHalf\"",
                            Condition: "rounds_after_half_announce == 1"),
                        // Match restart: the restarted match begins in its first half. No-op on
                        // single-start demos (the value is still the "FirstHalf" default).
                        new TriggerDef("$match_start", TriggerAction.Set, Value: "\"FirstHalf\"")
                    ]),

                new RuleDef("no_deaths_yet", RuleType.Bool, "NoDeathsYet",
                    Triggers:
                    [
                        new TriggerDef("$round_freeze_end"),
                        new TriggerDef("$player_death", TriggerAction.Deactivate)
                    ]),

                new RuleDef("map_name", RuleType.Value, "MapName",
                    "string",
                    Triggers:
                    [
                        new TriggerDef("CDemoFileHeader", TriggerAction.Set,
                            Value: "event.MapName")
                    ])
            ]);

        RuleChainDef perPlayerContext = new(
            "_builtin_player_context",
            ChainScope.PerPlayer,
            [
                new RuleDef("alive", RuleType.Bool, "Alive",
                    Triggers:
                    [
                        new TriggerDef("$round_freeze_end"),
                        new TriggerDef("$player_death", TriggerAction.Deactivate,
                            "event.UserId == player.slot && event.Attacker != event.UserId")
                    ]),

                new RuleDef("survived", RuleType.Bool, "Survived",
                    ResetOnRound: true, Default: false,
                    Parents: new ParentsDef(ParentMode.All, [new ParentRef("alive")]),
                    Triggers:
                    [
                        new TriggerDef("$round_end")
                    ]),

                new RuleDef("traded", RuleType.Bool, "Traded",
                    ResetOnRound: true, Default: false,
                    Parents: new ParentsDef(ParentMode.All, [new ParentRef("enrich.kill.was_trade_kill")]),
                    Triggers:
                    [
                        new TriggerDef("$player_death", TriggerAction.Activate,
                            "enrich.kill.traded_player_slot == player.slot")
                    ])
            ]);

        return [gameContext, perPlayerContext];
    }

    /// <summary>Bundle returned by <c>CreateEnrichmentInfrastructure</c>: nodes, edges, and a name → node lookup.</summary>
    /// <param name="Nodes">Transient enrichment nodes to register on the graph.</param>
    /// <param name="Edges">Enrichment edges (one per source event) wiring the transient nodes.</param>
    /// <param name="NodeLookup">Name lookup used by <c>ExpressionCompiler</c> to resolve <c>enrich.xxx</c> identifiers.</param>
    public sealed record EnrichmentInfrastructure(
        IReadOnlyList<StateNode> Nodes,
        IReadOnlyList<StateEdge> Edges,
        Dictionary<string, StateNode> NodeLookup);
}
