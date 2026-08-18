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
                cap);

            Console.WriteLine($"cap={cap}  workers={probe.Invocations}  peak={probe.PeakConcurrency}");
            await Assert.That(probe.PeakConcurrency).IsLessThanOrEqualTo(cap);
            await Assert.That(probe.PeakConcurrency).IsGreaterThan(0);
            await Assert.That(probe.Invocations).IsEqualTo(chunks.Count); // every chunk still ran
            await Assert.That(digests.Length).IsEqualTo(prefix.Count);
        }
    }

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
