#region

using System.Globalization;
using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Analysis.Nodes;
using CS2DemoKit.Analysis.Observation;
using CS2DemoKit.Analysis.Output;
using CS2DemoKit.Analysis.RulesetsV2.Model;
using CS2DemoKit.Analysis.Tests.RoundFacts;
using CS2DemoKit.TestSupport;

#endregion

namespace CS2DemoKit.Analysis.Tests;

/// <summary>
///     Where an in-frame <c>player.*</c> read lands. A trigger the engine raises from frame N (a
///     cadence step) reads frame N's folded cells, the same the frame observer reads. A wire game
///     event reads the state before its own frame's PacketEntities, so <c>player_hurt</c> sees the
///     victim's health before the hit.
/// </summary>
[Category("Integration")]
[NotInParallel]
public class InFrameReadPointTests
{
    private const string PosX = "entity.pawn.pos_x";
    private const string Health = "entity.pawn.health";

    private const string CadenceRules = """
        ruleset: read_point
        for: each_player
        cadences:
          step: { ticks: 32, anchor: round_freeze_end, until: round_ended }
        stats:
          pos_x:
            capture: player.pos_x
            on: cadence.step
            per: match
          health:
            capture: player.health
            on: cadence.step
            per: match
        show:
          tables:
            steps:
              per: player_event
              on: cadence.step
              columns:
                - { stat: pos_x, label: pos_x }
                - { stat: health, label: health }
        """;

    private const string HurtRules = """
        ruleset: hurt_read
        for: each_player
        stats:
          hurts:
            count: damage_taken
            where: "event.Health > 0 and player.health > 0"
            per: match
          pre_hit:
            count: damage_taken
            where: "event.Health > 0 and player.health > event.Health"
            per: match
          post_hit:
            count: damage_taken
            where: "event.Health > 0 and player.health == event.Health"
            per: match
        """;

    public static IEnumerable<string> Demos() =>
        [DemoTestHelper.SampleDemoFileName, DemoTestHelper.ReferenceDemoFileName];

    /// <summary>Per-slot cells of one frame, null where the view holds none.</summary>
    private sealed class Cells
    {
        public readonly float?[] PosX = new float?[64];
        public readonly int?[] Health = new int?[64];
    }

    /// <summary>Keeps the first frame of each tick, where a due step fires, and the frame before it.</summary>
    private sealed class FirstFrameOfTick : IFrameObserver
    {
        public readonly Dictionary<int, (Cells At, Cells Before)> ByTick = [];
        private Cells _previous = new();
        private int _lastTick = int.MinValue;

        public void OnFrame(FrameView frame)
        {
            ObservedProvider pos = frame.Provider(PosX);
            ObservedProvider health = frame.Provider(Health);
            Cells cells = new();
            for (int slot = 0; slot < 64; slot++)
            {
                cells.PosX[slot] = frame.TryReadFloat(pos, slot, out float x) ? x : null;
                cells.Health[slot] = frame.TryReadInt(health, slot, out int h) ? h : null;
            }

            int tick = frame.Tick;
            if (tick != _lastTick)
            {
                ByTick[tick] = (cells, _previous);
                _lastTick = tick;
            }

            _previous = cells;
        }

        public void Finish()
        {
        }
    }

    [Test]
    [MethodDataSource(nameof(Demos))]
    public async Task CadenceCapture_EqualsTheObserversReadOfTheSameFrame(string demoFile)
    {
        string path = DemoTestHelper.RequireDemo(demoFile);
        IReadOnlyList<RulesetDoc> docs = RoundFactsTestSupport.Load(CadenceRules);
        FirstFrameOfTick observer = new();
        AnalysisRun run = DemoAnalysis.Run(path, docs, new AnalysisOptions
        {
            CaptureSnapshots = false,
            ObservedProviders = [PosX, Health],
            FrameObserver = observer
        });
        await Assert.That(run.Build.ExcludedRulesets).IsEmpty();

        MetricTable table = run.ProjectConfiguredOutputs().Single(t => t.Name == "steps");
        int compared = 0, movedThisFrame = 0;
        foreach (MetricRow row in table.Rows)
        {
            int tick = RoundFactsTestSupport.Dim(row, "tick");
            int slot = RoundFactsTestSupport.Dim(row, "player_slot");
            (Cells at, Cells before) = observer.ByTick[tick];

            if (at.PosX[slot] is { } x)
            {
                float got = (float)Convert.ToDouble(row.Values["pos_x"], CultureInfo.InvariantCulture);
                await Assert.That(got).IsEqualTo(x).Because($"{demoFile} tick {tick} slot {slot} pos_x");
                compared++;
                movedThisFrame += before.PosX[slot] is { } bx && bx != x ? 1 : 0;
            }

            if (at.Health[slot] is { } h)
            {
                await Assert.That(RoundFactsTestSupport.Int(row, "health")).IsEqualTo(h)
                    .Because($"{demoFile} tick {tick} slot {slot} health");
            }
        }

        Console.WriteLine($"{demoFile}: {table.Rows.Count} step rows, {compared} pos_x compared, "
                          + $"{movedThisFrame} moved on the step's own frame");
        await Assert.That(compared).IsGreaterThan(0);

        // Rows whose value changed on the step's frame are the ones a pre-fold read would get wrong.
        await Assert.That(movedThisFrame).IsGreaterThan(0);
    }

