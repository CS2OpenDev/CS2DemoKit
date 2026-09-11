#region

using CS2DemoKit.Analysis.Yaml;

#endregion

namespace CS2DemoKit.Analysis.Tests;

/// <summary>
///     A scoreboard <c>label:</c> is the value-column key of the per-player metric table, matched
///     ordinally across EVERY ruleset in the load unit. Two entries landing the same label on the
///     same board therefore fight over one column, the later write wins, and the other stat reads
///     as zero with no error anywhere: the <c>Flick</c> column read 0 for weeks that way. The loader
///     now reports the collision as an attributed load error. The pairs below pin the boundary of
///     the check: the same label on the round board and the match board is two columns in two
///     tables, not a collision, which is exactly how the shipped kast and player_stats rulesets share
///     <c>FlashAst</c>, <c>NoScope</c>, <c>WB</c>, <c>Smoke</c> and <c>Shots</c>.
/// </summary>
[Category("Unit")]
public class ScoreboardLabelCollisionTests
{
    private const string MatchStatKills = """
                                          ruleset: a_totals
                                          for: each_player
                                          stats:
                                            total_kills:
                                              count: kill
                                              per: match
                                          show:
                                            scoreboard:
                                              - { stat: total_kills, label: Kills, group: game }
                                          """;

    private const string MatchStatKillsAgain = """
                                               ruleset: b_more_totals
                                               for: each_player
                                               stats:
                                                 enemy_kills:
                                                   count: kill
                                                   per: match
                                               show:
                                                 scoreboard:
                                                   - { stat: enemy_kills, label: Kills, group: game }
                                               """;

    private const string RoundStatKills = """
                                          ruleset: c_per_round
                                          for: each_player
                                          stats:
                                            kills:
                                              count: kill
                                              per: round
                                          show:
                                            scoreboard:
                                              - { stat: kills, label: Kills, group: round }
                                          """;

    private const string RoundStatOnBothBoards = """
                                                 ruleset: d_both_boards
                                                 for: each_player
                                                 stats:
                                                   kills:
                                                     count: kill
                                                     per: round
                                                 show:
                                                   scoreboard:
                                                     - { stat: kills, label: Kills, boards: [round, match] }
                                                 """;

    private const string HighlightCountKills = """
                                               ruleset: e_highlight
                                               for: each_player
                                               stats:
                                                 kills:
                                                   count: kill
                                                   per: round
                                               highlights:
                                                 double:
                                                   when: kills >= 2
                                                   per: round
                                                   title: "{player} doubled"
                                               show:
                                                 scoreboard:
                                                   - { stat: double.count, label: Kills, group: game }
                                               """;

    private const string DisabledMatchStatKills = """
                                                  ruleset: f_disabled
                                                  for: each_player
                                                  enabled: false
                                                  stats:
                                                    total_kills:
                                                      count: kill
                                                      per: match
                                                  show:
                                                    scoreboard:
                                                      - { stat: total_kills, label: Kills, group: game }
                                                  """;

    private const string UnlabelledMatchStat = """
                                               ruleset: g_unlabelled
                                               for: each_player
                                               stats:
                                                 Kills:
                                                   count: kill
                                                   per: match
                                               show:
                                                 scoreboard:
                                                   - { stat: Kills, group: game }
                                               """;

    [Test]
    public async Task SameLabel_SameBoard_AcrossRulesets_IsALoadError()
    {
        RuleConfigLoadResult loaded = YamlConfigLoader.LoadDocuments(
        [
            ("a_totals.rules.yaml", MatchStatKills),
            ("b_more_totals.rules.yaml", MatchStatKillsAgain)
        ]);

        RuleConfigError collision = loaded.Errors.Single();
        await Assert.That(collision.FilePath).IsEqualTo("b_more_totals.rules.yaml")
            .Because("the error is attributed to the later file, which is the one that overwrites the column");
        await Assert.That(collision.ChainId).IsEqualTo("b_more_totals");
        await Assert.That(collision.Message).Contains("Kills");
        await Assert.That(collision.Message).Contains("a_totals");
        await Assert.That(collision.Message).Contains("match");
        await Assert.That(collision.Line).IsNotNull()
            .Because("the entry's own line is what the author fixes");
        await Assert.That(loaded.Rulesets.Count).IsEqualTo(2)
            .Because("both rulesets still load; the error is what blocks the strict paths");
        await Assert.That(loaded.FailedFiles).Contains("b_more_totals.rules.yaml");
        await Assert.That(loaded.LoadedFiles).Contains("a_totals.rules.yaml");
    }

