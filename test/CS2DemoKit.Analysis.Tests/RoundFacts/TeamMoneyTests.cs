#region

using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Analysis.Catalog;
using CS2DemoKit.Analysis.Output;
using CS2DemoKit.Parser;
using CS2DemoKit.Parser.EntityTracking;
using CS2DemoKit.Parser.GameEvents;
using CS2DemoKit.TestSupport;

#endregion

namespace CS2DemoKit.Analysis.Tests.RoundFacts;

/// <summary>
///     Each player's cash (<c>player.money</c>, off the controller) and its freeze-end sums per side
///     (<c>round.team.money</c> / <c>round.enemies.money</c>), issue #54 piece 5.
/// </summary>
[Category("Integration")]
[NotInParallel]
public class TeamMoneyTests
{
    private const string MoneyRuleset = """
        ruleset: money
        for: each_player
        stats:
          money:
            capture: player.money
            on: raw.round_freeze_end
            per: round
          team_money:
            capture: round.team.money
            on: raw.round_freeze_end
            per: round
          enemies_money:
            capture: round.enemies.money
            on: raw.round_freeze_end
            per: round
        show:
          tables:
            money:
              per: player_round
              columns:
                - { stat: money, label: M }
                - { stat: team_money, label: TM }
                - { stat: enemies_money, label: EM }
        """;

    [Test]
    [Category("Unit")]
    public async Task Catalog_ExposesMoney_AsAPlayerRead_AndTheSideSums()
    {
        CatalogRoot catalog = CatalogResource.Load();
        CatalogProvider money = catalog.Providers.Single(p => p.Name == "entity.controller.money");
        await Assert.That(money.V2Name).IsEqualTo("player.money");
        await Assert.That(money.Scope).IsEqualTo("perPlayer");
        await Assert.That(money.Note).IsNotNull();
        await Assert.That(catalog.Contexts.Any(c => c.V2Name == "round.team.money")).IsTrue();
        await Assert.That(catalog.Contexts.Any(c => c.V2Name == "round.enemies.money")).IsTrue();
    }

    /// <summary>
    ///     At every round's freeze end the captured cash is the controller's <c>m_iAccount</c> as it
    ///     stood before the freeze-end frame, and each side's sum is the sum of its players' cash.
    /// </summary>
    [Test]
    public async Task Sample_MoneyAtFreezeEnd_IsTheControllersAccount_AndTheSumsAddUp()
    {
        ParsedDemo demo = RoundFactsTestSupport.Sample();
        List<Dictionary<int, (int Team, int Money)>> raw = RawMoneyAtFreezeEnds(demo);

        AnalysisRun run = RoundFactsTestSupport.Run(demo, MoneyRuleset);
        MetricTable table = RoundFactsTestSupport.Table(run, "money", demo);

        foreach (IGrouping<int, MetricRow> round in table.Rows.GroupBy(r => RoundFactsTestSupport.Dim(r, "round_number")))
        {
            Dictionary<int, (int Team, int Money)> expected = raw[round.Key - 1];
            Dictionary<int, int> sideSums = expected.Values.GroupBy(v => v.Team)
                .ToDictionary(g => g.Key, g => g.Sum(v => v.Money));

            foreach (MetricRow row in round)
            {
                int slot = RoundFactsTestSupport.Dim(row, "player_slot");
                (int team, int money) = expected[slot];
                await Assert.That(RoundFactsTestSupport.Int(row, "M")).IsEqualTo(money)
                    .Because($"round {round.Key} slot {slot}");
                await Assert.That(RoundFactsTestSupport.Int(row, "TM")).IsEqualTo(sideSums[team]);
                await Assert.That(RoundFactsTestSupport.Int(row, "EM")).IsEqualTo(sideSums[team == 2 ? 3 : 2]);
            }
        }

        // Round 1 on the sample: the pistol round's leftovers.
        MetricRow first = table.Rows.First(r => RoundFactsTestSupport.Dim(r, "round_number") == 1);
        int[] sums = [RoundFactsTestSupport.Int(first, "TM")!.Value, RoundFactsTestSupport.Int(first, "EM")!.Value];
        await Assert.That(string.Join(",", sums.Order())).IsEqualTo("700,750");
    }

    /// <summary>
    ///     The issue reported $3,400 to $4,300 per player in round 1 of the build-10231 nuke demo.
    ///     Read through the engine at the freeze-end sample the sides hold 2,600 and 750: the
    ///     pistol-round leftovers, nothing implausible. Recorded so a decode change that moves them
    ///     is seen.
    /// </summary>
    [Test]
    [Category("Corpus")]
    public async Task Corpus_Build10231Nuke_RoundOneMoney_IsPlausible()
    {
        string path = DemoTestHelper.RequireDemo(GameRulesProviderTests.Build10231Nuke);
        ParsedDemo demo = DemoTestHelper.GetOrParse(path);
        AnalysisRun run = RoundFactsTestSupport.Run(demo, MoneyRuleset);
        MetricTable table = RoundFactsTestSupport.Table(run, "money", demo);

        List<MetricRow> first = table.Rows.Where(r => RoundFactsTestSupport.Dim(r, "round_number") == 1).ToList();
        HashSet<int> sums = first.Select(r => RoundFactsTestSupport.Int(r, "TM")!.Value).ToHashSet();
        Console.WriteLine($"[money] round 1 side sums: {string.Join(", ", sums.Order())}; per player: "
                          + string.Join("/", first.Select(r => RoundFactsTestSupport.Int(r, "M"))));
        await Assert.That(string.Join(",", sums.Order())).IsEqualTo("750,2600");
        await Assert.That(first.All(r => RoundFactsTestSupport.Int(r, "M") is >= 0 and <= 800)).IsTrue();
    }

    /// <summary>
    ///     Walks the demo raw: for each round_freeze_end after the last begin_new_match, each
    ///     connected controller's team and <c>m_pInGameMoneyServices.m_iAccount</c> as they stood
    ///     after the previous frame (the pre-frame state an event reads).
    /// </summary>
    private static List<Dictionary<int, (int Team, int Money)>> RawMoneyAtFreezeEnds(ParsedDemo demo)
    {
        List<Dictionary<int, (int Team, int Money)>> rounds = [];
        EntityStateLayer layer = new();
        foreach (DemoFrame frame in demo.Frames)
        {
            foreach (NetMessage message in frame.DecodedMessages)
            {
                if (message is not GameEventMessage gem)
                {
                    continue;
                }

                if (gem.DecodedEvent.Name == "begin_new_match")
                {
                    rounds.Clear();
                }
                else if (gem.DecodedEvent.Name == "round_freeze_end")
                {
                    rounds.Add(ReadAccounts(layer.Tracker));
                }
            }

            layer.Apply(frame);
        }

        return rounds;
    }

    private static Dictionary<int, (int Team, int Money)> ReadAccounts(EntityTracker tracker)
    {
        Dictionary<int, (int, int)> accounts = [];
        for (int slot = 0; slot < 64; slot++)
        {
            EntityState? controller = tracker.CurrentEntities[slot + 1];
            if (controller is null || controller.ClassName != "CCSPlayerController")
            {
                continue;
            }

            int team = controller.TryGet<int>("m_iTeamNum") ?? -1;
            if (team is 2 or 3 && controller.TryGet<int>("m_pInGameMoneyServices.m_iAccount") is { } money)
            {
                accounts[slot] = (team, money);
            }
        }

        return accounts;
    }
}
