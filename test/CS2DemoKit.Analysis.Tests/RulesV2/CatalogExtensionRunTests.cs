#region

using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Analysis.Plugins;
using CS2DemoKit.Analysis.RulesetsV2.Model;
using CS2DemoKit.Analysis.Tests.RoundFacts;
using CS2DemoKit.TestSupport;

#endregion

namespace CS2DemoKit.Analysis.Tests.RulesV2;

/// <summary>
///     A run that registers its own providers and hands their rows over as a
///     <see cref="AnalysisOptions.CatalogExtension" /> reads them through every lowering: a per-player
///     path, a role path and a singleton path.
/// </summary>
[Category("Integration")]
[NotInParallel]
public class CatalogExtensionRunTests
{
    private const string Rules = """
        ruleset: ext_match
        for: match
        stats:
          kills:
            count: kill
            per: match
          helmet_kills:
            count: kill
            where: "victim.ext.has_helmet"
            per: match
          builtin_helmet_kills:
            count: kill
            where: "victim.has_helmet"
            per: match
          builtin_ct_timeouts:
            capture: match.ct_timeouts_left
            on: round_decided
            keep: list
            per: match
          ct_timeouts:
            capture: match.ext.ct_timeouts_left
            on: round_decided
            keep: list
            per: match
        """;

    internal static AnalysisOptions Options()
    {
        PerPlayerEntityValueProviderRegistry perPlayer = PerPlayerEntityValueProviderRegistry.CreateDefault();
        perPlayer.Register(CatalogExtensionTests.Helmet);
        EntityValueProviderRegistry singletons = EntityValueProviderRegistry.CreateDefault();
        singletons.Register(CatalogExtensionTests.Timeouts);
        return new AnalysisOptions
        {
            PerPlayerEntityProviders = perPlayer,
            EntityProviders = singletons,
            CatalogExtension = CatalogExtensionTests.Extension
        };
    }

    [Test]
    public async Task ExtensionReads_ReachTheRun()
    {
        string path = DemoTestHelper.RequireDemo(GameRulesProviderTests.Build10231Nuke);
        IReadOnlyList<RulesetDoc> rules = RoundFactsTestSupport.Load(Rules);

        AnalysisRun run = DemoAnalysis.Run(path, rules, Options());

        await Assert.That(run.Build.ExcludedRulesets.Count).IsEqualTo(0)
            .Because(string.Join("; ", run.Build.ExcludedRulesets.Select(e => e.ToString())));
        int kills = ((ValueNode<int>)run.Build.GameNodesByRuleId!["ext_match.kills"]).Value;
        int helmetKills = ((ValueNode<int>)run.Build.GameNodesByRuleId!["ext_match.helmet_kills"]).Value;
        int[] timeouts = RoundFactsCorpusTests.Ints(run, "ext_match.ct_timeouts");

        await Assert.That(helmetKills)
            .IsEqualTo(((ValueNode<int>)run.Build.GameNodesByRuleId!["ext_match.builtin_helmet_kills"]).Value)
            .Because("the extension row reads the same field as the built-in victim.has_helmet");
        await Assert.That(string.Join(",", timeouts))
            .IsEqualTo(string.Join(",", RoundFactsCorpusTests.Ints(run, "ext_match.builtin_ct_timeouts")));
        await Assert.That(helmetKills).IsGreaterThan(0);
        await Assert.That(helmetKills).IsLessThan(kills).Because("pistol rounds are mostly bare-headed");
        await Assert.That(timeouts.Length).IsGreaterThan(12);
        await Assert.That(timeouts.All(t => t is >= 0 and <= 4)).IsTrue().Because(string.Join(",", timeouts));
    }
}
