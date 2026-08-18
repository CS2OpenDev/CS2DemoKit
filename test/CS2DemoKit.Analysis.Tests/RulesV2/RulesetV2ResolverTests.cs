#region

using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Analysis.Catalog;
using CS2DemoKit.Analysis.Rules;
using CS2DemoKit.Analysis.Rules.Checking;
using CS2DemoKit.Analysis.Rules.Hashing;
using CS2DemoKit.Analysis.RulesetsV2.Model;
using CS2DemoKit.Analysis.RulesetsV2.Resolve;
using CS2DemoKit.Analysis.Yaml;

#endregion

namespace CS2DemoKit.Analysis.Tests.RulesV2;

/// <summary>
///     Resolver battery: the post-plant-double centerpiece resolves
///     to the expected <see cref="CheckedRuleset" /> IR — concrete-event sets, the composed
///     canonical ASTs (trigger condition, value selector, while-gate kept separate), declared reads,
///     and the compound <c>(For × Per)</c> scope axes; params bind to literals at build (two values →
///     two ASTs) but stay symbolic at demo-less load; list defines inline; a coverage-skipped view
///     yields a diagnostic (never a silent zero); and a cross-stat cycle is a build error. Demo-free.
/// </summary>
[Category("Unit")]
public class RulesetV2ResolverTests
{
    private const string Gotv = "Cs2GotvProfile";

    private const string Pilot = """
                                 ruleset: post_plant_double
                                 title: Post-Plant Double
                                 for: each_player

                                 params:
                                   min_kills: { type: int, default: 2, min: 2, max: 5 }

                                 define:
                                   post_plant_kill:
                                     on: kill
                                     match: { enemy: true }
                                     while: round.bomb.was_planted

                                 stats:
                                   plant_tick:
                                     capture: event.tick
                                     on: bomb_planted
                                     per: round

                                   post_plant_kills:
                                     count: post_plant_kill
                                     per: round

                                   kill_ticks:
                                     capture: event.tick
                                     on: post_plant_kill
                                     keep: list
                                     per: round

                                 highlights:
                                   post_plant_double:
                                     when: post_plant_kills >= params.min_kills
                                     per: round
                                     title: "{player.name} kills after plant"
                                 """;

    private static readonly CatalogScopeAdapter _adapter = CatalogScopeAdapter.From(CatalogResource.Load());

    private static RulesetDoc Doc(string yaml)
    {
        RulesetDocumentLoader.Outcome outcome = RulesetDocumentLoader.Load(yaml, "test.rules.yaml");
        return outcome.Doc
               ?? throw new InvalidOperationException(
                   $"pilot YAML failed to map: {string.Join("; ", outcome.Diagnostics)}");
    }

    private static CheckedStat Stat(CheckedRuleset ruleset, string id) =>
        ruleset.Stats.Single(s => s.StatId == id);

    private static CheckedHighlight Highlight(CheckedRuleset ruleset, string id) =>
        ruleset.Highlights.Single(h => h.HighlightId == id);

    // ── The pilot ──────────────────────────────────────────────────────────────

