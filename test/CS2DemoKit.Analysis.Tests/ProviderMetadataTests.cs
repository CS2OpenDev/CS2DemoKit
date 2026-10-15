#region

using CS2DemoKit.Analysis.Plugins;
using CS2DemoKit.Analysis.Plugins.Markers;
using SchemaNames = CS2OpenSchema.SchemaNames;

#endregion

namespace CS2DemoKit.Analysis.Tests;

/// <summary>
///     Provider metadata assertions — every entity-value provider exposes
///     stable identity fields (name, entity class, field name, value type,
///     marker type, emit direction, default value). These tests construct
///     a provider in microseconds, assert on string/type constants, and
///     touch no demo file. Deliberately separated from
///     <see cref="EntityIntegrationTests" /> (which is <c>[NotInParallel]</c>
///     because the demo-driven cases are memory-heavy) so these run in
///     parallel with anything.
/// </summary>
[Category("Unit")]
public class ProviderMetadataTests
{
    /// <summary>Active weapon provider_exposes expected metadata.</summary>
    [Test]
    public async Task ActiveWeaponProvider_ExposesExpectedMetadata()
    {
        ActiveWeaponProvider provider = new();
        await Assert.That(provider.Name).IsEqualTo("entity.pawn.active_weapon_class");
        await Assert.That(provider.EntityClass).IsEqualTo("CCSPlayerPawn");
        // Field is on sub-entity CPlayerWeaponServices, flattened under dotted parent path.
        await Assert.That(provider.FieldName).IsEqualTo("m_pWeaponServices.m_hActiveWeapon");
        await Assert.That(provider.ValueType).IsEqualTo(typeof(string));
    }

    /// <summary>Freeze period provider_exposes expected metadata.</summary>
    [Test]
    public async Task FreezePeriodProvider_ExposesExpectedMetadata()
    {
        FreezePeriodProvider provider = new();
        await Assert.That(provider.ContextName).IsEqualTo("entity.game.freeze_period");
        await Assert.That(provider.EntityClass).IsEqualTo("CCSGameRulesProxy");
        await Assert.That(provider.FieldName).IsEqualTo(
            SchemaNames.CCSGameRulesProxy.GameRules + "." + SchemaNames.CCSGameRules.FreezePeriod);
        await Assert.That(provider.ValueType).IsEqualTo(typeof(bool));
        await Assert.That(provider.MarkerType).IsEqualTo(typeof(CCSGameRulesFreezePeriodMarker));
        await Assert.That(provider.EmitOn).IsEqualTo(ChangeDirection.RisingOnly);
        await Assert.That(provider.DefaultValue).IsEqualTo(false);
    }

    /// <summary>
    ///     The per-side team reads: one instance per spec and side, named <c>{spec}@{side}</c>, each
    ///     with its own marker, reading <c>m_iScore</c> / <c>m_szClanTeamname</c> off <c>CCSTeam</c>.
    /// </summary>
    [Test]
    public async Task TeamSideProviders_ExposeExpectedMetadata()
    {
        List<GenericSideFieldProvider> providers =
            BuiltinProviderSpecs.CreateTeamProviders().Cast<GenericSideFieldProvider>().ToList();
        await Assert.That(string.Join(",", providers.Select(p => p.ContextName))).IsEqualTo(
            "entity.team.score@2,entity.team.score@3,entity.team.clan_name@2,entity.team.clan_name@3");
        await Assert.That(providers.Select(p => p.MarkerType).Distinct().Count()).IsEqualTo(4);
        foreach (GenericSideFieldProvider provider in providers)
        {
            await Assert.That(provider.EntityClass).IsEqualTo("CCSTeam");
            await Assert.That(provider.EmitOn).IsEqualTo(ChangeDirection.Both);
            await Assert.That(provider.DefaultValue).IsNull();
            await Assert.That(provider.Side is 2 or 3).IsTrue();
            await Assert.That(((GenericSideFieldProvider)((IWorkerCloneable<IEntityValueProvider>)provider).CloneForWorker()).Side)
                .IsEqualTo(provider.Side);
        }

        await Assert.That(providers[0].FieldName).IsEqualTo("m_iScore");
        await Assert.That(providers[0].ValueType).IsEqualTo(typeof(int));
        await Assert.That(providers[2].FieldName).IsEqualTo("m_szClanTeamname");
        await Assert.That(providers[2].ValueType).IsEqualTo(typeof(string));
    }

    /// <summary>Pawn health provider_exposes expected metadata.</summary>
    [Test]
    public async Task PawnHealthProvider_ExposesExpectedMetadata()
    {
        PawnHealthProvider provider = new();
        await Assert.That(provider.Name).IsEqualTo("entity.pawn.health");
        await Assert.That(provider.EntityClass).IsEqualTo("CCSPlayerPawn");
        await Assert.That(provider.FieldName).IsEqualTo(SchemaNames.CBaseEntity.Health);
        await Assert.That(provider.ValueType).IsEqualTo(typeof(int));
    }

