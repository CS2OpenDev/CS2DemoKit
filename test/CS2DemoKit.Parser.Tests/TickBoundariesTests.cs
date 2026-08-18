namespace CS2DemoKit.Parser.Tests;

/// <summary>
///     <see cref="TickBoundaries" /> unit battery. The recipe was extracted from the App's
///     <c>SemanticNavigator.Build</c> and is now shared with <see cref="TickMapper" />, so the
///     frame-clock facts it must preserve are pinned here: pre-game frames carry a large NEGATIVE
///     sentinel <see cref="DemoFrame.ServerTick" /> (a legal tick, not an "unset" marker), several
///     consecutive frames can share one tick, and the boundary is the FIRST frame of each tick.
/// </summary>
[Category("Unit")]
public class TickBoundariesTests
{
    private const int Sentinel = -999_999;

    // index:      0         1         2  3  4  5  6  7
    // ServerTick: sentinel  sentinel  1  1  2  4  4  7
    private static readonly int[] _mixedTicks = [Sentinel, Sentinel, 1, 1, 2, 4, 4, 7];
    private static readonly int[] _mixedBoundaries = [0, 2, 4, 5, 7];

    private static readonly int[] _sentinelRunTicks = [Sentinel, Sentinel, Sentinel, 1];
    private static readonly int[] _sentinelRunBoundaries = [0, 3];

    private static readonly int[] _distinctTicks = [1, 2, 3];
    private static readonly int[] _distinctBoundaries = [0, 1, 2];

    private static IReadOnlyList<DemoFrame> Frames(int[] ticks) =>
        [.. ticks.Select((tick, index) => new DemoFrame
        {
            Command = "dem_packet",
            FrameNumber = index,
            HeaderLength = 0,
            IsCompressed = false,
            RawLength = 0,
            RawStart = 0,
            ServerTick = tick
        })];

    [Test]
    public async Task FrameIndices_TakesFirstFrameOfEachDistinctTick()
    {
        int[] boundaries = TickBoundaries.FrameIndices(Frames(_mixedTicks));

        await Assert.That(boundaries).IsEquivalentTo(_mixedBoundaries);
    }

    [Test]
    public async Task FrameIndices_TreatsThePreGameSentinelAsARealTick()
    {
        // The sentinel is a legal frame-clock value: frame 0 opens a boundary and the sentinel run
        // collapses to it. A port that compared against a magic "unset" tick (0 or -1) would either
        // drop frame 0's boundary or emit one per sentinel frame.
        int[] boundaries = TickBoundaries.FrameIndices(Frames(_sentinelRunTicks));

        await Assert.That(boundaries).IsEquivalentTo(_sentinelRunBoundaries);
    }

    [Test]
    public async Task FrameIndices_EmptyFrameList_ReturnsEmpty()
    {
        await Assert.That(TickBoundaries.FrameIndices([])).IsEmpty();
    }

    [Test]
    public async Task FrameIndices_EveryFrameItsOwnTick_ReturnsEveryIndex()
    {
        await Assert.That(TickBoundaries.FrameIndices(Frames(_distinctTicks)))
            .IsEquivalentTo(_distinctBoundaries);
    }
}
