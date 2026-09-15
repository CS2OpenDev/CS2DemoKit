#region

using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Parser;
using CS2DemoKit.Parser.EntityTracking;
using CS2DemoKit.TestSupport;

#endregion

namespace CS2DemoKit.Analysis.Tests;

/// <summary>A list-backed layer seeks forward through the retained frames and can start over.</summary>
[NotInParallel]
[Category("Integration")]
public class EntityStateLayerSeekTests
{
    [Test]
    public async Task SeekToTick_AdvancesForward_AndResetStartsOver()
    {
        string path = DemoTestHelper.RequireDemo();
        ParsedDemo parsed = DemoTestHelper.GetOrParse(path);
        EntityStateLayer layer = new(parsed.Frames);

        int maxTick = parsed.TickCount;
        int[] checkpoints = [maxTick / 4, maxTick / 2, maxTick * 3 / 4, maxTick];
        int lastEntityCount = 0;
        int lastTick = 0;
        foreach (int targetTick in checkpoints)
        {
            EntityTracker tracker = layer.SeekToTick(targetTick);
            await Assert.That(layer.CurrentTick).IsGreaterThanOrEqualTo(lastTick);
            lastEntityCount = tracker.CurrentEntities.All().Count();
            lastTick = layer.CurrentTick;
        }

        // Under 100 means the seek replayed almost nothing; over 50k means it counted phantoms.
        await Assert.That(lastEntityCount).IsBetween(100, 50_000).WithInclusiveBounds();

        layer.Reset();
        await Assert.That(layer.CurrentTick).IsEqualTo(0);
        EntityTracker afterReset = layer.SeekToTick(checkpoints[0]);
        await Assert.That(afterReset.CurrentEntities.All().Count()).IsGreaterThan(0);
    }
}
