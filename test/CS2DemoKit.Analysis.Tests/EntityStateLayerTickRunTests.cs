#region

using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Parser;
using CS2OpenSchema.Protos;

#endregion

namespace CS2DemoKit.Analysis.Tests;

/// <summary>
///     The evaluator feeds a stream's frames to the scanner as tick runs, and the scanner holds
///     back a run whose tick is not past the layer's current tick. That must reproduce, frame for
///     frame, what the tick-gated seek over a list did: the negative-tick signon prefix and the
///     tick-zero frames are deferred until the first positive tick, a full packet and its same-tick
///     packet apply together, and nothing is ever applied twice.
/// </summary>
[Category("Unit")]
public class EntityStateLayerTickRunTests
{
    private static List<DemoFrame> Frames(params (EDemoCommands Command, int Tick)[] spec)
    {
        List<DemoFrame> frames = [];
        foreach ((EDemoCommands command, int tick) in spec)
        {
            frames.Add(new DemoFrame
            {
                CommandKind = command,
                FrameNumber = frames.Count,
                ServerTick = tick,
                GameTick = tick,
                RawStart = 0,
                RawLength = 1,
                HeaderLength = 1,
                IsCompressed = false
            });
        }

        return frames;
    }

    private static List<List<DemoFrame>> TickRuns(List<DemoFrame> frames)
    {
        List<List<DemoFrame>> runs = [];
        int i = 0;
        while (i < frames.Count)
        {
            List<DemoFrame> run = [frames[i]];
            int j = i + 1;
            while (j < frames.Count && frames[j].ServerTick == frames[i].ServerTick)
            {
                run.Add(frames[j++]);
            }

            runs.Add(run);
            i = j;
        }

        return runs;
    }

    // A signon prefix at the sentinel tick, tick-zero frames, a full packet with a same-tick
    // successor, and a run of three at one tick: every shape the tick gate has to get right.
    private static List<DemoFrame> SignonThenTicks()
    {
        const int sentinel = -1 - 20000;
        return Frames(
            (EDemoCommands.DemFileHeader, sentinel),
            (EDemoCommands.DemSendTables, sentinel),
            (EDemoCommands.DemClassInfo, sentinel),
            (EDemoCommands.DemSignonPacket, sentinel),
            (EDemoCommands.DemSignonPacket, sentinel),
            (EDemoCommands.DemSyncTick, 0),
            (EDemoCommands.DemPacket, 0),
            (EDemoCommands.DemPacket, 1),
            (EDemoCommands.DemPacket, 2),
            (EDemoCommands.DemFullPacket, 3),
            (EDemoCommands.DemPacket, 3),
            (EDemoCommands.DemPacket, 4),
            (EDemoCommands.DemPacket, 4),
            (EDemoCommands.DemPacket, 4),
            (EDemoCommands.DemPacket, 7),
            (EDemoCommands.DemFileInfo, 7));
    }

    // The tick-gated seek over the list, polled once per frame as the evaluator did.
    private static List<(int Tick, int Index)> SeekTrace(List<DemoFrame> frames)
    {
        EntityStateLayer seeking = new(frames);
        List<(int Tick, int Index)> trace = [];
        foreach (DemoFrame frame in frames)
        {
            seeking.SeekToTick(frame.ServerTick);
            trace.Add((seeking.Tracker.CurrentTick, seeking.Tracker.CurrentFrameIndex));
        }

        return trace;
    }

    [Test]
    public async Task RunDriven_AppliesTheSameFramesAsTheTickGatedSeek()
    {
        List<DemoFrame> frames = SignonThenTicks();
        List<(int Tick, int Index)> seekTrace = SeekTrace(frames);

        // Under test: the frame-free layer driven by runs, through the scanner's own rule.
        EntityStateLayer driven = new();
        EntityChangeScanner scanner = new(driven, []);
        scanner.BeginEvaluation(new FrameListSourceForTest(frames), 1, CancellationToken.None);
        List<(int Tick, int Index)> runTrace = [];
        foreach (List<DemoFrame> run in TickRuns(frames))
        {
            scanner.BeginTickRun(run);
            foreach (DemoFrame _ in run)
            {
                runTrace.Add((driven.Tracker.CurrentTick, driven.Tracker.CurrentFrameIndex));
            }
        }

        await Assert.That(runTrace.Count).IsEqualTo(seekTrace.Count);
        for (int i = 0; i < seekTrace.Count; i++)
        {
            await Assert.That(runTrace[i]).IsEqualTo(seekTrace[i]).Because($"after polling frame {i}");
        }

        // The sentinel and tick-zero frames were held until tick 1 and applied exactly then.
        await Assert.That(seekTrace[6].Index).IsEqualTo(-1).Because("nothing applied before the first positive tick");
        await Assert.That(seekTrace[7].Index).IsEqualTo(7);
        // The full packet and its same-tick packet applied together, visible to both frames.
        await Assert.That(seekTrace[9]).IsEqualTo((3, 10));
        await Assert.That(seekTrace[10]).IsEqualTo((3, 10));
        await Assert.That(seekTrace[^1]).IsEqualTo((7, 15));
    }

