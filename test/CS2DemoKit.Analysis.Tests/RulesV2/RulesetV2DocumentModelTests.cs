#region

using CS2DemoKit.Analysis.Config;
using CS2DemoKit.Analysis.RulesetsV2.Model;
using CS2DemoKit.Analysis.Yaml;

#endregion

namespace CS2DemoKit.Analysis.Tests.RulesV2;

/// <summary>
///     Document-model battery: the post-plant-double centerpiece (docs/rules-v2/rule-authoring-ux-review.md
///     §1.4) maps to the expected <see cref="RulesetDoc" />, the loader routes
///     <c>ruleset:</c> files to the v2 pipeline, and a retired-v1 <c>chains:</c>/<c>outputs:</c>
///     file fails loudly with the retired-format error. Demo-free; pure config-layer.
/// </summary>
[Category("Unit")]
public class RulesetV2DocumentModelTests
{
    private const string PostPlantDouble = """
                                           ruleset: post_plant_double
                                           title: Post-Plant Double
                                           summary: 2+ enemy kills after the bomb plant in one round, with clip-ready tick context.
                                           for: each_player

                                           params:
                                             min_kills: { type: int, default: 2, min: 2, max: 5 }

                                           define:
                                             post_plant_kill:
                                               on: kill
                                               match: { enemy: true }
                                               while: round.bomb.was_planted

                                           stats:
                                             plant_tick:
                                               capture: event.tick
                                               on: bomb_planted
                                               per: round

                                             post_plant_kills:
                                               count: post_plant_kill
                                               per: round

                                             kill_ticks:
                                               capture: event.tick
                                               on: post_plant_kill
                                               keep: list
                                               per: round

                                           highlights:
                                             post_plant_double:
                                               when: post_plant_kills >= params.min_kills
                                               per: round
                                               title: "{player.name} - {post_plant_kills} kills after the plant (round {round.number})"

                                           show:
                                             scoreboard:
                                               - { stat: post_plant_double.count, label: PostPlantDoubles, group: objectives }
                                             tables:
                                               post_plant_double_context:
                                                 per: player_round
                                                 columns:
                                                   - { stat: post_plant_double,  label: Achieved }
                                                   - { stat: post_plant_kills,   label: PostPlantKills }
                                                   - { stat: plant_tick,         label: PlantTick }
                                                   - { stat: kill_ticks,         label: KillTick }
                                           """;

    private const string V1Chain = """
                                   chains:
                                     - id: kills
                                       scope: game
                                       rules:
                                         - id: total_kills
                                           type: counter
                                           triggers:
                                             - on: player_death
                                               action: increment
                                   """;

    private static RulesetDoc LoadDoc(string yaml)
    {
        RulesetDocumentLoader.Outcome outcome = RulesetDocumentLoader.Load(yaml, null);
        return outcome.Doc ?? throw new InvalidOperationException(
            $"expected a mapped ruleset; diagnostics: {string.Join(" | ", outcome.Diagnostics)}");
    }

    // ── Header + params ────────────────────────────────────────────────────────

    /// <summary>The document header maps: id, for-scope, and the enabled default.</summary>
    [Test]
    public async Task PostPlant_Header_Maps()
    {
        RulesetDocumentLoader.Outcome outcome = RulesetDocumentLoader.Load(PostPlantDouble, null);
        await Assert.That(outcome.Diagnostics).IsEmpty();

        RulesetDoc doc = outcome.Doc!;
        await Assert.That(doc.Id).IsEqualTo("post_plant_double");
        await Assert.That(doc.Title).IsEqualTo("Post-Plant Double");
        await Assert.That(doc.For).IsEqualTo(RulesetScope.EachPlayer);
        await Assert.That(doc.Enabled).IsTrue();
    }

    /// <summary>
    ///     Provenance fields (<c>catalog_version</c> / <c>min_app_version</c>) load clean — no
    ///     unknown-key / reserved rejection — and store their free-form strings.
    /// </summary>
    [Test]
    public async Task Provenance_CatalogAndMinAppVersion_LoadCleanAndStore()
    {
        const string Yaml = """
                            ruleset: prov
                            for: each_player
                            catalog_version: "2026.07.1"
                            min_app_version: "0.0.4"
                            stats:
                              k:
                                count: kill
                                per: round
                            """;

        RulesetDocumentLoader.Outcome outcome = RulesetDocumentLoader.Load(Yaml, null);
        await Assert.That(outcome.Diagnostics).IsEmpty()
            .Because("catalog_version / min_app_version are accepted provenance metadata, not reserved rejections");

        RulesetDoc doc = outcome.Doc!;
        await Assert.That(doc.CatalogVersion).IsEqualTo("2026.07.1");
        await Assert.That(doc.MinAppVersion).IsEqualTo("0.0.4");
    }

