#region

using CS2DemoKit.Analysis.Edges;
using CS2DemoKit.Analysis.Visibility;

#endregion

namespace CS2DemoKit.Analysis.Building;

/// <summary>
///     Maps player slots to their materialized context nodes. Populated during player
///     materialization, queried by enrichment edges to copy cross-player context
///     (e.g., victim's health at time of damage).
/// </summary>
public sealed class PlayerContextIndex
{
    private readonly Dictionary<int, PlayerContext> _slots = [];

    /// <summary>All currently-registered <see cref="PlayerContext" /> instances.</summary>
    public IEnumerable<PlayerContext> AllPlayers => _slots.Values;

    /// <summary>True when a defusal completed in the current round; cleared by <see cref="ResetRoundState" />.</summary>
    public bool BombDefused { get; set; }

    /// <summary>True when the bomb exploded in the current round; cleared by <see cref="ResetRoundState" />.</summary>
    public bool BombExploded { get; set; }

    /// <summary>Bomb state for the current round — set by enrichment edges.</summary>
    public bool BombPlanted { get; set; }

    /// <summary>Current round number (1-based), set by HealthResetEdge on round_freeze_end.</summary>
    public int RoundNumber { get; set; }

    /// <summary>
    ///     The one per-round anchor the aim metrics read that does NOT live on a
    ///     <see cref="PlayerContext" />: <see cref="VisibilityTransitionScanner" />'s pair state and
    ///     crosshair-arrival stamps. Everything else these metrics latch — <c>LastSpotTick</c>,
    ///     <c>SpotCount</c>, the on-target answer latches — is cleared by
    ///     <see cref="ResetRoundState" />; the scanner sits outside the index, so without somewhere
    ///     to attach it, it is the only state in the feature that spans rounds. A pair still visible
    ///     when the round ends suppresses its own first contact of the next one, and
    ///     <see cref="Edges.SpottedEnrichmentEdge" /> then numbers whichever contact came second as
    ///     the round's first.
    ///     <para>
    ///         Optional: null leaves the scanner spanning rounds, which is what it did before there
    ///         was anywhere to attach it, and every other consumer of the index is unaffected.
    ///     </para>
    /// </summary>
    public VisibilityTransitionScanner? VisibilityTransitions { get; set; }

    /// <summary>Clears any recorded flash-blind state for the given player slot.</summary>
    public void ClearBlind(int slot)
    {
        if (_slots.TryGetValue(slot, out PlayerContext? ctx))
        {
            ctx.BlindedBySlot = -1;
            ctx.BlindedAtTick = -1;
        }
    }

