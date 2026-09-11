#region

using CS2DemoKit.Analysis.Catalog;
using CS2DemoKit.Analysis.Edges;
using CS2DemoKit.Analysis.RulesetsV2.Model;
using CS2DemoKit.Analysis.RulesetsV2.Resolve;
using CS2DemoKit.Analysis.Yaml;

#endregion

namespace CS2DemoKit.Analysis.Tests.RulesV2;

/// <summary>
///     Some enrichments carry a SENTINEL when there was nothing to measure: <c>ticks_since_spot</c>
///     reads 1,000,000 on a shot with no contact behind it, <c>travel_from_spot_deg</c> reads -1,
///     <c>flick_error_deg</c> reads -1000. That is deliberate (a missing write must not read as a
///     perfect zero), but it means a <c>sum:</c> over one of them without a gate adds a million per
///     unspotted shot and produces a plausible-looking number rather than an error. The catalogue
///     now marks those enrichments and the resolver refuses to aggregate one unless the trigger
///     condition tests it (or an enrichment declared to prove it measured, the way a travel gate
///     proves the flick error). The accepted shapes below are the ones the shipped aim ruleset uses.
/// </summary>
[Category("Unit")]
public class SentinelAggregateGuardTests
{
    private const string Code = "resolve.ungated-sentinel-aggregate";

    private static readonly CatalogScopeAdapter _adapter = CatalogScopeAdapter.From(CatalogResource.Load());

    private static string Ruleset(string statBody) => $"""
                                                       ruleset: t
                                                       for: each_player
                                                       stats:
                                                         m:
                                                       {Indent(statBody)}
                                                       """;

    private static string Indent(string body) =>
        string.Join("\n", body.Split('\n').Select(line => "    " + line));

    // ── Rejected: an aggregate over a sentinel-bearing read with no gate on it ─────────