    /// <summary>The <c>min_kills</c> param maps its type, coerced default, and bounds.</summary>
    [Test]
    public async Task PostPlant_Param_Maps()
    {
        RulesetDoc doc = LoadDoc(PostPlantDouble);
        await Assert.That(doc.Params.Count).IsEqualTo(1);

        ParamDef p = doc.Params[0];
        await Assert.That(p.Name).IsEqualTo("min_kills");
        await Assert.That(p.Type).IsEqualTo(ParamType.Int);
        await Assert.That(p.Default).IsEqualTo(2L);
        await Assert.That(p.Min).IsEqualTo(2d);
        await Assert.That(p.Max).IsEqualTo(5d);
    }

    // ── Define ─────────────────────────────────────────────────────────────────

    /// <summary>The trigger-bodied <c>post_plant_kill</c> define maps its on / match / while.</summary>
    [Test]
    public async Task PostPlant_TriggerDefine_Maps()
    {
        RulesetDoc doc = LoadDoc(PostPlantDouble);
        await Assert.That(doc.Defines.Count).IsEqualTo(1);

        DefineDef define = doc.Defines[0];
        await Assert.That(define.Name).IsEqualTo("post_plant_kill");
        TriggerDefineBody body = (TriggerDefineBody)define.Body;
        await Assert.That(body.Trigger.On!.Kind).IsEqualTo(TriggerRefKind.ViewOrDefine);
        await Assert.That(body.Trigger.On!.Name).IsEqualTo("kill");
        await Assert.That(body.Trigger.While).IsEqualTo("round.bomb.was_planted");

        await Assert.That(body.Trigger.Match.Count).IsEqualTo(1);
        MatchBinding binding = body.Trigger.Match[0];
        await Assert.That(binding.Key).IsEqualTo("enemy");
        LiteralTest literal = (LiteralTest)binding.Test;
        await Assert.That(literal.RawText).IsEqualTo("true");
        await Assert.That(literal.Kind).IsEqualTo(ScalarKind.Bool);
    }

    // ── Stats ──────────────────────────────────────────────────────────────────

    /// <summary>The three stats map their kind, kind-argument text, per-scope, keep, and on-trigger.</summary>
    [Test]
    public async Task PostPlant_Stats_Map()
    {
        RulesetDoc doc = LoadDoc(PostPlantDouble);
        string[] expectedStatIds = ["plant_tick", "post_plant_kills", "kill_ticks"];
        await Assert.That(doc.Stats.Select(s => s.Id)).IsEquivalentTo(expectedStatIds);

        StatDef plantTick = doc.Stats[0];
        await Assert.That(plantTick.Kind).IsEqualTo(StatKind.Capture);
        await Assert.That(plantTick.KindArg).IsEqualTo("event.tick");
        await Assert.That(plantTick.Per).IsEqualTo(PerScope.Round);
        await Assert.That(plantTick.Trigger!.On!.Name).IsEqualTo("bomb_planted");

        StatDef postPlantKills = doc.Stats[1];
        await Assert.That(postPlantKills.Kind).IsEqualTo(StatKind.Count);
        await Assert.That(postPlantKills.KindArg).IsEqualTo("post_plant_kill");
        await Assert.That(postPlantKills.Trigger).IsNull();

        StatDef killTicks = doc.Stats[2];
        await Assert.That(killTicks.Kind).IsEqualTo(StatKind.Capture);
        await Assert.That(killTicks.Keep).IsEqualTo(KeepMode.List);
        await Assert.That(killTicks.Trigger!.On!.Name).IsEqualTo("post_plant_kill");
    }

    // ── Highlight + show ───────────────────────────────────────────────────────

    /// <summary>The highlight maps its when-expression, per-scope, and title template.</summary>
    [Test]
    public async Task PostPlant_Highlight_Maps()
    {
        RulesetDoc doc = LoadDoc(PostPlantDouble);
        await Assert.That(doc.Highlights.Count).IsEqualTo(1);

        HighlightDef highlight = doc.Highlights[0];
        await Assert.That(highlight.Id).IsEqualTo("post_plant_double");
        await Assert.That(highlight.When).IsEqualTo("post_plant_kills >= params.min_kills");
        await Assert.That(highlight.Per).IsEqualTo(PerScope.Round);
        await Assert.That(highlight.Title).Contains("{player.name}");
        await Assert.That(highlight.Title).Contains("{round.number}");
    }

