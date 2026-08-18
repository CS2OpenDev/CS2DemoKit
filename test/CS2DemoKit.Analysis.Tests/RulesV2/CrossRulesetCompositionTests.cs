#region

using CS2DemoKit.Analysis.Catalog;
using CS2DemoKit.Analysis.Rules.Checking;
using CS2DemoKit.Analysis.RulesetsV2.Compile;
using CS2DemoKit.Analysis.RulesetsV2.Model;
using CS2DemoKit.Analysis.RulesetsV2.Resolve;
using CS2DemoKit.Analysis.Yaml;

#endregion

namespace CS2DemoKit.Analysis.Tests.RulesV2;

/// <summary>
///     The cross-ruleset composition battery: the export graph resolves a
///     qualified <c>ruleset.stat</c> read, the validator attributes the four failure modes plus the
///     read-scope rule, the structural cycle detector names a cross-ruleset cycle, and the identity
///     mechanism is the sibling-reference one — a stat reading <c>a.x</c> hashes over <c>x</c>'s own
///     resolved hash (name-free), so two rulesets exporting a structurally-identical stat make their
///     readers dedup. Demo-free.
/// </summary>
[Category("Unit")]
public class CrossRulesetCompositionTests
{
    private static readonly CatalogScopeAdapter _adapter = CatalogScopeAdapter.From(CatalogResource.Load());

    private static RulesetDoc Doc(string id, string yaml)
    {
        RulesetDocumentLoader.Outcome outcome = RulesetDocumentLoader.Load(yaml, $"{id}.rules.yaml");
        return outcome.Doc ?? throw new InvalidOperationException(
            $"'{id}' failed to map: {string.Join("; ", outcome.Diagnostics)}");
    }

    private static RulesetComposition.Result Compose(params RulesetDoc[] docs) =>
        RulesetComposition.ComposeDraft(docs, _adapter);

    // A ruleset exporting one int stat `x` (count: kill), plus optional extras appended verbatim.
    private static RulesetDoc Provider(string id, string exportsLine = "exports: [x]", string extraStats = "") =>
        Doc(id, $"""
                 ruleset: {id}
                 for: each_player
                 {exportsLine}
                 stats:
                   x:
                     count: kill
                     per: match
                 {extraStats}
                 """);

    private static RulesetDoc Reader(string id, string useLine, string formula) =>
        Doc(id, $"""
                 ruleset: {id}
                 for: each_player
                 {useLine}
                 stats:
                   r:
                     compute: "{formula}"
                 """);

    // ── Happy path ─────────────────────────────────────────────────────────────

    [Test]
    public async Task QualifiedRead_Resolves_WhenUsedAndExported()
    {
        RulesetComposition.Result composed = Compose(Provider("a"), Reader("b", "use: [a]", "a.x + 1"));

        await Assert.That(composed.Success).IsTrue()
            .Because("a legal used+exported qualified read must compose cleanly: "
                     + string.Join("; ", composed.Diagnostics));

        CheckedStat reader = composed.Rulesets.Single(rs => rs.Id.Id == "b").Stats.Single(s => s.StatId == "r");
        ResolvedReference crossRef = reader.TriggerCondition!.References.Single(r => r.IsStatReference);
        await Assert.That(crossRef.StatPath).IsEqualTo("a.x")
            .Because("the qualified read resolves through the SAME stat-reference mechanism as a sibling — "
                     + "StatPath keys the hasher and the planner node map");
    }

    // ── The four attributed errors ─────────────────────────────────────────────

    [Test]
    public async Task NotInUse_IsAttributed()
    {
        RulesetComposition.Result composed = Compose(Provider("a"), Reader("b", "use: []", "a.x + 1"));
        await Assert.That(composed.Diagnostics.Any(d => d.Code == ResolveDiagnosticCodes.CrossRefNotInUse)).IsTrue()
            .Because("reading a.x without a in use: is the not-in-use error");
    }

