#region

using System.Reflection;
using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Analysis.Building;
using CS2DemoKit.Analysis.Catalog;
using CS2DemoKit.Analysis.Config;
using CS2DemoKit.Analysis.Graphs;
using CS2DemoKit.Analysis.Registry;
using CS2DemoKit.Analysis.Rules;
using CS2DemoKit.Analysis.RulesetsV2.Model;
using CS2DemoKit.Analysis.RulesetsV2.Resolve;
using CS2DemoKit.Analysis.Yaml;

#endregion

namespace CS2DemoKit.Analysis.Tests.RulesV2;

/// <summary>
///     Net-message payload matching: a <c>net.&lt;Message&gt;</c> trigger may carry
///     <c>where:</c> (and a field-facet <c>match:</c>) over its payload fields, read under
///     <c>event.&lt;Field&gt;</c> and typed from the <c>netMessages</c> catalog family — exactly the
///     spelling a game-event view uses. This battery pins:
///     <list type="bullet">
///         <item>a net <c>where:</c> over a payload field resolves + type-checks;</item>
///         <item>an unknown payload field is an attributed <c>resolve.unknown-member</c> error;</item>
///         <item>a wrong-type comparison is an attributed <c>check.type-mismatch</c> error;</item>
///         <item>a field-facet <c>match:</c> resolves (and an unknown key errors the same way);</item>
///         <item>
///             the planner lowers a net trigger to a net-message edge, threading the compiled
///             <c>where:</c> condition; a bare net trigger lowers to the same edge with no condition.
///         </item>
///     </list>
///     Uses <c>CDemoFileHeader</c> (a catalog + registry net message with a string <c>MapName</c>,
///     an int <c>BuildNum</c>, and a bool <c>AllowClientsideEntities</c>). Demo-free.
/// </summary>
[Category("Unit")]
public class NetPayloadMatchTests
{
    private const string Gotv = "Cs2GotvProfile";
    private static readonly CatalogScopeAdapter _adapter = CatalogScopeAdapter.From(CatalogResource.Load());

    private static RulesetDoc Doc(string yaml)
    {
        RulesetDocumentLoader.Outcome outcome = RulesetDocumentLoader.Load(yaml, "test.rules.yaml");
        return outcome.Doc
               ?? throw new InvalidOperationException(
                   $"YAML failed to map: {string.Join("; ", outcome.Diagnostics)}");
    }

    private static RulesetResolveResult Resolve(string yaml) =>
        CheckedRulesetDraft.Load(Doc(yaml), _adapter).Build(64.0, Gotv);

    // ── where: over payload fields ─────────────────────────────────────────────

    [Test]
    public async Task NetWhere_OverPayloadField_ResolvesAndTypeChecks()
    {
        const string Yaml = """
                            ruleset: net_where_ok
                            for: each_player
                            stats:
                              headers_on_mirage:
                                count: net.CDemoFileHeader
                                where: 'event.MapName == "de_mirage" and event.BuildNum > 1000'
                                per: match
                            """;

        RulesetResolveResult result = Resolve(Yaml);

        await Assert.That(result.Success).IsTrue()
            .Because("a net where: over string + int payload fields must resolve and type-check: "
                     + string.Join("; ", result.Diagnostics.Select(d => $"{d.Code}: {d.Message}")));
    }

    [Test]
    public async Task NetWhere_UnknownField_AttributedError()
    {
        const string Yaml = """
                            ruleset: net_where_bad_field
                            for: each_player
                            stats:
                              s:
                                count: net.CDemoFileHeader
                                where: 'event.NoSuchField == 1'
                                per: match
                            """;

        RulesetResolveResult result = Resolve(Yaml);

        await Assert.That(result.Success).IsFalse();
        RulesetDiagnostic error = result.Diagnostics.Single(d => d.Code == DiagnosticCodes.UnknownMember);
        await Assert.That(error.Message.Contains("NoSuchField", StringComparison.Ordinal)).IsTrue()
            .Because("the attributed error must name the unknown payload field");
        await Assert.That(error.Position.Line).IsGreaterThan(0);
    }

    [Test]
    public async Task NetWhere_WrongTypeComparison_TypeError()
    {
        // event.MapName is a string; comparing it with an int literal is a type mismatch, the same
        // error a game-event where: field would raise.
        const string Yaml = """
                            ruleset: net_where_bad_type
                            for: each_player
                            stats:
                              s:
                                count: net.CDemoFileHeader
                                where: 'event.MapName > 5'
                                per: match
                            """;

        RulesetResolveResult result = Resolve(Yaml);

        await Assert.That(result.Success).IsFalse();
        await Assert.That(result.Diagnostics.Any(d => d.Code == DiagnosticCodes.TypeMismatch)).IsTrue()
            .Because("comparing a string payload field to an int must be a type error: "
                     + string.Join("; ", result.Diagnostics.Select(d => $"{d.Code}: {d.Message}")));
    }

    // ── field-facet match: ─────────────────────────────────────────────────────

    [Test]
    public async Task NetMatch_FieldFacet_Resolves()
    {
        // match: { Field: <test> } over a net payload lowers to an event.<Field> where:-conjunct.
        const string Yaml = """
                            ruleset: net_match_ok
                            for: each_player
                            stats:
                              s:
                                count: net.CDemoFileHeader
                                match: { AllowClientsideEntities: true, BuildNum: [1000..2000] }
                                per: match
                            """;

        RulesetResolveResult result = Resolve(Yaml);

        await Assert.That(result.Success).IsTrue()
            .Because("a field-facet match: over a bool + int payload field must resolve: "
                     + string.Join("; ", result.Diagnostics.Select(d => $"{d.Code}: {d.Message}")));
    }

