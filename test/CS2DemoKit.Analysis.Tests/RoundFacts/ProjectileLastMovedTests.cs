#region

using System.Numerics;
using CS2DemoKit.Analysis.Output;
using CS2DemoKit.Parser;
using CS2DemoKit.Parser.EntityTracking;
using CS2DemoKit.TestSupport;

#endregion

namespace CS2DemoKit.Analysis.Tests.RoundFacts;

/// <summary>
///     <c>projectile.last_moved_tick</c> kept last on a <c>per: projectile</c> row equals the last
///     tick at which <see cref="ProjectileSampler" />'s consecutive samples of the same projectile
///     differ by more than <see cref="ProjectileLifecycle.MovedUnits" />, on the sample demo on both
///     paths. A smoke rests for most of its life, so the tick trails the removal on some rows.
/// </summary>
[Category("Integration")]
[NotInParallel]
public class ProjectileLastMovedTests
{
    private const string Rules = """
        ruleset: rest
        for: each_projectile
        stats:
          class:      { capture: projectile.class,           keep: first }
          first_seen: { capture: projectile.first_seen_tick, keep: first }
          last_moved: { capture: projectile.last_moved_tick, keep: last }
        show:
          tables:
            grenades:
              per: projectile
              columns:
                - { stat: class, label: class }
                - { stat: first_seen, label: first_seen }
                - { stat: last_moved, label: last_moved }
        """;

    [Test]
    public async Task LastMovedTick_EqualsTheSamplersLastMove_OnBothPaths()
    {
        string path = DemoTestHelper.RequireDemo(DemoTestHelper.SampleDemoFileName);
        ParsedDemo demo = RoundFactsTestSupport.Sample();
        Dictionary<(int Index, int Serial), (int FirstTick, int LastMoved, int LastTick)> expected = Oracle(demo);
        await Assert.That(expected.Count).IsGreaterThan(10);

        foreach ((string label, AnalysisRun run) in RoundFactsCorpusTests.RunBothPaths(path, RoundFactsTestSupport.Load(Rules)))
        {
            await Assert.That(run.Build.ExcludedRulesets).IsEmpty()
                .Because($"{label}: {string.Join("; ", run.Build.ExcludedRulesets.SelectMany(e => e.Diagnostics))}");

            MetricTable grenades = RoundFactsTestSupport.Table(run, "grenades", demo);
            int compared = 0, rested = 0;
            foreach (MetricRow row in grenades.Rows)
            {
                (int Index, int Serial) key = (RoundFactsTestSupport.Dim(row, "entity_index"), RoundFactsTestSupport.Dim(row, "serial"));
                string because = $"{label}: projectile {key.Index}:{key.Serial} ({row.Values["class"]})";
                await Assert.That(expected.ContainsKey(key)).IsTrue().Because(because);
                (int firstTick, int lastMoved, int lastTick) = expected[key];

                await Assert.That(RoundFactsTestSupport.Int(row, "first_seen")).IsEqualTo(firstTick).Because(because);
                await Assert.That(RoundFactsTestSupport.Int(row, "last_moved")).IsEqualTo(lastMoved).Because(because);
                await Assert.That(lastMoved).IsLessThanOrEqualTo(RoundFactsTestSupport.Dim(row, "removed_tick")).Because(because);
                compared++;
                rested += lastMoved < lastTick ? 1 : 0;
            }

            Console.WriteLine($"{label}: {compared} projectiles compared, {rested} at rest before their last row");
            await Assert.That(compared).IsEqualTo(expected.Count).Because(label);
            await Assert.That(rested).IsGreaterThan(0).Because(label);
        }
    }

    // Per projectile: its first sample's tick, the tick of the last sample whose position differs
    // from the previous sample's by more than a tenth of a unit (the first tick until then), and
    // its last sample's tick.
    private static Dictionary<(int, int), (int FirstTick, int LastMoved, int LastTick)> Oracle(ParsedDemo demo)
    {
        Dictionary<(int, int), (int First, int Moved, int Last, Vector3? Position)> state = [];
        foreach (ProjectileSample sample in ProjectileSampler.Walk(demo))
        {
            (int, int) key = (sample.EntityIndex, sample.Serial);
            if (!state.TryGetValue(key, out (int First, int Moved, int Last, Vector3? Position) s))
            {
                state[key] = (sample.Tick, sample.Tick, sample.Tick, sample.Position);
                continue;
            }

            bool moved = s.Position is { } a && sample.Position is { } b
                         && Vector3.DistanceSquared(a, b) > ProjectileLifecycle.MovedUnits * ProjectileLifecycle.MovedUnits;
            state[key] = (s.First, moved ? sample.Tick : s.Moved, sample.Tick, sample.Position);
        }

        return state.ToDictionary(kv => kv.Key, kv => (kv.Value.First, kv.Value.Moved, kv.Value.Last));
    }
}