    [Test]
    public async Task Pilot_ResolvesToExpectedCheckedRuleset()
    {
        RulesetResolveResult result = CheckedRulesetDraft.Load(Doc(Pilot), _adapter).Build(64.0, Gotv);

        await Assert.That(result.Success).IsTrue();
        CheckedRuleset rs = result.Ruleset!;
        await Assert.That(rs.Id.JoinKey).IsEqualTo("_chain_post_plant_double");
        await Assert.That(rs.For).IsEqualTo(RulesetScope.EachPlayer);
        await Assert.That(rs.Coverage.Count).IsEqualTo(0);
        await Assert.That(rs.Stats.Count).IsEqualTo(3);
        await Assert.That(rs.Highlights.Count).IsEqualTo(1);

        // plant_tick: capture event.tick on bomb_planted (binding: none — no trigger condition).
        CheckedStat plant = Stat(rs, "plant_tick");
        await Assert.That(plant.Kind).IsEqualTo(RuleNodeKind.Capture);
        await Assert.That(plant.ValueType).IsEqualTo(RulesType.Instant);
        await Assert.That(plant.Scope).IsEqualTo(ScopeAxis.PlayerRound);
        await Assert.That(plant.Keep).IsEqualTo(KeepKind.Last);
        await Assert.That(string.Join(",", plant.ConcreteEvents)).IsEqualTo("bomb_planted");
        await Assert.That(plant.TriggerCondition).IsNull();
        await Assert.That(plant.ValueSelector!.Root.CanonicalText).IsEqualTo("(ref event.tick)");
        await Assert.That(plant.WhileGate).IsNull();

        // post_plant_kills: count of the spliced define — §4.2 order is match FIRST, baked SECOND.
        CheckedStat kills = Stat(rs, "post_plant_kills");
        await Assert.That(kills.Kind).IsEqualTo(RuleNodeKind.Count);
        await Assert.That(kills.ValueType).IsEqualTo(RulesType.Int);
        await Assert.That(kills.ResolvedView).IsEqualTo("kill");
        await Assert.That(string.Join(",", kills.ConcreteEvents)).IsEqualTo("player_death");
        await Assert.That(kills.TriggerCondition!.Root.CanonicalText).IsEqualTo(
            "(and (eq (ref enrich.kill.was_enemy_kill) (bool true)) (ne (ref event.Attacker) (ref event.UserId)))");
        await Assert.That(kills.ValueSelector).IsNull();
        await Assert.That(kills.WhileGate!.Root.CanonicalText).IsEqualTo("(ref round.bomb.was_planted)");
        await Assert.That(string.Join(",", kills.DeclaredReads)).IsEqualTo(
            "enrich.kill.was_enemy_kill,event.Attacker,event.UserId,round.bomb.was_planted");
        // The enrichment/event-field reads are declared reads, NOT entity-provider reads.
        await Assert.That(kills.EntityReads.Count).IsEqualTo(0);

        // kill_ticks: capture list<instant>, same spliced trigger + gate, distinct value selector.
        CheckedStat ticks = Stat(rs, "kill_ticks");
        await Assert.That(ticks.Kind).IsEqualTo(RuleNodeKind.Capture);
        await Assert.That(ticks.Keep).IsEqualTo(KeepKind.List);
        await Assert.That(ticks.ValueType).IsEqualTo(RulesType.ListOf(RulesTypeKind.Instant));
        await Assert.That(ticks.ValueSelector!.Root.CanonicalText).IsEqualTo("(ref event.tick)");
        await Assert.That(ticks.TriggerCondition!.Root.CanonicalText).IsEqualTo(
            kills.TriggerCondition!.Root.CanonicalText);

        // Highlight: match-scoped auto .count node; when: reads the sibling stat.
        CheckedHighlight highlight = rs.Highlights[0];
        await Assert.That(highlight.HighlightId).IsEqualTo("post_plant_double");
        await Assert.That(highlight.Scope).IsEqualTo(ScopeAxis.PlayerRound);
        await Assert.That(highlight.CountScope).IsEqualTo(ScopeAxis.PlayerMatch);
        await Assert.That(highlight.CountNodeId).IsEqualTo("post_plant_double.count");
        await Assert.That(highlight.When.Root.CanonicalText).IsEqualTo("(ge (ref post_plant_kills) (int 2))");
        await Assert.That(string.Join(",", highlight.DeclaredReads)).IsEqualTo("post_plant_kills");
    }

    // ── Params → literals (decision 2) ─────────────────────────────────────────

    [Test]
    public async Task Params_BindToLiterals_TwoValuesProduceDifferentAsts()
    {
        CheckedRulesetDraft draft = CheckedRulesetDraft.Load(Doc(Pilot), _adapter);

        string two = draft.Build(64.0, Gotv, new Dictionary<string, object?>
            {
                ["min_kills"] = 2L
            })
            .Ruleset!.Highlights[0].When.Root.CanonicalText;
        string three = draft.Build(64.0, Gotv, new Dictionary<string, object?>
            {
                ["min_kills"] = 3L
            })
            .Ruleset!.Highlights[0].When.Root.CanonicalText;

        await Assert.That(two).IsEqualTo("(ge (ref post_plant_kills) (int 2))");
        await Assert.That(three).IsEqualTo("(ge (ref post_plant_kills) (int 3))");
        await Assert.That(two).IsNotEqualTo(three);
    }

    [Test]
    public async Task Draft_KeepsParamsSymbolic()
    {
        // Decision 2: symbolic params survive ONLY on the demo-less path (no hashing there).
        CheckedRulesetDraft draft = CheckedRulesetDraft.Load(Doc(Pilot), _adapter);

        await Assert.That(draft.Success).IsTrue();
        CheckedHighlight highlight = draft.DemolessRuleset!.Highlights[0];
        await Assert.That(highlight.When.Root.CanonicalText)
            .IsEqualTo("(ge (ref post_plant_kills) (ref params.min_kills))");
    }

