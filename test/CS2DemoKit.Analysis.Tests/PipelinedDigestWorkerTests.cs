#region

using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Analysis.Plugins;
using CS2DemoKit.Parser;
using CS2DemoKit.TestSupport;
using TUnit.Core.Exceptions;

using CS2OpenSchema.Protos;

#endregion

namespace CS2DemoKit.Analysis.Tests;

/// <summary>
///     Gates <c>AnalysisOptions.MaxDegreeOfParallelism</c> as the pipelined producer honours it. Two
///     levels:
///     <list type="bullet">
///         <item>
///             the mapping from the nullable public knob onto a worker count (null and nonsense
///             values must take the source's default, never throw out of an evaluation; one selects
///             the sequential producer). No demo needed;
///         </item>
///         <item>
///             the count actually bounding the fold: a probe factory (invoked by the producer at the
///             start of every chunk's fold, on the fold worker) records peak concurrency while the
///             pipeline runs.
///         </item>
///     </list>
///     <para>
///         Three things that ride the same producer are gated here as well, over synthetic frames so
///         they run without a demo: the minimum chunk size reaching the reader, which is what lets
///         <see cref="PipelinedDigestEquivalenceTests" /> sweep the chunk boundaries instead of taking
///         whatever the demo's full-packet cadence gives it; a chunk opening at its checkpoint's tick-run
///         start; and how a failure leaves a fold worker. Over a demo: how a cancelled evaluation
///         leaves the pool.
///     </para>
/// </summary>
[NotInParallel]
public class PipelinedDigestWorkerTests
{
    /// <summary>Chunks needed before a bound is meaningfully binding rather than vacuous.</summary>
    private const int MinChunksForBoundToBind = 4;

    /// <summary>Long enough that overlapping workers reliably observe each other; short enough to stay cheap.</summary>
    private static readonly TimeSpan _probeHold = TimeSpan.FromMilliseconds(40);

    [Test]
    public async Task NullDop_MapsToTheSourcesDefault()
    {
        await Assert.That(EntityChangeScanner.ResolveWorkers(null, randomAccess: false))
            .IsEqualTo(Math.Min(Environment.ProcessorCount, EntityChangeScanner.DefaultPipelineWorkers));
        await Assert.That(EntityChangeScanner.ResolveWorkers(null, randomAccess: true))
            .IsEqualTo(Math.Min(Environment.ProcessorCount, EntityChangeScanner.DefaultListWorkers));
        await Assert.That(EntityChangeScanner.DefaultListWorkers).IsGreaterThanOrEqualTo(EntityChangeScanner.DefaultPipelineWorkers)
            .Because("over a list the frames are already resident, so memory does not bound the count the way it does over a stream");
    }

    /// <summary>
    ///     Zero and negatives are ignored rather than thrown on: the knob rides an options record that
    ///     a service may populate from config, and an evaluation is far too expensive to lose to a
    ///     misconfigured integer.
    /// </summary>
    [Test]
    [Arguments(0)]
    [Arguments(-1)]
    [Arguments(-8)]
    public async Task NonPositiveDop_DegradesToTheDefault(int dop)
    {
        await Assert.That(EntityChangeScanner.ResolveWorkers(dop, randomAccess: false))
            .IsEqualTo(EntityChangeScanner.ResolveWorkers(null, randomAccess: false));
        await Assert.That(EntityChangeScanner.ResolveWorkers(dop, randomAccess: true))
            .IsEqualTo(EntityChangeScanner.ResolveWorkers(null, randomAccess: true));
    }

    [Test]
    [Arguments(1)]
    [Arguments(2)]
    [Arguments(7)]
    public async Task PositiveDop_IsPassedThrough(int dop)
    {
        await Assert.That(EntityChangeScanner.ResolveWorkers(dop, randomAccess: false)).IsEqualTo(dop);
        await Assert.That(EntityChangeScanner.ResolveWorkers(dop, randomAccess: true)).IsEqualTo(dop);
    }

