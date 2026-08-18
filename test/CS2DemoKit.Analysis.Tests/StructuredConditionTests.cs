#region

using CS2DemoKit.Analysis.Building;
using CS2DemoKit.Analysis.Plugins;
using CS2DemoKit.Analysis.Registry;

#endregion

namespace CS2DemoKit.Analysis.Tests;

/// <summary>
///     The pure compose/parse seam for the scope-aware breakpoint editor. The condition
///     STRING stays canonical; the structured rows are a bidirectional view. These pin the load-bearing
///     invariant: the round-trip is <b>lossless</b> (rows survive <c>Parse∘Compose</c>; arbitrary strings
///     survive <c>Compose∘Parse</c> semantically), conservative (only clean entity reads become rows, the
///     rest stays free text), and every composed string parses through the real validators.
/// </summary>
[Category("Unit")]
public class StructuredConditionTests
{
    private const string NodePrefix = "input.player_death.";
    private const string EdgePrefix = "";

    private static PerPlayerEntityValueProviderRegistry Providers() =>
        PerPlayerEntityValueProviderRegistry.CreateDefault();

    private static HashSet<string> SlotFields() =>
        new(StringComparer.Ordinal)
        {
            "UserId",
            "Attacker",
            "Assister"
        };

    private static EntityCheckRow Row(string subject, string provider, string op, string value) =>
        new(subject, provider, op, value);

    // ── Compose ─────────────────────────────────────────────────────────────────

    [Test]
    public async Task Compose_NodeVictimHealth_PrefixesWithInputEvent()
    {
        string s = StructuredCondition.Compose(
            "", [Row("UserId", "entity.pawn.health", "<", "20")], NodePrefix, Providers());
        await Assert.That(s).IsEqualTo("input.player_death.UserId.entity.pawn.health < 20");
    }

    [Test]
    public async Task Compose_EdgeVictimHealth_NoPrefix()
    {
        string s = StructuredCondition.Compose(
            "", [Row("UserId", "entity.pawn.health", "<", "20")], EdgePrefix, Providers());
        await Assert.That(s).IsEqualTo("UserId.entity.pawn.health < 20");
    }

    [Test]
    public async Task Compose_SelectedPlayer_UsesPlayerNoPrefix()
    {
        string s = StructuredCondition.Compose(
            "", [Row("player", "entity.pawn.equipment_value", ">=", "4000")], NodePrefix, Providers());
        await Assert.That(s).IsEqualTo("player.entity.pawn.equipment_value >= 4000");
    }

    [Test]
    public async Task Compose_TextProvider_QuotesValue()
    {
        string s = StructuredCondition.Compose(
            "", [Row("player", "entity.pawn.active_weapon_class", "==", "weapon_ak47")], EdgePrefix, Providers());
        await Assert.That(s).IsEqualTo("player.entity.pawn.active_weapon_class == \"weapon_ak47\"");
    }

    [Test]
    public async Task Compose_EventMatchAndRows_AreAndJoinedEventFirst()
    {
        string s = StructuredCondition.Compose(
            "Headshot == false",
            [Row("UserId", "entity.pawn.health", "<", "20"), Row("player", "entity.pawn.equipment_value", ">=", "4000")],
            EdgePrefix, Providers());
        await Assert.That(s).IsEqualTo(
            "Headshot == false && UserId.entity.pawn.health < 20 && player.entity.pawn.equipment_value >= 4000");
    }

    // ── Parse ───────────────────────────────────────────────────────────────────

    [Test]
    public async Task Parse_DecomposesEventAndRows()
    {
        StructuredCondition r = StructuredCondition.Parse(
            "Headshot == false && UserId.entity.pawn.health < 20", EdgePrefix, SlotFields(), Providers());

        await Assert.That(r.Decomposed).IsTrue();
        await Assert.That(r.EventMatch).IsEqualTo("Headshot == false");
        await Assert.That(r.Rows).IsEquivalentTo(new List<EntityCheckRow>
        {
            Row("UserId", "entity.pawn.health", "<", "20")
        });
    }

    [Test]
    public async Task Parse_NodeRow_StripsInputPrefixIntoSubject()
    {
        StructuredCondition r = StructuredCondition.Parse(
            "input.player_death.Attacker.entity.pawn.armor >= 50", NodePrefix, SlotFields(), Providers());
        await Assert.That(r.Rows).IsEquivalentTo(new List<EntityCheckRow>
        {
            Row("Attacker", "entity.pawn.armor", ">=", "50")
        });
        await Assert.That(r.EventMatch).IsEqualTo("");
    }

    [Test]
    public async Task Parse_TextProvider_StripsQuotes()
    {
        StructuredCondition r = StructuredCondition.Parse(
            "player.entity.pawn.active_weapon_class == \"weapon_ak47\"", EdgePrefix, SlotFields(), Providers());
        await Assert.That(r.Rows).IsEquivalentTo(
            new List<EntityCheckRow>
            {
                Row("player", "entity.pawn.active_weapon_class", "==", "weapon_ak47")
            });
    }

    [Test]
    public async Task Parse_TopLevelOr_IsNotDecomposed()
    {
        const string Expr = "UserId.entity.pawn.health < 20 || Headshot == true";
        StructuredCondition r = StructuredCondition.Parse(Expr, EdgePrefix, SlotFields(), Providers());
        await Assert.That(r.Decomposed).IsFalse();
        await Assert.That(r.EventMatch).IsEqualTo(Expr);
        await Assert.That(r.Rows).IsEmpty();
    }

