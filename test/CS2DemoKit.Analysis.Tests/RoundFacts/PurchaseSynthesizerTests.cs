#region

using CS2DemoKit.Analysis.Graphs;
using CS2DemoKit.Analysis.Profiles;
using CS2DemoKit.Analysis.RulesetsV2.Model;
using CS2DemoKit.TestSupport;

#endregion

namespace CS2DemoKit.Analysis.Tests.RoundFacts;

/// <summary>
///     <c>item_purchase</c> / <c>item_refund</c> on a tournament demo: every purchase's cost is a rise of
///     the buyer's cash spent, so per player the purchases less the refunds equal the cash spent the rounds
///     closed on, and the item comes from the same frame's pickup almost always.
/// </summary>
[Category("Integration")]
[NotInParallel]
public class PurchaseSynthesizerTests
{
    private const string Rules = """
        ruleset: econ
        for: each_player
        stats:
          bought:
            sum: event.Cost
            on: item_purchased
            where: "match.live or match.freeze_period"
            per: match
          buys:
            count: item_purchased
            where: "match.live or match.freeze_period"
            per: match
          unknown:
            count: item_purchased
            where: '(match.live or match.freeze_period) and event.Item == "unknown"'
            per: match
          refunded:
            sum: event.Refund
            on: item_refunded
            where: "match.live or match.freeze_period"
            per: match
          awps:
            count: item_purchased
            match: { item: awp }
            where: "match.live or match.freeze_period"
            per: match
          final:
            sum: player.cash_spent
            on: round_ended
            while: match.live
            per: match
        """;

    [Test]
    public async Task PortoDust2_PurchasesAddUpToTheCashSpent()
    {
        string path = DemoTestHelper.RequireDemo(EconomyAndTimeoutReadTests.PortoDust2);
        IReadOnlyList<RulesetDoc> rules = RoundFactsTestSupport.Load(Rules);

        AnalysisRun run = DemoAnalysis.Run(path, rules);

        await Assert.That(run.Build.ExcludedRulesets.Count).IsEqualTo(0);
        int players = 0, exact = 0, buys = 0, unknown = 0, awps = 0;
        foreach (PerPlayerNodeTemplate.MaterializedPlayer player in run.MaterializedPlayers)
        {
            int V(string key) => ((Abstractions.ValueNode<int>)player.NodesByRuleId!["econ." + key]).Value;
            if (V("buys") == 0)
            {
                continue;
            }

            players++;
            buys += V("buys");
            unknown += V("unknown");
            awps += V("awps");
            exact += V("bought") - V("refunded") == V("final") ? 1 : 0;
        }

        await Assert.That(players).IsEqualTo(10);
        // One player is 3,500 apart over the match; the per-round corpus check in the commit message
        // puts the synthesis exact on 99%+ of player-rounds.
        await Assert.That(exact).IsGreaterThanOrEqualTo(9);
        await Assert.That(unknown * 100).IsLessThan(buys * 3).Because($"{unknown} of {buys} with no item");
        await Assert.That(awps).IsGreaterThan(0);
    }

    [Test]
    [Category("Unit")]
    public async Task OnlyASubscription_BuildsTheSynthesis()
    {
        RulesetDoc kills = RoundFactsTestSupport.Load("""
            ruleset: k
            for: each_player
            stats:
              kills:
                count: kill
                per: match
            """)[0];
        AnalysisTarget target = new(64, new Cs2GotvProfile());

        BuildResult without = DemoAnalysis.Build(target, [kills]);
        BuildResult with = DemoAnalysis.Build(target, [kills, .. RoundFactsTestSupport.Load(Rules)]);

        await Assert.That(without.EntityScanner?.Purchases).IsNull();
        await Assert.That(without.EntityScanner?.ColumnLayout.Names ?? []).DoesNotContain(PurchaseSynthesizer.CashSpentProvider);
        await Assert.That(with.EntityScanner?.Purchases).IsNotNull();
        await Assert.That(with.EntityScanner!.ColumnLayout.Names).Contains(PurchaseSynthesizer.MoneyProvider);
    }
}
