#region

using CS2DemoKit.Analysis.Catalog;
using CS2DemoKit.Analysis.RulesetsV2.Compile;
using CS2DemoKit.Analysis.RulesetsV2.Model;
using CS2DemoKit.Analysis.RulesetsV2.Resolve;
using CS2DemoKit.Analysis.Yaml;

#endregion

namespace CS2DemoKit.Analysis.Tests.RulesV2;

/// <summary>
///     The <c>streak:</c> vertical demo-free battery: the model carries
///     <c>window:</c>/<c>min_streak:</c>, structural validation enforces the streak shape (source
///     required, window/min-streak streak-only, positive min), the resolver folds a duration window
///     to ticks, and the resolved-identity hash distinguishes streaks that differ only in window or
///     min-streak. (The v1-differential runtime golden retired with Rulesets v1. Runtime streak
///     values are pinned by <c>PlayerStatsPilotTests</c>' <c>rapid_kill_sequences</c> column, which
///     needs the reference demo and skips without it; nothing pins them on a bare clone.)
/// </summary>
[Category("Unit")]
public class StreakKindModelTests
{
    private const string ValidStreak = """
                                       ruleset: t
                                       for: each_player
                                       stats:
                                         s:
                                           streak: kill
                                           window: 640
                                           min_streak: 2
                                           per: match
                                       """;

    private static readonly CatalogScopeAdapter _adapter = CatalogScopeAdapter.From(CatalogResource.Load());

    // ── Mapping ────────────────────────────────────────────────────────────────

    [Test]
    public async Task Maps_Streak_KindArg_WindowAndMinStreak()
    {
        RulesetDocumentLoader.Outcome outcome = RulesetDocumentLoader.Load(ValidStreak, "t.rules.yaml");
        await Assert.That(outcome.Diagnostics).IsEmpty();

        StatDef streak = outcome.Doc!.Stats.Single(s => s.Id == "s");
        await Assert.That(streak.Kind).IsEqualTo(StatKind.Streak);
        await Assert.That(streak.KindArg).IsEqualTo("kill");
        await Assert.That(streak.StreakWindow).IsEqualTo("640");
        await Assert.That(streak.StreakMinStreak).IsEqualTo(2);
    }

    // ── Structural validation ──────────────────────────────────────────────────

    [Test]
    public async Task Streak_WithoutSource_IsRejected()
    {
        // A streak with no kind value (the event source) — mapping still yields a doc, validation flags it.
        const string Yaml = """
                            ruleset: t
                            for: each_player
                            stats:
                              s:
                                streak:
                                window: 640
                                per: match
                            """;

        RulesetDocumentLoader.Outcome outcome = RulesetDocumentLoader.Load(Yaml, "t.rules.yaml");
        await Assert.That(outcome.Diagnostics.Any(d => d.Code == RulesetDiagnosticCodes.BadKindArgs)).IsTrue();
    }

    [Test]
    public async Task WindowOrMinStreak_OnNonStreak_IsRejected()
    {
        const string Yaml = """
                            ruleset: t
                            for: each_player
                            stats:
                              c:
                                count: kill
                                window: 640
                                per: round
                            """;

        RulesetDocumentLoader.Outcome outcome = RulesetDocumentLoader.Load(Yaml, "t.rules.yaml");
        await Assert.That(outcome.Diagnostics.Any(d => d.Code == RulesetDiagnosticCodes.BadKindArgs)).IsTrue();
    }

    // ── Duration window folds to ticks ─────────────────────────────────────────

    [Test]
    public async Task DurationWindow_FoldsToTicks_AtContextTickRate()
    {
        // 10s at 64 t/s folds to 640 ticks — identical to the integer form.
        const string DurationYaml = """
                                    ruleset: t
                                    for: each_player
                                    stats:
                                      s:
                                        streak: kill
                                        window: 10s
                                        min_streak: 2
                                        per: match
                                    """;

        CheckedStat folded = Resolve(DurationYaml).Ruleset!.Stats.Single(s => s.StatId == "s");
        await Assert.That(folded.StreakWindow).IsEqualTo(640);
    }

    [Test]
    public async Task ClockWindow_FoldsToTicks_SameAsSecondsLiteral()
    {
        // The "m:ss" slot scalar "1:30" = 90s; at 64 t/s that is 90*64 = 5760 ticks, identical to 90s.
        const string ClockYaml = """
                                 ruleset: t
                                 for: each_player
                                 stats:
                                   s:
                                     streak: kill
                                     window: "1:30"
                                     min_streak: 2
                                     per: match
                                 """;
        const string SecondsYaml = """
                                   ruleset: t
                                   for: each_player
                                   stats:
                                     s:
                                       streak: kill
                                       window: 90s
                                       min_streak: 2
                                       per: match
                                   """;

        int clockTicks = Resolve(ClockYaml).Ruleset!.Stats.Single(s => s.StatId == "s").StreakWindow!.Value;
        int secondsTicks = Resolve(SecondsYaml).Ruleset!.Stats.Single(s => s.StatId == "s").StreakWindow!.Value;

        await Assert.That(clockTicks).IsEqualTo(5760);
        await Assert.That(clockTicks).IsEqualTo(secondsTicks)
            .Because("the m:ss clock form and the s literal fold to the same tick count");
    }

    // ── Resolved-identity hash distinctness (row 8) ─────────────────────────────

    [Test]
    public async Task Streaks_DifferingInWindowOrMinStreak_HashApart()
    {
        byte[] baseline = StreakHash(ValidStreak);
        byte[] biggerWindow = StreakHash(ValidStreak.Replace("window: 640", "window: 320", StringComparison.Ordinal));
        byte[] longerMin = StreakHash(ValidStreak.Replace("min_streak: 2", "min_streak: 3", StringComparison.Ordinal));

        await Assert.That(Convert.ToHexString(biggerWindow)).IsNotEqualTo(Convert.ToHexString(baseline));
        await Assert.That(Convert.ToHexString(longerMin)).IsNotEqualTo(Convert.ToHexString(baseline));

        // The integer and duration spellings of the same window hash identically (both fold to 640).
        byte[] durationForm = StreakHash(ValidStreak.Replace("window: 640", "window: 10s", StringComparison.Ordinal));
        await Assert.That(Convert.ToHexString(durationForm)).IsEqualTo(Convert.ToHexString(baseline));
    }

    private static RulesetResolveResult Resolve(string yaml)
    {
        RulesetDocumentLoader.Outcome outcome = RulesetDocumentLoader.Load(yaml, "t.rules.yaml");
        return CheckedRulesetDraft.Load(outcome.Doc!, _adapter).Build(64.0, "Cs2GotvProfile");
    }

    private static byte[] StreakHash(string yaml)
    {
        CheckedRuleset ruleset = Resolve(yaml).Ruleset
                                 ?? throw new InvalidOperationException("streak ruleset failed to resolve");
        MapStatHashSource source = new(new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.Ordinal));
        return V2StatHasher.Hash(ruleset.Stats.Single(s => s.StatId == "s"), source);
    }
}