    // ── Highlight score / kind ──────────────────────────────────────────────────

    /// <summary>
    ///     A highlight's authored <c>score:</c>/<c>kind:</c> resolve onto <see cref="CheckedHighlight" />
    ///     — defaulting to 50 / <see cref="HighlightKind.Highlight" /> when absent, and carrying the
    ///     explicit values otherwise (including <c>hidden</c>, the counting-only track).
    /// </summary>
    [Test]
    public async Task Highlight_ScoreAndKind_DefaultAndResolveExplicitValues()
    {
        const string Yaml = """
                            ruleset: hl_meta
                            for: each_player
                            stats:
                              kills_r: { count: kill, per: round }
                            highlights:
                              plain:      { when: kills_r >= 2, per: round, title: "T" }
                              scored:     { when: kills_r >= 3, per: round, score: 80, kind: lowlight, title: "T" }
                              counter:    { when: kills_r >= 1, per: round, kind: hidden, title: "T" }
                            """;

        RulesetResolveResult result = CheckedRulesetDraft.Load(Doc(Yaml), _adapter).Build(64.0, Gotv);
        await Assert.That(result.Success).IsTrue();

        CheckedHighlight plain = Highlight(result.Ruleset!, "plain");
        await Assert.That(plain.Score).IsEqualTo(50).Because("score: defaults to 50");
        await Assert.That(plain.Kind).IsEqualTo(HighlightKind.Highlight).Because("kind: defaults to Highlight");

        CheckedHighlight scored = Highlight(result.Ruleset!, "scored");
        await Assert.That(scored.Score).IsEqualTo(80);
        await Assert.That(scored.Kind).IsEqualTo(HighlightKind.Lowlight);

        await Assert.That(Highlight(result.Ruleset!, "counter").Kind).IsEqualTo(HighlightKind.Hidden);
    }

    /// <summary>An unrecognized <c>kind:</c> value reports a <c>BadHighlightKind</c> diagnostic naming the bad value.</summary>
    [Test]
    public async Task Highlight_BadKind_ReportsDiagnostic()
    {
        const string Yaml = """
                            ruleset: hl_badkind
                            for: each_player
                            stats:
                              kills_r: { count: kill, per: round }
                            highlights:
                              h: { when: kills_r >= 2, per: round, kind: bogus, title: "T" }
                            """;

        RulesetResolveResult result = CheckedRulesetDraft.Load(Doc(Yaml), _adapter).Build(64.0, Gotv);

        RulesetDiagnostic bad = result.Diagnostics.Single(d => d.Code == ResolveDiagnosticCodes.BadHighlightKind);
        await Assert.That(bad.Message.Contains("bogus", StringComparison.Ordinal)).IsTrue();
        await Assert.That(bad.Position.Line).IsGreaterThan(0);
    }

    // ── Define inlining ────────────────────────────────────────────────────────

    [Test]
    public async Task Define_ListInlinesAtUseSite()
    {
        const string Yaml = """
                            ruleset: rifle_kills
                            for: each_player
                            define:
                              rifles: [ak47, m4a1]
                            stats:
                              rk:
                                count: kill
                                match: { weapon: in rifles }
                                per: match
                            """;

        RulesetResolveResult result = CheckedRulesetDraft.Load(Doc(Yaml), _adapter).Build(64.0, Gotv);

        await Assert.That(result.Success).IsTrue();
        string condition = Stat(result.Ruleset!, "rk").TriggerCondition!.Root.CanonicalText;
        // The 'in rifles' list ref inlines to a literal list before hashing (spec §5 row 4).
        await Assert.That(condition.Contains("(in (ref event.Weapon) (list (str \"ak47\") (str \"m4a1\")))",
            StringComparison.Ordinal)).IsTrue();
        await Assert.That(condition.Contains("rifles", StringComparison.Ordinal)).IsFalse();
    }

    // ── Coverage skips (never silent) ──────────────────────────────────────────

