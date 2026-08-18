#region

using System.Diagnostics.CodeAnalysis;
using CS2DemoKit.Analysis.Rules;
using CS2DemoKit.Analysis.Rules.Ast;
using CS2DemoKit.Analysis.Rules.Lexing;
using CS2DemoKit.Analysis.Rules.Normalization;
using CS2DemoKit.Analysis.Rules.Parsing;

#endregion

namespace CS2DemoKit.Analysis.Tests.RulesV2;

/// <summary>
///     Semantic-core normalizer battery, pinned to spec §5 conservative
///     canonicalization: the hash-equal pairs (whitespace, word forms, redundant parens,
///     duration folding at the tick rate) and — just as load-bearing — the hash-DISTINCT
///     pairs (no constant arithmetic folding, no operand reordering). Structural node
///     equality is exactly canonical-serialization equality, so these pins are the
///     equivalence layer under the SHA-256 hasher. Pure in-memory; no demo.
/// </summary>
[Category("Unit")]
public class RulesExpressionNormalizerTests
{
    private static ExpressionNode Normalize(string source, NormalizerOptions? options = null) =>
        ExpressionNormalizer.Normalize(ExpressionParser.Parse(source).Require(), options).Require();

    // ── Hash-equal pairs (spec §5 rows 1–3) ──────────────────────────────────────

    /// <summary>Whitespace vanishes at lexing: 'a&gt;1' ≡ 'a &gt; 1'.</summary>
    [Test]
    public async Task Normalize_WhitespaceSpellings_Equal()
    {
        await Assert.That(Normalize("a>1")).IsEqualTo(Normalize("a > 1"));
    }

    /// <summary>Word forms vanish at lexing: 'a &amp;&amp; b' ≡ 'a and b'.</summary>
    [Test]
    public async Task Normalize_WordFormSpellings_Equal()
    {
        await Assert.That(Normalize("a && b")).IsEqualTo(Normalize("a and b"));
        await Assert.That(Normalize("!a || b")).IsEqualTo(Normalize("not a or b"));
    }

    /// <summary>Redundant parentheses vanish at parsing.</summary>
    [Test]
    public async Task Normalize_RedundantParens_Equal()
    {
        await Assert.That(Normalize("((a + b)) * c")).IsEqualTo(Normalize("(a + b) * c"));
        await Assert.That(Normalize("a + (b * c)")).IsEqualTo(Normalize("a + b * c"));
    }

    /// <summary>Duration literals fold to int ticks at 64/s: '5s' ≡ '320', '0.5s' ≡ '500ms' ≡ '32'.</summary>
    [Test]
    public async Task Normalize_DurationFolding_At64()
    {
        await Assert.That(Normalize("a > 5s")).IsEqualTo(Normalize("a > 320"));
        await Assert.That(Normalize("a > 0.5s")).IsEqualTo(Normalize("a > 32"));
        await Assert.That(Normalize("a > 500ms")).IsEqualTo(Normalize("a > 32"));
        await Assert.That(Normalize("5s").CanonicalText).IsEqualTo("(int 320)");
    }

    /// <summary>Folding uses the supplied tick rate (the demo's), not always 64.</summary>
    [Test]
    public async Task Normalize_DurationFolding_UsesTickRate()
    {
        NormalizerOptions at128 = new()
        {
            TicksPerSecond = 128.0
        };

        await Assert.That(Normalize("0.5s", at128).CanonicalText).IsEqualTo("(int 64)");
        await Assert.That(Normalize("500ms", at128).CanonicalText).IsEqualTo("(int 64)");
    }

    /// <summary>Midpoints round away from zero (spec §5 row 3): 7.8125ms = 0.5 ticks at 64/s → 1.</summary>
    [Test]
    public async Task Normalize_DurationFolding_MidpointAwayFromZero()
    {
        await Assert.That(Normalize("7.8125ms").CanonicalText).IsEqualTo("(int 1)");
        await Assert.That(Normalize("-7.8125ms").CanonicalText).IsEqualTo("(int -1)");
    }

