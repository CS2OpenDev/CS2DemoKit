#region

using CS2DemoKit.Analysis.Plugins.Markers;
using SchemaNames = CS2OpenSchema.SchemaNames;

#endregion

namespace CS2DemoKit.Analysis.Plugins;

/// <summary>
///     The shipped entity reads as <see cref="ProviderSpec" /> data: the
///     declarative equivalents of the hand-written provider classes, byte-identical by the
///     <c>ProviderDigestParityTests</c> gate. Once parity has baked, the default registries can
///     construct from these and the hand-written classes retire; until then both forms coexist
///     and the gate pins them together. Reads added since the original five have no hand-written
///     twin at all: they are spec-constructed on BOTH sides of the gate, so parity holds by
///     construction rather than by comparison.
/// </summary>
public static class BuiltinProviderSpecs
{
    /// <summary>entity.pawn.health — 0 means dead or never-networked, both "no value".</summary>
    public static ProviderSpec PawnHealth { get; } = new(
        "entity.pawn.health", "CCSPlayerPawn",
        SchemaNames.CBaseEntity.Health, typeof(int),
        true);

    /// <summary>entity.pawn.armor — 0 is a real observation (lane-default parity).</summary>
    public static ProviderSpec PawnArmor { get; } = new(
        "entity.pawn.armor", "CCSPlayerPawn",
        SchemaNames.CCSPlayerPawn.ArmorValue, typeof(int),
        UnseenAsDefault: true);

    /// <summary>entity.pawn.equipment_value — always emits (lane-default parity).</summary>
    public static ProviderSpec PawnEquipmentValue { get; } = new(
        "entity.pawn.equipment_value", "CCSPlayerPawn",
        SchemaNames.CCSPlayerPawn.CurrentEquipmentValue, typeof(int),
        UnseenAsDefault: true);

    /// <summary>entity.pawn.active_weapon_class — single-hop handle follow to the weapon's class name.</summary>
    public static ProviderSpec PawnActiveWeaponClass { get; } = new(
        "entity.pawn.active_weapon_class", "CCSPlayerPawn",
        "", typeof(string),
        ViaHandleToClassName: SchemaNames.CBasePlayerPawn.WeaponServices + "."
                                                                         + SchemaNames.CPlayerWeaponServices.ActiveWeapon);

    /// <summary>
    ///     entity.pawn.active_weapon_clip — two-hop read (Tier C): the pawn's active-weapon
    ///     handle → the weapon entity's <c>m_iClip1</c> (rounds currently in the magazine).
    ///     Null (slot skipped) when the pawn has no active weapon or the clip is unseen; 0 and
    ///     -1 (no-magazine weapons like knives) are real observations and emit as-is. NOTE:
    ///     rule-site reads are PRE-FRAME (the scanner snapshots the previous frame), so at a
    ///     kill event this is the clip BEFORE the killing shot — "last bullet" is <c>== 1</c>,
    ///     not <c>== 0</c>.
    /// </summary>
    public static ProviderSpec PawnActiveWeaponClip { get; } = new(
        "entity.pawn.active_weapon_clip", "CCSPlayerPawn",
        "", typeof(int),
        ViaHandleToField: new HandleFieldHop(
            SchemaNames.CBasePlayerPawn.WeaponServices + "." + SchemaNames.CPlayerWeaponServices.ActiveWeapon,
            SchemaNames.CBasePlayerWeapon.Clip1));

    /// <summary>
    ///     entity.pawn.place — the pawn's <c>m_szLastPlaceName</c>: the human-readable nav-mesh
    ///     place the player was last located in (e.g. <c>BombsiteA</c>, <c>CTSpawn</c>). A
    ///     <c>char[18]</c> on the wire; the tracker decodes fixed char arrays as a single UTF-8
    ///     string on the object lane, so this is a plain string read. Null (slot skipped) when
    ///     the field has never been networked for the pawn; maps without named nav areas simply
    ///     never populate it.
    /// </summary>
    public static ProviderSpec PawnPlace { get; } = new(
        "entity.pawn.place", "CCSPlayerPawn",
        SchemaNames.CCSPlayerPawn.LastPlaceName, typeof(string));

    /// <summary>
    ///     entity.pawn.duck_amount: the pawn's crouch ramp, dotted through
    ///     <c>CCSPlayer_MovementServices</c>. A <c>float32</c> in 0..1, CONTINUOUS rather than a
    ///     bool: it interpolates over the crouch transition, so a threshold test on it means
    ///     "how far into the crouch", not "is crouching". <c>VisibilityAnalyzer</c> already reads
    ///     this exact path to place the eye height, so the two stay consistent by sharing it.
    /// </summary>
    public static ProviderSpec PawnDuckAmount { get; } = new(
        "entity.pawn.duck_amount", "CCSPlayerPawn",
        SchemaNames.CBasePlayerPawn.MovementServices + "." + SchemaNames.CCSPlayerMovementServices.DuckAmount,
        typeof(float));

