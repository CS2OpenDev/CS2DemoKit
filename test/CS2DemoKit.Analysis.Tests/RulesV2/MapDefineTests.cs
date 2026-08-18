#region

using CS2DemoKit.Analysis.Building;
using CS2DemoKit.Analysis.Registry;
using CS2DemoKit.Analysis.Rules;
using CS2DemoKit.Analysis.Rules.Ast;
using CS2DemoKit.Analysis.Rules.Checking;
using CS2DemoKit.Analysis.Rules.Normalization;
using CS2DemoKit.Analysis.Rules.Parsing;
using CS2DemoKit.Analysis.Rules.Scopes;
using CS2DemoKit.Analysis.RulesetsV2.Compile;
using CS2DemoKit.Analysis.RulesetsV2.Model;
using CS2DemoKit.Analysis.Yaml;
using CS2DemoKit.Parser.GameEvents;

#endregion

using CS2DemoKit.TestSupport;

namespace CS2DemoKit.Analysis.Tests.RulesV2;

/// <summary>
///     Map-valued <c>define:</c> battery. A <c>define:</c> body may be a string-keyed lookup
///     table (<c>weapon_class: {ak47: rifle, awp: sniper}</c>) read through <c>ref[key]</c> (value | null).
///     Covers: the mapper/model (uniform value type, mixed-map structural error), the inlined
///     <see cref="MapLiteralNode" /> (canonical identity + order-independence), the checker's map typing
///     over the inline path, the v1 lowering, and end-to-end runtime evaluation (hit → value, miss → null).
///     Demo-free; pure config/expression layer.
/// </summary>
[Category("Unit")]
public class MapDefineTests
{
    // ── Mapper / document model ──────────────────────────────────────────────────

    private const string StringMapDoc = """
                                        ruleset: map_demo
                                        title: Map Demo
                                        for: each_player

                                        define:
                                          weapon_class: { ak47: rifle, awp: sniper, deagle: pistol }

                                        stats:
                                          kills:
                                            count: kill
                                            per: match
                                        """;
    // ── AST identity ─────────────────────────────────────────────────────────────

    private static MapLiteralNode StringMap(params (string Key, string Value)[] entries) =>
        new([.. entries.Select(e => new MapEntry(e.Key, new StringLiteralNode(e.Value)))]);

    private static MapLiteralNode NumberMap(params (string Key, long Value)[] entries) =>
        new([.. entries.Select(e => new MapEntry(e.Key, new IntLiteralNode(e.Value)))]);

    /// <summary>A map serializes as a key-sorted table of canonical entries.</summary>
    [Test]
    public async Task MapLiteral_Canonical_IsKeySortedEntries()
    {
        await Assert.That(StringMap(("awp", "sniper"), ("ak47", "rifle")).CanonicalText)
            .IsEqualTo("(map (entry \"ak47\" (str \"rifle\")) (entry \"awp\" (str \"sniper\")))");
    }

    /// <summary>Author key order is not identity-bearing: two orderings of the same table are equal.</summary>
    [Test]
    public async Task MapLiteral_KeyOrder_IsIdentityIndependent()
    {
        await Assert.That(StringMap(("ak47", "rifle"), ("awp", "sniper")))
            .IsEqualTo(StringMap(("awp", "sniper"), ("ak47", "rifle")));
    }

    /// <summary>Different tables (value change) hash apart.</summary>
    [Test]
    public async Task MapLiteral_DifferentValues_AreDistinct()
    {
        await Assert.That(StringMap(("ak47", "rifle")))
            .IsNotEqualTo(StringMap(("ak47", "smg")));
    }

    private static RulesetDocumentLoader.Outcome Load(string yaml) =>
        RulesetDocumentLoader.Load(yaml, "map.rules.yaml");

    /// <summary>A <c>{k: v}</c> mapping without <c>on:</c> maps to a string-valued map define.</summary>
    [Test]
    public async Task Mapper_StringMapDefine_ClassifiesEntriesAndType()
    {
        RulesetDocumentLoader.Outcome outcome = Load(StringMapDoc);
        await Assert.That(outcome.Diagnostics).IsEmpty();

        DefineDef define = outcome.Doc!.Defines.Single(d => d.Name == "weapon_class");
        MapDefineBody body = (MapDefineBody)define.Body;
        await Assert.That(body.ValueType).IsEqualTo(MapValueType.String);
        await Assert.That(body.Entries.Count).IsEqualTo(3);
        await Assert.That(body.Entries[0]).IsEqualTo(new MapDefineEntry("ak47", "rifle"));
    }

