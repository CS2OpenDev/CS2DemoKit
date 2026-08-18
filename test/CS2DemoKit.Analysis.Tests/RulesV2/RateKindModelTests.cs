#region

using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Analysis.Building;
using CS2DemoKit.Analysis.Catalog;
using CS2DemoKit.Analysis.Config;
using CS2DemoKit.Analysis.Graphs;
using CS2DemoKit.Analysis.Nodes;
using CS2DemoKit.Analysis.Registry;
using CS2DemoKit.Analysis.Rules.Hashing;
using CS2DemoKit.Analysis.RulesetsV2.Compile;
using CS2DemoKit.Analysis.RulesetsV2.Model;
using CS2DemoKit.Analysis.RulesetsV2.Resolve;
using CS2DemoKit.Analysis.Yaml;

#endregion

namespace CS2DemoKit.Analysis.Tests.RulesV2;

/// <summary>
///     Pre-freeze gap G3 (<c>rate:</c> per-key ratios) demo-free vertical battery: the model carries the
///     <c>of:</c>/<c>per:</c> refs, structural validation enforces the rate shape (both refs required,
///     match-scoped), the resolver type-checks both refs are numeric sibling buckets keying on identical
///     <c>key:</c> parts, the resolved-identity hash distinguishes rates over different bucket pairs
///     (via the synthesized <c>of / per</c> row-5 expression), the planner materializes a
///     <see cref="KeyedRatioNode" />, and the node divides per-key over the denominator key set with the
///     locked skip/zero semantics.
/// </summary>
[Category("Unit")]
public class RateKindModelTests
{
    // Two buckets over the kill view, both keyed on event.Weapon (identical key parts), distinguished
    // only by match: {enemy: true} — so a rate over the two is well-formed, and swapping of/per yields a
    // structurally-different (hash-distinct) rate.
    private const string ValidRate = """
                                     ruleset: t
                                     for: each_player
                                     stats:
                                       kills_by_weapon:
                                         bucket: kill
                                         key: event.Weapon
                                         per: match
                                       enemy_kills_by_weapon:
                                         bucket: kill
                                         key: event.Weapon
                                         match: { enemy: true }
                                         per: match
                                       enemy_rate:
                                         rate: { of: enemy_kills_by_weapon, per: kills_by_weapon }
                                         per: match
                                     """;

    // ── Resolved-identity hash (row 5+6 via the synthesized of/per expression) ───

    // Two rates over DIFFERENT bucket pairs (of/per swapped) plus a twin of the first.
    private const string HashRate = """
                                    ruleset: t
                                    for: each_player
                                    stats:
                                      a:
                                        bucket: kill
                                        key: event.Weapon
                                        per: match
                                      b:
                                        bucket: kill
                                        key: event.Weapon
                                        match: { enemy: true }
                                        per: match
                                      rate_ba:
                                        rate: { of: b, per: a }
                                        per: match
                                      rate_ab:
                                        rate: { of: a, per: b }
                                        per: match
                                      rate_ba_twin:
                                        rate: { of: b, per: a }
                                        per: match
                                    """;

    private static readonly CatalogScopeAdapter _adapter = CatalogScopeAdapter.From(CatalogResource.Load());

    // ── Mapping ────────────────────────────────────────────────────────────────

    [Test]
    public async Task Maps_Rate_OfAndPer()
    {
        RulesetDocumentLoader.Outcome outcome = RulesetDocumentLoader.Load(ValidRate, "t.rules.yaml");
        await Assert.That(outcome.Diagnostics).IsEmpty();

        StatDef rate = outcome.Doc!.Stats.Single(s => s.Id == "enemy_rate");
        await Assert.That(rate.Kind).IsEqualTo(StatKind.Rate);
        await Assert.That(rate.RateOf).IsEqualTo("enemy_kills_by_weapon");
        await Assert.That(rate.RatePer).IsEqualTo("kills_by_weapon");
    }

