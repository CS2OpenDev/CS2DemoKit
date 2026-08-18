#region

using CS2DemoKit.Analysis.Building;
using CS2DemoKit.Analysis.Nodes;

#endregion

namespace CS2DemoKit.Analysis.Tests;

/// <summary>
///     Unit tests for <see cref="ExpressionCompiler" />, the expression DSL
///     compiler that drives YAML rule conditions and value expressions.
///     The compiler accepts strings like <c>"rule.value + 1"</c> or
///     <c>"enrich.kill.was_enemy_kill == true"</c> and produces a compiled
///     delegate; tests below exercise the parser via the lowest-friction
///     entry-point (<see cref="ExpressionCompiler.CompileNodeExpression" />)
///     so each test stays narrowly focused on one syntactic form.
///     <para>
///         Coverage: literals, identifiers, arithmetic (<c>+ - * /</c>),
///         operator precedence, comparisons, logical AND / OR, parenthesised
///         sub-expressions, node-Value resolution by name.
///     </para>
/// </summary>
[Category("Unit")]
public class ExpressionCompilerTests
{
    // ── Arithmetic ────────────────────────────────────────────────────────────

    [Test]
    [Arguments("1 + 2", 3.0)]
    [Arguments("10 - 4", 6.0)]
    [Arguments("3 * 4", 12.0)]
    [Arguments("20 / 4", 5.0)]
    [Arguments("7 + 3 * 2", 13.0)] // precedence: * before +
    [Arguments("(7 + 3) * 2", 20.0)] // parens override precedence
    [Arguments("100 - 30 - 20", 50.0)] // left-associative
    public async Task Arithmetic_FollowsStandardPrecedence(string expr, double expected)
    {
        Func<double> fn = ExpressionCompiler.CompileNodeExpression(expr,
            new Dictionary<string, object>());
        await Assert.That(fn()).IsEqualTo(expected).Within(0.0001);
    }

    /// <summary>Arithmetic_with nodes.</summary>
    [Test]
    public async Task Arithmetic_WithNodes()
    {
        Func<double> fn = ExpressionCompiler.CompileNodeExpression("kills + assists * 2",
            NodeMap(("kills", 10), ("assists", 5)));
        await Assert.That(fn()).IsEqualTo(20.0);
    }

    // ── Comparisons ───────────────────────────────────────────────────────────
    // Comparisons produce bool which CompileNodeExpression converts to double
    // (true → 1.0, false → 0.0). Asserting on the converted double is the
    // simplest way to verify the comparison semantics.
    /// <summary>Comparisons_produce expected truth values.</summary>
    [Test]
    [Arguments("5 > 3", 1.0)]
    [Arguments("5 < 3", 0.0)]
    [Arguments("3 == 3", 1.0)]
    [Arguments("3 == 4", 0.0)]
    [Arguments("3 != 4", 1.0)]
    [Arguments("3 != 3", 0.0)]
    [Arguments("5 >= 5", 1.0)]
    [Arguments("5 <= 4", 0.0)]
    public async Task Comparisons_ProduceExpectedTruthValues(string expr, double expected)
    {
        Func<double> fn = ExpressionCompiler.CompileNodeExpression(expr,
            new Dictionary<string, object>());
        await Assert.That(fn()).IsEqualTo(expected);
    }

    /// <summary>Enrich path_in arithmetic.</summary>
    [Test]
    public async Task EnrichPath_InArithmetic()
    {
        Func<double> fn = ExpressionCompiler.CompileNodeExpression(
            "enrich.hurt.capped_damage + 10",
            NodeMap(("enrich.hurt.capped_damage", 50)));
        await Assert.That(fn()).IsEqualTo(60.0);
    }

    // ── enrich.X resolution ───────────────────────────────────────────────────
    // The dotted "enrich.kill.was_enemy_kill" form is how YAML rules reference
    // enrichment nodes. The compiler builds the key as enrich. + the dotted
    // suffix and looks it up in EnrichmentNodes.
    /// <summary>Enrich path_resolves to node value.</summary>
    [Test]
    public async Task EnrichPath_ResolvesToNodeValue()
    {
        Func<double> fn = ExpressionCompiler.CompileNodeExpression(
            "enrich.kill.was_enemy_kill",
            NodeMap(("enrich.kill.was_enemy_kill", 1)));
        await Assert.That(fn()).IsEqualTo(1.0);
    }

