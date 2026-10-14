#region

using CS2DemoKit.Analysis.Output;
using CS2DemoKit.Analysis.Profiles;
using CS2DemoKit.Analysis.RulesetsV2.Model;
using CS2DemoKit.Analysis.RulesetsV2.Resolve;
using CS2DemoKit.Parser;
using CS2DemoKit.Parser.GameEvents;
using CS2DemoKit.TestSupport;

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
              thrown_tick:
                capture: event.tick
                on: projectile_thrown
                per: round
              removed_tick:
                capture: event.tick
                on: raw.projectile_removed
                per: round
            """);

        CheckedStat Stat(string id) => rs.Stats.Single(s => s.StatId == id);
        await Assert.That(Stat("plant_frame").ValueType.Kind).IsEqualTo(Rules.RulesTypeKind.Instant);
        await Assert.That(Stat("plant_frame").Clock).IsEqualTo(TickClock.Frame);
        await Assert.That(Stat("plant_server").Clock).IsEqualTo(TickClock.Server);
        // molotov_thrown is synthesized from entity state: its event.tick is the frame clock.
        await Assert.That(Stat("molotov_tick").Clock).IsEqualTo(TickClock.Frame);
        // So are the projectile lifecycle events, through a view or raw.
        await Assert.That(Stat("thrown_tick").Clock).IsEqualTo(TickClock.Frame);
        await Assert.That(Stat("removed_tick").Clock).IsEqualTo(TickClock.Frame);
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

    private const string CadenceMatch = """
        ruleset: cadence_match
        for: match
        cadences:
          second: { ticks: 64, anchor: round_freeze_end, until: round_ended }
        stats:
          ticks:
            capture: event.tick
            on: cadence.second
            keep: list
            per: match
          steps:
            capture: event.Step
            on: cadence.second
            keep: list
            per: match
        """;

    private const string CadencePlayers = """
        ruleset: cadence_players
        for: each_player
        cadences:
          second: { ticks: 64, anchor: round_freeze_end, until: round_ended }
        stats:
          samples:
            count: cadence.second
            per: match
        show:
          tables:
            cadence_samples:
              per: player_match
              columns:
                - { stat: samples, label: Samples }
        """;

    /// <summary>
    ///     A 64-tick cadence fires once per period from each freeze end to the round's close: step 0 on
    ///     the freeze-end frame, then on the first frame at or after each due tick. The close's own
    ///     frame stops the schedule before the frame's steps drain, so a step due on it does not
    ///     fire. Every player of the each_player ruleset fires on every step, on both paths.
    /// </summary>
    [Test]
    [Category("Integration")]
    [NotInParallel]
    public async Task CadenceTick_FiresOncePerPeriod_OnFrameClock()
    {
        string path = DemoTestHelper.RequireDemo(DemoTestHelper.SampleDemoFileName);
        ParsedDemo demo = RoundFactsTestSupport.Sample();
        IReadOnlyList<RulesetDoc> docs = RoundFactsTestSupport.Load(CadenceMatch, CadencePlayers);
        LogicalEventResolver resolver = new(DemoAnalysis.Build(demo, docs).Profile);
        HashSet<string> anchors = new(resolver.Resolve("round_freeze_end")!.ConcreteEventNames, StringComparer.Ordinal);
        HashSet<string> stops = new(resolver.Resolve("round_end")!.ConcreteEventNames, StringComparer.Ordinal);

        // Each round's freeze end and close as frame indices, and every frame's tick. A step fires on
        // the first frame at or after its due tick, and only before the close's frame.
        List<(int FreezeEnd, int Close)> rounds = [];
        List<int> frameTicks = [];
        int? open = null;
        int matchStart = 0;
        for (int f = 0; f < demo.Frames.Count; f++)
        {
            DemoFrame frame = demo.Frames[f];
            frameTicks.Add(frame.GameTick ?? frame.ServerTick);
            foreach (NetMessage message in frame.DecodedMessages)
            {
                if (message is not GameEventMessage gem)
                {
                    continue;
                }

                string name = gem.DecodedEvent.Name;
                if (open is { } freezeEnd && (stops.Contains(name) || name == "begin_new_match"))
                {
                    rounds.Add((freezeEnd, f));
                    open = null;
                }

                if (anchors.Contains(name))
                {
                    open = f;
                }

                if (name == "begin_new_match")
                {
                    matchStart = f;
                }
            }
        }

        if (open is { } last)
        {
            rounds.Add((last, int.MaxValue));
        }

        List<int> expectedTicks = [];
        List<int> expectedSteps = [];
        int closedRounds = 0;
        foreach ((int freezeEnd, int close) in rounds)
        {
            int anchorTick = frameTicks[freezeEnd];
            int firedFrame = freezeEnd;
            int perRound = 0;
            for (int step = 0;; step++)
            {
                int due = anchorTick + 64 * step;
                while (firedFrame < frameTicks.Count && frameTicks[firedFrame] < due)
                {
                    firedFrame++;
                }

                if (firedFrame >= frameTicks.Count || firedFrame >= close)
                {
                    break;
                }

                // A match restart resets the per: match captures.
                if (firedFrame >= matchStart)
                {
                    expectedTicks.Add(frameTicks[firedFrame]);
                    expectedSteps.Add(step);
                }

                perRound++;
            }

            if (close != int.MaxValue)
            {
                closedRounds++;
                // floor((close - freeze_end) / 64) + 1, less a step due on the close's own tick.
                int span = frameTicks[close] - anchorTick;
                int formula = span / 64 + 1 - (span % 64 == 0 ? 1 : 0);
                await Assert.That(perRound).IsEqualTo(formula).Because($"round from frame {freezeEnd}");
            }
        }

        await Assert.That(closedRounds).IsGreaterThan(0);
        await Assert.That(expectedTicks.Count).IsGreaterThan(100);

        foreach ((string label, AnalysisRun run) in RoundFactsCorpusTests.RunBothPaths(path, docs))
        {
            await Assert.That(RoundFactsCorpusTests.List(run, "cadence_match.ticks"))
                .IsEqualTo(string.Join(",", expectedTicks)).Because(label);
            await Assert.That(RoundFactsCorpusTests.List(run, "cadence_match.steps"))
                .IsEqualTo(string.Join(",", expectedSteps)).Because(label);
            await Assert.That(run.Build.Cadences.Count).IsEqualTo(1).Because("one key across both rulesets");

            MetricTable table = RoundFactsTestSupport.Table(run, "cadence_samples", demo);
            await Assert.That(table.Rows.Count).IsEqualTo(run.MaterializedPlayers.Count).Because(label);
            await Assert.That(table.Rows.Sum(r => RoundFactsTestSupport.Int(r, "Samples") ?? 0))
                .IsEqualTo(run.MaterializedPlayers.Count * expectedTicks.Count).Because(label);
        }
    }

    /// <summary>A build whose rulesets use no cadence schedules none.</summary>
    [Test]
    [Category("Integration")]
    [NotInParallel]
    public async Task NoCadence_BuildsNoSchedule()
    {
        ParsedDemo demo = RoundFactsTestSupport.Sample();
        IReadOnlyList<RulesetDoc> docs = RoundFactsTestSupport.Load("""
            ruleset: plain
            for: each_player
            cadences:
              unused: { ticks: 64, anchor: round_freeze_end }
            stats:
              kills:
                count: kill
                per: match
            """);

        await Assert.That(DemoAnalysis.Build(demo, docs).Cadences).IsEmpty();
    }
}