    [Test]
    public async Task Rate_DefaultsToMatchScope()
    {
        // No stat-level per: on a rate defaults to match (like a bucket), NOT the round default.
        const string Yaml = """
                            ruleset: t
                            for: each_player
                            stats:
                              a:
                                bucket: kill
                                key: event.Weapon
                                per: match
                              b:
                                bucket: kill
                                key: event.Weapon
                                match: { enemy: true }
                                per: match
                              r:
                                rate: { of: b, per: a }
                            """;

        RulesetDocumentLoader.Outcome outcome = RulesetDocumentLoader.Load(Yaml, "t.rules.yaml");
        await Assert.That(outcome.Diagnostics).IsEmpty();
        await Assert.That(outcome.Doc!.Stats.Single(s => s.Id == "r").Per).IsEqualTo(PerScope.Match);
    }

    // The nested rate per: (denominator ref) must NOT be confused with the stat-level per: reset scope.
    [Test]
    public async Task Rate_NestedPer_IsDenominator_NotResetScope()
    {
        RulesetDocumentLoader.Outcome outcome = RulesetDocumentLoader.Load(ValidRate, "t.rules.yaml");
        StatDef rate = outcome.Doc!.Stats.Single(s => s.Id == "enemy_rate");
        await Assert.That(rate.RatePer).IsEqualTo("kills_by_weapon"); // the nested denominator ref
        await Assert.That(rate.Per).IsEqualTo(PerScope.Match); // the stat-level reset scope
    }

    // ── Structural validation ──────────────────────────────────────────────────

    [Test]
    public async Task Rate_MissingOf_IsRejected()
    {
        RulesetDocumentLoader.Outcome outcome = RulesetDocumentLoader.Load(
            ValidRate.Replace("rate: { of: enemy_kills_by_weapon, per: kills_by_weapon }",
                "rate: { per: kills_by_weapon }", StringComparison.Ordinal),
            "t.rules.yaml");
        await Assert.That(outcome.Diagnostics.Any(d => d.Code == RulesetDiagnosticCodes.BadKindArgs)).IsTrue();
    }

    [Test]
    public async Task Rate_MissingPer_IsRejected()
    {
        RulesetDocumentLoader.Outcome outcome = RulesetDocumentLoader.Load(
            ValidRate.Replace("rate: { of: enemy_kills_by_weapon, per: kills_by_weapon }",
                "rate: { of: enemy_kills_by_weapon }", StringComparison.Ordinal),
            "t.rules.yaml");
        await Assert.That(outcome.Diagnostics.Any(d => d.Code == RulesetDiagnosticCodes.BadKindArgs)).IsTrue();
    }

    [Test]
    public async Task Rate_PerRound_IsRejected()
    {
        RulesetDocumentLoader.Outcome outcome = RulesetDocumentLoader.Load(
            ValidRate.Replace("""
                                enemy_rate:
                                  rate: { of: enemy_kills_by_weapon, per: kills_by_weapon }
                                  per: match
                              """,
                """
                  enemy_rate:
                    rate: { of: enemy_kills_by_weapon, per: kills_by_weapon }
                    per: round
                """, StringComparison.Ordinal),
            "t.rules.yaml");
        await Assert.That(outcome.Diagnostics.Any(d => d.Code == RulesetDiagnosticCodes.BadKindArgs)).IsTrue();
    }

    // ── Resolve type-checks ─────────────────────────────────────────────────────

    [Test]
    public async Task Rate_OverNonBucket_IsRejected()
    {
        // of: references a count stat, not a bucket → resolver error.
        const string Yaml = """
                            ruleset: t
                            for: each_player
                            stats:
                              kills:
                                count: kill
                                per: round
                              kills_by_weapon:
                                bucket: kill
                                key: event.Weapon
                                per: match
                              r:
                                rate: { of: kills, per: kills_by_weapon }
                                per: match
                            """;

        RulesetResolveResult resolved =
            CheckedRulesetDraft.Load(RulesetDocumentLoader.Load(Yaml, "t.rules.yaml").Doc!, _adapter)
                .Build(64.0, "Cs2GotvProfile");
        await Assert.That(resolved.Diagnostics.Any(d => d.Code == ResolveDiagnosticCodes.BadSlotType)).IsTrue();
    }

