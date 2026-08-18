#region

using CS2DemoKit.Analysis.Building;
using CS2DemoKit.Analysis.Events;
using CS2DemoKit.Analysis.Registry;
using CS2DemoKit.Parser.GameEvents;

#endregion

using CS2DemoKit.TestSupport;

namespace CS2DemoKit.Analysis.Tests;

/// <summary>
///     Tests for <see cref="EdgeBreakpointConditions" /> — the conditional edge-breakpoint seam:
///     real <see cref="EventRegistry" /> field accessors resolve <c>event.&lt;field&gt;</c>, the
///     compiled predicate returns the right bool against a constructed event, unknown fields are
///     rejected at validation, and a runtime-throwing predicate (divide-by-zero) never propagates.
///     No GUI, no decode machinery — events are positional records constructed directly.
/// </summary>
[Category("Unit")]
public class EdgeBreakpointConditionsTests
{
    private static (Type Type, IReadOnlyDictionary<string, EventFieldAccessor> Fields) DeathMeta()
    {
        EventRegistration reg = EventRegistry.Build().GetEvent("player_death")!;
        return (reg.EventType, reg.Fields);
    }

    // A PlayerDeathEvent with just the fields we vary; the rest are harmless defaults.
    private static PlayerDeathEvent Death(bool isHeadshot = false, int dmgHealth = 0, int dmgArmor = 0) =>
        TestGameEvents.PlayerDeathPayload(
            headshot: isHeadshot, dmgHealth: (short)dmgHealth, dmgArmor: (byte)dmgArmor);

    // ── Validation ────────────────────────────────────────────────────────────

    [Test]
    [Arguments(null)]
    [Arguments("")]
    public async Task Validate_Blank_IsValid(string? expr)
    {
        (Type type, IReadOnlyDictionary<string, EventFieldAccessor> fields) = DeathMeta();
        await Assert.That(EdgeBreakpointConditions.Validate(expr, type, fields)).IsNull();
    }

    [Test]
    [Arguments("event.Headshot == true")]
    [Arguments("event.Weapon == \"ak47\"")]
    [Arguments("event.DmgHealth > 50")]
    [Arguments("event.Headshot == true && event.AttackerBlind == false")]
    public async Task Validate_EventFieldComparisons_AreValid(string expr)
    {
        (Type type, IReadOnlyDictionary<string, EventFieldAccessor> fields) = DeathMeta();
        await Assert.That(EdgeBreakpointConditions.Validate(expr, type, fields)).IsNull()
            .Because($"'{expr}' references only real fields on the event");
    }

    [Test]
    public async Task Validate_UnknownField_IsRejected()
    {
        (Type type, IReadOnlyDictionary<string, EventFieldAccessor> fields) = DeathMeta();
        await Assert.That(EdgeBreakpointConditions.Validate("event.NotAField == 1", type, fields)).IsNotNull()
            .Because("a field that isn't on the event must be rejected, not silently never-match");
    }

    [Test]
    public async Task Validate_PlayerReference_IsRejected()
    {
        // `player` needs a per-player slot; a game-edge condition has none → rejected.
        (Type type, IReadOnlyDictionary<string, EventFieldAccessor> fields) = DeathMeta();
        await Assert.That(EdgeBreakpointConditions.Validate("event.Attacker == player", type, fields)).IsNotNull();
    }

    // ── Compile + invoke (the seam) ─────────────────────────────────────────────

    [Test]
    public async Task Compile_Predicate_ReturnsRightBoolPerPayload()
    {
        (Type type, IReadOnlyDictionary<string, EventFieldAccessor> fields) = DeathMeta();
        Delegate? pred = EdgeBreakpointConditions.Compile("event.Headshot == true", type, fields);
        await Assert.That(pred).IsNotNull();
        await Assert.That(pred!.DynamicInvoke(Death(true)) is true).IsTrue();
        await Assert.That(pred.DynamicInvoke(Death(false)) is true).IsFalse();
    }

    // ── FilterApplied ───────────────────────────────────────────────────────────

