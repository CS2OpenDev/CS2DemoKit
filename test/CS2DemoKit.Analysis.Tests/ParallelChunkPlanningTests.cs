#region

using CS2DemoKit.Parser;

#endregion

namespace CS2DemoKit.Analysis.Tests;

/// <summary>
///     Checkpoint selection for the parallel decode.
///     <para>
///         <c>EntityStateLayer.PrimeFromCheckpoint</c> cannot represent a <c>DEM_FullPacket</c> that shares
///         its tick with the frame after it: priming leaves the layer at the checkpoint tick, so the
///         worker's first <c>SeekToTick</c> early-returns and that successor's delta never lands, while a
///         sequential decode folds it into the very same frame. It throws rather than diverge silently, so
///         <see cref="ParallelDigestProducer.PlanChunks" /> must never hand it one.
///     </para>
///     <para>
///         Synthetic frame lists rather than a demo: the shape being pinned is a property of the frame
///         sequence, and every real demo in the corpus carries exactly one same-tick full packet (the
///         signon one at tick 1, which is <c>F_0</c> and excluded for a different reason already). A gate
///         that needed a demo to exhibit the shape could not run at all.
///     </para>
/// </summary>
[Category("Unit")]
public class ParallelChunkPlanningTests
{
    /// <summary>
    ///     The whole point: with half the full packets carrying a same-tick successor, no matter where the
    ///     coarsening stride lands, nothing selected may be one of them.
    /// </summary>
    [Test]
    public async Task PlanChunks_NeverPicksAFullPacketWithASameTickSuccessor()
    {
        List<DemoFrame> frames = BuildFrames(12, 16, f => f % 2 == 1);
        IReadOnlyList<ParallelDigestProducer.Chunk> chunks =
            ParallelDigestProducer.PlanChunks(frames, out _);

        await AssertWellFormed(frames, chunks);
        await Assert.That(chunks.Count).IsGreaterThan(1)
            .Because("six clash-free candidates remain, so the plan should still split");

        foreach (ParallelDigestProducer.Chunk chunk in chunks.Where(c => c.CheckpointFrameIndex >= 0))
        {
            int cp = chunk.CheckpointFrameIndex;
            await Assert.That(frames[cp].Command).IsEqualTo("DEM_FullPacket");
            await Assert.That(frames[cp + 1].ServerTick).IsNotEqualTo(frames[cp].ServerTick)
                .Because($"checkpoint frame {cp} (tick {frames[cp].ServerTick}) has a same-tick successor");
        }
    }

    /// <summary>
    ///     The exact regression pin. The stride always started at the first candidate, so a clash on
    ///     <c>F_1</c> was selected every time regardless of core count. Selection must now step past it.
    /// </summary>
    [Test]
    public async Task PlanChunks_StepsPastTheFirstCandidate_WhenItClashes()
    {
        List<DemoFrame> frames = BuildFrames(8, 16, f => f == 1);
        int[] full = FullPacketIndices(frames);

        IReadOnlyList<ParallelDigestProducer.Chunk> chunks =
            ParallelDigestProducer.PlanChunks(frames, out _);

        await AssertWellFormed(frames, chunks);
        await Assert.That(chunks[1].CheckpointFrameIndex).IsNotEqualTo(full[1])
            .Because("F_1 carries a same-tick successor and used to be picked unconditionally");
        await Assert.That(chunks[1].CheckpointFrameIndex).IsEqualTo(full[2]);
    }

    /// <summary>
    ///     Every candidate clashing leaves nothing to split on. One from-scratch chunk is the sequential
    ///     mechanism, which is slower but always correct, and beats planning a chunk the prime rejects.
    /// </summary>
    [Test]
    public async Task PlanChunks_FallsBackToASingleChunk_WhenEveryCandidateClashes()
    {
        List<DemoFrame> frames = BuildFrames(8, 16, f => f >= 1);
        IReadOnlyList<ParallelDigestProducer.Chunk> chunks =
            ParallelDigestProducer.PlanChunks(frames, out _);

        await AssertWellFormed(frames, chunks);
        await Assert.That(chunks.Count).IsEqualTo(1);
        await Assert.That(chunks[0].CheckpointFrameIndex).IsEqualTo(-1);
        await Assert.That(chunks[0].End).IsEqualTo(frames.Count);
    }

