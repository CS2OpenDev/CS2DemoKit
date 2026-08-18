#region

using System.Diagnostics.Metrics;

#endregion

namespace CS2DemoKit.Analysis.Diagnostics;

/// <summary>OpenTelemetry meter and counters for the analysis evaluator's hot-path instrumentation.</summary>
internal static class EvaluatorMetrics
{
    private static readonly Meter _meter = new("CS2DemoKit.Analysis.Evaluator");

    /// <summary>Counter: number of edges considered for evaluation across all frames.</summary>
    public static readonly Counter<long> EdgesEvaluated =
        _meter.CreateCounter<long>("analysis.edges.evaluated");

    /// <summary>Counter: number of edges whose <c>TryApply</c> returned <c>true</c>.</summary>
    public static readonly Counter<long> EdgesFired =
        _meter.CreateCounter<long>("analysis.edges.fired");

    /// <summary>Histogram: wall-clock duration of a single frame's evaluation, in milliseconds.</summary>
    public static readonly Histogram<double> FrameDurationMs =
        _meter.CreateHistogram<double>("analysis.frame.duration_ms");

    /// <summary>Counter: number of logic-node (conjunction/disjunction) recomputes triggered.</summary>
    public static readonly Counter<long> LogicNodesRecomputed =
        _meter.CreateCounter<long>("analysis.logic_nodes.recomputed");

    /// <summary>Counter: total messages dispatched through the evaluator.</summary>
    public static readonly Counter<long> MessagesProcessed =
        _meter.CreateCounter<long>("analysis.messages.processed");

    /// <summary>Counter: number of per-player templates materialized into concrete nodes.</summary>
    public static readonly Counter<long> PlayersMaterialized =
        _meter.CreateCounter<long>("analysis.players.materialized");

    /// <summary>
    ///     True when a <see cref="MeterListener" /> (e.g. <c>dotnet-counters</c>, or AnalysisBench
    ///     <c>--counters</c>) is subscribed to this meter's instruments. The evaluator guards its
    ///     per-message <c>Counter.Add</c> block on this, so the default user path pays a single bool
    ///     read instead of four <c>Counter.Add</c> when nobody is listening. <c>Instrument.Enabled</c>
    ///     is the runtime's own near-free "are there any listeners?" check.
    /// </summary>
    public static bool Enabled => MessagesProcessed.Enabled;
}
