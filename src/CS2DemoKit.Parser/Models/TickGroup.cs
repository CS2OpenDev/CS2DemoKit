namespace CS2DemoKit.Parser.Models;

// Note: IGrouping<int, DemoFrame> was considered, but StartFrameIndex / EndFrameIndex are not part
// of that interface and are used by the ViewModel for entity-state seeking, so the class earns its place.
/// <summary>
///     A group of consecutive <see cref="DemoFrame" /> instances sharing the same <see cref="GameTick" />.
///     Produced by <c>DemoParser</c>'s tick-grouping pass; consumed by the UI for tick-level navigation.
/// </summary>
public sealed class TickGroup
{
    /// <summary>Inclusive frame index of the last frame in the group.</summary>
    public int EndFrameIndex { get; init; }

    /// <summary>All frames in the group, in original order.</summary>
    public IReadOnlyList<DemoFrame> Frames { get; init; } = [];

    /// <summary>
    ///     Game tick (= ServerTick − server_start_tick) shared by all frames in this group.
    ///     Negative for pre-game frames captured before the match started.
    /// </summary>
    public int GameTick { get; init; }

    /// <summary>Inclusive frame index of the first frame in the group.</summary>
    public int StartFrameIndex { get; init; }

    /// <summary>Raw server tick of the first frame in this group (used for entity seeking).</summary>
    public int Tick { get; init; }

    /// <inheritdoc />
    public override string ToString() => $"GameTick {GameTick}  ({Frames.Count} frames)";
}
