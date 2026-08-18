#region

using CS2DemoKit.Analysis.Building;
using CS2DemoKit.Analysis.Catalog;
using CS2DemoKit.Analysis.Config;
using CS2DemoKit.Analysis.Graphs;
using CS2DemoKit.Analysis.Plugins;
using CS2DemoKit.Analysis.Registry;
using CS2DemoKit.Analysis.RulesetsV2.Model;
using CS2DemoKit.Analysis.RulesetsV2.Resolve;
using CS2DemoKit.Analysis.Yaml;

#endregion

namespace CS2DemoKit.Analysis.Tests.RulesV2;

/// <summary>
///     <c>show: scoreboard:</c> projection: a scoreboard entry lowers
///     to a per-player <see cref="PerPlayerColumnAssignment" /> whose board is inferred from the
///     referenced node's scope. A highlight ref surfaces the match-scoped auto <c>&lt;id&gt;.count</c>
///     node and always defaults to the <b>match</b> board; a plain <c>per: round</c> stat ref
///     defaults to the <b>round</b> board (the projectors split tables on
///     <see cref="PerPlayerColumnAssignment.IsRoundScoped" />). Demo-free — the per-player template
///     materializes against a null demo.
/// </summary>
[Category("Unit")]
public class RulesetV2ShowScoreboardTests
{
    private const string ProbeYaml = """
                                     ruleset: sb_probe
                                     for: each_player
                                     stats:
                                       round_kills:
                                         count: kill
                                         per: round
                                     highlights:
                                       multi:
                                         when: round_kills >= 2
                                         per: round
                                         title: "multi"
                                     show:
                                       scoreboard:
                                         - { stat: multi.count, label: Multis, group: objectives }
                                         - { stat: round_kills, label: RoundKills }
                                     """;

    private const string TallyProbeYaml = """
                                          ruleset: sb_tally_probe
                                          for: each_player
                                          stats:
                                            round_kills:
                                              count: kill
                                              per: round
                                            multi_tally:
                                              tally: round_kills
                                              thresholds:
                                                - { min: 2, target: rounds_2k }
                                              per: match
                                          show:
                                            scoreboard:
                                              - { stat: rounds_2k, label: "2K", group: game }
                                          """;

    /// <summary>
    ///     The highlight <c>.count</c> ref projects to the match board with its label + group; the
    ///     plain <c>per: round</c> stat ref projects to the round board.
    /// </summary>
    [Test]
    public async Task Scoreboard_HighlightCountIsMatchBoard_PlainPerRoundStatIsRoundBoard()
    {
        IReadOnlyList<PerPlayerColumnAssignment> columns = Columns(Compile(ProbeYaml));

        PerPlayerColumnAssignment multis = columns.Single(c => c.ColumnName == "Multis");
        await Assert.That(multis.IsRoundScoped).IsFalse()
            .Because("a highlight ref surfaces the match-scoped .count node — it defaults to the match board");
        await Assert.That(multis.GroupName).IsEqualTo("objectives");
        await Assert.That(multis.ChainId).IsEqualTo("_chain_sb_probe")
            .Because("the column carries its owning ruleset's join key for the graph-filter feature");

        PerPlayerColumnAssignment roundKills = columns.Single(c => c.ColumnName == "RoundKills");
        await Assert.That(roundKills.IsRoundScoped).IsTrue()
            .Because("a plain per: round stat ref defaults from its per: to the round board");
    }

    /// <summary>
    ///     The shipped pilot's scoreboard entry (<c>post_plant_double.count → PostPlantDoubles</c>,
    ///     group objectives) projects to the match board with the right column — the contract-named
    ///     case.
    /// </summary>
    [Test]
    public async Task Scoreboard_PilotPostPlantDoubleCount_ProjectsToMatchBoard()
    {
        string yaml = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "CS2DemoKit.Analysis", "Rules", "post_plant_double.rules.yaml"));
        IReadOnlyList<PerPlayerColumnAssignment> columns = Columns(Compile(yaml));

        PerPlayerColumnAssignment doubles = columns.Single(c => c.ColumnName == "PostPlantDoubles");
        await Assert.That(doubles.IsRoundScoped).IsFalse()
            .Because("post_plant_double.count is the match-scoped highlight count node");
        await Assert.That(doubles.GroupName).IsEqualTo("objectives");
        await Assert.That(doubles.ChainId).IsEqualTo("_chain_post_plant_double");
    }

    /// <summary>
    ///     A scoreboard entry naming a <c>tally:</c> threshold <c>target:</c> (which is an emit node,
    ///     not a stat/highlight) resolves to that node on the board of the owning tally's scope
    ///     (a <c>per: match</c> tally ⇒ match board) — the v1-parity path for 2K/3K/4K/5K columns.
    /// </summary>
    [Test]
    public async Task Scoreboard_TallyTargetRef_ProjectsToOwningTallyBoard()
    {
        IReadOnlyList<PerPlayerColumnAssignment> columns = Columns(Compile(TallyProbeYaml));

        PerPlayerColumnAssignment twoK = columns.Single(c => c.ColumnName == "2K");
        await Assert.That(twoK.IsRoundScoped).IsFalse()
            .Because("rounds_2k is the emit node of a per: match tally — it defaults to the match board");
        await Assert.That(twoK.GroupName).IsEqualTo("game");
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

    /// <summary>Materializes the v2 per-player template against a null demo and returns its column assignments.</summary>
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

    private static string FindRepoRoot()
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
