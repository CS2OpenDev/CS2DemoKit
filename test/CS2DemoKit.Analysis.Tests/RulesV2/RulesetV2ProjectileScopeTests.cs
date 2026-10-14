#region

using CS2DemoKit.Analysis.Config;
using CS2DemoKit.Analysis.Rules;
using CS2DemoKit.Analysis.Rules.Checking;
using CS2DemoKit.Analysis.Rules.Hashing;
using CS2DemoKit.Analysis.RulesetsV2.Compile;
using CS2DemoKit.Analysis.RulesetsV2.Model;
using CS2DemoKit.Analysis.RulesetsV2.Resolve;
using CS2DemoKit.Analysis.Tests.RoundFacts;
using CS2DemoKit.Analysis.Yaml;

#endregion

namespace CS2DemoKit.Analysis.Tests.RulesV2;

/// <summary>
///     <c>for: each_projectile</c> at the mapper, resolver, show-validator and show-lowering seams,
///     demo-free: the scope maps, <c>projectile.*</c> and the <c>thrower</c> role are in scope, every
///     stat lands on the projectile axis, a capture with no <c>on:</c> takes a bare projectile read
///     kept first or last, and a table is <c>per: projectile</c>.
/// </summary>
[Category("Unit")]
public class RulesetV2ProjectileScopeTests
{
    private const string GrenadeRows = """
        ruleset: grenade_rows
        for: each_projectile
        stats:
          class:        { capture: projectile.class,   keep: first }
          thrower:      { capture: projectile.thrower, keep: first }
          spawn_x:      { capture: projectile.pos_x,   keep: first }
          end_x:        { capture: projectile.pos_x,   keep: last }
          bounces:      { capture: projectile.bounces, keep: last }
          thrower_walk: { capture: thrower.is_walking,    on: raw.projectile_created }
          landed_x:     { capture: projectile.pos_x,   on: projectile_landed }
          lands:        { count: projectile_landed }
        show:
          tables:
            grenades:
              per: projectile
              columns:
                - { stat: class, label: class }
                - { stat: thrower, label: thrower }
                - { stat: spawn_x, label: spawn_x }
                - { stat: end_x, label: end_x }
                - { stat: bounces, label: bounces }
                - { stat: thrower_walk, label: thrower_walk }
                - { stat: landed_x, label: landed_x }
                - { stat: lands, label: lands }
        """;

    [Test]
    public async Task Mapper_MapsEachProjectile()
    {
        RulesetDoc doc = RulesetDocumentLoader.Load(GrenadeRows, null).Doc
                         ?? throw new InvalidOperationException("the ruleset failed to map");
        await Assert.That(doc.For).IsEqualTo(RulesetScope.EachProjectile);
    }

    [Test]
    public async Task Resolver_PutsEveryStatOnTheProjectileAxis_AndTypesTheReads()
    {
        CheckedRuleset ruleset = RoundFactsTestSupport.Checked(GrenadeRows);

        await Assert.That(ruleset.For).IsEqualTo(RulesetScope.EachProjectile);
        await Assert.That(ruleset.Stats.All(s => s.Scope == ScopeAxis.Projectile)).IsTrue();

        CheckedStat cls = ruleset.Stats.Single(s => s.StatId == "class");
        await Assert.That(cls.ValueType.Kind).IsEqualTo(RulesTypeKind.String);
        await Assert.That(cls.Keep).IsEqualTo(KeepKind.First);
        await Assert.That(cls.ConcreteEvents.Count).IsEqualTo(0);

        CheckedStat endX = ruleset.Stats.Single(s => s.StatId == "end_x");
        await Assert.That(endX.ValueType.Kind).IsEqualTo(RulesTypeKind.Float);
        await Assert.That(endX.Keep).IsEqualTo(KeepKind.Last);

        // thrower.* is the thrower's per-player provider, read at the subject's slot.
        CheckedStat walk = ruleset.Stats.Single(s => s.StatId == "thrower_walk");
        await Assert.That(walk.EntityReads.Count).IsEqualTo(1);
        await Assert.That(walk.EntityReads[0].Path).IsEqualTo("thrower.is_walking");
        await Assert.That(walk.EntityReads[0].Subject).IsEqualTo(EntityProviderReference.PlayerSubject);
        await Assert.That(walk.EntityReads[0].ProviderName).IsEqualTo("entity.pawn.is_walking");

        // A projectile.* read is a loader-injected symbol, never an entity provider.
        await Assert.That(endX.EntityReads.Count).IsEqualTo(0);
        await Assert.That(endX.DeclaredReads).Contains("projectile.pos_x");
    }

