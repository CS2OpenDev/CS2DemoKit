#region

using CS2DemoKit.Analysis.Catalog;
using CS2DemoKit.Analysis.Plugins;
using CS2DemoKit.Analysis.RulesetsV2.Compile;
using CS2DemoKit.Analysis.RulesetsV2.Model;
using CS2DemoKit.Analysis.RulesetsV2.Resolve;

#endregion

namespace CS2DemoKit.Analysis.Tests.RulesV2;

/// <summary>
///     <see cref="CatalogExtension" />: a host's own providers become nameable by rulesets for the builds,
///     validations and fingerprints it is handed to, and nowhere else.
/// </summary>
[Category("Unit")]
public class CatalogExtensionTests
{
    internal const string HelmetName = "entity.ext.has_helmet";
    internal const string HelmetPath = "player.ext.has_helmet";
    internal const string TimeoutsName = "entity.ext.ct_timeouts_left";
    internal const string TimeoutsPath = "match.ext.ct_timeouts_left";

    internal static readonly IPerPlayerEntityValueProvider Helmet = new GenericPerPlayerFieldProvider(
        new ProviderSpec(HelmetName, "CCSPlayerPawn", "m_pItemServices.m_bHasHelmet", typeof(bool)));

    internal static readonly IEntityValueProvider Timeouts = new GenericSingletonFieldProvider(
        new ProviderSpec(TimeoutsName, "CCSGameRulesProxy", "m_pGameRules.m_nCTTimeOuts", typeof(int)),
        ChangeDirection.RisingOnly, typeof(TimeoutsMarker), 0);

    internal static CatalogExtension Extension { get; } = CatalogExtension.Create(
    [
        CatalogExtension.PerPlayer(Helmet, HelmetPath, note: "test helmet"),
        CatalogExtension.Singleton(Timeouts, TimeoutsPath)
    ]);

    private const string Reader = """
        ruleset: ext_reader
        for: each_player
        stats:
          helmet_kills:
            count: kill
            where: "victim.ext.has_helmet"
            per: match
          had_helmet:
            capture: player.ext.has_helmet
            on: round_ended
            per: round
          ct_timeouts:
            capture: match.ext.ct_timeouts_left
            on: round_ended
            per: round
        """;

    private static RulesetDoc Doc(string yaml) => RulesetCompositionDiagnosticsTests.Doc("ext.rules.yaml", yaml);

    [Test]
    public async Task ExtensionRows_ResolveFlatNestedAndRoleReads()
    {
        RulesetValidationResult validated = DemoAnalysis.ValidateRulesets([Doc(Reader)], Extension);

        await Assert.That(validated.Success).IsTrue()
            .Because(string.Join("; ", validated.Diagnostics));
        await Assert.That(validated.ValidatedRulesetIds).Contains("ext_reader");
    }

    [Test]
    public async Task WithoutTheExtension_TheSamePathsAreUnknown_AndTheEmbeddedCatalogIsUntouched()
    {
        _ = DemoAnalysis.ValidateRulesets([Doc(Reader)], Extension);
        RulesetValidationResult validated = DemoAnalysis.ValidateRulesets([Doc(Reader)]);

        await Assert.That(validated.Success).IsFalse().Because("no extension, no row");
        await Assert.That(CatalogResource.Load().Providers.Any(p => p.Name == HelmetName)).IsFalse()
            .Because("an extension merges into a copy, never into the cached catalog");
        await Assert.That(CatalogResource.Load(Extension).Providers.Count)
            .IsEqualTo(CatalogResource.Load().Providers.Count + 2);
        await Assert.That(ReferenceEquals(CatalogResource.Load(Extension), CatalogResource.Load(Extension))).IsTrue()
            .Because("the merge is built once per extension");
    }

    [Test]
    public async Task MergedRows_KeepTheGeneratorsOrder()
    {
        IReadOnlyList<CatalogProvider> merged = CatalogResource.Load(Extension).Providers;
        await Assert.That(merged.Select(p => p.Name).SequenceEqual(merged.Select(p => p.Name).Order(StringComparer.Ordinal)))
            .IsTrue();
    }

    [Test]
    public async Task RowHelpers_TakeScopeAndTypesFromTheProvider()
    {
        CatalogProvider row = Extension.Providers[0];
        await Assert.That(row).IsEqualTo(new CatalogProvider(HelmetName, "perPlayer", "bool", HelmetPath, "Bool",
            Note: "test helmet"));
        await Assert.That(Extension.Providers[1])
            .IsEqualTo(new CatalogProvider(TimeoutsName, "singleton", "int", TimeoutsPath, "Int"));
    }

    [Test]
    public async Task Fingerprint_ComposesAgainstTheExtension()
    {
        HighlightConfigFingerprint.Result result =
            HighlightConfigFingerprint.Compute([Doc(Reader)], 64.0, "Cs2GotvProfile", Extension);
        await Assert.That(result).IsNotNull();
        Assert.Throws<InvalidOperationException>(() =>
            HighlightConfigFingerprint.Compute([Doc(Reader)], 64.0, "Cs2GotvProfile"));
    }

