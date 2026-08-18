#region

using System.Diagnostics.CodeAnalysis;
using CS2DemoKit.Analysis.Building;
using CS2DemoKit.Analysis.Plugins;
using CS2DemoKit.Analysis.Registry;
using CS2DemoKit.Parser.GameEvents;

#endregion

using CS2DemoKit.TestSupport;

namespace CS2DemoKit.Analysis.Tests;

/// <summary>
///     Tests for the per-player / entity-relative EDGE breakpoint grammar — the
///     <see cref="ExpressionCompiler.CompileEdgePlayerEntityCondition" /> seam and the player/entity
///     overloads of <see cref="EdgeBreakpointConditions" />. Step 1 covers the bare-<c>player</c>
///     comparison (the selected player's slot bound as a constant); the entity-read forms
///     (<c>&lt;SlotField&gt;.entity.&lt;provider&gt;</c>) are exercised against a stub
///     <see cref="IEntityValueAt" /> as they land. No GUI, no decode — events are positional records.
/// </summary>
[Category("Unit")]
public class EdgePlayerEntityConditionsTests
{
    private static (Type Type, IReadOnlyDictionary<string, EventFieldAccessor> Fields) DeathMeta()
    {
        EventRegistration reg = EventRegistry.Build().GetEvent("player_death")!;
        return (reg.EventType, reg.Fields);
    }

    private static PlayerDeathEvent Death(int killerSlot = 0, int victimSlot = 0) =>
        TestGameEvents.PlayerDeathPayload(userId: victimSlot, attacker: killerSlot);

    private static PerPlayerEntityValueProviderRegistry Providers() =>
        PerPlayerEntityValueProviderRegistry.CreateDefault();

    // ── Bare `player` comparison (Step 1) ───────────────────────────────────────

    /// <summary>
    ///     With a selected slot bound, `event.Attacker == player` validates and flags the
    ///     selected-player reference without needing the entity cache.
    /// </summary>
    [Test]
    public async Task BarePlayer_CompilesWithSelectedSlot_FlagsAndBool()
    {
        (Type type, IReadOnlyDictionary<string, EventFieldAccessor> fields) = DeathMeta();
        EdgeConditionCompileResult result = ExpressionCompiler.CompileEdgePlayerEntityCondition(
            "event.Attacker == player", type, fields, 3, null);

        await Assert.That(result.ReferencesSelectedPlayer).IsTrue();
        await Assert.That(result.NeedsEntityCache).IsFalse();
        await Assert.That(result.Predicate.DynamicInvoke(Death(3), NoEntities.Instance) is true).IsTrue();
        await Assert.That(result.Predicate.DynamicInvoke(Death(5), NoEntities.Instance) is true).IsFalse();
    }

    /// <summary>
    ///     The new Validate overload (slot + providers bound) accepts `player`; the old overload
    ///     (no slot) rejects it — pinning that the slot binding is what unlocks the reference.
    /// </summary>
    [Test]
    public async Task Validate_BarePlayer_ValidWithSlot_RejectedWithout()
    {
        (Type type, IReadOnlyDictionary<string, EventFieldAccessor> fields) = DeathMeta();
        await Assert.That(EdgeBreakpointConditions.Validate(
            "event.Attacker == player", type, fields, 0, null)).IsNull();
        await Assert.That(EdgeBreakpointConditions.Validate("event.Attacker == player", type, fields)).IsNotNull();
    }

    /// <summary>
    ///     A pure-event condition compiles through the player/entity path unchanged (no flags),
    ///     so the host can route every edge condition through one compile.
    /// </summary>
    [Test]
    public async Task PureEvent_ThroughPlayerEntityPath_NoFlags()
    {
        (Type type, IReadOnlyDictionary<string, EventFieldAccessor> fields) = DeathMeta();
        EdgeConditionCompileResult result = ExpressionCompiler.CompileEdgePlayerEntityCondition(
            "event.UserId == 2", type, fields, 0, null);

        await Assert.That(result.ReferencesSelectedPlayer).IsFalse();
        await Assert.That(result.NeedsEntityCache).IsFalse();
        await Assert.That(result.Predicate.DynamicInvoke(Death(victimSlot: 2), NoEntities.Instance) is true).IsTrue();
        await Assert.That(result.Predicate.DynamicInvoke(Death(victimSlot: 1), NoEntities.Instance) is true).IsFalse();
    }

