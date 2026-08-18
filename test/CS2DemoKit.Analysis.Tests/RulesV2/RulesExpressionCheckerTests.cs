#region

using CS2DemoKit.Analysis.Rules;
using CS2DemoKit.Analysis.Rules.Checking;
using CS2DemoKit.Analysis.Rules.Scopes;

#endregion

namespace CS2DemoKit.Analysis.Tests.RulesV2;

/// <summary>
///     Semantic-core resolver + typed-checker battery, pinned to spec §3/§4:
///     every §3.2 coercion (legal and forbidden), the duration/instant algebra, the §3.4
///     list restrictions, §3.3 null-literal rules, out-of-scope roots naming the slot's
///     allowed roots, and did-you-mean on one-edit typos (§8). Scope environments are
///     hand-built fakes — the Catalog wiring is the v2 loader's job.
///     Pure in-memory; no demo.
/// </summary>
[Category("Unit")]
public class RulesExpressionCheckerTests
{
    /// <summary>An event-slot environment: event fields, player providers, contexts, stats, defines.</summary>
    private static ScopeEnvironment WhereSlot() =>
        new("where:",
        [
            ScopeSymbol.Namespace("event",
                ScopeSymbol.Value("Attacker", RulesType.Int),
                ScopeSymbol.Value("weapon", RulesType.String),
                ScopeSymbol.Value("headshot", RulesType.Bool),
                ScopeSymbol.Value("tick", RulesType.Instant)),
            ScopeSymbol.Namespace("player",
                ScopeSymbol.Value("health", RulesType.Int),
                ScopeSymbol.Value("name", RulesType.String),
                ScopeSymbol.Value("active_weapon_class", RulesType.String)),
            ScopeSymbol.Namespace("round",
                ScopeSymbol.Value("number", RulesType.Int),
                ScopeSymbol.Namespace("bomb",
                    ScopeSymbol.Value("was_planted", RulesType.Bool))),
            ScopeSymbol.Namespace("match",
                ScopeSymbol.Value("tick", RulesType.Instant),
                ScopeSymbol.Value("map", RulesType.String)),
            ScopeSymbol.Stat("kills", RulesType.Int),
            ScopeSymbol.Stat("damage_avg", RulesType.Float),
            ScopeSymbol.Stat("first_kill_tick", RulesType.Instant, true),
            ScopeSymbol.Stat("reaction", RulesType.Duration),
            ScopeSymbol.Stat("weapons_seen", RulesType.ListOf(RulesTypeKind.String)),
            ScopeSymbol.Stat("first_blood", RulesType.Bool, true),
            ScopeSymbol.Value("allowed_weapons", RulesType.ListOf(RulesTypeKind.String)),
            ScopeSymbol.Value("weapon_scores", RulesType.MapOf(RulesTypeKind.Int)),
            ScopeSymbol.Param("min_kills", RulesType.Int)
        ]);

    /// <summary>A when-slot environment with NO event root (spec §4: slot-dependent roots).</summary>
    private static ScopeEnvironment WhenSlot() =>
        new("when:",
        [
            ScopeSymbol.Stat("kills", RulesType.Int),
            ScopeSymbol.Namespace("round",
                ScopeSymbol.Value("number", RulesType.Int)),
            ScopeSymbol.Namespace("player",
                ScopeSymbol.Value("health", RulesType.Int))
        ]);

    private static CheckedExpression Check(string source, RulesType? expected = null) =>
        ExpressionPipeline.Analyze(source, WhereSlot(), expectedType: expected).Require();

    private static Diagnostic FirstError(string source, RulesType? expected = null)
    {
        LanguageResult<CheckedExpression> result =
            ExpressionPipeline.Analyze(source, WhereSlot(), expectedType: expected);
        return result.Diagnostics[0];
    }

    // ── Legal coercions (spec §3.2) ──────────────────────────────────────────────

    /// <summary>int → float is implicit in arithmetic and comparison when either operand is float.</summary>
    [Test]
    public async Task Check_IntToFloat_Legal()
    {
        await Assert.That(Check("kills + damage_avg").ResultType).IsEqualTo(RulesType.Float);
        await Assert.That(Check("kills > damage_avg").ResultType).IsEqualTo(RulesType.Bool);
        await Assert.That(Check("kills * 2").ResultType).IsEqualTo(RulesType.Int);
    }

