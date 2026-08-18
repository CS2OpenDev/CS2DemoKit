#region

using CS2DemoKit.Analysis.Catalog;
using CS2DemoKit.Analysis.Rules;
using CS2DemoKit.Analysis.Rules.Hashing;
using CS2DemoKit.Analysis.Rules.Scopes;
using CS2DemoKit.Analysis.RulesetsV2.Model;
using CS2DemoKit.Analysis.RulesetsV2.Resolve;
using CS2DemoKit.Analysis.Yaml;

#endregion

namespace CS2DemoKit.Analysis.Tests.RulesV2;

/// <summary>
///     Checker-integration battery (the check stage): the resolver runs the
///     semantic-core typed checker per slot with the right Catalog-backed scope and expected type,
///     and surfaces every failure as a positioned <see cref="RulesetDiagnostic" /> — out-of-scope
///     references, wrong slot types, unknown facets, and duplicate merged <c>match:</c> keys. Also
///     pins the adapter's friendly-type mapping (the unknown-type path is a loud build error) and
///     the injected symbols (<c>round.bomb.was_planted</c>, instants). Demo-free.
/// </summary>
[Category("Unit")]
public class RulesetV2CheckerTests
{
    private const string Gotv = "Cs2GotvProfile";
    private static readonly CatalogScopeAdapter _adapter = CatalogScopeAdapter.From(CatalogResource.Load());

    private static RulesetDoc Doc(string yaml)
    {
        RulesetDocumentLoader.Outcome outcome = RulesetDocumentLoader.Load(yaml, "test.rules.yaml");
        return outcome.Doc
               ?? throw new InvalidOperationException(
                   $"YAML failed to map: {string.Join("; ", outcome.Diagnostics)}");
    }

    private static RulesetResolveResult Build(string yaml) =>
        CheckedRulesetDraft.Load(Doc(yaml), _adapter).Build(64.0, Gotv);

    // ── Adapter friendly-type mapping ──────────────────────────────────────────

    [Test]
    public async Task FriendlyTypeMap_MapsTheClosedSet()
    {
        await Assert.That(FriendlyTypeMap.Map("bool")).IsEqualTo(RulesType.Bool);
        await Assert.That(FriendlyTypeMap.Map("int")).IsEqualTo(RulesType.Int);
        await Assert.That(FriendlyTypeMap.Map("uint")).IsEqualTo(RulesType.Int);
        await Assert.That(FriendlyTypeMap.Map("ulong")).IsEqualTo(RulesType.Int);
        await Assert.That(FriendlyTypeMap.Map("long")).IsEqualTo(RulesType.Int);
        await Assert.That(FriendlyTypeMap.Map("float")).IsEqualTo(RulesType.Float);
        await Assert.That(FriendlyTypeMap.Map("double")).IsEqualTo(RulesType.Float);
        await Assert.That(FriendlyTypeMap.Map("string")).IsEqualTo(RulesType.String);
    }

    [Test]
    public async Task FriendlyTypeMap_UnknownType_ThrowsLoudly()
    {
        // The adapter never silently skips an unmapped type — it is a generator/build error.
        InvalidOperationException ex =
            Assert.Throws<InvalidOperationException>(() => FriendlyTypeMap.Map("uint128"));
        await Assert.That(ex.Message.Contains("uint128", StringComparison.Ordinal)).IsTrue();
    }

    [Test]
    public async Task Adapter_InjectsStickyBombGate_AndInstants()
    {
        // round.bomb.was_planted is injected by the adapter (catalog gains it in 2.2c).
        await Assert.That(_adapter.Round.TryGetMember("bomb", out IScopeSymbol? bomb)).IsTrue();
        await Assert.That(bomb!.TryGetMember("was_planted", out IScopeSymbol? planted)).IsTrue();
        await Assert.That(planted!.ValueType).IsEqualTo(RulesType.Bool);

        // match.tick is an injected instant (no catalog event field carries it).
        await Assert.That(_adapter.Match.TryGetMember("tick", out IScopeSymbol? matchTick)).IsTrue();
        await Assert.That(matchTick!.ValueType).IsEqualTo(RulesType.Instant);
    }

    // ── Out-of-scope reference ─────────────────────────────────────────────────

    [Test]
    public async Task OutOfScopeReference_Errors_WithPosition()
    {
        const string Yaml = """
                            ruleset: bad_ref
                            for: each_player
                            stats:
                              s:
                                count: kill
                                where: bogus_root > 1
                                per: match
                            """;

        RulesetResolveResult result = Build(Yaml);

        await Assert.That(result.Success).IsFalse();
        RulesetDiagnostic error = result.Diagnostics.Single(d => d.Code == DiagnosticCodes.UnknownRoot);
        await Assert.That(error.Message.Contains("bogus_root", StringComparison.Ordinal)).IsTrue();
        await Assert.That(error.Position.Line).IsGreaterThan(0);
    }

    // ── Wrong slot type ────────────────────────────────────────────────────────