    [Test]
    public async Task FilterApplied_KeepsOnlyMatchingPayloads()
    {
        (Type type, IReadOnlyDictionary<string, EventFieldAccessor> fields) = DeathMeta();
        Delegate pred = EdgeBreakpointConditions.Compile("event.Headshot == true", type, fields)!;

        // applied indices 10,20,30 → payloads headshot, body, headshot → keep 10,30.
        Dictionary<int, object?> payloads = new()
        {
            [10] = Death(true),
            [20] = Death(false),
            [30] = Death(true)
        };

        List<int> hits = EdgeBreakpointConditions.FilterApplied([10, 20, 30], pred, i => payloads[i]);
        await Assert.That(hits).IsEquivalentTo(new List<int>
        {
            10,
            30
        });
    }

    /// <summary>A predicate that throws at runtime (÷0) treats that index as a non-match, never crashes.</summary>
    [Test]
    public async Task FilterApplied_PredicateThrows_IndexIsNonMatchNotCrash()
    {
        (Type type, IReadOnlyDictionary<string, EventFieldAccessor> fields) = DeathMeta();
        // Integer divide: DmgHealth / DmgArmor — throws when DmgArmor == 0.
        Delegate pred = EdgeBreakpointConditions.Compile("event.DmgHealth / event.DmgArmor == 1", type, fields)!;

        Dictionary<int, object?> payloads = new()
        {
            [0] = Death(dmgHealth: 5, dmgArmor: 5), // 5/5 == 1 → match
            [1] = Death(dmgHealth: 5, dmgArmor: 0), // ÷0 → throws → non-match (not a crash)
            [2] = Death(dmgHealth: 3, dmgArmor: 5) // 3/5 == 0 → no match
        };

        List<int> hits = EdgeBreakpointConditions.FilterApplied([0, 1, 2], pred, i => payloads[i]);
        await Assert.That(hits).IsEquivalentTo(new List<int>
        {
            0
        });
    }

    [Test]
    public async Task FilterApplied_NullPayload_IsSkipped()
    {
        (Type type, IReadOnlyDictionary<string, EventFieldAccessor> fields) = DeathMeta();
        Delegate pred = EdgeBreakpointConditions.Compile("event.Headshot == true", type, fields)!;
        List<int> hits = EdgeBreakpointConditions.FilterApplied([0], pred, _ => null);
        await Assert.That(hits).IsEmpty();
    }

    // ── Field identifiers (autocomplete) ────────────────────────────────────────

    [Test]
    public async Task FieldIdentifiers_AreEventDotPrefixedAndSorted()
    {
        (_, IReadOnlyDictionary<string, EventFieldAccessor> fields) = DeathMeta();
        IReadOnlyList<string> ids = EdgeBreakpointConditions.FieldIdentifiers(fields);
        await Assert.That(ids).Contains("event.Headshot");
        await Assert.That(ids).Contains("event.Weapon");
        await Assert.That(ids.All(s => s.StartsWith("event.", StringComparison.Ordinal))).IsTrue();
        await Assert.That(ids.SequenceEqual(ids.OrderBy(x => x, StringComparer.Ordinal))).IsTrue();
    }

    // ── Envelope parameter: per-fire transport (event.tick) ─────────────────────
    // Game-event edges compile against the GameEvent envelope, so wire fields reach through
    // Payload and the per-fire transport resolves — the same rule compiled ruleset delegates use.

    [Test]
    [Arguments("event.tick > 1000")]
    [Arguments("event.ServerTick >= 0")]
    [Arguments("event.GameTick < 999999")]
    [Arguments("event.FrameNumber >= 0")]
    [Arguments("event.Headshot == true && event.tick > 1000")]
    public async Task Validate_TransportReferences_WithEnvelope_AreValid(string expr)
    {
        (Type type, IReadOnlyDictionary<string, EventFieldAccessor> fields) = DeathMeta();
        await Assert.That(EdgeBreakpointConditions.Validate(expr, type, fields, typeof(GameEvent)))
            .IsNull().Because($"'{expr}' resolves transport off the envelope parameter");
    }

