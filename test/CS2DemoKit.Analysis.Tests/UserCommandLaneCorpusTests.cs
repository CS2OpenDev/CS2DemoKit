#region

using System.Globalization;
using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Analysis.Observation;
using CS2DemoKit.Analysis.Output;
using CS2DemoKit.Analysis.RulesetsV2.Model;
using CS2DemoKit.Analysis.Tests.RoundFacts;
using CS2DemoKit.Analysis.Yaml;
using CS2DemoKit.Parser;
using CS2DemoKit.Parser.EntityTracking;
using CS2DemoKit.TestSupport;
using TUnit.Core.Exceptions;

#endregion

namespace CS2DemoKit.Analysis.Tests;

/// <summary>
///     <c>player.cmd.*</c> reads at a cadence step against a <see cref="UserCmdReconstructor" />
///     walked independently over the same frames, on both paths.
///     <para>
///         The <c>usercmds-delta</c> fixture's source demo is not kept locally, so the demo is
///         <c>DEMO_PATH</c> when set, else the smallest matchmaking demo of the corpus. A demo that
///         carries no commands skips.
///     </para>
/// </summary>
[Category("Integration")]
[NotInParallel]
public class UserCommandLaneCorpusTests
{
    private const string CorpusDemo = "match730_003774074388190593393_1164257366_406.dem";

    private const string Rules = """
        ruleset: cmd_lane
        for: each_player
        cadences:
          step: { ticks: 8, anchor: round_freeze_end, until: round_ended }
        stats:
          fire_ticks:
            capture: event.tick
            on: cadence.step
            keep: list
            per: match
          cmd_ticks:
            capture: player.cmd.tick
            on: cadence.step
            keep: list
            per: match
          pressing:
            count: cadence.step
            where: player.cmd.buttons != 0
            per: match
        """;

    private static string Demo()
    {
        string? env = Environment.GetEnvironmentVariable("DEMO_PATH");
        string? path = !string.IsNullOrWhiteSpace(env) && File.Exists(env)
            ? env
            : DemoTestHelper.FindDemoPath(CorpusDemo);
        return path ?? throw new SkipTestException($"neither DEMO_PATH nor {CorpusDemo} is on this machine");
    }

    [Test]
    public async Task CommandReads_AtACadenceStep_EqualAnIndependentRebuild()
    {
        string path = Demo();
        ParsedDemo demo = DemoParser.Parse(File.ReadAllBytes(path).AsMemory());
        IReadOnlyList<RulesetDoc> docs = RoundFactsTestSupport.Load(Rules);

        foreach ((string label, AnalysisRun run) in RoundFactsCorpusTests.RunBothPaths(path, docs))
        {
            await Assert.That(run.Build.ExcludedRulesets).IsEmpty().Because(label);
            await Assert.That(run.Build.DecodesUserCommands).IsTrue().Because(label);

            Dictionary<int, (int[] Fires, int[] Cmds, int Pressing)> byPlayer = [];
            HashSet<int> fireTicks = [];
            foreach (PerPlayerNodeTemplate.MaterializedPlayer player in run.MaterializedPlayers)
            {
                int[] fires = Ints(player, "fire_ticks");
                int[] cmds = Ints(player, "cmd_ticks");
                await Assert.That(cmds.Length).IsEqualTo(fires.Length).Because($"{label} slot {player.PlayerSlot}");
                byPlayer[player.PlayerSlot] = (fires, cmds, (int)Value(player, "pressing")!);
                fireTicks.UnionWith(fires);
            }

            // A cadence step drains on the first frame at or past its due tick, after the frame's
            // commands are applied: the first frame carrying the fire tick.
            Dictionary<(int Tick, int Slot), (int ClientTick, bool Pressing)> expected = [];
            UserCmdReconstructor oracle = new();
            int lastTick = int.MinValue;
            foreach (DemoFrame frame in demo.Frames)
            {
                oracle.AdvanceOneFrame(frame);
                int tick = frame.GameTick ?? frame.ServerTick;
                if (tick != lastTick && fireTicks.Contains(tick))
                {
                    foreach (int slot in byPlayer.Keys)
                    {
                        CSGOUserCmdPB? cmd = oracle.Current(slot);
                        expected[(tick, slot)] = (cmd?.Base?.ClientTick ?? 0,
                            (cmd?.Base?.ButtonsPb?.Buttonstate1 ?? 0) != 0);
                    }
                }

                lastTick = tick;
            }

            if (oracle.Stats.Full + oracle.Stats.Delta == 0)
            {
                throw new SkipTestException($"{Path.GetFileName(path)} carries no player commands");
            }

            long compared = 0, nonZero = 0;
            foreach ((int slot, (int[] fires, int[] cmds, int pressing)) in byPlayer)
            {
                int expectedPressing = 0;
                for (int i = 0; i < fires.Length; i++)
                {
                    (int clientTick, bool down) = expected[(fires[i], slot)];
                    await Assert.That(cmds[i]).IsEqualTo(clientTick).Because($"{label} slot {slot} at tick {fires[i]}");
                    expectedPressing += down ? 1 : 0;
                    compared++;
                    nonZero += clientTick != 0 ? 1 : 0;
                }

                await Assert.That(pressing).IsEqualTo(expectedPressing).Because($"{label} slot {slot}");
            }

            Console.WriteLine($"{label}: {compared} command reads compared, {nonZero} nonzero; "
                              + $"rebuilt {oracle.Stats.Full} full, {oracle.Stats.Delta} delta");
            await Assert.That(nonZero).IsGreaterThan(compared / 2).Because(label);
        }
    }

