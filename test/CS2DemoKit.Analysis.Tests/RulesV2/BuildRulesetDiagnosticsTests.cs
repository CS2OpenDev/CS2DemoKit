#region

using CS2DemoKit.Analysis.Building;
using CS2DemoKit.Analysis.Catalog;
using CS2DemoKit.Analysis.Graphs;
using CS2DemoKit.Analysis.Output;
using CS2DemoKit.Analysis.Plugins;
using CS2DemoKit.Analysis.Registry;
using CS2DemoKit.Analysis.Rules;
using CS2DemoKit.Analysis.RulesetsV2.Model;
using CS2DemoKit.Analysis.RulesetsV2.Resolve;
using CS2DemoKit.Parser;
using CS2DemoKit.TestSupport;

#endregion

namespace CS2DemoKit.Analysis.Tests.RulesV2;

/// <summary>
///     "<c>Build</c> silently discards composition diagnostics" — facade half: a set holding
///     one broken ruleset and one healthy one must still build the healthy one, AND must hand the
///     caller enough to see that the broken one was dropped — <see cref="BuildResult.RulesetDiagnostics" />
///     and <see cref="BuildResult.ExcludedRulesets" />. Before this, <c>DemoAnalysis.Build</c> took
///     <c>RulesetComposition.Compose(...).Rulesets</c> and discarded <c>.Diagnostics</c>, so a
///     leaderboard whose rule stopped compiling saw a feat that scored zero rather than a feat that
///     never ran.
///     <para>
///         Parses the reference demo, so <see cref="NotInParallelAttribute" /> and the shared parse
///         cache apply (ONE heavy demo parse machine-wide). The two builds here are build-only —
///         nothing is evaluated, so the game-scoped shared-mutable-state hazard does not arise.
///     </para>
/// </summary>
[Category("Integration")]
[NotInParallel]
public class BuildRulesetDiagnosticsTests
{
    /// <summary>
    ///     A game-scoped healthy ruleset — <c>for: match</c> so its node lands in
    ///     <see cref="BuildResult.GameNodesByRuleId" />, where the test can see it directly (a
    ///     <c>for: each_player</c> ruleset materializes per player only at evaluation time).
    /// </summary>
    private static RulesetDoc HealthyMatchScoped() =>
        RulesetCompositionDiagnosticsTests.Doc("healthy_match.rules.yaml", """
                                                                          ruleset: healthy_match
                                                                          for: match
                                                                          stats:
                                                                            total_kills:
                                                                              count: kill
                                                                              per: match
                                                                          """);

    [Test]
    public async Task Build_WithOneBrokenRuleset_BuildsTheRestAndReportsWhatItDropped()
    {
        ParsedDemo demo = DemoTestHelper.GetOrParse(DemoTestHelper.RequireDemo());

        // bad_ref declares no use:, so its exclusion cannot cascade into the healthy ruleset.
        List<RulesetDoc> mixed =
        [
            RulesetCompositionDiagnosticsTests.BadIdentifier(),
            HealthyMatchScoped()
        ];
        BuildResult build = DemoAnalysis.Build(demo, mixed);

        // ── The healthy ruleset genuinely built ──
        await Assert.That(build.GameNodesByRuleId).IsNotNull()
            .Because("the healthy for: match ruleset contributes a game-scoped node");
        await Assert.That(build.GameNodesByRuleId!.Keys.Any(k => k.Contains("total_kills", StringComparison.Ordinal)))
            .IsTrue().Because("a broken sibling must not stop the healthy ruleset from compiling");

        // ── ...and the drop is visible, not silent ──
        await Assert.That(build.RulesetDiagnostics.Count).IsGreaterThan(0)
            .Because("the composition diagnostics must reach the caller, not be discarded in Build");
        await Assert.That(build.RulesetDiagnostics.Any(d =>
                d.RulesetId == "bad_ref"
                && d.Code == DiagnosticCodes.UnknownRoot
                && d.Message.Contains("nonexistent_stat", StringComparison.Ordinal)))
            .IsTrue().Because("each diagnostic names its ruleset, a stable code, and what was written");
        await Assert.That(build.RulesetDiagnostics.All(d => d.Position.Line > 0)).IsTrue()
            .Because("resolve-tier diagnostics carry a document position for the author-facing report");

        await Assert.That(build.ExcludedRulesets.Select(e => e.Id).ToList())
            .IsEquivalentTo(new List<string>
            {
                "bad_ref"
            })
            .Because("exactly the broken ruleset is excluded from the graph");
    }

