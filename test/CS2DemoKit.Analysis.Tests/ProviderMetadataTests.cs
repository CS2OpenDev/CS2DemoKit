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
