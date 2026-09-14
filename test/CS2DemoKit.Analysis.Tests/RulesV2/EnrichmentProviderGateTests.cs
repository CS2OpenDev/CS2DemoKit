#region

using CS2DemoKit.Analysis.Building;
using CS2DemoKit.Analysis.Catalog;
using CS2DemoKit.Analysis.Graphs;
using CS2DemoKit.Analysis.Plugins;
using CS2DemoKit.Analysis.Registry;
using CS2DemoKit.Analysis.RulesetsV2.Model;
using CS2DemoKit.Analysis.RulesetsV2.Resolve;
using CS2DemoKit.Analysis.Yaml;
using CS2DemoKit.Parser;

#endregion

namespace CS2DemoKit.Analysis.Tests.RulesV2;

/// <summary>
///     Per-player providers are gated in by name, and a read that names one directly
///     (<c>player.health</c>) reaches the gate through <c>UnionV2EntityReads</c>: the catalog maps the
///     v2 spelling to the provider, so the union sees it. An enrichment read does not. The hurt
///     enrichments carry no catalog <c>v2Name</c>, so they resolve to no provider at all — yet
///     <c>HurtTeamEnrichmentEdge</c> computes <c>enrich.hurt.capped_damage</c> from the health column
///     on the rule's behalf.
///     <para>
///         That indirect need is what the provider-gate switch in <c>RuleChainBuilder.Build</c> exists
///         for, and it is the only thing holding the health column in for a ruleset like
///         <c>player_stats</c>'s <c>TotalEnemyDmg</c>. Drop the arm and the build still succeeds, the
///         edge still runs, and capped damage quietly falls back to the event-cache HP instead of the
///         entity snapshot — a number, not an error. These tests pin the three tiers apart so that
///         swap cannot happen unobserved.
///     </para>
/// </summary>
[Category("Unit")]
public class EnrichmentProviderGateTests
{
    private const string HealthProvider = "entity.pawn.health";

    private static readonly CatalogScopeAdapter _adapter = CatalogScopeAdapter.From(CatalogResource.Load());

    /// <summary>The shape of player_stats' TotalEnemyDmg: the health column is needed, never named.</summary>
    private const string EnrichmentOnly = """
                                          ruleset: enrich_only
                                          for: each_player
                                          stats:
                                            dmg:
                                              sum: enrich.hurt.capped_damage
                                              on: damage_dealt
                                              match: { enemy: true }
                                              per: match
                                          """;

    /// <summary>A direct entity read — the tier UnionV2EntityReads covers.</summary>
    private const string DirectEntityRead = """
                                            ruleset: direct_read
                                            for: each_player
                                            stats:
                                              lowhp:
                                                count: kill
                                                where: "player.health < 30"
                                                per: match
                                            """;

    /// <summary>Neither tier: no entity read, no enrichment that needs one.</summary>
    private const string NoEntityNeed = """
                                        ruleset: no_need
                                        for: each_player
                                        stats:
                                          kills:
                                            count: kill
                                            per: match
                                        """;

    [Test]
    public async Task EnrichmentOnlyRead_ForcesTheHealthProviderIntoTheScanner()
    {
        (BuildResult build, PerPlayerEntityValueProviderRegistry providers) = Build(EnrichmentOnly);

        await Assert.That(IsSnapshotted(build, providers)).IsTrue()
            .Because("enrich.hurt.capped_damage is computed from the health column, so gating the "
                     + "provider out would silently fall the enrichment back to the event-cache HP");
    }

    [Test]
    public async Task EnrichmentOnlyRead_IsInvisibleToTheEntityReadUnion()
    {
        // Why the switch arm cannot be replaced by UnionV2EntityReads: the enrichment is a declared
        // read but not an entity read, because the catalog gives it no v2Name to resolve through.
        CheckedStat stat = Resolve(EnrichmentOnly).Stats.Single(s => s.StatId == "dmg");

        await Assert.That(stat.DeclaredReads).Contains("enrich.hurt.capped_damage");
        await Assert.That(stat.EntityReads).IsEmpty()
            .Because("an enrichment resolves to no provider, so the union has nothing to fold in");
    }

