#region

using CS2DemoKit.Analysis.Graphs;
using CS2DemoKit.Analysis.Plugins;
using CS2DemoKit.Analysis.Profiles;
using CS2DemoKit.Analysis.Yaml;
using CS2DemoKit.Parser;
using CS2OpenSchema.Protos;

#endregion

namespace CS2DemoKit.Analysis.Tests;

/// <summary>
///     What a build asks the reader to decode: the events its edges and the evaluator consume,
///     entities only when something reads them, and never the user commands.
/// </summary>
[Category("Unit")]
public class AnalysisDecodePlanTests
{
    private static AnalysisTarget Target => new(64, DemoSourceProfileRegistry.DefaultFallback);

    [Test]
    public async Task ShippedRulesets_PlanEntities_AndTheEventsTheyRead()
    {
        RuleConfigLoadResult rules = YamlConfigLoader.LoadShippedEmbedded();
        BuildResult build = DemoAnalysis.Build(Target, rules.Rulesets);
        await Assert.That(build.EntityScanner).IsNotNull();

        DecodePlan plan = DemoAnalysis.PlanDecode(build);
        await Assert.That(plan.DecodesEverything).IsFalse();
        await Assert.That(plan.Categories.HasFlag(MessageCategories.Header)).IsTrue();
        await Assert.That(plan.Categories.HasFlag(MessageCategories.StringTables)).IsTrue();
        await Assert.That(plan.Categories.HasFlag(MessageCategories.GameEvents)).IsTrue();
        await Assert.That(plan.Categories.HasFlag(MessageCategories.Schema)).IsTrue();
        await Assert.That(plan.Categories.HasFlag(MessageCategories.Entities)).IsTrue();
        await Assert.That(plan.RetainSignonPrefix).IsTrue();
        await Assert.That(plan.Decodes(NetMessageCatalog.UserCmdsTypeId)).IsFalse();

        await Assert.That(plan.GameEventNames).IsNotNull();
        foreach (string name in new[]
                 {
                     "player_death", "player_hurt", "weapon_fire", "round_freeze_end", "player_team",
                     "round_officially_ended", "cs_pre_restart"
                 })
        {
            await Assert.That(plan.GameEventNames!.Contains(name)).IsTrue().Because(name);
        }
    }

    [Test]
    public async Task NoEntityReads_PlansNoEntities_AndNoSchema()
    {
        AnalysisOptions options = new()
        {
            EntityProviders = new EntityValueProviderRegistry(),
            PerPlayerEntityProviders = new PerPlayerEntityValueProviderRegistry()
        };
        BuildResult build = DemoAnalysis.Build(Target, [], options);
        await Assert.That(build.EntityScanner).IsNull();

        DecodePlan plan = DemoAnalysis.PlanDecode(build);
        await Assert.That(plan.Categories.HasFlag(MessageCategories.Entities)).IsFalse();
        await Assert.That(plan.Categories.HasFlag(MessageCategories.Schema)).IsFalse();
        await Assert.That(plan.RetainSignonPrefix).IsFalse();
        await Assert.That(plan.Decodes((int)SVC_Messages.SvcPacketEntities)).IsFalse();
        await Assert.That(plan.Decodes((int)SVC_Messages.SvcServerInfo)).IsTrue();
        await Assert.That(plan.Decodes((int)SVC_Messages.SvcCreateStringTable)).IsTrue();
        await Assert.That(plan.Decodes(NetMessageCatalog.UserCmdsTypeId)).IsFalse();
        await Assert.That(plan.GameEventNames!.Contains("player_death")).IsTrue();
    }

    [Test]
    public async Task ABuildCarriesTheProfileAndRegistryItWasBuiltWith()
    {
        BuildResult build = DemoAnalysis.Build(Target, []);
        await Assert.That(build.Profile).IsSameReferenceAs(Target.Profile);
        await Assert.That(build.ProfileResolution).IsEqualTo(ProfileResolutionKind.Explicit);
        await Assert.That(build.Events).IsNotNull();
    }
}
