#region

using System.Numerics;
using CS2DemoKit.Parser.Entities;
using CS2DemoKit.Parser.EntityTracking;
using CS2DemoKit.TestSupport;
using TUnit.Core.Exceptions;

#endregion

namespace CS2DemoKit.Parser.Tests.EntityTracking;

/// <summary>
///     Gate for <see cref="PositionSampler" />: the packaged trajectory walk must produce exactly
///     what the four-piece hand-rolled version produces, and striding must subsample the output
///     without disturbing the decode.
/// </summary>
[Category("Integration")]
[NotInParallel]
public class PositionSamplerTests
{
    /// <summary>
    ///     Frames to walk. Two independent walks per test, so this caps the walk rather than
    ///     covering the whole demo. Long enough to clear the schema-bootstrap region and reach
    ///     live play.
    /// </summary>
    private const int MaxFrames = 6000;

    /// <summary>
    ///     The stride must change which samples come out and nothing else. If it skipped decode
    ///     instead of skipping emission, entity deltas would be lost and the positions on the
    ///     surviving frames would drift from the full-fidelity walk.
    /// </summary>
    [Test]
    public async Task Walk_WithStride_IsTheFullWalkSubsampled()
    {
        ParsedDemo demo = Demo();
        const int stride = 8;

        List<PositionSample> full = PositionSampler.Walk(demo, maxFrames: MaxFrames).ToList();
        List<PositionSample> strided = PositionSampler.Walk(demo, stride, MaxFrames).ToList();

        List<PositionSample> expected = full.Where(s => s.FrameIndex % stride == 0).ToList();

        await Assert.That(strided.Count).IsEqualTo(expected.Count)
            .Because($"stride {stride} must drop only the frames it skips, not change decode");
        await Assert.That(strided).IsEquivalentTo(expected);

        Console.WriteLine($"[sampler] {MaxFrames} frames: {full.Count} samples, "
                          + $"{strided.Count} at stride {stride}");
    }

    /// <summary>
    ///     The sampler is the four pieces assembled: incremental advance, live-pawn enumeration,
    ///     slot resolution, cell→world. Rolling them by hand must land on the same stream.
    /// </summary>
    [Test]
    public async Task Walk_MatchesTheHandRolledFourPieceVersion()
    {
        ParsedDemo demo = Demo();
        IReadOnlyList<DemoFrame> frames = demo.Frames;

        List<PositionSample> packaged = PositionSampler.Walk(demo, maxFrames: MaxFrames).ToList();

        List<PositionSample> byHand = [];
        EntityTracker tracker = EntityTrackerFactory.CreateCurated();
        for (int i = 0; i < MaxFrames; i++)
        {
            tracker.AdvanceOneFrame(frames[i]);
            int frameIndex = i;
            int tick = frames[i].ServerTick;
            PawnLookup.ForEachLivePawn(tracker, (slot, pawn) =>
            {
                if (PositionUtil.CellToWorld(pawn) is { } p)
                {
                    byHand.Add(new PositionSample(frameIndex, tick, slot, p,
                        pawn["m_szLastPlaceName"] as string));
                }
            });
        }

        await Assert.That(packaged.Count).IsEqualTo(byHand.Count);
        await Assert.That(packaged).IsEquivalentTo(byHand);
    }

    /// <summary>Slots come from the controller index, so an out-of-range one is a decode bug.</summary>
    [Test]
    public async Task Walk_EmitsPlausibleSlotsPositionsAndPlaces()
    {
        List<PositionSample> samples = PositionSampler.Walk(Demo(), 16, MaxFrames).ToList();

        if (samples.Count == 0)
        {
            throw new SkipTestException("no live pawn in the walked frames, nothing to assert on");
        }

        await Assert.That(samples.All(s => s.PlayerSlot is >= 0 and < 64)).IsTrue()
            .Because("slots are controller-derived and must land in 0-63");

        // WorldHalfExtent is the encoding's own bound, so a position outside it means the cell
        // reconstruction is wrong rather than the map being large.
        const float bound = PositionUtil.WorldHalfExtent + PositionUtil.CellWidth;
        await Assert.That(samples.All(s => InBounds(s.Position, bound))).IsTrue()
            .Because("reconstructed positions must sit inside the cell grid");

        int placed = samples.Count(s => !string.IsNullOrEmpty(s.Place));
        Console.WriteLine($"[sampler] {samples.Count} samples, {samples.Select(s => s.PlayerSlot).Distinct().Count()} "
                          + $"slots, place set on {placed * 100.0 / samples.Count:F1}%");
    }

    [Test]
    public void Walk_RejectsAStrideBelowOne()
    {
        ParsedDemo demo = Demo();

        // Never enumerated. Validation belongs at the call and not at the first MoveNext, which is
        // why Walk is a plain method wrapping a private iterator.
        Assert.Throws<ArgumentOutOfRangeException>(() => PositionSampler.Walk(demo, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => PositionSampler.Walk(demo, 1, -1));
    }

    private static bool InBounds(Vector3 v, float bound) =>
        Math.Abs(v.X) <= bound && Math.Abs(v.Y) <= bound && Math.Abs(v.Z) <= bound;

    private static ParsedDemo Demo() => DemoTestHelper.GetOrParse(DemoTestHelper.RequireDemo());
}