    [Test]
    public async Task Resolver_RefusesACaptureWithNoTrigger_UnlessItIsABareProjectileReadKeptFirstOrLast()
    {
        RulesetResolveResult list = RoundFactsTestSupport.Resolve("""
            ruleset: bad
            for: each_projectile
            stats:
              xs: { capture: projectile.pos_x, keep: list }
            """);
        await Assert.That(list.Success).IsFalse();
        await Assert.That(list.Diagnostics.Any(d => d.Code == ResolveDiagnosticCodes.TriggerUnsupportedForKind)).IsTrue();

        RulesetResolveResult expression = RoundFactsTestSupport.Resolve("""
            ruleset: bad
            for: each_projectile
            stats:
              doubled: { capture: "projectile.pos_x * 2", keep: last }
            """);
        await Assert.That(expression.Success).IsFalse();
        await Assert.That(expression.Diagnostics.Any(d => d.Code == ResolveDiagnosticCodes.TriggerUnsupportedForKind)).IsTrue();

        RulesetResolveResult thrower = RoundFactsTestSupport.Resolve("""
            ruleset: bad
            for: each_projectile
            stats:
              walk: { capture: thrower.is_walking, keep: last }
            """);
        await Assert.That(thrower.Success).IsFalse();
        await Assert.That(thrower.Diagnostics.Any(d => d.Code == ResolveDiagnosticCodes.TriggerUnsupportedForKind)).IsTrue();
    }

    [Test]
    public async Task Resolver_KeepsProjectileAndThrowerOutOfOtherScopes()
    {
        RulesetResolveResult player = RoundFactsTestSupport.Resolve("""
            ruleset: bad
            for: each_player
            stats:
              x: { capture: projectile.pos_x, on: projectile_landed, per: match }
            """);
        await Assert.That(player.Success).IsFalse();
        await Assert.That(player.Diagnostics.Any(d => d.Code == DiagnosticCodes.UnknownRoot)).IsTrue();

        RulesetResolveResult match = RoundFactsTestSupport.Resolve("""
            ruleset: bad
            for: match
            stats:
              w: { capture: thrower.is_walking, on: raw.projectile_created, per: match }
            """);
        await Assert.That(match.Success).IsFalse();
        await Assert.That(match.Diagnostics.Any(d => d.Code == DiagnosticCodes.UnknownRoot)).IsTrue();
    }

    [Test]
    public async Task Resolver_RefusesPlayerReads_Highlights_AndTheKindsThatNeedLogicNodes()
    {
        RulesetResolveResult player = RoundFactsTestSupport.Resolve("""
            ruleset: bad
            for: each_projectile
            stats:
              hp: { capture: player.health, on: raw.projectile_created }
            """);
        await Assert.That(player.Success).IsFalse();
        await Assert.That(player.Diagnostics.Any(d => d.Code == DiagnosticCodes.UnknownRoot)).IsTrue();

        RulesetResolveResult highlight = RoundFactsTestSupport.Resolve("""
            ruleset: bad
            for: each_projectile
            stats:
              lands: { count: projectile_landed }
            highlights:
              landed: { when: "lands >= 1", per: match, title: "landed" }
            """);
        await Assert.That(highlight.Success).IsFalse();
        await Assert.That(highlight.Diagnostics.Any(d => d.Code == ResolveDiagnosticCodes.ProjectileScopeUnsupported)).IsTrue();

        RulesetResolveResult tally = RoundFactsTestSupport.Resolve("""
            ruleset: bad
            for: each_projectile
            stats:
              lands:
                tally: projectile_landed
                thresholds: [ { min: 1, target: one } ]
            """);
        await Assert.That(tally.Success).IsFalse();
        await Assert.That(tally.Diagnostics.Any(d => d.Code == ResolveDiagnosticCodes.ProjectileScopeUnsupported)).IsTrue();
    }