    [Test]
    public async Task DirectEntityRead_ArrivesThroughTheUnionInV2Spelling()
    {
        // The other tier, for contrast: this read DOES resolve to the provider — under its v2 name.
        // `entity.pawn.health` is not a spelling any ruleset can write (there is no `entity` root),
        // which is why a gate arm testing DeclaredReads for the provider name can never match.
        CheckedStat stat = Resolve(DirectEntityRead).Stats.Single(s => s.StatId == "lowhp");

        await Assert.That(stat.DeclaredReads).Contains("player.health");
        await Assert.That(stat.DeclaredReads).DoesNotContain(HealthProvider);
        await Assert.That(stat.EntityReads.Single().ProviderName).IsEqualTo(HealthProvider);

        (BuildResult build, PerPlayerEntityValueProviderRegistry providers) = Build(DirectEntityRead);
        await Assert.That(IsSnapshotted(build, providers)).IsTrue();
    }

    [Test]
    public async Task RulesetNeedingNoHealth_LeavesTheColumnOut()
    {
        // The gate has to stay a gate: every held column costs the digest's delta encoding on every
        // frame, so a ruleset that needs no HP must not pay for one.
        (BuildResult build, PerPlayerEntityValueProviderRegistry providers) = Build(NoEntityNeed);

        await Assert.That(IsSnapshotted(build, providers)).IsFalse()
            .Because("nothing reads the health column on this build, directly or through an enrichment");
    }

    // ── Harness ────────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     Whether the built scanner snapshots the health provider. Asked through
    ///     <c>GetPreFrameValue</c>, whose loud arm throws for a provider the scanner does not hold —
    ///     the same seam a compile site would hit, so this measures the gate and not a mirror of it.
    /// </summary>
    private static bool IsSnapshotted(BuildResult build, PerPlayerEntityValueProviderRegistry providers)
    {
        IPerPlayerEntityValueProvider provider = providers.Get(HealthProvider)
                                                 ?? throw new InvalidOperationException(
                                                     "the default registry no longer carries " + HealthProvider);
        EntityChangeScanner scanner = build.EntityScanner
                                      ?? throw new InvalidOperationException(
                                          "no scanner was built — the builtin freeze-period context always forces one");

        try
        {
            // No frames, so every cell reads null; the question is only whether the read is legal.
            scanner.GetPreFrameValue(provider, 0);
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static CheckedRuleset Resolve(string yaml)
    {
        RulesetDoc doc = RulesetDocumentLoader.Load(yaml, "gate.rules.yaml").Doc
                         ?? throw new InvalidOperationException("ruleset failed to map");
        RulesetResolveResult resolved = CheckedRulesetDraft.Load(doc, _adapter).Build(64.0, "Cs2GotvProfile");
        return resolved.Ruleset
               ?? throw new InvalidOperationException(
                   "resolve failed: " + string.Join("; ", resolved.Diagnostics.Select(d => $"{d.Code}: {d.Message}")));
    }

    private static (BuildResult Build, PerPlayerEntityValueProviderRegistry Providers) Build(string yaml)
    {
        // The registry instance matters: GetPreFrameValue is keyed by provider REFERENCE, so the
        // lookup has to go through the same registry the builder gated.
        PerPlayerEntityValueProviderRegistry providers = PerPlayerEntityValueProviderRegistry.CreateDefault();
        RuleChainBuilder builder = new(
            EventRegistry.Build(),
            Demo(),
            entityProviders: EntityValueProviderRegistry.CreateDefault(),
            perPlayerEntityProviders: providers);

        return (builder.Build([Resolve(yaml)]), providers);
    }

    /// <summary>Two known players so the per-player template materializes; no frames, so no scan runs.</summary>
    private static ParsedDemo Demo()
    {
        Dictionary<int, PlayerInfo> players = new()
        {
            [0] = new PlayerInfo(0, "Alice", 0UL, 0, 2, false),
            [1] = new PlayerInfo(1, "Bob", 0UL, 1, 3, false)
        };

        return new ParsedDemo(
            [], [], players, null, "de_anytown", 0, 1f / 64f, "test", "test", "csgo",
            0, 0, 0, "valve_demo_2", "", "", DemoProfile.Unknown);
    }
}
