#region

using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Analysis.Building;
using CS2DemoKit.Analysis.Catalog;
using CS2DemoKit.Analysis.Graphs;
using CS2DemoKit.Analysis.Plugins;
using CS2DemoKit.Analysis.Profiles;
using CS2DemoKit.Analysis.Registry;
using CS2DemoKit.Analysis.RulesetsV2.Model;
using CS2DemoKit.Analysis.RulesetsV2.Resolve;
using CS2DemoKit.Analysis.Visibility;
using CS2DemoKit.Analysis.Yaml;
using CS2DemoKit.Parser;

#endregion

namespace CS2DemoKit.Analysis.Tests.RulesV2;

/// <summary>
///     The spot-derived aim facets and the <c>enemy_spotted</c> view measure nothing unless the
///     visibility scan is running, and the scan needs two things the ruleset cannot see: baked map
///     geometry handed in through <c>AnalysisOptions.VisibilityEngine</c>, and some rule subscribing
///     to the event. Miss either and every one of those facets reads its no-measurement sentinel on
///     every event — a total that is a plausible number, not an error, and indistinguishable from a
///     player who genuinely never spotted anyone.
///     <para>
///         <c>RuleChainBuilder</c> reports that as a coverage diagnostic, the same channel a view
///         that does not bind on the active profile uses. It cannot be a resolve diagnostic: the
///         engine reaches the builder's constructor, not <c>ResolveContext</c>, and the subscription
///         half is directory-wide while resolution is per document. These tests drive both
///         directions — it fires on every shape that reads the family unproducibly, and it stays
///         silent on the four shipped rulesets and on the aim facets that need no geometry.
///     </para>
/// </summary>
[Category("Unit")]
public class SpotDerivedCoverageTests
{
    /// <summary>The working shape from docs/RULES_AUTHORING.md §5, "Facets that need a map bake".</summary>
    private const string DocumentedSpotExample = """
                                                 ruleset: t
                                                 for: each_player
                                                 stats:
                                                   contacts:
                                                     count: enemy_spotted
                                                     per: round
                                                   reaction_ticks:
                                                     sum: enrich.shot.ticks_since_spot
                                                     on: shot
                                                     match: { first_after_spot: true, ticks_since_spot: "<= 320" }
                                                     per: round
                                                   flick_error_sum:
                                                     sum: enrich.shot.flick_error_deg
                                                     on: shot
                                                     match: { travel_from_spot_deg: ">= 0" }
                                                     per: round
                                                 """;

    private static readonly CatalogScopeAdapter _adapter = CatalogScopeAdapter.From(CatalogResource.Load());

    private static readonly string[] _shippedRulesets =
    [
        "kast.rules.yaml",
        "player_stats.rules.yaml",
        "post_plant_double.rules.yaml",
        "weapon_stats.rules.yaml"
    ];

    // ── Fires: every shape that reads the family on a run that cannot produce it ────────

    [Test]
    public async Task ShotSpotFacet_WithNoVisibilityEngine_IsReported()
    {
        BuildResult build = BuildWithoutGeometry("""
                                                 ruleset: t
                                                 for: each_player
                                                 stats:
                                                   reaction_ticks:
                                                     sum: enrich.shot.ticks_since_spot
                                                     on: shot
                                                     match: { ticks_since_spot: "<= 320" }
                                                     per: round
                                                 """);

        RulesetCoverageDiagnostic reported = await SingleCoverage(build, "reaction_ticks");
        await Assert.That(reported.Message).Contains("enrich.shot.ticks_since_spot");
        await Assert.That(reported.Message).Contains("AnalysisOptions.VisibilityEngine")
            .Because("the author has to be told which of the two prerequisites this run is missing");
    }

    [Test]
    public async Task OnTargetFacet_WithNoVisibilityEngine_IsReported()
    {
        // The on-target pair reads the transition scanner directly rather than through
        // PlayerContext.LastSpotTick, so it is a separate path to the same dead column.
        BuildResult build = BuildWithoutGeometry("""
                                                 ruleset: t
                                                 for: each_player
                                                 stats:
                                                   aimed_reaction:
                                                     sum: enrich.shot.ticks_since_on_target
                                                     on: shot
                                                     match: { ticks_since_on_target: "<= 320" }
                                                     per: round
                                                 """);

        RulesetCoverageDiagnostic reported = await SingleCoverage(build, "aimed_reaction");
        await Assert.That(reported.Message).Contains("enrich.shot.ticks_since_on_target");
    }

