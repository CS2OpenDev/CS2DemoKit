#region

using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Parser;

using CS2OpenSchema.Protos;

#endregion

namespace CS2DemoKit.Analysis.Tests;

/// <summary>
///     Checkpoint selection in the pipelined producer.
///     <para>
///         <c>EntityStateLayer.PrimeFromCheckpoint</c> cannot represent a <c>DEM_FullPacket</c> that shares
///         its tick with the frame after it: priming leaves the layer at the checkpoint tick, so the
///         worker's first tick-gated apply skips that successor's delta, while a sequential decode folds
///         it into the very same frame. It throws rather than diverge silently, so the reader must never
///         close a chunk on one.
///     </para>
///     <para>
///         Synthetic frame lists rather than a demo: the shape being pinned is a property of the frame
///         sequence, and every real demo in the corpus carries exactly one same-tick full packet (the
///         signon one at tick 1, which is <c>F_0</c> and excluded for a different reason already). A gate
///         that needed a demo to exhibit the shape could not run at all. The producer runs for real over
///         them, folding frames that carry no messages, and the chunks it closed are read back.
///     </para>
/// </summary>
[Category("Unit")]
public class PipelinedChunkPlanningTests
{
    /// <summary>
    ///     The whole point: with half the full packets carrying a same-tick successor, wherever the
    ///     minimum chunk size lands, nothing selected may be one of them.
    /// </summary>
    [Test]
    public async Task Reader_NeverClosesOnAFullPacketWithASameTickSuccessor()
    {
        List<DemoFrame> frames = BuildFrames(12, 16, f => f % 2 == 1);
        IReadOnlyList<PipelinedDigestSource.ChunkSpan> spans = Plan(frames, 1);

        await AssertWellFormed(frames, spans);
        await AssertNoCheckpointClashes(frames, spans);
        await Assert.That(spans.Count).IsGreaterThan(1)
            .Because("clash-free candidates remain, so the plan should still split");
    }

    /// <summary>
    ///     The same property once the minimum chunk size actually bites. With a chunk spanning
    ///     several full-packet periods the reader passes candidates it could have closed on, so the
    ///     check runs against whichever candidate happens to be the first past the minimum, which is
    ///     where a clash next to that position would hide.
    /// </summary>
    [Test]
    public async Task Reader_SkipsClashes_WhenTheChunkSpansSeveralPeriods()
    {
        const int period = 8;
        List<DemoFrame> frames = BuildFrames(36, period, f => f % 3 == 0);

        IReadOnlyList<PipelinedDigestSource.ChunkSpan> spans = Plan(frames, (2 * period) + 1);

        int candidates = FullPacketIndices(frames).Skip(1)
            .Count(i => i + 1 >= frames.Count || frames[i + 1].ServerTick != frames[i].ServerTick);
        int picked = spans.Count(s => s.CheckpointFrameIndex >= 0);
        Console.WriteLine($"fullPackets=36 candidates={candidates} picked={picked}");

        await AssertWellFormed(frames, spans);
        await AssertNoCheckpointClashes(frames, spans);
        await Assert.That(picked).IsLessThan(candidates)
            .Because("the minimum has to actually pass candidates for this gate to mean anything");
        await Assert.That(picked).IsGreaterThan(1);
    }

    /// <summary>
    ///     The exact regression pin. A clash on <c>F_1</c> used to be selected regardless. Selection
    ///     must step past it to the next clash-free full packet.
    /// </summary>
    [Test]
    public async Task Reader_StepsPastTheFirstCandidate_WhenItClashes()
    {
        List<DemoFrame> frames = BuildFrames(8, 16, f => f == 1);
        int[] full = FullPacketIndices(frames);

        IReadOnlyList<PipelinedDigestSource.ChunkSpan> spans = Plan(frames, 1);

        await AssertWellFormed(frames, spans);
        await Assert.That(spans[1].CheckpointFrameIndex).IsNotEqualTo(full[1])
            .Because("F_1 carries a same-tick successor and used to be picked unconditionally");
        await Assert.That(spans[1].CheckpointFrameIndex).IsEqualTo(full[2]);
    }

    /// <summary>
    ///     Every candidate clashing leaves nothing to split on. One from-scratch chunk is the sequential
    ///     mechanism, which is slower but always correct, and beats closing a chunk the prime rejects.
    /// </summary>
    [Test]
    public async Task Reader_FoldsOneChunk_WhenEveryCandidateClashes()
    {
        List<DemoFrame> frames = BuildFrames(8, 16, f => f >= 1);
        IReadOnlyList<PipelinedDigestSource.ChunkSpan> spans = Plan(frames, 1);

        await AssertWellFormed(frames, spans);
        await Assert.That(spans.Count).IsEqualTo(1);
        await Assert.That(spans[0].CheckpointFrameIndex).IsEqualTo(-1);
        await Assert.That(spans[0].FrameCount).IsEqualTo(frames.Count);
    }

    /// <summary>
    ///     A clash-free demo must plan exactly as the rule reads: with a minimum of one frame every full
    ///     packet after <c>F_0</c> closes a chunk, in order, starting with <c>F_1</c>.
    /// </summary>
    [Test]
    public async Task Reader_ClosesOnEveryFullPacketAfterTheFirst_WhenNothingClashes()
    {
        List<DemoFrame> frames = BuildFrames(12, 16, _ => false);
        int[] full = FullPacketIndices(frames);

        IReadOnlyList<PipelinedDigestSource.ChunkSpan> spans = Plan(frames, 1);

        await AssertWellFormed(frames, spans);
        await Assert.That(spans[1].CheckpointFrameIndex).IsEqualTo(full[1])
            .Because("with no clashes the first candidate is still F_1");

        int[] picked = [.. spans.Where(s => s.CheckpointFrameIndex >= 0).Select(s => s.CheckpointFrameIndex)];
        await Assert.That(picked).IsEquivalentTo(full.Skip(1).ToArray())
            .Because("every full packet after F_0 is a checkpoint, in ascending order");
    }