    private const string Highlighted = """
        ruleset: ext_hl
        for: each_player
        stats:
          helmet_kills:
            count: kill
            where: "victim.ext.has_helmet"
            per: match
          kills:
            count: kill
            per: match
          timeouts_seen:
            sum: match.ext.ct_timeouts_left
            on: round_ended
            per: match
        highlights:
          helmet_hunter:
            when: helmet_kills >= 2
            per: match
            title: "helmet hunter"
          multi:
            when: kills >= 2
            per: match
            title: "multi"
          patient:
            when: timeouts_seen >= 10
            per: match
            title: "patient"
        """;

    [Test]
    public async Task Fingerprint_RemappingARowsProvider_ChangesOnlyTheHighlightsThatReadIt()
    {
        IPerPlayerEntityValueProvider other = new GenericPerPlayerFieldProvider(
            new ProviderSpec("entity.ext.has_defuser", "CCSPlayerPawn", "m_pItemServices.m_bHasDefuser", typeof(bool)));
        CatalogExtension remapped = CatalogExtension.Create(
        [
            CatalogExtension.PerPlayer(other, HelmetPath, note: "test helmet"),
            CatalogExtension.Singleton(Timeouts, TimeoutsPath)
        ]);

        HighlightConfigFingerprint.Result before =
            HighlightConfigFingerprint.Compute([Doc(Highlighted)], 64.0, "Cs2GotvProfile", Extension);
        HighlightConfigFingerprint.Result after =
            HighlightConfigFingerprint.Compute([Doc(Highlighted)], 64.0, "Cs2GotvProfile", remapped);

        await Assert.That(after.HighlightHashes["ext_hl.helmet_hunter"])
            .IsNotEqualTo(before.HighlightHashes["ext_hl.helmet_hunter"])
            .Because("its stat's rules path now reads another provider, so its cached results are stale");
        await Assert.That(after.HighlightHashes["ext_hl.multi"]).IsEqualTo(before.HighlightHashes["ext_hl.multi"])
            .Because("it reads no extension row, so its cache survives");
        await Assert.That(after.Fingerprint).IsNotEqualTo(before.Fingerprint);
    }

    [Test]
    public async Task Fingerprint_RemappingASingletonReadInAValue_ChangesItsHighlight()
    {
        IEntityValueProvider other = new GenericSingletonFieldProvider(
            new ProviderSpec("entity.ext.t_timeouts_left", "CCSGameRulesProxy", "m_pGameRules.m_nTerroristTimeOuts",
                typeof(int)),
            ChangeDirection.Both, typeof(TimeoutsMarker), 0);
        CatalogExtension remapped = CatalogExtension.Create(
        [
            CatalogExtension.PerPlayer(Helmet, HelmetPath, note: "test helmet"),
            CatalogExtension.Singleton(other, TimeoutsPath)
        ]);

        HighlightConfigFingerprint.Result before =
            HighlightConfigFingerprint.Compute([Doc(Highlighted)], 64.0, "Cs2GotvProfile", Extension);
        HighlightConfigFingerprint.Result after =
            HighlightConfigFingerprint.Compute([Doc(Highlighted)], 64.0, "Cs2GotvProfile", remapped);

        await Assert.That(after.HighlightHashes["ext_hl.patient"])
            .IsNotEqualTo(before.HighlightHashes["ext_hl.patient"])
            .Because("a sum: value read of a singleton row is an entity read too");
        await Assert.That(after.HighlightHashes["ext_hl.helmet_hunter"])
            .IsEqualTo(before.HighlightHashes["ext_hl.helmet_hunter"]);
    }

    [Test]
    public async Task Fingerprint_AddingOrRepathingAnUnreadRow_KeepsEveryHighlightHash()
    {
        IEntityValueProvider unread = new GenericSingletonFieldProvider(
            new ProviderSpec("entity.ext.t_timeouts_left", "CCSGameRulesProxy", "m_pGameRules.m_nTerroristTimeOuts",
                typeof(int)),
            ChangeDirection.Both, typeof(TimeoutsMarker), 0);
        CatalogExtension With(string unreadPath) => CatalogExtension.Create(
        [
            CatalogExtension.PerPlayer(Helmet, HelmetPath, note: "test helmet"),
            CatalogExtension.Singleton(Timeouts, TimeoutsPath),
            CatalogExtension.Singleton(unread, unreadPath)
        ]);

        HighlightConfigFingerprint.Result baseline =
            HighlightConfigFingerprint.Compute([Doc(Highlighted)], 64.0, "Cs2GotvProfile", Extension);
        HighlightConfigFingerprint.Result added =
            HighlightConfigFingerprint.Compute([Doc(Highlighted)], 64.0, "Cs2GotvProfile", With("match.ext.t_left"));
        HighlightConfigFingerprint.Result repathed =
            HighlightConfigFingerprint.Compute([Doc(Highlighted)], 64.0, "Cs2GotvProfile", With("match.ext.t_moved"));

        await Assert.That(added.HighlightHashes).IsEquivalentTo(baseline.HighlightHashes);
        await Assert.That(repathed.HighlightHashes).IsEquivalentTo(baseline.HighlightHashes);
        await Assert.That(repathed.Fingerprint).IsEqualTo(baseline.Fingerprint);
    }

