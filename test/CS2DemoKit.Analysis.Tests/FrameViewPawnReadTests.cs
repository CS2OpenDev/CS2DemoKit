#region

using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Analysis.Observation;
using CS2DemoKit.Analysis.Plugins;
using CS2DemoKit.Parser;
using CS2DemoKit.Parser.EntityTracking;
using CS2DemoKit.TestSupport;

#endregion

namespace CS2DemoKit.Analysis.Tests;

/// <summary>
///     Pins what <see cref="FrameView" /> reads: a declared provider's value inside
///     <see cref="IFrameObserver.OnFrame" /> for frame N is the pre-frame value the first edge of
///     frame N+1 reads through <see cref="EntityChangeScanner.GetPreFrameValue" />. That is the
///     statement the fold move rests on: the per-pawn fold now runs at the end of frame N instead
///     of at the start of N+1, and nothing an edge sees may change.
/// </summary>
[Category("Integration")]
[NotInParallel]
public class FrameViewPawnReadTests
{
    private const string Place = "entity.pawn.place";
    private const string Health = "entity.pawn.health";
    private const string PosX = "entity.pawn.pos_x";
    private static readonly string[] Declared = [Place, Health, PosX];

    /// <summary>Every declared value for every slot on one frame, boxed, as the scanner reads it.</summary>
    private sealed class FrameValues
    {
        public readonly object?[,] Values = new object?[Declared.Length, 64];
    }

    private sealed class RecordingObserver : IFrameObserver
    {
        public readonly List<FrameValues> Frames = [];
        public FrameView? Held;
        public int Finished;
        public string? TypedMismatch;

        public void OnFrame(FrameView frame)
        {
            Held = frame;
            FrameValues values = new();
            for (int p = 0; p < Declared.Length; p++)
            {
                for (int slot = 0; slot < 64; slot++)
                {
                    object? boxed = frame.Read(Declared[p], slot);
                    values.Values[p, slot] = boxed;
                    CheckTyped(frame, Declared[p], slot, boxed);
                }
            }

            Frames.Add(values);
        }

        public void Finish() => Finished++;

        // The typed reads, through a resolved handle, must agree with the boxed name read cell for
        // cell, and say so when a cell is empty.
        private void CheckTyped(FrameView frame, string name, int slot, object? boxed)
        {
            if (TypedMismatch is not null)
            {
                return;
            }

            ObservedProvider handle = frame.Provider(name);
            bool present;
            object? typed;
            switch (name)
            {
                case Health:
                    present = frame.TryReadInt(handle, slot, out int i);
                    typed = i;
                    break;
                case PosX:
                    present = frame.TryReadFloat(handle, slot, out float f);
                    typed = f;
                    break;
                default:
                    present = frame.TryReadString(handle, slot, out string? s);
                    typed = s;
                    break;
            }

            if (present != (boxed is not null) || (present && !Equals(typed, boxed)))
            {
                TypedMismatch = $"frame {frame.FrameIndex} {name} slot {slot}: boxed {boxed ?? "null"} typed {(present ? typed : "absent")}";
            }
        }
    }

    [Test]
    public async Task Read_InsideOnFrame_AtTheSequentialCap_EqualsThePreFrameValueTheNextFrameReads() => await AssertReads(1);

    [Test]
    public async Task Read_InsideOnFrame_AtTheDefaultCap_EqualsThePreFrameValueTheNextFrameReads() => await AssertReads(null);

    private static async Task AssertReads(int? maxDegreeOfParallelism)
    {
        ParsedDemo demo = DemoTestHelper.GetOrParse(DemoTestHelper.RequireDemo(DemoTestHelper.SampleDemoFileName));

        List<FrameValues> expected = NextFrameReads(demo);
        RecordingObserver observer = new();
        DemoAnalysis.Run(demo, [], new AnalysisOptions
        {
            MaxDegreeOfParallelism = maxDegreeOfParallelism,
            CaptureSnapshots = false,
            ObservedProviders = Declared,
            FrameObserver = observer
        });

        await Assert.That(observer.Finished).IsEqualTo(1);
        await Assert.That(observer.Frames.Count).IsEqualTo(demo.Frames.Count);
        await Assert.That(observer.TypedMismatch).IsNull();

        // The reference reads frame N through frame N+1's poll, so the last frame has no reference.
        int populated = 0;
        for (int n = 0; n < expected.Count; n++)
        {
            for (int p = 0; p < Declared.Length; p++)
            {
                for (int slot = 0; slot < 64; slot++)
                {
                    object? want = expected[n].Values[p, slot];
                    object? got = observer.Frames[n].Values[p, slot];
                    if (want is not null)
                    {
                        populated++;
                    }

                    await Assert.That(got).IsEqualTo(want)
                        .Because($"frame {n} {Declared[p]} slot {slot}");
                }
            }
        }

        await Assert.That(populated).IsGreaterThan(0)
            .Because("a reference with no populated cell would pass while checking nothing");
    }