    [Test]
    public async Task Rate_DifferentKeyParts_IsRejected()
    {
        // of: keys on event.Weapon, per: keys on event.WeaponItemId → incomparable key spaces.
        const string Yaml = """
                            ruleset: t
                            for: each_player
                            stats:
                              by_weapon:
                                bucket: kill
                                key: event.Weapon
                                per: match
                              by_itemid:
                                bucket: kill
                                key: event.WeaponItemId
                                per: match
                              r:
                                rate: { of: by_weapon, per: by_itemid }
                                per: match
                            """;

        RulesetResolveResult resolved =
            CheckedRulesetDraft.Load(RulesetDocumentLoader.Load(Yaml, "t.rules.yaml").Doc!, _adapter)
                .Build(64.0, "Cs2GotvProfile");
        await Assert.That(resolved.Diagnostics.Any(d => d.Code == ResolveDiagnosticCodes.BadSlotType)).IsTrue();
    }

    [Test]
    public async Task Rate_UnknownRef_IsRejected()
    {
        const string Yaml = """
                            ruleset: t
                            for: each_player
                            stats:
                              by_weapon:
                                bucket: kill
                                key: event.Weapon
                                per: match
                              r:
                                rate: { of: by_weapon, per: nonexistent }
                                per: match
                            """;

        RulesetResolveResult resolved =
            CheckedRulesetDraft.Load(RulesetDocumentLoader.Load(Yaml, "t.rules.yaml").Doc!, _adapter)
                .Build(64.0, "Cs2GotvProfile");
        await Assert.That(resolved.Diagnostics.Any(d => d.Code == ResolveDiagnosticCodes.BadSlotType)).IsTrue();
    }

    [Test]
    public async Task Rate_ValidPair_Resolves_CarryingRefs()
    {
        CheckedRuleset rs = Compile(ValidRate);
        CheckedStat rate = rs.Stats.Single(s => s.StatId == "enemy_rate");
        await Assert.That(rate.Kind).IsEqualTo(RuleNodeKind.Rate);
        await Assert.That(rate.RateOf).IsEqualTo("enemy_kills_by_weapon");
        await Assert.That(rate.RatePer).IsEqualTo("kills_by_weapon");
        // The synthesized of/per division rides the row-5 TriggerCondition slot and reads both buckets.
        await Assert.That(rate.TriggerCondition).IsNotNull();
        await Assert.That(rate.DeclaredReads).Contains("enemy_kills_by_weapon");
        await Assert.That(rate.DeclaredReads).Contains("kills_by_weapon");
    }

    [Test]
    public async Task Rates_OverDifferentBucketPairs_HashApart_TwinDedups()
    {
        CheckedRuleset rs = Compile(HashRate);
        string ba = RateHashHex(rs, "rate_ba");
        string ab = RateHashHex(rs, "rate_ab");
        string baTwin = RateHashHex(rs, "rate_ba_twin");

        await Assert.That(ba).IsNotEqualTo(ab)
            .Because("b/a and a/b reference different bucket nodes in a different order — the row-6 embedded "
                     + "bucket hashes must keep them apart");
        await Assert.That(ba).IsEqualTo(baTwin)
            .Because("two rates over the SAME bucket pair are behaviorally interchangeable and must dedup");
    }

    /// <summary>
    ///     End-to-end through the planner: a rate materializes to a <see cref="KeyedRatioNode" />; two
    ///     rates over different bucket pairs are SEPARATE nodes; a twin dedups onto one node.
    /// </summary>
    [Test]
    public async Task Rate_Materializes_ToKeyedRatioNode_IdentityHolds()
    {
        CheckedRuleset rs = Compile(HashRate);
        Dictionary<string, StateNode> nodes = Materialize(rs);

        StateNode ba = nodes["t.rate_ba"];
        StateNode ab = nodes["t.rate_ab"];
        StateNode baTwin = nodes["t.rate_ba_twin"];

        await Assert.That(ba is KeyedRatioNode).IsTrue();
        await Assert.That(ReferenceEquals(ba, ab)).IsFalse()
            .Because("rates over different bucket pairs must be distinct nodes");
        await Assert.That(ReferenceEquals(ba, baTwin)).IsTrue()
            .Because("a rate twin over the same bucket pair must dedup onto the SAME node");
    }

    // ── Node semantics (denominator key set, missing→0, denom-0→skip) ───────────

