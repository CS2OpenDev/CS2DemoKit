#region

using System.Reflection;
using CS2DemoKit.Analysis.Events;
using CS2DemoKit.Parser.GameEvents;

#endregion

namespace CS2DemoKit.Analysis.Registry;

/// <summary>One entry in the game-event registry: the event's wire name, its CLR type, and accessors for its fields.</summary>
/// <param name="Name">CS2 wire name of the event (e.g. <c>player_death</c>).</param>
/// <param name="EventType">CLR <see cref="GameEvent" /> subtype the parser decodes the event into.</param>
/// <param name="Fields">Pre-compiled accessors for every property on the event type, keyed by field name.</param>
public sealed record EventRegistration(
    string Name,
    Type EventType,
    IReadOnlyDictionary<string, EventFieldAccessor> Fields);

/// <summary>One entry in the net-message registry: payload class name, CLR payload type, and accessors for its fields.</summary>
/// <param name="Name">Protobuf class name of the payload (e.g. <c>CDemoFileHeader</c>).</param>
/// <param name="PayloadType">CLR type of the protobuf-generated payload class.</param>
/// <param name="Fields">Pre-compiled accessors for every property on the payload type.</param>
public sealed record NetMessageRegistration(
    string Name,
    Type PayloadType,
    IReadOnlyDictionary<string, EventFieldAccessor> Fields);

