#region

using CS2DemoKit.Parser.GameEvents;
using CS2DemoKit.Analysis.Building;
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
        await Assert.That(build.RelevantMessageTypes).DoesNotContain(typeof(ItemPickupEvent))
            .Because("no pickups are dispatched for a synthesis that will not run");
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
    public async Task AnEntityRegistryWithTheFlagsButNotTheCounts_StillProducesTheTimeoutEvents()
    {
        EntityValueProviderRegistry flagsOnly = new();
        foreach (IEntityValueProvider provider in EntityValueProviderRegistry.CreateDefault().All
                     .Where(p => !EntityChangeScanner.TimeoutsLeftContexts.Contains(p.ContextName)))
        {
            flagsOnly.Register(provider);
        }

        BuildResult build = DemoAnalysis.Build(_target, Load(),
            new AnalysisOptions { EntityProviders = flagsOnly });

        await Assert.That((build.RulesetCoverage ?? []).Select(c => c.NodeId)).DoesNotContain("timeouts")
            .Because("the counts only fill timeouts_left, which reads -1 without them");
        await Assert.That(build.EntityScanner?.EmitsTimeouts).IsEqualTo(true)
            .Because("the three flags are all the events need");
    }

    [Test]
    public async Task TheTimeoutCounts_EmitTheirMarkerOnEveryChange()
    {
        IEnumerable<IEntityValueProvider> counts = BuiltinProviderSpecs.CreateGameRulesProviders()
            .Where(p => EntityChangeScanner.TimeoutsLeftContexts.Contains(p.ContextName));

        await Assert.That(counts.Select(p => p.EmitOn)).IsEquivalentTo([ChangeDirection.Both, ChangeDirection.Both])
            .Because("a count that only goes down never rises, so RisingOnly would never fire on a timeout");
    }

    [Test]
    public async Task TheDefaultRegistries_ReportNothing()
    {
        BuildResult build = DemoAnalysis.Build(_target, Load());

        await Assert.That(build.EntityScanner?.Purchases).IsNotNull();
        await Assert.That((build.RulesetCoverage ?? []).Select(c => c.NodeId))
            .DoesNotContain("buys").And.DoesNotContain("timeouts");
    }

    // A view can bind several concrete events on a profile, so the message is pinned on event lists
    // directly: no shipped view mixes a synthesized event with a wire one today.
    private static readonly Dictionary<string, string> _unproduced = new(StringComparer.Ordinal)
    {
        ["item_purchase"] = "no economy columns",
        ["timeout_started"] = "no timeout flags",
        ["enemy_spotted"] = "no geometry"
    };

    [Test]
    public async Task AStatOnUnproducedEventsFromTwoCauses_IsReportedAsNeverFiring()
    {
        (string Message, string FirstMissing)? found = RuleChainBuilder.UnproducedEventMessage("mix",
            ["item_purchase", "timeout_started"], _unproduced);

        await Assert.That(found).IsNotNull();
        await Assert.That(found!.Value.Message).Contains("never fires")
            .And.Contains("no economy columns").And.Contains("no timeout flags");
        await Assert.That(found.Value.FirstMissing).IsEqualTo("item_purchase");
    }

    [Test]
    public async Task AStatThatAlsoTriggersOnAProducedEvent_IsReportedAsPartial()
    {
        (string Message, string FirstMissing)? found = RuleChainBuilder.UnproducedEventMessage("half",
            ["enemy_spotted", "player_death"], _unproduced);

        await Assert.That(found).IsNotNull();
        await Assert.That(found!.Value.Message).Contains("fires only on its other events")
            .And.Contains("'enemy_spotted'").And.DoesNotContain("never fires");
    }

    [Test]
    public async Task AnEventReportedElsewhere_StillCountsTowardNeverFiring()
    {
        (string Message, string FirstMissing)? found = RuleChainBuilder.UnproducedEventMessage("facet",
            ["enemy_spotted", "timeout_started"], _unproduced, spotReportedElsewhere: true);

        await Assert.That(found).IsNotNull();
        await Assert.That(found!.Value.Message).Contains("never fires").And.DoesNotContain("no geometry")
            .And.Contains("'enemy_spotted' trigger, reported with its facet")
            .Because("both triggers are unproduced; the facet row already gives enemy_spotted's cause");
        await Assert.That(found.Value.FirstMissing).IsEqualTo("timeout_started");
        await Assert.That(RuleChainBuilder.UnproducedEventMessage("only", ["enemy_spotted"], _unproduced,
            spotReportedElsewhere: true)).IsNull();
    }

    [Test]
    public async Task AStatOnProducedEventsOnly_IsNotReported()
    {
        await Assert.That(RuleChainBuilder.UnproducedEventMessage("kills", ["player_death"], _unproduced)).IsNull();
    }

    private static IReadOnlyList<RulesetDoc> Load() => RoundFactsTestSupport.Load(Rules);
}