    /// <summary>
    ///     entity.pawn.max_speed: the pawn's current movement cap in units/second, dotted
    ///     through the movement services. Per-weapon (an AWP out caps far lower than a knife),
    ///     which is what makes it the right denominator for a counter-strafe test: the engine's
    ///     own "moving enough to spoil the shot" line sits at <c>0.34</c> of this, not at a
    ///     fixed speed. Quantised to 12 bits over 0..2048 (about 0.5 u/s steps), comfortably
    ///     finer than that threshold needs but not a full float.
    /// </summary>
    public static ProviderSpec PawnMaxSpeed { get; } = new(
        "entity.pawn.max_speed", "CCSPlayerPawn",
        SchemaNames.CBasePlayerPawn.MovementServices + "." + SchemaNames.CPlayerMovementServices.MaxSpeed,
        typeof(float));

    /// <summary>
    ///     entity.pawn.shots_fired: the pawn's position WITHIN the current burst, not a round
    ///     or match counter: it resets on trigger release and on weapon change, so it reads as
    ///     "how deep into this spray" and is the natural spray-index companion to
    ///     <see cref="WeaponRecoilIndex" />.
    /// </summary>
    public static ProviderSpec PawnShotsFired { get; } = new(
        "entity.pawn.shots_fired", "CCSPlayerPawn",
        SchemaNames.CCSPlayerPawn.ShotsFired, typeof(int));

    /// <summary>entity.pawn.is_scoped: whether the pawn is currently looking down a scope.</summary>
    public static ProviderSpec PawnIsScoped { get; } = new(
        "entity.pawn.is_scoped", "CCSPlayerPawn",
        SchemaNames.CCSPlayerPawn.IsScoped, typeof(bool));

    /// <summary>
    ///     entity.pawn.flash_duration: the blind time the server LATCHED for the flash, in
    ///     SECONDS; <c>0</c> means not flashed. Present so aim metrics can exclude blinded
    ///     engagements, which otherwise enter the population as catastrophic crosshair placement
    ///     that says nothing about aim.
    ///     <para>
    ///         <b>Latched, not counted down — measured, because the difference decides how a rule
    ///         may test it.</b> Sampled across every pawn-frame of the bundled GOTV sample
    ///         (230,856 of them), <c>m_flFlashDuration</c> is a step function: each flash writes
    ///         ONE value that then holds unchanged for the whole blind and steps back to <c>0</c>.
    ///         A server decrementing it per tick would network a fresh float on nearly every tick;
    ///         instead the busiest slot on the sample carries eleven distinct values across four
    ///         rounds. Two of the latches, with the pawn alive throughout: <c>0.6618</c> held for
    ///         0.94 s before zeroing, <c>1.7494</c> held for 2.46 s. The clear lands at about
    ///         1.4x the latched value after the latch, consistently across the sample, so the
    ///         column does end with the blind — it just never reads as the time still left in it.
    ///     </para>
    ///     <para>
    ///         So test it as <c>&gt; 0</c> for "was blinded at all". A magnitude test is a test on
    ///         how strong the flash was, not on how much of it remains, and a rule written as if it
    ///         were a countdown reads the full duration at every tick of the blind.
    ///     </para>
    ///     <para>
    ///         <c>m_blindStartTime</c> / <c>m_blindUntilTime</c> would express "now vs until"
    ///         directly, and the lens curates both on this class, but neither is networked on the
    ///         bundled sample: zero non-null reads over the same 230,856 pawn-frames. There is
    ///         nothing to switch to today.
    ///     </para>
    /// </summary>
    public static ProviderSpec PawnFlashDuration { get; } = new(
        "entity.pawn.flash_duration", "CCSPlayerPawn",
        SchemaNames.CCSPlayerPawnBase.FlashDuration, typeof(float));

    /// <summary>
    ///     entity.weapon.recoil_index: two-hop read (pawn's active-weapon handle, then
    ///     <c>m_flRecoilIndex</c> on the resolved weapon): the fractional index into the
    ///     weapon's recoil pattern, rising per shot and decaying between sprays. Fractional, so
    ///     it is the reliable spray-segmentation signal (a run in which it never drops is one
    ///     spray) where a per-shot count is not.
    ///     <para>
    ///         <b>The two-hop caveat, restated because it bites quietly.</b> Prime-time schema
    ///         validation judges hop 1 only. If the hop-2 field name drifts, every slot is
    ///         skipped and the column reads null, which looks like "this player had no weapon"
    ///         rather than an error. Confirmed present on <c>CCSWeaponBaseGun</c> by probe, but
    ///         that is a per-demo guarantee.
    ///     </para>
    ///     <para>
    ///         The constant lives under the SDK's <c>SchemaNames.CCSWeaponBase</c> group, which is
    ///         a naming convenience only: <c>CCSWeaponBase</c> is not a serializer class at all
    ///         (a schema probe reports it NOT FOUND). The class that carries these fields on the
    ///         wire is <c>CCSWeaponBaseGun</c>, and a spec naming <c>CCSWeaponBase</c> as its
    ///         entity class would not validate.
    ///     </para>
    /// </summary>
    public static ProviderSpec WeaponRecoilIndex { get; } = new(
        "entity.weapon.recoil_index", "CCSPlayerPawn",
        "", typeof(float),
        ViaHandleToField: new HandleFieldHop(
            SchemaNames.CBasePlayerPawn.WeaponServices + "." + SchemaNames.CPlayerWeaponServices.ActiveWeapon,
            SchemaNames.CCSWeaponBase.FlRecoilIndex));

