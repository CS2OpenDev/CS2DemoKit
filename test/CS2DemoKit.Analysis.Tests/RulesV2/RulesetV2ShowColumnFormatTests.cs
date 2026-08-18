#region

using CS2DemoKit.Analysis.Building;
using CS2DemoKit.Analysis.Catalog;
using CS2DemoKit.Analysis.Config;
using CS2DemoKit.Analysis.Graphs;
using CS2DemoKit.Analysis.Output;
using CS2DemoKit.Analysis.Plugins;
using CS2DemoKit.Analysis.Registry;
using CS2DemoKit.Analysis.RulesetsV2.Compile;
using CS2DemoKit.Analysis.RulesetsV2.Model;
using CS2DemoKit.Analysis.RulesetsV2.Resolve;
using CS2DemoKit.Analysis.Yaml;

#endregion

namespace CS2DemoKit.Analysis.Tests.RulesV2;

/// <summary>
///     <c>show: as: ticks|seconds|time</c> column display formatting. The
///     <c>as:</c> key threads through the mapper onto scoreboard columns
///     (<see cref="PerPlayerColumnAssignment.Format" />) and table columns
///     (<see cref="MetricRef.Format" />), and the projector-shared
///     <see cref="StatValues.ApplyColumnFormat" /> reshapes a tick-valued cell at the demo's tick
///     rate. A column with no <c>as:</c> is byte-identical to today.
/// </summary>
[Category("Unit")]
public class RulesetV2ShowColumnFormatTests
{
    private const string FmtProbe = """
                                    ruleset: fmt_probe
                                    for: each_player
                                    stats:
                                      plant_tick:
                                        capture: event.tick
                                        on: bomb_planted
                                        keep: last
                                        per: round
                                    show:
                                      scoreboard:
                                        - { stat: plant_tick, label: PlantSec, as: seconds }
                                      tables:
                                        ctx:
                                          per: player_round
                                          columns:
                                            - { stat: plant_tick, label: PlantTime, as: time }
                                            - { stat: plant_tick, label: PlantRaw }
                                    """;

    // ── Formatter semantics (pure) ─────────────────────────────────────────────

    /// <summary>5760 ticks at 64 t/s: seconds = 90.0, time = "1:30", ticks = 5760 (integer).</summary>
    [Test]
    public async Task ApplyColumnFormat_ReshapesTickValueAtRate()
    {
        await Assert.That(StatValues.ApplyColumnFormat(5760, ColumnValueFormat.Seconds, 64)).IsEqualTo(90.0);
        await Assert.That(StatValues.ApplyColumnFormat(5760, ColumnValueFormat.Time, 64)).IsEqualTo("1:30");
        await Assert.That(StatValues.ApplyColumnFormat(5760, ColumnValueFormat.Ticks, 64)).IsEqualTo(5760L);
    }

    /// <summary><c>None</c> and non-numeric / null values pass through unchanged (byte-identical).</summary>
    [Test]
    public async Task ApplyColumnFormat_NoneOrNonNumeric_Unchanged()
    {
        await Assert.That(StatValues.ApplyColumnFormat(5760, ColumnValueFormat.None, 64)).IsEqualTo(5760);
        await Assert.That(StatValues.ApplyColumnFormat("AWP", ColumnValueFormat.Seconds, 64)).IsEqualTo("AWP");
        await Assert.That(StatValues.ApplyColumnFormat(null, ColumnValueFormat.Time, 64)).IsNull();
    }

    // ── as: threads through the mapper + lowering ──────────────────────────────

    /// <summary>The scoreboard column's <c>as: seconds</c> reaches its <see cref="PerPlayerColumnAssignment.Format" />.</summary>
    [Test]
    public async Task Scoreboard_As_ThreadsToColumnFormat()
    {
        PerPlayerColumnAssignment plantSec = Columns(Compile(FmtProbe)).Single(c => c.ColumnName == "PlantSec");
        await Assert.That(plantSec.Format).IsEqualTo(ColumnValueFormat.Seconds);
    }

    /// <summary>Table columns thread <c>as:</c> onto the metric ref; a column without <c>as:</c> stays <c>None</c>.</summary>
    [Test]
    public async Task Table_As_ThreadsToMetricFormat()
    {
        OutputDef ctx = ShowLowering.LowerTables(Compile(FmtProbe)).Single(o => o.Id == "ctx");

        MetricRef plantTime = ctx.Metrics.Single(m => m.Label == "PlantTime");
        MetricRef plantRaw = ctx.Metrics.Single(m => m.Label == "PlantRaw");
        await Assert.That(plantTime.Format).IsEqualTo(ColumnValueFormat.Time);
        await Assert.That(plantRaw.Format).IsEqualTo(ColumnValueFormat.None);
    }

    /// <summary>The un-reserve is additive: the <c>as:</c> keys load with no diagnostics.</summary>
    [Test]
    public async Task As_LoadsCleanNotRejected()
    {
        RulesetDocumentLoader.Outcome outcome = RulesetDocumentLoader.Load(FmtProbe, null);
        await Assert.That(outcome.Diagnostics).IsEmpty();
    }

    private static CheckedRuleset Compile(string yaml)
    {
        RulesetDoc doc = RulesetDocumentLoader.Load(yaml, "test.rules.yaml").Doc
                         ?? throw new InvalidOperationException("test ruleset failed to map");
        CatalogScopeAdapter adapter = CatalogScopeAdapter.From(CatalogResource.Load());
        RulesetResolveResult resolved = CheckedRulesetDraft.Load(doc, adapter).Build(64.0, "Cs2GotvProfile");
        return resolved.Ruleset
               ?? throw new InvalidOperationException(
                   "test ruleset failed to resolve: " + string.Join("; ", resolved.Diagnostics));
    }

    private static List<PerPlayerColumnAssignment> Columns(CheckedRuleset rs)
    {
        RuleChainBuilder builder = new(
            EventRegistry.Build(),
            null,
            entityProviders: EntityValueProviderRegistry.CreateDefault(),
            perPlayerEntityProviders: PerPlayerEntityValueProviderRegistry.CreateDefault());
        BuildResult build = builder.Build([rs]);

        List<PerPlayerColumnAssignment> merged = [];
        foreach (PerPlayerNodeTemplate template in build.Graph.PerPlayerTemplates)
        {
            PerPlayerNodeTemplate.MaterializedPlayer player = template.Materialize(0, 0, "test", null);
            merged.AddRange(player.ColumnAssignments);
        }

        return merged;
    }
}