    [Test]
    public async Task WrongSlotType_Errors_WithPosition()
    {
        // A highlight's when: must be bool; an int counter is not (spec §4 expected type).
        const string Yaml = """
                            ruleset: bad_type
                            for: each_player
                            stats:
                              kills:
                                count: kill
                                per: match
                            highlights:
                              h:
                                when: kills
                                title: "x"
                            """;

        RulesetResolveResult result = Build(Yaml);

        await Assert.That(result.Success).IsFalse();
        RulesetDiagnostic error = result.Diagnostics.Single(d => d.Code == DiagnosticCodes.ExpectedType);
        await Assert.That(error.Position.Line).IsGreaterThan(0);
    }

    // ── Unknown facet ──────────────────────────────────────────────────────────

    [Test]
    public async Task UnknownFacet_Errors()
    {
        const string Yaml = """
                            ruleset: bad_facet
                            for: each_player
                            stats:
                              s:
                                count: kill
                                match: { not_a_facet: true }
                                per: match
                            """;

        RulesetResolveResult result = Build(Yaml);

        await Assert.That(result.Success).IsFalse();
        RulesetDiagnostic error = result.Diagnostics.Single(d => d.Code == ResolveDiagnosticCodes.UnknownFacet);
        await Assert.That(error.Message.Contains("not_a_facet", StringComparison.Ordinal)).IsTrue();
    }

    // ── Duplicate merged match: key (define + site) ────────────────────────────

    [Test]
    public async Task DuplicateMatchKey_AcrossDefineAndSite_Errors()
    {
        // The 'enemy' key is set in both the trigger define and the count site — no silent last-wins.
        const string Yaml = """
                            ruleset: dup_match
                            for: each_player
                            define:
                              enemy_kill:
                                on: kill
                                match: { enemy: true }
                            stats:
                              s:
                                count: enemy_kill
                                match: { enemy: false }
                                per: match
                            """;

        RulesetResolveResult result = Build(Yaml);

        await Assert.That(result.Success).IsFalse();
        RulesetDiagnostic error = result.Diagnostics.Single(d => d.Code == ResolveDiagnosticCodes.DuplicateMatchKey);
        await Assert.That(error.Message.Contains("enemy", StringComparison.Ordinal)).IsTrue();
    }

    // ── tally resolves; streak/bucket still gated loudly (window/key not yet modeled) ──

    [Test]
    public async Task TallyKind_WithSourceAndThresholds_Resolves()
    {
        // A well-formed tally (source value + thresholds) resolves to a Tally node carrying its
        // (min, target) pairs — no longer gated as unsupported.
        const string Yaml = """
                            ruleset: t
                            for: each_player
                            stats:
                              round_kills:
                                count: kill
                                per: round
                              multi:
                                tally: round_kills
                                thresholds:
                                  - { min: 2, target: multi2 }
                                  - { min: 3, target: multi3 }
                                per: match
                            """;

        RulesetResolveResult result = Build(Yaml);

        await Assert.That(result.Success).IsTrue();
        CheckedStat tally = result.Ruleset!.Stats.Single(s => s.StatId == "multi");
        await Assert.That(tally.Kind).IsEqualTo(RuleNodeKind.Tally);
        await Assert.That(tally.TallyThresholds!.Count).IsEqualTo(2);
        await Assert.That(tally.TallyThresholds!.Any(t => t is { Min: 3, Target: "multi3" })).IsTrue();
    }

    [Test]
    public async Task StreakKind_WithWindowAndMinStreak_Resolves()
    {
        const string Yaml = """
                            ruleset: t
                            for: each_player
                            stats:
                              s:
                                streak: kill
                                window: 640
                                min_streak: 2
                                per: match
                            """;

        RulesetResolveResult result = Build(Yaml);

        await Assert.That(result.Success).IsTrue();
        CheckedStat streak = result.Ruleset!.Stats.Single(s => s.StatId == "s");
        await Assert.That(streak.Kind).IsEqualTo(RuleNodeKind.Streak);
        await Assert.That(streak.StreakWindow).IsEqualTo(640);
        await Assert.That(streak.StreakMinStreak).IsEqualTo(2);
    }

    [Test]
    public async Task BucketKind_WithKey_Resolves()
    {
        const string Yaml = """
                            ruleset: t
                            for: each_player
                            stats:
                              s:
                                bucket: kill
                                key: event.Weapon
                                per: match
                            """;

        RulesetResolveResult result = Build(Yaml);

        await Assert.That(result.Success).IsTrue();
        CheckedStat bucket = result.Ruleset!.Stats.Single(s => s.StatId == "s");
        await Assert.That(bucket.Kind).IsEqualTo(RuleNodeKind.Bucket);
        await Assert.That(bucket.BucketKeyParts!.Single()).IsEqualTo("event.Weapon");
    }

    // ── Unknown trigger source ─────────────────────────────────────────────────

    [Test]
    public async Task UnknownTriggerSource_Errors()
    {
        const string Yaml = """
                            ruleset: bad_src
                            for: each_player
                            stats:
                              s:
                                count: not_a_view
                                per: match
                            """;

        RulesetResolveResult result = Build(Yaml);

        await Assert.That(result.Success).IsFalse();
        await Assert.That(result.Diagnostics.Any(d => d.Code == ResolveDiagnosticCodes.UnknownTriggerSource)).IsTrue();
    }
}
