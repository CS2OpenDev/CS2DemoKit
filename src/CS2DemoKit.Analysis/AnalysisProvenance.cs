#region

using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Parser;

#endregion

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

    /// <summary>
    ///     Chunks decoded ahead of the evaluator over a bounded window, on either source, or the
    ///     whole demo before it when the host precomputed.
    /// </summary>
    Pipelined
}

/// <summary>How the source profile a build ran under was chosen.</summary>
public enum ProfileResolutionKind
{
    /// <summary>The caller passed <see cref="AnalysisOptions.Profile" />.</summary>
    Explicit,

    /// <summary>Header classification alone. A tournament recording reads as matchmaking here.</summary>
    HeaderOnly,

    /// <summary>Header classification refined by the game-event names the demo actually fires.</summary>
    HeaderAndVocabulary
}

/// <summary>
///     What the evaluation saw of the two per-round end markers, so a header-only profile that
///     guessed the wrong dialect is visible rather than silent.
/// </summary>
/// <param name="RoundOfficiallyEndedSeen">How many <c>round_officially_ended</c> fires the run dispatched.</param>
/// <param name="CsPreRestartSeen">How many <c>cs_pre_restart</c> fires the run dispatched.</param>
/// <param name="BoundMarkerNeverSeen">
///     True when the profile's per-round marker never fired while the other one did: every
///     round-end effect of the run stayed silent until the match-end fallback.
/// </param>
public readonly record struct DialectCheck(int RoundOfficiallyEndedSeen, int CsPreRestartSeen, bool BoundMarkerNeverSeen);

/// <summary>
///     What an evaluation actually did, so a caller knows what it is holding and a measurement
///     knows what it measured.
/// </summary>
/// <param name="Source">The frame source kind.</param>
/// <param name="Profile">The source profile the graph was built for.</param>
/// <param name="ProfileResolution">How that profile was chosen.</param>
/// <param name="DecodePlan">The plan the source decoded under, when the source reports one.</param>
/// <param name="Digest">How entity digests were produced.</param>
/// <param name="SnapshotsCaptured">Whether per-message node snapshots were kept.</param>
/// <param name="FramesConsumed">Frames the evaluator read.</param>
/// <param name="MessagesConsumed">Messages the evaluator dispatched, synthesized ones included.</param>
/// <param name="Dialect">The round-end markers the run saw against the profile it was bound to.</param>
public sealed record AnalysisProvenance(
    AnalysisSourceKind Source,
    DemoSourceProfile Profile,
    ProfileResolutionKind ProfileResolution,
    DecodePlan? DecodePlan,
    DigestProducerKind Digest,
    bool SnapshotsCaptured,
    int FramesConsumed,
    int MessagesConsumed,
    DialectCheck Dialect);