    /// <summary>
    ///     entity.weapon.accuracy_penalty: two-hop read of the active weapon's accumulated
    ///     inaccuracy state (<c>m_fAccuracyPenalty</c>). This is the weapon's own decaying
    ///     penalty term and does NOT include the movement contribution, so a counter-strafe
    ///     judgement still needs speed against <see cref="PawnMaxSpeed" />; reading this alone
    ///     as "how inaccurate was the shot" understates a running player. Same hop-2 caveat as
    ///     <see cref="WeaponRecoilIndex" />.
    /// </summary>
    public static ProviderSpec WeaponAccuracyPenalty { get; } = new(
        "entity.weapon.accuracy_penalty", "CCSPlayerPawn",
        "", typeof(float),
        ViaHandleToField: new HandleFieldHop(
            SchemaNames.CBasePlayerPawn.WeaponServices + "." + SchemaNames.CPlayerWeaponServices.ActiveWeapon,
            SchemaNames.CCSWeaponBase.AccuracyPenalty));

    /// <summary>entity.game.freeze_period — the singleton freeze-period poll.</summary>
    public static ProviderSpec GameFreezePeriod { get; } = new(
        "entity.game.freeze_period", "CCSGameRulesProxy",
        SchemaNames.CCSGameRulesProxy.GameRules + "." + SchemaNames.CCSGameRules.FreezePeriod,
        typeof(bool));

    /// <summary>
    ///     The generic per-player providers equivalent to
    ///     <see cref="PerPlayerEntityValueProviderRegistry.CreateDefault" />.
    /// </summary>
    public static List<IPerPlayerEntityValueProvider> CreateGenericPerPlayerProviders() =>
    [
        new GenericPerPlayerFieldProvider(PawnHealth),
        new GenericPerPlayerFieldProvider(PawnActiveWeaponClass),
        new GenericPerPlayerFieldProvider(PawnEquipmentValue),
        new GenericPerPlayerFieldProvider(PawnArmor),
        // Registered generic-only on BOTH sides of the parity gate: CreateDefault() registers
        // this same spec-constructed provider (there is no hand-written twin), so the two
        // digest streams contain an identical fifth column by construction.
        new GenericPerPlayerFieldProvider(PawnActiveWeaponClip),
        // Same generic-only pattern (Tier C place): spec-constructed on both sides, at the same
        // position in both lists, digest parity by construction.
        new GenericPerPlayerFieldProvider(PawnPlace),
        // Aim-rating reads (same spec-constructed-on-both-sides pattern: no hand-written twin,
        // so digest parity holds by construction). Inserted BEFORE the position trio rather than
        // appended, because PawnPositionProviderTests pins those three as the last three.
        //
        // Every one of these changes far more often than the economy reads above, so each costs
        // the digest's delta encoding a full column. Gating is by name: a ruleset that reads none
        // of them pays nothing at all.
        new GenericPerPlayerFieldProvider(PawnDuckAmount),
        new GenericPerPlayerFieldProvider(PawnMaxSpeed),
        new GenericPerPlayerFieldProvider(PawnShotsFired),
        new GenericPerPlayerFieldProvider(PawnIsScoped),
        new GenericPerPlayerFieldProvider(PawnFlashDuration),
        new GenericPerPlayerFieldProvider(WeaponRecoilIndex),
        new GenericPerPlayerFieldProvider(WeaponAccuracyPenalty),
        // Angle components. QAngle has no scalar leaf to name and the rules language has no
        // vector type, so these are hand-written classes with no spec form, registered
        // identically on both sides of the parity gate exactly like the position trio.
        new PawnEyeAngleProvider(PawnAngleAxis.Pitch),
        new PawnEyeAngleProvider(PawnAngleAxis.Yaw),
        new PawnAimPunchProvider(PawnAngleAxis.Pitch),
        new PawnAimPunchProvider(PawnAngleAxis.Yaw),
        // Computed from six CBodyComponent leaves, so there is no spec form to migrate to. Both
        // lists register the same class, which makes these three pass-through columns in the
        // parity gate rather than a hand-written-vs-spec comparison. The gate's population is
        // the providers that HAVE a spec twin; position is judged instead by
        // PawnPositionProviderTests, which pins it against PositionUtil's oracle formula.
        new PawnPositionProvider(PawnPositionAxis.X),
        new PawnPositionProvider(PawnPositionAxis.Y),
        new PawnPositionProvider(PawnPositionAxis.Z)
    ];

    /// <summary>The generic singleton provider equivalent to <see cref="FreezePeriodProvider" />.</summary>
    public static IEntityValueProvider CreateGenericFreezePeriodProvider() =>
        new GenericSingletonFieldProvider(
            GameFreezePeriod,
            ChangeDirection.RisingOnly,
            typeof(CCSGameRulesFreezePeriodMarker),
            false);
}