    /// <summary>
    ///     A stat whose trigger the planner cannot lower for its kind. The resolver refuses the same
    ///     shape written as YAML, so the ruleset is built by hand: <c>Build</c> has to defend itself
    ///     against any <see cref="CheckedRuleset" /> a caller constructs.
    /// </summary>
    [Test]
    public async Task LoweringFailure_EachPlayer_IsExcludedAndSiblingsBuild()
    {
        ParsedDemo demo = DemoTestHelper.GetOrParse(DemoTestHelper.RequireDemo());
        RuleChainBuilder builder = new(
            EventRegistry.Build(),
            AnalysisTarget.From(demo),
            entityProviders: EntityValueProviderRegistry.CreateDefault(),
            perPlayerEntityProviders: PerPlayerEntityValueProviderRegistry.CreateDefault());

        CheckedRuleset streak = Resolve(demo, builder, """
                                                       ruleset: bad_streak
                                                       for: each_player
                                                       stats:
                                                         sprees:
                                                           streak: kill
                                                           window: 640
                                                           min_streak: 2
                                                           per: match
                                                       """);
        CheckedStat spree = streak.Stats.Single();
        CheckedRuleset broken = streak with { Stats = [spree with { ConcreteEvents = ["CDemoFileHeader"] }] };
        CheckedRuleset healthy = Resolve(demo, builder, """
                                                        ruleset: healthy_player
                                                        for: each_player
                                                        stats:
                                                          kills:
                                                            count: kill
                                                            per: round
                                                        show:
                                                          tables:
                                                            kills_by_round:
                                                              per: player_round
                                                              columns:
                                                                - stat: kills
                                                        """);

        BuildResult build = builder.Build([broken, healthy]);

        ExcludedRuleset excluded = build.ExcludedRulesets.Single();
        await Assert.That(excluded.Id).IsEqualTo("bad_streak");
        RulesetCompositionDiagnostic diagnostic = excluded.Diagnostics.Single();
        await Assert.That(diagnostic.Code).IsEqualTo(BuildDiagnosticCodes.LoweringFailed);
        await Assert.That(diagnostic.RulesetId).IsEqualTo("bad_streak");
        await Assert.That(diagnostic.Position).IsEqualTo(spree.Position);

        AnalysisRun run = DemoAnalysis.Evaluate(demo, build);
        MetricTable table = run.ProjectConfiguredOutputs(demo).Single(t => t.Name == "kills_by_round");
        await Assert.That(table.Rows.Count).IsGreaterThan(0)
            .Because("the healthy sibling still builds and evaluates");
    }

    /// <summary>
    ///     A subject-relative read on a <c>for: match</c> ruleset validates (it sits under the
    ///     always-present <c>round</c> root) but only the game-scope lowering can see it has no
    ///     subject. Composed through the facade beside a healthy ruleset.
    /// </summary>
    [Test]
    public async Task LoweringFailure_Match_IsExcluded()
    {
        ParsedDemo demo = DemoTestHelper.GetOrParse(DemoTestHelper.RequireDemo());
        RulesetDoc subjectRelative = RulesetCompositionDiagnosticsTests.Doc("bad_agg.rules.yaml", """
            ruleset: bad_agg
            for: match
            stats:
              s:
                count: kill
                where: round.team.alive > 0
                per: match
            """);

        RulesetValidationResult validated = DemoAnalysis.ValidateRulesets([subjectRelative]);
        await Assert.That(validated.Success).IsTrue()
            .Because("this shape must get past validation, or it proves nothing about Build: "
                     + string.Join("; ", validated.Diagnostics));

        BuildResult build = DemoAnalysis.Build(demo, [subjectRelative, HealthyMatchScoped()]);

        ExcludedRuleset excluded = build.ExcludedRulesets.Single();
        await Assert.That(excluded.Id).IsEqualTo("bad_agg");
        await Assert.That(excluded.SourceFile).IsEqualTo("bad_agg.rules.yaml");
        RulesetCompositionDiagnostic diagnostic = excluded.Diagnostics.Single();
        await Assert.That(diagnostic.Code).IsEqualTo(BuildDiagnosticCodes.LoweringFailed);
        await Assert.That(diagnostic.Message).Contains("round.team.alive");
        await Assert.That(diagnostic.Position.Line).IsGreaterThan(0);
        await Assert.That(build.GameNodesByRuleId!.Keys.Any(k => k.StartsWith("bad_agg.", StringComparison.Ordinal)))
            .IsFalse();
        await Assert.That(build.GameNodesByRuleId!.ContainsKey("healthy_match.total_kills")).IsTrue();

        AnalysisRun run = DemoAnalysis.Evaluate(demo, build);
        await Assert.That(run).IsNotNull();
    }

    private static CheckedRuleset Resolve(ParsedDemo demo, RuleChainBuilder builder, string yaml)
    {
        RulesetDoc doc = RulesetCompositionDiagnosticsTests.Doc("probe.rules.yaml", yaml);
        CatalogScopeAdapter adapter = CatalogScopeAdapter.From(CatalogResource.Load());
        RulesetResolveResult resolved = CheckedRulesetDraft.Load(doc, adapter)
            .Build(demo.TickRate, builder.Profile.GetType().Name);
        return resolved.Ruleset
               ?? throw new InvalidOperationException(
                   "test ruleset failed to resolve: " + string.Join("; ", resolved.Diagnostics));
    }

    [Test]
    public async Task Build_WithOnlyHealthyRulesets_ReportsNoDiagnostics()
    {
        ParsedDemo demo = DemoTestHelper.GetOrParse(DemoTestHelper.RequireDemo());

        BuildResult build = DemoAnalysis.Build(demo, [HealthyMatchScoped()]);

        await Assert.That(build.RulesetDiagnostics.Count).IsEqualTo(0)
            .Because("a clean set reports nothing: " + string.Join("; ", build.RulesetDiagnostics));
        await Assert.That(build.ExcludedRulesets.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Build_WithNoV2Documents_ReportsNoDiagnostics()
    {
        ParsedDemo demo = DemoTestHelper.GetOrParse(DemoTestHelper.RequireDemo());

        BuildResult build = DemoAnalysis.Build(demo, []);

        await Assert.That(build.RulesetDiagnostics.Count).IsEqualTo(0)
            .Because("the bare context/enrichment graph composes nothing, so it can drop nothing");
        await Assert.That(build.ExcludedRulesets.Count).IsEqualTo(0);
    }
}