    /// <summary>int → duration where a duration is demanded: comparison, arithmetic, and slot type.</summary>
    [Test]
    public async Task Check_IntToDuration_Legal()
    {
        await Assert.That(Check("reaction > 320").ResultType).IsEqualTo(RulesType.Bool);
        await Assert.That(Check("reaction + 5").ResultType).IsEqualTo(RulesType.Duration);
        await Assert.That(Check("kills", RulesType.Duration).ResultType).IsEqualTo(RulesType.Int);
    }

    /// <summary>int → instant in comparisons (a bare tick number against event.tick).</summary>
    [Test]
    public async Task Check_IntToInstant_Legal()
    {
        await Assert.That(Check("event.tick > 6400").ResultType).IsEqualTo(RulesType.Bool);
    }

    // ── Forbidden coercions (spec §3.2: no others exist) ─────────────────────────

    /// <summary>string never coerces to a number.</summary>
    [Test]
    public async Task Check_StringToNumber_Forbidden()
    {
        await Assert.That(FirstError("player.name + 1").Code).IsEqualTo(DiagnosticCodes.TypeMismatch);
        await Assert.That(FirstError("player.name > 1").Code).IsEqualTo(DiagnosticCodes.TypeMismatch);
        Diagnostic error = FirstError("event.weapon * 2");
        await Assert.That(error.Message).Contains("string");
    }

    /// <summary>bool never coerces to int.</summary>
    [Test]
    public async Task Check_BoolToInt_Forbidden()
    {
        await Assert.That(FirstError("event.headshot + 1").Code).IsEqualTo(DiagnosticCodes.TypeMismatch);
        await Assert.That(FirstError("event.headshot > 0").Code).IsEqualTo(DiagnosticCodes.TypeMismatch);
    }

    /// <summary>float never coerces to int (no truncation anywhere in the language).</summary>
    [Test]
    public async Task Check_FloatToInt_Forbidden()
    {
        Diagnostic indexError = FirstError("weapons_seen[1.5]");
        await Assert.That(indexError.Code).IsEqualTo(DiagnosticCodes.IndexType);
        await Assert.That(indexError.Message).Contains("int");
        await Assert.That(indexError.Message).Contains("float");

        // duration scales by int scalars only — float scaling would be hidden truncation.
        await Assert.That(FirstError("reaction * 1.5").Code).IsEqualTo(DiagnosticCodes.TypeMismatch);
        // and a float expression does not satisfy an int-ish time slot
        await Assert.That(FirstError("damage_avg", RulesType.Duration).Code)
            .IsEqualTo(DiagnosticCodes.ExpectedType);
    }

    /// <summary>string does not compare with numbers even under ==.</summary>
    [Test]
    public async Task Check_StringNumberEquality_Forbidden()
    {
        Diagnostic error = FirstError("event.weapon == 1");
        await Assert.That(error.Code).IsEqualTo(DiagnosticCodes.TypeMismatch);
        await Assert.That(error.Message).Contains("string");
        await Assert.That(error.Message).Contains("int");
    }

    // ── Duration / instant algebra (spec §3.1) ───────────────────────────────────

    /// <summary>instant − instant = duration.</summary>
    [Test]
    public async Task Check_InstantMinusInstant_IsDuration()
    {
        await Assert.That(Check("event.tick - first_kill_tick").ResultType).IsEqualTo(RulesType.Duration);
    }

    /// <summary>instant + instant is a type error with an instructive message.</summary>
    [Test]
    public async Task Check_InstantPlusInstant_Errors()
    {
        Diagnostic error = FirstError("event.tick + first_kill_tick");

        await Assert.That(error.Code).IsEqualTo(DiagnosticCodes.TypeMismatch);
        await Assert.That(error.Message).Contains("instant");
    }

    /// <summary>duration × int is legal (both orders); duration ÷ int too.</summary>
    [Test]
    public async Task Check_DurationTimesInt_Legal()
    {
        await Assert.That(Check("reaction * 2").ResultType).IsEqualTo(RulesType.Duration);
        await Assert.That(Check("2 * reaction").ResultType).IsEqualTo(RulesType.Duration);
        await Assert.That(Check("reaction / 2").ResultType).IsEqualTo(RulesType.Duration);
    }

    /// <summary>instant ± duration stays an instant; duration ± duration stays a duration.</summary>
    [Test]
    public async Task Check_TimeAlgebra_Combinations()
    {
        await Assert.That(Check("event.tick + reaction").ResultType).IsEqualTo(RulesType.Instant);
        await Assert.That(Check("event.tick - reaction").ResultType).IsEqualTo(RulesType.Instant);
        await Assert.That(Check("reaction + reaction").ResultType).IsEqualTo(RulesType.Duration);
        await Assert.That(Check("event.tick - 320").ResultType).IsEqualTo(RulesType.Instant);
    }