    /// <summary>An all-number map classifies as <see cref="MapValueType.Number" />.</summary>
    [Test]
    public async Task Mapper_NumberMapDefine_ClassifiesAsNumber()
    {
        RulesetDocumentLoader.Outcome outcome = Load(StringMapDoc.Replace(
            "{ ak47: rifle, awp: sniper, deagle: pistol }", "{ ak47: 1, awp: 3, deagle: 2 }",
            StringComparison.Ordinal));
        await Assert.That(outcome.Diagnostics).IsEmpty();

        MapDefineBody body = (MapDefineBody)outcome.Doc!.Defines.Single(d => d.Name == "weapon_class").Body;
        await Assert.That(body.ValueType).IsEqualTo(MapValueType.Number);
    }

    /// <summary>A map mixing string and number values is a structural error.</summary>
    [Test]
    public async Task Mapper_MixedMapDefine_IsStructuralError()
    {
        RulesetDocumentLoader.Outcome outcome = Load(StringMapDoc.Replace(
            "{ ak47: rifle, awp: sniper, deagle: pistol }", "{ ak47: rifle, awp: 3 }",
            StringComparison.Ordinal));

        await Assert.That(outcome.Diagnostics.Select(d => d.Code))
            .Contains(RulesetDiagnosticCodes.MixedMapDefine);
    }

    /// <summary>A mapping with <c>on:</c> is still a trigger define, not a map.</summary>
    [Test]
    public async Task Mapper_TriggerMapping_StaysTrigger()
    {
        string yaml = StringMapDoc.Replace(
            "weapon_class: { ak47: rifle, awp: sniper, deagle: pistol }",
            "enemy_kill:\n            on: kill\n            match: { enemy: true }",
            StringComparison.Ordinal);
        RulesetDocumentLoader.Outcome outcome = Load(yaml);

        DefineDef define = outcome.Doc!.Defines.Single(d => d.Name == "enemy_kill");
        await Assert.That(define.Body).IsTypeOf<TriggerDefineBody>();
    }

    // ── Inline path + checker ────────────────────────────────────────────────────

    private static ScopeEnvironment EventScope() =>
        new("where:",
        [
            ScopeSymbol.Namespace("event",
                ScopeSymbol.Value("weapon", RulesType.String),
                ScopeSymbol.Value("count", RulesType.Int))
        ]);

    private static NormalizerOptions WithMap(MapLiteralNode map, string name) =>
        new()
        {
            DefineLookup = head => head == name ? map : null
        };

    private static LanguageResult<CheckedExpression> CheckInlined(string source, MapLiteralNode map, string name,
        RulesType? expected = null)
    {
        ExpressionNode parsed = ExpressionParser.Parse(source).Require();
        ExpressionNode normalized = ExpressionNormalizer.Normalize(parsed, WithMap(map, name)).Require();
        return ExpressionChecker.Check(normalized, EventScope(), expected);
    }

    /// <summary>A map define inlines and a dynamic <c>ref[key]</c> types as the map's value type.</summary>
    [Test]
    public async Task Inline_MapLookup_TypesAsValueType()
    {
        CheckedExpression checkedExpr =
            CheckInlined("weapon_class[event.weapon]", StringMap(("ak47", "rifle")), "weapon_class").Require();
        await Assert.That(checkedExpr.ResultType).IsEqualTo(RulesType.String);

        // The define expanded to the map literal, so the checked AST is (index (map …) (ref event.weapon)).
        IndexAccessNode index = (IndexAccessNode)checkedExpr.Root;
        await Assert.That(index.Target).IsTypeOf<MapLiteralNode>();
    }

    /// <summary>A number map lookup types as int and composes in a comparison.</summary>
    [Test]
    public async Task Inline_NumberMapLookup_ComparesAsBool()
    {
        await Assert.That(CheckInlined("weapon_class[event.weapon] > 1", NumberMap(("ak47", 1)), "weapon_class")
            .Require().ResultType).IsEqualTo(RulesType.Bool);
    }

    /// <summary>A non-string key on a map is a type error.</summary>
    [Test]
    public async Task Inline_NonStringKey_IsTypeError()
    {
        LanguageResult<CheckedExpression> result =
            CheckInlined("weapon_class[event.count]", StringMap(("ak47", "rifle")), "weapon_class");
        await Assert.That(result.Success).IsFalse();
        await Assert.That(result.Diagnostics[0].Code).IsEqualTo(DiagnosticCodes.IndexType);
    }