    /// <summary>
    ///     The chunk size follows the source. A stream, whose frame count is unknown, keeps the
    ///     producer's minimum: a chunk there is memory held ahead of the loop. A list is cut into
    ///     about twice the worker count of chunks, never smaller than that minimum, because every
    ///     chunk costs a checkpoint prime and the frames are resident either way.
    /// </summary>
    [Test]
    public async Task ChunkSize_FollowsTheSource()
    {
        await Assert.That(EntityChangeScanner.MinChunkFrames(null, 8)).IsEqualTo(PipelinedDigestSource.MinChunkFrames);
        await Assert.That(EntityChangeScanner.MinChunkFrames(20, 8)).IsEqualTo(PipelinedDigestSource.MinChunkFrames);
        await Assert.That(EntityChangeScanner.MinChunkFrames(121_233, 8)).IsEqualTo(121_233 / 16);
        await Assert.That(EntityChangeScanner.MinChunkFrames(121_233, 3)).IsEqualTo(121_233 / 6);
    }

    /// <summary>
    ///     A cap of one is the sequential producer on either source: the evaluator keeps reading the
    ///     source it was given and the scanner's own layer is driven in step with the loop. Anything
    ///     above one hands the evaluator the pipeline, over a list as over a stream.
    /// </summary>
    [Test]
    public async Task DopOfOne_SelectsTheSequentialProducer_OnEitherSource()
    {
        List<DemoFrame> frames = SyntheticFrames(3);
        EntityChangeScanner scanner = new(new EntityStateLayer(), []);

        IDemoFrameSource list = new FrameListSource(frames, null);
        await Assert.That(scanner.BeginEvaluation(list, 1, CancellationToken.None)).IsSameReferenceAs(list);
        await Assert.That(scanner.ProducerKind).IsEqualTo(DigestProducerKind.Sequential);
        scanner.EndEvaluation();

        IDemoFrameSource pipelined = scanner.BeginEvaluation(new FrameListSource(frames, null), 2, CancellationToken.None);
        await Assert.That(pipelined).IsTypeOf<PipelinedDigestSource>();
        await Assert.That(scanner.ProducerKind).IsEqualTo(DigestProducerKind.Pipelined);
        scanner.EndEvaluation();
    }

    /// <summary>
    ///     The worker count bounds the fold exactly. Runs the pipeline over a short prefix of a real
    ///     demo (enough <c>DEM_FullPacket</c>s to plan several chunks) with a probe provider factory
    ///     that holds briefly while recording peak concurrency: a count of one folds one chunk at a
    ///     time, on a multi-core runner where five chunks each holding 40 ms would never serialise
    ///     by accident; a count of two never has three in flight; and every chunk still folds
    ///     exactly once (the count changes scheduling, never output). The tracker count rides the
    ///     same run: a fold that found the pool dry would throw rather than build a tracker.
    /// </summary>
    [Test]
    [Category("Integration")]
    public async Task Pipeline_BoundsTheFoldsInFlight_ByItsWorkerCount()
    {
        ParsedDemo demo = DemoTestHelper.GetOrParse(DemoTestHelper.RequireDemo());
        IReadOnlyList<DemoFrame> prefix = TakeChunkablePrefix(demo.Frames);
        if (Environment.ProcessorCount < 2)
        {
            throw new SkipTestException("needs >= 2 cores for folds to overlap");
        }

        foreach (int workers in (int[]) [1, 2])
        {
            ConcurrencyProbe probe = new(_probeHold);
            EntityFrameDigest[] digests = PipelinedDigests.Produce(
                new FrameListSource(prefix, null),
                probe.NewPerPlayer,
                NewSingletons,
                false,
                out IReadOnlyList<PipelinedDigestSource.ChunkSpan> spans,
                workers: workers,
                minChunkFrames: 1);

            Console.WriteLine($"prefix frames={prefix.Count:N0}  chunks={spans.Count}  workers={workers}  " +
                              $"folds={probe.Invocations}  peak={probe.PeakConcurrency}");
            if (spans.Count < MinChunksForBoundToBind)
            {
                throw new SkipTestException($"needs >= {MinChunksForBoundToBind} chunks (got {spans.Count})");
            }

            await Assert.That(probe.PeakConcurrency).IsLessThanOrEqualTo(workers);
            await Assert.That(probe.PeakConcurrency).IsGreaterThan(0);
            await Assert.That(probe.Invocations).IsEqualTo(spans.Count); // every chunk folded once
            await Assert.That(digests.Length).IsEqualTo(prefix.Count);
        }
    }