/// <summary>
///     Single source of truth mapping wire-level event / net-message names to their CLR types and
///     field accessors. Constructed once via <see cref="Build" /> at rule-build time; lookups are
///     O(1) dictionary hits.
/// </summary>
public sealed class EventRegistry
{
    private static readonly (string Name, Type Type)[] _gameEventTypes =
    [
        ("player_death", typeof(PlayerDeathEvent)),
        ("player_hurt", typeof(PlayerHurtEvent)),
        ("weapon_fire", typeof(WeaponFireEvent)),
        ("player_blind", typeof(PlayerBlindEvent)),
        ("bomb_planted", typeof(BombPlantedEvent)),
        ("bomb_defused", typeof(BombDefusedEvent)),
        ("bomb_exploded", typeof(BombExplodedEvent)),
        ("round_start", typeof(RoundStartEvent)),
        ("round_end", typeof(RoundEndEvent)),
        ("round_mvp", typeof(RoundMvpEvent)),
        ("player_connect", typeof(PlayerConnectEvent)),
        ("player_disconnect", typeof(PlayerDisconnectEvent)),
        ("player_team", typeof(PlayerTeamEvent)),
        ("item_pickup", typeof(ItemPickupEvent)),
        ("item_drop", typeof(ItemDropEvent)),
        ("item_equip", typeof(ItemEquipEvent)),
        ("player_spawn", typeof(PlayerSpawnEvent)),
        ("round_freeze_end", typeof(RoundFreezeEndEvent)),
        ("round_officially_ended", typeof(RoundOfficiallyEndedEvent)),
        ("round_prestart", typeof(RoundPreStartEvent)),
        ("round_poststart", typeof(RoundPoststartEvent)),
        ("bullet_damage", typeof(BulletDamageEvent)),
        ("halftime", typeof(HalfTimeEvent)),
        ("begin_new_match", typeof(BeginNewMatchEvent)),
        ("cs_win_panel_match", typeof(CsWinPanelMatchEvent)),
        ("game_restart", typeof(GameRestartEvent)),
        ("announce_phase_end", typeof(AnnouncePhaseEndEvent)),
        ("smokegrenade_expired", typeof(SmokeGrenadeExpiredEvent)),
        ("grenade_thrown", typeof(GrenadeThrownEvent)),
        ("weapon_zoom", typeof(WeaponZoomEvent)),
        ("weapon_reload", typeof(WeaponReloadEvent)),
        ("weapon_fire_on_empty", typeof(WeaponFireOnEmptyEvent)),
        ("player_jump", typeof(PlayerJumpEvent)),
        ("bomb_pickup", typeof(BombPickupEvent)),
        ("bomb_dropped", typeof(BombDroppedEvent)),
        ("bomb_beginplant", typeof(BombBeginplantEvent)),
        ("bomb_abortplant", typeof(BombAbortplantEvent)),
        ("bomb_begindefuse", typeof(BombBegindefuseEvent)),
        ("bomb_abortdefuse", typeof(BombAbortdefuseEvent)),
        ("defuser_pickup", typeof(DefuserPickupEvent)),
        ("defuser_dropped", typeof(DefuserDroppedEvent)),
        ("player_footstep", typeof(PlayerFootstepEvent)),
        ("player_avenged_teammate", typeof(PlayerAvengedTeammateEvent)),
        ("buytime_ended", typeof(BuyTimeEndedEvent)),
        ("cs_round_start_beep", typeof(CsRoundStartBeepEvent)),
        ("inferno_expire", typeof(InfernoExpireEvent)),
        ("inferno_extinguish", typeof(InfernoExtinguishEvent)),
        ("flashbang_detonate", typeof(FlashbangDetonateEvent)),
        ("hegrenade_detonate", typeof(HegrenadeDetonateEvent)),
        ("smokegrenade_detonate", typeof(SmokeGrenadeDetonateEvent)),
        ("inferno_startburn", typeof(InfernoStartburnEvent)),
        // Synthesized (EntityChangeScanner): molotov/incendiary has no usable wire detonation
        // event in GOTV, so this is attributed from the CMolotovProjectile thrower handle.
        ("molotov_thrown", typeof(MolotovThrownEvent)),
        ("decoy_detonate", typeof(DecoyDetonateEvent)),
        ("bullet_impact", typeof(BulletImpactEvent)),
        ("other_death", typeof(OtherDeathEvent)),
        // HLTV/pro round-end marker — used by Cs2HltvProfile.RoundEnd. Generated
        // type already exists (CsPreRestartEvent); previously parsed as
        // UnknownGameEvent because it wasn't registered here.
        ("cs_pre_restart", typeof(CsPreRestartEvent)),
        // Match/round phase boundary events used by the gameplay_phase /
        // regulation_status state machines in BuiltinContexts. Auto-generated
        // record types already exist; just registering name → type so the
        // analysis trigger pipeline can subscribe.
        ("cs_intermission", typeof(CsIntermissionEvent)),
        ("cs_match_end_restart", typeof(CsMatchEndRestartEvent)),
        ("round_announce_match_start", typeof(RoundAnnounceMatchStartEvent)),
        ("round_announce_match_point", typeof(RoundAnnounceMatchPointEvent)),
        ("round_announce_last_round_half", typeof(RoundAnnounceLastRoundHalfEvent)),
        ("round_announce_warmup", typeof(RoundAnnounceWarmupEvent)),
        ("warmup_end", typeof(WarmupEndEvent)),
        ("announce_phase_end", typeof(AnnouncePhaseEndEvent)),
        // Per-round end markers. `round_end` (the canonical Valve event) fires
        // 0 times in both MM and HLTV demos; `cs_win_panel_round` and
        // `cs_round_final_beep` are the actual signals. `cs_round_final_beep`
        // fires 18×/24× across MM/HLTV — the most reliable per-round end
        // signal we have. See docs/Demo-Event-Compatibility.md.
        ("cs_win_panel_round", typeof(CsWinPanelRoundEvent)),
        ("cs_round_final_beep", typeof(CsRoundFinalBeepEvent)),
        // Per-player team-switch event; fires 10× at halftime in HLTV
        // (per-player). The only halftime-adjacent signal we have, since
        // `halftime` itself fires 0 times in the bench demos.
        ("switch_team", typeof(SwitchTeamEvent))
    ];