    [Test]
    public async Task UnbindableView_YieldsCoverageDiagnostic()
    {
        // The 'blinded' view has no wire event on the HLTV profile → coverage-skipped, not zeroed.
        const string Yaml = """
                            ruleset: flashes
                            for: each_player
                            stats:
                              blinds:
                                count: blinded
                                per: match
                            """;

        RulesetResolveResult result = CheckedRulesetDraft.Load(Doc(Yaml), _adapter).Build(64.0, "Cs2HltvProfile");

        await Assert.That(result.Success).IsTrue(); // a coverage skip is not an error
        CheckedRuleset rs = result.Ruleset!;
        await Assert.That(rs.Stats.Count).IsEqualTo(0);
        await Assert.That(rs.Coverage.Count).IsEqualTo(1);
        await Assert.That(rs.Coverage[0].NodeId).IsEqualTo("blinds");
        await Assert.That(rs.Coverage[0].ViewName).IsEqualTo("blinded");
        await Assert.That(rs.Coverage[0].ProfileId).IsEqualTo("Cs2HltvProfile");

        // The same view binds on GOTV, so no skip there.
        RulesetResolveResult gotv = CheckedRulesetDraft.Load(Doc(Yaml), _adapter).Build(64.0, Gotv);
        await Assert.That(gotv.Ruleset!.Stats.Count).IsEqualTo(1);
        await Assert.That(gotv.Ruleset!.Coverage.Count).IsEqualTo(0);
    }

    // ── `this` self-reference ──────────────────────────────────────────────────

    [Test]
    public async Task This_TypesAsEnclosingStatValue_AndIsNotAStatReference()
    {
        const string Yaml = """
                            ruleset: first_tick
                            for: each_player
                            stats:
                              ft:
                                capture: event.tick
                                on: kill
                                where: this > 100
                                per: match
                            """;

        RulesetResolveResult result = CheckedRulesetDraft.Load(Doc(Yaml), _adapter).Build(64.0, Gotv);

        await Assert.That(result.Success).IsTrue();
        CheckedExpression condition = Stat(result.Ruleset!, "ft").TriggerCondition!;
        ResolvedReference thisRef = condition.References.Single(r => r.Path == "this");
        // `this` is the enclosing capture's value type (event.tick → instant) …
        await Assert.That(thisRef.Type).IsEqualTo(RulesType.Instant);
        // … and it is a non-stat Value symbol, so it never contributes a cycle edge (spec §4).
        await Assert.That(thisRef.IsStatReference).IsFalse();
    }

    // ── compute: is single-AST (formula in the condition slot, not the value selector) ──

    [Test]
    public async Task Compute_FormulaLandsInConditionSlot_NotValueSelector()
    {
        const string Yaml = """
                            ruleset: ratio
                            for: each_player
                            stats:
                              kills:
                                count: kill
                                per: match
                              doubled:
                                compute: max(kills, 1)
                                per: match
                            """;

        RulesetResolveResult result = CheckedRulesetDraft.Load(Doc(Yaml), _adapter).Build(64.0, Gotv);

        await Assert.That(result.Success).IsTrue();
        CheckedStat compute = Stat(result.Ruleset!, "doubled");
        await Assert.That(compute.Kind).IsEqualTo(RuleNodeKind.Compute);
        // compute is single-AST — its formula is the row-5 expression, and it reads a sibling.
        await Assert.That(compute.TriggerCondition!.Root.CanonicalText).IsEqualTo("(call max (ref kills) (int 1))");
        await Assert.That(compute.ValueSelector).IsNull();
        // Iterative value-type inference resolves the sibling: max(int, int) → int.
        await Assert.That(compute.ValueType).IsEqualTo(RulesType.Int);
        await Assert.That(compute.Scope).IsEqualTo(ScopeAxis.PlayerMatch);
        await Assert.That(compute.ConcreteEvents.Count).IsEqualTo(0);
    }

    // ── Cross-stat cycle ───────────────────────────────────────────────────────

    [Test]
    public async Task CrossStatCycle_IsABuildError_WithPath()
    {
        // a's predicate reads b; b's predicate reads a — the per-expression checker cannot see this.
        const string Yaml = """
                            ruleset: cyclic
                            for: match
                            stats:
                              a:
                                flag: b
                                per: match
                              b:
                                flag: a
                                per: match
                            """;

        RulesetResolveResult result = CheckedRulesetDraft.Load(Doc(Yaml), _adapter).Build(64.0, Gotv);

        await Assert.That(result.Success).IsFalse();
        RulesetDiagnostic cycle = result.Diagnostics.Single(d =>
            d.Code == ResolveDiagnosticCodes.StatReferenceCycle);
        await Assert.That(cycle.Message.Contains("->", StringComparison.Ordinal)).IsTrue();
        await Assert.That(cycle.Position.Line).IsGreaterThan(0);
    }
}
