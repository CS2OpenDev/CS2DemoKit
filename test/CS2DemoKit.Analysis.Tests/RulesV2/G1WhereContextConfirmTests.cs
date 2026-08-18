#region

using CS2DemoKit.Analysis.Building;
using CS2DemoKit.Analysis.Catalog;
using CS2DemoKit.Analysis.Config;
using CS2DemoKit.Analysis.Graphs;
using CS2DemoKit.Analysis.Registry;
using CS2DemoKit.Analysis.RulesetsV2.Resolve;
using CS2DemoKit.Analysis.Yaml;

#endregion

namespace CS2DemoKit.Analysis.Tests.RulesV2;

/// <summary>
///     STEP-1 CONFIRM probe for pre-freeze gap G1 (event-gated per-player aggregate reads): does a
///     per-player context (<c>player.survived</c>) OR a B6 aggregate (<c>round.enemies.alive</c>) read
///     inside a stat's <c>where:</c> event-condition resolve + materialize, and if so does it bind the
///     subject slot? Pure resolve + no-demo materialize — records the outcome (resolve error, build
///     throw, or clean) for each read class so the confirm/fix decision is empirical, not inferred.
/// </summary>
[Category("Unit")]
public class G1WhereContextConfirmTests
{
    private const string Gotv = "Cs2GotvProfile";
    private static readonly CatalogScopeAdapter _adapter = CatalogScopeAdapter.From(CatalogResource.Load());

    private static string PerPlayerContextWhere =>
        """
        ruleset: g1_ctx_where
        for: each_player
        stats:
          survived_kills:
            count: kill
            where: "player.survived"
            per: round
        """;

    private static string B6AggregateWhere =>
        """
        ruleset: g1_b6_where
        for: each_player
        stats:
          eco_frags:
            count: kill
            where: "round.enemies.alive > 0"
            per: round
        """;

    [Test]
    public async Task PerPlayerContext_In_Where_Probe()
    {
        (string outcome, string detail) = Probe(PerPlayerContextWhere);
        Console.WriteLine($"[G1/confirm] player.survived in where: outcome={outcome} detail={detail}");
        // Pre-fix this was BUILD_THROW ("Unknown player member: survived"); the G1 fix makes a
        // per-player context read in a where: resolve + materialize cleanly.
        await Assert.That(outcome).IsEqualTo("CLEAN").Because(detail);
    }

    [Test]
    public async Task B6Aggregate_In_Where_Probe()
    {
        (string outcome, string detail) = Probe(B6AggregateWhere);
        Console.WriteLine($"[G1/confirm] round.enemies.alive in where: outcome={outcome} detail={detail}");
        // Pre-fix this was BUILD_THROW ("Unknown identifier: round"); the G1 fix makes a B6 aggregate
        // read in a where: resolve + materialize cleanly.
        await Assert.That(outcome).IsEqualTo("CLEAN").Because(detail);
    }

    /// <summary>
    ///     Runs the full resolve + no-demo materialize pipeline and classifies the outcome:
    ///     RESOLVE_ERROR (checker rejects), BUILD_THROW (materialize throws), or CLEAN.
    /// </summary>
    private static (string Outcome, string Detail) Probe(string yaml)
    {
        RulesetDocumentLoader.Outcome loaded = RulesetDocumentLoader.Load(yaml, "g1.rules.yaml");
        if (loaded.Doc is null)
        {
            return ("LOAD_ERROR", string.Join("; ", loaded.Diagnostics));
        }

        RulesetResolveResult resolved = CheckedRulesetDraft.Load(loaded.Doc, _adapter).Build(64.0, Gotv);
        if (!resolved.Success)
        {
            return ("RESOLVE_ERROR", string.Join("; ", resolved.Diagnostics.Select(d => $"{d.Code}: {d.Message}")));
        }

        try
        {
            RuleChainBuilder builder = new(EventRegistry.Build());
            BuildResult build = builder.Build([resolved.Ruleset!]);
            foreach (PerPlayerNodeTemplate template in build.Graph.PerPlayerTemplates)
            {
                // Materialize slot 0 — runs the per-player template lambda that compiles the where:
                // condition via ExpressionCompiler.CompileEventCondition.
                _ = template.Materialize(0, 2, "confirm-probe", null);
            }
        }
        catch (Exception ex)
        {
            return ("BUILD_THROW", $"{ex.GetType().Name}: {ex.Message}");
        }

        return ("CLEAN", "resolved + materialized without throwing");
    }
}