    /// <summary>
    ///     FilterAppliedWithEntities keeps only the matching fires (bare-`player` + no-op accessor),
    ///     and a runtime-throwing predicate / null accessor are non-matches, never crashes.
    /// </summary>
    [Test]
    public async Task FilterAppliedWithEntities_KeepsMatching_BarePlayer()
    {
        (Type type, IReadOnlyDictionary<string, EventFieldAccessor> fields) = DeathMeta();
        EdgeConditionCompileResult result = ExpressionCompiler.CompileEdgePlayerEntityCondition(
            "event.Attacker == player", type, fields, 3, null);

        Dictionary<int, object?> payloads = new()
        {
            [10] = Death(3),
            [20] = Death(1),
            [30] = Death(3)
        };

        List<int> hits = EdgeBreakpointConditions.FilterAppliedWithEntities(
            [10, 20, 30], result.Predicate, i => payloads[i], _ => NoEntities.Instance);
        await Assert.That(hits).IsEquivalentTo(new List<int>
        {
            10,
            30
        });

        // Null accessor → every index a non-match (skip), never crashes.
        List<int> none = EdgeBreakpointConditions.FilterAppliedWithEntities(
            [10, 20, 30], result.Predicate, i => payloads[i], _ => null);
        await Assert.That(none).IsEmpty();
    }

    // ── Event-subject entity reads (Step 2) ─────────────────────────────────────

    /// <summary>
    ///     `UserId.entity.pawn.health` compared to 20 reads the victim slot's HP via the
    ///     accessor; a missing value coalesces to the provider default (0) — documenting that the
    ///     slot-below-zero / "no value never matches" guard is the host's job, not the predicate's.
    /// </summary>
    [Test]
    public async Task EventSubjectEntity_ReadsVictimSlotHealth_FlagsAndBool()
    {
        (Type type, IReadOnlyDictionary<string, EventFieldAccessor> fields) = DeathMeta();
        EdgeConditionCompileResult result = ExpressionCompiler.CompileEdgePlayerEntityCondition(
            "UserId.entity.pawn.health < 20", type, fields, 0, Providers());

        await Assert.That(result.NeedsEntityCache).IsTrue();
        await Assert.That(result.ReferencesSelectedPlayer).IsFalse();
        await Assert.That(result.SlotFields).Contains("UserId");
        await Assert.That(result.Providers).Contains("entity.pawn.health");

        StubEntities low = new(new Dictionary<(string Provider, int Slot), object?>
        {
            [("entity.pawn.health", 7)] = 15
        });
        StubEntities high = new(new Dictionary<(string Provider, int Slot), object?>
        {
            [("entity.pawn.health", 7)] = 25
        });
        StubEntities empty = new(new Dictionary<(string Provider, int Slot), object?>());

        await Assert.That(result.Predicate.DynamicInvoke(Death(victimSlot: 7), low) is true).IsTrue(); // 15 < 20
        await Assert.That(result.Predicate.DynamicInvoke(Death(victimSlot: 7), high) is true).IsFalse(); // 25 < 20
        await Assert.That(result.Predicate.DynamicInvoke(Death(victimSlot: 7), empty) is true).IsTrue(); // null→0, 0<20
    }

    /// <summary>`player.entity.pawn.equipment_value > 4000` reads the SELECTED slot's value.</summary>
    [Test]
    public async Task SelectedPlayerEntity_ReadsSelectedSlot_Flags()
    {
        (Type type, IReadOnlyDictionary<string, EventFieldAccessor> fields) = DeathMeta();
        EdgeConditionCompileResult result = ExpressionCompiler.CompileEdgePlayerEntityCondition(
            "player.entity.pawn.equipment_value > 4000", type, fields, 3, Providers());

        await Assert.That(result.NeedsEntityCache).IsTrue();
        await Assert.That(result.ReferencesSelectedPlayer).IsTrue();
        await Assert.That(result.Providers).Contains("entity.pawn.equipment_value");

        StubEntities rich = new(new Dictionary<(string Provider, int Slot), object?>
        {
            [("entity.pawn.equipment_value", 3)] = 5000
        });
        await Assert.That(result.Predicate.DynamicInvoke(Death(), rich) is true).IsTrue();
        await Assert.That(result.Predicate.DynamicInvoke(Death(), new StubEntities(new Dictionary<(string Provider, int Slot), object?>())) is true).IsFalse(); // 0 > 4000
    }