    [Test]
    public async Task EnemySpottedSubscription_WithNoVisibilityEngine_IsReported()
    {
        // The reviewer's case from the other end: this resolves, type-checks, and reports 0 for
        // every player on every round, while the catalogue advertises the view on all five profiles.
        BuildResult build = BuildWithoutGeometry("""
                                                 ruleset: t
                                                 for: each_player
                                                 stats:
                                                   contacts:
                                                     count: enemy_spotted
                                                     per: round
                                                 """);

        RulesetCoverageDiagnostic reported = await SingleCoverage(build, "contacts");
        await Assert.That(reported.Message).Contains("never fires");
        await Assert.That(reported.ViewName).IsEqualTo("enemy_spotted");
    }

    [Test]
    public async Task KillTicksSinceSpot_MatchedOnTheKillView_IsReported()
    {
        // The kill view's ticks_since_spot has the same shape one view over: a rule that only
        // writes the match: satisfies neither prerequisite and reads the sentinel forever.
        BuildResult build = BuildWithoutGeometry("""
                                                 ruleset: t
                                                 for: each_player
                                                 stats:
                                                   quick_kills:
                                                     count: kill
                                                     match: { enemy: true, ticks_since_spot: "<= 128" }
                                                     per: round
                                                 """);

        RulesetCoverageDiagnostic reported = await SingleCoverage(build, "quick_kills");
        await Assert.That(reported.Message).Contains("enrich.kill.ticks_since_spot");
        await Assert.That(reported.ViewName).IsEqualTo("kill");
    }

    [Test]
    public async Task SpotFacet_WithGeometryButNoSubscription_NamesTheMissingSubscription()
    {
        // The half a reader never thinks about: the geometry is in hand, but the contact scan runs
        // only for a rule that subscribes to the event, and `on: shot` is not a subscription.
        BuildResult build = BuildWithGeometry("""
                                              ruleset: t
                                              for: each_player
                                              stats:
                                                reaction_ticks:
                                                  sum: enrich.shot.ticks_since_spot
                                                  on: shot
                                                  match: { first_after_spot: true, ticks_since_spot: "<= 320" }
                                                  per: round
                                              """);

        RulesetCoverageDiagnostic reported = await SingleCoverage(build, "reaction_ticks");
        await Assert.That(reported.Message).Contains("no stat subscribes to the 'enemy_spotted' view");
        await Assert.That(reported.Message).DoesNotContain("AnalysisOptions.VisibilityEngine")
            .Because("naming the prerequisite this run already satisfies sends the author the wrong way");
    }

    // ── Stays silent: the setups that actually work, and the rules that need none of this ──

    /// <summary>
    ///     The "working shape" of docs/RULES_AUTHORING.md §5 — a subscribing stat next to the two
    ///     readers — must build clean on a run with geometry, and must be the shape that stops the
    ///     diagnostic. An author copying a documented example gets a working stat or a clear row;
    ///     this is the half that has to be working.
    /// </summary>
    [Test]
    public async Task DocumentedSpotExample_WithGeometry_IsNotReported()
    {
        BuildResult build = BuildWithGeometry(DocumentedSpotExample);

        await Assert.That(SpotCoverage(build)).IsEmpty()
            .Because("a diagnostic on a correctly-set-up ruleset is worse than the silent zero it replaces");
    }

    /// <summary>
    ///     And the other half: dropping the prerequisite the document names is exactly what makes
    ///     every stat in it report — including the subscription, which is the one an author is least
    ///     likely to suspect.
    /// </summary>
    [Test]
    public async Task DocumentedSpotExample_WithNoGeometry_ReportsEveryStat()
    {
        BuildResult build = BuildWithoutGeometry(DocumentedSpotExample);

        await Assert.That(SpotCoverage(build).Select(c => c.NodeId))
            .IsEquivalentTo(["contacts", "reaction_ticks", "flick_error_sum"]);
    }

    /// <summary>
    ///     The sentinel-gate example from the same section reads an enrichment that needs no bake, so
    ///     it resolves clean and reports nothing on an ordinary run — which is the point of having
    ///     replaced the spot-derived pair that used to sit there.
    /// </summary>
    [Test]
    public async Task DocumentedSentinelExample_NeedsNoGeometry()
    {
        BuildResult build = BuildWithoutGeometry("""
                                                 ruleset: t
                                                 for: each_player
                                                 stats:
                                                   burst_gaps:
                                                     sum: enrich.shot.ticks_since_last_shot
                                                     on: shot
                                                     where: "enrich.shot.ticks_since_last_shot <= 64"
                                                     per: round
                                                 """);

        await Assert.That(SpotCoverage(build)).IsEmpty();
    }

