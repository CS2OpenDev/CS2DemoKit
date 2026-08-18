#region

using CS2DemoKit.Analysis.Rules;
using CS2DemoKit.Analysis.RulesetsV2.Model;
using CS2DemoKit.Analysis.RulesetsV2.Resolve;
using CS2DemoKit.Analysis.Yaml;

#endregion

namespace CS2DemoKit.Analysis.Tests.RulesV2;

/// <summary>
///     CONS-5 (no library rule-validation entry point): <c>DemoAnalysis.ValidateRulesets</c> is the
///     public, demo-less whole-set check an upload endpoint calls before storing user-authored
///     rules. It must catch what <c>rules check</c> catches — cross-ruleset conflicts, unresolvable
///     identifiers, type errors — and it must pass the 14 shipped rulesets clean, because those are
///     the reference set every consumer inherits.
/// </summary>
[Category("Unit")]
public class ValidateRulesetsTests
{
    [Test]
    public async Task ShippedRulesets_ValidateClean()
    {
        RuleConfigLoadResult loaded = YamlConfigLoader.LoadShippedEmbedded();
        await Assert.That(loaded.Success).IsTrue()
            .Because("the embedded shipped rules must load: " + string.Join("; ", loaded.Errors));

        RulesetValidationResult validated = DemoAnalysis.ValidateRulesets(loaded.Rulesets);

        await Assert.That(validated.Success).IsTrue()
            .Because("the shipped set is the reference config — it must compose clean: "
                     + string.Join("; ", validated.Diagnostics));
        await Assert.That(validated.Excluded.Count).IsEqualTo(0);
        await Assert.That(validated.ValidatedRulesetIds.Count).IsEqualTo(loaded.Rulesets.Count)
            .Because("every shipped ruleset validates, so every one appears in the validated ids");
    }

    [Test]
    public async Task CrossRulesetConflict_IsCaught()
    {
        // `reader` reads a stat `provider` declares but does not export — a conflict no single-document
        // check can see, which is the whole reason validation is whole-set.
        RulesetDoc provider = RulesetCompositionDiagnosticsTests.Doc("provider.rules.yaml", """
             ruleset: provider
             for: each_player
             exports: [shown]
             stats:
               shown:
                 count: kill
                 per: match
               hidden:
                 count: death
                 per: match
             """);
        RulesetDoc reader = RulesetCompositionDiagnosticsTests.Doc("reader.rules.yaml", """
             ruleset: reader
             for: each_player
             use: [provider]
             stats:
               r:
                 compute: "provider.hidden + 1"
             """);

        RulesetValidationResult validated = DemoAnalysis.ValidateRulesets([provider, reader]);

        await Assert.That(validated.Success).IsFalse().Because("reading a non-exported stat is a conflict");
        await Assert.That(validated.Diagnostics.Any(d =>
                d.Code == ResolveDiagnosticCodes.CrossRefNotExported && d.RulesetId == "reader"))
            .IsTrue().Because("the conflict is attributed to the ruleset that did the illegal read");
        await Assert.That(validated.Excluded.Select(e => e.Id).ToList())
            .IsEquivalentTo(new List<string>
            {
                "reader"
            })
            .Because("only the reader fails; the provider is fine on its own");
        await Assert.That(validated.ValidatedRulesetIds).Contains("provider")
            .Because("the still-valid rulesets are named, so a partial upload can be reasoned about");
    }

    [Test]
    public async Task IdentifierAndTypeErrors_AreCaught_WithPositions()
    {
        RulesetValidationResult validated = DemoAnalysis.ValidateRulesets(
        [
            RulesetCompositionDiagnosticsTests.BadIdentifier(),
            RulesetCompositionDiagnosticsTests.BadType()
        ]);

        await Assert.That(validated.Diagnostics.Any(d =>
                d.RulesetId == "bad_ref" && d.Code == DiagnosticCodes.UnknownRoot))
            .IsTrue().Because("an unresolvable identifier is a resolution error");
        await Assert.That(validated.Diagnostics.Any(d =>
                d.RulesetId == "bad_type" && d.Message.Contains("must be bool", StringComparison.Ordinal)))
            .IsTrue().Because("a non-bool when: is a type error stated in language terms");
        await Assert.That(validated.Diagnostics.All(d => d.Position.Line > 0 && d.Position.File is not null))
            .IsTrue().Because("an upload endpoint hands file(line,col) back to the author");
    }

    [Test]
    public async Task EmptyInput_ValidatesTrivially()
    {
        RulesetValidationResult validated = DemoAnalysis.ValidateRulesets(Array.Empty<RulesetDoc>());

        await Assert.That(validated.Success).IsTrue();
        await Assert.That(validated.ValidatedRulesetIds.Count).IsEqualTo(0);
    }

    [Test]
    public async Task YamlOverload_KeepsLoadErrorsSeparate_AndStillComposesTheRest()
    {
        RulesetValidationResult validated = DemoAnalysis.ValidateRulesets(
        [
            ("broken.yaml", "ruleset: [this is not a map]"),
            ("bad_ref.yaml", """
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
                             """),
            ("healthy.yaml", """
                             ruleset: healthy
                             for: each_player
                             stats:
                               kills:
                                 count: kill
                                 per: round
                             """)
        ]);

        await Assert.That(validated.Success).IsFalse();
        await Assert.That(validated.LoadErrors.Count).IsGreaterThan(0)
            .Because("the YAML-tier failure has no diagnostic code, so it stays in its own list");
        await Assert.That(validated.LoadErrors.All(e => e.FilePath == "broken.yaml")).IsTrue()
            .Because("only the unparseable document fails to load");
        await Assert.That(validated.Diagnostics.Any(d => d.RulesetId == "bad_ref")).IsTrue()
            .Because("an unparseable sibling must not mask composition errors in the documents that DID load");
        await Assert.That(validated.ValidatedRulesetIds).Contains("healthy")
            .Because("the healthy document still validates");
    }

    [Test]
    public async Task OverlayLoad_LetsAUserRulesetReadAShippedOne()
    {
        // The load-bearing "pass the whole id namespace" rule: validated alone, `use: [kast]` is an
        // unknown-ruleset error; validated over the shipped-plus-overlay set, it resolves.
        (string Label, string Yaml)[] userDocs =
        [
            ("db://user/1", """
                            ruleset: user_derived
                            for: each_player
                            use: [kast]
                            stats:
                              doubled:
                                compute: "kast.kast_pct * 2"
                            """)
        ];

        RulesetValidationResult alone = DemoAnalysis.ValidateRulesets(userDocs);
        await Assert.That(alone.Diagnostics.Any(d => d.Code == ResolveDiagnosticCodes.CrossRefUnknownRuleset))
            .IsTrue().Because("validated on its own the user ruleset cannot see the shipped tier");

        RuleConfigLoadResult overlaid = YamlConfigLoader.LoadShippedWithOverlay(userDocs);
        RulesetValidationResult together = DemoAnalysis.ValidateRulesets(overlaid.Rulesets);

        await Assert.That(together.Success).IsTrue()
            .Because("over the shipped tier the qualified read resolves: "
                     + string.Join("; ", together.Diagnostics));
        await Assert.That(together.ValidatedRulesetIds).Contains("user_derived");
    }
}
