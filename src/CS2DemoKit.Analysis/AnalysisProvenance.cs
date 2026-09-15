namespace CS2DemoKit.Analysis;

/// <summary>Which kind of frame source an evaluation ran over.</summary>
public enum AnalysisSourceKind
{
    /// <summary>A retained <c>ParsedDemo</c>, or any source with random access.</summary>
    Materialised,

    /// <summary>A forward reader: frames were dropped as they were consumed.</summary>
    Stream
}

/// <summary>How the entity digests the rules read were produced.</summary>
public enum DigestProducerKind
{
    /// <summary>No rule read entity state; no scanner ran.</summary>
    None,

    /// <summary>One layer advanced in step with the evaluator.</summary>
    Sequential,

    /// <summary>Every chunk decoded in parallel before the first frame was evaluated.</summary>
    ParallelUpFront,

    /// <summary>Chunks decoded ahead of the evaluator over a bounded window.</summary>
    Pipelined
}

/// <summary>
///     What an evaluation actually did, so a caller knows what it is holding and a measurement
///     knows what it measured.
/// </summary>
/// <param name="Source">The frame source kind.</param>
/// <param name="Digest">How entity digests were produced.</param>
/// <param name="SnapshotsCaptured">Whether per-message node snapshots were kept.</param>
/// <param name="FramesConsumed">Frames the evaluator read.</param>
/// <param name="MessagesConsumed">Messages the evaluator dispatched, synthesized ones included.</param>
public sealed record AnalysisProvenance(
    AnalysisSourceKind Source,
    DigestProducerKind Digest,
    bool SnapshotsCaptured,
    int FramesConsumed,
    int MessagesConsumed);
