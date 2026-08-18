#region

using CS2DemoKit.Analysis.Catalog;
using CS2DemoKit.Analysis.RulesetsV2.Compile;
using CS2DemoKit.Analysis.RulesetsV2.Model;
using CS2DemoKit.Analysis.RulesetsV2.Resolve;
using CS2DemoKit.Analysis.Yaml;

#endregion

namespace CS2DemoKit.Analysis.Tests.RulesV2;

/// <summary>
///     The <c>bucket:</c> vertical demo-free battery: the model carries the
///     <c>key:</c> expression, structural validation enforces the bucket shape (source + key required,
///     key bucket-only, match-scoped), the resolver type-checks the key to a string, and the
///     resolved-identity hash distinguishes buckets that key on different expressions. Runtime parity
///     lives in <see cref="BucketKindGoldenTests" />.
/// </summary>
[Category("Unit")]
public class BucketKindModelTests
{
    private const string ValidBucket = """
                                       ruleset: t
                                       for: each_player
                                       stats:
                                         kills_by_weapon:
                                           bucket: kill
                                           key: event.Weapon
                                           per: match
                                       """;

    // ── Composite (list) keys (C8) ──────────────────────────────────────────────

    private const string CompositeBucket = """
                                           ruleset: t
                                           for: each_player
                                           stats:
                                             kills_by_weapon_hs:
                                               bucket: kill
                                               key: [event.Weapon, event.WeaponItemId]
                                               per: match
                                           """;

    private static readonly CatalogScopeAdapter _adapter = CatalogScopeAdapter.From(CatalogResource.Load());

    // ── Mapping ────────────────────────────────────────────────────────────────

    [Test]
    public async Task Maps_Bucket_KindArg_AndKey()
    {
        RulesetDocumentLoader.Outcome outcome = RulesetDocumentLoader.Load(ValidBucket, "t.rules.yaml");
        await Assert.That(outcome.Diagnostics).IsEmpty();

        StatDef bucket = outcome.Doc!.Stats.Single(s => s.Id == "kills_by_weapon");
        await Assert.That(bucket.Kind).IsEqualTo(StatKind.Bucket);
        await Assert.That(bucket.KindArg).IsEqualTo("kill");
        await Assert.That(bucket.BucketKey).IsEqualTo("event.Weapon");
    }

    [Test]
    public async Task Bucket_DefaultsToMatchScope()
    {
        // No per: on a bucket defaults to match (not the round default other kinds get).
        const string Yaml = """
                            ruleset: t
                            for: each_player
                            stats:
                              kills_by_weapon:
                                bucket: kill
                                key: event.Weapon
                            """;

        RulesetDocumentLoader.Outcome outcome = RulesetDocumentLoader.Load(Yaml, "t.rules.yaml");
        await Assert.That(outcome.Diagnostics).IsEmpty();
        await Assert.That(outcome.Doc!.Stats.Single().Per).IsEqualTo(PerScope.Match);
    }

    // ── Structural validation ──────────────────────────────────────────────────

    [Test]
    public async Task Bucket_WithoutKey_IsRejected()
    {
        const string Yaml = """
                            ruleset: t
                            for: each_player
                            stats:
                              b:
                                bucket: kill
                                per: match
                            """;

        RulesetDocumentLoader.Outcome outcome = RulesetDocumentLoader.Load(Yaml, "t.rules.yaml");
        await Assert.That(outcome.Diagnostics.Any(d => d.Code == RulesetDiagnosticCodes.BadKindArgs)).IsTrue();
    }

    [Test]
    public async Task Key_OnNonBucket_IsRejected()
    {
        const string Yaml = """
                            ruleset: t
                            for: each_player
                            stats:
                              c:
                                count: kill
                                per: round
                                key: event.Weapon
                            """;

        RulesetDocumentLoader.Outcome outcome = RulesetDocumentLoader.Load(Yaml, "t.rules.yaml");
        await Assert.That(outcome.Diagnostics.Any(d => d.Code == RulesetDiagnosticCodes.BadKindArgs)).IsTrue();
    }

