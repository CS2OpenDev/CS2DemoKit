#region

using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using CS2DemoKit.Analysis.Events;
using CS2DemoKit.Analysis.Visibility;
using CS2DemoKit.Parser;
using CS2DemoKit.Parser.EntityTracking;
using TUnit.Core.Exceptions;

#endregion

namespace CS2DemoKit.Analysis.Tests;

/// <summary>
///     Pins everything the two visibility consumers produce on the committed sample demo against
///     the real de_nuke bake: the accumulating analyzer's report, and the transition scanner's
///     spots, per-viewer visible level and on-target stamps on every frame.
///     <para>
///         This is the output gate for the ray-path performance work. A change that casts fewer
///         rays is allowed; a change that moves one spot tick, one chest angle or one accumulated
///         second is not, and the fixture is the byte-identical record it is held to. Regenerate
///         only after an intended behaviour change, with <c>CS2DEMOKIT_UPDATE_VISIBILITY_TRACE=1</c>,
///         and only with a differential log adjudicating every changed ray.
///     </para>
///     <para>
///         The fixture is the whole trace, not a hash of it. The scanner's per-viewer state is
///         written only when it moves, which keeps the four-round sample under a thousand lines,
///         and a full text means a mismatch names the spot or the pair that changed. On failure the
///         live rendering is written next to the temp directory and named in the message.
///     </para>
/// </summary>
[Category("Golden")]
[Category("RealAsset")]
[NotInParallel] // runs with the process-wide counters on, so nothing else may cast rays meanwhile
public class VisibilityTraceGoldenTests
{
    private const string UpdateVariable = "CS2DEMOKIT_UPDATE_VISIBILITY_TRACE";
    private const string FixtureName = "visibility-trace.golden.txt";

    [Test]
    public async Task VisibilityTrace_MatchesGolden()
    {
        string root = RepoRoot() ?? throw new SkipTestException("repo root not found");
        string goldenPath = Path.Combine(root, "tests", "fixtures", "sample-de_nuke", FixtureName);

        ParsedDemo demo = VisibilityReplay.RequireSampleDemo();
        await Assert.That(demo.Frames.Count).IsEqualTo(VisibilityReplay.SampleDemoFrameCount)
            .Because("the fixture describes exactly this trim of the sample demo");
        VisibilityEngine engine = VisibilityReplay.RequireBake(VisibilityReplay.SampleDemoMap);

        // Counters on, so the run can also say that the gates were exercised: a fixture pinned on a
        // trim where no smoke ever sat on a sightline would say nothing about smoke gating.
        Trace trace;
        VisibilityCounters.Enabled = true;
        VisibilityCounters.Reset();
        try
        {
            trace = Render(demo, engine);
        }
        finally
        {
            VisibilityCounters.Enabled = false;
            VisibilityCounters.Reset();
        }

        string rendered = trace.Render();

        if (Environment.GetEnvironmentVariable(UpdateVariable) == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(goldenPath)!);
            await File.WriteAllTextAsync(goldenPath, rendered);
            Console.WriteLine($"wrote {goldenPath}");
            return;
        }

        // Regenerate mode returned above, so this is a verify run and the fixture IS the gate on
        // every verdict below: a missing one fails rather than skips, or deleting the file would
        // stand in for passing it.
        if (!File.Exists(goldenPath))
        {
            Assert.Fail($"no fixture at {goldenPath}, and it is the only gate on the ray path. Restore the "
                        + $"committed file, or regenerate it with {UpdateVariable}=1 and commit the result with "
                        + "the differential log.");
        }

        // A trace that judged nothing would pin nothing, so the run has to have done real work
        // before a matching text means anything.
        await Assert.That(trace.Spots).IsGreaterThan(0).Because("the sample has engagements, so it has rising edges");
        await Assert.That(trace.AnalyzePairs).IsGreaterThan(0);
        await Assert.That(trace.TicksWithSmokes).IsGreaterThan(0)
            .Because("smoke gating is only pinned if some sampled tick had an active smoke");