    /// <summary>
    ///     Active-weapon clip provider (Tier C, spec-constructed) exposes expected metadata.
    ///     FieldName surfaces hop 1 — the pawn's handle path, mirroring
    ///     <see cref="ActiveWeaponProvider" /> — while the <c>m_iClip1</c> read is the
    ///     internal hop 2 (<see cref="ProviderSpec.ViaHandleToField" />).
    /// </summary>
    [Test]
    public async Task ActiveWeaponClipProvider_ExposesExpectedMetadata()
    {
        GenericPerPlayerFieldProvider provider = new(BuiltinProviderSpecs.PawnActiveWeaponClip);
        await Assert.That(provider.Name).IsEqualTo("entity.pawn.active_weapon_clip");
        await Assert.That(provider.EntityClass).IsEqualTo("CCSPlayerPawn");
        await Assert.That(provider.FieldName).IsEqualTo("m_pWeaponServices.m_hActiveWeapon");
        await Assert.That(provider.ValueType).IsEqualTo(typeof(int));
        await Assert.That(provider.Spec.ViaHandleToField!.TargetField).IsEqualTo("m_iClip1");
    }

    /// <summary>
    ///     Place provider (Tier C, spec-constructed) exposes expected metadata: a plain string
    ///     read of the pawn's <c>m_szLastPlaceName</c> (nav-mesh place, e.g. <c>BombsiteA</c>) —
    ///     no handle hop, no emit gates (null when unseen; empty string is a real observation).
    /// </summary>
    [Test]
    public async Task PawnPlaceProvider_ExposesExpectedMetadata()
    {
        GenericPerPlayerFieldProvider provider = new(BuiltinProviderSpecs.PawnPlace);
        await Assert.That(provider.Name).IsEqualTo("entity.pawn.place");
        await Assert.That(provider.EntityClass).IsEqualTo("CCSPlayerPawn");
        await Assert.That(provider.FieldName).IsEqualTo(SchemaNames.CCSPlayerPawn.LastPlaceName);
        await Assert.That(provider.FieldName).IsEqualTo("m_szLastPlaceName");
        await Assert.That(provider.ValueType).IsEqualTo(typeof(string));
        await Assert.That(provider.Spec.PositiveOnly).IsFalse();
        await Assert.That(provider.Spec.UnseenAsDefault).IsFalse();
        await Assert.That(provider.Spec.ViaHandleToClassName).IsNull();
        await Assert.That(provider.Spec.ViaHandleToField).IsNull();
    }

    /// <summary>
    ///     Walking provider (spec-constructed, no hand-written twin) exposes expected metadata: a
    ///     plain bool read of <c>m_bIsWalking</c>, no handle hop, no emit gates.
    /// </summary>
    [Test]
    public async Task PawnIsWalkingProvider_ExposesExpectedMetadata()
    {
        GenericPerPlayerFieldProvider provider = new(BuiltinProviderSpecs.PawnIsWalking);
        await Assert.That(provider.Name).IsEqualTo("entity.pawn.is_walking");
        await Assert.That(provider.EntityClass).IsEqualTo("CCSPlayerPawn");
        await Assert.That(provider.FieldName).IsEqualTo(SchemaNames.CCSPlayerPawn.IsWalking);
        await Assert.That(provider.FieldName).IsEqualTo("m_bIsWalking");
        await Assert.That(provider.ValueType).IsEqualTo(typeof(bool));
        await Assert.That(provider.Spec.PositiveOnly).IsFalse();
        await Assert.That(provider.Spec.UnseenAsDefault).IsFalse();
        await Assert.That(provider.Spec.ViaHandleToClassName).IsNull();
        await Assert.That(provider.Spec.ViaHandleToField).IsNull();
    }

    /// <summary>The raw flags column: a plain int read of <c>m_fFlags</c>, no hop, no gates.</summary>
    [Test]
    public async Task PawnFlagsProvider_ExposesExpectedMetadata()
    {
        GenericPerPlayerFieldProvider provider = new(BuiltinProviderSpecs.PawnFlags);
        await Assert.That(provider.Name).IsEqualTo("entity.pawn.flags");
        await Assert.That(provider.EntityClass).IsEqualTo("CCSPlayerPawn");
        await Assert.That(provider.FieldName).IsEqualTo("m_fFlags");
        await Assert.That(provider.ValueType).IsEqualTo(typeof(int));
        await Assert.That(provider.Spec.PositiveOnly).IsFalse();
        await Assert.That(provider.Spec.UnseenAsDefault).IsFalse();
        await Assert.That(provider.Spec.ViaHandleToClassName).IsNull();
        await Assert.That(provider.Spec.ViaHandleToField).IsNull();
    }