    [Test]
    public async Task Fingerprint_WithAnEmptyExtension_MatchesNoExtension()
    {
        const string plain = """
            ruleset: plain
            for: each_player
            stats:
              kills:
                count: kill
                per: match
            """;

        string none = HighlightConfigFingerprint.Compute([Doc(plain)], 64.0, "Cs2GotvProfile").Fingerprint;
        string empty = HighlightConfigFingerprint.Compute([Doc(plain)], 64.0, "Cs2GotvProfile",
            CatalogExtension.Create([])).Fingerprint;

        await Assert.That(empty).IsEqualTo(none).Because("a host with no rows keeps its cached fingerprints");
    }

    [Test]
    [Arguments("pawn.has_helmet", "perPlayer", "bool", "player.x.y", "must start with 'entity.'")]
    [Arguments("entity.pawn.health", "perPlayer", "int", "player.x.health", "already in the catalog")]
    [Arguments("entity.ext.health", "perPlayer", "int", "player.health", "already in the catalog")]
    [Arguments("entity.ext.slot", "perPlayer", "int", "player.slot", "already in the catalog")]
    [Arguments("entity.ext.cmd", "perPlayer", "int", "player.cmd", "is a namespace of")]
    [Arguments("entity.ext.under", "perPlayer", "int", "player.health.under", "is a value")]
    [Arguments("entity.ext.c", "perPlayer", "int", "player.cmd.c", "reserved")]
    [Arguments("entity.ext.m", "perPlayer", "int", "match.m", "must sit under 'player.'")]
    [Arguments("entity.ext.s", "singleton", "int", "player.s", "must sit under 'match.'")]
    [Arguments("entity.ext.t", "perTeam", "int", "team.t", "cannot be extended")]
    [Arguments("entity.ext.u", "perPlayerCommand", "int", "player.u", "cannot be extended")]
    [Arguments("entity.ext.v", "perPlayer", "vector", "player.v", "has no rules type")]
    [Arguments("entity.ext.w", "perPlayer", "int", "player.1w", "not a dotted path")]
    [Arguments("entity.ext.d", "perPlayer", "double", "player.d", "cannot be stored per player")]
    [Arguments("entity.ext.l", "perPlayer", "long", "player.l", "cannot be stored per player")]
    public async Task Create_RefusesARowTheResolverCouldNotServe(string name, string scope, string type, string path,
        string why)
    {
        ArgumentException? refused = null;
        try
        {
            CatalogExtension.Create([new CatalogProvider(name, scope, type, path)]);
        }
        catch (ArgumentException ex)
        {
            refused = ex;
        }

        await Assert.That(refused).IsNotNull();
        await Assert.That(refused!.Message).Contains(why);
    }

    [Test]
    public void Create_RefusesTwoRowsUnderOneNameOrPath()
    {
        Assert.Throws<ArgumentException>(() => CatalogExtension.Create(
        [
            new CatalogProvider("entity.ext.a", "perPlayer", "int", "player.ext.a"),
            new CatalogProvider("entity.ext.b", "perPlayer", "int", "player.ext.a")
        ]));
        Assert.Throws<ArgumentException>(() => CatalogExtension.Create(
        [
            new CatalogProvider("entity.ext.a", "perPlayer", "int", "player.ext.a"),
            new CatalogProvider("entity.ext.a", "singleton", "int", "match.ext.a")
        ]));
        Assert.Throws<ArgumentException>(() => CatalogExtension.Create(
        [
            new CatalogProvider("entity.ext.a", "perPlayer", "int", "player.ext"),
            new CatalogProvider("entity.ext.b", "perPlayer", "int", "player.ext.b")
        ]));
    }

    [Test]
    public async Task AnExtensionPerPlayerRow_IsARoleMemberAtItsRelativePath()
    {
        CatalogScopeAdapter adapter = CatalogScopeAdapter.FromEmbeddedCatalog(Extension);
        await Assert.That(adapter.RoleNamespace("victim").TryGetMember("ext", out var ext)).IsTrue();
        await Assert.That(ext!.TryGetMember("has_helmet", out _)).IsTrue();
        await Assert.That(adapter.RoleNamespace("victim").TryGetMember("health", out _)).IsTrue()
            .Because("the built-in one-segment members are where they were");
    }

    private sealed class TimeoutsMarker;
}
