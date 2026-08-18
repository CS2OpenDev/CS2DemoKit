#region

using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Analysis.Building;
using CS2DemoKit.Analysis.Catalog;
using CS2DemoKit.Analysis.Config;
using CS2DemoKit.Analysis.Graphs;
using CS2DemoKit.Analysis.Nodes;
using CS2DemoKit.Analysis.Plugins;
using CS2DemoKit.Analysis.Registry;
using CS2DemoKit.Analysis.Rules;
using CS2DemoKit.Analysis.Rules.Hashing;
using CS2DemoKit.Analysis.RulesetsV2.Compile;
using CS2DemoKit.Analysis.RulesetsV2.Model;
using CS2DemoKit.Analysis.RulesetsV2.Resolve;
using CS2DemoKit.Analysis.Yaml;
using CS2DemoKit.Parser;
using CS2DemoKit.TestSupport;

#endregion

namespace CS2DemoKit.Analysis.Tests.RulesV2;

/// <summary>
///     Pre-freeze gap gates for the game-scoped (<c>for: match</c>) v2 planner path
///     (<see cref="RuleChainBuilder" />.<c>BuildV2GameScope</c>). A match ruleset has no subject: its
///     stats lower to single game-scoped graph nodes (not a per-player template), the view actor
///     binding is suppressed (so <c>count: kill</c> counts every kill), subject-relative B6 reads are
///     rejected, and <c>show: tables (per: match)</c> projects a single match-level row. The
///     demo-backed total asserts the match count equals the sum of the per-player twin.
/// </summary>
[Category("Unit")]
public class RulesetV2GameScopeTests
{
    private const string Gotv = "Cs2GotvProfile";
    private static readonly CatalogScopeAdapter _adapter = CatalogScopeAdapter.From(CatalogResource.Load());

    private static RulesetResolveResult Resolve(string yaml)
    {
        RulesetDoc doc = RulesetDocumentLoader.Load(yaml, "test.rules.yaml").Doc
                         ?? throw new InvalidOperationException("test ruleset failed to map");
        return CheckedRulesetDraft.Load(doc, _adapter).Build(64.0, Gotv);
    }

    private static CheckedRuleset Checked(string yaml) =>
        Resolve(yaml).Ruleset
        ?? throw new InvalidOperationException("resolve failed: "
                                               + string.Join("; ", Resolve(yaml).Diagnostics));

    private static BuildResult Build(params CheckedRuleset[] rulesets) =>
        new RuleChainBuilder(EventRegistry.Build()).Build(rulesets);

    // ── STEP 1 resolver contract (empirical): actor suppressed, player.* rejected ──

    /// <summary>
    ///     The resolver emits a valid match-scoped stat for <c>count: kill</c>: <c>For == Match</c>,
    ///     <see cref="ScopeAxis.Match" />, the kill view, and the view's baked trigger condition — but
    ///     no per-player actor binding (that is a planner concern, suppressed for a non-each_player
    ///     ruleset). Confirms the contract the game-scope planner builds against.
    /// </summary>
    [Test]
    public async Task MatchScope_Resolves_WithNoSubjectActorBinding()
    {
        const string Yaml = """
                            ruleset: match_totals
                            for: match
                            stats:
                              total_kills:
                                count: kill
                                per: match
                            """;

        CheckedRuleset rs = Checked(Yaml);
        await Assert.That(rs.For).IsEqualTo(RulesetScope.Match);

        CheckedStat s = rs.Stats.Single(x => x.StatId == "total_kills");
        await Assert.That(s.Kind).IsEqualTo(RuleNodeKind.Count);
        await Assert.That(s.Scope).IsEqualTo(ScopeAxis.Match);
        await Assert.That(s.ResolvedView).IsEqualTo("kill");
    }

    /// <summary>
    ///     A <c>player.*</c> read in a match stat is rejected at resolve — the <c>player</c> root is
    ///     only in scope for an each_player ruleset (there is no subject at match scope).
    /// </summary>
    [Test]
    public async Task MatchScope_PlayerRead_RejectedAtResolve()
    {
        const string Yaml = """
                            ruleset: bad_match
                            for: match
                            stats:
                              s:
                                count: kill
                                where: player.survived
                                per: match
                            """;

        RulesetResolveResult r = Resolve(Yaml);
        await Assert.That(r.Success).IsFalse();
        await Assert.That(r.Diagnostics.Any(d => d.Code == DiagnosticCodes.UnknownRoot)).IsTrue();
    }

    // ── STEP 2 planner: game-scoped nodes ──