        // The fixture is compared before anything about the counters is asserted, so a verdict
        // change is reported as the trace diff it is and not as a counter invariant it happened to
        // break on the way. The counter assertions below say the gates and the hints were
        // exercised on this trim; they are not evidence about verdicts.
        string expected = await File.ReadAllTextAsync(goldenPath);
        if (!string.Equals(Normalize(rendered), Normalize(expected), StringComparison.Ordinal))
        {
            string actualPath = Path.Combine(Path.GetTempPath(), "visibility-trace.actual.txt");
            await File.WriteAllTextAsync(actualPath, rendered);
            await Assert.That(Normalize(rendered)).IsEqualTo(Normalize(expected))
                .Because($"the visibility trace changed; the live rendering is at {actualPath}, diff it against "
                         + $"{goldenPath}. If the change was intended, re-run with {UpdateVariable}=1 and attach the "
                         + "differential log adjudicating every changed ray before committing it.");
        }

        // Both gates fired on both paths, and the accounting identity held on both. On the scanner
        // (could-see) path every smoked in-frustum anchor is a gate skip; on the analyzer path a smoke
        // skip needs an already-exposed pair with a smoked in-frustum anchor, which this trim has
        // (616 of them when the fixture was pinned), so it is asserted there as well.
        VisibilityCountersSnapshot analyzer = trace.AnalyzerCounters;
        VisibilityCountersSnapshot scanner = trace.ScannerCounters;
        Console.WriteLine($"[visibility-trace] analyzer: cast={analyzer.RaysCast} gate={analyzer.RaysSkippedByGate} "
                          + $"smoke={analyzer.RaysSkippedBySmoke} earlyExit={analyzer.RaysSkippedByEarlyExit} "
                          + $"couldSee={analyzer.PairsCouldSee} exposed={analyzer.PairsExposed}");
        Console.WriteLine($"[visibility-trace] scanner: cast={scanner.RaysCast} gate={scanner.RaysSkippedByGate} "
                          + $"smoke={scanner.RaysSkippedBySmoke} earlyExit={scanner.RaysSkippedByEarlyExit} "
                          + $"couldSee={scanner.PairsCouldSee} pairs={scanner.PairsEvaluated}");
        await Assert.That(scanner.RaysSkippedBySmoke).IsGreaterThan(0L)
            .Because("some in-frustum anchor on the sample must have sat behind a smoke");
        await Assert.That(scanner.RaysSkippedByGate).IsGreaterThan(scanner.RaysSkippedBySmoke)
            .Because("the frustum gate fires far more often than the smoke gate");
        await Assert.That(analyzer.RaysSkippedBySmoke).IsGreaterThan(0L)
            .Because("some exposed pair on the sample must have had an in-frustum anchor behind a smoke");
        await Assert.That(scanner.RaysCastOutsideFrustum).IsEqualTo(0L)
            .Because("could-see never casts for an anchor outside the frustum");
        await Assert.That(scanner.RaysCast + scanner.RaysSkippedByGate + scanner.RaysSkippedByEarlyExit)
            .IsEqualTo(scanner.AnchorsTotal);
        await Assert.That(analyzer.RaysCast + analyzer.RaysSkippedByGate + analyzer.RaysSkippedByEarlyExit)
            .IsEqualTo(analyzer.AnchorsTotal);

        // Magnitude, not just existence: on the could-see path only an in-frustum anchor can be cast,
        // and every out-of-frustum anchor the loop reached was a gate skip, so the gate count is
        // bounded below by the out-of-frustum anchors less the unvisited tail. A gate that skipped
        // one anchor per demo would satisfy the assertions above and fail these.
        await Assert.That(scanner.RaysCast).IsLessThanOrEqualTo(scanner.AnchorsInFrustum)
            .Because("could-see casts only for anchors inside the frustum");
        await Assert.That(scanner.RaysSkippedByGate + scanner.RaysSkippedByEarlyExit)
            .IsGreaterThanOrEqualTo(scanner.AnchorsTotal - scanner.AnchorsInFrustum)
            .Because("every out-of-frustum anchor is either gate-skipped or never visited");
        await Assert.That(analyzer.RaysCastOutsideFrustum).IsGreaterThan(0L)
            .Because("EvaluatePair still casts out-of-frustum anchors while exposed is unknown");

