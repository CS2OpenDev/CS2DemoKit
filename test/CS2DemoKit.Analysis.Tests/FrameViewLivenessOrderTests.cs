#region

using CS2DemoKit.Analysis.Graphs;
using CS2DemoKit.Analysis.Observation;
using CS2DemoKit.Parser;
using CS2DemoKit.Parser.EntityTracking;
using CS2DemoKit.TestSupport;

#endregion

namespace CS2DemoKit.Analysis.Tests;

/// <summary>
///     Pins <c>entity.pawn.is_alive</c> and <c>entity.pawn.entity_index</c> read through a
///     <see cref="FrameView" /> against <see cref="PositionSampler.Walk" /> on the sample demo:
///     every pawn the walk yields reads its <see cref="PositionSample.IsAlive" />, its index puts
///     the frame's pawns in the walk's order, and a slot the walk does not yield that frame never
///     reads alive. That is what lets a host replace the walk with the observer.
/// </summary>
[Category("Integration")]
[NotInParallel]
public class FrameViewLivenessOrderTests
{
    private const string Alive = "entity.pawn.is_alive";
    private const string Index = "entity.pawn.entity_index";
    private const string PosX = "entity.pawn.pos_x";

    private sealed class Recorder : IFrameObserver
    {
        public readonly List<(int?[] Alive, int?[] Index, bool[] Positioned)> Frames = [];

        public void OnFrame(FrameView frame)
        {
            ObservedProvider alive = frame.Provider(Alive);
            ObservedProvider index = frame.Provider(Index);
            ObservedProvider posX = frame.Provider(PosX);
            int?[] a = new int?[64];
            int?[] e = new int?[64];
            bool[] p = new bool[64];
            for (int slot = 0; slot < 64; slot++)
            {
                a[slot] = frame.TryReadInt(alive, slot, out int v) ? v : null;
                e[slot] = frame.TryReadInt(index, slot, out int i) ? i : null;
                p[slot] = frame.TryReadFloat(posX, slot, out _);
            }

            Frames.Add((a, e, p));
        }

        public void Finish()
        {
        }
    }

    [Test]
    public async Task LivenessAndOrder_AtTheSequentialCap_MatchTheWalk() => await AssertMatchesWalk(1);

    [Test]
    public async Task LivenessAndOrder_AtTheDefaultCap_MatchTheWalk() => await AssertMatchesWalk(null);

    private static async Task AssertMatchesWalk(int? maxDegreeOfParallelism)
    {
        ParsedDemo demo = DemoTestHelper.GetOrParse(DemoTestHelper.RequireDemo(DemoTestHelper.SampleDemoFileName));
        Recorder observer = new();
        DemoAnalysis.Run(demo, [], new AnalysisOptions
        {
            MaxDegreeOfParallelism = maxDegreeOfParallelism,
            CaptureSnapshots = false,
            ObservedProviders = [Alive, Index, PosX],
            FrameObserver = observer
        });
        await Assert.That(observer.Frames.Count).IsEqualTo(demo.Frames.Count);

        // The engine applies a tick's frames together, so the view equals the walk at the last
        // frame of each tick run; earlier frames of a run already see the run's end state. The
        // tick-0 prefix is held back until the first later tick, so it is compared from tick 1.
        ILookup<int, PositionSample> walk = PositionSampler.Walk(demo).ToLookup(s => s.FrameIndex);
        Tally tally = new();
        string? first = null;
        int midRun = 0, midRunDiffering = 0;
        for (int n = 0; n < observer.Frames.Count && first is null; n++)
        {
            bool runEnd = n + 1 == demo.Frames.Count || demo.Frames[n + 1].ServerTick != demo.Frames[n].ServerTick;
            if (!runEnd || demo.Frames[n].ServerTick <= 0)
            {
                midRun++;
                midRunDiffering += Compare(n, observer.Frames[n], walk[n], new Tally()) is null ? 0 : 1;
                continue;
            }

            first = Compare(n, observer.Frames[n], walk[n], tally);
        }

        Console.WriteLine($"samples {tally.Samples}, alive {tally.Alive}, dead {tally.Dead}, stale positioned slot-frames {tally.Stale}; "
                          + $"mid-run or prefix frames {midRun}, of which differ {midRunDiffering}");
        await Assert.That(first).IsNull();
        await Assert.That(tally.Alive).IsGreaterThan(0);
        await Assert.That(tally.Dead).IsGreaterThan(0).Because("a liveness column that never reads dead checks nothing");
    }

    private sealed class Tally
    {
        public int Samples, Alive, Dead, Stale;
    }

    // Null when frame n's view agrees with the walk: same liveness for every pawn the walk yields,
    // indices ascending in walk order, and no positioned slot outside the walk reading alive.
    private static string? Compare(int n, (int?[] Alive, int?[] Index, bool[] Positioned) view,
        IEnumerable<PositionSample> samples, Tally tally)
    {
        bool[] yielded = new bool[64];
        int last = -1;
        foreach (PositionSample s in samples)
        {
            tally.Samples++;
            yielded[s.PlayerSlot] = true;
            if (view.Alive[s.PlayerSlot] is not { } got || got != (s.IsAlive ? 1 : 0))
            {
                return $"frame {n} slot {s.PlayerSlot}: walk alive {s.IsAlive}, observer {(object?)view.Alive[s.PlayerSlot] ?? "null"}";
            }

            if (view.Index[s.PlayerSlot] is not { } index || index <= last)
            {
                return $"frame {n} slot {s.PlayerSlot}: index {(object?)view.Index[s.PlayerSlot] ?? "null"} after {last}";
            }

            last = index;
            _ = s.IsAlive ? tally.Alive++ : tally.Dead++;
        }

        for (int slot = 0; slot < 64; slot++)
        {
            if (yielded[slot] || !view.Positioned[slot])
            {
                continue;
            }

            tally.Stale++;
            if (view.Alive[slot] is 1)
            {
                return $"frame {n} slot {slot}: not in the walk but reads alive";
            }
        }

        return null;
    }

    [Test]
    public async Task Undeclared_LeavesBothColumnsOutOfTheScanner()
    {
        ParsedDemo demo = DemoTestHelper.GetOrParse(DemoTestHelper.RequireDemo(DemoTestHelper.SampleDemoFileName));
        string[] without = Columns(DemoAnalysis.Build(demo, [], new AnalysisOptions { ObservedProviders = [PosX] }));
        string[] with = Columns(DemoAnalysis.Build(demo, [], new AnalysisOptions { ObservedProviders = [Alive, Index] }));

        await Assert.That(without).DoesNotContain(Alive);
        await Assert.That(without).DoesNotContain(Index);
        await Assert.That(with).Contains(Alive);
        await Assert.That(with).Contains(Index);
    }

    private static string[] Columns(BuildResult build) =>
        [.. build.EntityScanner?.PerPlayerProviders.Select(p => p.Name) ?? []];
}
