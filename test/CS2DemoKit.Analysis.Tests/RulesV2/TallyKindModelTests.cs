#region

using CS2DemoKit.Analysis.Catalog;
using CS2DemoKit.Analysis.RulesetsV2.Compile;
using CS2DemoKit.Analysis.RulesetsV2.Model;
using CS2DemoKit.Analysis.RulesetsV2.Resolve;
using CS2DemoKit.Analysis.Yaml;

#endregion

namespace CS2DemoKit.Analysis.Tests.RulesV2;

/// <summary>
///     The <c>tally:</c> vertical demo-free battery: the model carries the
///     <c>thresholds:</c> list, structural validation enforces the tally shape (thresholds required,
///     tally-only), and the resolved-identity hash distinguishes tallies that differ only in a
///     threshold's <c>(min, target)</c>. (The v1-differential runtime golden retired with
///     Rulesets v1. Runtime tally values are pinned by <c>PlayerStatsPilotTests</c> and, on the
///     committed sample demo, by <c>StatParityTests</c> via <c>multi_2k</c>..<c>multi_5k</c>.)
/// </summary>
[Category("Unit")]
public class TallyKindModelTests
{
    private const string ValidTally = """
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

    private static readonly CatalogScopeAdapter _adapter = CatalogScopeAdapter.From(CatalogResource.Load());

    // ── Mapping ────────────────────────────────────────────────────────────────

    [Test]
    public async Task Maps_Tally_KindArg_AndThresholds()
    {
        RulesetDocumentLoader.Outcome outcome = RulesetDocumentLoader.Load(ValidTally, "t.rules.yaml");
        await Assert.That(outcome.Diagnostics).IsEmpty();

        StatDef tally = outcome.Doc!.Stats.Single(s => s.Id == "multi");
        await Assert.That(tally.Kind).IsEqualTo(StatKind.Tally);
        await Assert.That(tally.KindArg).IsEqualTo("round_kills");
        await Assert.That(tally.Thresholds!.Count).IsEqualTo(2);
        await Assert.That(tally.Thresholds![0])
            .IsEqualTo(new TallyThreshold(new TallyMinLiteral(2), "multi2", tally.Thresholds![0].Position));
        await Assert.That(tally.Thresholds![1].Min).IsEqualTo(new TallyMinLiteral(3));
        await Assert.That(tally.Thresholds![1].Target).IsEqualTo("multi3");
    }

    // ── Structural validation ──────────────────────────────────────────────────

    [Test]
    public async Task Tally_WithoutThresholds_IsRejected()
    {
        const string Yaml = """
                            ruleset: t
                            for: each_player
                            stats:
                              round_kills:
                                count: kill
                                per: round
                              multi:
                                tally: round_kills
                                per: match
                            """;

        RulesetDocumentLoader.Outcome outcome = RulesetDocumentLoader.Load(Yaml, "t.rules.yaml");
        await Assert.That(outcome.Diagnostics.Any(d => d.Code == RulesetDiagnosticCodes.BadKindArgs)).IsTrue();
    }

    [Test]
    public async Task Thresholds_OnNonTally_IsRejected()
    {
        const string Yaml = """
                            ruleset: t
                            for: each_player
                            stats:
                              c:
                                count: kill
                                per: round
                                thresholds:
                                  - { min: 2, target: x }
                            """;

        RulesetDocumentLoader.Outcome outcome = RulesetDocumentLoader.Load(Yaml, "t.rules.yaml");
        await Assert.That(outcome.Diagnostics.Any(d => d.Code == RulesetDiagnosticCodes.BadKindArgs)).IsTrue();
    }

    // ── Resolved-identity hash distinctness (row 8) ─────────────────────────────

    [Test]
    public async Task TwoTallies_DifferingInAThreshold_HashApart()
    {
        // Baseline vs a single min changed (3 -> 4) vs a single target changed (multi3 -> other).
        byte[] baseline = TallyHash(ValidTally);
        byte[] changedMin = TallyHash(ValidTally.Replace("min: 3, target: multi3", "min: 4, target: multi3",
            StringComparison.Ordinal));
        byte[] changedTarget = TallyHash(ValidTally.Replace("min: 3, target: multi3", "min: 3, target: other",
            StringComparison.Ordinal));

        await Assert.That(Convert.ToHexString(changedMin)).IsNotEqualTo(Convert.ToHexString(baseline));
        await Assert.That(Convert.ToHexString(changedTarget)).IsNotEqualTo(Convert.ToHexString(baseline));

        // Re-resolving the identical ruleset hashes identically (dedup-stable).
        await Assert.That(Convert.ToHexString(TallyHash(ValidTally))).IsEqualTo(Convert.ToHexString(baseline));
    }

    /// <summary>
    ///     Resolves the ruleset and returns the resolved-identity hash of its <c>multi</c> tally,
    ///     replicating the planner's dependency-ordered hashing so the tally's source reference resolves.
    /// </summary>
    private static byte[] TallyHash(string yaml)
    {
        RulesetDocumentLoader.Outcome outcome = RulesetDocumentLoader.Load(yaml, "t.rules.yaml");
        RulesetResolveResult resolved = CheckedRulesetDraft.Load(outcome.Doc!, _adapter).Build(64.0, "Cs2GotvProfile");
        CheckedRuleset ruleset = resolved.Ruleset
                                 ?? throw new InvalidOperationException(
                                     "tally ruleset failed to resolve: " + string.Join("; ", resolved.Diagnostics));

        Dictionary<string, ReadOnlyMemory<byte>> byPath = new(StringComparer.Ordinal);
        MapStatHashSource source = new(byPath);
        byte[]? tallyHash = null;
        foreach (CheckedStat stat in ruleset.Stats)
        {
            byte[] hash = V2StatHasher.Hash(stat, source);
            byPath[stat.StatId] = hash;
            byPath[$"{ruleset.Id.Id}.{stat.StatId}"] = hash;
            if (stat.StatId == "multi")
            {
                tallyHash = hash;
            }
        }

        return tallyHash ?? throw new InvalidOperationException("tally stat 'multi' not resolved");
    }
}
