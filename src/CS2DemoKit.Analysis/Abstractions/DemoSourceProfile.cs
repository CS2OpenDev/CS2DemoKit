#region

using CS2DemoKit.Parser;

#endregion

namespace CS2DemoKit.Analysis.Abstractions;

/// <summary>
///     Engine-side profile describing the event vocabulary a demo source emits.
///     Implementations bind logical event names (<c>RoundEnd</c>,
///     <c>PlayerBlind</c>, …) to ordered lists of concrete game events. The
///     analysis evaluator's rule builder consults these bindings at build
///     time to expand <c>$logical</c> trigger references into concrete
///     event subscriptions, so source-awareness has zero runtime cost.
/// </summary>
/// <remarks>
///     A logical-event accessor returning <c>null</c> means "this source
///     does not emit any concrete event for this logical concept" — for
///     example, the HLTV profile returns <c>null</c> for
///     <see cref="PlayerBlind" /> because HLTV demos lack the underlying
///     <c>player_blind</c> event entirely. Rules that strictly require
///     such an event should declare a <c>requires:</c> field listing the
///     logical events they need; the rule builder skips those rules
///     silently when the active profile lacks them.
///     Adding a new logical event to this base class is non-breaking —
///     all existing implementations inherit the default <c>null</c>
///     return. Override what your source supports and leave the rest.
///     Authors writing custom profiles for unusual sources can extend
///     this class directly. A future release will support loading such
///     subclasses from external assemblies; for v0.0.2 only the
///     internally-shipped profiles are recognised.
/// </remarks>
public abstract class DemoSourceProfile
{
    /// <summary>Logical binding for the "bomb abort defuse" game event. See class summary for binding semantics.</summary>
    public virtual LogicalEventBinding? BombAbortDefuse => null;

    /// <summary>Logical binding for the "bomb abort plant" game event. See class summary for binding semantics.</summary>
    public virtual LogicalEventBinding? BombAbortPlant => null;

    /// <summary>Logical binding for the "bomb begin defuse" game event. See class summary for binding semantics.</summary>
    public virtual LogicalEventBinding? BombBeginDefuse => null;

    // ── Bomb ──────────────────────────────────────────────────────────────

    /// <summary>Logical binding for the "bomb begin plant" game event. See class summary for binding semantics.</summary>
    public virtual LogicalEventBinding? BombBeginPlant => null;

    /// <summary>Logical binding for the "bomb defused" game event. See class summary for binding semantics.</summary>
    public virtual LogicalEventBinding? BombDefused => null;

    /// <summary>Logical binding for the "bomb dropped" game event. See class summary for binding semantics.</summary>
    public virtual LogicalEventBinding? BombDropped => null;

    /// <summary>Logical binding for the "bomb exploded" game event. See class summary for binding semantics.</summary>
    public virtual LogicalEventBinding? BombExploded => null;

    /// <summary>Logical binding for the "bomb pickup" game event. See class summary for binding semantics.</summary>
    public virtual LogicalEventBinding? BombPickup => null;

    /// <summary>Logical binding for the "bomb planted" game event. See class summary for binding semantics.</summary>
    public virtual LogicalEventBinding? BombPlanted => null;

    /// <summary>Logical binding for the "bullet damage" game event. See class summary for binding semantics.</summary>
    public virtual LogicalEventBinding? BulletDamage => null;

    /// <summary>Logical binding for the "bullet impact" game event. See class summary for binding semantics.</summary>
    public virtual LogicalEventBinding? BulletImpact => null;

    /// <summary>Logical binding for the "combat start" game event. See class summary for binding semantics.</summary>
    public virtual LogicalEventBinding? CombatStart => null;

    /// <summary>Logical binding for the "decoy detonate" game event. See class summary for binding semantics.</summary>
    public virtual LogicalEventBinding? DecoyDetonate => null;

    /// <summary>Logical binding for the "defuser dropped" game event. See class summary for binding semantics.</summary>
    public virtual LogicalEventBinding? DefuserDropped => null;

    /// <summary>Logical binding for the "defuser pickup" game event. See class summary for binding semantics.</summary>
    public virtual LogicalEventBinding? DefuserPickup => null;

    /// <summary>Display name used in tooling output and error messages.</summary>
    public virtual string DisplayName =>
        MaxBuildNumber == int.MaxValue && MinBuildNumber == 0
            ? Kind.ToString()
            : $"{Kind} (builds {MinBuildNumber}..{MaxBuildNumber})";

    /// <summary>Logical binding for the "entity killed" game event. See class summary for binding semantics.</summary>
    public virtual LogicalEventBinding? EntityKilled => null;

    // ── Capabilities (delegates to parser-side feature flags) ─────────────

    /// <summary>
    ///     Returns the set of known capabilities advertised by this profile.
    ///     Used for graceful degradation when a rule's <c>requires:</c>
    ///     declaration can't be satisfied.
    /// </summary>
    public abstract DemoFeatureSet Features { get; }