    /// <summary>The two flag bits: bool columns over <c>m_fFlags</c>, cloned per worker with their mask.</summary>
    [Test]
    public async Task PawnFlagBitProviders_ExposeExpectedMetadata()
    {
        PawnFlagBitProvider onGround = PawnFlagBitProvider.OnGround();
        PawnFlagBitProvider ducking = PawnFlagBitProvider.Ducking();
        await Assert.That(onGround.Name).IsEqualTo("entity.pawn.on_ground");
        await Assert.That(onGround.Mask).IsEqualTo(1);
        await Assert.That(ducking.Name).IsEqualTo("entity.pawn.ducking");
        await Assert.That(ducking.Mask).IsEqualTo(2);
        foreach (PawnFlagBitProvider provider in new[] { onGround, ducking })
        {
            await Assert.That(provider.EntityClass).IsEqualTo("CCSPlayerPawn");
            await Assert.That(provider.FieldName).IsEqualTo("m_fFlags");
            await Assert.That(provider.ValueType).IsEqualTo(typeof(bool));
            IPerPlayerEntityValueProvider clone = provider.CloneForWorker();
            await Assert.That(clone).IsNotSameReferenceAs(provider);
            await Assert.That(clone.Name).IsEqualTo(provider.Name);
            await Assert.That(((PawnFlagBitProvider)clone).Mask).IsEqualTo(provider.Mask);
        }
    }

    /// <summary>Throw strength: a float two-hop read through the active-weapon handle to <c>m_flThrowStrength</c>.</summary>
    [Test]
    public async Task WeaponThrowStrengthProvider_ExposesExpectedMetadata()
    {
        GenericPerPlayerFieldProvider provider = new(BuiltinProviderSpecs.WeaponThrowStrength);
        await Assert.That(provider.Name).IsEqualTo("entity.weapon.throw_strength");
        await Assert.That(provider.EntityClass).IsEqualTo("CCSPlayerPawn");
        await Assert.That(provider.FieldName).IsEqualTo("m_pWeaponServices.m_hActiveWeapon");
        await Assert.That(provider.ValueType).IsEqualTo(typeof(float));
        await Assert.That(provider.Spec.ViaHandleToField!.TargetField).IsEqualTo("m_flThrowStrength");
        await Assert.That(provider.Spec.PositiveOnly).IsFalse();
        await Assert.That(provider.Spec.UnseenAsDefault).IsFalse();
    }

    /// <summary>Coaching team: an int two-hop read through the pawn's controller handle to <c>m_iCoachingTeam</c>.</summary>
    [Test]
    public async Task ControllerCoachingTeamProvider_ExposesExpectedMetadata()
    {
        GenericPerPlayerFieldProvider provider = new(BuiltinProviderSpecs.ControllerCoachingTeam);
        await Assert.That(provider.Name).IsEqualTo("entity.controller.coaching_team");
        await Assert.That(provider.EntityClass).IsEqualTo("CCSPlayerPawn");
        await Assert.That(provider.FieldName).IsEqualTo("m_hController");
        await Assert.That(provider.ValueType).IsEqualTo(typeof(int));
        await Assert.That(provider.Spec.ViaHandleToField!.TargetField).IsEqualTo("m_iCoachingTeam");
        await Assert.That(provider.Spec.PositiveOnly).IsFalse();
        await Assert.That(provider.Spec.UnseenAsDefault).IsFalse();
    }

    /// <summary>
    ///     The parity gate's structural precondition (<c>ProviderDigestParityTests</c> compares
    ///     value arrays index-by-index): the hand-written default registry and the generic spec
    ///     list must carry the SAME providers in the SAME order. Guarded here demo-free so a
    ///     one-sided registration fails fast, not only on the demo-gated digest run.
    /// </summary>
    [Test]
    public async Task DefaultRegistry_MatchesGenericSpecList_NameForName()
    {
        string defaults = string.Join(
            ",", PerPlayerEntityValueProviderRegistry.CreateDefault().All.Select(p => p.Name));
        string generic = string.Join(
            ",", BuiltinProviderSpecs.CreateGenericPerPlayerProviders().Select(p => p.Name));
        await Assert.That(generic).IsEqualTo(defaults);
    }

    /// <summary>A spec declaring both handle-follow modes is rejected at construction.</summary>
    [Test]
    public async Task GenericProvider_RejectsAmbiguousHandleSpec()
    {
        ProviderSpec bad = new(
            "entity.pawn.bad", "CCSPlayerPawn", "", typeof(int),
            ViaHandleToClassName: "m_hActiveWeapon",
            ViaHandleToField: new HandleFieldHop("m_hActiveWeapon", "m_iClip1"));
        ArgumentException ex = Assert.Throws<ArgumentException>(
            () => _ = new GenericPerPlayerFieldProvider(bad));
        await Assert.That(ex.Message).Contains("mutually");
    }
}
