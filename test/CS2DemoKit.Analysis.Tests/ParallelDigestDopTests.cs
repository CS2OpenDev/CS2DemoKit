#region

using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Analysis.Plugins;
using CS2DemoKit.Parser;
using CS2DemoKit.TestSupport;
using TUnit.Core.Exceptions;

#endregion

namespace CS2DemoKit.Analysis.Tests;

/// <summary>
///     Gates <c>AnalysisOptions.MaxDegreeOfParallelism</c> (the fix for two long-standing
///     frictions: "no public DOP knob" / "no parallelism control"). Two levels:
///     <list type="bullet">
///         <item>
///             the pure mapping from the nullable public knob onto <see cref="ParallelOptions" />
///             (null and nonsense values must degrade to unbounded, never throw out of an
///             evaluation) — no demo needed;
///         </item>
///         <item>
///             the cap actually constraining the decode: a probe factory (invoked by
///             <c>ParallelDigestProducer.Produce</c> inside the parallel region, once per worker)
///             records peak concurrency while the fan-out runs.
///         </item>
///     </list>
///     <para>
///         Two things that ride the same fan-out are gated here as well, both over synthetic frames so
///         they run without a demo: the chunk target reaching <c>PlanChunks</c>, which is what lets
///         <see cref="ParallelDigestEquivalenceTests" /> sweep the chunk boundaries instead of taking
///         whatever the runner's core count gives it, and how a failure leaves the parallel region.
///     </para>
/// </summary>
[NotInParallel]
public class ParallelDigestDopTests
{
    /// <summary>Chunks needed before a cap of 1 is meaningfully "serializing" rather than vacuous.</summary>
    private const int MinChunksForCapToBind = 3;

    /// <summary>Long enough that overlapping workers reliably observe each other; short enough to stay cheap.</summary>
    private static readonly TimeSpan _probeHold = TimeSpan.FromMilliseconds(40);