    /// <summary>
    ///     The headline mix: selected-player comparison AND an event-subject entity read intersect
    ///     at the same fire — both flags set, AND semantics honoured.
    /// </summary>
    [Test]
    public async Task Mixed_SelectedPlayerCompare_AndVictimEntity()
    {
        (Type type, IReadOnlyDictionary<string, EventFieldAccessor> fields) = DeathMeta();
        EdgeConditionCompileResult result = ExpressionCompiler.CompileEdgePlayerEntityCondition(
            "event.Attacker == player && UserId.entity.pawn.health < 20", type, fields,
            2, Providers());

        await Assert.That(result.ReferencesSelectedPlayer).IsTrue();
        await Assert.That(result.NeedsEntityCache).IsTrue();

        StubEntities lowVictim = new(new Dictionary<(string Provider, int Slot), object?>
        {
            [("entity.pawn.health", 7)] = 10
        });
        await Assert.That(result.Predicate.DynamicInvoke(Death(2, 7), lowVictim) is true).IsTrue();
        // Killer isn't the selected player → no match even though the victim was low.
        await Assert.That(result.Predicate.DynamicInvoke(Death(5, 7), lowVictim) is true).IsFalse();
    }

    // ── Validation rejections ───────────────────────────────────────────────────

    [Test]
    [Arguments("UserId.entity.pawn.nonsense < 1")] // unknown provider
    [Arguments("Weapon.entity.pawn.health < 20")] // event field that isn't a *Slot
    [Arguments("NotASlot.entity.pawn.health < 20")] // identifier that isn't an event field
    public async Task Validate_BadEntityForms_AreRejected(string expr)
    {
        (Type type, IReadOnlyDictionary<string, EventFieldAccessor> fields) = DeathMeta();
        await Assert.That(EdgeBreakpointConditions.Validate(expr, type, fields, 0, Providers()))
            .IsNotNull().Because($"'{expr}' is not a valid entity read");
    }

    [Test]
    [Arguments("UserId.entity.pawn.health < 20")]
    [Arguments("player.entity.pawn.armor >= 50")]
    [Arguments("event.Attacker == player && Attacker.entity.pawn.equipment_value > 4000")]
    public async Task Validate_GoodEntityForms_AreValid(string expr)
    {
        (Type type, IReadOnlyDictionary<string, EventFieldAccessor> fields) = DeathMeta();
        await Assert.That(EdgeBreakpointConditions.Validate(expr, type, fields, 0, Providers()))
            .IsNull().Because($"'{expr}' is a valid player/entity edge condition");
    }

    // ── Autocomplete identifiers ────────────────────────────────────────────────

    [Test]
    public async Task FieldIdentifiers_IncludePlayerAndEntityForms()
    {
        (_, IReadOnlyDictionary<string, EventFieldAccessor> fields) = DeathMeta();
        IReadOnlyList<string> ids = EdgeBreakpointConditions.FieldIdentifiers(fields, Providers());
        await Assert.That(ids).Contains("player");
        await Assert.That(ids).Contains("event.UserId");
        await Assert.That(ids).Contains("player.entity.pawn.health");
        await Assert.That(ids).Contains("UserId.entity.pawn.health");
        await Assert.That(ids.SequenceEqual(ids.OrderBy(x => x, StringComparer.Ordinal))).IsTrue();
    }

    /// <summary>
    ///     The free-text event-match box uses <c>includeEntityReads: false</c> (the scope-aware rows author
    ///     entity reads): the bare <c>player</c> token and <c>event.&lt;field&gt;</c> shapes survive, but the
    ///     entity-read grammar (<c>player.&lt;provider&gt;</c> / <c>&lt;Slot&gt;.entity.&lt;provider&gt;</c>) is
    ///     trimmed out.
    /// </summary>
    [Test]
    public async Task FieldIdentifiers_WithoutEntityReads_KeepsBarePlayerDropsEntityGrammar()
    {
        (_, IReadOnlyDictionary<string, EventFieldAccessor> fields) = DeathMeta();
        IReadOnlyList<string> ids = EdgeBreakpointConditions.FieldIdentifiers(fields, Providers(), false);
        await Assert.That(ids).Contains("player");
        await Assert.That(ids).Contains("event.UserId");
        await Assert.That(ids.Contains("player.entity.pawn.health")).IsFalse();
        await Assert.That(ids.Contains("UserId.entity.pawn.health")).IsFalse();
    }

    // An accessor that returns nothing — bare-`player` predicates never read it.
    private sealed class NoEntities : IEntityValueAt
    {
        public static readonly NoEntities Instance = new();

        [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "This is a special case of the IEntityValueAt interface that must return null for GetValue")]
        public object? GetValue(string providerName, int slot) => null;
    }

    // A dictionary-backed accessor keyed by (provider, slot) — stands in for the per-fire EntityValueCache.
    private sealed class StubEntities(Dictionary<(string Provider, int Slot), object?> values) : IEntityValueAt
    {
        public object? GetValue(string providerName, int slot) => values.GetValueOrDefault((providerName, slot));
    }
}
