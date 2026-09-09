#region

using System.Numerics;
using CS2DemoKit.Analysis.Visibility;

#endregion

namespace CS2DemoKit.Analysis.Tests;

/// <summary>
///     Pins the ray budget counters to their contract: off by default and inert when off, and when
///     on they observe the visibility computation without changing it. The geometry is one wall
///     between two players so that both the occluded and the clear branches are exercised, and the
///     viewer looks away from the target so that the frustum-rejected tally is non-zero.
/// </summary>
[Category("Unit")]
[NotInParallel("VisibilityCounters")]
public class VisibilityCountersTests
{
    private static VisibilityEngine WallAtX(float x)
    {
        // Two triangles making a tall wall in the YZ plane at the given X.
        float[] v =
        [
            x, -1000f, -100f, x, 1000f, -100f, x, 1000f, 1000f,
            x, -1000f, -100f, x, 1000f, 1000f, x, -1000f, 1000f
        ];
        return VisibilityEngine.FromTriangles(v, 2);
    }

    private static VisibilityAnalyzer.Vantage Player(int slot, int team, float x, float yawDeg)
    {
        Vector3 feet = new(x, 0f, 0f);
        return new VisibilityAnalyzer.Vantage(
            slot, team, feet, PlayerVantage.Eye(feet, 0f), PlayerVantage.Forward(0f, yawDeg), true, 0f);
    }

    [Test]
    public async Task Counters_AreOffByDefault_AndInertWhenOff()
    {
        VisibilityCounters.Enabled = false;
        VisibilityCounters.Reset();

        VisibilityEngine open = VisibilityEngine.FromTriangles([], 0);
        VisibilityAnalyzer.Vantage a = Player(0, 2, 0f, 0f);
        VisibilityAnalyzer.Vantage b = Player(1, 3, 500f, 180f);
        _ = VisibilityAnalyzer.EvaluatePair(open, a, b, 53f, 37f);
        _ = VisibilityAnalyzer.EvaluatePair(open, b, a, 53f, 37f);

        VisibilityCountersSnapshot s = VisibilityCounters.Snapshot();
        await Assert.That(s.PairsEvaluated).IsEqualTo(0L);
        await Assert.That(s.RaysCast).IsEqualTo(0L);
        await Assert.That(s.SampledTicks).IsEqualTo(0L);
    }

    [Test]
    public async Task Counters_DoNotChangeResults_AndTallyWhatWasCast()
    {
        VisibilityEngine wall = WallAtX(250f);
        VisibilityEngine open = VisibilityEngine.FromTriangles([], 0);
        VisibilityAnalyzer.Vantage a = Player(0, 2, 0f, 0f); // looks +X, toward b
        VisibilityAnalyzer.Vantage b = Player(1, 3, 500f, 0f); // looks +X, away from a

        VisibilityCounters.Enabled = false;
        (bool, bool) offWallAb = VisibilityAnalyzer.EvaluatePair(wall, a, b, 53f, 37f);
        (bool, bool) offWallBa = VisibilityAnalyzer.EvaluatePair(wall, b, a, 53f, 37f);
        (bool, bool) offOpenAb = VisibilityAnalyzer.EvaluatePair(open, a, b, 53f, 37f);
        (bool, bool) offOpenBa = VisibilityAnalyzer.EvaluatePair(open, b, a, 53f, 37f);

        VisibilityCounters.Enabled = true;
        VisibilityCounters.Reset();
        try
        {
            (bool, bool) onWallAb = VisibilityAnalyzer.EvaluatePair(wall, a, b, 53f, 37f);
            (bool, bool) onWallBa = VisibilityAnalyzer.EvaluatePair(wall, b, a, 53f, 37f);
            (bool, bool) onOpenAb = VisibilityAnalyzer.EvaluatePair(open, a, b, 53f, 37f);
            (bool, bool) onOpenBa = VisibilityAnalyzer.EvaluatePair(open, b, a, 53f, 37f);

            await Assert.That(onWallAb).IsEqualTo(offWallAb);
            await Assert.That(onWallBa).IsEqualTo(offWallBa);
            await Assert.That(onOpenAb).IsEqualTo(offOpenAb);
            await Assert.That(onOpenBa).IsEqualTo(offOpenBa);

            // Sanity on the geometry itself, so the tallies below are about known branches.
            await Assert.That(offWallAb).IsEqualTo((false, false));
            await Assert.That(offOpenAb).IsEqualTo((true, true));
            await Assert.That(offOpenBa).IsEqualTo((true, false)); // exposed, but b looks away

            VisibilityCountersSnapshot s = VisibilityCounters.Snapshot();
            await Assert.That(s.PairsEvaluated).IsEqualTo(4L);
            await Assert.That(s.AnchorsTotal).IsEqualTo(4L * PlayerVantage.MaxAnchors);
            // b -> a twice: every anchor behind b's eye, so both are frustum-rejected pairs.
            await Assert.That(s.PairsNoAnchorInFrustum).IsEqualTo(2L);
            await Assert.That(s.PairsExposed).IsEqualTo(2L);
            await Assert.That(s.PairsCouldSee).IsEqualTo(1L);
            // Every anchor is either cast or skipped; nothing falls through the accounting.
            await Assert.That(s.RaysCast + s.RaysSkippedByEarlyExit).IsEqualTo(s.AnchorsTotal);
            // Through the wall all six anchors are cast per pair (no early exit); in the open the
            // first clear anchor decides a -> b, and b -> a casts one then skips the rest.
            await Assert.That(s.RaysCast).IsEqualTo(6L + 6L + 1L + 1L);
            await Assert.That(s.RaysClear).IsEqualTo(2L);
            await Assert.That(s.RaysCastOnFrustumRejectedPairs).IsEqualTo(6L + 1L);
            await Assert.That(s.RaysCastOutsideFrustum).IsEqualTo(6L + 1L);
            await Assert.That(s.RayTicks).IsGreaterThanOrEqualTo(0L);
        }
        finally
        {
            VisibilityCounters.Enabled = false;
            VisibilityCounters.Reset();
        }
    }

    [Test]
    public async Task Scanner_RecordsOneSamplePerAdmittedTick()
    {
        VisibilityEngine open = VisibilityEngine.FromTriangles([], 0);
        VisibilityTransitionScanner scanner = new(open, new VisibilityTransitionScanner.Options(SampleStrideTicks: 4));
        List<AimVantage> vantages =
        [
            new(Player(0, 2, 0f, 0f), 0f, 0f, 0f),
            new(Player(1, 3, 500f, 180f), 0f, 0f, 180f)
        ];

        VisibilityCounters.Enabled = true;
        VisibilityCounters.Reset();
        try
        {
            _ = scanner.Sample(100, 100, vantages);
            _ = scanner.Sample(102, 102, vantages); // inside the stride: no evaluation
            _ = scanner.Sample(104, 104, vantages);

            VisibilityCountersSnapshot s = VisibilityCounters.Snapshot();
            await Assert.That(s.SampledTicks).IsEqualTo(2L);
            await Assert.That(s.PairsEvaluated).IsEqualTo(4L);
            await Assert.That(s.SampleTicks).IsGreaterThanOrEqualTo(s.RayTicks);
        }
        finally
        {
            VisibilityCounters.Enabled = false;
            VisibilityCounters.Reset();
        }
    }
}