    [Test]
    public async Task NullDop_MapsToUnbounded()
    {
        ParallelOptions options = ParallelDigestProducer.BuildParallelOptions(null, CancellationToken.None);

        await Assert.That(options.MaxDegreeOfParallelism).IsEqualTo(-1); // ParallelOptions' "unlimited"
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
    public async Task NonPositiveDop_DegradesToUnbounded(int dop)
    {
        ParallelOptions options = ParallelDigestProducer.BuildParallelOptions(dop, CancellationToken.None);

        await Assert.That(options.MaxDegreeOfParallelism).IsEqualTo(-1);
    }

    [Test]
    [Arguments(1)]
    [Arguments(2)]
    [Arguments(7)]
    public async Task PositiveDop_IsPassedThrough(int dop)
    {
        ParallelOptions options = ParallelDigestProducer.BuildParallelOptions(dop, CancellationToken.None);

        await Assert.That(options.MaxDegreeOfParallelism).IsEqualTo(dop);
    }

    [Test]
    public async Task CancellationToken_RidesTheSameOptions()
    {
        using CancellationTokenSource cts = new();

        ParallelOptions options = ParallelDigestProducer.BuildParallelOptions(4, cts.Token);

        await Assert.That(options.CancellationToken).IsEqualTo(cts.Token);
    }

    /// <summary>
    ///     The cap constrains the real decode. Runs <c>Produce</c> over a short prefix of a real demo
    ///     (enough <c>DEM_FullPacket</c>s to plan several chunks) with a probe provider factory that
    ///     holds briefly while recording peak concurrency:
    ///     <list type="bullet">
    ///         <item>cap 1 ⇒ peak concurrency EXACTLY 1 — on a multi-core runner several chunks each
    ///         holding 40 ms would never serialize by accident;</item>
    ///         <item>cap 2 ⇒ peak concurrency at most 2;</item>
    ///         <item>either way every frame's digest is still produced (the cap changes scheduling,
    ///         never output).</item>
    ///     </list>
    /// </summary>
    [Test]
    [Category("Integration")]
    public async Task Produce_HonoursMaxDegreeOfParallelism()
    {
        ParsedDemo demo = DemoTestHelper.GetOrParse(DemoTestHelper.RequireDemo());
        IReadOnlyList<DemoFrame> prefix = TakeChunkablePrefix(demo.Frames);

        IReadOnlyList<ParallelDigestProducer.Chunk> chunks =
            ParallelDigestProducer.PlanChunks(prefix, out _);
        Console.WriteLine($"prefix frames={prefix.Count:N0}  chunks={chunks.Count}  " +
                          $"cores={Environment.ProcessorCount}");
        if (chunks.Count < MinChunksForCapToBind || Environment.ProcessorCount < 2)
        {
            throw new SkipTestException(
                $"needs >= {MinChunksForCapToBind} chunks on >= 2 cores (got {chunks.Count} on " +
                $"{Environment.ProcessorCount})");
        }

        foreach (int cap in (int[]) [1, 2])
        {
            ConcurrencyProbe probe = new(_probeHold);
            EntityFrameDigest[] digests = ParallelDigestProducer.Produce(
                prefix,
                probe.NewPerPlayer,
                NewSingletons,
                false,
                maxDegreeOfParallelism: cap);

            Console.WriteLine($"cap={cap}  workers={probe.Invocations}  peak={probe.PeakConcurrency}");
            await Assert.That(probe.PeakConcurrency).IsLessThanOrEqualTo(cap);
            await Assert.That(probe.PeakConcurrency).IsGreaterThan(0);
            await Assert.That(probe.Invocations).IsEqualTo(chunks.Count); // every chunk still ran
            await Assert.That(digests.Length).IsEqualTo(prefix.Count);
        }
    }

    /// <summary>
    ///     The chunk target reaches the plan. Without this seam the number and position of the chunk
    ///     boundaries is a function of <see cref="Environment.ProcessorCount" /> and nothing in the suite
    ///     can move them, which matters because a boundary is where a worker primes from a checkpoint and
    ///     re-emits every live cell — the one position a reconstruction bug can hide at.
    ///     <para>
    ///         The counts are exact rather than bounds: 60 candidates (F_0 is never one) divide evenly by
    ///         every stride the targets below produce, so the ceiling in the stride arithmetic does not
    ///         round a checkpoint away. A target of 1 still plans two chunks — the from-scratch prefix
    ///         plus one checkpoint — because the planner clamps to at least one checkpoint rather than
    ///         planning a chunk list it would then have to special-case.
    ///     </para>
    /// </summary>
    /// <param name="target">The chunk count to aim for.</param>
    /// <param name="expectedChunks">Chunks the planner should return for it.</param>
    [Test]
    [Arguments(1, 2)]
    [Arguments(2, 2)]
    [Arguments(3, 3)]
    [Arguments(5, 5)]
    [Arguments(8, 8)]
    public async Task PlanChunks_HonoursItsChunkTarget(int target, int expectedChunks)
    {
        List<DemoFrame> frames = SyntheticFrames(61);

        IReadOnlyList<ParallelDigestProducer.Chunk> chunks =
            ParallelDigestProducer.PlanChunks(frames, out _, target);

        await Assert.That(chunks.Count).IsEqualTo(expectedChunks);
        await Assert.That(chunks[0].Start).IsEqualTo(0);
        await Assert.That(chunks[0].CheckpointFrameIndex).IsEqualTo(-1)
            .Because("chunk 0 decodes from scratch at every target; it sits in the schema-bootstrap region");
        await Assert.That(chunks[^1].End).IsEqualTo(frames.Count);
        for (int c = 1; c < chunks.Count; c++)
        {
            await Assert.That(chunks[c].Start).IsEqualTo(chunks[c - 1].End)
                .Because("the chunks must tile the frame list with no gap and no overlap");
            await Assert.That(chunks[c].CheckpointFrameIndex).IsEqualTo(chunks[c].Start);
            await Assert.That(frames[chunks[c].Start].Command).IsEqualTo("DEM_FullPacket");
        }
    }

    /// <summary>
    ///     The seam's default IS the production path, so a gate that sweeps the target is sweeping the
    ///     thing production uses rather than a parallel code path that only tests reach.
    /// </summary>
    [Test]
    public async Task PlanChunks_WithNoTarget_PlansWhatTheCoreCountPlans()
    {
        List<DemoFrame> frames = SyntheticFrames(61);

        IReadOnlyList<ParallelDigestProducer.Chunk> byDefault = ParallelDigestProducer.PlanChunks(frames, out _);
        IReadOnlyList<ParallelDigestProducer.Chunk> byCoreCount =
            ParallelDigestProducer.PlanChunks(frames, out _, Environment.ProcessorCount);

        await Assert.That(byDefault.Count).IsEqualTo(byCoreCount.Count);
        for (int c = 0; c < byDefault.Count; c++)
        {
            await Assert.That(byDefault[c]).IsEqualTo(byCoreCount[c]);
        }
    }

    /// <summary>
    ///     A worker's failure reaches the caller as the exception it threw, not as the
    ///     <see cref="AggregateException" /> <c>Parallel.For</c> delivers it in — the same contract
    ///     <c>TriangleBvh.Build</c> gives the other half of this PR's fan-out. The failures worth catching
    ///     here are typed: <c>EntityDigestExtractor</c> throws <see cref="InvalidOperationException" /> when
    ///     a provider's read returns a type its declared kind cannot hold, and <c>PrimeFromCheckpoint</c>
    ///     throws the same on a checkpoint it cannot represent. Both are schema drift, which this codebase
    ///     wants loud rather than wrapped in something a caller has to unwrap to recognise.
    ///     <para>
    ///         One full packet means one from-scratch chunk, so exactly one worker and exactly one failure.
    ///         Several distinct failures at once are what an aggregate is actually for and stay aggregated.
    ///     </para>
    /// </summary>
    [Test]
    public async Task Produce_SurfacesAWorkerFailure_AsTheTypeItThrew()
    {
        List<DemoFrame> frames = SyntheticFrames(1);
        IReadOnlyList<ParallelDigestProducer.Chunk> chunks = ParallelDigestProducer.PlanChunks(frames, out _);
        await Assert.That(chunks.Count).IsEqualTo(1)
            .Because("with one full packet there is no split point, so one worker throws once");

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => ParallelDigestProducer.Produce(
                frames,
                () => throw new InvalidOperationException("provider schema drift"),
                NewSingletons,
                false));

        await Assert.That(ex.Message).IsEqualTo("provider schema drift");
    }

