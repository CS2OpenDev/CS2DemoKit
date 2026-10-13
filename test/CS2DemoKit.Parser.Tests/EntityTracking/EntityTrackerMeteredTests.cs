#region

using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;
using System.Threading;
using CS2DemoKit.Parser.Entities;
using CS2DemoKit.Parser.EntityTracking;
using CS2DemoKit.TestSupport;

#endregion

namespace CS2DemoKit.Parser.Tests.EntityTracking;

/// <summary>
///     <see cref="EntityTracker.Metered" /> gates the decode meter's Parser-side counter,
///     <c>parser.entity_decode.started</c> (see docs/profiling.md). A layer-owned tracker sets it
///     false so a rules run's own tracker is never double-counted against the analysis-side scan
///     counter; this proves the flag actually silences <c>Counter.Add</c> rather than just skipping
///     some unrelated side effect.
/// </summary>
[Category("Integration")]
[NotInParallel]
public class EntityTrackerMeteredTests
{
    private const int MaxFrames = 200;

    [Test]
    public async Task Replay_WithMeteredFalse_NeverTouchesTheCounter()
    {
        List<DemoFrame> frames = Demo().Frames.Take(MaxFrames).ToList();

        long adds = ReplayUnderListener(frames, metered: false);

        await Assert.That(adds).IsEqualTo(0L);
    }

    [Test]
    public async Task Replay_WithMeteredTrue_TouchesTheCounterOnce()
    {
        List<DemoFrame> frames = Demo().Frames.Take(MaxFrames).ToList();

        long adds = ReplayUnderListener(frames, metered: true);

        await Assert.That(adds).IsEqualTo(1L);
    }

    // AsyncLocal scopes the measurement to this call: Replay runs on the calling thread, so the
    // box is visible exactly where it is set and nowhere another concurrent test's tracker folds.
    private static readonly AsyncLocal<StrongBox<long>?> _active = new();

    private static long ReplayUnderListener(List<DemoFrame> frames, bool metered)
    {
        StrongBox<long> box = new(0);
        _active.Value = box;
        try
        {
            using MeterListener listener = new();
            listener.InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == "CS2DemoKit.Parser.Entities")
                {
                    l.EnableMeasurementEvents(instrument);
                }
            };
            listener.SetMeasurementEventCallback<long>((instrument, measurement, _, _) =>
            {
                if (instrument.Name == "parser.entity_decode.started" && _active.Value is { } active)
                {
                    Interlocked.Add(ref active.Value, measurement);
                }
            });
            listener.Start();

            EntityTracker tracker = EntityTrackerFactory.CreateCurated();
            tracker.Metered = metered;
            tracker.Replay(frames);
        }
        finally
        {
            _active.Value = null;
        }

        return box.Value;
    }

    private static ParsedDemo Demo() => DemoTestHelper.GetOrParse(DemoTestHelper.RequireDemo());
}