    /// <summary>The show block maps the scoreboard entry and the custom player_round table.</summary>
    [Test]
    public async Task PostPlant_Show_Maps()
    {
        RulesetDoc doc = LoadDoc(PostPlantDouble);
        ShowDef show = doc.Show!;

        await Assert.That(show.Scoreboard.Count).IsEqualTo(1);
        ScoreboardEntry entry = show.Scoreboard[0];
        await Assert.That(entry.Stat).IsEqualTo("post_plant_double.count");
        await Assert.That(entry.Label).IsEqualTo("PostPlantDoubles");
        await Assert.That(entry.Group).IsEqualTo("objectives");

        await Assert.That(show.Tables.Count).IsEqualTo(1);
        TableDef table = show.Tables[0];
        await Assert.That(table.Name).IsEqualTo("post_plant_double_context");
        await Assert.That(table.Per).IsEqualTo("player_round");
        string[] expectedColumnStats = ["post_plant_double", "post_plant_kills", "plant_tick", "kill_ticks"];
        await Assert.That(table.Columns.Select(c => c.Stat)).IsEquivalentTo(expectedColumnStats);
    }

    /// <summary>Every mapped element carries a real 1-based source position.</summary>
    [Test]
    public async Task PostPlant_Positions_AreOneBased()
    {
        RulesetDoc doc = LoadDoc(PostPlantDouble);
        foreach (StatDef stat in doc.Stats)
        {
            await Assert.That(stat.Position.Line).IsGreaterThan(0);
            await Assert.That(stat.Position.Column).IsGreaterThan(0);
        }
    }

    // ── Unary-test forms ───────────────────────────────────────────────────────

    /// <summary>All four unary-test forms parse into the right variant: literal, in-ref, in-literal, comparison, range.</summary>
    [Test]
    public async Task UnaryTests_AllForms_ParseToRightVariant()
    {
        const string Yaml = """
                            ruleset: forms
                            for: match
                            define:
                              rifles: [ak47, m4a1]
                            stats:
                              s:
                                count: kill
                                per: match
                                match:
                                  enemy: true
                                  weapon: in rifles
                                  damage: ">= 5"
                                  rounds: [2..5]
                                  guns: in [ak47, m4a1]
                            """;

        RulesetDoc doc = LoadDoc(Yaml);
        Dictionary<string, UnaryTest> tests = doc.Stats[0].Trigger!.Match.ToDictionary(m => m.Key, m => m.Test);

        LiteralTest enemy = (LiteralTest)tests["enemy"];
        await Assert.That(enemy.RawText).IsEqualTo("true");
        await Assert.That(enemy.Kind).IsEqualTo(ScalarKind.Bool);

        InListRefTest weapon = (InListRefTest)tests["weapon"];
        await Assert.That(weapon.ListRef).IsEqualTo("rifles");

        ComparisonTest damage = (ComparisonTest)tests["damage"];
        await Assert.That(damage.Operator).IsEqualTo(ComparisonOperator.GreaterOrEqual);
        await Assert.That(damage.LiteralRawText).IsEqualTo("5");

        RangeTest rounds = (RangeTest)tests["rounds"];
        await Assert.That(rounds.Low).IsEqualTo(2L);
        await Assert.That(rounds.High).IsEqualTo(5L);

        InListLiteralTest guns = (InListLiteralTest)tests["guns"];
        string[] expectedGuns = ["ak47", "m4a1"];
        await Assert.That(guns.Items).IsEquivalentTo(expectedGuns);
    }

    // ── Dispatch: v1 unchanged, v2 routed, coexistence ─────────────────────────

    /// <summary>
    ///     A retired-v1 <c>chains:</c> file in a directory is a loud, attributed error — and the
    ///     sibling v2 ruleset file still loads (per-file containment).
    /// </summary>
    [Test]
    public async Task Directory_RetiredV1File_FailsLoud_SiblingV2StillLoads()
    {
        DirectoryInfo dir = Directory.CreateTempSubdirectory("rulesv2_retiredv1_");
        try
        {
            File.WriteAllText(Path.Combine(dir.FullName, "kills.yaml"), V1Chain);
            File.WriteAllText(Path.Combine(dir.FullName, "post_plant_double.rules.yaml"), PostPlantDouble);

            RuleConfigLoadResult result = YamlConfigLoader.TryLoadDirectory(dir.FullName);

            await Assert.That(result.Errors.Count).IsEqualTo(1);
            await Assert.That(result.Errors[0].Message).Contains("retired Rulesets v1 format")
                .Because("a pre-existing v1 overlay file must fail loudly and legibly, never silently");
            await Assert.That(result.Errors[0].FilePath!).EndsWith("kills.yaml");
            await Assert.That(result.FailedFiles.Single()).EndsWith("kills.yaml");
            await Assert.That(result.Rulesets.Select(r => r.Id)).Contains("post_plant_double");
        }
        finally
        {
            dir.Delete(true);
        }
    }