    /// <summary>
    ///     A token already cancelled aborts before any chunk decodes, and does so as
    ///     <see cref="OperationCanceledException" /> — cancellation rides the <see cref="ParallelOptions" />
    ///     rather than arriving through the aggregate, so the unwrap above never sees it.
    /// </summary>
    [Test]
    public void Produce_WithACancelledToken_ThrowsOperationCanceled()
    {
        List<DemoFrame> frames = SyntheticFrames(1);
        using CancellationTokenSource cts = new();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(
            () => ParallelDigestProducer.Produce(
                frames, NewPerPlayer, NewSingletons, false, cancellationToken: cts.Token));
    }

    /// <summary>
    ///     The per-worker allocation sum belongs to the <c>Produce</c> call that accumulated it. The decode
    ///     runs on pool threads, so the orchestrator's own <c>GC.GetAllocatedBytesForCurrentThread</c>
    ///     bracket cannot see the workers and the producer hands the figure back out of band — and the
    ///     knob this class gates is what makes that interesting, because it exists so several demos can
    ///     decode at once in one process. A process-wide accumulator cannot survive that: the second run's
    ///     zeroing discards the first's total and the two then add into each other.
    ///     <para>
    ///         The sharp half is the bystander. A thread that never produced must read nothing, however
    ///         many decodes have finished elsewhere in the process since — which is exactly what a static
    ///         accumulator could not say, since it would still be holding the last run's figure.
    ///     </para>
    /// </summary>
    [Test]
    public async Task WorkerAllocAccounting_BelongsToTheCallThatProducedIt()
    {
        List<DemoFrame> frames = SyntheticFrames(1);
        bool wasEnabled = Profiling.Enabled;
        Profiling.Enabled = true;
        long mine;
        long bystander;
        try
        {
            mine = await Task.Run(() =>
            {
                ParallelDigestProducer.Produce(frames, NewPerPlayer, NewSingletons, false);
                return ParallelDigestProducer.ReadWorkerAllocBytes();
            });

            // A second decode, run to completion in between, as a second demo in the same process would.
            await Task.Run(() => ParallelDigestProducer.Produce(frames, NewPerPlayer, NewSingletons, false));

            bystander = await Task.Run(ParallelDigestProducer.ReadWorkerAllocBytes);
        }
        finally
        {
            Profiling.Enabled = wasEnabled;
        }

        Console.WriteLine($"worker alloc: producing call={mine:N0} bytes  bystander={bystander:N0} bytes");
        await Assert.That(mine).IsGreaterThan(0)
            .Because("a profiled decode must account for the allocation of its own workers");
        await Assert.That(bystander).IsEqualTo(0)
            .Because("a thread that never produced must read nothing, whatever other decodes have finished");
    }

    /// <summary>
    ///     A frame list shaped like a GOTV recording: a two-frame signon prefix, then
    ///     <paramref name="fullPackets" /> <c>DEM_FullPacket</c>s on a fixed tick cadence with ordinary
    ///     packets between, none of them sharing a tick with its successor (which the planner would skip).
    ///     Synthetic because what the chunk target is judged on is a property of the frame sequence alone:
    ///     nothing here is decoded, so a gate over it runs on a machine with no demo.
    /// </summary>
    private static List<DemoFrame> SyntheticFrames(int fullPackets)
    {
        const int period = 8;
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

        for (int f = 0; f < fullPackets; f++)
        {
            int tick = 1 + (f * period);
            Add("DEM_FullPacket", tick);
            for (int i = 1; i < period; i++)
            {
                Add("DEM_Packet", tick + i);
            }
        }

        return frames;
    }

    private static IReadOnlyList<IPerPlayerEntityValueProvider> NewPerPlayer() =>
    [
        new PawnHealthProvider()
    ];

    /// <summary>
    ///     The prefix ending just after the 4th <c>DEM_FullPacket</c> — the smallest slice
    ///     <c>PlanChunks</c> still splits into several chunks (F_0 is never a checkpoint), which keeps
    ///     this gate at a couple of seconds instead of a full-demo decode.
    /// </summary>
    private static IReadOnlyList<DemoFrame> TakeChunkablePrefix(IReadOnlyList<DemoFrame> frames)
    {
        int seen = 0;
        for (int i = 0; i < frames.Count; i++)
        {
            if (frames[i].Command != "DEM_FullPacket" || ++seen < 4)
            {
                continue;
            }

            return frames.Take(i + 1).ToList();
        }

        return frames;
    }

    private static IReadOnlyList<IEntityValueProvider> NewSingletons() => [new FreezePeriodProvider()];

    /// <summary>
    ///     Stands in for the per-worker provider factory. <c>Produce</c> calls it once per worker
    ///     INSIDE the parallel region, so holding here for <see cref="_probeHold" /> makes concurrent
    ///     workers overlap observably; the peak is the largest number of workers ever inside at once.
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