    [Test]
    public async Task UnknownRuleset_IsAttributed()
    {
        RulesetComposition.Result composed = Compose(Reader("b", "use: [ghost]", "ghost.x + 1"));
        await Assert.That(composed.Diagnostics.Any(d => d.Code == ResolveDiagnosticCodes.CrossRefUnknownRuleset)).IsTrue()
            .Because("use: names a ruleset no document declares");
    }

    [Test]
    public async Task UnknownStat_IsAttributed()
    {
        RulesetComposition.Result composed = Compose(Provider("a"), Reader("b", "use: [a]", "a.nope + 1"));
        await Assert.That(composed.Diagnostics.Any(d => d.Code == ResolveDiagnosticCodes.CrossRefUnknownStat)).IsTrue()
            .Because("a declares no stat 'nope'");
    }

    [Test]
    public async Task NotExported_IsAttributed()
    {
        // a declares x and y but exports only x; reading a.y is not-exported (distinct from unknown-stat).
        RulesetDoc a = Provider("a", "exports: [x]", "  y:\n    count: death\n    per: match");
        RulesetComposition.Result composed = Compose(a, Reader("b", "use: [a]", "a.y + 1"));
        await Assert.That(composed.Diagnostics.Any(d => d.Code == ResolveDiagnosticCodes.CrossRefNotExported)).IsTrue()
            .Because("a declares y but does not export it");
        await Assert.That(composed.Diagnostics.Any(d => d.Code == ResolveDiagnosticCodes.CrossRefUnknownStat)).IsFalse()
            .Because("a declared y — not-exported must not be mis-attributed as unknown-stat");
    }

    // ── Read-scope rule ────────────────────────────────────────────────────────

    [Test]
    public async Task MatchReadingPerPlayer_IsReadScopeError()
    {
        RulesetDoc perPlayer = Provider("pp"); // for: each_player, exports x
        RulesetDoc match = Doc("m", """
                                    ruleset: m
                                    for: match
                                    use: [pp]
                                    stats:
                                      r:
                                        compute: "pp.x + 1"
                                    """);
        RulesetComposition.Result composed = Compose(perPlayer, match);
        await Assert.That(composed.Diagnostics.Any(d => d.Code == ResolveDiagnosticCodes.CrossRefReadScope)).IsTrue()
            .Because("a match-scoped ruleset has no player binding to read a per-player stat");
    }

    [Test]
    public async Task PerPlayerReadingMatch_IsLegal()
    {
        RulesetDoc match = Doc("mm", """
                                     ruleset: mm
                                     for: match
                                     exports: [rounds]
                                     stats:
                                       rounds:
                                         count: kill
                                         per: match
                                     """);
        RulesetDoc perPlayer = Reader("pp2", "use: [mm]", "mm.rounds + 1");
        RulesetComposition.Result composed = Compose(match, perPlayer);
        await Assert.That(composed.Success).IsTrue()
            .Because("per-player -> match reads are legal: " + string.Join("; ", composed.Diagnostics));
    }

    // ── Cross-ruleset cycle ────────────────────────────────────────────────────

    [Test]
    public async Task CrossRulesetCycle_IsNamed()
    {
        // a.qa reads b.qb; b.qb reads a.qa — a mutual cross-ruleset read (flags type kind-first, but the
        // read structure still forms a cycle the structural detector catches).
        RulesetDoc a = Doc("ca", """
                                 ruleset: ca
                                 for: each_player
                                 use: [cb]
                                 exports: [qa]
                                 stats:
                                   qa:
                                     flag:
                                       when: "cb.qb"
                                     per: round
                                 """);
        RulesetDoc b = Doc("cb", """
                                 ruleset: cb
                                 for: each_player
                                 use: [ca]
                                 exports: [qb]
                                 stats:
                                   qb:
                                     flag:
                                       when: "ca.qa"
                                     per: round
                                 """);
        RulesetComposition.Result composed = Compose(a, b);
        await Assert.That(composed.Diagnostics.Any(d => d.Code == ResolveDiagnosticCodes.CrossRefCycle)).IsTrue()
            .Because("qa -> cb.qb -> ca.qa is a cross-ruleset cycle");
    }

