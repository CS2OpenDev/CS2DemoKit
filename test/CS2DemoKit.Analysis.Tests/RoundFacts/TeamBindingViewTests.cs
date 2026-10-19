#region

using CS2DemoKit.Analysis.Observation;
using CS2DemoKit.Analysis.Output;
using CS2DemoKit.Analysis.RulesetsV2.Resolve;
using CS2DemoKit.Parser;
using CS2DemoKit.Parser.EntityTracking;
using CS2DemoKit.Parser.GameEvents;
using CS2DemoKit.TestSupport;

using CS2OpenSchema.Protos;
using TUnit.Core.Exceptions;

#endregion

namespace CS2DemoKit.Analysis.Tests.RoundFacts;

/// <summary>
///     <c>round_won</c> and <c>round_lost</c> filter on the round's winner against the ruleset player's
///     live team (views.yaml's <c>binding: team</c>). The binding was documented and never lowered:
///     both views fired for every player, so each read 1 in every round.
/// </summary>
[Category("Integration")]
[NotInParallel]
public class TeamBindingViewTests
{
    [Test]
    [Category("Unit")]
    public async Task TeamViews_DeclareTheWinnerReads_TheBindingNeeds()
    {
        CheckedRuleset rs = RoundFactsTestSupport.Checked("""
            ruleset: wl
            for: each_player
            stats:
              won:
                count: round_won
                per: round
              lost_any:
                count: round_lost
                match: { actor: any }
                per: round
            """);

        await Assert.That(rs.Stats[0].DeclaredReads).Contains("enrich.round.has_winner");
        await Assert.That(rs.Stats[0].DeclaredReads).Contains("enrich.round.winner_team");
        // actor: any suppresses the binding, and with it the reads it would make.
        await Assert.That(rs.Stats[1].DeclaredReads).DoesNotContain("enrich.round.winner_team");
    }

