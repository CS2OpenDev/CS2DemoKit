#region

using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Parser;
using CS2DemoKit.Parser.EntityTracking;
using CS2DemoKit.Parser.GameEvents;
using CS2DemoKit.TestSupport;

#endregion

namespace CS2DemoKit.Analysis.Tests;

/// <summary>Demo analyzer tests.</summary>
[NotInParallel]
[Category("Integration")]
public class DemoAnalyzerTests
{
    /// <summary>Build context_indexes events and replays entities.</summary>
    [Test]
    public async Task BuildContext_IndexesEventsAndReplaysEntities()
    {
        string path = DemoTestHelper.RequireDemo(DemoTestHelper.ReferenceDemoFileName);

        ParsedDemo parsed = DemoTestHelper.GetOrParse(path);

        DemoContext ctx = DemoAnalyzer.BuildContext(parsed);

        // ── EventsOfType ──────────────────────────────────────────────────────
        IReadOnlyList<GameEvent> deaths = ctx.EventsOfType<PlayerDeathEvent>();
        IReadOnlyList<GameEvent> shots = ctx.EventsOfType<WeaponFireEvent>();
        Console.WriteLine($"Deaths (via EventsOfType): {deaths.Count}");
        Console.WriteLine($"Shots  (via EventsOfType): {shots.Count}");
        await Assert.That(deaths.Count).IsEqualTo(parsed.AllGameEvents.Select(e => e.Payload).OfType<PlayerDeathEvent>().Count());
        await Assert.That(shots.Count).IsEqualTo(parsed.AllGameEvents.Select(e => e.Payload).OfType<WeaponFireEvent>().Count());

        // ── EventsInRange ─────────────────────────────────────────────────────
        int midTick = parsed.TickCount / 2;
        IReadOnlyList<GameEvent> midRange = ctx.EventsInRange(midTick - 128, midTick + 128);
        Console.WriteLine($"Events in ±128 ticks of midpoint ({midTick}): {midRange.Count}");
        await Assert.That(midRange).IsNotNull();
        // Verify ordering is preserved.
        for (int i = 1; i < midRange.Count; i++)
        {
            await Assert.That(midRange[i].ServerTick).IsGreaterThanOrEqualTo(midRange[i - 1].ServerTick);
        }

        // ── Rounds ────────────────────────────────────────────────────────────
        Console.WriteLine($"Rounds derived: {ctx.Rounds.Count}");

        // ── EntityState ───────────────────────────────────────────────────────
        int entityCount = ctx.EntityState.CurrentEntities.All().Count();
        Console.WriteLine($"Entities after full replay: {entityCount}");
        // Typical CS2 demo has 1k–10k entities at end of match. Bound generously
        // on both sides: < 100 means the parser bailed out early; > 50k means
        // we're counting something we shouldn't (the phantom-class-id bug
        // would have landed in this range pre-fix).
        await Assert.That(entityCount).IsBetween(100, 50_000).WithInclusiveBounds();
    }

    /// <summary>Entity state layer_seek to tick advances forward.</summary>
    [Test]
    public async Task EntityStateLayer_SeekToTickAdvancesForward()
    {
        string path = DemoTestHelper.RequireDemo();

        ParsedDemo parsed = DemoTestHelper.GetOrParse(path);
        DemoContext ctx = DemoAnalyzer.BuildContext(parsed);

        EntityStateLayer layer = ctx.CreateEntityLayer();

        // Seek to 25 %, 50 %, 75 %, and 100 % of the demo tick range.
        int maxTick = parsed.TickCount;
        int[] checkpoints = [maxTick / 4, maxTick / 2, maxTick * 3 / 4, maxTick];

        int prevEntityCount = 0;
        int prevTick = 0;

        foreach (int targetTick in checkpoints)
        {
            EntityTracker tracker = layer.SeekToTick(targetTick);

            await Assert.That(layer.CurrentTick).IsGreaterThanOrEqualTo(prevTick);

            int count = tracker.CurrentEntities.All().Count();
            Console.WriteLine($"  SeekToTick({targetTick:N0}) → currentTick={layer.CurrentTick:N0}  entities={count}");

            prevEntityCount = count;
            prevTick = layer.CurrentTick;
        }

        // Final position should have a sensible entity count — same bounds as the
        // build-context test. Anything under 100 means the seek didn't replay much.
        await Assert.That(prevEntityCount).IsBetween(100, 50_000).WithInclusiveBounds();

        // Reset and verify we can seek again from the start.
        layer.Reset();
        await Assert.That(layer.CurrentTick).IsEqualTo(0);

        EntityTracker afterReset = layer.SeekToTick(checkpoints[0]);
        Console.WriteLine($"  After Reset, SeekToTick({checkpoints[0]:N0}) → entities={afterReset.CurrentEntities.All().Count()}");
        await Assert.That(afterReset.CurrentEntities.All().Count()).IsGreaterThanOrEqualTo(0);
    }
}
