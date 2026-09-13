#region

using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Analysis.Plugins;
using CS2DemoKit.Parser;
using CS2DemoKit.Parser.EntityTracking;
using CS2DemoKit.TestSupport;

#endregion

namespace CS2DemoKit.Analysis.Tests;

/// <summary>
///     <see cref="PawnAimPunchProvider" /> resolves the aim-punch field family once and remembers
///     it, because the probe walks the class's descriptors and the digest path would otherwise pay
///     that per pawn-frame. What it remembers has to be a fact about the TRACKER it was asked
///     about: <see cref="PawnAimPunchProvider.Read" /> is on the public
///     <c>IPerPlayerEntityValueProvider</c> contract and takes whatever layer the caller hands it,
///     so a registry can be pointed at a second demo of the other CS2 vintage and a layout carried
///     over from the first is not stale, it is wrong — every slot of the second demo reads null.
///     <para>
///         A <see cref="AimPunchLayout.None" /> result must not be remembered at all.
///         <see cref="AimPunchSchema.Resolve" /> answers from the tracker's descriptors, and a
///         tracker that has not decoded a pawn yet has none, so None early in a parse means "not
///         yet" rather than "not this demo" — which is exactly what
///         <see cref="AimPunchSchema.Resolve" />'s own doc warns callers about.
///     </para>
///     <para>
///         Both tests use a bare <see cref="EntityTracker" /> as the second vintage. It has no
///         schema and therefore no aim-punch family at all, which is the same shape of disagreement
///         as Services-vs-Flat and the only one reproducible without a second demo file.
///     </para>
/// </summary>
[Category("Unit")]
[NotInParallel]
public class PawnAimPunchProviderLatchTests
{
    [Test]
    public async Task AFailedProbe_IsNotLatched_SoALaterTrackerStillResolves()
    {
        (EntityStateLayer layer, EntityState pawn) = FirstPawnCarryingPunch();
        PawnAimPunchProvider provider = new(PawnAngleAxis.Pitch);

        await Assert.That(provider.ReadForPawnState(new EntityTracker(), pawn)).IsNull()
            .Because("a tracker with no descriptors networks no aim punch");

        await Assert.That(provider.ReadForPawnState(layer.Tracker, pawn)).IsNotNull()
            .Because("the failed probe must not have latched — the demo's own tracker does resolve");
    }

    [Test]
    public async Task TheLatchIsKeyedOnTheTracker_NotOnFirstUse()
    {
        (EntityStateLayer layer, EntityState pawn) = FirstPawnCarryingPunch();
        PawnAimPunchProvider provider = new(PawnAngleAxis.Pitch);

        await Assert.That(provider.ReadForPawnState(layer.Tracker, pawn)).IsNotNull();

        await Assert.That(provider.ReadForPawnState(new EntityTracker(), pawn)).IsNull()
            .Because("the layout describes the tracker it was probed on, so a different tracker re-probes");
    }

    [Test]
    public async Task TheSameTrackerKeepsAnsweringTheSameWay()
    {
        (EntityStateLayer layer, EntityState pawn) = FirstPawnCarryingPunch();
        PawnAimPunchProvider provider = new(PawnAngleAxis.Pitch);

        object? first = provider.ReadForPawnState(layer.Tracker, pawn);
        await Assert.That(provider.ReadForPawnState(layer.Tracker, pawn)).IsEqualTo(first);
        await Assert.That(provider.ReadForPawnState(layer.Tracker, pawn)).IsEqualTo(first);
    }

    /// <summary>
    ///     The first pawn of the sample demo that has actually networked a spring sample, with the
    ///     layer positioned on the frame it was found on. Walked forward rather than taken from the
    ///     first frame because a pawn that has not fired since spawning has no sample and would make
    ///     every assertion below vacuously null.
    /// </summary>
    private static (EntityStateLayer Layer, EntityState Pawn) FirstPawnCarryingPunch()
    {
        ParsedDemo demo = DemoTestHelper.GetOrParse(DemoTestHelper.RequireDemo(DemoTestHelper.SampleDemoFileName));
        IReadOnlyList<DemoFrame> frames = demo.Frames;
        EntityStateLayer layer = new(frames);
        PawnAimPunchProvider probe = new(PawnAngleAxis.Pitch);

        for (int n = 0; n < frames.Count; n += 50)
        {
            layer.SeekToTick(frames[n].ServerTick);
            EntityTracker tracker = layer.Tracker;
            EntityState? found = null;
            PawnLookup.ForEachLivePawn(tracker, (_, pawn) =>
            {
                if (found is null && probe.ReadForPawnState(tracker, pawn) is not null)
                {
                    found = pawn;
                }
            });

            if (found is not null)
            {
                return (layer, found);
            }
        }

        throw new InvalidOperationException(
            "No pawn on the sample demo networked an aim-punch base angle: the fixture the latch "
            + "tests read is gone, and a skip here would hide that.");
    }
}