    /// <summary>Float literal_evaluates to value.</summary>
    [Test]
    public async Task FloatLiteral_EvaluatesToValue()
    {
        Func<double> fn = ExpressionCompiler.CompileNodeExpression("3.14",
            new Dictionary<string, object>());
        await Assert.That(fn()).IsEqualTo(3.14).Within(0.0001);
    }

    // Note: bare unary minus (e.g. "-5" as a standalone expression) is NOT
    // supported by the current ExpressionCompiler — the parser has no
    // ParseUnary level above ParseMultiplicative. Subtraction inside a larger
    // expression ("0 - 5", "x - 5") works fine. If unary-minus support is
    // ever added, drop this comment and add the obvious test back in.

    // ── Identifier resolution (node Value lookup) ─────────────────────────────
    /// <summary>Identifier_resolves to node value.</summary>
    [Test]
    public async Task Identifier_ResolvesToNodeValue()
    {
        Func<double> fn = ExpressionCompiler.CompileNodeExpression("kills",
            NodeMap(("kills", 24)));
        await Assert.That(fn()).IsEqualTo(24.0);
    }

    // ── Literals ──────────────────────────────────────────────────────────────
    /// <summary>Integer literal_evaluates to value.</summary>
    [Test]
    public async Task IntegerLiteral_EvaluatesToValue()
    {
        Func<double> fn = ExpressionCompiler.CompileNodeExpression("42",
            new Dictionary<string, object>());
        await Assert.That(fn()).IsEqualTo(42.0);
    }

    // ── Logical operators ─────────────────────────────────────────────────────
    /// <summary>Logical operators_follow short circuit.</summary>
    [Test]
    [Arguments("5 > 3 && 2 < 4", 1.0)]
    [Arguments("5 > 3 && 2 > 4", 0.0)]
    [Arguments("5 < 3 || 2 < 4", 1.0)]
    [Arguments("5 < 3 || 2 > 4", 0.0)]
    [Arguments("(5 > 3) && (2 == 2)", 1.0)]
    public async Task LogicalOperators_FollowShortCircuit(string expr, double expected)
    {
        Func<double> fn = ExpressionCompiler.CompileNodeExpression(expr,
            new Dictionary<string, object>());
        await Assert.That(fn()).IsEqualTo(expected);
    }

    // ── Mixing nodes and literals ────────────────────────────────────────────
    /// <summary>Mixed expression_nodes and literals and comparison.</summary>
    [Test]
    public async Task MixedExpression_NodesAndLiteralsAndComparison()
    {
        // Common rule shape: "node.value + 1" used by counters that increment
        // on each event. The compiler must coerce int node values to double
        // for arithmetic with double literals.
        Func<double> fn = ExpressionCompiler.CompileNodeExpression(
            "round_number + 1",
            NodeMap(("round_number", 12)));
        await Assert.That(fn()).IsEqualTo(13.0);
    }

    /// <summary>Unknown identifier_throws.</summary>
    [Test]
    public async Task UnknownIdentifier_Throws()
    {
        bool threw = false;
        try
        {
            ExpressionCompiler.CompileNodeExpression("not_a_real_node",
                new Dictionary<string, object>());
        }
        catch (InvalidOperationException)
        {
            threw = true;
        }

        await Assert.That(threw).IsTrue();
    }

