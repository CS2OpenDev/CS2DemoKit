#region

using CS2DemoKit.Analysis.Catalog;
using CS2DemoKit.Analysis.RulesetsV2.Model;
using CS2DemoKit.Analysis.RulesetsV2.Resolve;
using CS2DemoKit.Analysis.Yaml;

#endregion

namespace CS2DemoKit.Analysis.Tests.RulesV2;

/// <summary>
///     "<c>Build</c> silently discards composition diagnostics" — composition half: the
///     attributed diagnostic list <see cref="RulesetComposition.Result" /> now carries must be a
///     lossless mirror of the raw one, and every dropped document must name itself and its reason
///     in <see cref="RulesetComposition.Result.Excluded" />. Demo-free.
///     <para>
///         The 1:1 assertion is the load-bearing one. <c>rules check</c> counts one list and prints
///         the other (its exit code and <c>N error(s)</c> line come from the attributed list), so a
///         re-derived rather than mirrored attribution would silently change CLI output.
///     </para>
/// </summary>
[Category("Unit")]
public class RulesetCompositionDiagnosticsTests
{
    private static readonly CatalogScopeAdapter _adapter = CatalogScopeAdapter.From(CatalogResource.Load());

    /// <summary>A healthy per-player ruleset: counts kills, no references to anything external.</summary>
    private const string HealthyYaml = """
                                       ruleset: healthy
                                       for: each_player
                                       stats:
                                         kills:
                                           count: kill
                                           per: round
                                       """;

    /// <summary>Broken by an unresolvable identifier in a <c>when:</c> slot (a resolve-tier error).</summary>
    private const string BadIdentifierYaml = """
                                             ruleset: bad_ref
                                             for: each_player
                                             stats:
                                               kills:
                                                 count: kill
                                                 per: round
                                             highlights:
                                               h:
                                                 when: nonexistent_stat >= 2
                                                 per: round
                                                 title: "x"
                                             """;

    /// <summary>Broken by a type error: <c>when:</c> must be bool, <c>kills</c> is an int counter.</summary>
    private const string BadTypeYaml = """
                                       ruleset: bad_type
                                       for: each_player
                                       stats:
                                         kills:
                                           count: kill
                                           per: round
                                       highlights:
                                         h:
                                           when: kills
                                           per: round
                                           title: "x"
                                       """;

    internal static RulesetDoc Doc(string label, string yaml)
    {
        RulesetDocumentLoader.Outcome outcome = RulesetDocumentLoader.Load(yaml, label);
        return outcome.Doc ?? throw new InvalidOperationException(
            $"'{label}' failed to map: {string.Join("; ", outcome.Diagnostics)}");
    }

    internal static RulesetDoc Healthy() => Doc("healthy.rules.yaml", HealthyYaml);

    internal static RulesetDoc BadIdentifier() => Doc("bad_ref.rules.yaml", BadIdentifierYaml);

    internal static RulesetDoc BadType() => Doc("bad_type.rules.yaml", BadTypeYaml);

    [Test]
    public async Task AttributedDiagnostics_MirrorsRawDiagnostics_OneToOneInOrder()
    {
        // A mix of every diagnostic-producing path: a cross-ruleset cycle (attributed to no single
        // ruleset), a resolve error, and a type error.
        RulesetDoc cycleA = Doc("ca.rules.yaml", """
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
        RulesetDoc cycleB = Doc("cb.rules.yaml", """
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
        RulesetComposition.Result composed = RulesetComposition.ComposeDraft(
            [cycleA, cycleB, BadIdentifier(), BadType(), Healthy()], _adapter);

        await Assert.That(composed.Diagnostics.Count).IsGreaterThan(0)
            .Because("this input is deliberately broken three different ways");
        await Assert.That(composed.AttributedDiagnostics.Count).IsEqualTo(composed.Diagnostics.Count)
            .Because("the attributed list is a wrapper, not a re-derivation — rules check's error "
                     + "count and exit code come from it");

        for (int i = 0; i < composed.Diagnostics.Count; i++)
        {
            RulesetDiagnostic raw = composed.Diagnostics[i];
            RulesetCompositionDiagnostic attributed = composed.AttributedDiagnostics[i];
            await Assert.That(attributed.Code).IsEqualTo(raw.Code).Because($"codes must pair at index {i}");
            await Assert.That(attributed.Message).IsEqualTo(raw.Message).Because($"messages must pair at index {i}");
            await Assert.That(attributed.Position).IsEqualTo(raw.Position).Because($"positions must pair at index {i}");
            await Assert.That(attributed.ToString()).IsEqualTo(raw.ToString())
                .Because("rules check prints the attributed form and its output must not drift");
        }
    }

    [Test]
    public async Task Excluded_NamesEachDroppedRuleset_WithItsReason()
    {
        RulesetComposition.Result composed =
            RulesetComposition.ComposeDraft([BadIdentifier(), Healthy(), BadType()], _adapter);

        await Assert.That(composed.Excluded.Select(e => e.Id).Order(StringComparer.Ordinal).ToList())
            .IsEquivalentTo(new List<string>
            {
                "bad_ref",
                "bad_type"
            })
            .Because("exactly the two broken rulesets are dropped; the healthy one composes");
        await Assert.That(composed.Rulesets.Select(rs => rs.Id.Id).ToList())
            .IsEquivalentTo(new List<string>
            {
                "healthy"
            })
            .Because("a broken sibling must not take the healthy ruleset down with it");

        ExcludedRuleset badRef = composed.Excluded.Single(e => e.Id == "bad_ref");
        await Assert.That(badRef.Diagnostics.Count).IsGreaterThan(0).Because("an exclusion always states why");
        await Assert.That(badRef.Diagnostics.All(d => d.RulesetId == "bad_ref")).IsTrue()
            .Because("a per-document diagnostic is attributed to the document that produced it");
        await Assert.That(badRef.Diagnostics.Any(d => d.Message.Contains("nonexistent_stat", StringComparison.Ordinal)))
            .IsTrue().Because("the reason names what was written");
        await Assert.That(badRef.SourceFile).IsEqualTo("bad_ref.rules.yaml")
            .Because("the exclusion carries the document's source label for the author-facing report");
    }

    [Test]
    public async Task CleanComposition_ReportsNoDiagnosticsAndNoExclusions()
    {
        RulesetComposition.Result composed = RulesetComposition.ComposeDraft([Healthy()], _adapter);

        await Assert.That(composed.Success).IsTrue()
            .Because("the healthy ruleset composes: " + string.Join("; ", composed.Diagnostics));
        await Assert.That(composed.AttributedDiagnostics.Count).IsEqualTo(0);
        await Assert.That(composed.Excluded.Count).IsEqualTo(0);
    }

    [Test]
    public async Task CrossRulesetCycle_IsAttributedToNoSingleRuleset()
    {
        RulesetDoc a = Doc("ca.rules.yaml", """
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
        RulesetDoc b = Doc("cb.rules.yaml", """
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
        RulesetComposition.Result composed = RulesetComposition.ComposeDraft([a, b], _adapter);

        RulesetCompositionDiagnostic cycle = composed.AttributedDiagnostics
            .Single(d => d.Code == ResolveDiagnosticCodes.CrossRefCycle);
        await Assert.That(cycle.RulesetId).IsNull()
            .Because("a cycle spans rulesets by construction — its message names the participating ids instead");
    }
}
