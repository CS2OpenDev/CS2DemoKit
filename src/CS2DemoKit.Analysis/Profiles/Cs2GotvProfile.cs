#region

using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Parser;

#endregion

namespace CS2DemoKit.Analysis.Profiles;

/// <summary>
///     Default profile for Valve matchmaking GOTV (a.k.a. SourceTV) recordings.
///     This is the broadest event vocabulary we support — when the source is
///     unknown the registry falls back to this profile because most CS2
///     statistics tooling assumes its event set.
/// </summary>
public class Cs2GotvProfile : DemoSourceProfile
{
    /// <inheritdoc />
    public override LogicalEventBinding? BombAbortDefuse =>
        LogicalEventBinding.Of("bomb_abortdefuse");

    /// <inheritdoc />
    public override LogicalEventBinding? BombAbortPlant =>
        LogicalEventBinding.Of("bomb_abortplant");

    /// <inheritdoc />
    public override LogicalEventBinding? BombBeginDefuse =>
        LogicalEventBinding.Of("bomb_begindefuse");

    // ── Bomb ──────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public override LogicalEventBinding? BombBeginPlant =>
        LogicalEventBinding.Of("bomb_beginplant");

    /// <inheritdoc />
    public override LogicalEventBinding? BombDefused =>
        LogicalEventBinding.Of("bomb_defused");

    /// <inheritdoc />
    public override LogicalEventBinding? BombDropped =>
        LogicalEventBinding.Of("bomb_dropped");

    /// <inheritdoc />
    public override LogicalEventBinding? BombExploded =>
        LogicalEventBinding.Of("bomb_exploded");

    /// <inheritdoc />
    public override LogicalEventBinding? BombPickup =>
        LogicalEventBinding.Of("bomb_pickup");

    /// <inheritdoc />
    public override LogicalEventBinding? BombPlanted =>
        LogicalEventBinding.Of("bomb_planted");

    /// <inheritdoc />
    public override LogicalEventBinding? BulletDamage =>
        LogicalEventBinding.Of("bullet_damage");

    /// <inheritdoc />
    public override LogicalEventBinding? BulletImpact =>
        LogicalEventBinding.Of("bullet_impact");

    /// <inheritdoc />
    public override LogicalEventBinding? CombatStart =>
        LogicalEventBinding.Of("buytime_ended");

    /// <inheritdoc />
    public override LogicalEventBinding? DecoyDetonate =>
        LogicalEventBinding.Of("decoy_detonate");

    /// <inheritdoc />
    public override LogicalEventBinding? DefuserDropped =>
        LogicalEventBinding.Of("defuser_dropped");

    /// <inheritdoc />
    public override LogicalEventBinding? DefuserPickup =>
        LogicalEventBinding.Of("defuser_pickup");

    /// <inheritdoc />
    public override DemoFeatureSet Features =>
        DemoFeatureSet.HasPlayerBlind
        | DemoFeatureSet.HasRoundOfficiallyEnded
        | DemoFeatureSet.HasWeaponReload
        | DemoFeatureSet.HasWeaponZoom;

    /// <inheritdoc />
    public override LogicalEventBinding? FlashbangDetonate =>
        LogicalEventBinding.Of("flashbang_detonate");

    /// <inheritdoc />
    public override LogicalEventBinding? GameRestart =>
        LogicalEventBinding.Of("game_restart");

    /// <inheritdoc />
    public override LogicalEventBinding? Halftime =>
        LogicalEventBinding.Of("halftime");

    // ── Grenades / utility ────────────────────────────────────────────────

    /// <inheritdoc />
    public override LogicalEventBinding? HeGrenadeDetonate =>
        LogicalEventBinding.Of("hegrenade_detonate");

    /// <inheritdoc />
    public override LogicalEventBinding? InfernoExpired =>
        LogicalEventBinding.Of("inferno_expire");

    /// <inheritdoc />
    public override LogicalEventBinding? InfernoExtinguished =>
        LogicalEventBinding.Of("inferno_extinguish");

    /// <inheritdoc />
    public override LogicalEventBinding? InfernoStart =>
        LogicalEventBinding.Of("inferno_startburn");

    /// <inheritdoc />
    public override LogicalEventBinding? ItemDrop =>
        LogicalEventBinding.Of("item_drop");

    /// <inheritdoc />
    public override LogicalEventBinding? ItemEquip =>
        LogicalEventBinding.Of("item_equip");

    // ── Items / economy ───────────────────────────────────────────────────

    /// <inheritdoc />
    public override LogicalEventBinding? ItemPickup =>
        LogicalEventBinding.Of("item_pickup");

    /// <inheritdoc />
    public override DemoSourceKind Kind => DemoSourceKind.GotvMatchmaking;

    /// <inheritdoc />
    public override LogicalEventBinding? MatchEnd =>
        LogicalEventBinding.Of("cs_win_panel_match");

    // ── Match lifecycle ───────────────────────────────────────────────────

    /// <inheritdoc />
    public override LogicalEventBinding? MatchStart =>
        LogicalEventBinding.Of("begin_new_match");