    /// <summary>Time types never coerce back to bare int, and duration−instant is meaningless.</summary>
    [Test]
    public async Task Check_TimeAlgebra_Forbidden()
    {
        await Assert.That(FirstError("reaction - event.tick").Code).IsEqualTo(DiagnosticCodes.TypeMismatch);
        await Assert.That(FirstError("reaction > event.tick").Code).IsEqualTo(DiagnosticCodes.TypeMismatch);
        await Assert.That(FirstError("event.tick * 2").Code).IsEqualTo(DiagnosticCodes.TypeMismatch);
        await Assert.That(FirstError("-event.tick").Code).IsEqualTo(DiagnosticCodes.TypeMismatch);
    }

    /// <summary>min/max/abs/floor accept durations and reject instants (spec §3.7 signatures).</summary>
    [Test]
    public async Task Check_Functions_TimeTypes()
    {
        await Assert.That(Check("abs(reaction)").ResultType).IsEqualTo(RulesType.Duration);
        await Assert.That(Check("min(reaction, 320)").ResultType).IsEqualTo(RulesType.Duration);
        await Assert.That(Check("min(kills, damage_avg)").ResultType).IsEqualTo(RulesType.Float);
        await Assert.That(Check("floor(reaction)").ResultType).IsEqualTo(RulesType.Duration);
        await Assert.That(Check("floor(kills)").ResultType).IsEqualTo(RulesType.Int);
        await Assert.That(Check("floor(damage_avg)").ResultType).IsEqualTo(RulesType.Float);
        await Assert.That(FirstError("abs(event.tick)").Code).IsEqualTo(DiagnosticCodes.TypeMismatch);
        await Assert.That(FirstError("floor(event.tick)").Code).IsEqualTo(DiagnosticCodes.TypeMismatch);
        await Assert.That(FirstError("min(event.tick, event.tick)").Code).IsEqualTo(DiagnosticCodes.TypeMismatch);
    }

    /// <summary>contains/startswith are (string, string) → bool and nothing else.</summary>
    [Test]
    public async Task Check_StringFunctions()
    {
        await Assert.That(Check("contains(player.name, \"a\")").ResultType).IsEqualTo(RulesType.Bool);
        await Assert.That(Check("startswith(event.weapon, \"ak\")").ResultType).IsEqualTo(RulesType.Bool);
        await Assert.That(FirstError("contains(kills, \"a\")").Code).IsEqualTo(DiagnosticCodes.TypeMismatch);
    }

    // ── List restrictions (spec §3.4) ────────────────────────────────────────────

    /// <summary>.count, [n], and .set are the legal list reads.</summary>
    [Test]
    public async Task Check_ListReads_Legal()
    {
        await Assert.That(Check("weapons_seen.count > 0").ResultType).IsEqualTo(RulesType.Bool);
        await Assert.That(Check("weapons_seen[0] == \"ak47\"").ResultType).IsEqualTo(RulesType.Bool);
        await Assert.That(Check("weapons_seen.set").ResultType).IsEqualTo(RulesType.Bool);
        await Assert.That(Check("player.active_weapon_class in allowed_weapons").ResultType)
            .IsEqualTo(RulesType.Bool);
    }

    /// <summary>Arithmetic and comparison on a whole list value are type errors.</summary>
    [Test]
    public async Task Check_ListOperands_Forbidden()
    {
        await Assert.That(FirstError("weapons_seen + 1").Code).IsEqualTo(DiagnosticCodes.ListOperand);
        await Assert.That(FirstError("weapons_seen == weapons_seen").Code).IsEqualTo(DiagnosticCodes.ListOperand);
        await Assert.That(FirstError("weapons_seen > 1").Code).IsEqualTo(DiagnosticCodes.ListOperand);
        Diagnostic error = FirstError("weapons_seen + 1");
        await Assert.That(error.Message).Contains(".count");
    }

    /// <summary>'in' element types must match the left scalar.</summary>
    [Test]
    public async Task Check_InElementTypes()
    {
        await Assert.That(Check("kills in [1, 2, 3]").ResultType).IsEqualTo(RulesType.Bool);
        await Assert.That(FirstError("kills in allowed_weapons").Code).IsEqualTo(DiagnosticCodes.TypeMismatch);
        await Assert.That(FirstError("kills in event.weapon").Message).Contains("list");
    }