    // ── The contact scan's round boundary ──────────────────────────────────────────────

    [Test]
    public async Task ContactScan_IsAttachedToThePlayerContextIndex()
    {
        // The scanner's visible / on-target sets are match-lived; a contact is a per-round fact.
        // PlayerContextIndex.ResetRoundState is where the round ends, and it can only end the
        // scanner's round if the build attached it — this is the only place the two meet, and an
        // unattached scanner leaves the reset a no-op with nothing reporting it.
        BuildResult build = BuildWithGeometry("""
                                              ruleset: t
                                              for: each_player
                                              stats:
                                                contacts:
                                                  count: enemy_spotted
                                                  per: round
                                              """);

        await Assert.That(build.PlayerContextIndex).IsNotNull();
        await Assert.That(build.PlayerContextIndex!.VisibilityTransitions).IsNotNull()
            .Because("without the attachment every round after the first inherits the previous "
                     + "round's contact state and its first real contact emits nothing");
    }

    [Test]
    public async Task ContactScan_IsNotAttached_WhenNoScanWasBuilt()
    {
        // The other direction: the attachment rides with the scanner, so a run that builds none
        // leaves the property null rather than pointing the reset at a stale instance.
        BuildResult build = BuildWithoutGeometry("""
                                                 ruleset: t
                                                 for: each_player
                                                 stats:
                                                   contacts:
                                                     count: enemy_spotted
                                                     per: round
                                                 """);

        await Assert.That(build.PlayerContextIndex!.VisibilityTransitions).IsNull();
    }

    [Test]
    public async Task AimFacetsThatNeedNoGeometry_AreNotReported()
    {
        // Counter-strafing and the spray residual are computed from the movement and recoil columns
        // alone. They are in AimShotEnrichments but not in SpotDerivedEnrichments, and reporting
        // them would fire on every aim ruleset ever written.
        BuildResult build = BuildWithoutGeometry("""
                                                 ruleset: t
                                                 for: each_player
                                                 stats:
                                                   cs_good:
                                                     count: shot
                                                     match: { bullet: true, counter_strafe_admitted: true, counter_strafe_good: true }
                                                     per: round
                                                   residual:
                                                     sum: enrich.shot.spray_residual_deg
                                                     on: shot
                                                     match: { bullet: true, spray_residual_measured: true }
                                                     per: round
                                                 """);

        await Assert.That(SpotCoverage(build)).IsEmpty();
    }

    [Test]
    public async Task ShippedRulesets_ReportNothing_OnARunWithNoGeometry()
    {
        // None of the four reads the family, and none of them should ever be told about geometry
        // they do not need. This is the false-positive gate.
        List<RulesetDoc> docs = _shippedRulesets.Select(LoadShipped).ToList();
        RuleChainBuilder builder = new(EventRegistry.Build());
        RulesetComposition.Result composed = RulesetComposition.Compose(
            docs, _adapter, 64.0, builder.Profile.GetType().Name);
        await Assert.That(composed.Success).IsTrue()
            .Because("a composition failure here would make the coverage assertion vacuous: "
                     + string.Join("; ", composed.Diagnostics));

        BuildResult build = builder.Build(composed.Rulesets);
        await Assert.That(composed.Rulesets.SelectMany(r => r.Stats)).IsNotEmpty();
        await Assert.That(SpotCoverage(build)).IsEmpty();
    }

    // ── The list itself ────────────────────────────────────────────────────────────────

    [Test]
    public async Task SpotDerivedList_NamesRegisteredEnrichmentNodes()
    {
        // A misspelt entry is silent in both directions: it never matches a declared read, so the
        // diagnostic it was added for never fires and nothing says so.
        BuiltinContexts.EnrichmentInfrastructure infrastructure = BuiltinContexts.CreateEnrichment(
            new StateGraph().Root,
            new PlayerContextIndex(),
            EventRegistry.Build(),
            new LogicalEventResolver(new Cs2GotvProfile()));

        foreach (string name in BuiltinContexts.SpotDerivedEnrichments)
        {
            await Assert.That(infrastructure.NodeLookup.ContainsKey(name)).IsTrue()
                .Because($"{name} is reported on but no enrichment node answers to it");
        }
    }

