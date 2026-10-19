#region

using CS2DemoKit.Analysis.Observation;
using CS2DemoKit.Analysis.Output;
using CS2DemoKit.Analysis.Tests.RoundFacts;
using CS2DemoKit.Parser;
using CS2DemoKit.Parser.EntityTracking;
using CS2DemoKit.TestSupport;

#endregion

namespace CS2DemoKit.Analysis.Tests;

/// <summary>
///     <see cref="ProjectileRow.ExplodeEffectTickBegin" /> on the sample demo: every HE removed in
///     the demo carries a server tick inside its own life, and <c>projectile.explode_effect_tick</c>
///     with <c>keep: last</c> reads the same value the observer saw on the row at removal. Flashes
///     read 0 at removal on this demo, so only the HE is held to a detonation.
/// </summary>
[Category("Integration")]
[NotInParallel]
public class ProjectileExplodeTickTests
{
    private const string ExplodeRows = """
        ruleset: explode_rows
        for: each_projectile
        stats:
          explode: { capture: projectile.explode_effect_tick, keep: last }
        show:
          tables:
            explodes:
              per: projectile
              columns:
                - { stat: explode, label: explode }
        """;

    private sealed class Life
    {
        public required string ClassName;
        public int CreatedServerTick;
        public int? RemovedServerTick;
        public int? ExplodeAtRemoval;
        public bool EverRead;
    }

    private sealed class Recorder : IFrameObserver
    {
        public readonly Dictionary<(int, int), Life> Lives = [];

        public void OnFrame(FrameView frame)
        {
            int serverTick = frame.Frame.ServerTick;
            foreach (ProjectileRow row in frame.Created)
            {
                Lives[(row.EntityIndex, row.Serial)] = new Life { ClassName = row.ClassName, CreatedServerTick = serverTick };
            }

            foreach (ProjectileRow row in frame.Projectiles)
            {
                if (Lives.TryGetValue((row.EntityIndex, row.Serial), out Life? life) && row.ExplodeEffectTickBegin is not null)
                {
                    life.EverRead = true;
                }
            }

            foreach (ProjectileRow row in frame.Removed)
            {
                if (Lives.TryGetValue((row.EntityIndex, row.Serial), out Life? life))
                {
                    life.RemovedServerTick = serverTick;
                    life.ExplodeAtRemoval = row.ExplodeEffectTickBegin;
                }
            }
        }

        public void Finish()
        {
        }
    }

    [Test]
    public async Task DetonatedGrenades_CarryAnExplodeTickInsideTheirLife()
    {
        ParsedDemo demo = RoundFactsTestSupport.Sample();
        Recorder observer = Observe();
        Dictionary<string, (int Lives, int Read, int Exploded)> byClass = [];
        foreach (Life life in observer.Lives.Values)
        {
            (int lives, int read, int exploded) = byClass.GetValueOrDefault(life.ClassName);
            byClass[life.ClassName] = (lives + 1, read + (life.EverRead ? 1 : 0), exploded + (life.ExplodeAtRemoval > 0 ? 1 : 0));
        }

        Console.WriteLine(string.Join("; ", byClass.OrderBy(c => c.Key, StringComparer.Ordinal)
            .Select(c => $"{c.Key}: lives {c.Value.Lives}, read {c.Value.Read}, exploded at removal {c.Value.Exploded}")));

        int checkedLives = 0;
        foreach (Life life in observer.Lives.Values)
        {
            await Assert.That(life.EverRead).IsTrue().Because($"{life.ClassName} created at {life.CreatedServerTick}");
            if (life.RemovedServerTick is not { } removed
                || life.ClassName != GrenadeProjectileClasses.HEGrenade)
            {
                continue;
            }

            // The field is on the server-boot clock; the frame clock is that less ServerStartTick.
            string because = $"{life.ClassName} {life.CreatedServerTick}..{removed}";
            await Assert.That(life.ExplodeAtRemoval).IsNotNull().Because(because);
            int explode = life.ExplodeAtRemoval!.Value - demo.ServerStartTick;
            await Assert.That(explode).IsGreaterThanOrEqualTo(life.CreatedServerTick).Because(because);
            await Assert.That(explode).IsLessThanOrEqualTo(removed).Because(because);
            checkedLives++;
        }

        await Assert.That(checkedLives).IsGreaterThan(0).Because("a demo with no removed HE checks nothing");
    }

    [Test]
    public async Task KeepLast_ReadsTheRowAtRemoval()
    {
        ParsedDemo demo = RoundFactsTestSupport.Sample();
        Recorder observer = Observe();
        AnalysisRun run = DemoAnalysis.Run(demo, RoundFactsTestSupport.Load(ExplodeRows));
        MetricTable table = RoundFactsTestSupport.Table(run, "explodes", demo);

        await Assert.That(table.Rows.Count).IsGreaterThan(0);
        int exploded = 0;
        foreach (MetricRow row in table.Rows)
        {
            (int, int) key = (RoundFactsTestSupport.Dim(row, "entity_index"), RoundFactsTestSupport.Dim(row, "serial"));
            if (!observer.Lives.TryGetValue(key, out Life? life) || life.RemovedServerTick is null)
            {
                continue;
            }

            int? read = RoundFactsTestSupport.Int(row, "explode");
            await Assert.That(read).IsEqualTo(life.ExplodeAtRemoval ?? 0).Because($"projectile {key}");
            exploded += read > 0 ? 1 : 0;
        }

        await Assert.That(exploded).IsGreaterThan(0);
    }

    private static Recorder Observe()
    {
        ParsedDemo demo = RoundFactsTestSupport.Sample();
        Recorder observer = new();
        DemoAnalysis.Run(demo, [], new AnalysisOptions
        {
            CaptureSnapshots = false,
            ObservedProjectileClasses = GrenadeProjectileClasses.All,
            FrameObserver = observer
        });
        return observer;
    }
}
