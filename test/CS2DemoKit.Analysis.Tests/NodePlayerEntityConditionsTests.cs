#region

using System.Globalization;
using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Analysis.Building;
using CS2DemoKit.Analysis.Nodes;
using CS2DemoKit.Analysis.Plugins;
using CS2DemoKit.Analysis.Registry;
using CS2DemoKit.Parser.GameEvents;

#endregion

using CS2DemoKit.TestSupport;

namespace CS2DemoKit.Analysis.Tests;

/// <summary>
///     Bare-<c>player</c> on NODE input-event conditions. A node condition may now
///     compare an event slot-field to the filter's selected player slot, e.g.
///     <c>input.player_death.Attacker == player</c> (on a kill-streak node) or the mixed
///     <c>value == true &amp;&amp; input.player_death.Attacker == player</c>. The selected slot is a
///     compile-time constant; a negative "all players" slot short-circuits a <c>player</c>-referencing
///     condition to no hits (mirrors the edge-breakpoint invariant). No entity cache here — that is
///     Step 2; these all stay on the synchronous path.
/// </summary>
[Category("Unit")]
public class NodePlayerEntityConditionsTests
{
    private const int Player = 3; // the "selected" player slot under test

    // Tracked nodes by column: a counter and a bool (the node carrying the breakpoint).
    private static IReadOnlyList<StateNode> Tracked() =>
    [
        new GenericValueNode<int>("kills"), // col 0 — Number
        new GenericBoolNode("alive") // col 1 — Bool (the input event deactivates it)
    ];

    private static NodeSnapshot Num(int v) => new(true, v.ToString(CultureInfo.InvariantCulture), v);
    private static NodeSnapshot Bool(bool b) => new(b);

    // A player_death input event with the given fire indices.
    private static Dictionary<string, NodeBreakpointConditions.InputEventInfo> DeathInput(params int[] fireIndices)
    {
        EventRegistration reg = EventRegistry.Build().GetEvent("player_death")!;
        return new Dictionary<string, NodeBreakpointConditions.InputEventInfo>(StringComparer.OrdinalIgnoreCase)
        {
            ["player_death"] = new(reg.EventType, reg.Fields, fireIndices)
        };
    }

    private static PlayerDeathEvent DeathBy(int killerSlot) => Death(killerSlot);

    private static PlayerDeathEvent Death(int killerSlot = 0, int victimSlot = 0, bool isHeadshot = false) =>
        TestGameEvents.PlayerDeathPayload(
            userId: victimSlot, attacker: killerSlot, headshot: isHeadshot);

    private static PerPlayerEntityValueProviderRegistry Providers() =>
        PerPlayerEntityValueProviderRegistry.CreateDefault();

    // ── Hit computation: pure bare-player ───────────────────────────────────────

    /// <summary>
    ///     A pure bare-player input condition is discrete over the event's fires whose killer is the
    ///     selected player — not a rising edge, each qualifying fire is its own hit.
    /// </summary>
    [Test]
    public async Task ComputeHits_KillerEqualsPlayer_DiscreteOverSelectedPlayersKills()
    {
        // Fires at 1,3,5; killer is the selected player (3) at 1 and 5, someone else (7) at 3 → hits [1,5].
        NodeSnapshot[][] snaps =
        [
            [Num(0), Bool(true)], [Num(0), Bool(true)], [Num(0), Bool(true)],
            [Num(0), Bool(true)], [Num(0), Bool(true)], [Num(0), Bool(true)]
        ];
        Func<int, object?> payloadAt = i => i is 1 or 5 ? DeathBy(Player)
            : i is 3 ? DeathBy(7)
            : null;

        List<int> hits = NodeBreakpointConditions.ComputeHits(
            snaps, Tracked(), 1, "input.player_death.Attacker == player",
            DeathInput(1, 3, 5), payloadAt, Player);

        await Assert.That(hits).IsEquivalentTo(new List<int>
        {
            1,
            5
        });
    }