    /// <summary>A bare (un-indexed) map value cannot be combined with an operator.</summary>
    [Test]
    public async Task Inline_BareMapRef_IsTypeError()
    {
        LanguageResult<CheckedExpression> result =
            CheckInlined("weapon_class == \"x\"", StringMap(("ak47", "rifle")), "weapon_class");
        await Assert.That(result.Success).IsFalse();
        await Assert.That(result.Diagnostics[0].Code).IsEqualTo(DiagnosticCodes.ListOperand);
    }

    // ── v1 lowering ──────────────────────────────────────────────────────────────

    /// <summary>The map lowers to a <c>{"k": v, …}[key]</c> string the v1 ExpressionCompiler parses.</summary>
    [Test]
    public async Task V1Writer_MapLookup_EmitsBraceLiteralAndSubscript()
    {
        IndexAccessNode lookup = new(
            StringMap(("ak47", "rifle"), ("awp", "sniper")), ReferenceNode.FromPath("event.Weapon"));
        await Assert.That(V1ExpressionWriter.Write(lookup))
            .IsEqualTo("{\"ak47\": \"rifle\", \"awp\": \"sniper\"}[event.Weapon]");
    }

    // ── Runtime evaluation ───────────────────────────────────────────────────────

    private static (Type Type, IReadOnlyDictionary<string, EventFieldAccessor> Fields) DeathMeta()
    {
        EventRegistration reg = EventRegistry.Build().GetEvent("player_death")!;
        return (reg.EventType, reg.Fields);
    }

    // Returns the PAYLOAD: the compiled selectors and conditions under test operate on the
    // event record, not on the fire that carried it.
    private static PlayerDeathEvent Death(string weapon) =>
        (PlayerDeathEvent)TestGameEvents.PlayerDeath(weapon: weapon).Payload!;

    /// <summary>A string-map value selector yields the mapped value on a hit and null on a miss.</summary>
    [Test]
    public async Task Runtime_StringMapValueSelector_HitReturnsValue_MissReturnsNull()
    {
        (Type type, IReadOnlyDictionary<string, EventFieldAccessor> fields) = DeathMeta();
        Func<PlayerDeathEvent, string?> selector = (Func<PlayerDeathEvent, string?>)ExpressionCompiler.CompileEventValueSelector(
            "{\"ak47\": \"rifle\", \"awp\": \"sniper\"}[event.Weapon]", type, typeof(string), fields);

        await Assert.That(selector(Death("ak47"))).IsEqualTo("rifle");
        await Assert.That(selector(Death("glock"))).IsNull(); // miss → null
    }

    /// <summary>A string-map lookup composes in a runtime condition (hit true, miss false).</summary>
    [Test]
    public async Task Runtime_StringMapCondition_ComparesHitAndMiss()
    {
        (Type type, IReadOnlyDictionary<string, EventFieldAccessor> fields) = DeathMeta();
        Func<PlayerDeathEvent, bool> predicate = (Func<PlayerDeathEvent, bool>)ExpressionCompiler.CompileEventCondition(
            "{\"ak47\": \"rifle\", \"awp\": \"sniper\"}[event.Weapon] == \"sniper\"", type, fields);

        await Assert.That(predicate(Death("awp"))).IsTrue();
        await Assert.That(predicate(Death("ak47"))).IsFalse();
        await Assert.That(predicate(Death("glock"))).IsFalse(); // miss → null == "sniper" → false
    }

    /// <summary>A number-map lookup evaluates through the lifted (null-safe) comparison path.</summary>
    [Test]
    public async Task Runtime_NumberMapCondition_ComparesLifted()
    {
        (Type type, IReadOnlyDictionary<string, EventFieldAccessor> fields) = DeathMeta();
        Func<PlayerDeathEvent, bool> predicate = (Func<PlayerDeathEvent, bool>)ExpressionCompiler.CompileEventCondition(
            "{\"ak47\": 1, \"awp\": 3}[event.Weapon] > 1", type, fields);

        await Assert.That(predicate(Death("awp"))).IsTrue(); // 3 > 1
        await Assert.That(predicate(Death("ak47"))).IsFalse(); // 1 > 1
        await Assert.That(predicate(Death("glock"))).IsFalse(); // miss → null > 1 → false
    }
}