    [Test]
    public async Task Bucket_PerRound_IsRejected()
    {
        const string Yaml = """
                            ruleset: t
                            for: each_player
                            stats:
                              b:
                                bucket: kill
                                key: event.Weapon
                                per: round
                            """;

        RulesetDocumentLoader.Outcome outcome = RulesetDocumentLoader.Load(Yaml, "t.rules.yaml");
        await Assert.That(outcome.Diagnostics.Any(d => d.Code == RulesetDiagnosticCodes.BadKindArgs)).IsTrue();
    }

    // ── Resolved-identity hash distinctness (row 8) ─────────────────────────────

    [Test]
    public async Task Buckets_KeyingOnDifferentExpressions_HashApart()
    {
        byte[] byWeapon = BucketHash(ValidBucket);
        byte[] byItemId = BucketHash(ValidBucket.Replace("key: event.Weapon", "key: event.WeaponItemId",
            StringComparison.Ordinal));

        await Assert.That(Convert.ToHexString(byItemId)).IsNotEqualTo(Convert.ToHexString(byWeapon));
        await Assert.That(Convert.ToHexString(BucketHash(ValidBucket))).IsEqualTo(Convert.ToHexString(byWeapon));
    }

    [Test]
    public async Task Maps_CompositeKey_AsList()
    {
        RulesetDocumentLoader.Outcome outcome = RulesetDocumentLoader.Load(CompositeBucket, "t.rules.yaml");
        await Assert.That(outcome.Diagnostics).IsEmpty();

        StatDef bucket = outcome.Doc!.Stats.Single(s => s.Id == "kills_by_weapon_hs");
        await Assert.That(bucket.BucketKey).IsNull();
        await Assert.That(bucket.BucketKeys).IsNotNull();
        await Assert.That(bucket.BucketKeys!).HasCount().EqualTo(2);
        await Assert.That(bucket.BucketKeys![0]).IsEqualTo("event.Weapon");
        await Assert.That(bucket.BucketKeys![1]).IsEqualTo("event.WeaponItemId");
    }

    [Test]
    public async Task Resolves_CompositeKey_ToOrderedParts()
    {
        RulesetDocumentLoader.Outcome outcome = RulesetDocumentLoader.Load(CompositeBucket, "t.rules.yaml");
        await Assert.That(outcome.Diagnostics).IsEmpty();
        CheckedRuleset ruleset = CheckedRulesetDraft.Load(outcome.Doc!, _adapter).Build(64.0, "Cs2GotvProfile").Ruleset
                                 ?? throw new InvalidOperationException("composite bucket failed to resolve");

        CheckedStat bucket = ruleset.Stats.Single(s => s.StatId == "kills_by_weapon_hs");
        await Assert.That(bucket.BucketKeyParts!).HasCount().EqualTo(2);
        await Assert.That(bucket.BucketKeyParts![0]).IsEqualTo("event.Weapon");
        await Assert.That(bucket.BucketKeyParts![1]).IsEqualTo("event.WeaponItemId");
    }

    [Test]
    public async Task CompositeKey_OrderIsIdentityBearing()
    {
        byte[] ab = BucketHash(CompositeBucket, "kills_by_weapon_hs");
        byte[] ba = BucketHash(
            CompositeBucket.Replace("[event.Weapon, event.WeaponItemId]", "[event.WeaponItemId, event.Weapon]",
                StringComparison.Ordinal),
            "kills_by_weapon_hs");

        await Assert.That(Convert.ToHexString(ba)).IsNotEqualTo(Convert.ToHexString(ab));
    }

    [Test]
    public async Task CompositeKey_EmptyPart_IsRejected()
    {
        const string Yaml = """
                            ruleset: t
                            for: each_player
                            stats:
                              b:
                                bucket: kill
                                key: [event.Weapon, ""]
                                per: match
                            """;

        RulesetDocumentLoader.Outcome outcome = RulesetDocumentLoader.Load(Yaml, "t.rules.yaml");
        await Assert.That(outcome.Diagnostics.Any(d => d.Code == RulesetDiagnosticCodes.BadKindArgs)).IsTrue();
    }