    [Test]
    public async Task SameLabel_DifferentBoards_IsTwoColumns_NotACollision()
    {
        RuleConfigLoadResult loaded = YamlConfigLoader.LoadDocuments(
        [
            ("a_totals.rules.yaml", MatchStatKills),
            ("c_per_round.rules.yaml", RoundStatKills)
        ]);

        await Assert.That(loaded.Errors).IsEmpty()
            .Because("the round scoreboard and the match scoreboard are separate tables with separate column keys");
    }

    [Test]
    public async Task BoardsOverride_PutsARoundStatOnTheMatchBoard_AndCollidesThere()
    {
        RuleConfigLoadResult loaded = YamlConfigLoader.LoadDocuments(
        [
            ("a_totals.rules.yaml", MatchStatKills),
            ("d_both_boards.rules.yaml", RoundStatOnBothBoards)
        ]);

        RuleConfigError collision = loaded.Errors.Single();
        await Assert.That(collision.ChainId).IsEqualTo("d_both_boards");
        await Assert.That(collision.Message).Contains("match");
    }

    [Test]
    public async Task HighlightCountRef_LandsOnTheMatchBoard_AndCollidesWithAMatchStat()
    {
        // The Flick incident: a highlight's .count column colliding with a match-scoped stat's label.
        RuleConfigLoadResult loaded = YamlConfigLoader.LoadDocuments(
        [
            ("a_totals.rules.yaml", MatchStatKills),
            ("e_highlight.rules.yaml", HighlightCountKills)
        ]);

        RuleConfigError collision = loaded.Errors.Single();
        await Assert.That(collision.ChainId).IsEqualTo("e_highlight");
    }

    [Test]
    public async Task DisabledRuleset_ContributesNoColumns_SoItCannotCollide()
    {
        RuleConfigLoadResult loaded = YamlConfigLoader.LoadDocuments(
        [
            ("a_totals.rules.yaml", MatchStatKills),
            ("f_disabled.rules.yaml", DisabledMatchStatKills)
        ]);

        await Assert.That(loaded.Errors).IsEmpty();
    }

    [Test]
    public async Task MissingLabel_FallsBackToTheStatId_WhichIsStillAColumnKey()
    {
        RuleConfigLoadResult loaded = YamlConfigLoader.LoadDocuments(
        [
            ("a_totals.rules.yaml", MatchStatKills),
            ("g_unlabelled.rules.yaml", UnlabelledMatchStat)
        ]);

        RuleConfigError collision = loaded.Errors.Single();
        await Assert.That(collision.ChainId).IsEqualTo("g_unlabelled");
    }

    [Test]
    public async Task SameLabel_TwiceInOneRuleset_SameBoard_IsAlsoACollision()
    {
        const string twice = """
                             ruleset: h_twice
                             for: each_player
                             stats:
                               a:
                                 count: kill
                                 per: match
                               b:
                                 count: death
                                 per: match
                             show:
                               scoreboard:
                                 - { stat: a, label: Same, group: game }
                                 - { stat: b, label: Same, group: game }
                             """;

        RuleConfigLoadResult loaded = YamlConfigLoader.LoadDocuments([("h_twice.rules.yaml", twice)]);

        await Assert.That(loaded.Errors.Count).IsEqualTo(1);
        await Assert.That(loaded.Errors[0].ChainId).IsEqualTo("h_twice");
    }

    [Test]
    public async Task ShippedRulesets_ShareNoLabelOnAnyBoard()
    {
        // A control, not evidence the check exists: it passes with the loader change reverted.
        RuleConfigLoadResult shipped = YamlConfigLoader.LoadShippedEmbedded();

        await Assert.That(shipped.Errors).IsEmpty()
            .Because("the shipped set is the one every consumer loads together");
    }

    // ── The overlay entry points: the merge is a new load unit ─────────────────────────
    //
    // Each tier is checked on its own, then the merged set is checked again, because a user column
    // can only collide with a shipped one once the two are together. `TotalK` is player_stats'
    // match-board column (TotalEnemyKills, per: match).

    private const string UserTotalK = """
                                      ruleset: my_totals
                                      for: each_player
                                      stats:
                                        my_kills:
                                          count: kill
                                          per: match
                                      show:
                                        scoreboard:
                                          - { stat: my_kills, label: TotalK, group: game }
                                      """;