    private const string AimRules = """
        ruleset: cmd_aim
        for: each_player
        cadences:
          step: { ticks: 64, anchor: round_freeze_end, until: round_ended }
        stats:
          view_yaw:
            capture: player.cmd.view_yaw
            on: cadence.step
            per: match
          eye_yaw:
            capture: player.eye_yaw
            on: cadence.step
            per: match
        show:
          tables:
            aim:
              per: player_event
              on: cadence.step
              columns:
                - { stat: view_yaw, label: view_yaw }
                - { stat: eye_yaw, label: eye_yaw }
        """;

    /// <summary>
    ///     A command slot is the player slot: each player's command yaw tracks their own pawn's eye
    ///     yaw far more often than the next player's.
    /// </summary>
    [Test]
    public async Task CommandSlot_IsThePlayerSlot()
    {
        string path = Demo();
        ParsedDemo demo = DemoParser.Parse(File.ReadAllBytes(path).AsMemory());
        AnalysisRun run = DemoAnalysis.Run(demo, RoundFactsTestSupport.Load(AimRules));
        await Assert.That(run.Build.ExcludedRulesets).IsEmpty();

        Dictionary<int, Dictionary<int, (double View, double Eye)>> byTick = [];
        foreach (MetricRow row in RoundFactsTestSupport.Table(run, "aim", demo).Rows)
        {
            int tick = Convert.ToInt32(row.Dimensions["tick"], CultureInfo.InvariantCulture);
            int slot = Convert.ToInt32(row.Dimensions["player_slot"], CultureInfo.InvariantCulture);
            if (!byTick.TryGetValue(tick, out Dictionary<int, (double, double)>? slots))
            {
                byTick[tick] = slots = [];
            }

            slots[slot] = (Convert.ToDouble(row.Values["view_yaw"], CultureInfo.InvariantCulture),
                Convert.ToDouble(row.Values["eye_yaw"], CultureInfo.InvariantCulture));
        }

        static bool Close(double a, double b) => Math.Abs(((a - b) % 360.0 + 540.0) % 360.0 - 180.0) < 5.0;

        int same = 0, sameClose = 0, shifted = 0, shiftedClose = 0;
        foreach (Dictionary<int, (double View, double Eye)> slots in byTick.Values)
        {
            int[] order = [.. slots.Keys.Order()];
            for (int i = 0; i < order.Length; i++)
            {
                (double view, double eye) = slots[order[i]];
                if (view == 0 && eye == 0)
                {
                    continue;
                }

                same++;
                sameClose += Close(view, eye) ? 1 : 0;
                shifted++;
                shiftedClose += Close(view, slots[order[(i + 1) % order.Length]].Eye) ? 1 : 0;
            }
        }

        Console.WriteLine($"same slot: {sameClose}/{same} within 5 degrees; next slot: {shiftedClose}/{shifted}");
        await Assert.That(same).IsGreaterThan(1000);
        // Dead and spectating pawns keep a stale eye yaw, so the own-slot rate is well short of 1.
        await Assert.That(sameClose).IsGreaterThan(same / 2);
        await Assert.That(shiftedClose * 3).IsLessThan(sameClose);
    }

