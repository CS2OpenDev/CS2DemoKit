#region

using System.Globalization;
using CS2DemoKit.Analysis.Observation;
using CS2DemoKit.Analysis.Output;
using CS2DemoKit.Analysis.Tests.RoundFacts;
using CS2DemoKit.TestSupport;

#endregion

namespace CS2DemoKit.Analysis.Tests.RulesV2;

/// <summary>
///     <c>player.team</c> on full matches, a matchmaking demo, a tournament demo and a matchmaking
///     demo that goes to overtime, against the controller's <c>m_iTeamNum</c> that a frame observer
///     reads. The value is fed by <c>player_team</c> events only, so this is the check that every side
///     change on these demos comes with one. The first two live outside the repository: point
///     <c>CS2DEMOKIT_CORPUS_DIR</c> at the folder holding each, and the other skips. The overtime
///     demo is under the repository's <c>demos/</c>.
/// </summary>
[Category("Integration")]
[NotInParallel]
public class PlayerTeamCorpusTests
{
    // 29 rounds: the halftime swap before round 13 and the overtime swap before round 28.
    private const string OvertimeDemo = "match730_003770625492944683328_1992712646_129.dem";

    private const string Rules = """
        ruleset: team_probe
        for: each_player
        cadences:
          second: { ticks: 64, anchor: round_freeze_end, until: round_ended }
        stats:
          team_now:
            capture: player.team
            on: cadence.second
            per: match
          round_team:
            capture: player.team
            on: raw.round_freeze_end
            per: round
        show:
          tables:
            team_samples:
              per: player_event
              on: cadence.second
              columns:
                - { stat: team_now, label: team_now }
            team_rounds:
              per: player_round
              columns:
                - { stat: round_team, label: round_team }
        """;

    private sealed class ControllerTeams : IFrameObserver
    {
        private readonly int[] _last = Enumerable.Repeat(-1, 64).ToArray();

        // Each slot's controller team changes, as (tick, team), in read order.
        public readonly List<(int Tick, int Team)>[] Changes =
            Enumerable.Range(0, 64).Select(_ => new List<(int, int)>()).ToArray();

        public void OnFrame(FrameView frame)
        {
            for (int slot = 0; slot < 64; slot++)
            {
                int team = frame.Team(slot);
                if (team != _last[slot])
                {
                    _last[slot] = team;
                    Changes[slot].Add((frame.Tick, team));
                }
            }
        }

        public void Finish()
        {
        }

        // The team at the end of the last frame at or before the tick.
        public int At(int slot, int tick)
        {
            int team = -1;
            foreach ((int changeTick, int value) in Changes[slot])
            {
                if (changeTick > tick)
                {
                    break;
                }

                team = value;
            }

            return team;
        }
    }

    /// <summary>
    ///     Every cadence row's captured team and its <c>team</c> dimension equal the controller's team
    ///     at that row's tick, for every player on a side.
    /// </summary>
    [Test]
    [Arguments("003816809596253634708_1168926414.dem")]
    [Arguments("vitality-vs-fut-m2-dust2.dem")]
    [Arguments(OvertimeDemo)]
    public async Task Capture_EqualsTheControllerTeam_OnEveryCadenceRow(string file)
    {
        string path = DemoTestHelper.RequireDemo(file);
        ControllerTeams controller = new();
        AnalysisRun run = DemoAnalysis.Run(path, RoundFactsTestSupport.Load(Rules),
            new AnalysisOptions { FrameObserver = controller });
        await Assert.That(run.Build.ExcludedRulesets).IsEmpty();

        MetricTable samples = RoundFactsTestSupport.Table(run, "team_samples");
        int compared = 0, mismatched = 0;
        string? first = null;
        foreach (MetricRow row in samples.Rows)
        {
            int slot = RoundFactsTestSupport.Dim(row, "player_slot");
            int tick = RoundFactsTestSupport.Dim(row, "tick");
            int captured = RoundFactsTestSupport.Int(row, "team_now") ?? -1;
            int expected = controller.At(slot, tick);
            if (expected is not (2 or 3))
            {
                continue;
            }

            compared++;
            if (captured != expected || RoundFactsTestSupport.Dim(row, "team") != expected)
            {
                mismatched++;
                first ??= $"slot {slot} tick {tick}: captured {captured}, row team {row.Dimensions["team"]}, controller {expected}";
            }
        }

        Console.WriteLine($"{file}: {samples.Rows.Count} rows, {compared} on a side, {mismatched} differ");
        await Assert.That(compared).IsGreaterThan(10_000);
        await Assert.That(mismatched).IsEqualTo(0).Because(first ?? "");
    }