    /// <summary>None of the kills are the selected player's → no hits.</summary>
    [Test]
    public async Task ComputeHits_KillerNeverThePlayer_NoHits()
    {
        NodeSnapshot[][] snaps = [[Num(0), Bool(true)], [Num(0), Bool(true)], [Num(0), Bool(true)]];
        Func<int, object?> payloadAt = _ => DeathBy(7); // every kill by slot 7, never the player (3)

        List<int> hits = NodeBreakpointConditions.ComputeHits(
            snaps, Tracked(), 1, "input.player_death.Attacker == player",
            DeathInput(1, 2), payloadAt, Player);

        await Assert.That(hits).IsEmpty();
    }

    // ── Hit computation: mixed state + bare-player (the genuinely new node grammar) ──

    /// <summary>
    ///     A MIXED condition intersects the node's PRE-EVENT state with the bare-player comparison at the
    ///     same fire: <c>value == true &amp;&amp; input.player_death.Attacker == player</c> hits only the
    ///     selected player's kills where this (bool) node was active <em>entering</em> the kill. The state
    ///     term reads <c>snaps[i-1]</c> (pre-event, the verified Phase-F semantics); the player term reads
    ///     the payload — so both halves must agree at the same fire.
    /// </summary>
    [Test]
    public async Task ComputeHits_Mixed_PreEventStateAndKillerEqualsPlayer()
    {
        // alive col: idx0 T, idx1 F, idx2 F, idx3 F, idx4 T, idx5 F — fires at 1,3,5.
        //   fire@1: pre = snaps[0].alive = T  AND killer 3 == player → HIT
        //   fire@3: pre = snaps[2].alive = F  → miss (state fails)
        //   fire@5: pre = snaps[4].alive = T  AND killer 7 != player → miss (player fails)
        NodeSnapshot[][] snaps =
        [
            [Num(0), Bool(true)], // 0: pre-state for the fire at 1
            [Num(0), Bool(false)], // 1: fire — death deactivated `alive`; pre was active
            [Num(0), Bool(false)],
            [Num(0), Bool(false)], // 3: fire — pre (idx2) inactive
            [Num(0), Bool(true)], // 4: pre-state for the fire at 5
            [Num(0), Bool(false)] // 5: fire — pre (idx4) active
        ];
        Func<int, object?> payloadAt = i => i is 1 or 3 ? DeathBy(Player) : i is 5 ? DeathBy(7) : null;

        List<int> hits = NodeBreakpointConditions.ComputeHits(
            snaps, Tracked(), 1, "value == true && input.player_death.Attacker == player",
            DeathInput(1, 3, 5), payloadAt, Player);

        await Assert.That(hits).IsEquivalentTo(new List<int>
        {
            1
        });
    }

    // ── The "All players" short-circuit invariant ───────────────────────────────

    /// <summary>
    ///     With no player selected (negative "all players" slot), a <c>player</c>-referencing condition
    ///     yields NO hits — not "every fire." A negative slot would otherwise compare the killer against
    ///     the sentinel; the invariant makes the condition inert until a player is picked.
    /// </summary>
    [Test]
    public async Task ComputeHits_PlayerReference_AllPlayersSlot_ShortCircuitsToNoHits()
    {
        NodeSnapshot[][] snaps = [[Num(0), Bool(true)], [Num(0), Bool(true)], [Num(0), Bool(true)]];
        Func<int, object?> payloadAt = _ => DeathBy(Player); // kills that WOULD match a real selection

        List<int> hits = NodeBreakpointConditions.ComputeHits(
            snaps, Tracked(), 1, "input.player_death.Attacker == player",
            DeathInput(1, 2), payloadAt, -1);

        await Assert.That(hits).IsEmpty()
            .Because("a player-referencing condition under 'All players' is inert, not match-everything");
    }

    /// <summary>A condition that does NOT reference the player is unaffected by a negative slot.</summary>
    [Test]
    public async Task ComputeHits_NonPlayerCondition_AllPlayersSlot_StillComputes()
    {
        NodeSnapshot[][] snaps = [[Num(0), Bool(true)], [Num(0), Bool(true)], [Num(0), Bool(true)]];
        Func<int, object?> payloadAt = i => i == 1 ? DeathBy(7) : null;

        // No `player` reference → the -1 slot must not suppress hits.
        List<int> hits = NodeBreakpointConditions.ComputeHits(
            snaps, Tracked(), 1, "input.player_death.Attacker == 7",
            DeathInput(1), payloadAt, -1);

        await Assert.That(hits).IsEquivalentTo(new List<int>
        {
            1
        });
    }

