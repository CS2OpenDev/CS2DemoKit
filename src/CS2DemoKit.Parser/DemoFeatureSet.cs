namespace CS2DemoKit.Parser;

/// <summary>
///     Capability flags advertising which game-event categories a demo's source
///     is expected to emit. Set by <see cref="DemoSourceClassifier" /> based on
///     header heuristics. Consumers (especially the analysis engine) can
///     check these to gracefully skip rules that depend on unavailable events.
/// </summary>
[Flags]
public enum DemoFeatureSet : long
{
    None = 0,

    /// <summary>
    ///     <c>player_blind</c> events are emitted (GOTV).
    /// </summary>
    HasPlayerBlind = 1L << 0,

    /// <summary>
    ///     <c>round_officially_ended</c> events are emitted per round (GOTV).
    /// </summary>
    HasRoundOfficiallyEnded = 1L << 1,

    /// <summary>
    ///     <c>weapon_reload</c> events are emitted (GOTV).
    /// </summary>
    HasWeaponReload = 1L << 2,

    /// <summary>
    ///     <c>weapon_zoom</c> events are emitted (GOTV).
    /// </summary>
    HasWeaponZoom = 1L << 3,

    /// <summary>
    ///     <c>grenade_thrown</c> events are emitted (HLTV).
    /// </summary>
    HasGrenadeThrown = 1L << 4,

    /// <summary>
    ///     <c>hltv_*</c> camera/observer events are emitted (HLTV).
    /// </summary>
    HasHltvCameraEvents = 1L << 5,

    /// <summary>
    ///     <c>entity_killed</c> events are emitted (HLTV's alternate death track).
    /// </summary>
    HasEntityKilled = 1L << 6,

    /// <summary>
    ///     <c>player_sound</c> events are emitted (HLTV).
    /// </summary>
    HasPlayerSound = 1L << 7,

    /// <summary>
    ///     <c>cs_pre_restart</c> is emitted as a round-end marker (HLTV).
    /// </summary>
    HasCsPreRestart = 1L << 8
}