    private const string ThrowRules = """
        ruleset: throw_read
        for: each_projectile
        stats:
          thrower_x: { capture: thrower.pos_x, on: raw.projectile_created }
          grenade_x: { capture: projectile.pos_x, on: raw.projectile_created }
        show:
          tables:
            throws:
              per: projectile
              columns:
                - { stat: thrower_x, label: thrower_x }
                - { stat: grenade_x, label: grenade_x }
        """;

    /// <summary>
    ///     A synthesized entity event is raised from the frame's own digest, so a <c>thrower.*</c>
    ///     read on <c>projectile_created</c> sees the frame the grenade appeared in, folded: the
    ///     same cells the observer reads, and the same frame <c>projectile.*</c> reads.
    /// </summary>
    [Test]
    [MethodDataSource(nameof(Demos))]
    public async Task ProjectileCreatedCapture_EqualsTheObserversReadOfTheSameFrame(string demoFile)
    {
        string path = DemoTestHelper.RequireDemo(demoFile);
        FirstFrameOfTick observer = new();
        AnalysisRun run = DemoAnalysis.Run(path, RoundFactsTestSupport.Load(ThrowRules), new AnalysisOptions
        {
            CaptureSnapshots = false,
            ObservedProviders = [PosX, Health],
            FrameObserver = observer
        });
        await Assert.That(run.Build.ExcludedRulesets).IsEmpty();

        MetricTable table = run.ProjectConfiguredOutputs().Single(t => t.Name == "throws");
        int compared = 0, movedThisFrame = 0;
        foreach (MetricRow row in table.Rows)
        {
            int tick = RoundFactsTestSupport.Dim(row, "created_tick");
            int slot = RoundFactsTestSupport.Dim(row, "player_slot");
            (Cells at, Cells before) = observer.ByTick[tick];
            if (at.PosX[slot] is not { } x)
            {
                continue;
            }

            float got = (float)Convert.ToDouble(row.Values["thrower_x"], CultureInfo.InvariantCulture);
            await Assert.That(got).IsEqualTo(x).Because($"{demoFile} throw at tick {tick} by slot {slot}");
            compared++;
            movedThisFrame += before.PosX[slot] is { } bx && bx != x ? 1 : 0;
        }

        Console.WriteLine($"{demoFile}: {table.Rows.Count} throws, {compared} compared, {movedThisFrame} moved on the created frame");
        await Assert.That(compared).IsGreaterThan(0);
        await Assert.That(movedThisFrame).IsGreaterThan(0);
    }

    [Test]
    [MethodDataSource(nameof(Demos))]
    public async Task PlayerHurt_ReadsTheVictimsHealthBeforeTheHit(string demoFile)
    {
        string path = DemoTestHelper.RequireDemo(demoFile);
        AnalysisRun run = DemoAnalysis.Run(path, RoundFactsTestSupport.Load(HurtRules),
            new AnalysisOptions { CaptureSnapshots = false });
        await Assert.That(run.Build.ExcludedRulesets).IsEmpty();

        int hurts = Sum(run, "hurts"), pre = Sum(run, "pre_hit"), post = Sum(run, "post_hit");
        Console.WriteLine($"{demoFile}: {hurts} non-fatal hurts, {pre} read above the event's health, {post} equal to it");

        await Assert.That(hurts).IsGreaterThan(0);
        await Assert.That(post).IsEqualTo(0);
        await Assert.That(pre).IsEqualTo(hurts);
    }

    private static int Sum(AnalysisRun run, string stat)
    {
        int total = 0;
        foreach (PerPlayerNodeTemplate.MaterializedPlayer player in run.MaterializedPlayers)
        {
            StateNode node = player.NodesByRuleId![$"hurt_read.{stat}"];
            total += Convert.ToInt32(node.GetType().GetProperty("Value")!.GetValue(node), CultureInfo.InvariantCulture);
        }

        return total;
    }
}