        // Last-occluder hints are on both paths. They are bounded by the occluded rays (a clear
        // ray is never short-circuited), and they have to decide most rays on real play, or the
        // premise that consecutive samples share an occluder is wrong for this geometry.
        double scannerHitRate = scanner.RaysShortCircuited / (double)scanner.RaysCast;
        double analyzerHitRate = analyzer.RaysShortCircuited / (double)analyzer.RaysCast;
        Console.WriteLine($"[visibility-trace] short-circuit: scanner {scanner.RaysShortCircuited}/{scanner.RaysCast} "
                          + $"({scannerHitRate:P2}) analyzer {analyzer.RaysShortCircuited}/{analyzer.RaysCast} ({analyzerHitRate:P2})");
        await Assert.That(scanner.RaysShortCircuited).IsLessThanOrEqualTo(scanner.RaysCast - scanner.RaysClear);
        await Assert.That(analyzer.RaysShortCircuited).IsLessThanOrEqualTo(analyzer.RaysCast - analyzer.RaysClear);
        await Assert.That(scannerHitRate).IsGreaterThan(0.5)
            .Because("at stride 1 nearly every occluded sightline is stopped by the surface that stopped it last tick");
        await Assert.That(analyzer.RaysShortCircuited).IsGreaterThan(0L)
            .Because("the stride-4 analyzer carries hints too");
    }

    /// <summary>Everything the two consumers produced, plus the counts that head the fixture.</summary>
    internal sealed class Trace
    {
        /// <summary>The full rendering, one line per fact.</summary>
        public string Full { get; init; } = string.Empty;

        /// <summary>Ticks the accumulating analyzer sampled.</summary>
        public int AnalyzeSampledTicks { get; init; }

        /// <summary>Directed pairs with any accumulated time.</summary>
        public int AnalyzePairs { get; init; }

        /// <summary>Frames handed to the scanner.</summary>
        public int ScannerFrames { get; init; }

        /// <summary>Rising edges the scanner emitted.</summary>
        public int Spots { get; init; }

        /// <summary>Frames on which the per-viewer state line changed.</summary>
        public int StateChanges { get; init; }

        /// <summary>Frames on which at least one smoke was active.</summary>
        public int TicksWithSmokes { get; init; }

        /// <summary>Ray counters over the analyzer section; all zero unless the caller enabled them.</summary>
        public VisibilityCountersSnapshot AnalyzerCounters { get; init; }

        /// <summary>Ray counters over the scanner section; all zero unless the caller enabled them.</summary>
        public VisibilityCountersSnapshot ScannerCounters { get; init; }

        /// <summary>The fixture text: structural counts, then the trace.</summary>
        public string Render()
        {
            StringBuilder sb = new(Full.Length + 256);
            sb.Append("frames=").Append(VisibilityReplay.SampleDemoFrameCount).Append('\n');
            sb.Append("analyze.sampledTicks=").Append(AnalyzeSampledTicks).Append('\n');
            sb.Append("analyze.pairs=").Append(AnalyzePairs).Append('\n');
            sb.Append("scanner.frames=").Append(ScannerFrames).Append('\n');
            sb.Append("scanner.spots=").Append(Spots).Append('\n');
            sb.Append("scanner.stateChanges=").Append(StateChanges).Append('\n');
            sb.Append("scanner.ticksWithSmokes=").Append(TicksWithSmokes).Append('\n');
            sb.Append(Full);
            return sb.ToString();
        }
    }

    /// <summary>Runs both consumers over the demo and renders every result deterministically.</summary>
    internal static Trace Render(ParsedDemo demo, VisibilityEngine engine)
    {
        StringBuilder sb = new(1 << 16);
        CultureInfo inv = CultureInfo.InvariantCulture;

        // Section 1: the accumulating analyzer at its defaults (stride 4, smokes on).
        VisibilityAnalyzer.Report report = VisibilityAnalyzer.Analyze(
            demo.Frames, engine, PositionUtil.CellToWorld, new VisibilityAnalyzer.Options());
        VisibilityCountersSnapshot analyzerCounters = VisibilityCounters.Snapshot();
        VisibilityCounters.Reset();
        sb.Append("[analyze]\n");
        sb.Append("sampledTicks=").Append(report.SampledTicks).Append('\n');
        sb.Append("sampledSeconds=").Append(report.SampledSeconds.ToString("R", inv)).Append('\n');
        foreach (VisibilityAnalyzer.PairStat pair in report.Pairs
                     .OrderBy(p => p.ViewerSlot).ThenBy(p => p.TargetSlot))
        {
            sb.Append("pair ").Append(pair.ViewerSlot).Append('>').Append(pair.TargetSlot)
                .Append(" exposed=").Append(pair.ExposedSeconds.ToString("R", inv))
                .Append(" couldSee=").Append(pair.CouldSeeSeconds.ToString("R", inv)).Append('\n');
        }

        foreach (KeyValuePair<int, double> kv in report.CouldSeeAnyEnemySeconds.OrderBy(kv => kv.Key))
        {
            sb.Append("couldSeeAny ").Append(kv.Key).Append('=').Append(kv.Value.ToString("R", inv)).Append('\n');
        }

        foreach (KeyValuePair<int, double> kv in report.ExposedToAnyEnemySeconds.OrderBy(kv => kv.Key))
        {
            sb.Append("exposedToAny ").Append(kv.Key).Append('=').Append(kv.Value.ToString("R", inv)).Append('\n');
        }

        // Section 2: the transition scanner at its defaults (stride 1), every frame, with smokes.
        sb.Append("[scanner]\n");
        VisibilityTransitionScanner scanner = new(engine);
        int frames = 0, spots = 0, stateChanges = 0, ticksWithSmokes = 0;
        string previousState = string.Empty;
        StringBuilder state = new(256);
        List<int> slots = new(12);

        VisibilityReplay.ForEachFrame(demo.Frames, (tick, vantages, smokes) =>
        {
            frames++;
            if (smokes.Count > 0)
            {
                ticksWithSmokes++;
            }

            IReadOnlyList<EnemySpottedEvent> emitted = scanner.Sample(
                tick, tick, vantages, CollectionsMarshal.AsSpan(smokes));
            for (int i = 0; i < emitted.Count; i++)
            {
                EnemySpottedEvent spot = emitted[i];
                spots++;
                sb.Append("spot ").Append(spot.ServerTick).Append(' ')
                    .Append(spot.ViewerSlot).Append('>').Append(spot.TargetSlot)
                    .Append(" chest=").Append(spot.AngleToChestDeg.ToString("R", inv))
                    .Append(" pitch=").Append(spot.ViewerPitchDeg.ToString("R", inv))
                    .Append(" yaw=").Append(spot.ViewerYawDeg.ToString("R", inv)).Append('\n');
            }

            // The level and the stamps, per viewer, emitted only when they move so the trace stays
            // readable; the tick on the line says when.
            slots.Clear();
            for (int i = 0; i < vantages.Count; i++)
            {
                slots.Add(vantages[i].Vantage.Slot);
            }

            slots.Sort();
            state.Clear();
            for (int i = 0; i < slots.Count; i++)
            {
                int slot = slots[i];
                state.Append(slot).Append(':').Append(scanner.IsAnyEnemyVisibleTo(slot) ? '1' : '0')
                    .Append(':').Append(scanner.OnTargetSince(slot)).Append(',');
            }

            string current = state.ToString();
            if (!string.Equals(current, previousState, StringComparison.Ordinal))
            {
                stateChanges++;
                previousState = current;
                sb.Append("state ").Append(tick).Append(' ').Append(current).Append('\n');
            }
        });

        return new Trace
        {
            Full = sb.ToString(),
            AnalyzeSampledTicks = report.SampledTicks,
            AnalyzePairs = report.Pairs.Count,
            ScannerFrames = frames,
            Spots = spots,
            StateChanges = stateChanges,
            TicksWithSmokes = ticksWithSmokes,
            AnalyzerCounters = analyzerCounters,
            ScannerCounters = VisibilityCounters.Snapshot()
        };
    }

    private static string Normalize(string s) => s.Replace("\r\n", "\n").TrimEnd('\n');

    private static string? RepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8 && dir is not null; i++)
        {
            if (File.Exists(Path.Combine(dir, "CS2DemoKit.slnx")))
            {
                return dir;
            }

            dir = Path.GetDirectoryName(dir);
        }

        return null;
    }
}
