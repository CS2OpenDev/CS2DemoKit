#region

using CS2DemoKit.Analysis.Building;
using CS2DemoKit.Analysis.Catalog;
using CS2DemoKit.Analysis.Graphs;
using CS2DemoKit.Analysis.Registry;
using CS2DemoKit.Analysis.Rules;
using CS2DemoKit.Analysis.RulesetsV2.Model;
using CS2DemoKit.Analysis.RulesetsV2.Resolve;

#endregion

namespace CS2DemoKit.Analysis.Tests.RulesV2;

/// <summary>
///     The per-player lowering probe's verdict is memoized by build input. Each test owns its cache,
///     so no other test can warm or evict an entry under it.
/// </summary>
[Category("Unit")]
public class LoweringProbeCacheTests
{
    [Test]
    public async Task SameSet_BuiltTwice_ReplaysTheExclusionsWithoutProbing()
    {
        LoweringProbeCache cache = new();
        RuleChainBuilder first = new(EventRegistry.Build()) { ProbeCache = cache };
        BuildResult a = first.Build(Compose());
        RuleChainBuilder second = new(EventRegistry.Build()) { ProbeCache = cache };
        BuildResult b = second.Build(Compose());

        await Assert.That(first.LoweringProbeRuns).IsGreaterThan(0);
        await Assert.That(second.LoweringProbeRuns).IsEqualTo(0)
            .Because("a freshly composed but identical set hits the cache");

        await Assert.That(a.ExcludedRulesets.Select(e => e.Id).ToList())
            .IsEquivalentTo(new List<string> { "bad_streak", "reader" });
        await Assert.That(Flatten(b)).IsEquivalentTo(Flatten(a));

        await Assert.That(b.Graph.PerPlayerTemplates.Count).IsEqualTo(1)
            .Because("the healthy ruleset still registers its template");
        int nodesA = a.Graph.PerPlayerTemplates.Single().Materialize(0, 0, "x").Nodes.Count;
        int nodesB = b.Graph.PerPlayerTemplates.Single().Materialize(0, 0, "x").Nodes.Count;
        await Assert.That(nodesB).IsEqualTo(nodesA);
    }

    [Test]
    public async Task ChangedSet_MissesTheCache()
    {
        LoweringProbeCache cache = new();
        _ = new RuleChainBuilder(EventRegistry.Build()) { ProbeCache = cache }.Build(Compose());

        // Same ids, one stat moved down a line: the replayed position would be stale.
        List<CheckedRuleset> moved = [.. Compose().Select(rs => rs.Id.Id == "bad_streak"
            ? rs with
            {
                Stats = [rs.Stats.Single() with { Position = rs.Stats.Single().Position with { Line = 99 } }]
            }
            : rs)];
        RuleChainBuilder builder = new(EventRegistry.Build()) { ProbeCache = cache };
        BuildResult build = builder.Build(moved);

        await Assert.That(builder.LoweringProbeRuns).IsGreaterThan(0);
        await Assert.That(build.ExcludedRulesets.Single(e => e.Id == "bad_streak").Diagnostics.Single().Position.Line)
            .IsEqualTo(99);

        RuleChainBuilder healthyOnly = new(EventRegistry.Build()) { ProbeCache = cache };
        BuildResult clean = healthyOnly.Build([.. Compose().Where(rs => rs.Id.Id == "healthy")]);
        await Assert.That(healthyOnly.LoweringProbeRuns).IsEqualTo(1);
        await Assert.That(clean.ExcludedRulesets.Count).IsEqualTo(0);
    }

    private static List<(string Id, string? File, string? RulesetId, DiagnosticSeverity Severity, string Code,
        string Message, SourcePosition Position)> Flatten(BuildResult build) =>
    [
        .. build.ExcludedRulesets.SelectMany(e => e.Diagnostics.Select(d =>
            (e.Id, e.SourceFile, d.RulesetId, d.Severity, d.Code, d.Message, d.Position)))
    ];

    // A broken per-player ruleset, a reader of it, and a healthy sibling, composed afresh per call.
    private static List<CheckedRuleset> Compose()
    {
        RulesetDoc provider = RulesetCompositionDiagnosticsTests.Doc("bad_streak.rules.yaml", """
            ruleset: bad_streak
            for: each_player
            exports: [sprees]
            stats:
              sprees:
                streak: kill
                window: 640
                min_streak: 2
                per: match
            """);
        RulesetDoc reader = RulesetCompositionDiagnosticsTests.Doc("reader.rules.yaml", """
            ruleset: reader
            for: each_player
            use: [bad_streak]
            stats:
              more:
                compute: "bad_streak.sprees + 1"
            """);
        RulesetDoc healthy = RulesetCompositionDiagnosticsTests.Doc("healthy.rules.yaml", """
            ruleset: healthy
            for: each_player
            stats:
              kills:
                count: kill
                per: round
            """);
        CatalogScopeAdapter adapter = CatalogScopeAdapter.From(CatalogResource.Load());
        RulesetComposition.Result composed =
            RulesetComposition.Compose([provider, reader, healthy], adapter, 64.0, "Cs2GotvProfile");
        return
        [
            .. composed.Rulesets.Select(rs => rs.Id.Id == "bad_streak"
                ? rs with { Stats = [rs.Stats.Single() with { ConcreteEvents = ["CDemoFileHeader"] }] }
                : rs)
        ];
    }
}