    /// <summary>
    ///     Each player on a side in round 1 is on the other side in round 13, the first round after
    ///     the halftime swap, and still on it in round 12; every <c>player_round</c> row's <c>team</c> is
    ///     the team captured at that round's freeze end.
    /// </summary>
    [Test]
    [Arguments("003816809596253634708_1168926414.dem")]
    [Arguments("vitality-vs-fut-m2-dust2.dem")]
    public async Task RoundTeam_SwapsAtHalftime(string file)
    {
        (Dictionary<(int Slot, int Round), int> team, int rowTeamDiffers) = RoundTeams(file);

        List<int> players = team.Keys.Where(k => k.Round == 1 && team[k] is 2 or 3).Select(k => k.Slot).ToList();
        Console.WriteLine($"{file}: round 1 sides "
                          + string.Join(" ", players.Select(s => string.Create(CultureInfo.InvariantCulture,
                              $"s{s}:{team[(s, 1)]}->{team.GetValueOrDefault((s, 13))}"))));
        await Assert.That(players.Count).IsEqualTo(10);
        await Assert.That(rowTeamDiffers).IsEqualTo(0).Because("a player_round row's team is its freeze-end team");
        foreach (int slot in players)
        {
            await Assert.That(team.GetValueOrDefault((slot, 13))).IsEqualTo(5 - team[(slot, 1)])
                .Because($"slot {slot}");
            await Assert.That(team.GetValueOrDefault((slot, 12))).IsEqualTo(team[(slot, 1)])
                .Because($"slot {slot} before the swap");
        }
    }

    /// <summary>
    ///     In overtime the sides hold for the first three rounds and swap for the next three: each
    ///     player is on the same side in rounds 24 and 25 and on the other side in round 28.
    /// </summary>
    [Test]
    public async Task RoundTeam_SwapsAtTheOvertimeHalf()
    {
        (Dictionary<(int Slot, int Round), int> team, int rowTeamDiffers) = RoundTeams(OvertimeDemo);
        List<int> players = team.Keys.Where(k => k.Round == 24 && team[k] is 2 or 3).Select(k => k.Slot).ToList();
        await Assert.That(players.Count).IsEqualTo(10);
        await Assert.That(rowTeamDiffers).IsEqualTo(0);
        foreach (int slot in players)
        {
            await Assert.That(team.GetValueOrDefault((slot, 25))).IsEqualTo(team[(slot, 24)]).Because($"slot {slot}");
            await Assert.That(team.GetValueOrDefault((slot, 27))).IsEqualTo(team[(slot, 24)]).Because($"slot {slot}");
            await Assert.That(team.GetValueOrDefault((slot, 28))).IsEqualTo(5 - team[(slot, 24)]).Because($"slot {slot}");
        }
    }

    // Each round row's freeze-end team by (slot, round), and how many rows' team dimension differs from it.
    private static (Dictionary<(int Slot, int Round), int> Team, int RowTeamDiffers) RoundTeams(string file)
    {
        string path = DemoTestHelper.RequireDemo(file);
        AnalysisRun run = DemoAnalysis.Run(path, RoundFactsTestSupport.Load(Rules));
        MetricTable rounds = RoundFactsTestSupport.Table(run, "team_rounds");

        Dictionary<(int Slot, int Round), int> team = [];
        int rowTeamDiffers = 0;
        foreach (MetricRow row in rounds.Rows)
        {
            if (RoundFactsTestSupport.Int(row, "round_team") is { } value)
            {
                team[(RoundFactsTestSupport.Dim(row, "player_slot"), RoundFactsTestSupport.Dim(row, "round_number"))] = value;
                if (RoundFactsTestSupport.Dim(row, "team") != value)
                {
                    rowTeamDiffers++;
                }
            }
        }

        return (team, rowTeamDiffers);
    }
}