    [Test]
    public async Task ShowValidator_TakesPerProjectile_OnlyInTheProjectileScope()
    {
        CheckedRuleset ok = RoundFactsTestSupport.Checked(GrenadeRows);
        await Assert.That(ShowReferenceValidator.Validate(ok)).IsEmpty();

        CheckedRuleset wrongGrain = RoundFactsTestSupport.Checked("""
            ruleset: bad
            for: each_projectile
            stats:
              lands: { count: projectile_landed }
            show:
              tables:
                t: { per: player_match, columns: [ { stat: lands } ] }
            """);
        await Assert.That(ShowReferenceValidator.Validate(wrongGrain)
            .Any(d => d.Code == ResolveDiagnosticCodes.ShowTableScopeMismatch)).IsTrue();

        CheckedRuleset wrongScope = RoundFactsTestSupport.Checked("""
            ruleset: bad
            for: each_player
            stats:
              lands: { count: projectile_landed, per: match }
            show:
              tables:
                t: { per: projectile, columns: [ { stat: lands } ] }
            """);
        await Assert.That(ShowReferenceValidator.Validate(wrongScope)
            .Any(d => d.Code == ResolveDiagnosticCodes.ShowTableScopeMismatch)).IsTrue();
    }

    [Test]
    public async Task ShowLowering_MapsPerProjectile_ToTheProjectileScopeAndDimensions()
    {
        CheckedRuleset ruleset = RoundFactsTestSupport.Checked(GrenadeRows);
        OutputDef output = ShowLowering.LowerTables(ruleset).Single();

        await Assert.That(output.Scope).IsEqualTo(OutputScope.PerProjectile);
        await Assert.That(output.Dimensions).IsEquivalentTo(ShowLowering.PerProjectileDimensions);
        await Assert.That(output.Dimensions).Contains("entity_index");
        await Assert.That(output.Dimensions).Contains("serial");
        await Assert.That(output.Dimensions).Contains("steam_id");
        await Assert.That(output.Dimensions).Contains("created_tick");
        await Assert.That(output.Dimensions).Contains("removed_tick");
        await Assert.That(output.Metrics.Select(m => m.RuleRef)).Contains("grenade_rows.bounces");
    }

    [Test]
    public async Task CrossRuleset_NothingReadsAProjectileRuleset_AndItReadsOnlyMatchRulesets()
    {
        RulesetValidationResult reader = DemoAnalysis.ValidateRulesets(RoundFactsTestSupport.Load("""
            ruleset: grenades
            for: each_projectile
            exports: [ lands ]
            stats:
              lands: { count: projectile_landed }
            """, """
            ruleset: players
            for: each_player
            use: [ grenades ]
            stats:
              total: { compute: "grenades.lands", per: match }
            """));
        await Assert.That(reader.Diagnostics.Any(d => d.Code == ResolveDiagnosticCodes.CrossRefReadScope)).IsTrue();

        RulesetValidationResult perPlayerRead = DemoAnalysis.ValidateRulesets(RoundFactsTestSupport.Load("""
            ruleset: players
            for: each_player
            exports: [ kills ]
            stats:
              kills: { count: kill, per: match }
            """, """
            ruleset: grenades
            for: each_projectile
            use: [ players ]
            stats:
              k: { compute: "players.kills" }
            """));
        await Assert.That(perPlayerRead.Diagnostics.Any(d => d.Code == ResolveDiagnosticCodes.CrossRefReadScope)).IsTrue();
    }
}