    /// <summary>
    ///     The reference: a scanner walked frame by frame at the sequential cap, each frame's
    ///     per-pawn values read as frame N+1's first edge reads them, labelled as frame N.
    /// </summary>
    private static List<FrameValues> NextFrameReads(ParsedDemo demo)
    {
        PerPlayerEntityValueProviderRegistry registry = PerPlayerEntityValueProviderRegistry.CreateDefault();
        IPerPlayerEntityValueProvider[] providers = [.. Declared.Select(name => registry.Get(name)!)];
        EntityChangeScanner scanner = new(new EntityStateLayer { StoreUnlensedFields = false }, [], providers);

        List<FrameValues> reads = [];
        foreach ((int n, DemoFrame _, IReadOnlyList<NetMessage> _) in SequentialScan.Frames(scanner, demo))
        {
            if (n == 0)
            {
                continue;
            }

            FrameValues values = new();
            for (int p = 0; p < providers.Length; p++)
            {
                for (int slot = 0; slot < 64; slot++)
                {
                    values.Values[p, slot] = scanner.GetPreFrameValue(providers[p], slot);
                }
            }

            reads.Add(values);
        }

        return reads;
    }

    [Test]
    public async Task View_OutsideOnFrame_ThrowsAndKeepsItsGeneration()
    {
        ParsedDemo demo = DemoTestHelper.GetOrParse(DemoTestHelper.RequireDemo(DemoTestHelper.SampleDemoFileName));
        RecordingObserver observer = new();
        DemoAnalysis.Run(demo, [], new AnalysisOptions
        {
            CaptureSnapshots = false,
            ObservedProviders = Declared,
            FrameObserver = observer
        });

        FrameView held = observer.Held!;
        await Assert.That(held.Generation).IsEqualTo((long)demo.Frames.Count);
        Assert.Throws<InvalidOperationException>(() => _ = held.Frame);
        Assert.Throws<InvalidOperationException>(() => held.Read(Place, 0));
        Assert.Throws<InvalidOperationException>(() => _ = held.Projectiles.Length);
    }

    [Test]
    public async Task UnknownObservedProvider_ThrowsAtBuild()
    {
        ParsedDemo demo = DemoTestHelper.GetOrParse(DemoTestHelper.RequireDemo(DemoTestHelper.SampleDemoFileName));
        ArgumentException ex = Assert.Throws<ArgumentException>(() => DemoAnalysis.Build(demo, [], new AnalysisOptions
        {
            ObservedProviders = ["entity.pawn.no_such_column"]
        }));
        await Assert.That(ex.Message).Contains("entity.pawn.no_such_column");
    }

    [Test]
    public async Task UnknownObservedProjectileClass_ThrowsAtBuild()
    {
        ParsedDemo demo = DemoTestHelper.GetOrParse(DemoTestHelper.RequireDemo(DemoTestHelper.SampleDemoFileName));
        ArgumentException ex = Assert.Throws<ArgumentException>(() => DemoAnalysis.Build(demo, [], new AnalysisOptions
        {
            ObservedProjectileClasses = ["CNotAProjectile"]
        }));
        await Assert.That(ex.Message).Contains("CNotAProjectile");
    }

    private sealed class UndeclaredReader : IFrameObserver
    {
        public void OnFrame(FrameView frame) => frame.Read(Health, 0);

        public void Finish()
        {
        }
    }

    /// <summary>An undeclared read is a caller error, and a throwing observer takes the run down with it.</summary>
    [Test]
    public async Task UndeclaredRead_ThrowsOutOfTheRun()
    {
        ParsedDemo demo = DemoTestHelper.GetOrParse(DemoTestHelper.RequireDemo(DemoTestHelper.SampleDemoFileName));
        ArgumentException ex = Assert.Throws<ArgumentException>(() => DemoAnalysis.Run(demo, [], new AnalysisOptions
        {
            CaptureSnapshots = false,
            ObservedProviders = [Place],
            FrameObserver = new UndeclaredReader()
        }));
        await Assert.That(ex.Message).Contains(Health);
    }

    private sealed class CountingObserver : IFrameObserver
    {
        public int Frames;
        public int Rows;
        public int Finished;
        public int LastTick = -1;
        public bool TicksAscend = true;

        public void OnFrame(FrameView frame)
        {
            Frames++;
            Rows += frame.Projectiles.Length;
            TicksAscend &= frame.Tick >= LastTick;
            LastTick = frame.Tick;
            _ = frame.Team(0);
        }

        public void Finish() => Finished++;
    }

    /// <summary>With nothing declared the observer still sees every frame, with no rows to read.</summary>
    [Test]
    public async Task Observer_WithNothingDeclared_SeesEveryFrame()
    {
        ParsedDemo demo = DemoTestHelper.GetOrParse(DemoTestHelper.RequireDemo(DemoTestHelper.SampleDemoFileName));
        CountingObserver observer = new();
        DemoAnalysis.Run(demo, [], new AnalysisOptions
        {
            CaptureSnapshots = false,
            FrameObserver = observer
        });

        await Assert.That(observer.Frames).IsEqualTo(demo.Frames.Count);
        await Assert.That(observer.Rows).IsEqualTo(0);
        await Assert.That(observer.Finished).IsEqualTo(1);
        await Assert.That(observer.TicksAscend).IsTrue();
    }
}
