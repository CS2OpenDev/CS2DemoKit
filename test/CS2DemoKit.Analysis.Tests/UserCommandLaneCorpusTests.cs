#region

using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Analysis.Observation;
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

            Console.WriteLine($"{label}: {compared} command reads compared, {nonZero} nonzero");
            await Assert.That(nonZero).IsGreaterThan(compared / 2).Because(label);
        }
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

    private static object? Value(PerPlayerNodeTemplate.MaterializedPlayer player, string stat)
    {
        StateNode node = player.NodesByRuleId![$"cmd_lane.{stat}"];
        return node.GetType().GetProperty("Value")!.GetValue(node);
    }

    private static int[] Ints(PerPlayerNodeTemplate.MaterializedPlayer player, string stat) =>
        Value(player, stat) is IReadOnlyList<int> values ? [.. values] : [];

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