    // ── Named reducers (C8) ─────────────────────────────────────────────────────

    private static string ReduceBucket(string reduceLine, bool withValue) => $"""
                                                                              ruleset: t
                                                                              for: each_player
                                                                              stats:
                                                                                b:
                                                                                  bucket: damage_dealt
                                                                                  key: event.Weapon
                                                                              {(withValue ? "    value: event.DmgHealth" : "")}
                                                                              {reduceLine}
                                                                                  per: match
                                                                              """;

    [Test]
    public async Task Reduce_Max_Resolves_WithValue()
    {
        RulesetDocumentLoader.Outcome outcome =
            RulesetDocumentLoader.Load(ReduceBucket("    reduce: max", true), "t.rules.yaml");
        await Assert.That(outcome.Diagnostics).IsEmpty();
        CheckedRuleset ruleset = CheckedRulesetDraft.Load(outcome.Doc!, _adapter).Build(64.0, "Cs2GotvProfile").Ruleset
                                 ?? throw new InvalidOperationException("reduce:max bucket failed to resolve");
        await Assert.That(ruleset.Stats.Single(s => s.StatId == "b").BucketReducer).IsEqualTo("max");
    }

    [Test]
    public async Task Reduce_Min_WithoutValue_IsRejected()
    {
        RulesetDocumentLoader.Outcome outcome =
            RulesetDocumentLoader.Load(ReduceBucket("    reduce: min", false), "t.rules.yaml");
        // Structurally valid (reduce name is known); the resolver rejects the missing value:.
        RulesetResolveResult resolved = CheckedRulesetDraft.Load(outcome.Doc!, _adapter).Build(64.0, "Cs2GotvProfile");
        await Assert.That(resolved.Diagnostics.Any(d => d.Code == ResolveDiagnosticCodes.BadSlotType)).IsTrue();
    }

    [Test]
    public async Task Reduce_Count_WithValue_IsRejected()
    {
        RulesetDocumentLoader.Outcome outcome =
            RulesetDocumentLoader.Load(ReduceBucket("    reduce: count", true), "t.rules.yaml");
        RulesetResolveResult resolved = CheckedRulesetDraft.Load(outcome.Doc!, _adapter).Build(64.0, "Cs2GotvProfile");
        await Assert.That(resolved.Diagnostics.Any(d => d.Code == ResolveDiagnosticCodes.BadSlotType)).IsTrue();
    }

    [Test]
    public async Task Reduce_UnknownName_IsRejected()
    {
        RulesetDocumentLoader.Outcome outcome =
            RulesetDocumentLoader.Load(ReduceBucket("    reduce: median", true), "t.rules.yaml");
        await Assert.That(outcome.Diagnostics.Any(d => d.Code == RulesetDiagnosticCodes.BadKindArgs)).IsTrue();
    }

    [Test]
    public async Task Reduce_OnNonBucket_IsRejected()
    {
        const string Yaml = """
                            ruleset: t
                            for: each_player
                            stats:
                              c:
                                count: kill
                                per: round
                                reduce: max
                            """;

        RulesetDocumentLoader.Outcome outcome = RulesetDocumentLoader.Load(Yaml, "t.rules.yaml");
        await Assert.That(outcome.Diagnostics.Any(d => d.Code == RulesetDiagnosticCodes.BadKindArgs)).IsTrue();
    }

    private static byte[] BucketHash(string yaml) => BucketHash(yaml, "kills_by_weapon");

    private static byte[] BucketHash(string yaml, string statId)
    {
        RulesetDocumentLoader.Outcome outcome = RulesetDocumentLoader.Load(yaml, "t.rules.yaml");
        CheckedRuleset ruleset = CheckedRulesetDraft.Load(outcome.Doc!, _adapter).Build(64.0, "Cs2GotvProfile").Ruleset
                                 ?? throw new InvalidOperationException("bucket ruleset failed to resolve");
        MapStatHashSource source = new(new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.Ordinal));
        return V2StatHasher.Hash(ruleset.Stats.Single(s => s.StatId == statId), source);
    }
}