    /// <summary>
    ///     Runs the producer over <paramref name="frames" /> with no providers and hands back the chunks
    ///     the reader closed. The fold is real: each chunk primes a tracker from its checkpoint and applies
    ///     its frames, which carry nothing, so the plan is judged on the production reader rather than a
    ///     re-implementation of its rule.
    /// </summary>
    private static IReadOnlyList<PipelinedDigestSource.ChunkSpan> Plan(List<DemoFrame> frames, int minChunkFrames)
    {
        EntityFrameDigest[] digests = PipelinedDigests.Produce(
            new FrameListSource(frames, null), () => [], () => [], false,
            out IReadOnlyList<PipelinedDigestSource.ChunkSpan> spans, minChunkFrames: minChunkFrames);
        if (digests.Length != frames.Count)
        {
            throw new InvalidOperationException($"the producer yielded {digests.Length} digests for {frames.Count} frames");
        }

        return spans;
    }

    /// <summary>
    ///     Structural invariants every plan owes its consumer: chunk 0 decodes from scratch, every other
    ///     chunk opens at the tick-run start of its own checkpoint, and together they tile the frame list
    ///     with no gap or overlap (the consumer takes digests by frame index from whichever chunk it is in,
    ///     so a gap is a frame no chunk folded).
    /// </summary>
    private static async Task AssertWellFormed(
        List<DemoFrame> frames, IReadOnlyList<PipelinedDigestSource.ChunkSpan> spans)
    {
        await Assert.That(spans.Count).IsGreaterThan(0);
        await Assert.That(spans[0].FirstFrameIndex).IsEqualTo(0);
        await Assert.That(spans[0].CheckpointFrameIndex).IsEqualTo(-1);
        await Assert.That(spans[^1].FirstFrameIndex + spans[^1].FrameCount).IsEqualTo(frames.Count);

        for (int i = 0; i < spans.Count; i++)
        {
            await Assert.That(spans[i].FrameCount).IsGreaterThan(0);
            if (i == 0)
            {
                continue;
            }

            await Assert.That(spans[i].FirstFrameIndex).IsEqualTo(spans[i - 1].FirstFrameIndex + spans[i - 1].FrameCount);
            int cp = spans[i].CheckpointFrameIndex;
            await Assert.That(cp).IsGreaterThanOrEqualTo(spans[i].FirstFrameIndex);
            await Assert.That(cp).IsLessThan(spans[i].FirstFrameIndex + spans[i].FrameCount);
            for (int f = spans[i].FirstFrameIndex; f < cp; f++)
            {
                await Assert.That(frames[f].ServerTick).IsEqualTo(frames[cp].ServerTick)
                    .Because("a chunk opens at the start of its checkpoint's tick run");
            }
        }
    }

    /// <summary>
    ///     No selected checkpoint may be a full packet with a same-tick successor. The bounds guard
    ///     mirrors the reader's: a checkpoint at the very last frame has no successor to clash with.
    /// </summary>
    private static async Task AssertNoCheckpointClashes(
        List<DemoFrame> frames, IReadOnlyList<PipelinedDigestSource.ChunkSpan> spans)
    {
        foreach (PipelinedDigestSource.ChunkSpan span in spans.Where(s => s.CheckpointFrameIndex >= 0))
        {
            int cp = span.CheckpointFrameIndex;
            await Assert.That(frames[cp].CommandKind).IsEqualTo(EDemoCommands.DemFullPacket);
            if (cp + 1 < frames.Count)
            {
                await Assert.That(frames[cp + 1].ServerTick).IsNotEqualTo(frames[cp].ServerTick)
                    .Because($"checkpoint frame {cp} (tick {frames[cp].ServerTick}) has a same-tick successor");
            }
        }
    }

    private static int[] FullPacketIndices(List<DemoFrame> frames) =>
        [.. Enumerable.Range(0, frames.Count).Where(i => frames[i].CommandKind == EDemoCommands.DemFullPacket)];

    /// <summary>
    ///     A frame list shaped like a GOTV recording: a two-frame signon prefix, then a
    ///     <c>DEM_FullPacket</c> every <paramref name="period" /> ticks with ordinary packets between.
    ///     <paramref name="clashAt" /> selects, by full-packet ordinal, which ones get a same-tick
    ///     <c>DEM_Packet</c> immediately after them.
    /// </summary>
    private static List<DemoFrame> BuildFrames(int fullPacketCount, int period, Func<int, bool> clashAt)
    {
        List<DemoFrame> frames = [];

        void Add(EDemoCommands command, int tick) => frames.Add(new DemoFrame
        {
            CommandKind = command,
            FrameNumber = frames.Count,
            ServerTick = tick,
            RawStart = 0,
            RawLength = 1,
            HeaderLength = 1,
            IsCompressed = false
        });

        Add(EDemoCommands.DemFileHeader, -1);
        Add(EDemoCommands.DemPacket, 0); // first DEM_Packet: the schema prefix ends here

        for (int f = 0; f < fullPacketCount; f++)
        {
            // Real full packets sit at ticks 1, 1+period, 1+2*period, ... which is the cadence the
            // corpus shows (every one on the (tick-1) % 3840 lattice at 64 tick).
            int tick = 1 + (f * period);
            Add(EDemoCommands.DemFullPacket, tick);
            if (clashAt(f))
            {
                Add(EDemoCommands.DemPacket, tick);
            }

            for (int i = 1; i < period; i++)
            {
                Add(EDemoCommands.DemPacket, tick + i);
            }
        }

        return frames;
    }
}