    /// <summary>
    ///     A v2 ruleset's structural error surfaces through <see cref="YamlConfigLoader.TryLoadDirectory" />
    ///     as an attributed <see cref="RuleConfigError" /> — so the shipped-hard-fail /
    ///     user-tier-containment behaviour applies to v2 files exactly as to v1 chains.
    /// </summary>
    [Test]
    public async Task V2StructuralError_FlowsIntoResult_Errors()
    {
        const string BadRuleset = """
                                  ruleset: broken
                                  for: match
                                  stats:
                                    s:
                                      count: kill
                                      keep: list
                                      per: match
                                  """;

        DirectoryInfo dir = Directory.CreateTempSubdirectory("rulesv2_bad_");
        try
        {
            File.WriteAllText(Path.Combine(dir.FullName, "broken.rules.yaml"), BadRuleset);
            RuleConfigLoadResult result = YamlConfigLoader.TryLoadDirectory(dir.FullName);

            await Assert.That(result.Success).IsFalse();
            RuleConfigError error = result.Errors.Single();
            await Assert.That(error.Message).Contains("keep:");
            await Assert.That(error.ChainId).IsEqualTo("broken");
            await Assert.That(error.Line).IsNotNull();
        }
        finally
        {
            dir.Delete(true);
        }
    }

    // ── Two-tier overlay (LoadWithOverlay) ─────────────────────────────────────

    private static string MiniRuleset(string id, string? title = null, bool enabled = true)
    {
        string titleLine = title is null ? "" : $"\ntitle: {title}";
        string enabledLine = enabled ? "" : "\nenabled: false";
        return $"ruleset: {id}{titleLine}{enabledLine}\nfor: match\nstats:\n  k:\n    count: kill\n    per: match\n";
    }

    /// <summary>A user v2 ruleset overlays a shipped one with the same id (user replaces shipped in place).</summary>
    [Test]
    public async Task Overlay_UserRuleset_OverridesShippedById()
    {
        DirectoryInfo shipped = Directory.CreateTempSubdirectory("rulesv2_shipped_");
        DirectoryInfo user = Directory.CreateTempSubdirectory("rulesv2_user_");
        try
        {
            File.WriteAllText(Path.Combine(shipped.FullName, "rs1.rules.yaml"), MiniRuleset("rs1", "Shipped"));
            File.WriteAllText(Path.Combine(user.FullName, "rs1.rules.yaml"), MiniRuleset("rs1", "User"));

            RuleConfigLoadResult result = YamlConfigLoader.LoadWithOverlay(shipped.FullName, user.FullName);

            await Assert.That(result.Errors).IsEmpty();
            RulesetDoc rs1 = result.Rulesets.Single(r => r.Id == "rs1");
            await Assert.That(rs1.Title).IsEqualTo("User");
        }
        finally
        {
            shipped.Delete(true);
            user.Delete(true);
        }
    }

    /// <summary>A user override that sets <c>enabled: false</c> drops the ruleset after overlay.</summary>
    [Test]
    public async Task Overlay_DisabledUserOverride_Drops()
    {
        DirectoryInfo shipped = Directory.CreateTempSubdirectory("rulesv2_shipped_");
        DirectoryInfo user = Directory.CreateTempSubdirectory("rulesv2_user_");
        try
        {
            File.WriteAllText(Path.Combine(shipped.FullName, "rs1.rules.yaml"), MiniRuleset("rs1"));
            File.WriteAllText(Path.Combine(shipped.FullName, "rs2.rules.yaml"), MiniRuleset("rs2"));
            File.WriteAllText(Path.Combine(user.FullName, "rs1.rules.yaml"), MiniRuleset("rs1", enabled: false));

            RuleConfigLoadResult result = YamlConfigLoader.LoadWithOverlay(shipped.FullName, user.FullName);

            await Assert.That(result.Rulesets.Select(r => r.Id)).DoesNotContain("rs1");
            await Assert.That(result.Rulesets.Select(r => r.Id)).Contains("rs2");
        }
        finally
        {
            shipped.Delete(true);
            user.Delete(true);
        }
    }

