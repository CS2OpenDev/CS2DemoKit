namespace CS2DemoKit.Parser;

/// <summary>
///     Tick-boundary indexing over a parsed demo's frame list — the precompute
///     <see cref="TickMapper" /> binary-searches (and the App's semantic navigation seeks over).
///     <para>
///         A demo frame's <see cref="DemoFrame.ServerTick" /> is the <b>demo/frame clock</b>:
///         pre-game frames carry a large negative sentinel and gameplay frames run 1, 2, … Several
///         consecutive frames can share one tick, so "the frame that carries the state visible at
///         tick T" is the FIRST frame of that tick — which is what a boundary index is.
///     </para>
/// </summary>
public static class TickBoundaries
{
    /// <summary>
    ///     The index of the first frame of each distinct <see cref="DemoFrame.ServerTick" />, in
    ///     frame order (so ascending, and — because the frame clock is non-decreasing — ascending
    ///     by tick too). One linear pass; feed the result straight to the
    ///     <see cref="TickMapper" /> constructor.
    ///     <para>
    ///         Ticks are compared for CHANGE, never against a magic "unset" value: the pre-game
    ///         sentinel is a legal <see cref="DemoFrame.ServerTick" />, so frame 0 always opens a
    ///         boundary and a run of sentinel frames collapses to that one boundary.
    ///     </para>
    /// </summary>
    /// <param name="frames">The demo's frame list (<see cref="ParsedDemo.Frames" />).</param>
    /// <returns>Boundary frame indices; empty when <paramref name="frames" /> is empty.</returns>
    public static int[] FrameIndices(IReadOnlyList<DemoFrame> frames)
    {
        ArgumentNullException.ThrowIfNull(frames);

        List<int> boundaries = [];
        int lastTick = int.MinValue;
        bool haveTick = false;

        for (int i = 0; i < frames.Count; i++)
        {
            int serverTick = frames[i].ServerTick;
            if (!haveTick || serverTick != lastTick)
            {
                boundaries.Add(i);
                lastTick = serverTick;
                haveTick = true;
            }
        }

        return [.. boundaries];
    }
}