    // AdvanceAndPollAt is public and BeginTickRun is not, so a caller outside the evaluator has
    // only the poll to drive a scanner with. Over a list-backed layer it must seek as the
    // evaluator's runs do, frame for frame; a digest built from a never-advanced tracker is
    // empty and nothing says so.
    [Test]
    public async Task PolledOutsideAnEvaluation_SeeksItsOwnLayer()
    {
        List<DemoFrame> frames = SignonThenTicks();
        List<(int Tick, int Index)> seekTrace = SeekTrace(frames);

        EntityStateLayer own = new(frames);
        EntityChangeScanner scanner = new(own, []);
        List<(int Tick, int Index)> pollTrace = [];
        for (int i = 0; i < frames.Count; i++)
        {
            scanner.AdvanceAndPollAt(i, frames[i].ServerTick);
            pollTrace.Add((own.Tracker.CurrentTick, own.Tracker.CurrentFrameIndex));
        }

        await Assert.That(pollTrace.Count).IsEqualTo(seekTrace.Count);
        for (int i = 0; i < seekTrace.Count; i++)
        {
            await Assert.That(pollTrace[i]).IsEqualTo(seekTrace[i]).Because($"after polling frame {i}");
        }

        await Assert.That(own.Tracker.CurrentFrameIndex).IsEqualTo(15).Because("the poll applied every frame");
    }

    // A layer built without frames has no producer and no owner feeding it, so the poll cannot
    // advance it and must not hand back digests of nothing.
    [Test]
    public async Task PolledOutsideAnEvaluation_AFrameFreeLayerThrows()
    {
        EntityChangeScanner scanner = new(new EntityStateLayer(), []);
        Assert.Throws<InvalidOperationException>(() => scanner.AdvanceAndPollAt(0, 1));
        await Task.CompletedTask;
    }

    [Test]
    public async Task FrameFreeLayer_RefusesToSeek()
    {
        EntityStateLayer layer = new();
        await Assert.That(layer.HasFrames).IsFalse();
        Assert.Throws<InvalidOperationException>(() => layer.SeekToTick(5));
        Assert.Throws<InvalidOperationException>(() => layer.SeekBeforeFrame(5));
        await Assert.That(new EntityStateLayer(Frames((EDemoCommands.DemPacket, 1))).HasFrames).IsTrue();
    }

    // A source that reports no random access, so the scanner takes the sequential producer.
    private sealed class FrameListSourceForTest(IReadOnlyList<DemoFrame> frames) : IDemoFrameSource
    {
        private int _next;

        public IDemoEnrichmentView Enrichment => new ParsedDemo([], [], new Dictionary<int, PlayerInfo>(), null,
            "de_test", 0, 1f / 64, "", "", "csgo", 0, 0, 0, "", "", "", DemoProfile.Unknown).AsFrameSource().Enrichment;

        public int? FrameCount => null;

        public double? Progress => null;

        public bool SupportsRandomAccess => false;

        public IReadOnlyList<DemoFrame>? Frames => null;

        public IReadOnlyList<DemoFrame> SignonPrefix => [];

        public DemoFrame? LastInstanceBaselineFullPacket => null;

        public bool TryReadNext([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out DemoFrame? frame)
        {
            if (_next >= frames.Count)
            {
                frame = null;
                return false;
            }

            frame = frames[_next++];
            return true;
        }

        public bool TryPeekNext([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out DemoFrame? frame)
        {
            if (_next >= frames.Count)
            {
                frame = null;
                return false;
            }

            frame = frames[_next];
            return true;
        }
    }
}