    /// <inheritdoc />
    public override LogicalEventBinding? MolotovThrown =>
        // Synthesized by EntityChangeScanner (m_hThrower attribution) — GOTV has no wire
        // molotov-detonation event. Backs the `molotov` actor_slot view (grenade-usage stats).
        LogicalEventBinding.Of("molotov_thrown");

    /// <inheritdoc />
    public override LogicalEventBinding? OtherDeath =>
        LogicalEventBinding.Of("other_death");

    /// <inheritdoc />
    public override LogicalEventBinding? PhaseEnd =>
        LogicalEventBinding.Of("announce_phase_end");

    /// <inheritdoc />
    public override LogicalEventBinding? PlayerAvengedTeammate =>
        LogicalEventBinding.Of("player_avenged_teammate");

    /// <inheritdoc />
    public override LogicalEventBinding? PlayerBlind =>
        LogicalEventBinding.Of("player_blind");

    /// <inheritdoc />
    public override LogicalEventBinding? PlayerConnect =>
        LogicalEventBinding.Of("player_connect");

    /// <inheritdoc />
    public override LogicalEventBinding? PlayerDeath =>
        LogicalEventBinding.Of("player_death");

    /// <inheritdoc />
    public override LogicalEventBinding? PlayerDisconnect =>
        LogicalEventBinding.Of("player_disconnect");

    /// <inheritdoc />
    public override LogicalEventBinding? PlayerFootstep =>
        LogicalEventBinding.Of("player_footstep");

    /// <inheritdoc />
    public override LogicalEventBinding? PlayerHurt =>
        LogicalEventBinding.Of("player_hurt");

    /// <inheritdoc />
    public override LogicalEventBinding? PlayerJump =>
        LogicalEventBinding.Of("player_jump");

    // ── Player lifecycle ──────────────────────────────────────────────────

    /// <inheritdoc />
    public override LogicalEventBinding? PlayerSpawn =>
        LogicalEventBinding.Of("player_spawn");

    /// <inheritdoc />
    public override LogicalEventBinding? PlayerTeam =>
        LogicalEventBinding.Of("player_team");

    /// <inheritdoc />
    public override LogicalEventBinding? RoundEnd =>
        // Final round of a match has no round_officially_ended; cs_win_panel_match
        // (Valve's match-summary marker) serves as the terminal fallback for
        // normal completed matches. Demos cut off mid-round will not finalise
        // their final round — that is acceptable.
        //
        // The events here must be MUTUALLY EXCLUSIVE PER ROUND: the v2 `count:` planner emits one
        // unguarded Increment edge per concrete event, so two markers firing in the same round
        // count that round twice. round_officially_ended and cs_win_panel_match never overlap
        // (the final round is exactly the one with no round_officially_ended). This is why
        // cs_pre_restart — which fires alongside round_officially_ended on Valve servers — is NOT
        // listed here, and lives on Cs2GotvPreRestartProfile instead.
        LogicalEventBinding.FirstWins(
            "round_officially_ended",
            "cs_win_panel_match");

    /// <inheritdoc />
    public override LogicalEventBinding? RoundFreezeEnd =>
        LogicalEventBinding.Of("round_freeze_end");

    /// <inheritdoc />
    public override LogicalEventBinding? RoundMvp =>
        LogicalEventBinding.Of("round_mvp");

    /// <inheritdoc />
    public override LogicalEventBinding? RoundOfficiallyEnded =>
        LogicalEventBinding.Of("round_officially_ended");

    /// <inheritdoc />
    public override LogicalEventBinding? RoundPostStart =>
        LogicalEventBinding.Of("round_poststart");

    // ── Round lifecycle ───────────────────────────────────────────────────

    /// <inheritdoc />
    public override LogicalEventBinding? RoundPreStart =>
        LogicalEventBinding.Of("round_prestart");

    /// <inheritdoc />
    public override LogicalEventBinding? RoundStart =>
        LogicalEventBinding.Of("round_start");

    /// <inheritdoc />
    public override LogicalEventBinding? RoundStartBeep =>
        LogicalEventBinding.Of("cs_round_start_beep");

    /// <inheritdoc />
    public override LogicalEventBinding? SmokeDetonate =>
        LogicalEventBinding.Of("smokegrenade_detonate");

    /// <inheritdoc />
    public override LogicalEventBinding? SmokeExpired =>
        LogicalEventBinding.Of("smokegrenade_expired");

    // ── Combat ────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public override LogicalEventBinding? WeaponFire =>
        LogicalEventBinding.Of("weapon_fire");

    /// <inheritdoc />
    public override LogicalEventBinding? WeaponFireOnEmpty =>
        LogicalEventBinding.Of("weapon_fire_on_empty");

    /// <inheritdoc />
    public override LogicalEventBinding? WeaponReload =>
        LogicalEventBinding.Of("weapon_reload");

    /// <inheritdoc />
    public override LogicalEventBinding? WeaponZoom =>
        LogicalEventBinding.Of("weapon_zoom");
}