    private static readonly (string Name, Type Type)[] _netMessageTypes =
    [
        ("CDemoFileHeader", typeof(CDemoFileHeader)),
        ("CNETMsg_Tick", typeof(CNETMsg_Tick))
    ];

    private readonly Dictionary<string, EventRegistration> _events;
    private readonly Dictionary<string, NetMessageRegistration> _netMessages;

    private EventRegistry(
        Dictionary<string, EventRegistration> events,
        Dictionary<string, NetMessageRegistration> netMessages)
    {
        _events = events;
        _netMessages = netMessages;
    }

    /// <summary>All registered game-event wire names (for diagnostics / did-you-mean suggestions).</summary>
    public IEnumerable<string> EventNames => _events.Keys;

    /// <summary>All registered net-message payload class names (for diagnostics / did-you-mean suggestions).</summary>
    public IEnumerable<string> NetMessageNames => _netMessages.Keys;

    /// <summary>Builds a registry by reflecting field accessors off every registered event and net-message type.</summary>
    public static EventRegistry Build()
    {
        Dictionary<string, EventRegistration> events = BuildGameEventMap();
        Dictionary<string, NetMessageRegistration> netMessages = BuildNetMessageMap();
        return new EventRegistry(events, netMessages);
    }

    /// <summary>Returns the registration for a game event by wire name, or <c>null</c>.</summary>
    public EventRegistration? GetEvent(string name) =>
        _events.GetValueOrDefault(name);

    /// <summary>Returns the registration for a net message by payload class name, or <c>null</c>.</summary>
    public NetMessageRegistration? GetNetMessage(string name) =>
        _netMessages.GetValueOrDefault(name);

    /// <summary>True when <paramref name="name" /> matches a registered game event.</summary>
    public bool IsGameEvent(string name) => _events.ContainsKey(name);

    /// <summary>True when <paramref name="name" /> matches a registered net message.</summary>
    public bool IsNetMessage(string name) => _netMessages.ContainsKey(name);

    /// <summary>
    ///     Resolves <paramref name="name" /> to either a game-event or net-message CLR type. Returns <c>false</c> if
    ///     unknown.
    /// </summary>
    public bool TryResolve(string name, out Type type)
    {
        if (_events.TryGetValue(name, out EventRegistration? ev))
        {
            type = ev.EventType;
            return true;
        }

        if (_netMessages.TryGetValue(name, out NetMessageRegistration? nm))
        {
            type = nm.PayloadType;
            return true;
        }

        type = null!;
        return false;
    }

    private static Dictionary<string, EventFieldAccessor> BuildFieldAccessors(Type type)
    {
        Dictionary<string, EventFieldAccessor> accessors = new(StringComparer.OrdinalIgnoreCase);

        foreach (PropertyInfo prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (prop.DeclaringType == typeof(GameEvent))
            {
                continue;
            }

            if (prop.DeclaringType == typeof(object))
            {
                continue;
            }

            if (!prop.CanRead)
            {
                continue;
            }

            accessors[prop.Name] = EventFieldAccessor.FromProperty(prop);
        }

        return accessors;
    }

    private static Dictionary<string, EventRegistration> BuildGameEventMap()
    {
        Dictionary<string, EventRegistration> map = new(StringComparer.OrdinalIgnoreCase);

        foreach ((string name, Type type) in _gameEventTypes)
        {
            Dictionary<string, EventFieldAccessor> fields = BuildFieldAccessors(type);
            map[name] = new EventRegistration(name, type, fields);
        }

        return map;
    }

    private static Dictionary<string, NetMessageRegistration> BuildNetMessageMap()
    {
        Dictionary<string, NetMessageRegistration> map = new(StringComparer.OrdinalIgnoreCase);

        foreach ((string name, Type type) in _netMessageTypes)
        {
            Dictionary<string, EventFieldAccessor> fields = BuildFieldAccessors(type);
            map[name] = new NetMessageRegistration(name, type, fields);
        }

        return map;
    }
}
