#region

using System.Globalization;
using CS2DemoKit.Analysis.Output;
using CS2DemoKit.Parser;
using CS2DemoKit.Parser.EntityTracking;
using CS2DemoKit.TestSupport;
using TUnit.Core.Exceptions;

#endregion

namespace CS2DemoKit.Analysis.Tests.RoundFacts;

/// <summary>
///     <c>thrower.cmd.last_jump_tick</c> and <c>thrower.cmd.coverage</c> on <c>projectile_created</c>
///     against the jump-throw decision a grenade walker takes from an eager rebuild of the same
///     commands: a press is <c>IN_JUMP</c> set where the slot's previous command had it clear, or a
///     sub-tick step pressing it, and a throw is a jump-throw when a press lies in the 27 ticks up
///     to the spawn and the walker had a command in that window. On a matchmaking demo, whose
///     commands are deltas, and on a tournament demo, whose commands are keyframes, both paths.
/// </summary>
[Category("Integration")]
[NotInParallel]
public class ThrowerCommandLaneTests
{
    private const int JumpWindowTicks = 27;
    private const ulong Jump = 1ul << 1;

    private const string Rules = """
        ruleset: throws
        for: each_projectile
        stats:
          first_seen: { capture: projectile.first_seen_tick,  keep: first }
          last_jump:  { capture: thrower.cmd.last_jump_tick, on: raw.projectile_created }
          coverage:   { capture: thrower.cmd.coverage,       on: raw.projectile_created }
        show:
          tables:
            throws:
              per: projectile
              columns:
                - { stat: first_seen, label: first_seen }
                - { stat: last_jump, label: last_jump }
                - { stat: coverage, label: coverage }
        """;

    [Test]
    [Arguments(DemoTestHelper.ReferenceDemoFileName)]
    [Arguments("furia-vs-vitality-m1-mirage.dem")]
    public async Task JumpThrows_AgreeWithTheWalker_OnBothPaths(string fileName)
    {
        string path = DemoTestHelper.FindDemoPath(fileName)
                      ?? throw new SkipTestException($"{fileName} is not on this machine (set {DemoTestHelper.CorpusDirectoryVariable})");
        ParsedDemo demo = DemoParser.Parse(File.ReadAllBytes(path).AsMemory());

        (Dictionary<int, List<(int Tick, bool Rose)>> commands, UserCmdReconstructionStats stats) = Oracle(demo);
        if (stats.Full + stats.Delta == 0)
        {
            throw new SkipTestException($"{fileName} carries no player commands");
        }

        Console.WriteLine($"{fileName}: rebuilt {stats.Full} full, {stats.Delta} delta");

        foreach ((string label, AnalysisRun run) in RoundFactsCorpusTests.RunBothPaths(path, RoundFactsTestSupport.Load(Rules)))
        {
            await Assert.That(run.Build.ExcludedRulesets).IsEmpty()
                .Because($"{label}: {string.Join("; ", run.Build.ExcludedRulesets.SelectMany(e => e.Diagnostics))}");
            await Assert.That(run.Build.DecodesUserCommands).IsTrue().Because(label);

            int compared = 0, jumpThrows = 0, risesAfterTheSpawn = 0, covered = 0;
            foreach (MetricRow row in RoundFactsTestSupport.Table(run, "throws", demo).Rows)
            {
                int slot = RoundFactsTestSupport.Dim(row, "player_slot");
                int spawn = RoundFactsTestSupport.Int(row, "first_seen")!.Value;
                int created = RoundFactsTestSupport.Dim(row, "created_tick");
                int lastJump = RoundFactsTestSupport.Int(row, "last_jump")!.Value;
                double coverage = Convert.ToDouble(row.Values["coverage"], CultureInfo.InvariantCulture);
                string because = $"{label}: slot {slot} spawn {spawn} created {created}";

                List<(int Tick, bool Rose)> seen = commands.GetValueOrDefault(slot) ?? [];
                int recent = seen.Count(c => c.Tick > created - UserCmdReconstructor.CoverageTicks && c.Tick <= created);
                await Assert.That(coverage).IsEqualTo(Math.Min(1.0, recent / (double)UserCmdReconstructor.CoverageTicks))
                    .Within(1e-6).Because(because);

                // The walker: a command in the window, and the latest press inside it.
                bool walkerCovered = seen.Any(c => c.Tick >= spawn - JumpWindowTicks && c.Tick <= spawn);
                int? press = seen.Where(c => c.Rose && c.Tick >= spawn - JumpWindowTicks && c.Tick <= spawn)
                    .Select(c => (int?)c.Tick).Max();
                bool walkerJump = walkerCovered && press is not null;
                bool engineJump = lastJump >= spawn - JumpWindowTicks && lastJump <= spawn;

                if (engineJump != walkerJump)
                {
                    // The lane keeps one tick per slot: a rise between the spawn and the created frame
                    // hides an earlier one inside the window. Nothing else may disagree.
                    await Assert.That(lastJump).IsGreaterThan(spawn).Because(because);
                    await Assert.That(walkerJump).IsTrue().Because(because);
                    risesAfterTheSpawn++;
                }

                await Assert.That(lastJump).IsLessThanOrEqualTo(created).Because(because);
                compared++;
                jumpThrows += engineJump ? 1 : 0;
                covered += walkerCovered ? 1 : 0;
            }

            Console.WriteLine($"{label}: {compared} throws, {covered} with a command in the window, {jumpThrows} jump-throws, "
                              + $"{risesAfterTheSpawn} with a rise between the spawn and the created frame");
            await Assert.That(compared).IsGreaterThan(50).Because(label);
            await Assert.That(jumpThrows).IsGreaterThan(0).Because(label);
            await Assert.That(jumpThrows).IsLessThan(compared).Because(label);
        }
    }

    // Every command per slot at the frame-clock tick of the frame it arrived on, with whether
    // IN_JUMP rose on it, from an eager rebuild the lane does not share.
    private static (Dictionary<int, List<(int Tick, bool Rose)>>, UserCmdReconstructionStats) Oracle(ParsedDemo demo)
    {
        Dictionary<int, List<(int Tick, bool Rose)>> commands = [];
        Dictionary<int, ulong> lastButtons = [];
        UserCmdReconstructor eager = new();
        foreach (DemoFrame frame in demo.Frames)
        {
            int tick = frame.GameTick ?? frame.ServerTick;
            foreach (ReconstructedUserCmd cmd in eager.AdvanceOneFrame(frame))
            {
                CBaseUserCmdPB? b = cmd.Command.Base;
                ulong buttons = b?.ButtonsPb?.Buttonstate1 ?? 0;
                bool rose = (buttons & Jump) != 0 && (lastButtons.GetValueOrDefault(cmd.PlayerSlot) & Jump) == 0
                            || b is not null && b.SubtickMoves.Any(s => s.Button == Jump && s.Pressed);
                lastButtons[cmd.PlayerSlot] = buttons;
                if (!commands.TryGetValue(cmd.PlayerSlot, out List<(int, bool)>? list))
                {
                    commands[cmd.PlayerSlot] = list = [];
                }

                list.Add((tick, rose));
            }
        }

        return (commands, eager.Stats);
    }
}