    // ── Compiler usage flags ────────────────────────────────────────────────────

    /// <summary>
    ///     The compiler reports a bare-player condition references the selected player (drives the
    ///     host's recompute-on-selection + short-circuit), and needs no entity cache.
    /// </summary>
    [Test]
    public async Task Compile_BarePlayer_ReportsReferencesSelectedPlayer()
    {
        EventRegistration reg = EventRegistry.Build().GetEvent("player_death")!;
        NodeMixedCompileResult result = ExpressionCompiler.CompileNodeMixedExpression(
            "input.player_death.Attacker == player", new Dictionary<string, object>(),
            "player_death", reg.EventType, reg.Fields, Player);

        await Assert.That(result.ReferencesSelectedPlayer).IsTrue();
        await Assert.That(result.NeedsEntityCache).IsFalse();
    }

    /// <summary>A condition with no <c>player</c> reference does not flag selected-player usage.</summary>
    [Test]
    public async Task Compile_NoPlayer_DoesNotReferenceSelectedPlayer()
    {
        EventRegistration reg = EventRegistry.Build().GetEvent("player_death")!;
        NodeMixedCompileResult result = ExpressionCompiler.CompileNodeMixedExpression(
            "input.player_death.Headshot == true", new Dictionary<string, object>(),
            "player_death", reg.EventType, reg.Fields, Player);

        await Assert.That(result.ReferencesSelectedPlayer).IsFalse();
        await Assert.That(result.NeedsEntityCache).IsFalse();
    }

    // ── Validation ──────────────────────────────────────────────────────────────

    [Test]
    [Arguments("input.player_death.Attacker == player")]
    [Arguments("value == true && input.player_death.Attacker == player")] // mixed, on the bool target
    public async Task Validate_BarePlayer_WithSelectedSlot_IsValid(string expr) =>
        await Assert.That(NodeBreakpointConditions.Validate(
                expr, Tracked(), 1, DeathInput(1), Player)).IsNull()
            .Because("a bare-player comparison validates once the selected slot is bound");

    /// <summary>
    ///     Without a bound selected slot, bare <c>player</c> is an unknown identifier and the condition is
    ///     rejected (documents the binding requirement; the host always supplies a slot, ≥ -1).
    /// </summary>
    [Test]
    public async Task Validate_BarePlayer_WithoutSelectedSlot_IsRejected() =>
        await Assert.That(NodeBreakpointConditions.Validate(
                "input.player_death.Attacker == player", Tracked(), 1, DeathInput(1))).IsNotNull()
            .Because("`player` needs a bound selected slot to resolve; unbound it must error, not 0-hit");

    // ── Step 2: event-subject / selected-player entity reads ────────────────────

    /// <summary>
    ///     <c>input.player_death.UserId.entity.pawn.health &lt; 20</c> compiles to the entity-accessor
    ///     shape (<c>Func&lt;TEvent, IEntityValueAt, double&gt;</c>), flags NeedsEntityCache, and reads the
    ///     victim slot's HP through the accessor — a missing value coalesces to the provider default (0).
    /// </summary>
    [Test]
    public async Task Compile_EventSubjectEntity_FlagsNeedsCacheAndReadsVictimHealth()
    {
        EventRegistration reg = EventRegistry.Build().GetEvent("player_death")!;
        NodeMixedCompileResult result = ExpressionCompiler.CompileNodeMixedExpression(
            "input.player_death.UserId.entity.pawn.health < 20", new Dictionary<string, object>(),
            "player_death", reg.EventType, reg.Fields, 0, Providers());

        await Assert.That(result.NeedsEntityCache).IsTrue();
        await Assert.That(result.ReferencesSelectedPlayer).IsFalse();

        StubEntities low = new(new Dictionary<(string Provider, int Slot), object?>
        {
            [("entity.pawn.health", 7)] = 15
        });
        StubEntities high = new(new Dictionary<(string Provider, int Slot), object?>
        {
            [("entity.pawn.health", 7)] = 25
        });
        await Assert.That((double)result.Predicate.DynamicInvoke(Death(victimSlot: 7), low)! != 0.0).IsTrue(); // 15<20
        await Assert.That((double)result.Predicate.DynamicInvoke(Death(victimSlot: 7), high)! != 0.0).IsFalse(); // 25<20
    }