    /// <summary>Map defines serve [key] lookup with string keys only.</summary>
    [Test]
    public async Task Check_MapLookup()
    {
        await Assert.That(Check("weapon_scores[\"ak47\"] > 10").ResultType).IsEqualTo(RulesType.Bool);
        await Assert.That(FirstError("weapon_scores[1]").Code).IsEqualTo(DiagnosticCodes.IndexType);
        await Assert.That(FirstError("weapon_scores + 1").Code).IsEqualTo(DiagnosticCodes.ListOperand);
    }

    /// <summary>Indexing a scalar is an error naming the type.</summary>
    [Test]
    public async Task Check_IndexOnScalar_Errors()
    {
        Diagnostic error = FirstError("player.health[0]");

        await Assert.That(error.Code).IsEqualTo(DiagnosticCodes.NotIndexable);
        await Assert.That(error.Message).Contains("int");
    }

    // ── .set (spec §3.5) ─────────────────────────────────────────────────────────

    /// <summary>.set is legal on scalar capture stats and on list stats.</summary>
    [Test]
    public async Task Check_SetPseudoMember_Legal()
    {
        await Assert.That(Check("first_blood.set").ResultType).IsEqualTo(RulesType.Bool);
        await Assert.That(Check("first_kill_tick.set and kills > 0").ResultType).IsEqualTo(RulesType.Bool);
        await Assert.That(Check("weapons_seen.set").ResultType).IsEqualTo(RulesType.Bool);
    }

    /// <summary>.set on a plain value is its own instructive error.</summary>
    [Test]
    public async Task Check_SetOnPlainValue_Errors()
    {
        Diagnostic error = FirstError("player.health.set");

        await Assert.That(error.Code).IsEqualTo(DiagnosticCodes.SetNotSupported);
        await Assert.That(error.Message).Contains("capture");
    }

    // ── Null literal (spec §3.3) ─────────────────────────────────────────────────

    /// <summary>The explicit null literal presence test type-checks with == and !=.</summary>
    [Test]
    public async Task Check_NullPresenceTests_Legal()
    {
        await Assert.That(Check("first_kill_tick == null").ResultType).IsEqualTo(RulesType.Bool);
        await Assert.That(Check("first_kill_tick != null").ResultType).IsEqualTo(RulesType.Bool);
        await Assert.That(Check("null == null").ResultType).IsEqualTo(RulesType.Bool);
    }

    /// <summary>null outside ==/!= is an error (ordering, arithmetic, function arguments).</summary>
    [Test]
    public async Task Check_NullElsewhere_Errors()
    {
        await Assert.That(FirstError("first_kill_tick > null").Code).IsEqualTo(DiagnosticCodes.NullUsage);
        await Assert.That(FirstError("kills + null").Code).IsEqualTo(DiagnosticCodes.NullUsage);
        await Assert.That(FirstError("min(kills, null)").Code).IsEqualTo(DiagnosticCodes.NullUsage);
    }

    // ── Logical operators ────────────────────────────────────────────────────────

    /// <summary>and/or/not demand bool operands, naming the offending side.</summary>
    [Test]
    public async Task Check_LogicalOperands()
    {
        await Assert.That(Check("event.headshot and kills > 1").ResultType).IsEqualTo(RulesType.Bool);
        Diagnostic error = FirstError("kills and event.headshot");
        await Assert.That(error.Code).IsEqualTo(DiagnosticCodes.TypeMismatch);
        await Assert.That(error.Message).Contains("left");
        await Assert.That(error.Message).Contains("int");
        await Assert.That(FirstError("not kills").Code).IsEqualTo(DiagnosticCodes.TypeMismatch);
    }

    // ── Resolution (spec §4) ─────────────────────────────────────────────────────

    /// <summary>An out-of-scope root error names the slot and its allowed roots.</summary>
    [Test]
    public async Task Check_OutOfScopeRoot_NamesAllowedRoots()
    {
        LanguageResult<CheckedExpression> result =
            ExpressionPipeline.Analyze("event.Attacker > 0", WhenSlot());

        await Assert.That(result.Success).IsFalse();
        Diagnostic error = result.Diagnostics[0];
        await Assert.That(error.Code).IsEqualTo(DiagnosticCodes.UnknownRoot);
        await Assert.That(error.Message).Contains("when:");
        await Assert.That(error.Message).Contains("kills");
        await Assert.That(error.Message).Contains("round");
        await Assert.That(error.Message).Contains("player");
        await Assert.That(error.OffendingText).IsEqualTo("event");
    }

