#region

using System.Numerics;
using CS2DemoKit.Analysis.Visibility;
using TUnit.Core.Exceptions;
using CS2DemoKit.Parser;

#endregion

namespace CS2DemoKit.Analysis.Tests;

/// <summary>
///     Pins the ray budget counters to their contract: off by default and inert when off, and when
///     on they observe the visibility computation without changing it. The geometry is one wall
///     between two players so that both the occluded and the clear branches are exercised, and the
///     viewer looks away from the target so that the frustum-rejected tally is non-zero.
/// </summary>
[Category("Unit")]
[NotInParallel] // process-wide counters: any pair loop running alongside would land in the tallies
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
            // Every anchor is cast, gate-skipped or early-exit-skipped, exactly once; nothing falls
            // through the accounting.
            await Assert.That(s.RaysCast + s.RaysSkippedByGate + s.RaysSkippedByEarlyExit).IsEqualTo(s.AnchorsTotal);
            // Through the wall all six anchors are cast per pair (no early exit); in the open the
            // first clear anchor decides a -> b and the tail is an early exit, and b -> a casts one
            // out-of-frustum anchor to learn exposed, then the frustum gate refuses the other five.
            await Assert.That(s.RaysCast).IsEqualTo(6L + 6L + 1L + 1L);
            await Assert.That(s.RaysSkippedByEarlyExit).IsEqualTo(5L);
            await Assert.That(s.RaysSkippedByGate).IsEqualTo(5L);
            await Assert.That(s.RaysSkippedBySmoke).IsEqualTo(0L);
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

    /// <summary>
    ///     The frustum and smoke gates on both entry points, pinned anchor by anchor. The only
    ///     difference between <c>EvaluatePair</c> and <c>CouldSee</c> is whether a ray that can
    ///     only decide <c>exposed</c> is cast, so the same six pair evaluations are run through both
    ///     and the tallies are asserted against what each mode is allowed to skip. The accounting
    ///     identity is asserted per mode as well as in total.
    /// </summary>
    [Test]
    public async Task Gates_SkipOnlyRaysThatCannotChangeTheAnswer_InBothModes()
    {
        VisibilityEngine open = VisibilityEngine.FromTriangles([], 0);
        VisibilityAnalyzer.Vantage a = Player(0, 2, 0f, 0f); // looks +X, toward b
        VisibilityAnalyzer.Vantage b = Player(1, 3, 500f, 0f); // looks +X, away from a
        // A cloud on the midpoint of every a -> b sightline: all six of b's anchors are smoked.
        Vector4[] smoke = [new Vector4(250f, 0f, 48f, SmokeVolumes.DefaultRadius)];

        VisibilityCounters.Enabled = true;
        VisibilityCounters.Reset();
        try
        {
            // 1. Exposed wanted, every anchor smoked: one ray learns exposed, the smoke gate then
            //    refuses the other five because they can no longer change anything.
            (bool, bool) smokedPair = VisibilityAnalyzer.EvaluatePair(open, a, b, 53f, 37f, smoke);
            VisibilityCountersSnapshot s1 = VisibilityCounters.Snapshot();
            await Assert.That(smokedPair).IsEqualTo((true, false));
            await Assert.That(s1.RaysCast).IsEqualTo(1L);
            await Assert.That(s1.RaysSkippedByGate).IsEqualTo(5L);
            await Assert.That(s1.RaysSkippedBySmoke).IsEqualTo(5L);
            await Assert.That(s1.RaysSkippedByEarlyExit).IsEqualTo(0L);

            // 2. Could-see only, every anchor smoked: nothing is cast at all.
            VisibilityCounters.Reset();
            bool smokedCouldSee = VisibilityAnalyzer.CouldSee(open, a, b, 53f, 37f, smoke);
            VisibilityCountersSnapshot s2 = VisibilityCounters.Snapshot();
            await Assert.That(smokedCouldSee).IsFalse();
            await Assert.That(s2.RaysCast).IsEqualTo(0L);
            await Assert.That(s2.RaysSkippedByGate).IsEqualTo(6L);
            await Assert.That(s2.RaysSkippedBySmoke).IsEqualTo(6L);
            await Assert.That(s2.PairsExposed).IsEqualTo(0L).Because("exposed is not computed on this path");

            // 3. Exposed wanted, target behind the viewer: one out-of-frustum ray learns exposed,
            //    the frustum gate refuses the rest.
            VisibilityCounters.Reset();
            (bool, bool) behindPair = VisibilityAnalyzer.EvaluatePair(open, b, a, 53f, 37f);
            VisibilityCountersSnapshot s3 = VisibilityCounters.Snapshot();
            await Assert.That(behindPair).IsEqualTo((true, false));
            await Assert.That(s3.RaysCast).IsEqualTo(1L);
            await Assert.That(s3.RaysCastOutsideFrustum).IsEqualTo(1L);
            await Assert.That(s3.RaysSkippedByGate).IsEqualTo(5L);
            await Assert.That(s3.RaysSkippedBySmoke).IsEqualTo(0L);

            // 4. Could-see only, target behind the viewer: nothing is cast.
            VisibilityCounters.Reset();
            bool behindCouldSee = VisibilityAnalyzer.CouldSee(open, b, a, 53f, 37f, ReadOnlySpan<Vector4>.Empty);
            VisibilityCountersSnapshot s4 = VisibilityCounters.Snapshot();
            await Assert.That(behindCouldSee).IsFalse();
            await Assert.That(s4.RaysCast).IsEqualTo(0L);
            await Assert.That(s4.RaysCastOutsideFrustum).IsEqualTo(0L);
            await Assert.That(s4.RaysSkippedByGate).IsEqualTo(6L);
            await Assert.That(s4.PairsNoAnchorInFrustum).IsEqualTo(1L);

            // 5 and 6. In frustum, clear, unsmoked: both modes cast one ray and early-exit the tail.
            VisibilityCounters.Reset();
            (bool, bool) openPair = VisibilityAnalyzer.EvaluatePair(open, a, b, 53f, 37f);
            bool openCouldSee = VisibilityAnalyzer.CouldSee(open, a, b, 53f, 37f, ReadOnlySpan<Vector4>.Empty);
            VisibilityCountersSnapshot s5 = VisibilityCounters.Snapshot();
            await Assert.That(openPair).IsEqualTo((true, true));
            await Assert.That(openCouldSee).IsTrue();
            await Assert.That(s5.RaysCast).IsEqualTo(2L);
            await Assert.That(s5.RaysSkippedByEarlyExit).IsEqualTo(10L);
            await Assert.That(s5.RaysSkippedByGate).IsEqualTo(0L);
            await Assert.That(s5.PairsCouldSee).IsEqualTo(2L);

            // The accounting identity holds in every one of the snapshots above.
            foreach (VisibilityCountersSnapshot s in new[] { s1, s2, s3, s4, s5 })
            {
                await Assert.That(s.RaysCast + s.RaysSkippedByGate + s.RaysSkippedByEarlyExit).IsEqualTo(s.AnchorsTotal);
            }
        }
        finally
        {
            VisibilityCounters.Enabled = false;
            VisibilityCounters.Reset();
        }
    }

    /// <summary>
    ///     The last-occluder hint on the pair loop: the first evaluation of an occluded pair casts
    ///     every ray through the BVH and writes the hints, the second is decided by them ray for
    ///     ray, and neither the verdict nor any other tally moves. A clear ray is never
    ///     short-circuited, whatever the hint says.
    ///     <para>
    ///         This pins the plumbing: a hint row that was not passed through, or a tally that was
    ///         dropped, fails it. The hints it hands in are always right, so it says nothing about
    ///         a wrong hint and passes under any mutation of the hinted test that keeps the counter
    ///         wired. The verdict proofs are <c>OccluderHintTests</c> and
    ///         <c>OccluderHintDifferentialTests</c>.
    ///     </para>
    /// </summary>
    [Test]
    public async Task Hints_ShortCircuitRepeatedOccludedRays_AndChangeNoVerdict()
    {
        VisibilityEngine wall = WallAtX(250f);
        VisibilityEngine open = VisibilityEngine.FromTriangles([], 0);
        VisibilityAnalyzer.Vantage a = Player(0, 2, 0f, 0f); // looks +X, toward b
        VisibilityAnalyzer.Vantage b = Player(1, 3, 500f, 0f);
        int[] hints = new int[PlayerVantage.MaxAnchors];
        int[] pairHints = new int[PlayerVantage.MaxAnchors];
        int[] openHints = new int[PlayerVantage.MaxAnchors];
        Array.Fill(hints, -1);
        Array.Fill(pairHints, -1);
        Array.Fill(openHints, -1);

        VisibilityCounters.Enabled = true;
        VisibilityCounters.Reset();
        try
        {
            // 1. First look through the wall: six traversals, six hints written.
            bool first = VisibilityAnalyzer.CouldSee(wall, a, b, 53f, 37f, ReadOnlySpan<Vector4>.Empty, hints);
            VisibilityCountersSnapshot s1 = VisibilityCounters.Snapshot();
            await Assert.That(first).IsFalse();
            await Assert.That(s1.RaysCast).IsEqualTo(6L);
            await Assert.That(s1.RaysShortCircuited).IsEqualTo(0L).Because("nothing was hinted yet");
            await Assert.That(hints.All(h => h >= 0)).IsTrue().Because("every occluded ray writes its blocker back");

            // 2. Second look: every ray decided by its hint, same verdict, same tallies otherwise.
            VisibilityCounters.Reset();
            bool second = VisibilityAnalyzer.CouldSee(wall, a, b, 53f, 37f, ReadOnlySpan<Vector4>.Empty, hints);
            VisibilityCountersSnapshot s2 = VisibilityCounters.Snapshot();
            await Assert.That(second).IsFalse();
            await Assert.That(s2.RaysCast).IsEqualTo(6L).Because("a short-circuited ray is still a cast ray");
            await Assert.That(s2.RaysShortCircuited).IsEqualTo(6L);
            await Assert.That(s2.RaysClear).IsEqualTo(0L);
            await Assert.That(s2.RaysSkippedByGate).IsEqualTo(s1.RaysSkippedByGate);
            await Assert.That(s2.RaysSkippedByEarlyExit).IsEqualTo(s1.RaysSkippedByEarlyExit);

            // 3. The exposed-wanting entry point through the same wall behaves the same way.
            VisibilityCounters.Reset();
            (bool, bool) firstPair = VisibilityAnalyzer.EvaluatePair(wall, a, b, 53f, 37f, ReadOnlySpan<Vector4>.Empty, pairHints);
            VisibilityCountersSnapshot s3 = VisibilityCounters.Snapshot();
            VisibilityCounters.Reset();
            (bool, bool) secondPair = VisibilityAnalyzer.EvaluatePair(wall, a, b, 53f, 37f, ReadOnlySpan<Vector4>.Empty, pairHints);
            VisibilityCountersSnapshot s4 = VisibilityCounters.Snapshot();
            await Assert.That(firstPair).IsEqualTo((false, false));
            await Assert.That(secondPair).IsEqualTo((false, false));
            await Assert.That(s3.RaysShortCircuited).IsEqualTo(0L);
            await Assert.That(s4.RaysCast).IsEqualTo(6L);
            await Assert.That(s4.RaysShortCircuited).IsEqualTo(6L);

            // 4. Open geometry: the first anchor is clear, nothing can short-circuit, hints untouched.
            VisibilityCounters.Reset();
            bool openSee = VisibilityAnalyzer.CouldSee(open, a, b, 53f, 37f, ReadOnlySpan<Vector4>.Empty, openHints);
            VisibilityCountersSnapshot s5 = VisibilityCounters.Snapshot();
            await Assert.That(openSee).IsTrue();
            await Assert.That(s5.RaysCast).IsEqualTo(1L);
            await Assert.That(s5.RaysClear).IsEqualTo(1L);
            await Assert.That(s5.RaysShortCircuited).IsEqualTo(0L);
            await Assert.That(openHints.All(h => h == -1)).IsTrue().Because("a clear ray leaves its hint alone");

            // 5. The wall's hints handed to the open engine are out of range there and ignored.
            VisibilityCounters.Reset();
            bool staleSee = VisibilityAnalyzer.CouldSee(open, a, b, 53f, 37f, ReadOnlySpan<Vector4>.Empty, hints);
            VisibilityCountersSnapshot s6 = VisibilityCounters.Snapshot();
            await Assert.That(staleSee).IsTrue();
            await Assert.That(s6.RaysShortCircuited).IsEqualTo(0L);

            foreach (VisibilityCountersSnapshot s in new[] { s1, s2, s3, s4, s5, s6 })
            {
                await Assert.That(s.RaysShortCircuited).IsLessThanOrEqualTo(s.RaysCast - s.RaysClear)
                    .Because("only an occluded ray can be short-circuited");
                await Assert.That(s.RaysCast + s.RaysSkippedByGate + s.RaysSkippedByEarlyExit).IsEqualTo(s.AnchorsTotal);
            }
        }
        finally
        {
            VisibilityCounters.Enabled = false;
            VisibilityCounters.Reset();
        }
    }

    /// <summary>
    ///     The scanner hands its own hint table to the pair loop: two players facing each other
    ///     through a wall cast twelve traversals on the first sample and twelve short-circuits on
    ///     the next. Without the wiring the second figure is zero. Plumbing only, as above: it does
    ///     not observe a wrong hint.
    /// </summary>
    [Test]
    public async Task Scanner_CarriesOccluderHintsAcrossSamples()
    {
        VisibilityEngine wall = WallAtX(250f);
        VisibilityTransitionScanner scanner = new(wall, new VisibilityTransitionScanner.Options(SampleStrideTicks: 1));
        List<AimVantage> vantages =
        [
            new(Player(0, 2, 0f, 0f), 0f, 0f, 0f), // looks +X at slot 1
            new(Player(1, 3, 500f, 180f), 0f, 0f, 180f) // looks -X at slot 0
        ];

        VisibilityCounters.Enabled = true;
        VisibilityCounters.Reset();
        try
        {
            _ = scanner.Sample(100, 100, vantages);
            VisibilityCountersSnapshot s1 = VisibilityCounters.Snapshot();
            await Assert.That(s1.PairsEvaluated).IsEqualTo(2L);
            await Assert.That(s1.RaysCast).IsEqualTo(12L).Because("every anchor is in frustum and behind the wall");
            await Assert.That(s1.RaysShortCircuited).IsEqualTo(0L);

            VisibilityCounters.Reset();
            _ = scanner.Sample(101, 101, vantages);
            VisibilityCountersSnapshot s2 = VisibilityCounters.Snapshot();
            await Assert.That(s2.RaysCast).IsEqualTo(12L);
            await Assert.That(s2.RaysShortCircuited).IsEqualTo(12L)
                .Because("the scanner must carry each pair's hints from one sample to the next");
            await Assert.That(scanner.IsAnyEnemyVisibleTo(0)).IsFalse();
            await Assert.That(scanner.IsAnyEnemyVisibleTo(1)).IsFalse();
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

/// <summary>
///     Pins the bake-load seam, which is gated differently from every other counter in the file.
///     <para>
///         The ray counters carry their own switch for the cost reason
///         <see cref="VisibilityCounters.RecordBake" /> states. A bake is loaded once per map, so its
///         timing rides the house-wide <c>Profiling.Enabled</c> instead. That split is easy to undo
///         by accident in either direction, and either way the damage is silent: tie it to the ray
///         switch and a plain profiled run reports nothing for the single largest item in a demo
///         open, or drop the gate and every unprofiled run pays for Stopwatch reads it never looks
///         at.
///     </para>
/// </summary>
[NotInParallel]
public class VisibilityBakeCounterTests
{
    [Test]
    public async Task RecordBake_IsInert_WhenProfilingIsOff()
    {
        bool wasProfiling = Profiling.Enabled;
        bool wasCounters = VisibilityCounters.Enabled;
        try
        {
            VisibilityCounters.Reset();
            Profiling.Enabled = false;

            // Counters ON, profiling OFF: the ray switch must not be what turns this seam on.
            VisibilityCounters.Enabled = true;
            VisibilityCounters.RecordBake(1000, 2000, 500);

            VisibilityCountersSnapshot snap = VisibilityCounters.Snapshot();
            await Assert.That(snap.BakesLoaded).IsEqualTo(0L);
            await Assert.That(snap.BakeLoadTicks).IsEqualTo(0L);
            await Assert.That(snap.BvhBuildTicks).IsEqualTo(0L);
        }
        finally
        {
            Profiling.Enabled = wasProfiling;
            VisibilityCounters.Enabled = wasCounters;
            VisibilityCounters.Reset();
        }
    }

    [Test]
    public async Task RecordBake_Accumulates_WhenProfilingIsOn()
    {
        bool wasProfiling = Profiling.Enabled;
        bool wasCounters = VisibilityCounters.Enabled;
        try
        {
            VisibilityCounters.Reset();
            Profiling.Enabled = true;

            // Counters OFF on purpose: a profiled run that forgot the ray switch must still account
            // for the load, which is the whole reason the two gates differ.
            VisibilityCounters.Enabled = false;
            VisibilityCounters.RecordBake(1000, 2000, 500);
            VisibilityCounters.RecordBake(3000, 4000, 700);

            VisibilityCountersSnapshot snap = VisibilityCounters.Snapshot();
            await Assert.That(snap.BakesLoaded).IsEqualTo(2L);
            await Assert.That(snap.BakeLoadTicks).IsEqualTo(4000L);
            await Assert.That(snap.BvhBuildTicks).IsEqualTo(6000L);
            await Assert.That(snap.BakeTriangles).IsEqualTo(1200L);
        }
        finally
        {
            Profiling.Enabled = wasProfiling;
            VisibilityCounters.Enabled = wasCounters;
            VisibilityCounters.Reset();
        }
    }

    /// <summary>
    ///     The seam is on the real load path, not merely callable. Times a genuine bake so a
    ///     refactor that stops calling <c>RecordBake</c> from <c>VisibilityEngine.Load</c> fails here
    ///     rather than reporting zeros forever.
    /// </summary>
    [Test]
    [Category("RealAsset")]
    public async Task LoadingARealBake_RecordsBothHalves()
    {
        string? dir = Environment.GetEnvironmentVariable(CollisionAssetLocator.EnvVar);
        if (string.IsNullOrWhiteSpace(dir))
        {
            throw new SkipTestException(
                $"Set {CollisionAssetLocator.EnvVar} to a directory holding <map>/collision.tris.");
        }

        string path = Path.Combine(dir, "de_nuke", "collision.tris");
        if (!File.Exists(path))
        {
            throw new SkipTestException($"No bake: looked for {path}");
        }

        bool wasProfiling = Profiling.Enabled;
        try
        {
            VisibilityCounters.Reset();
            Profiling.Enabled = true;

            VisibilityEngine engine = VisibilityEngine.Load(path);

            VisibilityCountersSnapshot snap = VisibilityCounters.Snapshot();
            await Assert.That(snap.BakesLoaded).IsEqualTo(1L);
            await Assert.That(snap.BakeTriangles).IsEqualTo((long)engine.TriangleCount);
            await Assert.That(snap.BakeLoadMs).IsGreaterThan(0.0);
            await Assert.That(snap.BvhBuildMs).IsGreaterThan(0.0);
        }
        finally
        {
            Profiling.Enabled = wasProfiling;
            VisibilityCounters.Reset();
        }
    }
}