    [Test]
    public async Task ShippedOverlay_UserLabelOnAShippedColumn_IsReportedAgainstTheUserFile()
    {
        RuleConfigLoadResult loaded = YamlConfigLoader.LoadShippedWithOverlay([("my_totals.rules.yaml", UserTotalK)]);

        RuleConfigError collision = loaded.Errors.Single();
        await Assert.That(collision.ChainId).IsEqualTo("my_totals");
        await Assert.That(collision.FilePath).IsEqualTo("my_totals.rules.yaml");
        await Assert.That(collision.Message).Contains("player_stats");
        await Assert.That(loaded.FailedFiles).Contains("my_totals.rules.yaml");
        await Assert.That(loaded.LoadedFiles).DoesNotContain("my_totals.rules.yaml");
    }

    [Test]
    public async Task ShippedOverlay_UserOverrideOfAnEarlierShippedId_IsStillTheUsersError()
    {
        // `kast` sorts before `player_stats`, so an override of it sits EARLIER in merged order than
        // the shipped column it collides with. Order-of-appearance attribution would blame the
        // shipped file; the shipped tier is known clean and the user file is the one that changed.
        const string overrideKast = """
                                    ruleset: kast
                                    for: each_player
                                    stats:
                                      my_kills:
                                        count: kill
                                        per: match
                                    show:
                                      scoreboard:
                                        - { stat: my_kills, label: TotalK, group: game }
                                    """;

        RuleConfigLoadResult loaded = YamlConfigLoader.LoadShippedWithOverlay([("my_kast.rules.yaml", overrideKast)]);

        RuleConfigError collision = loaded.Errors.Single();
        await Assert.That(collision.ChainId).IsEqualTo("kast");
        await Assert.That(collision.FilePath).IsEqualTo("my_kast.rules.yaml");
        await Assert.That(collision.Message).Contains("player_stats");
        await Assert.That(loaded.FailedFiles).Contains("my_kast.rules.yaml");
        await Assert.That(loaded.LoadedFiles).Contains("player_stats.rules.yaml")
            .Because("a shipped file is never the one at fault in an overlay");
    }

    [Test]
    public async Task ShippedOverlay_UserVsUserCollision_IsReportedOnce()
    {
        RuleConfigLoadResult loaded = YamlConfigLoader.LoadShippedWithOverlay(
        [
            ("a_totals.rules.yaml", MatchStatKills),
            ("b_more_totals.rules.yaml", MatchStatKillsAgain)
        ]);

        await Assert.That(loaded.Errors.Count).IsEqualTo(1)
            .Because("the user tier already checked these two together; the merge adds only cross-tier collisions");
        await Assert.That(loaded.Errors[0].ChainId).IsEqualTo("b_more_totals");
    }

    [Test]
    public async Task DirectoryOverlay_UserLabelOnAShippedColumn_IsALoadError()
    {
        using TempDir shipped = new(("a_totals.rules.yaml", MatchStatKills));
        using TempDir user = new(("b_more_totals.rules.yaml", MatchStatKillsAgain));

        RuleConfigLoadResult loaded = YamlConfigLoader.LoadWithOverlay(shipped.Path, user.Path);

        RuleConfigError collision = loaded.Errors.Single();
        await Assert.That(collision.ChainId).IsEqualTo("b_more_totals");
        await Assert.That(Path.GetFileName(collision.FilePath)).IsEqualTo("b_more_totals.rules.yaml");
        await Assert.That(collision.Message).Contains("a_totals");
        await Assert.That(loaded.FailedFiles.Select(f => Path.GetFileName(f) ?? "")).Contains("b_more_totals.rules.yaml");
        await Assert.That(loaded.LoadedFiles.Select(f => Path.GetFileName(f) ?? "")).Contains("a_totals.rules.yaml");
    }

    private sealed class TempDir : IDisposable
    {
        public TempDir(params (string Name, string Content)[] files)
        {
            Path = Directory.CreateTempSubdirectory("cs2demokit-label-collision-").FullName;
            foreach ((string name, string content) in files)
            {
                File.WriteAllText(System.IO.Path.Combine(Path, name), content);
            }
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, true);
            }
            catch (IOException)
            {
                // A leaked temp directory is not a test failure.
            }
        }
    }
}
