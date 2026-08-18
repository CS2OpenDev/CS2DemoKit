#region

using CS2DemoKit.Analysis.Rules;
using CS2DemoKit.Analysis.Rules.Ast;
using CS2DemoKit.Analysis.Rules.Parsing;

#endregion

namespace CS2DemoKit.Analysis.Tests.RulesV2;

/// <summary>
///     Semantic-core parser battery, pinned to the spec §2 syntactic grammar:
///     one precedence test per adjacent level pair, the no-chained-comparisons rule, unary
///     minus (a v1 parse error, fixed), <c>in</c> operand shapes, index access, closed-set
///     function arity, and the hard EOF trailing-token rule (v1 silently truncated).
///     Assertions pin the canonical AST serialization, which is the dedup identity.
///     Pure in-memory; no demo.
/// </summary>
[Category("Unit")]
public class RulesExpressionParserTests
{
    private static string Canon(string source) => ExpressionParser.Parse(source).Require().CanonicalText;

    private static Diagnostic FirstError(string source)
    {
        LanguageResult<ExpressionNode> result = ExpressionParser.Parse(source);
        return result.Diagnostics[0];
    }

    // ── Precedence: every adjacent level pair ────────────────────────────────────

    /// <summary>or binds looser than and.</summary>
    [Test]
    public async Task Parse_OrVsAnd_Precedence()
    {
        await Assert.That(Canon("a or b and c")).IsEqualTo("(or (ref a) (and (ref b) (ref c)))");
        await Assert.That(Canon("a and b or c")).IsEqualTo("(or (and (ref a) (ref b)) (ref c))");
    }

    /// <summary>and binds looser than not.</summary>
    [Test]
    public async Task Parse_AndVsNot_Precedence()
    {
        await Assert.That(Canon("not a and b")).IsEqualTo("(and (not (ref a)) (ref b))");
    }

    /// <summary>not binds looser than comparison (spec §2 grammar: not-expr wraps comparison).</summary>
    [Test]
    public async Task Parse_NotVsComparison_Precedence()
    {
        await Assert.That(Canon("not a > 1")).IsEqualTo("(not (gt (ref a) (int 1)))");
        await Assert.That(Canon("not a in xs")).IsEqualTo("(not (in (ref a) (ref xs)))");
    }

    /// <summary>comparison binds looser than additive.</summary>
    [Test]
    public async Task Parse_ComparisonVsAdditive_Precedence()
    {
        await Assert.That(Canon("a + b > c + d"))
            .IsEqualTo("(gt (add (ref a) (ref b)) (add (ref c) (ref d)))");
    }

    /// <summary>in takes a full additive expression on its left.</summary>
    [Test]
    public async Task Parse_InVsAdditive_Precedence()
    {
        await Assert.That(Canon("a + 1 in xs")).IsEqualTo("(in (add (ref a) (int 1)) (ref xs))");
    }

    /// <summary>additive binds looser than multiplicative.</summary>
    [Test]
    public async Task Parse_AdditiveVsMultiplicative_Precedence()
    {
        await Assert.That(Canon("a + b * c")).IsEqualTo("(add (ref a) (mul (ref b) (ref c)))");
        await Assert.That(Canon("a - b / c")).IsEqualTo("(sub (ref a) (div (ref b) (ref c)))");
        await Assert.That(Canon("a % b + c")).IsEqualTo("(add (mod (ref a) (ref b)) (ref c))");
    }

    /// <summary>multiplicative binds looser than unary.</summary>
    [Test]
    public async Task Parse_MultiplicativeVsUnary_Precedence()
    {
        await Assert.That(Canon("-a * b")).IsEqualTo("(mul (neg (ref a)) (ref b))");
    }

    /// <summary>unary binds looser than postfix: minus applies to the whole member/index chain.</summary>
    [Test]
    public async Task Parse_UnaryVsPostfix_Precedence()
    {
        await Assert.That(Canon("-a.b")).IsEqualTo("(neg (ref a.b))");
        await Assert.That(Canon("-xs[0]")).IsEqualTo("(neg (index (ref xs) (int 0)))");
    }

    /// <summary>Word forms parse identically to symbolic forms — same node, same hash identity.</summary>
    [Test]
    public async Task Parse_WordAndSymbolicForms_SameAst()
    {
        await Assert.That(ExpressionParser.Parse("a && b || !c").Require())
            .IsEqualTo(ExpressionParser.Parse("a and b or not c").Require());
    }

    // ── Chained comparisons ──────────────────────────────────────────────────────