    // ── Builtin functions (runtime evaluation) ────────────────────────────────
    // Regression guard for the previously-latent hole: the checker admitted the closed function
    // set (min/max/abs/contains/startswith/floor) but the runtime compiler never lowered any call.
    // These assert the runtime actually EVALUATES the numeric builtins through CompileNodeExpression.
    /// <summary>Numeric builtins floor/abs/min/max evaluate to the expected value.</summary>
    [Test]
    [Arguments("floor(2.7)", 2.0)]
    [Arguments("floor(2.0)", 2.0)]
    [Arguments("floor(20 / 3)", 6.0)] // floor over a computed double (6.666…)
    [Arguments("abs(3)", 3.0)]
    [Arguments("abs(0 - 3)", 3.0)] // bare unary minus unsupported; subtract instead
    [Arguments("min(1, 5)", 1.0)]
    [Arguments("max(1, 5)", 5.0)]
    [Arguments("floor(2.7) + max(1, 5)", 7.0)] // calls compose with arithmetic
    public async Task Functions_EvaluateNumericBuiltins(string expr, double expected)
    {
        Func<double> fn = ExpressionCompiler.CompileNodeExpression(expr,
            new Dictionary<string, object>());
        await Assert.That(fn()).IsEqualTo(expected).Within(0.0001);
    }

    /// <summary>Builtins accept node references as arguments.</summary>
    [Test]
    public async Task Functions_AcceptNodeArguments()
    {
        Func<double> floorFn = ExpressionCompiler.CompileNodeExpression("floor(kills)",
            NodeMap(("kills", 7)));
        await Assert.That(floorFn()).IsEqualTo(7.0);

        Func<double> maxFn = ExpressionCompiler.CompileNodeExpression("max(kills, assists)",
            NodeMap(("kills", 3), ("assists", 8)));
        await Assert.That(maxFn()).IsEqualTo(8.0);
    }

    /// <summary>
    ///     String predicates contains/startswith evaluate through the runtime compiler; result is a
    ///     bool the node path coerces to double (true → 1.0, false → 0.0). Ordinal (case-sensitive)
    ///     semantics: <c>contains("ABC","abc")</c> is false.
    /// </summary>
    [Test]
    [Arguments("contains(\"abcd\", \"bc\")", 1.0)]
    [Arguments("contains(\"abcd\", \"xy\")", 0.0)]
    [Arguments("startswith(\"abcd\", \"ab\")", 1.0)]
    [Arguments("startswith(\"abcd\", \"bc\")", 0.0)]
    [Arguments("contains(\"ABC\", \"abc\")", 0.0)] // ordinal → case-sensitive, no match
    [Arguments("startswith(\"ABC\", \"abc\")", 0.0)] // ordinal → case-sensitive, no match
    public async Task Functions_EvaluateStringBuiltins(string expr, double expected)
    {
        Func<double> fn = ExpressionCompiler.CompileNodeExpression(expr,
            new Dictionary<string, object>());
        await Assert.That(fn()).IsEqualTo(expected);
    }

    /// <summary>
    ///     The string predicates also evaluate on the boolean condition-compilation path
    ///     (CompileNodeBoolExpression — the multi-source FromAll edge), returning a real bool.
    /// </summary>
    [Test]
    public async Task Functions_StringBuiltins_OnConditionPath()
    {
        Func<bool> hit = ExpressionCompiler.CompileNodeBoolExpression(
            "contains(\"knife_t\", \"knife\")", new Dictionary<string, object>());
        await Assert.That(hit()).IsTrue();

        Func<bool> miss = ExpressionCompiler.CompileNodeBoolExpression(
            "startswith(\"knife_t\", \"awp\")", new Dictionary<string, object>());
        await Assert.That(miss()).IsFalse();
    }

    /// <summary>A non-string operand to a string predicate is a clear compile error, not a silent coercion.</summary>
    [Test]
    public async Task Functions_StringBuiltins_NonStringOperand_Throws()
    {
        InvalidOperationException? ex = null;
        try
        {
            ExpressionCompiler.CompileNodeExpression("contains(kills, 1)", NodeMap(("kills", 1)));
        }
        catch (InvalidOperationException e)
        {
            ex = e;
        }

        await Assert.That(ex).IsNotNull();
        await Assert.That(ex!.Message).Contains("must be a string");
    }

    // ── Helper: build a synthetic node lookup from name → int value pairs ──
    private static Dictionary<string, object> NodeMap(
        params (string Name, int Value)[] entries)
    {
        Dictionary<string, object> dict = new(StringComparer.Ordinal);
        foreach ((string name, int value) in entries)
        {
            GenericValueNode<int> node = new(name);
            node.SetValue(value);
            dict[name] = node;
        }

        return dict;
    }
}
