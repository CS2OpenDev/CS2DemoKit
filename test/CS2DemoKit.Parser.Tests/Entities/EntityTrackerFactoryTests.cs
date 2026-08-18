#region

using CS2OpenDev.Sdk.Entities;
using CS2DemoKit.Parser;
using CS2DemoKit.Parser.Entities.Generated;
using CS2DemoKit.Parser.Entities.SchemaLens;
using CS2DemoKit.Parser.Entities.SdkAbstractions;
using CS2DemoKit.Parser.EntityTracking;
using CS2DemoKit.TestSupport;
using TUnit.Core.Exceptions;

#endregion

namespace CS2DemoKit.Parser.Entities.Tests;

/// <summary>
///     Covers <see cref="EntityTrackerFactory.CreateCurated" /> and the tripwire that
///     catches the bootstrap mistake it exists to make unnecessary: registered wrapper
///     factories with no Schema Lens resolver bound decode without complaint and then hand
///     back wrapper values read off the wrong lane. Since the SDK cutover the factory only
///     binds the lens (the local wrapper registry is retired); the equivalence test asserts
///     CreateCurated reproduces the hand lens-bind exactly, measured through SDK-wrapper
///     reads over both trackers.
///     <para>
///         <see cref="NotInParallelAttribute" /> follows the parser-test memory pressure guidance:
///         these replay a real demo through two trackers at once.
///     </para>
/// </summary>
[Category("Integration")]
[NotInParallel]
public class EntityTrackerFactoryTests
{
    /// <summary>
    ///     A tracker from <see cref="EntityTrackerFactory.CreateCurated" /> and one wired by the
    ///     hand bootstrap (bind lens yourself) serve identical SDK-wrapper reads for the same
    ///     entities after the same replay — plain int, a raw handle, and a seen-aware int?
    ///     spanning the getter shapes a dropped lens bind would corrupt.
    /// </summary>
    [Test]
    [Category("Smoke")]
    public async Task CreateCurated_WrapperValues_MatchManualBootstrap()
    {
        string? demoPath = DemoTestHelper.FindDemoPath();
        if (demoPath is null)
        {
            throw new SkipTestException("No demo found — skipping CreateCurated equivalence test.");
        }

        ParsedDemo parsed = DemoTestHelper.GetOrParse(demoPath);

        EntityTracker manual = new();
        manual.BindLensResolver(LensResolverBridge.Build(GeneratedLensRegistry.Load()));

        EntityTracker curated = EntityTrackerFactory.CreateCurated();

        // Half the demo is plenty for every curated class to be live, and keeps two concurrent
        // trackers' combined decode at roughly one full replay.
        int target = parsed.Frames.Count / 2;
        manual.AdvanceToIndex(target, parsed.Frames);
        curated.AdvanceToIndex(target, parsed.Frames);

        int slot = FirstSlotOfClass(manual, "CCSPlayerPawn");
        if (slot < 0)
        {
            throw new SkipTestException("No CCSPlayerPawn populated at mid-demo — skipping.");
        }

        EntityClassBinding pawnBinding = EntityWrapperRegistry.Bindings
            .Single(b => b.EngineClass == "CCSPlayerPawn");
        CSPlayerPawn manualPawn = (CSPlayerPawn)EntityWrapperRegistry.Create("CCSPlayerPawn",
            new TrackerEntityWorld(manual).CreateReader(pawnBinding, manual.CurrentEntities[slot]!),
            new TrackerEntityWorld(manual))!;
        CSPlayerPawn curatedPawn = (CSPlayerPawn)EntityWrapperRegistry.Create("CCSPlayerPawn",
            new TrackerEntityWorld(curated).CreateReader(pawnBinding, curated.CurrentEntities[slot]!),
            new TrackerEntityWorld(curated))!;

        await Assert.That(curatedPawn.Health).IsEqualTo(manualPawn.Health);
        await Assert.That(curatedPawn.TeamNum).IsEqualTo(manualPawn.TeamNum);
        await Assert.That(curatedPawn.ControllerHandle).IsEqualTo(manualPawn.ControllerHandle);
        await Assert.That(curatedPawn.LifeState).IsEqualTo(manualPawn.LifeState);
    }