    /// <summary>
    ///     <c>player.entity.*</c> on a node reads the SELECTED slot and flags both NeedsEntityCache
    ///     and ReferencesSelectedPlayer (so it recomputes on a selection change and short-circuits when
    ///     no player is selected).
    /// </summary>
    [Test]
    public async Task Compile_SelectedPlayerEntity_FlagsBoth()
    {
        EventRegistration reg = EventRegistry.Build().GetEvent("player_death")!;
        NodeMixedCompileResult result = ExpressionCompiler.CompileNodeMixedExpression(
            "input.player_death.Headshot == true && player.entity.pawn.equipment_value > 4000",
            new Dictionary<string, object>(), "player_death", reg.EventType, reg.Fields,
            Player, Providers());

        await Assert.That(result.NeedsEntityCache).IsTrue();
        await Assert.That(result.ReferencesSelectedPlayer).IsTrue();
    }

    /// <summary>
    ///     End-to-end through the planner: an entity-read input condition returns a DEFERRED plan (no sync
    ///     hits), exposes the fire frames the cache must cover, and its Recompute filters the fires against
    ///     a per-fire positioned accessor — halting only on the low-HP victims.
    /// </summary>
    [Test]
    public async Task PlanNodeHits_EntityRead_DefersThenFiltersByPositionedAccessor()
    {
        NodeSnapshot[][] snaps = [[Num(0), Bool(true)], [Num(0), Bool(true)], [Num(0), Bool(true)]];
        Func<int, object?> payloadAt = i => i == 1 ? Death(victimSlot: 7) : i == 2 ? Death(victimSlot: 8) : null;

        NodeBreakpointConditions.NodeHitPlan plan = NodeBreakpointConditions.PlanNodeHits(
            snaps, Tracked(), 1, "input.player_death.UserId.entity.pawn.health < 20",
            DeathInput(1, 2), payloadAt, 0, Providers());

        await Assert.That(plan.NeedsEntityCache).IsTrue();
        await Assert.That(plan.SyncHits).IsNull();
        await Assert.That(plan.FireMessageIndices).IsEquivalentTo(new List<int>
        {
            1,
            2
        });

        // Victim 7 (fire 1) enters on 10 HP; victim 8 (fire 2) on 90 HP. Positioned per fire-message.
        StubEntities atFire1 = new(new Dictionary<(string Provider, int Slot), object?>
        {
            [("entity.pawn.health", 7)] = 10
        });
        StubEntities atFire2 = new(new Dictionary<(string Provider, int Slot), object?>
        {
            [("entity.pawn.health", 8)] = 90
        });
        Func<int, IEntityValueAt?> accessorAt = i => i == 1 ? atFire1 : i == 2 ? atFire2 : null;

        List<int> hits = plan.Recompute!(accessorAt);
        await Assert.That(hits).IsEquivalentTo(new List<int>
        {
            1
        }); // only the 10-HP victim < 20
    }

    /// <summary>
    ///     The genuinely-new Phase-7 surface: a MIXED condition judged on BOTH pre-event node state
    ///     (message-granular, <c>snaps[i-1]</c>) AND the pre-frame entity read (the positioned accessor) at
    ///     the same fire. Both fires have a low-HP victim, but only the fire whose pre-event state is active
    ///     hits — proving the two clauses intersect rather than union, each read at its own pre-point.
    /// </summary>
    [Test]
    public async Task PlanNodeHits_MixedStateAndEntity_IntersectsAtSameFire()
    {
        // alive col: idx0 T, idx1 F, idx2 F — fires at 1,2.
        //   fire@1: pre = snaps[0].alive = T  AND victim HP 10 < 20 → HIT
        //   fire@2: pre = snaps[1].alive = F  → miss (state fails) even though victim HP is also low
        NodeSnapshot[][] snaps = [[Num(0), Bool(true)], [Num(0), Bool(false)], [Num(0), Bool(false)]];
        Func<int, object?> payloadAt = i => i is 1 or 2 ? Death(victimSlot: 7) : null;

        NodeBreakpointConditions.NodeHitPlan plan = NodeBreakpointConditions.PlanNodeHits(
            snaps, Tracked(), 1,
            "value == true && input.player_death.UserId.entity.pawn.health < 20",
            DeathInput(1, 2), payloadAt, 0, Providers());

        await Assert.That(plan.NeedsEntityCache).IsTrue();

        StubEntities lowAtBoth = new(new Dictionary<(string Provider, int Slot), object?>
        {
            [("entity.pawn.health", 7)] = 10
        });
        List<int> hits = plan.Recompute!(_ => lowAtBoth);
        await Assert.That(hits).IsEquivalentTo(new List<int>
            {
                1
            })
            .Because("state(pre-event) ∩ entity(pre-frame) must intersect at the same fire, not union");
    }