    /// <summary>a &lt; b &lt; c is a parse error, not silent truth (spec §2).</summary>
    [Test]
    public async Task Parse_ChainedComparison_Errors()
    {
        Diagnostic error = FirstError("a < b < c");

        await Assert.That(error.Code).IsEqualTo(DiagnosticCodes.ChainedComparison);
        await Assert.That(error.Message).Contains("and");
    }

    /// <summary>Chaining through in is equally rejected.</summary>
    [Test]
    public async Task Parse_ComparisonThenIn_Errors()
    {
        await Assert.That(FirstError("a < b in xs").Code).IsEqualTo(DiagnosticCodes.ChainedComparison);
        await Assert.That(FirstError("a in xs == true").Code).IsEqualTo(DiagnosticCodes.ChainedComparison);
    }

    // ── Unary minus ──────────────────────────────────────────────────────────────

    /// <summary>Unary minus is legal (v1 parse error, fixed); on literals it folds to a negative literal.</summary>
    [Test]
    public async Task Parse_UnaryMinus_Forms()
    {
        await Assert.That(Canon("-99")).IsEqualTo("(int -99)");
        await Assert.That(Canon("--x")).IsEqualTo("(neg (neg (ref x)))");
        await Assert.That(Canon("-(a + b)")).IsEqualTo("(neg (add (ref a) (ref b)))");
        await Assert.That(Canon("-2.5")).IsEqualTo("(float -2.5)");
    }

    // ── in operands ──────────────────────────────────────────────────────────────

    /// <summary>in accepts a reference on the right.</summary>
    [Test]
    public async Task Parse_InWithReference_Ok()
    {
        await Assert.That(Canon("weapon in allowed_weapons"))
            .IsEqualTo("(in (ref weapon) (ref allowed_weapons))");
    }

    /// <summary>in accepts a constant list literal, including negatives and the empty list.</summary>
    [Test]
    public async Task Parse_InWithListLiteral_Ok()
    {
        await Assert.That(Canon("slot in [1, 2, -3]"))
            .IsEqualTo("(in (ref slot) (list (int 1) (int 2) (int -3)))");
        await Assert.That(Canon("name in [\"a\", \"b\"]"))
            .IsEqualTo("(in (ref name) (list (str \"a\") (str \"b\")))");
        await Assert.That(Canon("slot in []")).IsEqualTo("(in (ref slot) (list))");
    }

    /// <summary>A list literal mixing element categories is an error.</summary>
    [Test]
    public async Task Parse_MixedListLiteral_Errors()
    {
        Diagnostic error = FirstError("x in [1, \"a\"]");

        await Assert.That(error.Code).IsEqualTo(DiagnosticCodes.MixedListLiteral);
        await Assert.That(error.Message).Contains("number");
        await Assert.That(error.Message).Contains("string");
    }

    /// <summary>null cannot appear in a list literal (spec §2 scalar-literal grammar).</summary>
    [Test]
    public async Task Parse_NullInListLiteral_Errors()
    {
        await Assert.That(FirstError("x in [1, null]").Code).IsEqualTo(DiagnosticCodes.InvalidListElement);
    }

    /// <summary>The right side of in must be a reference or list literal — not an arbitrary expression.</summary>
    [Test]
    public async Task Parse_InWithNonListOperand_Errors()
    {
        await Assert.That(FirstError("x in 5").Code).IsEqualTo(DiagnosticCodes.InvalidInOperand);
    }

    // ── References, member access, index access ──────────────────────────────────

    /// <summary>Dotted chains collapse into a single reference node — resolution happens later (spec §2).</summary>
    [Test]
    public async Task Parse_DottedReference_CollapsesToOneNode()
    {
        LanguageResult<ExpressionNode> result = ExpressionParser.Parse("round.bomb.was_planted");
        ReferenceNode reference = (ReferenceNode)result.Require();

        await Assert.That(reference.Path).IsEqualTo("round.bomb.was_planted");
        await Assert.That(reference.Segments.Length).IsEqualTo(3);
    }

    /// <summary>Index access parses for both literal and expression indices.</summary>
    [Test]
    public async Task Parse_IndexAccess_Forms()
    {
        await Assert.That(Canon("xs[0]")).IsEqualTo("(index (ref xs) (int 0))");
        await Assert.That(Canon("xs[i + 1]")).IsEqualTo("(index (ref xs) (add (ref i) (int 1)))");
        await Assert.That(Canon("weapon_map[\"ak47\"]")).IsEqualTo("(index (ref weapon_map) (str \"ak47\"))");
    }