    [Test]
    public async Task SpotDerivedList_IsReachableFromTheCatalogue()
    {
        // Every entry has to be a facet an author can actually write, or the diagnostic guards a
        // read that cannot be expressed.
        HashSet<string> facetEnrichments = new(
            CatalogResource.Load().Views
                .SelectMany(v => v.Facets)
                .Select(f => f.Enrichment)
                .Where(e => e is not null)!,
            StringComparer.Ordinal);

        foreach (string name in BuiltinContexts.SpotDerivedEnrichments)
        {
            await Assert.That(facetEnrichments.Contains(name)).IsTrue()
                .Because($"{name} is not the target of any view facet, so no ruleset can read it");
        }
    }

    [Test]
    public async Task SpotDerivedList_ExcludesTheAimFacetsThatMeasureWithoutGeometry()
    {
        // The seven are a strict subset of the shot family plus the kill view's one. Anything else
        // in the list means an unconditional facet is being reported as unproducible.
        HashSet<string> allowed = new(BuiltinContexts.AimShotEnrichments, StringComparer.Ordinal)
        {
            "enrich.kill.ticks_since_spot"
        };

        string[] unexpected = BuiltinContexts.SpotDerivedEnrichments
            .Where(name => !allowed.Contains(name))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        await Assert.That(unexpected).IsEmpty();

        string[] movementOnly =
        [
            "enrich.shot.counter_strafe_good",
            "enrich.shot.counter_strafe_admitted",
            "enrich.shot.is_first_bullet",
            "enrich.shot.spray_residual_pitch",
            "enrich.shot.spray_residual_yaw",
            "enrich.shot.spray_residual_measured",
            "enrich.shot.spray_residual_deg"
        ];
        foreach (string name in movementOnly)
        {
            await Assert.That(BuiltinContexts.SpotDerivedEnrichments.Contains(name, StringComparer.Ordinal))
                .IsFalse()
                .Because($"{name} is computed from the digest columns alone and measures on any run");
        }
    }

    // ── Harness ────────────────────────────────────────────────────────────────────────

    /// <summary>Two known players so the vantage sampler has a roster; no frames, so no scan runs.</summary>
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

    private static BuildResult BuildWithoutGeometry(string yaml) => Build(yaml, null);

    private static BuildResult BuildWithGeometry(string yaml) =>
        Build(yaml, VisibilityEngine.FromTriangles([], 0));

    private static BuildResult Build(string yaml, VisibilityEngine? engine)
    {
        RuleChainBuilder builder = new(
            EventRegistry.Build(),
            AnalysisTarget.From(Demo()),
            entityProviders: EntityValueProviderRegistry.CreateDefault(),
            perPlayerEntityProviders: PerPlayerEntityValueProviderRegistry.CreateDefault(),
            visibilityEngine: engine);

        RulesetDoc doc = RulesetDocumentLoader.Load(yaml, "t.rules.yaml").Doc
                         ?? throw new InvalidOperationException("ruleset failed to map");
        RulesetResolveResult resolved = CheckedRulesetDraft.Load(doc, _adapter)
            .Build(64.0, builder.Profile.GetType().Name);
        CheckedRuleset ruleset = resolved.Ruleset
                                 ?? throw new InvalidOperationException(
                                     "resolve failed: " + string.Join("; ", resolved.Diagnostics));

        return builder.Build([ruleset]);
    }

    /// <summary>The build's coverage rows that came from the spot family, by the event they name.</summary>
    private static List<RulesetCoverageDiagnostic> SpotCoverage(BuildResult build) =>
        (build.RulesetCoverage ?? [])
        .Where(c => c.Message.Contains("enemy_spotted", StringComparison.Ordinal))
        .ToList();

    private static async Task<RulesetCoverageDiagnostic> SingleCoverage(BuildResult build, string nodeId)
    {
        List<RulesetCoverageDiagnostic> reported = SpotCoverage(build);
        await Assert.That(reported.Select(c => c.NodeId)).IsEquivalentTo([nodeId])
            .Because("one row per node that cannot measure, naming the node the author wrote");
        return reported[0];
    }

    private static RulesetDoc LoadShipped(string fileName)
    {
        string path = Path.Combine(RepoRoot(), "src", "CS2DemoKit.Analysis", "Rules", fileName);
        RulesetDocumentLoader.Outcome outcome = RulesetDocumentLoader.Load(File.ReadAllText(path), fileName);
        return outcome.Doc ?? throw new InvalidOperationException(
            $"{fileName} failed to load: " + string.Join("; ", outcome.Diagnostics));
    }

    private static string RepoRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "CS2DemoKit.slnx")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("repo root not found");
    }
}
