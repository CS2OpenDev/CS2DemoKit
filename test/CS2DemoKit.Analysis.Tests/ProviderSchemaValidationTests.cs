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
///     into eternal nulls/zeros. Covers both producers: the pipelined fold judges the schema on
///     its own worker's tracker and fails the chunk, the sequential path judges it on the
///     scanner's layer as the loop advances. DEMO_PATH-gated.
/// </summary>
[Category("Unit")]
[NotInParallel]
public class ProviderSchemaValidationTests
{
    // Far enough for the first full packet to land descriptors; a few hundred frames is ample.
    private const int FramesToDrive = 600;

    private static ParsedDemo ParseReference() => DemoTestHelper.GetOrParse(DemoTestHelper.RequireDemo());

    /// <summary>
    ///     Polls the first <paramref name="frames" /> frames through the pipelined producer, as the
    ///     evaluator would. The schema check runs on the fold worker, so drift fails the chunk and
    ///     surfaces from the first poll that takes a digest from it.
    /// </summary>
    private static int DrivePipelined(EntityChangeScanner scanner, ParsedDemo parsed, int frames)
    {
        IDemoFrameSource source = scanner.BeginEvaluation(parsed.AsFrameSource(), null, CancellationToken.None);
        try
        {
            if (scanner.ProducerKind != DigestProducerKind.Pipelined)
            {
                throw new InvalidOperationException("the scanner chose the sequential producer");
            }

            int polled = 0;
            while (polled < frames && source.TryReadNext(out DemoFrame? frame))
            {
                scanner.AdvanceAndPollAt(polled, frame.ServerTick);
                polled++;
            }

            return polled;
        }
        finally
        {
            scanner.EndEvaluation();
        }
    }

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
    public async Task ShippedSpecs_ValidateClean_OnPipelinedPath()
    {
        ParsedDemo parsed = ParseReference();
        EntityChangeScanner scanner = Scanner(parsed, BuiltinProviderSpecs.CreateGenericPerPlayerProviders());

        int polled = DrivePipelined(scanner, parsed, FramesToDrive);

        await Assert.That(polled).IsEqualTo(FramesToDrive)
            .Because("clean validation must not fail the chunk it ran on");
    }

    /// <summary>A misspelled field path on a seen class throws the missing-field drift error.</summary>
    [Test]
    public async Task MissingField_ThrowsLoudly_OnPipelinedPath()
    {
        ParsedDemo parsed = ParseReference();
        EntityChangeScanner scanner = Scanner(parsed,
        [
            new GenericPerPlayerFieldProvider(new ProviderSpec(
                "entity.pawn.bogus", "CCSPlayerPawn", "m_iHealht" /* typo */, typeof(int)))
        ]);

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => DrivePipelined(scanner, parsed, FramesToDrive));
        await Assert.That(ex.Message).Contains("m_iHealht");
        await Assert.That(ex.Message).Contains("does not exist");
    }

    /// <summary>A wrong declared type throws the type-drift error (the loud arm).</summary>
    [Test]
    public async Task WrongDeclaredType_ThrowsLoudly_OnPipelinedPath()
    {
        ParsedDemo parsed = ParseReference();
        EntityChangeScanner scanner = Scanner(parsed,
        [
            new GenericPerPlayerFieldProvider(new ProviderSpec(
                "entity.pawn.health_as_bool", "CCSPlayerPawn",
                SchemaNames.CBaseEntity.Health, typeof(bool)))
        ]);

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => DrivePipelined(scanner, parsed, FramesToDrive));
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
            foreach ((int index, DemoFrame _, IReadOnlyList<NetMessage> _) in SequentialScan.Frames(scanner, parsed))
            {
                if (index >= FramesToDrive)
                {
                    break;
                }
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