    /// <summary>
    ///     The minimum chunk size reaches the reader: a chunk closes at the first candidate full
    ///     packet once it holds that many frames. Without this seam the number and position of the
    ///     chunk boundaries is a function of the demo's full-packet cadence and nothing in the suite
    ///     can move them, which matters because a boundary is where a worker primes from a checkpoint
    ///     and re-emits every live cell, the one position a reconstruction bug can hide at.
    ///     <para>
    ///         The counts are exact. Sixty-one full packets eight frames apart behind a two-frame
    ///         prefix: chunk 0 holds ten frames, every later chunk eight per period, so a minimum of
    ///         one or eight closes at every full packet after <c>F_0</c>, nine closes at every second,
    ///         seventeen at every third, and a minimum past the demo's length leaves one chunk.
    ///     </para>
    /// </summary>
    /// <param name="minChunkFrames">Frames a chunk must hold before the next candidate closes it.</param>
    /// <param name="expectedChunks">Chunks the reader should close for it.</param>
    [Test]
    [Arguments(1, 61)]
    [Arguments(8, 61)]
    [Arguments(9, 31)]
    [Arguments(17, 21)]
    [Arguments(1000, 1)]
    public async Task Pipeline_ClosesAChunk_AtTheFirstCandidateAfterMinChunkFrames(int minChunkFrames, int expectedChunks)
    {
        List<DemoFrame> frames = SyntheticFrames(61);

        EntityFrameDigest[] digests = PipelinedDigests.Produce(
            new FrameListSource(frames, null), () => [], () => [], false,
            out IReadOnlyList<PipelinedDigestSource.ChunkSpan> spans, minChunkFrames: minChunkFrames);

        await Assert.That(digests.Length).IsEqualTo(frames.Count);
        await Assert.That(spans.Count).IsEqualTo(expectedChunks);
        await Assert.That(spans[0].FirstFrameIndex).IsEqualTo(0);
        await Assert.That(spans[0].CheckpointFrameIndex).IsEqualTo(-1)
            .Because("chunk 0 decodes from scratch at every size; it holds the schema prefix");
        await Assert.That(spans[^1].FirstFrameIndex + spans[^1].FrameCount).IsEqualTo(frames.Count);
        for (int c = 1; c < spans.Count; c++)
        {
            await Assert.That(spans[c].FirstFrameIndex).IsEqualTo(spans[c - 1].FirstFrameIndex + spans[c - 1].FrameCount)
                .Because("the chunks must tile the frame list with no gap and no overlap");
            await Assert.That(spans[c].CheckpointFrameIndex).IsEqualTo(spans[c].FirstFrameIndex)
                .Because("no full packet here shares its tick with a predecessor, so each chunk opens on its checkpoint");
            await Assert.That(frames[spans[c].CheckpointFrameIndex].CommandKind).IsEqualTo(EDemoCommands.DemFullPacket);
            await Assert.That(spans[c - 1].FrameCount).IsGreaterThanOrEqualTo(minChunkFrames);
        }
    }