    /// <summary>
    ///     Substrate routing for a MIXED input + <c>player.entity.*</c> condition: the planner still detects
    ///     the <c>input.player_death</c> reference and routes to the deferred matcher (not the state
    ///     substrate), and the selected-player entity clause reads the SELECTED slot through the accessor.
    ///     Guards the silent-wrong-substrate → 0-hits trap on the path with the least coverage.
    /// </summary>
    [Test]
    public async Task PlanNodeHits_MixedInputAndSelectedPlayerEntity_RoutesToMatcher()
    {
        NodeSnapshot[][] snaps = [[Num(0), Bool(true)], [Num(0), Bool(true)]];
        Func<int, object?> payloadAt = i => i == 1 ? Death(isHeadshot: true) : null;

        NodeBreakpointConditions.NodeHitPlan plan = NodeBreakpointConditions.PlanNodeHits(
            snaps, Tracked(), 1,
            "input.player_death.Headshot == true && player.entity.pawn.equipment_value > 4000",
            DeathInput(1), payloadAt, Player, Providers());

        await Assert.That(plan.NeedsEntityCache).IsTrue()
            .Because("the input.player_death reference must route to the matcher, not the state substrate");
        await Assert.That(plan.SyncHits).IsNull();

        // Selected player (3) is over-equipped → both clauses hold at the headshot fire.
        StubEntities rich = new(new Dictionary<(string Provider, int Slot), object?>
        {
            [("entity.pawn.equipment_value", Player)] = 5000
        });
        await Assert.That(plan.Recompute!(_ => rich)).IsEquivalentTo(new List<int>
        {
            1
        });

        // Under-equipped selected player → the entity clause fails, so no hit despite the headshot.
        StubEntities poor = new(new Dictionary<(string Provider, int Slot), object?>
        {
            [("entity.pawn.equipment_value", Player)] = 1000
        });
        await Assert.That(plan.Recompute!(_ => poor)).IsEmpty();
    }

    /// <summary>
    ///     An entity read whose positioned accessor is null at a fire (entity absent pre-frame) is a
    ///     non-match at that fire — never a crash (parity with the edge path).
    /// </summary>
    [Test]
    public async Task PlanNodeHits_EntityRead_NullAccessorAtFire_IsNonMatch()
    {
        NodeSnapshot[][] snaps = [[Num(0), Bool(true)], [Num(0), Bool(true)]];
        Func<int, object?> payloadAt = i => i == 1 ? Death(victimSlot: 7) : null;

        NodeBreakpointConditions.NodeHitPlan plan = NodeBreakpointConditions.PlanNodeHits(
            snaps, Tracked(), 1, "input.player_death.UserId.entity.pawn.health < 20",
            DeathInput(1), payloadAt, 0, Providers());

        List<int> hits = plan.Recompute!(_ => null); // no entity state at any fire
        await Assert.That(hits).IsEmpty();
    }

    // ── Step 2 validation ───────────────────────────────────────────────────────

    [Test]
    [Arguments("input.player_death.UserId.entity.pawn.health < 20")]
    [Arguments("input.player_death.Headshot == true && player.entity.pawn.armor >= 50")]
    public async Task Validate_GoodEntityForms_WithProviders_AreValid(string expr) =>
        await Assert.That(NodeBreakpointConditions.Validate(
                expr, Tracked(), 1, DeathInput(1), Player, Providers())).IsNull()
            .Because($"'{expr}' is a valid node entity condition once providers are bound");

