#region

using System.Globalization;
using CS2DemoKit.Analysis.Observation;
using CS2DemoKit.Analysis.Output;
using CS2DemoKit.Analysis.Tests.RoundFacts;
using CS2DemoKit.TestSupport;

#endregion

namespace CS2DemoKit.Analysis.Tests.RulesV2;

/// <summary>
///     <c>player.team</c> on full matches, a matchmaking demo and a tournament demo, against the
///     controller's <c>m_iTeamNum</c> that a frame observer reads. The value is fed by
///     <c>player_team</c> events only, so this is the check that every side change on these demos
///     comes with one. Both demos live outside the repository: point <c>CS2DEMOKIT_CORPUS_DIR</c> at
///     the folder holding each, and the other skips.
/// </summary>
[Category("Integration")]
[NotInParallel]
public class PlayerTeamCorpusTests
{
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
    ///     Every cadence row's captured team equals the controller's team at that row's tick, for
    ///     every player on a side.
    /// </summary>
    [Test]
    [Arguments("003816809596253634708_1168926414.dem")]
    [Arguments("vitality-vs-fut-m2-dust2.dem")]
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
            if (captured != expected)
            {
                mismatched++;
                first ??= $"slot {slot} tick {tick}: captured {captured}, controller {expected}";
            }
        }

        Console.WriteLine($"{file}: {samples.Rows.Count} rows, {compared} on a side, {mismatched} differ");
        await Assert.That(compared).IsGreaterThan(10_000);
        await Assert.That(mismatched).IsEqualTo(0).Because(first ?? "");
    }

    /// <summary>
    ///     Each player on a side in round 1 is on the other side in round 13, the first round after
    ///     the halftime swap, and the team read at each freeze end is the controller's.
    /// </summary>
    [Test]
    [Arguments("003816809596253634708_1168926414.dem")]
    [Arguments("vitality-vs-fut-m2-dust2.dem")]
    public async Task RoundTeam_SwapsAtHalftime(string file)
    {
        string path = DemoTestHelper.RequireDemo(file);
        AnalysisRun run = DemoAnalysis.Run(path, RoundFactsTestSupport.Load(Rules));
        MetricTable rounds = RoundFactsTestSupport.Table(run, "team_rounds");

        Dictionary<(int Slot, int Round), int> team = [];
        foreach (MetricRow row in rounds.Rows)
        {
            if (RoundFactsTestSupport.Int(row, "round_team") is { } value)
            {
                team[(RoundFactsTestSupport.Dim(row, "player_slot"), RoundFactsTestSupport.Dim(row, "round_number"))] = value;
            }
        }

        List<int> players = team.Keys.Where(k => k.Round == 1 && team[k] is 2 or 3).Select(k => k.Slot).ToList();
        Console.WriteLine($"{file}: round 1 sides "
                          + string.Join(" ", players.Select(s => string.Create(CultureInfo.InvariantCulture,
                              $"s{s}:{team[(s, 1)]}->{team.GetValueOrDefault((s, 13))}"))));
        await Assert.That(players.Count).IsEqualTo(10);
        foreach (int slot in players)
        {
            await Assert.That(team.GetValueOrDefault((slot, 13))).IsEqualTo(5 - team[(slot, 1)])
                .Because($"slot {slot}");
            await Assert.That(team.GetValueOrDefault((slot, 12))).IsEqualTo(team[(slot, 1)])
                .Because($"slot {slot} before the swap");
        }
    }
}
