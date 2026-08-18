#region

using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Parser;

#endregion

namespace CS2DemoKit.Analysis.Profiles;

/// <summary>
///     Profile for HLTV / pro broadcast recordings. Inherits the GOTV vocabulary
///     and overrides where pro demos diverge — most notably:
///     <list type="bullet">
///         <item>
///             <description>
///                 <c>round_officially_ended</c> never fires; <c>cs_pre_restart</c> serves as the round-end
///                 marker.
///             </description>
///         </item>
///         <item>
///             <description><c>player_blind</c> is absent — flash-related stats degrade gracefully via <c>requires:</c>.</description>
///         </item>
///         <item>
///             <description>
///                 <c>weapon_reload</c>, <c>weapon_zoom</c>, <c>player_jump</c>, <c>player_footstep</c> are
///                 absent.
///             </description>
///         </item>
///         <item>
///             <description>Adds <c>hltv_chase</c>, <c>hltv_fixed</c>, <c>entity_killed</c>, <c>player_sound</c>.</description>
///         </item>
///         <item>
///             <description><c>grenade_thrown</c> is emitted on HLTV but not GOTV.</description>
///         </item>
///     </list>
///     Best-effort skeleton: refined as the profile smoke test catches gaps.
/// </summary>
public class Cs2HltvProfile : Cs2GotvProfile
{
    /// <inheritdoc />
    public override DemoFeatureSet Features =>
        DemoFeatureSet.HasGrenadeThrown
        | DemoFeatureSet.HasHltvCameraEvents
        | DemoFeatureSet.HasEntityKilled
        | DemoFeatureSet.HasPlayerSound
        | DemoFeatureSet.HasCsPreRestart;

    // ── HLTV-only events ──────────────────────────────────────────────────

    /// <inheritdoc />
    public override LogicalEventBinding? GrenadeThrown =>
        LogicalEventBinding.Of("grenade_thrown");

    /// <inheritdoc />
    public override DemoSourceKind Kind => DemoSourceKind.HltvPro;

    // ── Events absent on HLTV ─────────────────────────────────────────────

    /// <inheritdoc />
    public override LogicalEventBinding? PlayerBlind => null;

    /// <inheritdoc />
    public override LogicalEventBinding? PlayerFootstep => null;

    /// <inheritdoc />
    public override LogicalEventBinding? PlayerJump => null;

    // ── Round end: cs_pre_restart instead of round_officially_ended ──

    /// <inheritdoc />
    public override LogicalEventBinding? RoundEnd =>
        LogicalEventBinding.FirstWins(
            "cs_pre_restart",
            "cs_win_panel_match");

    /// <summary>HLTV demos lack a per-round official-end event entirely.</summary>
    public override LogicalEventBinding? RoundOfficiallyEnded => null;

    /// <inheritdoc />
    public override LogicalEventBinding? WeaponReload => null;

    /// <inheritdoc />
    public override LogicalEventBinding? WeaponZoom => null;

    // hltv_chase, hltv_fixed, entity_killed, player_sound: register the
    // generated event types when they exist; left null until their
    // EventRegistry entries are added.
}