    [Test]
    public async Task FrameViewCommand_ReadsTheLane_OnlyWhenDecoded()
    {
        string path = Demo();
        ParsedDemo demo = DemoParser.Parse(File.ReadAllBytes(path).AsMemory());

        CommandProbe on = new();
        DemoAnalysis.Run(demo, [], new AnalysisOptions { FrameObserver = on, DecodeUserCommands = true });
        UserCmdReconstructor oracle = new();
        foreach (DemoFrame frame in demo.Frames)
        {
            oracle.AdvanceOneFrame(frame);
        }

        if (oracle.Stats.Full + oracle.Stats.Delta == 0)
        {
            throw new SkipTestException($"{Path.GetFileName(path)} carries no player commands");
        }

        await Assert.That(on.Failure).IsNull();
        await Assert.That(on.LastClientTicks).IsEquivalentTo(
            Enumerable.Range(0, 64).Select(s => oracle.Current(s)?.Base?.ClientTick ?? -1).ToArray());

        CommandProbe off = new();
        DemoAnalysis.Run(demo, [], new AnalysisOptions { FrameObserver = off });
        await Assert.That(off.Failure).IsTypeOf<InvalidOperationException>();
    }

    /// <summary>
    ///     The lane's queue keeps its bound over a retained frame list, so a run of more frames than
    ///     the bound flushes: a read on every frame and a read after gaps wider than the bound both
    ///     equal an eager rebuild walked alongside, slot by slot.
    /// </summary>
    [Test]
    [Arguments(1)]
    [Arguments(UserCmdReconstructor.DefaultOnDemandFrameBound + 517)]
    public async Task FrameViewCommand_OverARetainedList_EqualsAnEagerRebuild_AcrossQueueFlushes(int every)
    {
        string path = Demo();
        ParsedDemo demo = DemoParser.Parse(File.ReadAllBytes(path).AsMemory());
        if (demo.Frames.Count <= UserCmdReconstructor.DefaultOnDemandFrameBound)
        {
            throw new SkipTestException($"{Path.GetFileName(path)} is shorter than the queue bound");
        }

        LockstepProbe probe = new(every);
        DemoAnalysis.Run(demo, [], new AnalysisOptions { FrameObserver = probe, DecodeUserCommands = true });
        if (probe.Oracle.Stats.Full + probe.Oracle.Stats.Delta == 0)
        {
            throw new SkipTestException($"{Path.GetFileName(path)} carries no player commands");
        }

        Console.WriteLine($"every {every}: {probe.Frames} frames, {probe.Compared} reads compared, {probe.NonNull} non-null; "
                          + $"first difference: {probe.FirstDifference ?? "none"}");
        await Assert.That(probe.Frames).IsGreaterThan(UserCmdReconstructor.DefaultOnDemandFrameBound);
        await Assert.That(probe.NonNull).IsGreaterThan(0);
        await Assert.That(probe.FirstDifference).IsNull();
    }

    private static object? Value(PerPlayerNodeTemplate.MaterializedPlayer player, string stat)
    {
        StateNode node = player.NodesByRuleId![$"cmd_lane.{stat}"];
        return node.GetType().GetProperty("Value")!.GetValue(node);
    }

    private static int[] Ints(PerPlayerNodeTemplate.MaterializedPlayer player, string stat) =>
        Value(player, stat) is IReadOnlyList<int> values ? [.. values] : [];

    // An eager reconstructor fed the same frame the view shows, compared on every slot each time it reads.
    private sealed class LockstepProbe(int every) : IFrameObserver
    {
        public UserCmdReconstructor Oracle { get; } = new();

        public int Frames { get; private set; }

        public long Compared { get; private set; }

        public long NonNull { get; private set; }

        public string? FirstDifference { get; private set; }

        public void OnFrame(FrameView view)
        {
            Oracle.AdvanceOneFrame(view.Frame);
            Frames++;
            if (Frames % every != 0 || FirstDifference is not null)
            {
                return;
            }

            for (int slot = 0; slot < 64; slot++)
            {
                CSGOUserCmdPB? expected = Oracle.Current(slot);
                CSGOUserCmdPB? actual = view.Command(slot);
                Compared++;
                NonNull += expected is null ? 0 : 1;
                if (!Equals(expected, actual))
                {
                    FirstDifference = $"frame {view.FrameIndex} slot {slot}: "
                                      + $"lane {actual?.Base?.ClientTick.ToString(CultureInfo.InvariantCulture) ?? "null"} "
                                      + $"against eager {expected?.Base?.ClientTick.ToString(CultureInfo.InvariantCulture) ?? "null"}";
                    return;
                }
            }
        }

        public void Finish()
        {
        }
    }

    private sealed class CommandProbe : IFrameObserver
    {
        public int[] LastClientTicks { get; } = new int[64];

        public Exception? Failure { get; private set; }

        public void OnFrame(FrameView view)
        {
            if (Failure is not null)
            {
                return;
            }

            try
            {
                for (int slot = 0; slot < 64; slot++)
                {
                    LastClientTicks[slot] = view.Command(slot)?.Base?.ClientTick ?? -1;
                }
            }
            catch (InvalidOperationException ex)
            {
                Failure = ex;
            }
        }

        public void Finish()
        {
        }
    }
}