    [Test]
    public async Task NetMatch_UnknownField_AttributedError()
    {
        const string Yaml = """
                            ruleset: net_match_bad_field
                            for: each_player
                            stats:
                              s:
                                count: net.CDemoFileHeader
                                match: { NoSuchField: true }
                                per: match
                            """;

        RulesetResolveResult result = Resolve(Yaml);

        await Assert.That(result.Success).IsFalse();
        RulesetDiagnostic error = result.Diagnostics.Single(d => d.Code == DiagnosticCodes.UnknownMember);
        await Assert.That(error.Message.Contains("NoSuchField", StringComparison.Ordinal)).IsTrue()
            .Because("an unknown match key on a net trigger must name the unknown payload field");
    }

    [Test]
    public async Task RawTrigger_Match_StillErrors()
    {
        // Regression: a raw.<event> trigger has no fields/facets, so match: on it still errors.
        const string Yaml = """
                            ruleset: raw_match_bad
                            for: each_player
                            stats:
                              s:
                                count: raw.player_death
                                match: { Anything: true }
                                per: match
                            """;

        RulesetResolveResult result = Resolve(Yaml);

        await Assert.That(result.Success).IsFalse()
            .Because("match: on a raw trigger has no field vocabulary and must still be rejected");
    }

    // ── Planner lowering ───────────────────────────────────────────────────────

    [Test]
    public async Task NetCount_WithWhere_LowersToNetMessageEdge_ThreadingCondition()
    {
        PerPlayerNodeTemplate.MaterializedPlayer player = Materialize("""
                                                                      ruleset: net_plan
                                                                      for: each_player
                                                                      stats:
                                                                        hdr_with_cond:
                                                                          count: net.CDemoFileHeader
                                                                          where: 'event.MapName == "de_mirage"'
                                                                          per: match
                                                                      """);

        StateEdge edge = FindWriteEdge(player, "hdr_with_cond");

        // A count-on-net lowers to OnNetMessageSetValue<CDemoFileHeader, int> (Increment expressed as
        // a "node.value + 1" Set selector — net edges have no Increment shortcut).
        await Assert.That(edge.GetType().Name.StartsWith("OnNetMessageSetValue", StringComparison.Ordinal))
            .IsTrue().Because($"expected a net-message value edge, got {edge.GetType().Name}");
        await Assert.That(edge.GetType().GetGenericArguments()[0].Name).IsEqualTo("CDemoFileHeader");

        // The compiled where: condition must be threaded (a non-null Func<CDemoFileHeader,bool>).
        await Assert.That(CompiledCondition(edge) is not null).IsTrue()
            .Because("the where: condition must be compiled and carried on the net edge");
    }

    [Test]
    public async Task NetCount_Bare_LowersToNetMessageEdge_NoCondition()
    {
        PerPlayerNodeTemplate.MaterializedPlayer player = Materialize("""
                                                                      ruleset: net_plan_bare
                                                                      for: each_player
                                                                      stats:
                                                                        hdr_bare:
                                                                          count: net.CDemoFileHeader
                                                                          per: match
                                                                      """);

        StateEdge edge = FindWriteEdge(player, "hdr_bare");

        await Assert.That(edge.GetType().Name.StartsWith("OnNetMessageSetValue", StringComparison.Ordinal))
            .IsTrue().Because($"expected a net-message value edge, got {edge.GetType().Name}");
        await Assert.That(CompiledCondition(edge) is null).IsTrue()
            .Because("a bare net trigger (no where:) carries no condition — value-identical to the v1 net path");
    }

    // ── Scaffolding ────────────────────────────────────────────────────────────

    private static PerPlayerNodeTemplate.MaterializedPlayer Materialize(string yaml)
    {
        RulesetResolveResult resolved = Resolve(yaml);
        CheckedRuleset rs = resolved.Ruleset
                            ?? throw new InvalidOperationException(
                                "ruleset failed to resolve: " + string.Join("; ", resolved.Diagnostics));

        RuleChainBuilder builder = new(EventRegistry.Build());
        BuildResult build = builder.Build([rs]);
        return build.Graph.PerPlayerTemplates[^1].Materialize(0, 0, "test", null);
    }

    private static StateEdge FindWriteEdge(PerPlayerNodeTemplate.MaterializedPlayer player, string writtenNodeName) =>
        player.Edges.FirstOrDefault(e =>
            e.WrittenNode is { } written && string.Equals(written.Name, writtenNodeName, StringComparison.Ordinal))
        ?? throw new InvalidOperationException($"no write edge for node '{writtenNodeName}'");

    /// <summary>The edge's compiled payload condition (the captured primary-ctor <c>condition</c> field).</summary>
    private static Delegate? CompiledCondition(StateEdge edge)
    {
        Type payloadType = edge.GetType().GetGenericArguments()[0];
        Type conditionType = typeof(Func<,>).MakeGenericType(payloadType, typeof(bool));
        FieldInfo field = edge.GetType()
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .Single(f => f.FieldType == conditionType);
        return (Delegate?)field.GetValue(edge);
    }
}
