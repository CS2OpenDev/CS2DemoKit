#region

using CS2DemoKit.Analysis.Graphs;
using CS2DemoKit.Analysis.Plugins;
using CS2DemoKit.Analysis.Profiles;
using CS2DemoKit.Analysis.RulesetsV2.Model;
using CS2DemoKit.Analysis.RulesetsV2.Resolve;

#endregion

namespace CS2DemoKit.Analysis.Tests.RoundFacts;

/// <summary>
///     <c>item_purchase</c> / <c>item_refund</c> and <c>timeout_started</c> / <c>timeout_ended</c> are read
///     off registered providers. A host registry without them must neither fail the build nor let a stat
///     report zero as if it were a measurement: the events stay off and each subscribing stat gets a
///     coverage diagnostic naming the missing providers.
/// </summary>
[Category("Unit")]
public class SynthesizedEventCoverageTests
{
    private const string Rules = """
        ruleset: synth
        for: each_player
        stats:
          kills:
            count: kill
            per: match
          buys:
            count: item_purchased
            per: match
          refunds:
            count: item_refunded
            per: match
          timeouts:
            count: timeout_started
            per: match
        """;

    private static readonly AnalysisTarget _target = new(64, new Cs2GotvProfile());

    [Test]
    public async Task APerPlayerRegistryWithoutTheEconomyColumns_BuildsAndReportsThePurchaseStats()
    {
        PerPlayerEntityValueProviderRegistry perPlayer = new();
        perPlayer.Register(new PawnHealthProvider());

        BuildResult build = DemoAnalysis.Build(_target, Load(),
            new AnalysisOptions { PerPlayerEntityProviders = perPlayer });

        await Assert.That(build.ExcludedRulesets.Count).IsEqualTo(0);
        await Assert.That(build.EntityScanner?.Purchases).IsNull();
        IReadOnlyList<RulesetCoverageDiagnostic> coverage = build.RulesetCoverage ?? [];
        await Assert.That(coverage.Select(c => c.NodeId)).Contains("buys");
        await Assert.That(coverage.Select(c => c.NodeId)).Contains("refunds");
        await Assert.That(coverage.Single(c => c.NodeId == "buys").Message)
            .Contains(PurchaseSynthesizer.CashSpentProvider);
        await Assert.That(coverage.Select(c => c.NodeId)).DoesNotContain("kills");
    }

    [Test]
    public async Task AnEntityRegistryWithoutTheTimeoutProviders_ReportsTheTimeoutStats()
    {
        BuildResult build = DemoAnalysis.Build(_target, Load(),
            new AnalysisOptions { EntityProviders = new EntityValueProviderRegistry() });

        IReadOnlyList<RulesetCoverageDiagnostic> coverage = build.RulesetCoverage ?? [];
        await Assert.That(coverage.Select(c => c.NodeId)).Contains("timeouts");
        await Assert.That(coverage.Single(c => c.NodeId == "timeouts").Message)
            .Contains(EntityChangeScanner.TimeoutFlagContexts[0]);
    }

    [Test]
    public async Task TheDefaultRegistries_ReportNothing()
    {
        BuildResult build = DemoAnalysis.Build(_target, Load());

        await Assert.That(build.EntityScanner?.Purchases).IsNotNull();
        await Assert.That((build.RulesetCoverage ?? []).Select(c => c.NodeId))
            .DoesNotContain("buys").And.DoesNotContain("timeouts");
    }

    private static IReadOnlyList<RulesetDoc> Load() => RoundFactsTestSupport.Load(Rules);
}
