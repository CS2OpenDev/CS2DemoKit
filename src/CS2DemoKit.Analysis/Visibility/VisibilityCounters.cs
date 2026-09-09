#region

using System.Diagnostics;

#endregion

namespace CS2DemoKit.Analysis.Visibility;

/// <summary>
///     Process-wide ray budget counters for the visibility path, OFF by default. When
///     <see cref="Enabled" /> is false (the default, and nothing in the library ever sets it true)
///     every instrumented seam costs one predicted branch and touches none of the accumulators, so a
///     normal run is byte-for-byte the same computation. When a host turns it on before a run, every
///     <c>VisibilityAnalyzer.EvaluatePair</c> and every
///     <c>VisibilityTransitionScanner.Sample</c> reports what it did here, and the host
///     reads one <see cref="Snapshot" /> afterwards.
///     <para>
///         The point of measuring rather than modelling: the number of rays a demo needs is a
///         function of how play clusters, and a uniform-bearing model of the frustum reject rate
///         says nothing about that. These counters distinguish the rays that were cast from the
///         rays a frustum-first gate would have skipped, so a proposed optimisation can be sized
///         against real demos before it is written.
///     </para>
///     <para>
///         <b>Threading.</b> Accumulation is <see cref="Interlocked" /> so concurrent analyzers
///         (tests, or a host evaluating two demos at once) cannot tear a total, but the numbers are
///         process-wide, not per run: a host that wants one run's figures calls <see cref="Reset" />
///         before it and <see cref="Snapshot" /> after, with nothing else casting in between.
///         <see cref="Enabled" /> follows the same set-before-the-run contract as
///         <c>CS2DemoKit.Parser.Profiling.Enabled</c>.
///     </para>
///     <para>
///         <b>Timing overhead.</b> <see cref="VisibilityCountersSnapshot.RayTicks" /> brackets each
///         individual raycast with two <see cref="Stopwatch" /> reads, which costs a few percent of
///         a ray on this traversal. It is the honest per-ray number, but a run with counters on is
///         therefore a little slower than one without, and an eval time taken with them on should be
///         compared against one taken with them off.
///     </para>
/// </summary>
public static class VisibilityCounters
{
    private static long _anchorsInFrustum;
    private static long _anchorsTotal;
    private static long _pairsCouldSee;
    private static long _pairsEvaluated;
    private static long _pairsExposed;
    private static long _pairsNoAnchorInFrustum;
    private static long _raysCast;
    private static long _raysCastOnFrustumRejectedPairs;
    private static long _raysCastOutsideFrustum;
    private static long _raysClear;
    private static long _raysSkippedByEarlyExit;
    private static long _rayTicks;
    private static long _sampledTicks;
    private static long _sampleTicks;

    /// <summary>
    ///     Whether the seams accumulate. Default false. Set on the orchestrating thread before the
    ///     run it governs; the seams read it once per pair, so a mid-run flip is observed at a pair
    ///     boundary and never inside one.
    /// </summary>
    public static bool Enabled { get; set; }

    /// <summary>Zeroes every accumulator. Does not change <see cref="Enabled" />.</summary>
    public static void Reset()
    {
        Interlocked.Exchange(ref _pairsEvaluated, 0);
        Interlocked.Exchange(ref _pairsNoAnchorInFrustum, 0);
        Interlocked.Exchange(ref _pairsExposed, 0);
        Interlocked.Exchange(ref _pairsCouldSee, 0);
        Interlocked.Exchange(ref _anchorsTotal, 0);
        Interlocked.Exchange(ref _anchorsInFrustum, 0);
        Interlocked.Exchange(ref _raysCast, 0);
        Interlocked.Exchange(ref _raysClear, 0);
        Interlocked.Exchange(ref _raysCastOutsideFrustum, 0);
        Interlocked.Exchange(ref _raysCastOnFrustumRejectedPairs, 0);
        Interlocked.Exchange(ref _raysSkippedByEarlyExit, 0);
        Interlocked.Exchange(ref _rayTicks, 0);
        Interlocked.Exchange(ref _sampledTicks, 0);
        Interlocked.Exchange(ref _sampleTicks, 0);
    }

    /// <summary>Reads every accumulator at once. All-zero when nothing ran with <see cref="Enabled" /> on.</summary>
    public static VisibilityCountersSnapshot Snapshot() => new(
        Interlocked.Read(ref _pairsEvaluated),
        Interlocked.Read(ref _pairsNoAnchorInFrustum),
        Interlocked.Read(ref _pairsExposed),
        Interlocked.Read(ref _pairsCouldSee),
        Interlocked.Read(ref _anchorsTotal),
        Interlocked.Read(ref _anchorsInFrustum),
        Interlocked.Read(ref _raysCast),
        Interlocked.Read(ref _raysClear),
        Interlocked.Read(ref _raysCastOutsideFrustum),
        Interlocked.Read(ref _raysCastOnFrustumRejectedPairs),
        Interlocked.Read(ref _raysSkippedByEarlyExit),
        Interlocked.Read(ref _rayTicks),
        Interlocked.Read(ref _sampledTicks),
        Interlocked.Read(ref _sampleTicks));

