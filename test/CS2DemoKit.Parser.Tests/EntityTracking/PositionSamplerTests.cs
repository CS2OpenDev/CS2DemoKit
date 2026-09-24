#region

using System.Numerics;
using CS2DemoKit.Parser.Entities;
using CS2DemoKit.Parser.EntityTracking;
using CS2DemoKit.Parser.GameEvents;
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
    ///     The sampler is the four pieces assembled: incremental advance, controller-bound pawn
    ///     enumeration, slot resolution, cell→world, plus the team and alive reads. Rolling them
    ///     by hand must land on the same stream.
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
                    // Team and alive written out from the raw fields rather than through
                    // PawnLookup.IsAlive, so this stays an independent oracle for the rule.
                    bool alive = pawn.TryGet<int>("m_lifeState") is 0 && pawn.TryGet<int>("m_iHealth") is > 0;
                    byHand.Add(new PositionSample(frameIndex, tick, slot, p,
                        pawn["m_szLastPlaceName"] as string, pawn.TryGet<int>("m_iTeamNum") ?? 0, alive));
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

    /// <summary>
    ///     Issue #58: the walk yields dead pawns, and <see cref="PositionSample.IsAlive" /> is what
    ///     tells them apart. Over the whole sample: some samples are dead, every team is T or CT
    ///     with both present, and no pawn reads alive on or after the frame carrying its
    ///     <c>player_death</c> until a <c>player_spawn</c> brings it back. Only that direction is
    ///     asserted: the trimmed sample starts without spawn events, so pawns alive before any
    ///     spawn has been seen are expected.
    /// </summary>
    [Test]
    public async Task Walk_TagsDeadPawnsAndTheirTeam()
    {
        ParsedDemo demo = Demo();
        List<PositionSample> samples = PositionSampler.Walk(demo).ToList();

        int dead = samples.Count(s => !s.IsAlive);
        await Assert.That(dead).IsGreaterThan(0)
            .Because("a dead player's pawn keeps sampling for the rest of the round");
        // Without this, an IsAlive stuck at false would pass: it is never alive after a death.
        await Assert.That(samples.Count - dead).IsGreaterThan(samples.Count / 2)
            .Because("players spend most of a round alive, so alive samples are the majority");
        await Assert.That(samples.All(s => s.Team is 2 or 3)).IsTrue()
            .Because("a controller-bound player pawn is on T or CT");
        await Assert.That(samples.Select(s => s.Team).Distinct().Count()).IsEqualTo(2);

        int violations = AliveAfterDeath(demo, samples);
        await Assert.That(violations).IsEqualTo(0)
            .Because("no pawn reads alive on or after the frame that carries its player_death");

        Console.WriteLine($"[sampler] {samples.Count} samples, {dead} dead");
    }

    /// <summary>
    ///     Issue #58: <see cref="PositionSample.Tick" /> is the frame clock. At every
    ///     <c>player_death</c>, the samples on the event's frame carry that frame's tick, the event's
    ///     GameTick is that tick or one below it, and ServerTick is GameTick plus
    ///     <see cref="ParsedDemo.ServerStartTick" />.
    /// </summary>
    [Test]
    public async Task Walk_TickIsTheFrameClock()
    {
        ParsedDemo demo = Demo();
        Dictionary<int, int> tickByFrame = PositionSampler.Walk(demo)
            .GroupBy(s => s.FrameIndex)
            .ToDictionary(g => g.Key, g => g.Select(s => s.Tick).Distinct().Single());

        // The pre-recording -1 frames carry no pawns, so no sample sits below 0.
        await Assert.That(tickByFrame.Values.Min()).IsGreaterThanOrEqualTo(0);

        List<GameEvent> deaths = demo.AllGameEvents.Where(e => e.Name == "player_death").ToList();
        await Assert.That(deaths.Count).IsGreaterThan(0);

        foreach (GameEvent death in deaths)
        {
            int frameTick = demo.Frames[death.FrameNumber].ServerTick;
            await Assert.That(tickByFrame[death.FrameNumber]).IsEqualTo(frameTick);
            await Assert.That(death.GameTick == frameTick || death.GameTick == frameTick - 1).IsTrue()
                .Because($"GameTick {death.GameTick} is on the frame clock (frame tick {frameTick})");
            await Assert.That(death.ServerTick - death.GameTick).IsEqualTo(demo.ServerStartTick);
        }
    }

    /// <summary>
    ///     Samples that read alive at or after their slot's latest <c>player_death</c> frame with no
    ///     <c>player_spawn</c> since, replaying the events by frame number.
    /// </summary>
    internal static int AliveAfterDeath(ParsedDemo demo, IEnumerable<PositionSample> samples)
    {
        List<GameEvent> lifecycle = demo.AllGameEvents
            .Where(e => e.Payload is PlayerDeathEvent or PlayerSpawnEvent)
            .OrderBy(e => e.FrameNumber)
            .ToList();
        bool[] deadByEvent = new bool[64];
        int next = 0;
        int violations = 0;
        foreach (PositionSample s in samples)
        {
            while (next < lifecycle.Count && lifecycle[next].FrameNumber <= s.FrameIndex)
            {
                GameEvent e = lifecycle[next++];
                (int slot, bool died) = e.Payload switch
                {
                    PlayerDeathEvent d => (d.UserId, true),
                    PlayerSpawnEvent p => (p.UserId, false),
                    _ => (-1, false)
                };
                if (slot is >= 0 and < 64)
                {
                    deadByEvent[slot] = died;
                }
            }

            if (s.IsAlive && deadByEvent[s.PlayerSlot])
            {
                violations++;
            }
        }

        return violations;
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