    /// <summary>
    ///     A subject on neither side (a spectator or coach on team 1, or a slot whose team was never
    ///     seen) neither wins nor loses a round. The winner is always 2 or 3, so a lost binding of
    ///     <c>winner_team != player.team</c> alone had them lose every decided round. Synthetic
    ///     frames: a terrorist, a counter-terrorist and a spectator, one round closed with the
    ///     counter-terrorists as the (derived) winner.
    /// </summary>
    [Test]
    [Category("Unit")]
    public async Task EachPlayer_ASubjectOnNeitherSide_NeitherWinsNorLoses()
    {
        ParsedDemo sample = RoundFactsTestSupport.Sample();
        Dictionary<int, PlayerInfo> players = new()
        {
            [0] = new PlayerInfo(0, "T", 0UL, 0, 2, false),
            [1] = new PlayerInfo(1, "CT", 0UL, 1, 3, false),
            [2] = new PlayerInfo(2, "Spectator", 0UL, 2, 1, false)
        };

        DemoFrame[] frames =
        [
            Frame(1,
                TestGameEvents.PlayerTeam(0, 2),
                TestGameEvents.PlayerTeam(1, 3),
                TestGameEvents.PlayerTeam(2, 1)),
            Frame(2, TestGameEvents.RoundFreezeEnd(2, 2, 2)),
            Frame(3, TestGameEvents.RoundOfficiallyEnded(3, 3, 3))
        ];

        ParsedDemo demo = new(frames, [], players, null, "de_anytown", 3, 1f / 64f, "test", "test", "csgo",
            sample.BuildNumber, 0, 0, "valve_demo_2", "", "", sample.Profile);

        AnalysisRun run = RoundFactsTestSupport.Run(demo, """
            ruleset: wl
            for: each_player
            stats:
              won:
                count: round_won
                per: match
              lost:
                count: round_lost
                per: match
              closed:
                count: round_ended
                per: match
            show:
              tables:
                wl:
                  per: player_match
                  columns:
                    - { stat: won, label: Won }
                    - { stat: lost, label: Lost }
                    - { stat: closed, label: Closed }
            """);

        MetricTable table = RoundFactsTestSupport.Table(run, "wl", demo);
        string Row(int slot)
        {
            MetricRow row = table.Rows.Single(r => RoundFactsTestSupport.Dim(r, "player_slot") == slot);
            return $"{RoundFactsTestSupport.Int(row, "Won") ?? 0}/{RoundFactsTestSupport.Int(row, "Lost") ?? 0}/"
                   + $"{RoundFactsTestSupport.Int(row, "Closed") ?? 0}";
        }

        // The round closed for all three; the CT side won it.
        await Assert.That(Row(0)).IsEqualTo("0/1/1");
        await Assert.That(Row(1)).IsEqualTo("1/0/1");
        await Assert.That(Row(2)).IsEqualTo("0/0/1");
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

    /// <summary>
    ///     Each closed round, exactly the players on the side the server named read <c>won</c>, and
    ///     exactly the players on the other side read <c>lost</c>. Checking only that the two sum to
    ///     one would pass with the binding inverted, so the winner comes from <c>round_decided</c> and
    ///     the sides from the <c>for: each_team</c> freeze-end rosters.
    /// </summary>
    [Test]
    public async Task EachPlayer_WonGoesToTheDecidedWinnersSide()
    {
        ParsedDemo demo = RoundFactsTestSupport.Sample();
        AnalysisRun run = RoundFactsTestSupport.Run(demo, """
            ruleset: wl
            for: each_player
            stats:
              won:
                count: round_won
                per: round
              lost:
                count: round_lost
                per: round
              closed:
                count: round_ended
                per: round
            show:
              tables:
                wl:
                  per: player_round
                  columns:
                    - { stat: won, label: Won }
                    - { stat: lost, label: Lost }
                    - { stat: closed, label: Closed }
            """, """
            ruleset: rosters
            for: each_team
            stats:
              side:
                capture: team.side
                on: round_ended
                per: round
            show:
              tables:
                rosters:
                  per: team_round
                  columns:
                    - { stat: side, label: S }
            """, """
            ruleset: decided
            for: match
            stats:
              winners:
                capture: event.Winner
                on: round_decided
                keep: list
                per: match
            """);

        MetricTable table = RoundFactsTestSupport.Table(run, "wl", demo);
        MetricTable rosters = RoundFactsTestSupport.Table(run, "rosters", demo);
        int[] winners = RoundFactsCorpusTests.Ints(run, "decided.winners");

        int closedRows = 0, checkedRounds = 0;
        foreach (MetricRow row in table.Rows)
        {
            int w = RoundFactsTestSupport.Int(row, "Won") ?? 0;
            int l = RoundFactsTestSupport.Int(row, "Lost") ?? 0;
            int c = RoundFactsTestSupport.Int(row, "Closed") ?? 0;
            if (c == 0)
            {
                await Assert.That(w + l).IsEqualTo(0);
                continue;
            }

            closedRows++;
            await Assert.That(w + l).IsEqualTo(1)
                .Because($"round {RoundFactsTestSupport.Dim(row, "round_number")} slot {row.Dimensions["player_slot"]}");
        }

        foreach (IGrouping<int, MetricRow> round in rosters.Rows.GroupBy(r => RoundFactsTestSupport.Dim(r, "round_number")))
        {
            int winner = winners[round.Key - 1];
            List<MetricRow> roundPlayers = table.Rows
                .Where(r => RoundFactsTestSupport.Dim(r, "round_number") == round.Key)
                .ToList();
            foreach (MetricRow side in round)
            {
                int sideNumber = RoundFactsTestSupport.Dim(side, "side");
                bool sideWon = sideNumber == winner;
                int[] slots = (side.Dimensions["slots"]?.ToString() ?? "")
                    .Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Select(int.Parse)
                    .ToArray();
                await Assert.That(slots.Length).IsEqualTo(5).Because($"round {round.Key} side {sideNumber}");
                foreach (int slot in slots)
                {
                    MetricRow member = roundPlayers.Single(r => RoundFactsTestSupport.Dim(r, "player_slot") == slot);
                    string because = $"round {round.Key}: slot {slot} on side {sideNumber}, server winner {winner}";
                    await Assert.That(RoundFactsTestSupport.Int(member, "Won") ?? 0).IsEqualTo(sideWon ? 1 : 0)
                        .Because(because);
                    await Assert.That(RoundFactsTestSupport.Int(member, "Lost") ?? 0).IsEqualTo(sideWon ? 0 : 1)
                        .Because(because);
                }
            }

            checkedRounds++;
        }

        Console.WriteLine($"[binding] rows={closedRows} rounds={checkedRounds} winners={string.Join(",", winners)}");
        await Assert.That(closedRows).IsGreaterThan(0);
        await Assert.That(checkedRounds).IsEqualTo(winners.Length);
        // Both sides win at least once on the sample, so neither Won nor Lost can pass by being
        // constant.
        await Assert.That(winners.Distinct().Count()).IsEqualTo(2);
    }

    private const string CoachRuleset = """
        ruleset: coach
        for: each_player
        stats:
          coaching:
            capture: player.coaching_team
            on: round_ended
            per: match
        show:
          tables:
            coach:
              per: player_match
              columns:
                - { stat: coaching, label: Coaching }
        """;

    /// <summary>
    ///     <c>entity.controller.coaching_team</c> through a <see cref="FrameView" /> on the sample, which
    ///     carries no coach: every value read is 0, and on the last frame every slot with a
    ///     <c>CCSPlayerController</c> reads a present 0, including slots with no player pawn bound.
    ///     A capture cell reads 0 for "no value" as well, so the presence check goes through the observer.
    /// </summary>
    [Test]
    [Arguments(1)]
    [Arguments(null)]
    public async Task CoachingTeam_ReadsEveryController_WithOrWithoutAPawn(int? maxDegreeOfParallelism)
    {
        ParsedDemo demo = RoundFactsTestSupport.Sample();
        CoachRecorder observer = new();
        DemoAnalysis.Run(demo, [], new AnalysisOptions
        {
            MaxDegreeOfParallelism = maxDegreeOfParallelism,
            CaptureSnapshots = false,
            ObservedProviders = [CoachingTeam, EntityIndex],
            FrameObserver = observer
        });

        Dictionary<int, int> controllers = LastFrameControllers(demo);
        int[] pawnless = controllers.Keys.Where(slot => !observer.LastBound[slot]).Order().ToArray();
        Console.WriteLine($"[coach] valued slot-frames {observer.Valued}, pawnless valued {observer.PawnlessValued}, "
                          + $"controllers {string.Join(",", controllers.Keys.Order())}, pawnless at the end {string.Join(",", pawnless)}");
        await Assert.That(observer.First).IsNull().Because(observer.First ?? "");
        await Assert.That(controllers.Values.Where(team => team != 0)).IsEmpty();
        foreach (int slot in controllers.Keys)
        {
            await Assert.That(observer.Last[slot]).IsEqualTo(0).Because($"slot {slot} on the last frame");
        }

        foreach (int slot in demo.Players.Values.Where(p => p.Team is 2 or 3 && !p.IsHltv).Select(p => p.Slot))
        {
            await Assert.That(controllers.ContainsKey(slot)).IsTrue().Because($"player slot {slot}");
        }

        // A pawn-rooted read never reaches these, which is the coach's case.
        await Assert.That(pawnless).IsNotEmpty();
        await Assert.That(observer.PawnlessValued).IsGreaterThan(0);
    }

    private const string CoachingTeam = "entity.controller.coaching_team";
    private const string EntityIndex = "entity.pawn.entity_index";

    private sealed class CoachRecorder : IFrameObserver
    {
        public readonly int?[] Last = new int?[64];
        public readonly bool[] LastBound = new bool[64];
        public string? First;
        public int Valued;
        public int PawnlessValued;

        public void OnFrame(FrameView frame)
        {
            ObservedProvider coach = frame.Provider(CoachingTeam);
            ObservedProvider index = frame.Provider(EntityIndex);
            for (int slot = 0; slot < 64 && First is null; slot++)
            {
                bool has = frame.TryReadInt(coach, slot, out int team);
                bool bound = frame.TryReadInt(index, slot, out _);
                Last[slot] = has ? team : null;
                LastBound[slot] = bound;
                if (has && team != 0)
                {
                    First = $"frame {frame.FrameIndex} slot {slot}: coaching_team {team}, pawn bound {bound}";
                    return;
                }

                Valued += has ? 1 : 0;
                PawnlessValued += has && !bound ? 1 : 0;
            }
        }

        public void Finish()
        {
        }
    }

    /// <summary>
    ///     On a tournament demo, the slots reading a non-zero <c>player.coaching_team</c> at the last
    ///     <c>round_ended</c> are exactly the slots whose controller (entity index slot + 1) holds a non-zero
    ///     <c>m_iCoachingTeam</c> on the last frame, with the same side. Skips without the demo, and after the
    ///     comparison when the demo carries no coach.
    /// </summary>
    [Test]
    public async Task EachPlayer_CoachingTeam_MatchesTheControllersOnATournamentDemo()
    {
        ParsedDemo demo = DemoTestHelper.GetOrParse(DemoTestHelper.RequireDemo("furia-vs-vitality-m1-mirage.dem"));
        Dictionary<int, int> expected = LastFrameCoaches(demo);
        MetricTable table = RoundFactsTestSupport.Table(RoundFactsTestSupport.Run(demo, CoachRuleset), "coach", demo);
        Dictionary<int, int> actual = table.Rows
            .Select(r => (Slot: RoundFactsTestSupport.Dim(r, "player_slot"), Team: RoundFactsTestSupport.Int(r, "Coaching") ?? 0))
            .Where(r => r.Team != 0)
            .ToDictionary(r => r.Slot, r => r.Team);

        Console.WriteLine($"[coach] controllers={string.Join(",", expected.Select(kv => $"{kv.Key}:{kv.Value}"))} "
                          + $"rows={string.Join(",", actual.Select(kv => $"{kv.Key}:{kv.Value}"))}");
        await Assert.That(actual).IsEquivalentTo(expected);
        if (expected.Count == 0)
        {
            // furia m1 and the six other tournament demos on hand hold m_iCoachingTeam 0 on every
            // controller on every frame, so the non-zero path has no demo to pin it.
            throw new SkipTestException("no controller on this demo carries a coaching team");
        }
    }

    // Slot to coached side, off the controllers at the last frame, the way the host's library read takes it.
    private static Dictionary<int, int> LastFrameCoaches(ParsedDemo demo) =>
        LastFrameControllers(demo).Where(kv => kv.Value != 0 && demo.Players.ContainsKey(kv.Key))
            .ToDictionary(kv => kv.Key, kv => kv.Value);

    // Every slot whose entity index slot + 1 holds a CCSPlayerController on the last frame, to its m_iCoachingTeam.
    private static Dictionary<int, int> LastFrameControllers(ParsedDemo demo)
    {
        EntityTracker tracker = new() { StoreClassFilter = new HashSet<string>(StringComparer.Ordinal) { "CCSPlayerController" } };
        tracker.ReplayToIndex(demo.Frames.Count - 1, demo.Frames);
        Dictionary<int, int> controllers = [];
        for (int slot = 0; slot < 64; slot++)
        {
            if (tracker.CurrentEntities[slot + 1] is { } controller
                && controller.ClassName == "CCSPlayerController")
            {
                controllers[slot] = controller.TryGet<int>("m_iCoachingTeam") ?? 0;
            }
        }

        return controllers;
    }
}
