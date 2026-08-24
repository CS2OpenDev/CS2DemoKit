namespace CS2DemoKit.Parser.EntityTracking;

/// <summary>
///     Stateless checkpoint-replay seek core for tick-accurate UI seeking. It owns exactly one
///     responsibility: build a fresh <see cref="EntityTracker" /> via the caller-supplied
///     factory and replay it from the start of the frame list up to a target frame index,
///     optionally taking a prev-frame snapshot mid-replay for delta display.
///     <para>
///         The snapshot <em>policy</em> deliberately stays with the caller. Callers differ only in
///         whether they want a fresh prev-tick snapshot (<see cref="SeekToFrame" />), preserve their
///         own (<see cref="SeekToFrameNoSnapshot" />), or use explicit start/end indices
///         (<see cref="SeekToFrameWithSnapshotAt" />). This service performs the replay; the caller
///         decides what to do with the result.
///     </para>
///     <para>
///         <b>Cost.</b> Every call replays from frame 0 — that is what "checkpoint replay" means
///         here, and it is the right shape for one-off jumps in an interactive seek (the tracker is
///         swapped in wholesale, so no incremental state can go stale). For a forward-only walk over
///         many ticks it is quadratic; use <c>EntityStateLayer</c> in CS2DemoKit.Analysis instead,
///         which advances incrementally.
///     </para>
///     Pure compute; safe to invoke from a background <c>Task.Run</c> (the replay is the heavy
///     part). Holds no state between calls.
/// </summary>
public sealed class EntitySeekService
{
    private readonly Func<EntityTracker> _createTracker;

    /// <param name="createTracker">
    ///     Factory yielding a fresh tracker, already wired to whatever diagnostics the caller wants
    ///     (<see cref="EntityTracker.DecodeErrorRaised" />, <see cref="EntityTracker.PacketProcessed" />,
    ///     typed-wrapper bootstrap). Invoked once per seek — this service never reuses a tracker.
    /// </param>
    public EntitySeekService(Func<EntityTracker> createTracker) => _createTracker = createTracker;

    /// <summary>
    ///     Builds a fresh tracker and replays it to <paramref name="frameIndex" /> (inclusive,
    ///     0-based). When <paramref name="frameIndex" /> &gt; 0 a snapshot is taken at the
    ///     <em>previous</em> frame (delta baseline); at index 0 no snapshot is taken.
    /// </summary>
    public SeekResult SeekToFrame(int frameIndex, IReadOnlyList<DemoFrame> frames)
    {
        EntityTracker tracker = _createTracker();
        Dictionary<int, Dictionary<string, object?>>? snapshot = null;

        if (frameIndex > 0)
        {
            snapshot = tracker.ReplayToIndexWithSnapshot(frameIndex - 1, frameIndex, frames);
        }
        else
        {
            tracker.ReplayToIndex(0, frames);
        }

        return new SeekResult(tracker, snapshot);
    }

    /// <summary>
    ///     Builds a fresh tracker and replays it to <paramref name="frameIndex" /> with no
    ///     snapshot — for callers that preserve their own prev snapshot.
    /// </summary>
    public SeekResult SeekToFrameNoSnapshot(int frameIndex, IReadOnlyList<DemoFrame> frames)
    {
        EntityTracker tracker = _createTracker();
        tracker.ReplayToIndex(frameIndex, frames);
        return new SeekResult(tracker, null);
    }

    /// <summary>
    ///     Builds a fresh tracker, snapshots at <paramref name="snapshotAt" />, then replays to
    ///     <paramref name="endFrameIndex" />. When <paramref name="takeSnapshot" /> is false no
    ///     snapshot is taken (the tick-0 case).
    /// </summary>
    public SeekResult SeekToFrameWithSnapshotAt(int snapshotAt, int endFrameIndex, bool takeSnapshot,
        IReadOnlyList<DemoFrame> frames)
    {
        EntityTracker tracker = _createTracker();
        Dictionary<int, Dictionary<string, object?>>? snapshot = null;

        if (takeSnapshot)
        {
            snapshot = tracker.ReplayToIndexWithSnapshot(snapshotAt, endFrameIndex, frames);
        }
        else
        {
            tracker.ReplayToIndex(endFrameIndex, frames);
        }

        return new SeekResult(tracker, snapshot);
    }
}

/// <summary>
///     Result of a checkpoint-replay seek: the freshly-built, fully-advanced tracker plus an
///     optional prev-frame field snapshot for delta display.
/// </summary>
public readonly record struct SeekResult(
    EntityTracker Tracker,
    Dictionary<int, Dictionary<string, object?>>? PrevSnapshot);