    /// <summary>
    ///     Publishes one directed pair's tallies. Called by <c>VisibilityAnalyzer.EvaluatePair</c>
    ///     after the pair is decided, once, with locals it accumulated during the anchor loop.
    /// </summary>
    internal static void RecordPair(
        int anchorsTotal, int anchorsInFrustum, int raysCast, int raysClear, int raysOutsideFrustum,
        int raysSkipped, long rayTicks, bool exposed, bool couldSee)
    {
        Interlocked.Increment(ref _pairsEvaluated);
        Interlocked.Add(ref _anchorsTotal, anchorsTotal);
        Interlocked.Add(ref _anchorsInFrustum, anchorsInFrustum);
        Interlocked.Add(ref _raysCast, raysCast);
        Interlocked.Add(ref _raysClear, raysClear);
        Interlocked.Add(ref _raysCastOutsideFrustum, raysOutsideFrustum);
        Interlocked.Add(ref _raysSkippedByEarlyExit, raysSkipped);
        Interlocked.Add(ref _rayTicks, rayTicks);
        if (anchorsInFrustum == 0)
        {
            Interlocked.Increment(ref _pairsNoAnchorInFrustum);
            Interlocked.Add(ref _raysCastOnFrustumRejectedPairs, raysCast);
        }

        if (exposed)
        {
            Interlocked.Increment(ref _pairsExposed);
        }

        if (couldSee)
        {
            Interlocked.Increment(ref _pairsCouldSee);
        }
    }

    /// <summary>Publishes one sampled tick's wall-clock from <c>VisibilityTransitionScanner.Sample</c>.</summary>
    internal static void RecordSample(long sampleTicks)
    {
        Interlocked.Increment(ref _sampledTicks);
        Interlocked.Add(ref _sampleTicks, sampleTicks);
    }
}

/// <summary>
///     One read of <see cref="VisibilityCounters" />. Tick fields are raw <see cref="Stopwatch" />
///     timestamps; divide by <see cref="Stopwatch.Frequency" /> for seconds.
/// </summary>
/// <param name="PairsEvaluated">Directed enemy pairs handed to <c>VisibilityAnalyzer.EvaluatePair</c>.</param>
/// <param name="PairsNoAnchorInFrustum">
///     Of those, pairs where NO body anchor was inside the viewer's frustum. A frustum-first gate
///     would reject exactly these before casting anything; the current code still casts for them
///     because <c>exposed</c> does not depend on the frustum.
/// </param>
/// <param name="PairsExposed">Pairs where some anchor had clear line of sight.</param>
/// <param name="PairsCouldSee">Pairs where some anchor was clear, in frustum, and not smoked.</param>
/// <param name="AnchorsTotal">Body anchors considered, summed over pairs (six per pair today).</param>
/// <param name="AnchorsInFrustum">Of those, anchors inside the viewer's frustum.</param>
/// <param name="RaysCast">Raycasts actually issued to the BVH.</param>
/// <param name="RaysClear">Of those, rays that found no occluder.</param>
/// <param name="RaysCastOutsideFrustum">Of those, rays whose anchor was outside the frustum (cast only to decide <c>exposed</c>).</param>
/// <param name="RaysCastOnFrustumRejectedPairs">
///     Of those, rays cast on pairs with no anchor in frustum. This is the count a frustum-first
///     gate would remove outright; <see cref="RaysCastOutsideFrustum" /> is the larger count that
///     gating per anchor rather than per pair would remove.
/// </param>
/// <param name="RaysSkippedByEarlyExit">Anchors never cast because the pair's result was already decided.</param>
/// <param name="RayTicks">Wall-clock inside <c>VisibilityEngine.IsVisible</c>, summed over every cast ray.</param>
/// <param name="SampledTicks">Ticks the transition scanner actually evaluated (stride-admitted, not skipped).</param>
/// <param name="SampleTicks">
///     Wall-clock of the transition scanner's whole pairwise pass per sampled tick, summed. Contains
///     <see cref="RayTicks" /> plus frustum tests, anchor building, the crosshair test and set upkeep.
/// </param>
public readonly record struct VisibilityCountersSnapshot(
    long PairsEvaluated,
    long PairsNoAnchorInFrustum,
    long PairsExposed,
    long PairsCouldSee,
    long AnchorsTotal,
    long AnchorsInFrustum,
    long RaysCast,
    long RaysClear,
    long RaysCastOutsideFrustum,
    long RaysCastOnFrustumRejectedPairs,
    long RaysSkippedByEarlyExit,
    long RayTicks,
    long SampledTicks,
    long SampleTicks)
{
    /// <summary>Wall-clock inside the raycaster, in milliseconds.</summary>
    public double RayMs => RayTicks * 1000.0 / Stopwatch.Frequency;

    /// <summary>Wall-clock of the transition scanner's pairwise pass, in milliseconds.</summary>
    public double SampleMs => SampleTicks * 1000.0 / Stopwatch.Frequency;
}
