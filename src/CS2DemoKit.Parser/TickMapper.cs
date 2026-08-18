namespace CS2DemoKit.Parser;

/// <summary>
///     Maps between DV frame indices and CS2 demo ticks (docs/csvg-integration/
///     implementation-plan.md §6.3). Both sides run the demo/frame clock (plan §2 —
///     gameplay ticks ≈ 1-based <see cref="DemoFrame.ServerTick" />); <paramref name="tickOffset" />
///     is an identity shim, default 0, settings-overridable if Windows validation
///     ever finds a fixed skew. Immutable per loaded demo — build a new instance on demo change.
/// </summary>
/// <param name="frames">The demo's frame list (<c>ParsedDemo.Frames</c> / the shell's frame list).</param>
/// <param name="tickBoundaryFrames">
///     First frame index of each distinct <see cref="DemoFrame.ServerTick" />, sorted ascending —
///     <see cref="TickBoundaries.FrameIndices" />, precomputed once per demo. Used for O(log n)
///     tick→frame mapping instead of a linear scan of the frame list.
/// </param>
/// <param name="tickOffset">Added to DV ticks on the way out, subtracted on the way in.</param>
public sealed class TickMapper(
    IReadOnlyList<DemoFrame> frames,
    IReadOnlyList<int> tickBoundaryFrames,
    int tickOffset = 0)
{
    /// <summary>The demo's last CS2 demo tick — the §6.7 post-roll clamp bound.</summary>
    public long MaxCs2DemoTick => frames.Count == 0 ? 0 : Math.Max(0, frames[^1].ServerTick) + tickOffset;

    /// <summary>
    ///     The CS2 demo tick for a DV frame index. Pre-game frames carry a large negative
    ///     sentinel tick — clamped to 0 (demo start) because CS2's demo clock has no pre-game
    ///     negative range.
    /// </summary>
    public int Cs2DemoTick(int frameIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(frameIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(frameIndex, frames.Count);
        return Math.Max(0, frames[frameIndex].ServerTick) + tickOffset;
    }

    /// <summary>
    ///     The DV frame-clock tick for a CS2 demo tick (offset removed) — the drift servo's error
    ///     term compares this against <c>PlaybackController.CurrentTick</c> directly (§6.4).
    /// </summary>
    public long DvTick(long cs2DemoTick) => cs2DemoTick - tickOffset;

    /// <summary>
    ///     The CS2 demo tick for a DV frame-clock TICK (as opposed to a frame index —
    ///     <see cref="Cs2DemoTick" />): pre-game negative sentinel clamped, offset applied.
    ///     Event ticks (<c>RuleChainEvent.Tick</c>, <c>GameEvent.GameTick</c>) are already
    ///     frame clock (plan §2) — feed them here directly, never <c>−ServerStartTick</c>.
    /// </summary>
    public long Cs2TickFromDvTick(long dvTick) => Math.Max(0, dvTick) + tickOffset;

    /// <summary>
    ///     The DV frame index for a CS2 demo tick: the first frame of the last tick boundary at or
    ///     before the (offset-adjusted) tick — i.e. the frame that carries the demo state visible
    ///     at that tick. Ticks before the first boundary clamp to the first boundary frame; ticks
    ///     past the end clamp to the last. Returns 0 when the demo has no frames/boundaries.
    /// </summary>
    public int FrameIndexOf(int cs2DemoTick)
    {
        if (tickBoundaryFrames.Count == 0)
        {
            return 0;
        }

        int dvTick = cs2DemoTick - tickOffset;

        // Binary search over boundary FRAMES comparing their ServerTick (ascending along the
        // boundary list): find the last boundary with ServerTick <= dvTick.
        int lo = 0;
        int hi = tickBoundaryFrames.Count - 1;
        if (frames[tickBoundaryFrames[lo]].ServerTick > dvTick)
        {
            return tickBoundaryFrames[lo];
        }

        while (lo < hi)
        {
            int mid = lo + (hi - lo + 1 >> 1);
            if (frames[tickBoundaryFrames[mid]].ServerTick <= dvTick)
            {
                lo = mid;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return tickBoundaryFrames[lo];
    }
}