    [Test]
    public async Task KeyedRatioNode_DividesOverDenominatorKeySet()
    {
        // Numerator (headshots): ak47=3, deagle=5 (deagle is NOT in the denominator).
        KeyedCounterNode numerator = new("hs", "hs");
        numerator.Add("ak47", 3);
        numerator.Add("deagle", 5);

        // Denominator (kills): ak47=6, knife=2, awp=0. Use Last so awp can hold a real 0.
        KeyedCounterNode denominator = new("kills", "kills", null, KeyedReduceMode.Last);
        denominator.Combine("ak47", 6);
        denominator.Combine("knife", 2);
        denominator.Combine("awp", 0);

        KeyedRatioNode rate = new("hs_rate", "hs_rate", numerator, denominator);
        IReadOnlyDictionary<string, double> ratios = rate.Buckets;

        // Output key set == denominator keys with a NON-ZERO value: {ak47, knife}. awp is skipped
        // (denominator 0 → undefined); deagle is excluded (numerator-only, not in the denominator).
        await Assert.That(ratios.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList())
            .IsEquivalentTo(new List<string>
            {
                "ak47",
                "knife"
            });
        await Assert.That(ratios.ContainsKey("awp")).IsFalse()
            .Because("a denominator key that is 0 is undefined and must be skipped (no row)");
        await Assert.That(ratios.ContainsKey("deagle")).IsFalse()
            .Because("a numerator-only key is not in the denominator population base");

        await Assert.That(ratios["ak47"]).IsEqualTo(0.5); // 3 / 6
        await Assert.That(ratios["knife"]).IsEqualTo(0.0); // numerator missing → 0 / 2 = 0.0 (a real row)
        await Assert.That(rate.IsActive).IsTrue();
    }

    [Test]
    public async Task KeyedRatioNode_EmptyDenominator_IsInactive()
    {
        KeyedCounterNode numerator = new("hs", "hs");
        numerator.Add("ak47", 3);
        KeyedCounterNode denominator = new("kills", "kills");

        KeyedRatioNode rate = new("hs_rate", "hs_rate", numerator, denominator);
        await Assert.That(rate.Buckets).IsEmpty();
        await Assert.That(rate.IsActive).IsFalse();
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    /// <summary>
    ///     Hashes every stat of <paramref name="rs" /> in dependency order (buckets precede rates in the
    ///     resolved stats list), accumulating each hash into the source so a rate's synthesized
    ///     <c>of / per</c> references resolve (row 6). Returns the target rate's hash hex.
    /// </summary>
    private static string RateHashHex(CheckedRuleset rs, string rateId)
    {
        Dictionary<string, ReadOnlyMemory<byte>> byPath = new(StringComparer.Ordinal);
        MapStatHashSource source = new(byPath);
        byte[]? target = null;
        foreach (CheckedStat stat in rs.Stats)
        {
            byte[] hash = V2StatHasher.Hash(stat, source);
            byPath[stat.StatId] = hash;
            byPath[$"{rs.Id.Id}.{stat.StatId}"] = hash;
            if (string.Equals(stat.StatId, rateId, StringComparison.Ordinal))
            {
                target = hash;
            }
        }

        return Convert.ToHexStringLower(target ?? throw new InvalidOperationException($"rate '{rateId}' not found"));
    }

    private static CheckedRuleset Compile(string yaml)
    {
        RulesetDoc doc = RulesetDocumentLoader.Load(yaml, "t.rules.yaml").Doc
                         ?? throw new InvalidOperationException("test ruleset failed to map");
        RulesetResolveResult resolved = CheckedRulesetDraft.Load(doc, _adapter).Build(64.0, "Cs2GotvProfile");
        return resolved.Ruleset
               ?? throw new InvalidOperationException(
                   "test ruleset failed to resolve: " + string.Join("; ", resolved.Diagnostics));
    }

    private static Dictionary<string, StateNode> Materialize(CheckedRuleset rs)
    {
        RuleChainBuilder builder = new(EventRegistry.Build());
        BuildResult build = builder.Build([rs]);

        Dictionary<string, StateNode> merged = new(StringComparer.OrdinalIgnoreCase);
        foreach (PerPlayerNodeTemplate template in build.Graph.PerPlayerTemplates)
        {
            PerPlayerNodeTemplate.MaterializedPlayer player = template.Materialize(0, 0, "test", null);
            if (player.NodesByRuleId is { } byId)
            {
                foreach ((string key, StateNode node) in byId)
                {
                    merged[key] = node;
                }
            }
        }

        return merged;
    }
}