    /// <summary>
    ///     Returns the number of registered players on <paramref name="team" /> that are currently
    ///     alive AND connected. The <see cref="PlayerContext.Connected" /> gate is the disconnect
    ///     defect fix: a disconnected player is resurrected as alive by <see cref="ResetRoundState" />
    ///     each round (it deliberately leaves Connected untouched), so without this gate a ghost
    ///     would keep inflating alive counts — and clutch detection — in every subsequent round.
    /// </summary>
    public int CountAlive(int team)
    {
        int count = 0;
        foreach (PlayerContext ctx in _slots.Values)
        {
            if (ctx is { IsAlive: true, Connected: true } && ctx.Team == team)
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>
    ///     Returns the number of registered players on <paramref name="team" /> that are currently
    ///     connected (regardless of alive state). Connected-gated for the same disconnect-ghost
    ///     reason as <see cref="CountAlive" />; also backs the B6 <c>round.team.players</c> /
    ///     <c>round.enemies.players</c> aggregates.
    /// </summary>
    public int CountConnected(int team)
    {
        int count = 0;
        foreach (PlayerContext ctx in _slots.Values)
        {
            if (ctx.Connected && ctx.Team == team)
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>
    ///     Returns the slot of the sole surviving player on <paramref name="team" />, or -1 if zero or more than
    ///     one player on that team is alive. Used by clutch detection. Disconnected players are excluded
    ///     (the same disconnect-ghost defect fix as <see cref="CountAlive" />).
    /// </summary>
    public int FindLoneAlive(int team)
    {
        int found = -1;
        foreach (PlayerContext ctx in _slots.Values)
        {
            if (!ctx.IsAlive || !ctx.Connected || ctx.Team != team)
            {
                continue;
            }

            if (found >= 0)
            {
                return -1;
            }

            found = ctx.Slot;
        }

        return found;
    }

    /// <summary>
    ///     Checks if the given victim had recently killed any tracked player on the
    ///     avenger's current team within the trade window. Returns the slot of the
    ///     traded player, or -1. Trade-kill semantics: only direct kills count
    ///     (not assists), and the avenger must be on the same side as the dead
    ///     teammate.
    /// </summary>
    public int FindTradedPlayer(int victimSlot, int killerSlot, int currentTick, int windowTicks = 256)
    {
        if (!_slots.TryGetValue(killerSlot, out PlayerContext? killerCtx))
        {
            return -1;
        }

        int killerTeam = killerCtx.Team;
        if (killerTeam <= 1)
        {
            return -1;
        }

        foreach (PlayerContext ctx in _slots.Values)
        {
            if (ctx.LastDeathTick < 0)
            {
                continue;
            }

            if (currentTick - ctx.LastDeathTick > windowTicks)
            {
                continue;
            }

            if (ctx.LastKillerSlot != victimSlot)
            {
                continue;
            }

            if (ctx.Team != killerTeam)
            {
                continue;
            }

            return ctx.Slot;
        }

        return -1;
    }

    /// <summary>
    ///     Returns the player's current team_num (2=T, 3=CT) at the moment of the
    ///     call. Updated in real-time by <see cref="Edges.PlayerTeamEdge" />.
    ///     Returns 0 if the player is not yet materialized.
    /// </summary>
    public int GetCurrentTeam(int slot) =>
        _slots.TryGetValue(slot, out PlayerContext? ctx) ? ctx.Team : 0;

    /// <summary>
    ///     Gets the health value for a player slot. Returns 100 if the slot is unknown.
    /// </summary>
    public int GetHealth(int slot) =>
        _slots.TryGetValue(slot, out PlayerContext? ctx) ? ctx.Health : 100;

    /// <summary>
    ///     Marks the given player slot connected (set on connect / spawn). No-op if the slot is not
    ///     registered. Re-connecting a slot that previously disconnected re-enables its inclusion in
    ///     the Connected-gated aggregates (<see cref="CountAlive" /> / <see cref="FindLoneAlive" />).
    /// </summary>
    public void MarkConnected(int slot)
    {
        if (_slots.TryGetValue(slot, out PlayerContext? ctx))
        {
            ctx.Connected = true;
        }
    }

    /// <summary>Marks the given player slot dead. No-op if the slot is not registered.</summary>
    public void MarkDead(int slot)
    {
        if (_slots.TryGetValue(slot, out PlayerContext? ctx))
        {
            ctx.IsAlive = false;
        }
    }

    /// <summary>
    ///     Marks the given player slot disconnected (cleared on <c>player_disconnect</c>). No-op if the
    ///     slot is not registered. A disconnected slot is deliberately NOT resurrected by
    ///     <see cref="ResetRoundState" />, so it stops counting as alive in every subsequent round.
    /// </summary>
    public void MarkDisconnected(int slot)
    {
        if (_slots.TryGetValue(slot, out PlayerContext? ctx))
        {
            ctx.Connected = false;
        }
    }

    /// <summary>
    ///     Records that <paramref name="victimSlot" /> was flash-blinded by <paramref name="flasherSlot" /> at
    ///     <paramref name="tick" />.
    /// </summary>
    public void RecordBlind(int victimSlot, int flasherSlot, int tick)
    {
        if (_slots.TryGetValue(victimSlot, out PlayerContext? ctx))
        {
            ctx.BlindedBySlot = flasherSlot;
            ctx.BlindedAtTick = tick;
        }
    }

    /// <summary>
    ///     Records that a player died at a given tick, killed by a given attacker.
    /// </summary>
    public void RecordDeath(int victimSlot, int killerSlot, int assisterSlot, int tick)
    {
        if (_slots.TryGetValue(victimSlot, out PlayerContext? ctx))
        {
            ctx.LastDeathTick = tick;
            ctx.LastKillerSlot = killerSlot;
            ctx.LastAssisterSlot = assisterSlot;
        }
    }

    /// <summary>Registers a per-slot <see cref="PlayerContext" />, replacing any existing entry for that slot.</summary>
    public void Register(int slot, PlayerContext context) => _slots[slot] = context;

    /// <summary>
    ///     Resets all per-round state (called at round start). Deliberately does NOT touch
    ///     <see cref="PlayerContext.Connected" />: connectivity is a match-lifetime property, not a
    ///     per-round one, so a player who disconnected in an earlier round is NOT resurrected here.
    ///     Resetting it (as the pre-fix code effectively did by only tracking <c>IsAlive</c>) is the
    ///     disconnect-ghost defect this exclusion closes.
    /// </summary>
    /// <summary>
    ///     Resets for a MATCH restart (a repeated <c>begin_new_match</c> — e.g. the server
    ///     restarting after a warmup/knife round): round state clears exactly as at a round
    ///     boundary, and <see cref="RoundNumber" /> returns to 0 so the restarted match's first
    ///     freeze-end counts as round 1 again (this also re-arms the pre-first-round guard in
    ///     <c>RoundEndEnrichmentEdge</c>). Team and connectivity state deliberately survive —
    ///     both are physical properties that span the restart.
    /// </summary>
    public void ResetForMatchRestart()
    {
        RoundNumber = 0;
        ResetRoundState();
    }

    public void ResetRoundState()
    {
        BombPlanted = false;
        BombExploded = false;
        BombDefused = false;
        VisibilityTransitions?.Reset();

        foreach (PlayerContext ctx in _slots.Values)
        {
            ctx.Health = 100;
            ctx.IsAlive = true;
            ctx.IsInClutch = false;
            ctx.ClutchOpponents = 0;
            ctx.LastDeathTick = -1;
            ctx.LastKillerSlot = -1;
            ctx.LastAssisterSlot = -1;
            ctx.BlindedBySlot = -1;
            ctx.BlindedAtTick = -1;
            ctx.LastShotGameTick = -1;
            ctx.LastShotPitch = 0f;
            ctx.LastShotYaw = 0f;
            ctx.SprayLastRecoil = 0f;
            ctx.SprayShotCount = 0;
            ctx.SprayVictimsMask = 0;
            ctx.SprayKillCount = 0;
            ctx.LastSpotTick = -1;
            ctx.LastSpotPitch = 0f;
            ctx.LastSpotYaw = 0f;
            ctx.LastSpotChestAngle = 0f;
            ctx.SpotAnsweredByShot = false;
            ctx.SpotAnsweredByLanded = false;
            ctx.AnsweredOnTargetSinceShot = -1;
            ctx.AnsweredOnTargetSinceLanded = -1;
            ctx.SpotCount = 0;
            ctx.AimShot.Reset();
        }
    }

    /// <summary>
    ///     Sets the health value for a player slot after a damage/spawn event.
    /// </summary>
    public void SetHealth(int slot, int health)
    {
        if (_slots.TryGetValue(slot, out PlayerContext? ctx))
        {
            ctx.Health = health;
        }
    }

    /// <summary>Attempts to look up the <see cref="PlayerContext" /> for the given slot. Returns <c>true</c> on hit.</summary>
    public bool TryGet(int slot, out PlayerContext? context) => _slots.TryGetValue(slot, out context);

    /// <summary>Per-player mutable state tracked across the demo: health, alive status, last kill/death info, blind state.</summary>
    /// <param name="slot">CS2 player slot (0..N-1).</param>
    /// <param name="team">Initial team_num at registration (2 = T, 3 = CT).</param>
    public sealed class PlayerContext(int slot, int team)
    {
        /// <summary>
        ///     The shot-anchored aim state: this player's current spray run and the last
        ///     <see cref="AimShotContext" /> resolved for them. One object rather than seven more
        ///     properties here because it has one lifetime and one reset, and a field added beside
        ///     the others that someone forgets to clear in <see cref="ResetRoundState" /> would carry
        ///     a spray run across a round boundary. Always present (an inert run costs one small
        ///     object per slot); populated only when a rule reads a shot-anchored enrichment.
        /// </summary>
        public AimShotState AimShot { get; } = new();

        /// <summary>Tick this player was last flash-blinded, or -1 if never (or already cleared).</summary>
        public int BlindedAtTick { get; set; } = -1;

        /// <summary>Slot of the player who last flashed this player, or -1 if never.</summary>
        public int BlindedBySlot { get; set; } = -1;

        /// <summary>
        ///     Whether this player is currently connected to the server. Defaults to <c>true</c> at
        ///     registration (materialization implies presence); set on connect/spawn, cleared on
        ///     <c>player_disconnect</c>. Deliberately excluded from <see cref="ResetRoundState" /> so a
        ///     mid-match disconnect stays reflected across later rounds. Gates the Connected-aware
        ///     aggregates (<see cref="CountAlive" />, <see cref="FindLoneAlive" />, and the B6 team
        ///     namespaces).
        /// </summary>
        public bool Connected { get; set; } = true;

        /// <summary>Current health (0..100). Reset to 100 on round start.</summary>
        public int Health { get; set; } = 100;

        /// <summary>Whether the player is currently alive in the active round.</summary>
        public bool IsAlive { get; set; } = true;

        /// <summary>True while this player is the lone survivor on their team in a 1vN situation.</summary>
        public bool IsInClutch { get; set; }

        /// <summary>
        ///     The number of enemies alive at the moment this player ENTERED the clutch (the N of the
        ///     1vN). Set once by the clutch enrichment edge when <see cref="IsInClutch" /> flips true and
        ///     held for the round (so a won clutch still reports N at round end); 0 when not in a clutch.
        /// </summary>
        public int ClutchOpponents { get; set; }

        /// <summary>Slot of the assister credited on the last death of this player, or -1.</summary>
        public int LastAssisterSlot { get; set; } = -1;

        /// <summary>Tick this player was last killed, or -1 if alive / not yet killed this round.</summary>
        public int LastDeathTick { get; set; } = -1;

        // Shotgun multi-pellet de-dup: a shotgun blast against one victim emits
        // many player_hurt events (one per pellet). Stat trackers count this as
        // one hit. Track the most recent (tick, victim) pair per attacker and
        // skip duplicates in HurtBulletEnrichmentEdge.

        /// <summary>Tick of this player's most recent damage event (shotgun de-dup key).</summary>
        public int LastHurtTick { get; set; } = -1;

        /// <summary>Victim slot of this player's most recent damage event (shotgun de-dup key).</summary>
        public int LastHurtVictim { get; set; } = -1;

        /// <summary>Slot of the killer credited on the last death of this player, or -1.</summary>
        public int LastKillerSlot { get; set; } = -1;

        // Tier C aim-highlight shot state (bullet_damage-driven, written by
        // Edges.ShotEnrichmentEdge / read by Edges.SprayKillEnrichmentEdge). All of it is
        // per-round: cleared by ResetRoundState so no spray run or "previous shot" anchor
        // ever spans a round boundary.

        /// <summary>Frame-clock GameTick of this player's last <c>bullet_damage</c> (damaging shot), or -1.</summary>
        public int LastShotGameTick { get; set; } = -1;

        /// <summary>Pitch (ShootAngX) of this player's last damaging shot; valid only when <see cref="LastShotGameTick" /> &gt;= 0.</summary>
        public float LastShotPitch { get; set; }

        /// <summary>Yaw (ShootAngY) of this player's last damaging shot; valid only when <see cref="LastShotGameTick" /> &gt;= 0.</summary>
        public float LastShotYaw { get; set; }

        /// <summary>
        ///     Kills credited to this player during the CURRENT spray run (see
        ///     <see cref="SprayShotCount" />). Reset when a new run starts and each round.
        /// </summary>
        public int SprayKillCount { get; set; }

        /// <summary>RecoilIndex of the last damaging shot — the monotonicity anchor for spray-run continuation.</summary>
        public float SprayLastRecoil { get; set; }

        /// <summary>
        ///     Damaging shots in the current uninterrupted spray run (consecutive
        ///     <c>bullet_damage</c> events with a small tick gap and non-dropping RecoilIndex).
        ///     0 = no run yet this round.
        /// </summary>
        public int SprayShotCount { get; set; }

        /// <summary>Bitmask of distinct victim slots (0..63) damaged during the current spray run.</summary>
        public ulong SprayVictimsMask { get; set; }

        // Visibility rising-edge state (written by Edges.SpottedEnrichmentEdge, driven by the
        // synthesized enemy_spotted event). Per-round like the shot state above: a round boundary
        // is a hard reset of everyone's contact history, so cleared by ResetRoundState.

        /// <summary>
        ///     Tick of the last enemy this player spotted, or -1 when they have spotted nobody this
        ///     round. The clock is the spot event's FRAME clock, so it is directly comparable with a
        ///     parsed event's <c>GameTick</c>; that is the whole point of latching it here.
        /// </summary>
        public int LastSpotTick { get; set; } = -1;

        /// <summary>
        ///     Where this player's crosshair was pointing at <see cref="LastSpotTick" />, pitch in
        ///     degrees. Valid only when <see cref="LastSpotTick" /> is not -1.
        /// </summary>
        public float LastSpotPitch { get; set; }

        /// <summary>
        ///     The yaw half of <see cref="LastSpotPitch" />.
        ///     <para>
        ///         These two exist so a shot can measure its crosshair travel from the contact that
        ///         PRECEDED IT rather than from the round's first contact. A YAML capture cannot make
        ///         that pairing: <c>keep: first</c> pins the round's opening contact and
        ///         <c>keep: last</c> settles to its final one, so on a round holding more than one
        ///         engagement neither is the spot the shot actually answered, and the measured travel
        ///         is the angle between unrelated instants.
        ///     </para>
        /// </summary>
        public float LastSpotYaw { get; set; }

        /// <summary>
        ///     The crosshair error at <see cref="LastSpotTick" />: degrees between the viewer's eye
        ///     ray and the target's chest anchor at the instant of contact. Latched alongside the
        ///     angles so a shot can report how much of its travel was CORRECTION rather than
        ///     placement, which is the difference between a flick and a held angle.
        /// </summary>
        public float LastSpotChestAngle { get; set; }

        /// <summary>
        ///     Whether a shot has already been FIRED in answer to <see cref="LastSpotTick" />.
        ///     <para>
        ///         The timing metrics measure the interval from a contact to the FIRST shot that
        ///         answered it. Averaging every shot after a contact instead measures something
        ///         else entirely: a spray of thirty bullets contributes thirty intervals, twenty-nine
        ///         of which are just the spray's own duration, and the number drifts toward how long
        ///         players hold the trigger rather than how fast they react.
        ///     </para>
        /// </summary>
        public bool SpotAnsweredByShot { get; set; }

        /// <summary>
        ///     The same for the first shot that LANDED. Tracked separately from
        ///     <see cref="SpotAnsweredByShot" /> because time-to-shoot and time-to-damage are
        ///     different metrics over different events, and a contact answered by a miss has been
        ///     answered for one and not the other.
        /// </summary>
        public bool SpotAnsweredByLanded { get; set; }

        /// <summary>
        ///     The acquisition tick (see <c>VisibilityTransitionScanner.OnTargetSince</c>) that a
        ///     FIRED shot has already answered, or -1.
        ///     <para>
        ///         Stored as the acquisition's own tick rather than a bool, because the scanner
        ///         re-arms whenever the crosshair leaves every enemy: comparing the two values tells a
        ///         later shot whether it belongs to the SAME acquisition or a new one, which a flag
        ///         cannot.
        ///     </para>
        /// </summary>
        public int AnsweredOnTargetSinceShot { get; set; } = -1;

        /// <summary>
        ///     The same for the first shot that LANDED. Tracked separately from
        ///     <see cref="AnsweredOnTargetSinceShot" /> for the reason
        ///     <see cref="SpotAnsweredByLanded" /> is tracked separately from
        ///     <see cref="SpotAnsweredByShot" />: <c>weapon_fire</c> always precedes the
        ///     <c>bullet_damage</c> it produced, so one shared latch is consumed by the fired arm
        ///     before the landed arm sees the shot at all, and the landed arm's copy of
        ///     <c>first_after_on_target</c> is then false on every shot in the demo.
        /// </summary>
        public int AnsweredOnTargetSinceLanded { get; set; } = -1;

        /// <summary>
        ///     How many enemies this player has spotted so far this round, counting re-acquisitions
        ///     of the same enemy separately. 0 before their first contact.
        /// </summary>
        public int SpotCount { get; set; }

        /// <summary>The CS2 player slot this context represents.</summary>
        public int Slot { get; } = slot;

        /// <summary>Current team_num (2 = T, 3 = CT). Mutates at halftime via <c>player_team</c> events.</summary>
        public int Team { get; set; } = team;
    }
}