    /// <summary>
    ///     The bootstrap tripwire fires: factories registered with no lens resolver bound produces one
    ///     diagnostic line at the first packet-entities decode, naming the fix. The trigger is now
    ///     the SDK registration path (<see cref="TrackerEntityWorld.RegisterWrapper" /> routes
    ///     through the same <see cref="EntityTracker.RegisterEntityFactory" /> the tripwire
    ///     watches).
    /// </summary>
    [Test]
    public async Task WrappersWithoutLens_EmitsBootstrapWarning_OnFirstDecode()
    {
        string? demoPath = DemoTestHelper.FindDemoPath();
        if (demoPath is null)
        {
            throw new SkipTestException("No demo found — skipping bootstrap-warning test.");
        }

        ParsedDemo parsed = DemoTestHelper.GetOrParse(demoPath);

        List<string> log = [];
        EntityTracker tracker = new() { DecodeDiagnosticSink = log.Add };
        // The wrong bootstrap: SDK factories registered, no BindLensResolver.
        TrackerEntityWorld world = new(tracker);
        world.RegisterWrapper(
            EntityWrapperRegistry.Bindings.Single(b => b.EngineClass == "CCSPlayerPawn"),
            (r, w) => EntityWrapperRegistry.Create("CCSPlayerPawn", r, w)!);
        AdvanceToFirstPacket(tracker, parsed);

        await Assert.That(tracker.PacketCount).IsGreaterThan(0)
            .Because("the warning is checked at packet-entities decode, so a packet must have run");
        await Assert.That(log.Count).IsEqualTo(1);
        await Assert.That(log[0]).Contains("no Schema Lens resolver is bound");
        await Assert.That(log[0]).Contains("CreateCurated");
    }

    /// <summary>
    ///     A correctly-bootstrapped tracker stays silent, and so does the plain dict-only tracker —
    ///     no factories, no lens, which is a legitimate way to use <see cref="EntityTracker" /> and
    ///     must not be nagged about wrappers it never asked for.
    /// </summary>
    [Test]
    public async Task CuratedAndDictOnlyTrackers_EmitNoBootstrapWarning()
    {
        string? demoPath = DemoTestHelper.FindDemoPath();
        if (demoPath is null)
        {
            throw new SkipTestException("No demo found — skipping bootstrap-silence test.");
        }

        ParsedDemo parsed = DemoTestHelper.GetOrParse(demoPath);

        List<string> curatedLog = [];
        EntityTracker curated = EntityTrackerFactory.CreateCurated();
        curated.DecodeDiagnosticSink = curatedLog.Add;
        AdvanceToFirstPacket(curated, parsed);

        List<string> plainLog = [];
        EntityTracker plain = new() { DecodeDiagnosticSink = plainLog.Add };
        AdvanceToFirstPacket(plain, parsed);

        await Assert.That(curatedLog).IsEmpty();
        await Assert.That(plainLog).IsEmpty();
        await Assert.That(curated.PacketCount).IsGreaterThan(0);
        await Assert.That(plain.PacketCount).IsGreaterThan(0);
    }

    /// <summary>
    ///     Steps frames until the tracker has decoded its first <c>svc_PacketEntities</c> — the
    ///     point the bootstrap tripwire is checked. Far cheaper than a full replay.
    /// </summary>
    private static void AdvanceToFirstPacket(EntityTracker tracker, ParsedDemo parsed)
    {
        foreach (DemoFrame frame in parsed.Frames)
        {
            tracker.AdvanceOneFrame(frame);
            if (tracker.PacketCount > 0)
            {
                return;
            }
        }
    }

    /// <summary>First populated slot holding an entity of <paramref name="className" />, or -1.</summary>
    private static int FirstSlotOfClass(EntityTracker tracker, string className)
    {
        foreach ((int index, EntityState entity) in tracker.CurrentEntities.AllIndexed())
        {
            if (string.Equals(entity.ClassName, className, StringComparison.Ordinal))
            {
                return index;
            }
        }

        return -1;
    }
}