    /// <summary>Member access after an index becomes a member-access node (not part of the reference).</summary>
    [Test]
    public async Task Parse_MemberAfterIndex_IsMemberAccess()
    {
        await Assert.That(Canon("xs[0].count")).IsEqualTo("(member (index (ref xs) (int 0)) count)");
    }

    // ── Functions ────────────────────────────────────────────────────────────────

    /// <summary>All closed-set functions parse.</summary>
    [Test]
    public async Task Parse_ClosedFunctions_Ok()
    {
        await Assert.That(Canon("min(a, b)")).IsEqualTo("(call min (ref a) (ref b))");
        await Assert.That(Canon("max(a, 1)")).IsEqualTo("(call max (ref a) (int 1))");
        await Assert.That(Canon("abs(a - b)")).IsEqualTo("(call abs (sub (ref a) (ref b)))");
        await Assert.That(Canon("floor(a)")).IsEqualTo("(call floor (ref a))");
        await Assert.That(Canon("contains(name, \"x\")")).IsEqualTo("(call contains (ref name) (str \"x\"))");
        await Assert.That(Canon("startswith(name, \"x\")")).IsEqualTo("(call startswith (ref name) (str \"x\"))");
    }

    /// <summary>Wrong argument counts fail with the expected and actual arity.</summary>
    [Test]
    public async Task Parse_FunctionArity_Errors()
    {
        Diagnostic minError = FirstError("min(1)");
        await Assert.That(minError.Code).IsEqualTo(DiagnosticCodes.WrongArity);
        await Assert.That(minError.Message).Contains("2");
        await Assert.That(minError.Message).Contains("1");

        await Assert.That(FirstError("abs(1, 2)").Code).IsEqualTo(DiagnosticCodes.WrongArity);
        await Assert.That(FirstError("floor(1, 2)").Code).IsEqualTo(DiagnosticCodes.WrongArity);
        await Assert.That(FirstError("contains(\"a\")").Code).IsEqualTo(DiagnosticCodes.WrongArity);
    }

    /// <summary>The function set is closed: calling anything else is an error with a suggestion.</summary>
    [Test]
    public async Task Parse_UnknownFunction_ErrorsWithSuggestion()
    {
        Diagnostic error = FirstError("Min(1, 2)");

        await Assert.That(error.Code).IsEqualTo(DiagnosticCodes.UnknownFunction);
        await Assert.That(error.OffendingText).IsEqualTo("Min");
        await Assert.That(error.DidYouMean).Contains("min");
    }

    // ── Hard EOF: trailing tokens ────────────────────────────────────────────────

    /// <summary>The v1 silent-truncation pin: 'a &gt; 1 1' fails loudly instead of evaluating 'a &gt; 1'.</summary>
    [Test]
    public async Task Parse_TrailingTokens_Error()
    {
        Diagnostic error = FirstError("a > 1 1");

        await Assert.That(error.Code).IsEqualTo(DiagnosticCodes.TrailingTokens);
        await Assert.That(error.OffendingText).IsEqualTo("1");
        await Assert.That(error.Span.Column).IsEqualTo(7);
    }

    /// <summary>Other trailing shapes fail the same way.</summary>
    [Test]
    public async Task Parse_TrailingTokenVariants_Error()
    {
        await Assert.That(FirstError("a b").Code).IsEqualTo(DiagnosticCodes.TrailingTokens);
        await Assert.That(FirstError("(a) )").Code).IsEqualTo(DiagnosticCodes.TrailingTokens);
    }

    /// <summary>Parenthesized spellings produce the identical node as the bare precedence (spec §5 row 2).</summary>
    [Test]
    public async Task Parse_RedundantParens_Vanish()
    {
        await Assert.That(ExpressionParser.Parse("a + (b * c)").Require())
            .IsEqualTo(ExpressionParser.Parse("a + b * c").Require());
        await Assert.That(ExpressionParser.Parse("((a + b)) * c").Require())
            .IsEqualTo(ExpressionParser.Parse("(a + b) * c").Require());
    }

    /// <summary>Incomplete input errors name what was expected.</summary>
    [Test]
    public async Task Parse_IncompleteInput_Errors()
    {
        await Assert.That(FirstError("a >").Code).IsEqualTo(DiagnosticCodes.UnexpectedToken);
        await Assert.That(FirstError("(a").Code).IsEqualTo(DiagnosticCodes.UnexpectedToken);
        await Assert.That(FirstError("a.").Code).IsEqualTo(DiagnosticCodes.UnexpectedToken);
        await Assert.That(FirstError("xs[1").Code).IsEqualTo(DiagnosticCodes.UnexpectedToken);
    }
}