    [Test]
    [Arguments("UserId.entity.pawn.health + 5 < 20")] // arithmetic LHS
    [Arguments("UserId.entity.pawn.health < Attacker.entity.pawn.health")] // cross-entity RHS (not a literal)
    [Arguments("event.DmgHealth > 50")] // an event-field clause, not an entity read
    public async Task Parse_NonRowClause_StaysInEventMatch(string expr)
    {
        StructuredCondition r = StructuredCondition.Parse(expr, EdgePrefix, SlotFields(), Providers());
        await Assert.That(r.Decomposed).IsTrue();
        await Assert.That(r.Rows).IsEmpty();
        await Assert.That(r.EventMatch).IsEqualTo(expr);
    }

    [Test]
    public async Task Parse_Empty_IsEmptyDecomposed()
    {
        StructuredCondition r = StructuredCondition.Parse("  ", EdgePrefix, SlotFields(), Providers());
        await Assert.That(r.Decomposed).IsTrue();
        await Assert.That(r.EventMatch).IsEqualTo("");
        await Assert.That(r.Rows).IsEmpty();
    }

    // ── Lossless round-trip (the gate) ──────────────────────────────────────────

    [Test]
    public async Task RoundTrip_RowsSurvive_ComposeThenParse()
    {
        List<EntityCheckRow> rows =
        [
            Row("UserId", "entity.pawn.health", "<", "20"),
            Row("Attacker", "entity.pawn.equipment_value", ">=", "4000"),
            Row("player", "entity.pawn.active_weapon_class", "==", "weapon_awp")
        ];

        foreach (string prefix in new[]
                 {
                     NodePrefix, EdgePrefix
                 })
        {
            string composed = StructuredCondition.Compose("Headshot == false", rows, prefix, Providers());
            StructuredCondition reparsed = StructuredCondition.Parse(composed, prefix, SlotFields(), Providers());

            await Assert.That(reparsed.EventMatch).IsEqualTo("Headshot == false");
            await Assert.That(reparsed.Rows).IsEquivalentTo(rows).Because($"rows must survive a round-trip (prefix '{prefix}')");
        }
    }

    [Test]
    public async Task RoundTrip_ArbitraryString_SurvivesParseThenCompose()
    {
        // A non-decomposable string must come back byte-identical (whole thing is the event match).
        const string Advanced = "(UserId.entity.pawn.health < 20 || Headshot == true) && Attacker == 3";
        StructuredCondition parsed = StructuredCondition.Parse(Advanced, EdgePrefix, SlotFields(), Providers());
        string recomposed = StructuredCondition.Compose(parsed.EventMatch, parsed.Rows, EdgePrefix, Providers());
        await Assert.That(recomposed).IsEqualTo(Advanced);
    }

    // ── Operator sets ───────────────────────────────────────────────────────────

    [Test]
    public async Task OpsFor_TextProvider_IsEqualityOnly()
    {
        IPerPlayerEntityValueProvider weapon = Providers().Get("entity.pawn.active_weapon_class")!;
        IPerPlayerEntityValueProvider health = Providers().Get("entity.pawn.health")!;
        await Assert.That(StructuredCondition.OpsFor(weapon)).IsEquivalentTo(new List<string>
        {
            "==",
            "!="
        });
        await Assert.That(StructuredCondition.OpsFor(health)).IsEquivalentTo(new List<string>
        {
            "==",
            "!=",
            "<=",
            ">=",
            "<",
            ">"
        });
    }

    // ── Composed strings parse through the REAL validators (no unparseable output) ──

    [Test]
    public async Task ComposedNodeCondition_ValidatesThroughNodeValidator()
    {
        EventRegistration reg = EventRegistry.Build().GetEvent("player_death")!;
        Dictionary<string, NodeBreakpointConditions.InputEventInfo> inputs = new(StringComparer.OrdinalIgnoreCase)
        {
            ["player_death"] = new NodeBreakpointConditions.InputEventInfo(reg.EventType, reg.Fields, [1])
        };

        string composed = StructuredCondition.Compose(
            "input.player_death.Headshot == false",
            [Row("UserId", "entity.pawn.health", "<", "20"), Row("player", "entity.pawn.equipment_value", ">=", "4000")],
            NodePrefix, Providers());

        string? error = NodeBreakpointConditions.Validate(
            composed, [], 0, inputs, 3, Providers());
        await Assert.That(error).IsNull().Because($"the composed string must validate, but got: {error}\n  ({composed})");
    }

    [Test]
    public async Task ComposedEdgeCondition_ValidatesThroughEdgeValidator()
    {
        EventRegistration reg = EventRegistry.Build().GetEvent("player_death")!;
        // Edge event fields use the `event.<field>` grammar (nodes use `input.<event>.<field>`); the seam
        // treats the event match as opaque free text, so realism only matters for the validator round-trip.
        string composed = StructuredCondition.Compose(
            "event.Headshot == false",
            [Row("UserId", "entity.pawn.health", "<", "20")],
            EdgePrefix, Providers());

        string? error = EdgeBreakpointConditions.Validate(composed, reg.EventType, reg.Fields, 3, Providers());
        await Assert.That(error).IsNull().Because($"the composed string must validate, but got: {error}\n  ({composed})");
    }
}
