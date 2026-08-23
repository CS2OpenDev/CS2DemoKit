#region

using CS2DemoKit.Analysis.Plugins.Markers;
using SchemaNames = CS2OpenSchema.SchemaNames;

#endregion

namespace CS2DemoKit.Analysis.Plugins;

/// <summary>
///     The five shipped entity reads as <see cref="ProviderSpec" /> data: the
///     declarative equivalents of the hand-written provider classes, byte-identical by the
///     <c>ProviderDigestParityTests</c> gate. Once parity has baked, the default registries can
///     construct from these and the hand-written classes retire; until then both forms coexist
///     and the gate pins them together.
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
        // Same generic-only pattern (Tier C position/place): spec-constructed on both sides,
        // appended last in both lists, digest parity by construction.
        new GenericPerPlayerFieldProvider(PawnPlace),
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