    /// <summary>
    ///     A clash-free demo must plan exactly as it did before the filter went in: the candidate list is
    ///     then every full packet after <c>F_0</c>, in order, so the stride sees an unchanged population.
    /// </summary>
    [Test]
    public async Task PlanChunks_IsUnchangedWhenNothingClashes()
    {
        List<DemoFrame> frames = BuildFrames(12, 16, _ => false);
        int[] full = FullPacketIndices(frames);

        IReadOnlyList<ParallelDigestProducer.Chunk> chunks =
            ParallelDigestProducer.PlanChunks(frames, out _);

        await AssertWellFormed(frames, chunks);
        await Assert.That(chunks[1].CheckpointFrameIndex).IsEqualTo(full[1])
            .Because("with no clashes the first candidate is still F_1");

        // Every checkpoint is a full packet after F_0, and they arrive in ascending order.
        int[] picked = [.. chunks.Where(c => c.CheckpointFrameIndex >= 0).Select(c => c.CheckpointFrameIndex)];
        await Assert.That(picked.SequenceEqual(picked.OrderBy(x => x))).IsTrue();
        await Assert.That(picked.All(p => full.Skip(1).Contains(p))).IsTrue();
    }

    /// <summary>
    ///     Structural invariants every plan owes its callers: chunk 0 decodes from scratch, every other
    ///     chunk starts at its own checkpoint, and together they tile the frame list with no gap or overlap
    ///     (workers write disjoint slices of one shared digest array, so a gap silently leaves nulls).
    /// </summary>
    private static async Task AssertWellFormed(
        List<DemoFrame> frames, IReadOnlyList<ParallelDigestProducer.Chunk> chunks)
    {
        await Assert.That(chunks.Count).IsGreaterThan(0);
        await Assert.That(chunks[0].Start).IsEqualTo(0);
        await Assert.That(chunks[0].CheckpointFrameIndex).IsEqualTo(-1);
        await Assert.That(chunks[^1].End).IsEqualTo(frames.Count);

        for (int i = 0; i < chunks.Count; i++)
        {
            await Assert.That(chunks[i].End).IsGreaterThan(chunks[i].Start);
            if (i > 0)
            {
                await Assert.That(chunks[i].Start).IsEqualTo(chunks[i - 1].End);
                await Assert.That(chunks[i].CheckpointFrameIndex).IsEqualTo(chunks[i].Start);
            }
        }
    }

    private static int[] FullPacketIndices(List<DemoFrame> frames) =>
        [.. Enumerable.Range(0, frames.Count).Where(i => frames[i].Command == "DEM_FullPacket")];

    /// <summary>
    ///     A frame list shaped like a GOTV recording: a two-frame signon prefix, then a
    ///     <c>DEM_FullPacket</c> every <paramref name="period" /> ticks with ordinary packets between.
    ///     <paramref name="clashAt" /> selects, by full-packet ordinal, which ones get a same-tick
    ///     <c>DEM_Packet</c> immediately after them.
    /// </summary>
    private static List<DemoFrame> BuildFrames(int fullPacketCount, int period, Func<int, bool> clashAt)
    {
        List<DemoFrame> frames = [];

        void Add(string command, int tick) => frames.Add(new DemoFrame
        {
            Command = command,
            FrameNumber = frames.Count,
            ServerTick = tick,
            RawStart = 0,
            RawLength = 1,
            HeaderLength = 1,
            IsCompressed = false
        });

        Add("DEM_FileHeader", -1);
        Add("DEM_Packet", 0); // first DEM_Packet: schemaPrefixEnd lands here

        for (int f = 0; f < fullPacketCount; f++)
        {
            // Real full packets sit at ticks 1, 1+period, 1+2*period, ... which is the cadence the
            // corpus shows (every one on the (tick-1) % 3840 lattice at 64 tick).
            int tick = 1 + (f * period);
            Add("DEM_FullPacket", tick);
            if (clashAt(f))
            {
                Add("DEM_Packet", tick);
            }

            for (int i = 1; i < period; i++)
            {
                Add("DEM_Packet", tick + i);
            }
        }

        return frames;
    }
}