    /// <summary>Did-you-mean fires on a one-edit member typo (Levenshtein ≤ 2, spec §8).</summary>
    [Test]
    public async Task Check_MemberTypo_DidYouMean()
    {
        Diagnostic error = FirstError("player.healt > 50");

        await Assert.That(error.Code).IsEqualTo(DiagnosticCodes.UnknownMember);
        await Assert.That(error.DidYouMean).Contains("health");
        await Assert.That(error.Message).Contains("health");
    }

    /// <summary>Did-you-mean fires on a one-edit root typo too.</summary>
    [Test]
    public async Task Check_RootTypo_DidYouMean()
    {
        Diagnostic error = FirstError("playr.health > 50");

        await Assert.That(error.Code).IsEqualTo(DiagnosticCodes.UnknownRoot);
        await Assert.That(error.DidYouMean).Contains("player");
    }

    /// <summary>Reading a namespace as a value names its members.</summary>
    [Test]
    public async Task Check_NamespaceAsValue_Errors()
    {
        Diagnostic error = FirstError("round.bomb == true");

        await Assert.That(error.Code).IsEqualTo(DiagnosticCodes.NotAValue);
        await Assert.That(error.Message).Contains("was_planted");
    }

    // ── Slot result type ─────────────────────────────────────────────────────────

    /// <summary>A slot's expected type is enforced with a language-level message.</summary>
    [Test]
    public async Task Check_ExpectedType()
    {
        await Assert.That(Check("kills > 1", RulesType.Bool).ResultType).IsEqualTo(RulesType.Bool);
        await Assert.That(Check("kills", RulesType.Float).ResultType).IsEqualTo(RulesType.Int);

        Diagnostic error = FirstError("kills + 1", RulesType.Bool);
        await Assert.That(error.Code).IsEqualTo(DiagnosticCodes.ExpectedType);
        await Assert.That(error.Message).Contains("bool");
        await Assert.That(error.Message).Contains("int");
        await Assert.That(error.Message).Contains("where:");
    }

    // ── The read set (spec §3.6) ─────────────────────────────────────────────────

    /// <summary>References are enumerable, distinct by path, in source order.</summary>
    [Test]
    public async Task Check_ReadSet_DistinctSourceOrder()
    {
        CheckedExpression @checked = Check("kills > 1 and kills < 5 and player.health > 0");

        await Assert.That(@checked.References.Count).IsEqualTo(2);
        await Assert.That(@checked.References[0].Path).IsEqualTo("kills");
        await Assert.That(@checked.References[1].Path).IsEqualTo("player.health");
        await Assert.That(@checked.References[0].IsStatReference).IsTrue();
        await Assert.That(@checked.References[0].StatPath).IsEqualTo("kills");
        await Assert.That(@checked.References[1].IsStatReference).IsFalse();
    }

    /// <summary>Stat classification survives pseudo-member tails (.count on a list stat).</summary>
    [Test]
    public async Task Check_StatReference_TailSegments()
    {
        CheckedExpression @checked = Check("weapons_seen.count > 0");

        ResolvedReference reference = @checked.References[0];
        await Assert.That(reference.IsStatReference).IsTrue();
        await Assert.That(reference.StatPath).IsEqualTo("weapons_seen");
        await Assert.That(reference.TailSegments.Length).IsEqualTo(1);
        await Assert.That(reference.TailSegments[0]).IsEqualTo("count");
        await Assert.That(reference.Type).IsEqualTo(RulesType.Int);
    }

    /// <summary>Params resolve like values with their bound type.</summary>
    [Test]
    public async Task Check_ParamReference()
    {
        CheckedExpression @checked = Check("kills >= min_kills");

        await Assert.That(@checked.ResultType).IsEqualTo(RulesType.Bool);
        await Assert.That(@checked.References[1].IsStatReference).IsFalse();
        await Assert.That(@checked.References[1].Symbol.Kind).IsEqualTo(ScopeSymbolKind.Param);
    }

    /// <summary>The checker collects multiple diagnostics in one run.</summary>
    [Test]
    public async Task Check_MultipleErrors_AllCollected()
    {
        LanguageResult<CheckedExpression> result = ExpressionPipeline.Analyze(
            "player.healt > 50 and event.wepon == 1", WhereSlot());

        await Assert.That(result.Success).IsFalse();
        await Assert.That(result.Diagnostics.Count).IsEqualTo(2);
    }
}