    /// <summary>
    ///     The two-line disable stub the provisioned user-rules README advertises —
    ///     <c>ruleset: id</c> + <c>enabled: false</c>, no body — loads without errors and drops the
    ///     shipped ruleset (GAP-AE-5 v2 provisioning cutover: the stub replaces the v1
    ///     <c>chains:</c> disable stub for new users).
    /// </summary>
    [Test]
    public async Task Overlay_MinimalDisableStub_DropsShippedRuleset_NoErrors()
    {
        DirectoryInfo shipped = Directory.CreateTempSubdirectory("rulesv2_shipped_");
        DirectoryInfo user = Directory.CreateTempSubdirectory("rulesv2_user_");
        try
        {
            File.WriteAllText(Path.Combine(shipped.FullName, "rs1.rules.yaml"), MiniRuleset("rs1"));
            File.WriteAllText(Path.Combine(user.FullName, "off.rules.yaml"),
                "ruleset: rs1\nenabled: false\n");

            RuleConfigLoadResult result = YamlConfigLoader.LoadWithOverlay(shipped.FullName, user.FullName);

            await Assert.That(result.Errors).IsEmpty();
            await Assert.That(result.Rulesets.Select(r => r.Id)).DoesNotContain("rs1");
        }
        finally
        {
            shipped.Delete(true);
            user.Delete(true);
        }
    }

    /// <summary>A shipped ruleset marked <c>enabled: false</c> drops even with no user directory.</summary>
    [Test]
    public async Task Overlay_DisabledShipped_NoUserDir_Drops()
    {
        DirectoryInfo shipped = Directory.CreateTempSubdirectory("rulesv2_shipped_");
        try
        {
            File.WriteAllText(Path.Combine(shipped.FullName, "on.rules.yaml"), MiniRuleset("on"));
            File.WriteAllText(Path.Combine(shipped.FullName, "off.rules.yaml"), MiniRuleset("off", enabled: false));

            RuleConfigLoadResult result = YamlConfigLoader.LoadWithOverlay(shipped.FullName, null);

            await Assert.That(result.Rulesets.Select(r => r.Id)).Contains("on");
            await Assert.That(result.Rulesets.Select(r => r.Id)).DoesNotContain("off");
        }
        finally
        {
            shipped.Delete(true);
        }
    }

    /// <summary>A retired-v1 <c>outputs:</c>-only file fails loudly with the retired-format error too.</summary>
    [Test]
    public async Task RetiredV1OutputsFile_FailsLoud()
    {
        const string OutputsOnly = """
                                   outputs:
                                     - id: my_table
                                       scope: per_player_per_game
                                       metrics:
                                         - rule: kills
                                   """;

        DirectoryInfo dir = Directory.CreateTempSubdirectory("rulesv2_v1outputs_");
        try
        {
            File.WriteAllText(Path.Combine(dir.FullName, "outputs.yaml"), OutputsOnly);
            RuleConfigLoadResult result = YamlConfigLoader.TryLoadDirectory(dir.FullName);

            await Assert.That(result.Success).IsFalse();
            await Assert.That(result.Errors.Single().Message).Contains("retired Rulesets v1 format");
        }
        finally
        {
            dir.Delete(true);
        }
    }

    /// <summary>A YAML file that is neither a ruleset nor v1 fails with the not-a-rules-document error.</summary>
    [Test]
    public async Task NonRulesetYaml_FailsLoud_WithRulesetHint()
    {
        DirectoryInfo dir = Directory.CreateTempSubdirectory("rulesv2_notrules_");
        try
        {
            File.WriteAllText(Path.Combine(dir.FullName, "random.yaml"), "foo: 1\nbar: 2\n");
            RuleConfigLoadResult result = YamlConfigLoader.TryLoadDirectory(dir.FullName);

            await Assert.That(result.Success).IsFalse();
            await Assert.That(result.Errors.Single().Message).Contains("'ruleset:'");
        }
        finally
        {
            dir.Delete(true);
        }
    }

    /// <summary>A YAML syntax error is reported with its position — classification did not swallow it.</summary>
    [Test]
    public async Task BrokenYamlSyntax_StillErrors_WithPosition()
    {
        DirectoryInfo dir = Directory.CreateTempSubdirectory("rulesv2_syntax_");
        try
        {
            File.WriteAllText(Path.Combine(dir.FullName, "bad.yaml"), "ruleset: [unclosed\n  nope: {\n");
            RuleConfigLoadResult result = YamlConfigLoader.TryLoadDirectory(dir.FullName);

            await Assert.That(result.Success).IsFalse();
            await Assert.That(result.Errors[0].Line).IsNotNull();
        }
        finally
        {
            dir.Delete(true);
        }
    }
}
