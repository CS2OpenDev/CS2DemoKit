#region

using CS2DemoKit.Analysis.Catalog;
using CS2DemoKit.Analysis.Config;
using CS2DemoKit.Analysis.RulesetsV2.Compile;
using CS2DemoKit.Analysis.RulesetsV2.Model;
using CS2DemoKit.Analysis.RulesetsV2.Resolve;
using CS2DemoKit.Analysis.Yaml;

#endregion

namespace CS2DemoKit.Analysis.Tests;

/// <summary>
///     The <c>show: tables:</c> half of the show-column collision check. A table column's key is the
///     same thing a scoreboard label is — <c>ConfiguredOutputProjector</c> writes
///     <c>values[MetricRef.Label]</c> into a dictionary — so two columns of one table sharing a
///     <c>label:</c> lose one stat to the other, and the duplicate key is emitted twice in the header
///     on top of that, so the CSV carries two identically named columns both holding the later
///     stat's value. Nothing reported it: <see cref="ReproducesWithoutTheGuard_TwoColumnsOneLabel" />
///     pins that the lowering happily builds the duplicate, which is why the loader has to.
///     <para>
///         Table NAMES collide differently and are checked separately: the projector emits both
///         tables, so nothing is overwritten in-process, but they come out under one name and a
///         consumer that addresses tables by name gets whichever one it happens to find.
///     </para>
/// </summary>
[Category("Unit")]
public class ShowTableColumnCollisionTests
{
    private static readonly string[] _oneColumnKeyTwice = ["K", "K"];

    private static readonly string[] _twoDistinctStats = ["d_dup_column.kills", "d_dup_column.deaths"];

    private const string TableA = """
                                  ruleset: a_duels
                                  for: each_player
                                  stats:
                                    kills:
                                      count: kill
                                      per: match
                                  show:
                                    tables:
                                      duels:
                                        per: player_match
                                        columns:
                                          - { stat: kills, label: K }
                                  """;

    private const string TableSameNameSameLabel = """
                                                  ruleset: b_duels
                                                  for: each_player
                                                  stats:
                                                    deaths:
                                                      count: death
                                                      per: match
                                                  show:
                                                    tables:
                                                      duels:
                                                        per: player_match
                                                        columns:
                                                          - { stat: deaths, label: K }
                                                  """;

    private const string TableOtherNameSameLabel = """
                                                   ruleset: c_trades
                                                   for: each_player
                                                   stats:
                                                     deaths:
                                                       count: death
                                                       per: match
                                                   show:
                                                     tables:
                                                       trades:
                                                         per: player_match
                                                         columns:
                                                           - { stat: deaths, label: K }
                                                   """;

    private const string TableTwoColumnsOneLabel = """
                                                   ruleset: d_dup_column
                                                   for: each_player
                                                   stats:
                                                     kills:
                                                       count: kill
                                                       per: match
                                                     deaths:
                                                       count: death
                                                       per: match
                                                   show:
                                                     tables:
                                                       duels:
                                                         per: player_match
                                                         columns:
                                                           - { stat: kills,  label: K }
                                                           - { stat: deaths, label: K }
                                                   """;

    private const string TableUnlabelledStatId = """
                                                 ruleset: e_unlabelled
                                                 for: each_player
                                                 stats:
                                                   K:
                                                     count: kill
                                                     per: match
                                                 show:
                                                   tables:
                                                     duels:
                                                       per: player_match
                                                       columns:
                                                         - { stat: K }
                                                 """;

    private const string ScoreboardKills = """
                                           ruleset: f_scoreboard
                                           for: each_player
                                           stats:
                                             total_kills:
                                               count: kill
                                               per: match
                                           show:
                                             scoreboard:
                                               - { stat: total_kills, label: K, group: game }
                                           """;

    [Test]
    public async Task TwoColumnsOfOneTable_SharingALabel_IsALoadError()
    {
        RuleConfigLoadResult loaded = YamlConfigLoader.LoadDocuments(
            [("d_dup_column.rules.yaml", TableTwoColumnsOneLabel)]);

        RuleConfigError collision = loaded.Errors.Single();
        await Assert.That(collision.ChainId).IsEqualTo("d_dup_column");
        await Assert.That(collision.Message).Contains("duels");
        await Assert.That(collision.Message).Contains("'K'");
        await Assert.That(collision.Line).IsNotNull()
            .Because("the colliding column's own line is what the author fixes");
    }

    [Test]
    public async Task SameTableName_SameColumnLabel_AcrossRulesets_IsALoadError()
    {
        RuleConfigLoadResult loaded = YamlConfigLoader.LoadDocuments(
        [
            ("a_duels.rules.yaml", TableA),
            ("b_duels.rules.yaml", TableSameNameSameLabel)
        ]);

        // The name clash is reported once; the column clash under it would only say it again.
        RuleConfigError collision = loaded.Errors.Single();
        await Assert.That(collision.FilePath).IsEqualTo("b_duels.rules.yaml");
        await Assert.That(collision.ChainId).IsEqualTo("b_duels");
        await Assert.That(collision.Message).Contains("duels");
        await Assert.That(collision.Message).Contains("a_duels");
    }

    [Test]
    public async Task SameColumnLabel_InDifferentlyNamedTables_IsTwoColumns_NotACollision()
    {
        RuleConfigLoadResult loaded = YamlConfigLoader.LoadDocuments(
        [
            ("a_duels.rules.yaml", TableA),
            ("c_trades.rules.yaml", TableOtherNameSameLabel)
        ]);

        await Assert.That(loaded.Errors).IsEmpty()
            .Because("each table is its own export with its own column keys");
    }

