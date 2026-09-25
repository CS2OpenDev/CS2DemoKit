#region

using CS2DemoKit.Analysis.Output;
using CS2DemoKit.Analysis.RulesetsV2.Model;
using CS2DemoKit.Analysis.RulesetsV2.Resolve;
using CS2DemoKit.Parser;

#endregion

namespace CS2DemoKit.Analysis.Tests.RoundFacts;

/// <summary>
///     <c>event.frame_tick</c> reads the frame clock on every event, and a table says which clock
///     each bare tick column is on (issue #54, piece 4).
/// </summary>
[Category("Unit")]
public class FrameTickTests
{
    [Test]
    public async Task FrameTick_ResolvesAsInstant_AndClassifiesEachClock()
    {
        CheckedRuleset rs = RoundFactsTestSupport.Checked("""
            ruleset: clocks
            for: each_player
            stats:
              plant_frame:
                capture: event.frame_tick
                on: bomb_planted
                per: round
              plant_server:
                capture: event.tick
                on: bomb_planted
                per: round
              molotov_tick:
                capture: event.tick
                on: molotov
                per: round
              plant_shifted:
                capture: event.tick + 1
                on: bomb_planted
                per: round
            """);

        CheckedStat Stat(string id) => rs.Stats.Single(s => s.StatId == id);
        await Assert.That(Stat("plant_frame").ValueType.Kind).IsEqualTo(Rules.RulesTypeKind.Instant);
        await Assert.That(Stat("plant_frame").Clock).IsEqualTo(TickClock.Frame);
        await Assert.That(Stat("plant_server").Clock).IsEqualTo(TickClock.Server);
        // molotov_thrown is synthesized from entity state: its event.tick is the frame clock.
        await Assert.That(Stat("molotov_tick").Clock).IsEqualTo(TickClock.Frame);
        // Arithmetic over a tick is on no single clock the table could promise.
        await Assert.That(Stat("plant_shifted").Clock).IsEqualTo(TickClock.None);
    }

    [Test]
    public async Task FrameTick_And_ServerTick_HashApart()
    {
        CheckedRuleset rs = RoundFactsTestSupport.Checked("""
            ruleset: clocks
            for: each_player
            stats:
              a:
                capture: event.frame_tick
                on: bomb_planted
                per: round
              b:
                capture: event.tick
                on: bomb_planted
                per: round
            """);

        await Assert.That(rs.Stats[0].DeclaredReads).Contains("event.frame_tick");
        await Assert.That(rs.Stats[1].DeclaredReads).Contains("event.tick");
    }

    /// <summary>
    ///     On the sample the round-1 plant is at frame 13085; the server stamped it 33542, higher by
    ///     the demo's ServerStartTick of 20457. The two captures read exactly those, and the table
    ///     tags the columns with their clocks.
    /// </summary>
    [Test]
    [Category("Integration")]
    [NotInParallel]
    public async Task Sample_BombPlanted_FrameAndServerTicks_DifferByServerStartTick()
    {
        ParsedDemo demo = RoundFactsTestSupport.Sample();
        AnalysisRun run = RoundFactsTestSupport.Run(demo, """
            ruleset: clocks
            for: each_player
            stats:
              plant_frame:
                capture: event.frame_tick
                on: bomb_planted
                per: round
              plant_server:
                capture: event.tick
                on: bomb_planted
                per: round
              kills:
                count: kill
                per: round
            show:
              tables:
                clocks:
                  per: player_round
                  columns:
                    - { stat: plant_frame, label: PlantFrame }
                    - { stat: plant_server, label: PlantServer }
                    - { stat: kills, label: Kills }
            """);

        MetricTable table = RoundFactsTestSupport.Table(run, "clocks", demo);
        MetricRow planted = table.Rows.First(r => RoundFactsTestSupport.Int(r, "PlantFrame") is > 0);

        await Assert.That(RoundFactsTestSupport.Int(planted, "PlantFrame")).IsEqualTo(13085);
        await Assert.That(RoundFactsTestSupport.Int(planted, "PlantServer")).IsEqualTo(33542);
        await Assert.That(demo.ServerStartTick).IsEqualTo(33542 - 13085);

        await Assert.That(table.ColumnClocks["PlantFrame"]).IsEqualTo(MetricTable.FrameClock);
        await Assert.That(table.ColumnClocks["PlantServer"]).IsEqualTo(MetricTable.ServerClock);
        await Assert.That(table.ColumnClocks.ContainsKey("Kills")).IsFalse();
    }
}