    /// <summary>Negative durations fold through sign folding: '-0.5s' ≡ '-32'.</summary>
    [Test]
    public async Task Normalize_NegativeDuration_Equal()
    {
        await Assert.That(Normalize("a + -0.5s")).IsEqualTo(Normalize("a + -32"));
        await Assert.That(Normalize("-(0.5s)").CanonicalText).IsEqualTo("(int -32)");
    }

    /// <summary>Durations inside list literals fold too.</summary>
    [Test]
    public async Task Normalize_DurationInListLiteral_Folds()
    {
        await Assert.That(Normalize("x in [5s, 320]").CanonicalText)
            .IsEqualTo("(in (ref x) (list (int 320) (int 320)))");
    }

    // ── Hash-DISTINCT pairs (spec §5 row 6: no algebraic rewriting) ──────────────

    /// <summary>Structure is identity: 'a + (b * c)' and '(a + b) * c' are different nodes.</summary>
    [Test]
    public async Task Normalize_DifferentStructure_Distinct()
    {
        await Assert.That(Normalize("a + (b * c)")).IsNotEqualTo(Normalize("(a + b) * c"));
    }

    /// <summary>NO constant arithmetic folding: '1 + 2' stays distinct from '3'.</summary>
    [Test]
    public async Task Normalize_NoConstantFolding()
    {
        await Assert.That(Normalize("1 + 2")).IsNotEqualTo(Normalize("3"));
        await Assert.That(Normalize("1 + 2").CanonicalText).IsEqualTo("(add (int 1) (int 2))");
    }

    /// <summary>Different references are different nodes: 'a &gt; 1' vs 'b &gt; 1'.</summary>
    [Test]
    public async Task Normalize_DifferentReferences_Distinct()
    {
        await Assert.That(Normalize("a > 1")).IsNotEqualTo(Normalize("b > 1"));
    }

    /// <summary>No operand reordering: 'a and b' stays distinct from 'b and a'.</summary>
    [Test]
    public async Task Normalize_NoOperandReordering()
    {
        await Assert.That(Normalize("a and b")).IsNotEqualTo(Normalize("b and a"));
    }

    // ── Define inlining (spec §5 row 4) ──────────────────────────────────────────

    private static NormalizerOptions Defines(params (string Name, string Body)[] defines)
    {
        Dictionary<string, string> table = defines.ToDictionary(d => d.Name, d => d.Body, StringComparer.Ordinal);
        return new NormalizerOptions
        {
            DefineLookup = name => table.TryGetValue(name, out string? body)
                ? ExpressionParser.Parse(body).Require()
                : null
        };
    }

    /// <summary>A define inlines at its use site: the spelled-out form is the identical node.</summary>
    [Test]
    public async Task Normalize_DefineInlining_Equal()
    {
        NormalizerOptions options = Defines(("good_kill", "kills + 1"));

        await Assert.That(Normalize("good_kill * 2", options)).IsEqualTo(Normalize("(kills + 1) * 2"));
    }

    /// <summary>Defines inline recursively, and duration folding applies inside the inlined body.</summary>
    [Test]
    public async Task Normalize_NestedDefines_InlineAndFold()
    {
        NormalizerOptions options = Defines(
            ("window", "5s"),
            ("in_window", "elapsed < window"));

        await Assert.That(Normalize("in_window and a", options)).IsEqualTo(Normalize("elapsed < 320 and a"));
    }

    /// <summary>A define whose body is a reference splices under member access.</summary>
    [Test]
    public async Task Normalize_DefineReferenceSplice_Equal()
    {
        NormalizerOptions options = Defines(("bomb", "round.bomb"));

        await Assert.That(Normalize("bomb.was_planted", options)).IsEqualTo(Normalize("round.bomb.was_planted"));
    }

    /// <summary>Member access through an expression-bodied define is an error, not a silent guess.</summary>
    [Test]
    public async Task Normalize_DefineExpressionMemberAccess_Errors()
    {
        NormalizerOptions options = Defines(("score", "kills + 1"));
        LanguageResult<ExpressionNode> result = ExpressionNormalizer.Normalize(
            ExpressionParser.Parse("score.count").Require(), options);

        await Assert.That(result.Success).IsFalse();
        await Assert.That(result.Diagnostics[0].Code).IsEqualTo(DiagnosticCodes.DefineMemberAccess);
    }