    [Test]
    public async Task ATableColumn_DoesNotCollideWithAScoreboardLabel()
    {
        RuleConfigLoadResult loaded = YamlConfigLoader.LoadDocuments(
        [
            ("a_duels.rules.yaml", TableA),
            ("f_scoreboard.rules.yaml", ScoreboardKills)
        ]);

        await Assert.That(loaded.Errors).IsEmpty()
            .Because("a custom table and the match scoreboard are different tables");
    }

    [Test]
    public async Task MissingLabel_FallsBackToTheStatId_WhichIsStillAColumnKey()
    {
        RuleConfigLoadResult loaded = YamlConfigLoader.LoadDocuments(
        [
            ("a_duels.rules.yaml", TableA),
            ("e_unlabelled.rules.yaml", TableUnlabelledStatId)
        ]);

        // Same table name, and the unlabelled column's key is its stat id `K` — the same column.
        RuleConfigError collision = loaded.Errors.Single();
        await Assert.That(collision.ChainId).IsEqualTo("e_unlabelled");
    }

    [Test]
    public async Task ShippedOverlay_UserTableNameOnAShippedTable_IsReportedAgainstTheUserFile()
    {
        const string userKastTotals = """
                                      ruleset: my_kast
                                      for: each_player
                                      stats:
                                        my_kills:
                                          count: kill
                                          per: match
                                      show:
                                        tables:
                                          kast_game_totals:
                                            per: player_match
                                            columns:
                                              - { stat: my_kills, label: MyK }
                                      """;

        RuleConfigLoadResult loaded =
            YamlConfigLoader.LoadShippedWithOverlay([("my_kast.rules.yaml", userKastTotals)]);

        RuleConfigError collision = loaded.Errors.Single();
        await Assert.That(collision.ChainId).IsEqualTo("my_kast");
        await Assert.That(collision.Message).Contains("kast_game_totals");
        await Assert.That(loaded.FailedFiles).Contains("my_kast.rules.yaml");
    }

    // ── The scoreboard entries the check used to walk past ─────────────────────────────
    //
    // `TryResolveScoreboardBoards` classifies an entry onto its board(s). It could fail, and the
    // caller skipped those entries on the theory that something downstream reported them. Nothing
    // on the load path does: an unknown ref is caught at RESOLVE (ShowReferenceValidator, which
    // needs a checked ruleset), and a bad `boards:` value is caught nowhere before ShowLowering
    // throws mid-build.

    [Test]
    public async Task ScoreboardEntry_ReferencingNothing_IsALoadError()
    {
        const string badRef = """
                              ruleset: g_bad_ref
                              for: each_player
                              stats:
                                kills:
                                  count: kill
                                  per: match
                              show:
                                scoreboard:
                                  - { stat: nope, label: K, group: game }
                              """;

        RuleConfigLoadResult loaded = YamlConfigLoader.LoadDocuments([("g_bad_ref.rules.yaml", badRef)]);

        RuleConfigError error = loaded.Errors.Single();
        await Assert.That(error.ChainId).IsEqualTo("g_bad_ref");
        await Assert.That(error.Message).Contains("nope");
        await Assert.That(error.Message).Contains("neither a stat, a highlight, nor a tally target");
        await Assert.That(loaded.FailedFiles).Contains("g_bad_ref.rules.yaml");
    }

    [Test]
    public async Task ScoreboardEntry_WithAnUnusableBoard_IsALoadError()
    {
        const string badBoard = """
                                ruleset: h_bad_board
                                for: each_player
                                stats:
                                  kills:
                                    count: kill
                                    per: match
                                show:
                                  scoreboard:
                                    - { stat: kills, label: K, boards: [overall] }
                                """;

        RuleConfigLoadResult loaded = YamlConfigLoader.LoadDocuments([("h_bad_board.rules.yaml", badBoard)]);

        RuleConfigError error = loaded.Errors.Single();
        await Assert.That(error.ChainId).IsEqualTo("h_bad_board");
        await Assert.That(error.Message).Contains("overall");
        await Assert.That(error.Message).Contains("round | match");
    }

    /// <summary>
    ///     The duplicate the guard exists for, shown reaching the lowering intact: two
    ///     <see cref="MetricRef" />s under one <see cref="MetricRef.Label" />, which
    ///     <c>ConfiguredOutputProjector</c> then writes into one dictionary slot. Resolve reports
    ///     nothing, so without the load error this ships.
    /// </summary>
    [Test]
    public async Task ReproducesWithoutTheGuard_TwoColumnsOneLabel()
    {
        RulesetDoc doc = RulesetDocumentLoader.Load(TableTwoColumnsOneLabel, "d_dup_column.rules.yaml").Doc
                         ?? throw new InvalidOperationException("test ruleset failed to map");
        RulesetResolveResult resolved = CheckedRulesetDraft
            .Load(doc, CatalogScopeAdapter.From(CatalogResource.Load()))
            .Build(64.0, "Cs2GotvProfile");

        await Assert.That(resolved.Diagnostics).IsEmpty()
            .Because("the resolver checks that show refs exist, not that their column keys are distinct");

        OutputDef duels = ShowLowering.LowerTables(resolved.Ruleset!).Single();
        await Assert.That(duels.Metrics.Select(m => m.Label).ToList()).IsEquivalentTo(_oneColumnKeyTwice);
        await Assert.That(duels.Metrics.Select(m => m.RuleRef).ToList())
            .IsEquivalentTo(_twoDistinctStats)
            .Because("two distinct stats, one column key — the second write is the one that survives");
    }

    [Test]
    public async Task ShippedRulesets_ShareNoTableNameAndNoColumnKey()
    {
        RuleConfigLoadResult shipped = YamlConfigLoader.LoadShippedEmbedded();

        await Assert.That(shipped.Errors).IsEmpty()
            .Because("the shipped set is the one every consumer loads together");
    }
}