    [Test]
    public async Task Validate_EventTick_PayloadTyped_IsRejected()
    {
        // Without the envelope (the net-message shape) there is no transport to resolve against —
        // tick is not a wire field, so a payload-typed compile must reject it, not guess.
        (Type type, IReadOnlyDictionary<string, EventFieldAccessor> fields) = DeathMeta();
        await Assert.That(EdgeBreakpointConditions.Validate("event.tick > 0", type, fields)).IsNotNull();
    }

    [Test]
    public async Task Validate_PlayerEntityOverload_ResolvesTickWithEnvelope()
    {
        (Type type, IReadOnlyDictionary<string, EventFieldAccessor> fields) = DeathMeta();
        await Assert.That(EdgeBreakpointConditions.Validate(
                "event.tick > 0 && event.Attacker == player", type, fields,
                selectedPlayerSlot: 3, providers: null, parameterType: typeof(GameEvent)))
            .IsNull();
    }

    [Test]
    public async Task Compile_EventTick_FiltersFiresByServerTick()
    {
        (Type type, IReadOnlyDictionary<string, EventFieldAccessor> fields) = DeathMeta();
        Delegate pred = EdgeBreakpointConditions.Compile(
            "event.tick > 1000", type, fields, typeof(GameEvent))!;

        GameEvent[] fires =
        [
            TestGameEvents.PlayerDeath(serverTick: 500),
            TestGameEvents.PlayerDeath(serverTick: 1500),
            TestGameEvents.PlayerDeath(serverTick: 2500)
        ];
        List<int> hits = EdgeBreakpointConditions.FilterApplied([0, 1, 2], pred, i => fires[i]);
        await Assert.That(hits).IsEquivalentTo(new List<int>
        {
            1,
            2
        });
    }

    [Test]
    public async Task Compile_MixedWireAndTransport_ReadsBothOffTheFire()
    {
        (Type type, IReadOnlyDictionary<string, EventFieldAccessor> fields) = DeathMeta();
        Delegate pred = EdgeBreakpointConditions.Compile(
            "event.Headshot == true && event.tick > 1000", type, fields, typeof(GameEvent))!;

        GameEvent[] fires =
        [
            TestGameEvents.PlayerDeath(headshot: true, serverTick: 500),   // headshot, too early
            TestGameEvents.PlayerDeath(headshot: false, serverTick: 1500), // late enough, no headshot
            TestGameEvents.PlayerDeath(headshot: true, serverTick: 1500)   // both
        ];
        List<int> hits = EdgeBreakpointConditions.FilterApplied([0, 1, 2], pred, i => fires[i]);
        await Assert.That(hits).IsEquivalentTo(new List<int>
        {
            2
        });
    }

    [Test]
    public async Task Compile_TickAlias_IsServerTick()
    {
        // `tick` aliases ServerTick (the same alias the ruleset loader applies), not GameTick.
        (Type type, IReadOnlyDictionary<string, EventFieldAccessor> fields) = DeathMeta();
        Delegate pred = EdgeBreakpointConditions.Compile(
            "event.tick == event.ServerTick", type, fields, typeof(GameEvent))!;
        await Assert.That(
                pred.DynamicInvoke(TestGameEvents.PlayerDeath(serverTick: 777, gameTick: 5)) is true)
            .IsTrue();
    }

    [Test]
    public async Task Compile_SynthesizedShape_OwnFieldsAndTransport_NoPayload()
    {
        // A GameEvent SUBCLASS declaring its own fields (Payload == null): field reads cast the
        // envelope down; transport reads stay on the envelope. Same predicate, one fire, both kinds.
        EventRegistration reg = EventRegistry.Build().GetEvent("molotov_thrown")!;
        Delegate pred = EdgeBreakpointConditions.Compile(
            "event.PlayerSlot == 4 && event.tick > 100", reg.EventType, reg.Fields,
            typeof(GameEvent))!;

        MolotovThrownEvent thrown = new(FrameNumber: 3, ServerTick: 500, GameTick: 400, PlayerSlot: 4);
        MolotovThrownEvent early = thrown with { ServerTick = 50 };
        await Assert.That(pred.DynamicInvoke(thrown) is true).IsTrue();
        await Assert.That(pred.DynamicInvoke(early) is true).IsFalse();
    }
}
