#region

using System.Diagnostics.Metrics;

#endregion

namespace CS2DemoKit.Parser;

/// <summary>
///     Parser-owned half of the decode meter (see docs/profiling.md). The analysis-side half,
///     <c>analysis.entity_scan.started</c>, lives on CS2DemoKit.Analysis's own evaluator Meter; a
///     host sums the two for "decodes per visit".
/// </summary>
internal static class EntityDecodeMetrics
{
    private static readonly Meter _meter = new("CS2DemoKit.Parser.Entities");

    /// <summary>
    ///     Counter: +1 when a metered <see cref="EntityTracking.EntityTracker" /> folds its first
    ///     frame. A tracker a rules run owns counts zero here; see
    ///     <see cref="EntityTracking.EntityTracker.Metered" />.
    /// </summary>
    public static readonly Counter<long> DecodeStarted =
        _meter.CreateCounter<long>("parser.entity_decode.started");
}
