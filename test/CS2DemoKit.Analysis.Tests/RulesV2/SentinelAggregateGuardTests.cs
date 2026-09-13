#region

using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Analysis.Building;
using CS2DemoKit.Analysis.Catalog;
using CS2DemoKit.Analysis.Edges;
using CS2DemoKit.Analysis.Profiles;
using CS2DemoKit.Analysis.Registry;
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

    // The declared "nothing to measure" constants, kept as sets rather than an or-pattern because
    // the three int ones are the same 1,000,000 and a duplicate constant pattern is a build error
    // here. Bool enrichments have no sentinel: false is a real answer.
    private static readonly HashSet<int> _intSentinels =
    [
        AimShotContextEdge.NoSpotSentinel,
        ShotEnrichmentEdge.NoPreviousShotSentinel,
        SpottedEnrichmentEdge.NoPreviousSpotSentinel
    ];

    private static readonly HashSet<double> _doubleSentinels =
    [
        AimShotContextEdge.NoTravelSentinel,
        AimShotContextEdge.NoFlickSentinel
    ];

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
    //
    // Rejection is "this code and nothing else", for the mirror of the reason acceptance is "no
    // diagnostic at all": these stats name facets (`enemy`, `event.Weapon`) that no accepting arm
    // here validates, so if one were retired the gate set would fail to resolve, the guard would
    // fire for the wrong reason, and an Any() assertion would keep passing while testing nothing.

    private static async Task AssertRejectedByTheGuard(RulesetResolveResult resolved)
    {
        await Assert.That(resolved.Diagnostics.Select(d => d.Code)).IsEquivalentTo([Code])
            .Because("a rejection riding on an unrelated resolve failure proves nothing about the guard");
    }

    [Test]
    public async Task Sum_OverTicksSinceSpot_WithNoGate_IsRejected()
    {
        RulesetResolveResult resolved = ResolveResult(Ruleset("""
                                                              sum: enrich.shot.ticks_since_spot
                                                              on: shot
                                                              per: round
                                                              """));

        await AssertRejectedByTheGuard(resolved);
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

        await AssertRejectedByTheGuard(resolved);
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

        await AssertRejectedByTheGuard(resolved);
    }

    [Test]
    public async Task Sum_OverKillTicksSinceSpot_GatedOnlyOnEnmity_IsRejected()
    {
        // `enemy: true` is a gate, but it is a gate on WHOSE kill it was, not on whether the killer
        // had a contact behind the kill — which is the only thing that proves the read measured.
        RulesetResolveResult resolved = ResolveResult(Ruleset("""
                                                              sum: enrich.kill.ticks_since_spot
                                                              on: kill
                                                              match: { enemy: true }
                                                              per: round
                                                              """));

        await AssertRejectedByTheGuard(resolved);
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

        await AssertRejectedByTheGuard(resolved);
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

        await AssertRejectedByTheGuard(resolved);
    }

    // ── tally: is outside the guard, and outside the reach of the defect ──────────────

    [Test]
    public async Task Tally_CannotReadASentinelBearingEnrichmentAtAll()
    {
        // tally: counts threshold crossings, so an ungated sentinel source would be exactly as wrong
        // as an ungated sum — but the guard has no Tally arm and needs none. A tally sources a
        // SIBLING STAT at round end: it resolves on its own path (nothing reaches the guard) and its
        // source is checked in the round-end state scope, which exposes no `enrich` root. The read is
        // refused before any question of gating arises. If `enrich` is ever added to that scope this
        // fails, and RejectUngatedSentinelAggregate has to grow the arm it does not need today.
        RulesetResolveResult resolved = ResolveResult(Ruleset("""
                                                              tally: enrich.shot.flick_error_deg
                                                              thresholds:
                                                                - { min: 5, target: sub_5_deg }
                                                              per: match
                                                              """));

        await Assert.That(resolved.Diagnostics.Select(d => d.Code))
            .IsEquivalentTo(["resolve.unknown-root"]);
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
                                                              match: { first_after_spot: true, ticks_since_spot: "<= 320" }
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

        // The count this used to assert could not fire on the case it named: a newly added,
        // UNDECLARED sentinel node leaves the count where it was and the test passes. Ask the node
        // set instead — every enrichment whose reset default IS one of the no-measurement constants
        // has to carry `sentinel:` in the catalogue, or the resolver will let a sum find it.
        BuiltinContexts.EnrichmentInfrastructure infrastructure = BuiltinContexts.CreateEnrichment(
            new StateGraph().Root,
            new PlayerContextIndex(),
            EventRegistry.Build(),
            new LogicalEventResolver(new Cs2GotvProfile()));

        // A transient's constructor default is the value Reset writes, not the value it holds before
        // its first dispatch (which is the CLR default), so put each node in its reset state first.
        foreach (StateNode node in infrastructure.Nodes)
        {
            if (node is ITransientNode transient)
            {
                transient.Reset();
            }
        }

        string[] sentinelDefaulted = infrastructure.Nodes
            .Where(CarriesANoMeasurementDefault)
            .Select(node => node.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        await Assert.That(sentinelDefaulted).IsNotEmpty()
            .Because("finding none would mean the defaults are not being read and the rest is vacuous");

        foreach (string name in sentinelDefaulted)
        {
            await Assert.That(byName[name].Sentinel).IsNotNull()
                .Because($"{name} defaults to a no-measurement sentinel, so a sum over it has to be "
                         + "refused; an undeclared one is found by whoever aggregates it first");
        }
    }

    private static bool CarriesANoMeasurementDefault(StateNode node) => node switch
    {
        ValueNode<int> ints => _intSentinels.Contains(ints.Value),
        ValueNode<double> doubles => _doubleSentinels.Contains(doubles.Value),
        _ => false
    };

    private static RulesetResolveResult ResolveResult(string yaml)
    {
        RulesetDoc doc = RulesetDocumentLoader.Load(yaml, "t.rules.yaml").Doc
                         ?? throw new InvalidOperationException("ruleset failed to map");
        return CheckedRulesetDraft.Load(doc, _adapter).Build(64.0, "Cs2GotvProfile");
    }
}