    /// <summary>
    ///     A <c>for: match</c> ruleset BUILDS (no throw) and registers each stat as a game-scoped node
    ///     under its qualified <c>{ruleset}.{stat}</c> key — resolvable by the configured-output
    ///     projector against the build's game node map.
    /// </summary>
    [Test]
    public async Task MatchScope_Builds_RegistersGameScopedNodes()
    {
        const string Yaml = """
                            ruleset: match_totals
                            for: match
                            stats:
                              total_kills:
                                count: kill
                                per: match
                              total_rounds:
                                count: round_won
                                per: match
                            """;

        BuildResult build = Build(Checked(Yaml));
        await Assert.That(build.GameNodesByRuleId).IsNotNull();
        await Assert.That(build.GameNodesByRuleId!.ContainsKey("match_totals.total_kills")).IsTrue();
        await Assert.That(build.GameNodesByRuleId!.ContainsKey("match_totals.total_rounds")).IsTrue();
        await Assert.That(build.GameNodesByRuleId!["match_totals.total_kills"]).IsTypeOf<GenericValueNode<int>>();
    }

    /// <summary>
    ///     A subject-relative B6 read (<c>round.team.alive</c>) type-checks at match scope (it sits
    ///     under the always-present <c>round</c> root) but has no subject — the planner rejects it loud
    ///     rather than binding a phantom slot-0 aggregate.
    /// </summary>
    [Test]
    public async Task MatchScope_SubjectRelativeRead_RejectedAtBuild()
    {
        const string Yaml = """
                            ruleset: bad_agg
                            for: match
                            stats:
                              s:
                                count: kill
                                where: round.team.alive > 0
                                per: match
                            """;

        CheckedRuleset rs = Checked(Yaml);
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => Build(rs));
        await Assert.That(ex.Message.Contains("round.team.alive", StringComparison.Ordinal)).IsTrue();
        await Assert.That(ex.Message.Contains("subject-relative", StringComparison.Ordinal)).IsTrue();
    }

    // ── STEP 3 projection: scoreboard rejected, tables per:match → match-level output ──

    /// <summary>
    ///     <c>show: scoreboard</c> is per-player and has no match-level meaning — rejected loud on a
    ///     <c>for: match</c> ruleset (use <c>show: tables (per: match)</c> instead).
    /// </summary>
    [Test]
    public async Task MatchScope_ShowScoreboard_Rejected()
    {
        const string Yaml = """
                            ruleset: sb_match
                            for: match
                            stats:
                              total_kills:
                                count: kill
                                per: match
                            show:
                              scoreboard:
                                - stat: total_kills
                            """;

        CheckedRuleset rs = Checked(Yaml);
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => Build(rs));
        await Assert.That(ex.Message.Contains("scoreboard", StringComparison.Ordinal)).IsTrue();
    }

    /// <summary>
    ///     <c>show: tables (per: match)</c> lowers to a single match-level <see cref="OutputDef" />
    ///     (<see cref="OutputScope.PerMatch" />) whose metric refs are the qualified
    ///     <c>{ruleset}.{stat}</c> game-node keys and whose only dimensions are <c>match_id</c>/<c>map</c>
    ///     (no player dimension).
    /// </summary>
    [Test]
    public async Task MatchScope_ShowTablesPerMatch_LowersToMatchLevelOutput()
    {
        const string Yaml = """
                            ruleset: tbl_match
                            for: match
                            stats:
                              total_kills:
                                count: kill
                                per: match
                            show:
                              tables:
                                match_totals:
                                  per: match
                                  columns:
                                    - stat: total_kills
                                      label: kills
                            """;

        CheckedRuleset rs = Checked(Yaml);
        IReadOnlyList<OutputDef> outputs = ShowLowering.LowerTables(rs);
        OutputDef table = outputs.Single();

        await Assert.That(table.Scope).IsEqualTo(OutputScope.PerMatch);
        await Assert.That(table.Dimensions.Count).IsEqualTo(2);
        await Assert.That(table.Dimensions.Contains("match_id")).IsTrue();
        await Assert.That(table.Dimensions.Contains("map")).IsTrue();
        await Assert.That(table.Dimensions.Contains("player_slot")).IsFalse();
        await Assert.That(table.Metrics.Single().RuleRef).IsEqualTo("tbl_match.total_kills");
        await Assert.That(table.Metrics.Single().Label).IsEqualTo("kills");
    }

    // ── Regression: an each_player ruleset is unchanged (builds, per-player template present) ──

    /// <summary>
    ///     An each_player ruleset still lowers to a per-player template with its qualified stat nodes —
    ///     the game-scope branch is purely additive and does not perturb the corpus path.
    /// </summary>
    [Test]
    public async Task EachPlayer_Unchanged_BuildsPerPlayerTemplate()
    {
        const string Yaml = """
                            ruleset: pp
                            for: each_player
                            stats:
                              kills:
                                count: kill
                                per: match
                            """;

        BuildResult build = Build(Checked(Yaml));
        await Assert.That(build.Graph.PerPlayerTemplates.Count).IsGreaterThan(0);

        PerPlayerNodeTemplate.MaterializedPlayer mp = build.Graph.PerPlayerTemplates[^1].Materialize(0, 0, "test", null);
        await Assert.That(mp.NodesByRuleId!.ContainsKey("pp.kills")).IsTrue();
    }

    // ── Demo-backed: match total == sum of the per-player twin ──

    /// <summary>
    ///     End-to-end on the reference demo: a <c>for: match</c> <c>total_kills: count: kill</c> counts
    ///     every kill (actor binding suppressed), which must equal the sum over all players of the
    ///     <c>for: each_player</c> twin <c>kills: count: kill</c> (each counting its subject's kills).
    ///     Both are built into one graph and evaluated once. Skips gracefully without a demo.
    /// </summary>
    [Test]
    [Category("Integration")]
    [NotInParallel]
    public async Task MatchScope_TotalKills_EqualsSumOfPerPlayerKills()
    {
        ParsedDemo demo = DemoTestHelper.GetOrParse(DemoTestHelper.RequireDemo());

        const string MatchYaml = """
                                 ruleset: match_totals
                                 for: match
                                 stats:
                                   total_kills:
                                     count: kill
                                     per: match
                                 """;

        const string PerPlayerYaml = """
                                     ruleset: pp_totals
                                     for: each_player
                                     stats:
                                       kills:
                                         count: kill
                                         per: match
                                     """;

        BuildResult build = CompileTwo(demo, MatchYaml, PerPlayerYaml);
        AnalysisRun run = DemoAnalysis.Evaluate(demo, build);
        EvaluationResult result = run.Snapshots
                                  ?? throw new InvalidOperationException("evaluation produced no snapshots");

        int gameTotal = ((ValueNode<int>)build.GameNodesByRuleId!["match_totals.total_kills"]).Value;

        int perPlayerSum = 0;
        foreach (PerPlayerNodeTemplate.MaterializedPlayer mp in result.MaterializedPlayers)
        {
            ValueNode<int>? kills = mp.Nodes
                .OfType<ValueNode<int>>()
                .FirstOrDefault(n => string.Equals(n.Name, "kills", StringComparison.Ordinal));
            if (kills is not null)
            {
                perPlayerSum += kills.Value;
            }
        }

        Console.WriteLine($"[game-scope] total_kills (match) = {gameTotal}; sum(per-player kills) = {perPlayerSum}");

        await Assert.That(gameTotal).IsGreaterThan(0)
            .Because("the reference demo has kills, so the match total must be positive");
        await Assert.That(gameTotal).IsEqualTo(perPlayerSum)
            .Because("a for: match count: kill (all kills) equals the sum of the per-player twin (each subject's kills)");
    }

    /// <summary>Resolves + composes two v2 rulesets into one ready-to-evaluate build against the demo's profile.</summary>
    private static BuildResult CompileTwo(ParsedDemo demo, string yamlA, string yamlB)
    {
        RuleChainBuilder builder = new(
            EventRegistry.Build(),
            demo,
            entityProviders: EntityValueProviderRegistry.CreateDefault(),
            perPlayerEntityProviders: PerPlayerEntityValueProviderRegistry.CreateDefault());

        string profileId = builder.Profile.GetType().Name;
        CheckedRuleset a = ResolveFor(demo, yamlA, profileId);
        CheckedRuleset b = ResolveFor(demo, yamlB, profileId);
        return builder.Build([a, b]);
    }

    private static CheckedRuleset ResolveFor(ParsedDemo demo, string yaml, string profileId)
    {
        RulesetDoc doc = RulesetDocumentLoader.Load(yaml, "golden.rules.yaml").Doc
                         ?? throw new InvalidOperationException("ruleset failed to map");
        RulesetResolveResult r = CheckedRulesetDraft.Load(doc, _adapter).Build(demo.TickRate, profileId);
        return r.Ruleset ?? throw new InvalidOperationException(
            "resolve failed: " + string.Join("; ", r.Diagnostics));
    }
}