    // ── Identity: a cross-ref hashes over the referenced node's own hash ───────

    [Test]
    public async Task CrossRef_HashesOverReferencedNodeHash()
    {
        RulesetComposition.Result composed = Compose(Provider("a"), Reader("b", "use: [a]", "a.x + 1"));
        CheckedStat reader = composed.Rulesets.Single(rs => rs.Id.Id == "b").Stats.Single(s => s.StatId == "r");

        byte[] h1 = V2StatHasher.Hash(reader, Source("a.x", Bytes(0xAA)));
        byte[] h2 = V2StatHasher.Hash(reader, Source("a.x", Bytes(0xBB)));

        await Assert.That(h1.SequenceEqual(h2)).IsFalse()
            .Because("the reader's hash must depend on a.x's resolved hash — change the referent, change the reader");
    }

    [Test]
    public async Task StructurallyIdenticalExports_MakeReadersDedup()
    {
        // a.x and b.x are structurally identical (count: kill per: match); readers of each must hash
        // equal (name-free identity), and hash apart from a reader of a structurally-different export.
        RulesetDoc a = Provider("a");
        RulesetDoc b = Provider("b");
        RulesetDoc cDifferent = Doc("c", """
                                         ruleset: c
                                         for: each_player
                                         exports: [x]
                                         stats:
                                           x:
                                             count: death
                                             per: match
                                         """);
        RulesetDoc r1 = Reader("r1", "use: [a]", "a.x + 1");
        RulesetDoc r2 = Reader("r2", "use: [b]", "b.x + 1");
        RulesetDoc r3 = Reader("r3", "use: [c]", "c.x + 1");
        RulesetComposition.Result composed = Compose(a, b, cDifferent, r1, r2, r3);
        await Assert.That(composed.Success).IsTrue()
            .Because("all readers resolve: " + string.Join("; ", composed.Diagnostics));

        // Hash each provider's x with an empty source (count: has no stat refs), then hash the readers
        // with a source that maps each qualified path to its referent's real hash.
        byte[] axHash = V2StatHasher.Hash(StatOf(composed, "a", "x"), Source());
        byte[] bxHash = V2StatHasher.Hash(StatOf(composed, "b", "x"), Source());
        byte[] cxHash = V2StatHasher.Hash(StatOf(composed, "c", "x"), Source());
        await Assert.That(axHash.SequenceEqual(bxHash)).IsTrue()
            .Because("count: kill per: match is structurally identical across a and b");

        MapStatHashSource source = new(new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.Ordinal)
        {
            ["a.x"] = axHash,
            ["b.x"] = bxHash,
            ["c.x"] = cxHash
        });
        byte[] r1Hash = V2StatHasher.Hash(StatOf(composed, "r1", "r"), source);
        byte[] r2Hash = V2StatHasher.Hash(StatOf(composed, "r2", "r"), source);
        byte[] r3Hash = V2StatHasher.Hash(StatOf(composed, "r3", "r"), source);

        await Assert.That(r1Hash.SequenceEqual(r2Hash)).IsTrue()
            .Because("readers of structurally-identical exports hash equal (name-free) — they dedup");
        await Assert.That(r1Hash.SequenceEqual(r3Hash)).IsFalse()
            .Because("a reader of a structurally-different export (count: death) must hash apart");
    }

    private static CheckedStat StatOf(RulesetComposition.Result composed, string ruleset, string stat) =>
        composed.Rulesets.Single(rs => rs.Id.Id == ruleset).Stats.Single(s => s.StatId == stat);

    private static byte[] Bytes(byte fill) => Enumerable.Repeat(fill, 32).ToArray();

    private static MapStatHashSource Source(string? path = null, byte[]? hash = null)
    {
        Dictionary<string, ReadOnlyMemory<byte>> map = new(StringComparer.Ordinal);
        if (path is not null && hash is not null)
        {
            map[path] = hash;
        }

        return new MapStatHashSource(map);
    }
}
