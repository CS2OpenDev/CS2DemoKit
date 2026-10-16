#region

using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Analysis.Nodes;
using CS2DemoKit.Analysis.Tests.RoundFacts;
using CS2DemoKit.Parser;
using CS2DemoKit.Parser.GameEvents;
using CS2DemoKit.TestSupport;
using CS2OpenSchema.Protos;

#endregion

namespace CS2DemoKit.Analysis.Tests.RulesV2;

/// <summary>
///     <c>player.team</c> read at every site a per-player stat exposes, across a team change. Only
///     <c>where:</c> used to lower: a value selector had no team binding and a node site had no node,
///     so every other site excluded its ruleset with <c>Unknown player member: team</c>.
/// </summary>
[Category("Unit")]
[NotInParallel]
public class PlayerTeamReadSiteTests
{
    private const string Rules = """
        ruleset: pt
        for: each_player
        stats:
          kills:
            count: kill
            per: round
          where_ct:
            count: kill
            where: "player.team == 3"
            per: round
          while_ct:
            count: kill
            while: "player.team == 3"
            per: round
          team_sum:
            sum: player.team
            on: kill
            per: round
          first_team:
            capture: player.team
            on: kill
            keep: first
            per: round
          last_team:
            capture: player.team
            on: kill
            per: round
          low_team:
            capture: player.team
            on: kill
            keep: min
            per: round
          high_team:
            capture: player.team
            on: kill
            keep: max
            per: round
          kill_teams:
            capture: player.team
            on: kill
            keep: list
            per: round
          swap_teams:
            capture: player.team
            on: raw.player_team
            keep: list
            per: match
          kills_plus_team:
            compute: "kills + player.team"
            per: round
          is_ct:
            flag:
              when: "player.team == 3"
            per: match
          team_by_weapon:
            bucket: kill
            key: event.Weapon
            value: player.team
            per: match
        """;

    /// <summary>
    ///     One round: slot 0 kills on T, switches to CT, and kills again. Every site reads the team
    ///     the player had at that moment, and a capture on the <c>player_team</c> itself reads the new
    ///     team.
    /// </summary>
    [Test]
    public async Task EverySite_ReadsTheTeamAtThatMoment()
    {
        Dictionary<string, string?> values = RunSlotZero(Rules);

        await Assert.That(values["kills"]).IsEqualTo("2");
        await Assert.That(values["where_ct"]).IsEqualTo("1");
        await Assert.That(values["while_ct"]).IsEqualTo("1");
        await Assert.That(values["team_sum"]).IsEqualTo("5");
        await Assert.That(values["first_team"]).IsEqualTo("2");
        await Assert.That(values["last_team"]).IsEqualTo("3");
        await Assert.That(values["low_team"]).IsEqualTo("2");
        await Assert.That(values["high_team"]).IsEqualTo("3");
        await Assert.That(values["kill_teams"]).IsEqualTo("2,3");
        // raw.player_team fires for every player's event, and each read is slot 0's team after the
        // event has applied: 0 or 2 here would be a read of the team before the change.
        await Assert.That(values["swap_teams"]).IsEqualTo("2,2,3,3");
        await Assert.That(values["kills_plus_team"]).IsEqualTo("5.0");
        await Assert.That(values["is_ct"]).IsEqualTo("True");
        await Assert.That(values["team_by_weapon"]).IsEqualTo("ak47=5");
    }

    /// <summary>
    ///     A <c>when:</c> over <c>player.team</c> follows the change: the flag is false while the player
    ///     is on T and turns true on the <c>player_team</c> that moves them to CT, with no other
    ///     message in between to recompute it.
    /// </summary>
    [Test]
    public async Task WhenFlag_TurnsOnWithThePlayerTeamEvent()
    {
        Dictionary<string, string?> before = RunSlotZero(Rules, stopBeforeSwitch: true);
        await Assert.That(before["is_ct"]).IsEqualTo("False");
        await Assert.That(before["last_team"]).IsEqualTo("2");
    }

    private static Dictionary<string, string?> RunSlotZero(string rules, bool stopBeforeSwitch = false)
    {
        ParsedDemo sample = RoundFactsTestSupport.Sample();
        Dictionary<int, PlayerInfo> players = new()
        {
            [0] = new PlayerInfo(0, "A", 0UL, 0, 2, false),
            [1] = new PlayerInfo(1, "B", 0UL, 1, 3, false)
        };

        List<DemoFrame> frames =
        [
            Frame(1, TestGameEvents.PlayerTeam(0, 2), TestGameEvents.PlayerTeam(1, 3)),
            Frame(2, TestGameEvents.RoundFreezeEnd(2, 2, 2)),
            Frame(3, TestGameEvents.PlayerDeath(userId: 1, attacker: 0, weapon: "ak47", frameNumber: 3, serverTick: 3,
                gameTick: 3))
        ];
        if (!stopBeforeSwitch)
        {
            frames.Add(Frame(4, TestGameEvents.PlayerTeam(0, 3, 2), TestGameEvents.PlayerTeam(1, 2, 3)));
            frames.Add(Frame(5, TestGameEvents.PlayerDeath(userId: 1, attacker: 0, weapon: "ak47", frameNumber: 5,
                serverTick: 5, gameTick: 5)));
            frames.Add(Frame(6, TestGameEvents.RoundOfficiallyEnded(6, 6, 6)));
        }

        ParsedDemo demo = new(frames, [], players, null, "de_anytown", frames.Count, 1f / 64f, "test", "test", "csgo",
            sample.BuildNumber, 0, 0, "valve_demo_2", "", "", sample.Profile);
        AnalysisRun run = RoundFactsTestSupport.Run(demo, rules);
        PerPlayerNodeTemplate.MaterializedPlayer slotZero = run.MaterializedPlayers.Single(p => p.PlayerSlot == 0);
        Dictionary<string, string?> values = new(StringComparer.Ordinal);
        foreach ((string id, StateNode node) in slotZero.NodesByRuleId!)
        {
            if (id.StartsWith("pt.", StringComparison.Ordinal))
            {
                values[id[3..]] = node switch
                {
                    BoolNode flag => flag.IsActive.ToString(),
                    KeyedCounterNode keyed => string.Join(",", keyed.Buckets.Select(kv => $"{kv.Key}={kv.Value}")),
                    _ => node.GetDisplayValue()
                };
            }
        }

        return values;
    }

    private static DemoFrame Frame(int tick, params GameEvent[] events) => new()
    {
        CommandKind = EDemoCommands.DemPacket,
        FrameNumber = tick,
        ServerTick = tick,
        RawStart = 0,
        RawLength = 1,
        HeaderLength = 1,
        IsCompressed = false,
        MessageList = [.. events.Select(e => (NetMessage)GameEventMessage.ForSynthesizedEvent(e))]
    };
}