    /// <summary>Define cycles are a named-path error (the spec §6 cycle rule analogue).</summary>
    [Test]
    public async Task Normalize_DefineCycle_Errors()
    {
        NormalizerOptions options = Defines(("d", "e + 1"), ("e", "d + 1"));
        LanguageResult<ExpressionNode> result = ExpressionNormalizer.Normalize(
            ExpressionParser.Parse("d > 0").Require(), options);

        await Assert.That(result.Success).IsFalse();
        Diagnostic error = result.Diagnostics[0];
        await Assert.That(error.Code).IsEqualTo(DiagnosticCodes.DefineCycle);
        await Assert.That(error.Message).Contains("d -> e -> d");
    }

    private static NormalizerOptions KillView() => new()
    {
        MatchBindingLowering = new FakeKillViewLowering()
    };

    /// <summary>Structured match: bindings hash identically to the free-form where: spelling.</summary>
    [Test]
    public async Task NormalizeMatchBindings_EqualsFreeFormSpelling()
    {
        // Bindings written in the "wrong" order — the fixed catalog key order wins.
        MatchBinding[] bindings =
        [
            new("headshot", new BoolLiteralNode(true)),
            new("weapon", new StringLiteralNode("ak47"))
        ];

        ExpressionNode structured = ExpressionNormalizer.NormalizeMatchBindings(
            bindings, ExpressionParser.Parse("kills > 1").Require(), KillView()).Require();

        await Assert.That(structured)
            .IsEqualTo(Normalize("event.weapon == \"ak47\" and event.headshot == true and kills > 1"));
    }

    /// <summary>Bindings alone (no where:) produce the bare conjunction in key order.</summary>
    [Test]
    public async Task NormalizeMatchBindings_BindingsOnly()
    {
        MatchBinding[] bindings = [new("weapon", new StringLiteralNode("awp"))];

        ExpressionNode structured =
            ExpressionNormalizer.NormalizeMatchBindings(bindings, null, KillView()).Require();

        await Assert.That(structured).IsEqualTo(Normalize("event.weapon == \"awp\""));
    }

    /// <summary>An unknown match: key is a diagnostic naming the key.</summary>
    [Test]
    public async Task NormalizeMatchBindings_UnknownKey_Errors()
    {
        MatchBinding[] bindings = [new("wepon", new StringLiteralNode("ak47"))];

        LanguageResult<ExpressionNode> result =
            ExpressionNormalizer.NormalizeMatchBindings(bindings, null, KillView());

        await Assert.That(result.Success).IsFalse();
        await Assert.That(result.Diagnostics[0].Code).IsEqualTo(DiagnosticCodes.UnknownMatchKey);
        await Assert.That(result.Diagnostics[0].Message).Contains("wepon");
    }

    /// <summary>Binding values fold like any expression (a duration-valued match: key).</summary>
    [Test]
    public async Task NormalizeMatchBindings_ValuesNormalize()
    {
        MatchBinding[] bindings =
        [
            new("weapon", new DurationLiteralNode(5, DurationUnit.Seconds))
        ];

        ExpressionNode structured =
            ExpressionNormalizer.NormalizeMatchBindings(bindings, null, KillView()).Require();

        await Assert.That(structured).IsEqualTo(Normalize("event.weapon == 320"));
    }

    // ── match: binding normalization (spec §5 row 5) ─────────────────────────────

    /// <summary>
    ///     Lowering hook for a fake 'kill' view with catalog key order [weapon, headshot]:
    ///     each binding becomes 'event.&lt;key&gt; == value'.
    /// </summary>
    private sealed class FakeKillViewLowering : IMatchBindingLowering
    {
        private static readonly string[] _keyOrder = ["weapon", "headshot"];

        public bool TryLower(MatchBinding binding,
            [NotNullWhen(true)] out ExpressionNode? lowered, out int keyOrder)
        {
            keyOrder = Array.IndexOf(_keyOrder, binding.Key);
            if (keyOrder < 0)
            {
                lowered = null;
                return false;
            }

            lowered = new BinaryNode(BinaryOperator.Equal,
                ReferenceNode.FromPath($"event.{binding.Key}"), binding.Value);
            return true;
        }
    }
}