    /// <summary>Logical binding for the "flashbang detonate" game event. See class summary for binding semantics.</summary>
    public virtual LogicalEventBinding? FlashbangDetonate => null;

    /// <summary>Logical binding for the "game restart" game event. See class summary for binding semantics.</summary>
    public virtual LogicalEventBinding? GameRestart => null;

    // ── Grenades / utility ────────────────────────────────────────────────

    /// <summary>Logical binding for the "grenade thrown" game event. See class summary for binding semantics.</summary>
    public virtual LogicalEventBinding? GrenadeThrown => null;

    /// <summary>Logical binding for the "halftime" game event. See class summary for binding semantics.</summary>
    public virtual LogicalEventBinding? Halftime => null;

    /// <summary>Logical binding for the "he grenade detonate" game event. See class summary for binding semantics.</summary>
    public virtual LogicalEventBinding? HeGrenadeDetonate => null;

    // ── HLTV-specific / observer ──────────────────────────────────────────

    /// <summary>Logical binding for the "hltv chase" game event. See class summary for binding semantics.</summary>
    public virtual LogicalEventBinding? HltvChase => null;

    /// <summary>Logical binding for the "hltv fixed" game event. See class summary for binding semantics.</summary>
    public virtual LogicalEventBinding? HltvFixed => null;

    /// <summary>Logical binding for the "inferno expired" game event. See class summary for binding semantics.</summary>
    public virtual LogicalEventBinding? InfernoExpired => null;

    /// <summary>Logical binding for the "inferno extinguished" game event. See class summary for binding semantics.</summary>
    public virtual LogicalEventBinding? InfernoExtinguished => null;

    /// <summary>Logical binding for the "inferno start" game event. See class summary for binding semantics.</summary>
    public virtual LogicalEventBinding? InfernoStart => null;

    /// <summary>Logical binding for the "intermission" game event. See class summary for binding semantics.</summary>
    public virtual LogicalEventBinding? Intermission => null;

    /// <summary>Logical binding for the "item drop" game event. See class summary for binding semantics.</summary>
    public virtual LogicalEventBinding? ItemDrop => null;

    /// <summary>Logical binding for the "item equip" game event. See class summary for binding semantics.</summary>
    public virtual LogicalEventBinding? ItemEquip => null;

    // ── Items / economy ───────────────────────────────────────────────────

    /// <summary>Logical binding for the "item pickup" game event. See class summary for binding semantics.</summary>
    public virtual LogicalEventBinding? ItemPickup => null;
    // ── Identity / matching ───────────────────────────────────────────────

    /// <summary>The source kind this profile recognises.</summary>
    public abstract DemoSourceKind Kind { get; }

    /// <summary>Logical binding for the "match end" game event. See class summary for binding semantics.</summary>
    public virtual LogicalEventBinding? MatchEnd => null;

    // ── Match lifecycle ───────────────────────────────────────────────────

    /// <summary>Logical binding for the "match start" game event. See class summary for binding semantics.</summary>
    public virtual LogicalEventBinding? MatchStart => null;

    /// <summary>
    ///     Upper bound of the game build-number range this profile applies to
    ///     (inclusive). Default <see cref="int.MaxValue" /> means "any build".
    /// </summary>
    public virtual int MaxBuildNumber => int.MaxValue;

    /// <summary>
    ///     Lower bound of the game build-number range this profile applies to
    ///     (inclusive). Default 0 means "any build".
    /// </summary>
    public virtual int MinBuildNumber => 0;

    /// <summary>Logical binding for the "molotov detonate" game event. See class summary for binding semantics.</summary>
    public virtual LogicalEventBinding? MolotovDetonate => null;

    /// <summary>
    ///     Logical binding for the synthesized "molotov thrown" event (one per <c>CMolotovProjectile</c>
    ///     creation, attributed to its thrower). GOTV has no usable wire molotov-detonation event, so
    ///     the scanner synthesizes <c>molotov_thrown</c>; profiles that expose it bind this. See class
    ///     summary for binding semantics.
    /// </summary>
    public virtual LogicalEventBinding? MolotovThrown => null;

    /// <summary>Logical binding for the "other death" game event. See class summary for binding semantics.</summary>
    public virtual LogicalEventBinding? OtherDeath => null;

    /// <summary>Logical binding for the "phase end" game event. See class summary for binding semantics.</summary>
    public virtual LogicalEventBinding? PhaseEnd => null;

    /// <summary>Logical binding for the "player avenged teammate" game event. See class summary for binding semantics.</summary>
    public virtual LogicalEventBinding? PlayerAvengedTeammate => null;

    /// <summary>Logical binding for the "player blind" game event. See class summary for binding semantics.</summary>
    public virtual LogicalEventBinding? PlayerBlind => null;

