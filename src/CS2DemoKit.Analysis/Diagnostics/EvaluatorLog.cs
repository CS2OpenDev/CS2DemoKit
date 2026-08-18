#region

using Microsoft.Extensions.Logging;

#endregion

namespace CS2DemoKit.Analysis.Diagnostics;

/// <summary>
///     Source-generated, high-performance log messages for the analysis evaluator's coarse lifecycle
///     — the human-readable counterpart to the firehose <see cref="EvaluatorEventSource" /> (which
///     stays the machine-readable per-frame/per-edge channel for <c>dotnet-trace</c>). Only low-rate,
///     end-user-meaningful seams are logged here (a run starts/finishes, a round resets, an authoring
///     warning); the per-frame / per-message / per-edge events are deliberately NOT logged, so this
///     stream is safe to surface live in the Diagnostics tab.
///     <para>
///         Each method is emitted by the <c>[LoggerMessage]</c> source generator: no boxing, no
///         format-string parsing at call time, and a compiler-inserted <see cref="ILogger.IsEnabled" />
///         guard so a call costs a single branch when the level is disabled.
///     </para>
/// </summary>
internal static partial class EvaluatorLog
{
    /// <summary>Category all evaluator log rows are tagged with in the Diagnostics tab.</summary>
    public const string Category = "Analysis.Evaluator";

    /// <summary>
    ///     A cached logger for evaluator log sites in <c>static</c> contexts (e.g. graph-build topological
    ///     sorting) where no per-instance logger is reachable. Resolved from the ambient factory on first
    ///     use — which is during an analysis run, after the App has wired a real factory at startup.
    /// </summary>
    internal static readonly ILogger Shared = DiagnosticsLog.CreateLogger(Category);

    [LoggerMessage(EventId = 1, Level = LogLevel.Information,
        Message = "Analysis started — {frameCount} frames, {edgeCount} edges, {nodeCount} nodes")]
    public static partial void EvaluationStarted(ILogger logger, int frameCount, int edgeCount, int nodeCount);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information,
        Message = "Analysis completed — {messagesProcessed} messages, {edgesFired} edges fired in {elapsedMs} ms")]
    public static partial void EvaluationCompleted(ILogger logger, int messagesProcessed, int edgesFired, double elapsedMs);

    [LoggerMessage(EventId = 3, Level = LogLevel.Debug,
        Message = "Round reset — {roundScopedNodeCount} round-scoped nodes cleared")]
    public static partial void RoundReset(ILogger logger, int roundScopedNodeCount);

    [LoggerMessage(EventId = 4, Level = LogLevel.Warning,
        Message = "Edge '{sourceName}' wrote node '{writtenNodeName}' without declaring its effect type")]
    public static partial void UndeclaredEdgeEffect(ILogger logger, string sourceName, string writtenNodeName);
}
