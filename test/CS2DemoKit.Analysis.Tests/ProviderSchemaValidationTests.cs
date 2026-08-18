#region

using CS2OpenSchema;
using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Analysis.Nodes;
using CS2DemoKit.Analysis.Plugins;
using CS2DemoKit.Parser;
using CS2DemoKit.TestSupport;

#endregion

namespace CS2DemoKit.Analysis.Tests;

/// <summary>
///     Post-SendTables prime validation: once the demo's schema is available,
///     every registered provider's field path must EXIST on its class and its declared type
///     must be COMPATIBLE with the wire type — both failures throw. Before this, CS2 schema
///     drift was silent: the read path's coercion fallback turned a renamed or re-typed field
///     into eternal nulls/zeros. Covers both decode paths (the parallel-precompute probe layer
///     and the sequential per-frame hook). DEMO_PATH-gated.
/// </summary>
[Category("Unit")]
[NotInParallel]
public class ProviderSchemaValidationTests
{
    private static ParsedDemo ParseReference() => DemoTestHelper.GetOrParse(DemoTestHelper.RequireDemo());

    private static EntityChangeScanner Scanner(
        ParsedDemo parsed, List<IPerPlayerEntityValueProvider> perPlayer) => new(
        new EntityStateLayer(parsed.Frames),
        [
            (BuiltinProviderSpecs.CreateGenericFreezePeriodProvider(),
                new GenericBoolNode("entity.game.freeze_period"))
        ],
        perPlayer,
        false);

    /// <summary>
    ///     The five shipped specs validate clean against the reference demo's real schema —
    ///     this also empirically pins the wire-type compatibility map (int ↔ int32/uint16…,
    ///     bool ↔ bool, string ↔ CHandle for the weapon projection).
    /// </summary>
    [Test]
    public async Task ShippedSpecs_ValidateClean_OnParallelPath()
    {
        ParsedDemo parsed = ParseReference();
        EntityChangeScanner scanner = Scanner(parsed, BuiltinProviderSpecs.CreateGenericPerPlayerProviders());

        scanner.PrecomputeParallelDigests(parsed.Frames);

        await Assert.That(scanner.PrecomputedDigests).IsNotNull()
            .Because("clean validation must not disturb the precompute");
    }

    /// <summary>A misspelled field path on a seen class throws the missing-field drift error.</summary>
    [Test]
    public async Task MissingField_ThrowsLoudly_OnParallelPath()
    {
        ParsedDemo parsed = ParseReference();
        EntityChangeScanner scanner = Scanner(parsed,
        [
            new GenericPerPlayerFieldProvider(new ProviderSpec(
                "entity.pawn.bogus", "CCSPlayerPawn", "m_iHealht" /* typo */, typeof(int)))
        ]);

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => scanner.PrecomputeParallelDigests(parsed.Frames));
        await Assert.That(ex.Message).Contains("m_iHealht");
        await Assert.That(ex.Message).Contains("does not exist");
    }

    /// <summary>A wrong declared type throws the type-drift error (the loud arm).</summary>
    [Test]
    public async Task WrongDeclaredType_ThrowsLoudly_OnParallelPath()
    {
        ParsedDemo parsed = ParseReference();
        EntityChangeScanner scanner = Scanner(parsed,
        [
            new GenericPerPlayerFieldProvider(new ProviderSpec(
                "entity.pawn.health_as_bool", "CCSPlayerPawn",
                SchemaNames.CBaseEntity.Health, typeof(bool)))
        ]);

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => scanner.PrecomputeParallelDigests(parsed.Frames));
        await Assert.That(ex.Message).Contains("not");
        await Assert.That(ex.Message).Contains("compatible");
    }

    /// <summary>The sequential path validates too (first frames after the schema lands).</summary>
    [Test]
    public async Task MissingField_ThrowsLoudly_OnSequentialPath()
    {
        ParsedDemo parsed = ParseReference();
        EntityChangeScanner scanner = Scanner(parsed,
        [
            new GenericPerPlayerFieldProvider(new ProviderSpec(
                "entity.pawn.bogus", "CCSPlayerPawn", "m_iHealht", typeof(int)))
        ]);

        InvalidOperationException? thrown = null;
        try
        {
            // Drive the sequential per-frame path far enough for the first FullPacket to land
            // descriptors (a few hundred frames is ample).
            for (int f = 0; f < Math.Min(parsed.Frames.Count, 600); f++)
            {
                scanner.AdvanceAndPollAt(f, parsed.Frames[f].ServerTick);
            }
        }
        catch (InvalidOperationException ex)
        {
            thrown = ex;
        }

        await Assert.That(thrown).IsNotNull()
            .Because("the sequential hook must catch the drift once descriptors exist");
        await Assert.That(thrown!.Message).Contains("m_iHealht");
    }
}
