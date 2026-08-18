#region

using CS2DemoKit.Parser.GameEvents;

using CS2OpenSchema.Events;

#endregion

namespace CS2DemoKit.TestSupport;

/// <summary>
///     Builds <see cref="GameEvent" /> fixtures over the SDK's payload records.
/// </summary>
/// <remarks>
///     <para>
///         Fixtures used to construct the parser's own generated records positionally —
///         <c>new PlayerDeathEvent("player_death", -1, 0, 0, 0, -1, -1, -1, false, "ak47", …)</c>,
///         twenty-seven arguments deep. Two things changed that. The payload records come from the
///         SDK now and every property is <c>required init</c>, so an object initialiser has to name
///         all of them; and the per-fire transport context moved out to the envelope.
///     </para>
///     <para>
///         Writing that out at each call site would bury the one or two fields a test actually
///         asserts on under twenty defaults. These factories carry the defaults and take only what
///         the test cares about, so a fixture reads as the thing it is testing.
///     </para>
/// </remarks>
public static class TestGameEvents
{
    /// <summary>A <c>player_death</c> fire. Slots default to -1, meaning "nobody".</summary>
    public static GameEvent PlayerDeath(
        int userId = -1,
        int attacker = -1,
        int assister = -1,
        string weapon = "",
        short dmgHealth = 0,
        byte dmgArmor = 0,
        bool headshot = false,
        short penetrated = 0,
        bool thruSmoke = false,
        bool attackerBlind = false,
        bool assistedFlash = false,
        float distance = 0f,
        byte hitGroup = 0,
        int frameNumber = 0,
        int serverTick = 0,
        int gameTick = 0,
        int eventId = -1) =>
        new("player_death", eventId, frameNumber, serverTick, gameTick,
            PlayerDeathPayload(userId, attacker, assister, weapon, dmgHealth, dmgArmor, headshot,
                penetrated, thruSmoke, attackerBlind, assistedFlash, distance, hitGroup));

    /// <summary>
    ///     The <c>player_death</c> payload on its own, without a fire around it — for compiles where
    ///     the payload IS the parameter: the net-message-shaped path, and tests pinning the
    ///     payload-typed compile. Game-event breakpoint predicates now bind the fire (the envelope
    ///     <see cref="PlayerDeath" /> returns), the same as compiled ruleset delegates.
    /// </summary>
    public static PlayerDeathEvent PlayerDeathPayload(
        int userId = -1,
        int attacker = -1,
        int assister = -1,
        string weapon = "",
        short dmgHealth = 0,
        byte dmgArmor = 0,
        bool headshot = false,
        short penetrated = 0,
        bool thruSmoke = false,
        bool attackerBlind = false,
        bool assistedFlash = false,
        float distance = 0f,
        byte hitGroup = 0) =>
            new()
            {
                UserId = userId,
                Attacker = attacker,
                Assister = assister,
                // 4.1 pawn-handle companions. The analysis layer keys everything on controller
                // slots, so the fixtures leave the handles at 0 (the KV1 absent-key default).
                UserIdPawn = 0,
                AttackerPawn = 0,
                AssisterPawn = 0,
                Weapon = weapon,
                DmgHealth = dmgHealth,
                Headshot = headshot,
                Penetrated = penetrated,
                ThruSmoke = thruSmoke,
                AttackerBlind = attackerBlind,
                AssistedFlash = assistedFlash,
                Distance = distance,
                HitGroup = hitGroup,
                AttackerInAir = false,
                DmgArmor = dmgArmor,
                Dominated = 0,
                NoScope = false,
                NoReplay = false,
                Revenge = 0,
                Wipe = 0,
                WeaponFauxItemId = "",
                WeaponItemId = "",
                WeaponOriginalOwnerXuid = ""
            };

    /// <summary>A <c>player_team</c> fire.</summary>
    public static GameEvent PlayerTeam(
        int userId,
        byte team,
        byte oldTeam = 0,
        bool disconnect = false,
        bool silent = false,
        bool isBot = false,
        int frameNumber = 0,
        int serverTick = 0,
        int gameTick = 0,
        int eventId = -1) =>
        new("player_team", eventId, frameNumber, serverTick, gameTick,
            new PlayerTeamEvent
            {
                UserId = userId,
                UserIdPawn = 0,
                Team = team,
                OldTeam = oldTeam,
                Disconnect = disconnect,
                Silent = silent,
                IsBot = isBot
            });

    /// <summary>A <c>round_freeze_end</c> fire. The payload declares no fields.</summary>
    public static GameEvent RoundFreezeEnd(
        int frameNumber = 0, int serverTick = 0, int gameTick = 0, int eventId = -1) =>
        new("round_freeze_end", eventId, frameNumber, serverTick, gameTick, new RoundFreezeEndEvent());

    /// <summary>A <c>round_officially_ended</c> fire. The payload declares no fields.</summary>
    public static GameEvent RoundOfficiallyEnded(
        int frameNumber = 0, int serverTick = 0, int gameTick = 0, int eventId = -1) =>
        new("round_officially_ended", eventId, frameNumber, serverTick, gameTick,
            new RoundOfficiallyEndedEvent());

    /// <summary>A <c>round_mvp</c> fire.</summary>
    public static GameEvent RoundMvp(
        int userId,
        short reason = 0,
        int value = 0,
        int frameNumber = 0,
        int serverTick = 0,
        int gameTick = 0,
        int eventId = -1) =>
        new("round_mvp", eventId, frameNumber, serverTick, gameTick,
            new RoundMvpEvent
            {
                UserId = userId,
                Reason = reason,
                Value = value,
                MusicKitId = 0,
                MusicKitMvps = 0,
                NoMusic = 0
            });
}
