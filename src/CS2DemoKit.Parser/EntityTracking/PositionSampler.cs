#region

using System.Numerics;
using CS2DemoKit.Parser.Entities;

#endregion

namespace CS2DemoKit.Parser.EntityTracking;

/// <summary>
///     One player's world position at one frame, with the nav place they were last inside.
/// </summary>
/// <param name="FrameIndex">0-based position in the frame list the sample came from.</param>
/// <param name="Tick">The frame's server tick. Not unique: several frames can share one.</param>
/// <param name="PlayerSlot">Controller-derived player slot, 0-63.</param>
/// <param name="Position">World position, reconstructed by <see cref="PositionUtil.CellToWorld" />.</param>
/// <param name="Place">
///     <c>m_szLastPlaceName</c>, e.g. <c>BombsiteA</c>. Null on maps with no named nav areas, and
///     before the field is first networked for the pawn.
/// </param>
public readonly record struct PositionSample(
    int FrameIndex,
    int Tick,
    int PlayerSlot,
    Vector3 Position,
    string? Place);

/// <summary>
///     Streams every player's position over a whole demo. Assembles the four pieces a trajectory
///     walk needs (incremental advance, live-pawn enumeration, slot resolution, cell→world
///     reconstruction) so consumers do not re-derive them.
///     <para>
///         Sized on a 223,628-frame match (Release, parse excluded): 1,635,249 samples in 3.6 s,
///         ~50 MB if collected. <see cref="Walk" /> is lazy, so a consumer that
///         folds rather than collects pays the time and not the memory.
///     </para>
/// </summary>
public static class PositionSampler
{
    /// <summary>
    ///     Positions for every live pawn on every <paramref name="frameStride" />-th frame, in
    ///     frame order then entity order. Lazy: nothing is decoded until enumerated, and the walk
    ///     restarts from frame 0 on a second enumeration.
    ///     <para>
    ///         <paramref name="frameStride" /> subsamples the <b>output</b> only. Every frame is
    ///         still decoded, because entity state is delta-encoded and skipping a frame's deltas
    ///         corrupts the ones after it. So the stride buys memory and downstream work, not
    ///         decode time. It is a frame stride and not a tick stride: several frames can carry
    ///         the same server tick, so a stride of N is not N ticks.
    ///     </para>
    ///     <para>
    ///         At 64 tick a player covers roughly 4 units per frame, so a stride of 8 draws a path
    ///         to about 32 units and costs an eighth of the points. Measured, that stride cut wall
    ///         time from 3.6 s to 2.3 s on a 223,628-frame match: the remainder is the decode floor.
    ///     </para>
    ///     <para>
    ///         <paramref name="maxFrames" /> stops the walk early. There is deliberately no way to
    ///         start it late: entity state is delta-encoded, so a walk that begins anywhere but
    ///         frame 0 reports positions built on deltas it never saw. To get one round, walk from
    ///         the start and filter the samples by <see cref="PositionSample.Tick" />.
    ///     </para>
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    ///     <paramref name="frameStride" /> is below 1, or <paramref name="maxFrames" /> is negative.
    /// </exception>
    public static IEnumerable<PositionSample> Walk(ParsedDemo demo, int frameStride = 1,
        int maxFrames = int.MaxValue)
    {
        ArgumentNullException.ThrowIfNull(demo);
        ArgumentOutOfRangeException.ThrowIfLessThan(frameStride, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(maxFrames);
        return Iterate(demo.Frames, frameStride, maxFrames);
    }

    private static IEnumerable<PositionSample> Iterate(IReadOnlyList<DemoFrame> frames, int frameStride,
        int maxFrames)
    {
        EntityTracker tracker = EntityTrackerFactory.CreateCurated();

        // ForEachLivePawn takes a callback and an iterator cannot yield from one, so pawns land
        // in the buffer first. Collect captures, so the conversion is hoisted rather than repeated
        // per frame.
        List<PositionSample> buffer = [];
        int frameIndex = 0;
        int tick = 0;

        void Collect(int slot, EntityState pawn)
        {
            if (PositionUtil.CellToWorld(pawn) is not { } position)
            {
                return;
            }

            buffer.Add(new PositionSample(frameIndex, tick, slot, position,
                pawn["m_szLastPlaceName"] as string));
        }

        Action<int, EntityState> collect = Collect;

        int limit = Math.Min(maxFrames, frames.Count);
        for (int i = 0; i < limit; i++)
        {
            DemoFrame frame = frames[i];
            tracker.AdvanceOneFrame(frame);

            if (i % frameStride != 0)
            {
                continue;
            }

            frameIndex = i;
            tick = frame.ServerTick;
            buffer.Clear();
            PawnLookup.ForEachLivePawn(tracker, collect);

            foreach (PositionSample sample in buffer)
            {
                yield return sample;
            }
        }
    }
}