    /// <summary>
    ///     A chunk opens at the start of its checkpoint's tick run, not at the checkpoint itself: the
    ///     frames of that run before the full packet are covered by its snapshot, and a run that
    ///     straddled a boundary would read two different states on the two sides.
    /// </summary>
    [Test]
    public async Task Pipeline_OpensAChunk_AtTheCheckpointsTickRunStart()
    {
        List<DemoFrame> frames = SyntheticFrames(3, packetsBeforeFullPacket: 2);

        EntityFrameDigest[] digests = PipelinedDigests.Produce(
            new FrameListSource(frames, null), () => [], () => [], false,
            out IReadOnlyList<PipelinedDigestSource.ChunkSpan> spans, minChunkFrames: 1);

        await Assert.That(digests.Length).IsEqualTo(frames.Count);
        await Assert.That(spans.Count).IsEqualTo(3);
        for (int c = 1; c < spans.Count; c++)
        {
            int cp = spans[c].CheckpointFrameIndex;
            await Assert.That(cp - spans[c].FirstFrameIndex).IsEqualTo(2)
                .Because("the two same-tick packets before the full packet belong to its chunk");
            for (int i = spans[c].FirstFrameIndex; i < cp; i++)
            {
                await Assert.That(frames[i].ServerTick).IsEqualTo(frames[cp].ServerTick);
            }

            await Assert.That(spans[c].FirstFrameIndex).IsEqualTo(spans[c - 1].FirstFrameIndex + spans[c - 1].FrameCount);
        }
    }

    /// <summary>
    ///     A worker's failure reaches the consumer as the exception it threw, not wrapped in the
    ///     <see cref="AggregateException" /> a task delivers it in. The failures worth catching here
    ///     are typed: <c>EntityDigestExtractor</c> throws <see cref="InvalidOperationException" /> when a
    ///     provider's read returns a type its declared kind cannot hold, the schema check throws it on
    ///     drift, and <c>PrimeFromCheckpoint</c> throws the same on a checkpoint it cannot represent.
    ///     All are meant to be loud rather than something a caller has to unwrap to recognise.
    /// </summary>
    [Test]
    public async Task Pipeline_SurfacesAWorkerFailure_AsTheTypeItThrew()
    {
        List<DemoFrame> frames = SyntheticFrames(1);

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => PipelinedDigests.Produce(
                new FrameListSource(frames, null),
                () => throw new InvalidOperationException("provider schema drift"),
                NewSingletons,
                false));