    [Test]
    public async Task Sum_OverTicksSinceSpot_WithNoGate_IsRejected()
    {
        RulesetResolveResult resolved = ResolveResult(Ruleset("""
                                                              sum: enrich.shot.ticks_since_spot
                                                              on: shot
                                                              per: round
                                                              """));

        RulesetDiagnostic diagnostic = resolved.Diagnostics.Single(d => d.Code == Code);
        await Assert.That(diagnostic.Message).Contains("enrich.shot.ticks_since_spot");
        await Assert.That(diagnostic.Message).Contains(AimShotContextEdge.NoSpotSentinel.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    [Test]
    public async Task Sum_OverTicksSinceSpot_GatedOnADifferentFacet_IsStillRejected()
    {
        // A gate that does not test the sentinel-bearing read proves nothing about it.
        RulesetResolveResult resolved = ResolveResult(Ruleset("""
                                                              sum: enrich.shot.ticks_since_spot
                                                              on: shot
                                                              match: { bullet: true }
                                                              per: round
                                                              """));

        await Assert.That(resolved.Diagnostics.Any(d => d.Code == Code)).IsTrue();
    }

    [Test]
    public async Task Sum_OverFlickError_GatedOnlyByTicksSinceSpot_IsRejected()
    {
        // ticks_since_spot proves a contact exists; it does not prove a view angle was available,
        // which is the condition the flick error (and the travel it derives from) needs.
        RulesetResolveResult resolved = ResolveResult(Ruleset("""
                                                              sum: enrich.shot.flick_error_deg
                                                              on: shot
                                                              match: { ticks_since_spot: "<= 320" }
                                                              per: round
                                                              """));

        await Assert.That(resolved.Diagnostics.Any(d => d.Code == Code)).IsTrue();
    }

    [Test]
    public async Task Sum_OverKillTicksSinceSpot_WithNoGate_IsRejected()
    {
        RulesetResolveResult resolved = ResolveResult(Ruleset("""
                                                              sum: enrich.kill.ticks_since_spot
                                                              on: kill
                                                              match: { enemy: true }
                                                              per: round
                                                              """));

        await Assert.That(resolved.Diagnostics.Any(d => d.Code == Code)).IsTrue();
    }

    [Test]
    public async Task BucketSum_OverASentinelValue_WithNoGate_IsRejected()
    {
        RulesetResolveResult resolved = ResolveResult(Ruleset("""
                                                              bucket: shot
                                                              key: event.Weapon
                                                              value: enrich.shot.ticks_since_on_target
                                                              per: round
                                                              """));

        await Assert.That(resolved.Diagnostics.Any(d => d.Code == Code)).IsTrue();
    }

    [Test]
    public async Task CaptureMin_OverASentinelValue_WithNoGate_IsRejected()
    {
        // keep: min over travel_from_spot_deg would settle on the -1 sentinel every round.
        RulesetResolveResult resolved = ResolveResult(Ruleset("""
                                                              capture: enrich.shot.travel_from_spot_deg
                                                              on: shot
                                                              keep: min
                                                              per: round
                                                              """));

        await Assert.That(resolved.Diagnostics.Any(d => d.Code == Code)).IsTrue();
    }

    // ── Accepted: the gate tests the read itself, or an enrichment that proves it ──────
    //
    // Acceptance is "the stat resolved with no diagnostic at all", not "this one code is absent":
    // the guard returns early on a null value selector, so a stat that failed to resolve for an
    // unrelated reason (a bad expression, an unknown facet) would also carry no guard code.

    private static async Task AssertAccepted(RulesetResolveResult resolved)
    {
        await Assert.That(resolved.Diagnostics).IsEmpty()
            .Because("an acceptance that rides on an unrelated resolve failure proves nothing about the guard");
        await Assert.That(resolved.Ruleset).IsNotNull();
    }

    [Test]
    public async Task Sum_OverTicksSinceSpot_GatedByMatch_IsAccepted()
    {
        RulesetResolveResult resolved = ResolveResult(Ruleset("""
                                                              sum: enrich.shot.ticks_since_spot
                                                              on: shot
                                                              match: { is_first_after_spot: true, ticks_since_spot: "<= 320" }
                                                              per: round
                                                              """));

        await AssertAccepted(resolved);
    }

    [Test]
    public async Task Sum_OverTicksSinceSpot_GatedByWhere_IsAccepted()
    {
        RulesetResolveResult resolved = ResolveResult(Ruleset("""
                                                              sum: enrich.shot.ticks_since_spot
                                                              on: shot
                                                              where: "enrich.shot.ticks_since_spot <= 320"
                                                              per: round
                                                              """));

        await AssertAccepted(resolved);
    }

    [Test]
    public async Task Sum_OverFlickError_GatedByTravel_IsAccepted()
    {
        // The shipped aim ruleset's shape: flick error is written exactly when travel is, so a
        // travel gate proves the flick error measured.
        RulesetResolveResult resolved = ResolveResult(Ruleset("""
                                                              sum: min(enrich.shot.flick_error_deg, 360.0)
                                                              on: shot
                                                              match: { travel_from_spot_deg: ">= 0", ticks_since_spot: "<= 320" }
                                                              per: round
                                                              """));

        await AssertAccepted(resolved);
    }

    [Test]
    public async Task Sum_OverTravel_GatedByTravel_IsAccepted()
    {
        RulesetResolveResult resolved = ResolveResult(Ruleset("""
                                                              sum: abs(enrich.shot.travel_from_spot_deg)
                                                              on: shot
                                                              match: { travel_from_spot_deg: ">= 0" }
                                                              per: round
                                                              """));

        await AssertAccepted(resolved);
    }

    [Test]
    public async Task Sum_OverAnUnsentinelledEnrichment_NeedsNoGate()
    {
        RulesetResolveResult resolved = ResolveResult(Ruleset("""
                                                              sum: enrich.shot.turn_degrees
                                                              on: shot
                                                              per: round
                                                              """));

        await AssertAccepted(resolved);
    }

    [Test]
    public async Task CaptureLast_OfASentinelValue_IsNotAnAggregate()
    {
        // A capture keeps the value as written; the sentinel comes through as the honest
        // "no measurement" it is, and nothing is summed.
        RulesetResolveResult resolved = ResolveResult(Ruleset("""
                                                              capture: enrich.shot.ticks_since_spot
                                                              on: shot
                                                              keep: last
                                                              per: round
                                                              """));

        await AssertAccepted(resolved);
    }

    [Test]
    public async Task Count_OnAViewWithSentinelFacets_IsUnaffected()
    {
        RulesetResolveResult resolved = ResolveResult(Ruleset("""
                                                              count: shot
                                                              per: round
                                                              """));

        await Assert.That(resolved.Diagnostics).IsEmpty();
    }

    // ── Catalogue: the sentinels are declared, not inferred ─────────────────────────

    [Test]
    public async Task Catalog_MarksEverySentinelBearingEnrichment()
    {
        CatalogRoot catalog = CatalogResource.Load();
        Dictionary<string, CatalogEnrichment> byName = catalog.Enrichments.ToDictionary(e => e.Name, StringComparer.Ordinal);

        await Assert.That(byName["enrich.shot.ticks_since_spot"].Sentinel).IsEqualTo("1000000");
        await Assert.That(byName["enrich.shot.ticks_since_on_target"].Sentinel).IsEqualTo("1000000");
        await Assert.That(byName["enrich.kill.ticks_since_spot"].Sentinel).IsEqualTo("1000000");
        await Assert.That(byName["enrich.shot.ticks_since_last_shot"].Sentinel).IsEqualTo("1000000");
        await Assert.That(byName["enrich.spotted.ticks_since_last_spot"].Sentinel).IsEqualTo("1000000");
        await Assert.That(byName["enrich.shot.travel_from_spot_deg"].Sentinel).IsEqualTo("-1");
        await Assert.That(byName["enrich.shot.flick_error_deg"].Sentinel).IsEqualTo("-1000");
        await Assert.That(byName["enrich.shot.flick_error_deg"].ProvenBy)
            .IsEquivalentTo(["enrich.shot.travel_from_spot_deg"]);

        // An ordinary enrichment carries neither.
        await Assert.That(byName["enrich.shot.turn_degrees"].Sentinel).IsNull();
        await Assert.That(byName["enrich.kill.was_enemy_kill"].Sentinel).IsNull();

        int marked = catalog.Enrichments.Count(e => e.Sentinel is not null);
        await Assert.That(marked).IsEqualTo(7)
            .Because("a new sentinel-defaulted enrichment must be declared here, not left for a sum to find");
    }

    private static RulesetResolveResult ResolveResult(string yaml)
    {
        RulesetDoc doc = RulesetDocumentLoader.Load(yaml, "t.rules.yaml").Doc
                         ?? throw new InvalidOperationException("ruleset failed to map");
        return CheckedRulesetDraft.Load(doc, _adapter).Build(64.0, "Cs2GotvProfile");
    }
}