    [Test]
    [Arguments("input.player_death.UserId.entity.pawn.nonsense < 1")] // unknown provider
    [Arguments("input.player_death.Weapon.entity.pawn.health < 20")] // event field that isn't a *Slot
    public async Task Validate_BadEntityForms_AreRejected(string expr) =>
        await Assert.That(NodeBreakpointConditions.Validate(
                expr, Tracked(), 1, DeathInput(1), Player, Providers())).IsNotNull()
            .Because($"'{expr}' is not a valid node entity read");

    /// <summary>
    ///     Without providers bound, an entity read fails to compile and is rejected (the host always
    ///     supplies the registry; this documents the requirement).
    /// </summary>
    [Test]
    public async Task Validate_EntityForm_WithoutProviders_IsRejected() =>
        await Assert.That(NodeBreakpointConditions.Validate(
            "input.player_death.UserId.entity.pawn.health < 20", Tracked(), 1,
            DeathInput(1), Player)).IsNotNull();

    // ── Step 3: autocomplete identifiers ────────────────────────────────────────

    /// <summary>
    ///     The node autocomplete carries the per-player / entity grammar with the <c>input.&lt;event&gt;.</c>
    ///     prefix on the event-subject read — NOT the edge's bare <c>UserId.entity.…</c>, which a node
    ///     can't parse (it names the slot through <c>input.</c>). Every suggested form is well-shaped and
    ///     the entity suggestion round-trips through <see cref="NodeBreakpointConditions.Validate" />.
    /// </summary>
    [Test]
    public async Task InputFieldIdentifiers_WithProviders_CarryInputPrefixedEntityForms()
    {
        Dictionary<string, NodeBreakpointConditions.InputEventInfo> inputs = DeathInput(1);
        IReadOnlyList<string> ids = NodeBreakpointConditions.InputFieldIdentifiers(inputs, Providers());

        await Assert.That(ids).Contains("player");
        await Assert.That(ids).Contains("input.player_death.UserId");
        await Assert.That(ids).Contains("player.entity.pawn.health");
        await Assert.That(ids).Contains("input.player_death.UserId.entity.pawn.health");
        // NOT the edge's bare form — that wouldn't parse on a node.
        await Assert.That(ids.Contains("UserId.entity.pawn.health")).IsFalse();
        await Assert.That(ids.SequenceEqual(ids.OrderBy(x => x, StringComparer.Ordinal))).IsTrue();

        // The suggested entity identifier parses as a real condition (round-trip).
        await Assert.That(NodeBreakpointConditions.Validate(
                "input.player_death.UserId.entity.pawn.health < 20", Tracked(), 1,
                inputs, Player, Providers()))
            .IsNull();
    }

    /// <summary>
    ///     The free-text event-match box uses <c>includeEntityReads: false</c> (the scope-aware rows author
    ///     entity reads): the bare <c>player</c> slot-comparison token and the <c>input.&lt;event&gt;.&lt;field&gt;</c>
    ///     shapes survive, but the entity-read grammar (<c>player.&lt;provider&gt;</c> /
    ///     <c>input.&lt;event&gt;.&lt;Slot&gt;.&lt;provider&gt;</c>) is trimmed out.
    /// </summary>
    [Test]
    public async Task InputFieldIdentifiers_WithoutEntityReads_KeepsBarePlayerDropsEntityGrammar()
    {
        Dictionary<string, NodeBreakpointConditions.InputEventInfo> inputs = DeathInput(1);
        IReadOnlyList<string> ids = NodeBreakpointConditions.InputFieldIdentifiers(inputs, Providers(), false);

        await Assert.That(ids).Contains("player");
        await Assert.That(ids).Contains("input.player_death.UserId");
        await Assert.That(ids.Contains("player.entity.pawn.health")).IsFalse();
        await Assert.That(ids.Contains("input.player_death.UserId.entity.pawn.health")).IsFalse();
    }

    // A dictionary-backed accessor keyed by (provider, slot) — stands in for the per-fire EntityValueCache.
    private sealed class StubEntities(Dictionary<(string Provider, int Slot), object?> values) : IEntityValueAt
    {
        public object? GetValue(string providerName, int slot) => values.GetValueOrDefault((providerName, slot));
    }
}