        await Assert.That(ex.Message).IsEqualTo("provider schema drift");
    }

    /// <summary>
    ///     A token already cancelled aborts before any chunk folds, and does so as
    ///     <see cref="OperationCanceledException" /> from the consumer's first read.
    /// </summary>
    [Test]
    public void Pipeline_WithACancelledToken_ThrowsOperationCanceled()
    {
        List<DemoFrame> frames = SyntheticFrames(1);
        using CancellationTokenSource cts = new();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(
            () => PipelinedDigests.Produce(
                new FrameListSource(frames, null), NewPerPlayer, NewSingletons, false, cancellationToken: cts.Token));
    }

    /// <summary>
    ///     Ending an evaluation joins the folds. A run cancelled after its first poll leaves the chunks
    ///     ahead of it folding on the pool; <c>EndEvaluation</c> returns only once every one of them has
    ///     ended, so no worker primes a tracker or judges the schema against the scanner after it, and
    ///     the next evaluation starts on a quiet pool.
    /// </summary>
    [Test]
    [Category("Integration")]
    public async Task EndEvaluation_JoinsEveryFold_AndTheNextEvaluationStarts()
    {
        ParsedDemo demo = DemoTestHelper.GetOrParse(DemoTestHelper.RequireDemo());
        if (Environment.ProcessorCount < 2)
        {
            throw new SkipTestException("needs >= 2 cores for the scanner to choose the pipeline");
        }

        EntityChangeScanner scanner = new(new EntityStateLayer(), [], [new PawnHealthProvider()]);
        using CancellationTokenSource cts = new();

        IDemoFrameSource source = scanner.BeginEvaluation(demo.AsFrameSource(), null, cts.Token);
        PipelinedDigestSource pipeline = (PipelinedDigestSource)source;
        await Assert.That(source.TryReadNext(out DemoFrame? first)).IsTrue();
        scanner.AdvanceAndPollAt(0, first!.ServerTick);
        int inFlight = pipeline.FoldsInFlight;
        await cts.CancelAsync();
        scanner.EndEvaluation();

        Console.WriteLine($"folds in flight at cancel={inFlight}");
        await Assert.That(inFlight).IsGreaterThan(0)
            .Because("the chunks ahead of the first poll must still be folding, or the join is vacuous");
        await Assert.That(pipeline.FoldsInFlight).IsEqualTo(0)
            .Because("EndEvaluation returns only after every fold has ended");

        IDemoFrameSource next = scanner.BeginEvaluation(demo.AsFrameSource(), null, CancellationToken.None);
        try
        {
            await Assert.That(next).IsTypeOf<PipelinedDigestSource>();
            await Assert.That(next.TryReadNext(out DemoFrame? frame)).IsTrue();
            await Assert.That(scanner.AdvanceAndPollAt(0, frame!.ServerTick)).IsNotNull();
        }
        finally
        {
            scanner.EndEvaluation();
        }
    }

    /// <summary>
    ///     A frame list shaped like a GOTV recording: a two-frame signon prefix, then
    ///     <paramref name="fullPackets" /> <c>DEM_FullPacket</c>s on a fixed tick cadence with ordinary
    ///     packets between, none of them sharing a tick with its successor (which the reader would skip).
    ///     <paramref name="packetsBeforeFullPacket" /> puts that many same-tick packets immediately before
    ///     each full packet, which is the shape the run-start rule exists for. Synthetic because what the
    ///     chunk plan is judged on is a property of the frame sequence alone: nothing here carries a
    ///     message, so a gate over it runs on a machine with no demo.
    /// </summary>
    private static List<DemoFrame> SyntheticFrames(int fullPackets, int packetsBeforeFullPacket = 0)
    {
        const int period = 8;
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

        for (int f = 0; f < fullPackets; f++)
        {
            int tick = 1 + (f * period);
            for (int i = 0; i < packetsBeforeFullPacket; i++)
            {
                Add(EDemoCommands.DemPacket, tick);
            }

            Add(EDemoCommands.DemFullPacket, tick);
            for (int i = 1; i < period; i++)
            {
                Add(EDemoCommands.DemPacket, tick + i);
            }
        }

        return frames;
    }

    private static IReadOnlyList<IPerPlayerEntityValueProvider> NewPerPlayer() =>
    [
        new PawnHealthProvider()
    ];

    /// <summary>
    ///     The prefix ending just after the 5th <c>DEM_FullPacket</c>: with a minimum chunk of one frame
    ///     the reader closes a chunk at every full packet after <c>F_0</c>, so this plans five chunks and
    ///     keeps the gate at a couple of seconds instead of a full-demo fold.
    /// </summary>
    private static IReadOnlyList<DemoFrame> TakeChunkablePrefix(IReadOnlyList<DemoFrame> frames)
    {
        int seen = 0;
        for (int i = 0; i < frames.Count; i++)
        {
            if (frames[i].CommandKind != EDemoCommands.DemFullPacket || ++seen < 5)
            {
                continue;
            }

            return frames.Take(i + 1).ToList();
        }

        return frames;
    }

    private static IReadOnlyList<IEntityValueProvider> NewSingletons() => [new FreezePeriodProvider()];

    /// <summary>
    ///     Stands in for the per-worker provider factory. The producer calls it once per chunk at the
    ///     start of the fold, on the fold worker, so holding here for <see cref="_probeHold" /> makes
    ///     concurrent folds overlap observably; the peak is the largest number ever inside at once.
    /// </summary>
    private sealed class ConcurrencyProbe(TimeSpan hold)
    {
        private readonly Lock _gate = new();
        private int _active;

        public int PeakConcurrency { get; private set; }

        public int Invocations { get; private set; }

        public IReadOnlyList<IPerPlayerEntityValueProvider> NewPerPlayer()
        {
            lock (_gate)
            {
                _active++;
                Invocations++;
                PeakConcurrency = Math.Max(PeakConcurrency, _active);
            }

            Thread.Sleep(hold);
            lock (_gate)
            {
                _active--;
            }

            return
            [
                new PawnHealthProvider(),
                new PawnArmorProvider(),
                new PawnEquipmentValueProvider(),
                new ActiveWeaponProvider()
            ];
        }
    }
}