    /// <summary>Logical binding for the "player connect" game event. See class summary for binding semantics.</summary>
    public virtual LogicalEventBinding? PlayerConnect => null;

    /// <summary>Logical binding for the "player death" game event. See class summary for binding semantics.</summary>
    public virtual LogicalEventBinding? PlayerDeath => null;

    /// <summary>Logical binding for the "player disconnect" game event. See class summary for binding semantics.</summary>
    public virtual LogicalEventBinding? PlayerDisconnect => null;

    /// <summary>Logical binding for the "player footstep" game event. See class summary for binding semantics.</summary>
    public virtual LogicalEventBinding? PlayerFootstep => null;

    /// <summary>Logical binding for the "player hurt" game event. See class summary for binding semantics.</summary>
    public virtual LogicalEventBinding? PlayerHurt => null;

    /// <summary>Logical binding for the "player jump" game event. See class summary for binding semantics.</summary>
    public virtual LogicalEventBinding? PlayerJump => null;

    /// <summary>Logical binding for the "player sound" game event. See class summary for binding semantics.</summary>
    public virtual LogicalEventBinding? PlayerSound => null;

    // ── Player lifecycle ──────────────────────────────────────────────────

    /// <summary>Logical binding for the "player spawn" game event. See class summary for binding semantics.</summary>
    public virtual LogicalEventBinding? PlayerSpawn => null;

    /// <summary>Logical binding for the "player team" game event. See class summary for binding semantics.</summary>
    public virtual LogicalEventBinding? PlayerTeam => null;

    /// <summary>Logical binding for the "round announce last round half" game event. See class summary for binding semantics.</summary>
    public virtual LogicalEventBinding? RoundAnnounceLastRoundHalf => null;

    /// <summary>Logical binding for the "round announce match point" game event. See class summary for binding semantics.</summary>
    public virtual LogicalEventBinding? RoundAnnounceMatchPoint => null;

    /// <summary>Logical binding for the "round announce match start" game event. See class summary for binding semantics.</summary>
    public virtual LogicalEventBinding? RoundAnnounceMatchStart => null;

    /// <summary>Logical binding for the "round end" game event. See class summary for binding semantics.</summary>
    public virtual LogicalEventBinding? RoundEnd => null;

    /// <summary>Logical binding for the "round freeze end" game event. See class summary for binding semantics.</summary>
    public virtual LogicalEventBinding? RoundFreezeEnd => null;

    /// <summary>Logical binding for the "round MVP" game event. See class summary for binding semantics.</summary>
    public virtual LogicalEventBinding? RoundMvp => null;

    /// <summary>Logical binding for the "round officially ended" game event. See class summary for binding semantics.</summary>
    public virtual LogicalEventBinding? RoundOfficiallyEnded => null;

    /// <summary>Logical binding for the "round post start" game event. See class summary for binding semantics.</summary>
    public virtual LogicalEventBinding? RoundPostStart => null;

    // ── Round lifecycle ───────────────────────────────────────────────────

    /// <summary>Logical binding for the "round pre start" game event. See class summary for binding semantics.</summary>
    public virtual LogicalEventBinding? RoundPreStart => null;

    /// <summary>Logical binding for the "round start" game event. See class summary for binding semantics.</summary>
    public virtual LogicalEventBinding? RoundStart => null;

    /// <summary>Logical binding for the "round start beep" game event. See class summary for binding semantics.</summary>
    public virtual LogicalEventBinding? RoundStartBeep => null;

    /// <summary>Logical binding for the "smoke detonate" game event. See class summary for binding semantics.</summary>
    public virtual LogicalEventBinding? SmokeDetonate => null;

    /// <summary>Logical binding for the "smoke expired" game event. See class summary for binding semantics.</summary>
    public virtual LogicalEventBinding? SmokeExpired => null;

    /// <summary>Logical binding for the "warmup end" game event. See class summary for binding semantics.</summary>
    public virtual LogicalEventBinding? WarmupEnd => null;

    /// <summary>Logical binding for the "warmup start" game event. See class summary for binding semantics.</summary>
    public virtual LogicalEventBinding? WarmupStart => null;

    // ── Combat ────────────────────────────────────────────────────────────

    /// <summary>Logical binding for the "weapon fire" game event. See class summary for binding semantics.</summary>
    public virtual LogicalEventBinding? WeaponFire => null;

    /// <summary>Logical binding for the "weapon fire on empty" game event. See class summary for binding semantics.</summary>
    public virtual LogicalEventBinding? WeaponFireOnEmpty => null;

    /// <summary>Logical binding for the "weapon reload" game event. See class summary for binding semantics.</summary>
    public virtual LogicalEventBinding? WeaponReload => null;

    /// <summary>Logical binding for the "weapon zoom" game event. See class summary for binding semantics.</summary>
    public virtual LogicalEventBinding? WeaponZoom => null;
}
