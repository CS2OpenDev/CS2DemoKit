#region

using System.Globalization;
using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Analysis.Building;
using CS2DemoKit.Analysis.Nodes;
using CS2DemoKit.Analysis.Registry;
using CS2DemoKit.Parser.GameEvents;

#endregion

using CS2DemoKit.TestSupport;

namespace CS2DemoKit.Analysis.Tests;

/// <summary>
///     Tests for <see cref="NodeBreakpointConditions" /> — conditional node breakpoints evaluated
///     against captured snapshots, including cross-node references and game entity contexts. The
///     synthetic timeline below stands in for an evaluation: each column is a tracked node, each row
///     a message's <see cref="NodeSnapshot" />. No demo needed.
///     <para>
///         These pin the contract the debugger relies on: validation shares the exact universe the
///         scan uses (so a validated condition can't silently fail to resolve), comparisons read the
///         right snapshot lane, the rising-edge semantics fire once per false→true (and again after a
///         re-cross), and "no numeric value" never matches.
///     </para>
/// </summary>
[Category("Unit")]
public class NodeBreakpointConditionsTests
{
    // Tracked nodes by column: a counter, a bool, and a game entity context (its Name IS the
    // dotted ContextName, exactly as RuleChainBuilder creates it).
    private static IReadOnlyList<StateNode> Tracked() =>
    [
        new GenericValueNode<int>("kills"), // col 0 — Number
        new GenericBoolNode("alive"), // col 1 — Bool
        new GenericBoolNode("entity.game.freeze_period") // col 2 — Bool, entity context
    ];

    private static NodeSnapshot Num(int v) => new(true, v.ToString(CultureInfo.InvariantCulture), v);
    private static NodeSnapshot Bool(bool b) => new(b);

    // kills: 0 1 2 3 3 1 ; alive: T T F T T T ; freeze: F F F F F F
    private static NodeSnapshot[][] Timeline() =>
    [
        [Num(0), Bool(true), Bool(false)],
        [Num(1), Bool(true), Bool(false)],
        [Num(2), Bool(false), Bool(false)],
        [Num(3), Bool(true), Bool(false)],
        [Num(3), Bool(true), Bool(false)],
        [Num(1), Bool(true), Bool(false)]
    ];

    // ── Validation ────────────────────────────────────────────────────────────

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("active")]
    public async Task Validate_BlankOrActive_IsValid(string? expr) =>
        await Assert.That(NodeBreakpointConditions.Validate(expr, Tracked(), 0)).IsNull();

    [Test]
    [Arguments("value >= 3")]
    [Arguments("kills >= 2")] // cross-node numeric
    [Arguments("alive")] // cross-node bool, as a truthy value
    [Arguments("value >= 1 && alive")] // own value + cross-node bool
    [Arguments("entity.game.freeze_period")] // game entity context (dotted)
    public async Task Validate_OwnValueAndCrossRefs_AreValid(string expr) =>
        // Target col 0 = the numeric `kills` node, so `value` is numeric here.
        await Assert.That(NodeBreakpointConditions.Validate(expr, Tracked(), 0)).IsNull()
            .Because($"'{expr}' references only the target value and tracked nodes");

    [Test]
    public async Task Validate_UnknownNode_IsRejected() =>
        await Assert.That(NodeBreakpointConditions.Validate("not_a_node >= 1", Tracked(), 0)).IsNotNull()
            .Because("an identifier that isn't a tracked node must be rejected, not silently 0-hits");

    [Test]
    public async Task Validate_NonNumericComparisonOnValue_IsRejected() =>
        // Target col 0 is numeric, so `value == "de_mirage"` is a number-vs-string compare → rejected.
        await Assert.That(NodeBreakpointConditions.Validate("value == \"de_mirage\"", Tracked(), 0)).IsNotNull()
            .Because("the target value proxy is numeric; a string comparison fails to compile");

    /// <summary>`value == true` validates when the breakpoint's own node is a bool (the user's case).</summary>
    [Test]
    public async Task Validate_ValueEqualsBool_OnBoolTarget_IsValid() =>
        // Target col 1 = the `alive` bool node → `value` is a bool → `value == true` type-checks.
        await Assert.That(NodeBreakpointConditions.Validate("value == true", Tracked(), 1)).IsNull()
            .Because("a bool node's `value` is a bool, so comparing it to true must compile");

    /// <summary>The same `value == true` is rejected when the node is numeric — type mismatch surfaces.</summary>
    [Test]
    public async Task Validate_ValueEqualsBool_OnNumberTarget_IsRejected() =>
        await Assert.That(NodeBreakpointConditions.Validate("value == true", Tracked(), 0)).IsNotNull()
            .Because("a numeric node's `value` can't be compared to a bool — that should error, not 0-hit");

    // ── Hit computation ───────────────────────────────────────────────────────

    /// <summary>Own-value condition fires on the rising edge of the target's numeric value.</summary>
    [Test]
    public async Task ComputeHits_OwnValue_RisingEdgeOnly()
    {
        // kills >= 3 holds at msgs 3,4 → one hit at the crossing (3).
        List<int> hits = NodeBreakpointConditions.ComputeHits(Timeline(), Tracked(), 0, "value >= 3");
        await Assert.That(hits).IsEquivalentTo(new List<int>
        {
            3
        });
    }

    /// <summary>A cross-node condition is independent of which node carries the breakpoint.</summary>
    [Test]
    public async Task ComputeHits_CrossNode_IndependentOfTarget()
    {
        // Breakpoint on `alive` (col 1), condition references `kills`: kills>=2 holds 2,3,4 → rises at 2.
        List<int> hits = NodeBreakpointConditions.ComputeHits(Timeline(), Tracked(), 1, "kills >= 2");
        await Assert.That(hits).IsEquivalentTo(new List<int>
        {
            2
        });
    }

    /// <summary>Own value AND a cross-node bool combine; the rising edge is of the whole expression.</summary>
    [Test]
    public async Task ComputeHits_Compound_TargetValueAndCrossNodeBool()
    {
        // value>=2 && alive : value>=2 at 2,3,4,5? kills=2,3,3,1 → >=2 at 2,3,4 ; alive at 2 is FALSE.
        // both true only at 3,4 → rises at 3.
        List<int> hits = NodeBreakpointConditions.ComputeHits(Timeline(), Tracked(), 0, "value >= 2 && alive");
        await Assert.That(hits).IsEquivalentTo(new List<int>
        {
            3
        });
    }

    /// <summary>A condition that goes true, false, then true again yields two hits.</summary>
    [Test]
    public async Task ComputeHits_ReCrossAfterDrop_ProducesTwoHits()
    {
        // kills: 0 2 0 2 → kills>=2 at 1 (rise), drops at 2, rises again at 3.
        NodeSnapshot[][] snaps =
        [
            [Num(0), Bool(true), Bool(false)],
            [Num(2), Bool(true), Bool(false)],
            [Num(0), Bool(true), Bool(false)],
            [Num(2), Bool(true), Bool(false)]
        ];
        List<int> hits = NodeBreakpointConditions.ComputeHits(snaps, Tracked(), 1, "kills >= 2");
        await Assert.That(hits).IsEquivalentTo(new List<int>
        {
            1,
            3
        });
    }

    /// <summary>A dotted entity-context name resolves to its tracked column.</summary>
    [Test]
    public async Task ComputeHits_EntityContextDottedName_Resolves()
    {
        // freeze_period true at msgs 1,2 → rises at 1.
        NodeSnapshot[][] snaps =
        [
            [Num(0), Bool(true), Bool(false)],
            [Num(0), Bool(true), Bool(true)],
            [Num(0), Bool(true), Bool(true)],
            [Num(0), Bool(true), Bool(false)]
        ];
        List<int> hits = NodeBreakpointConditions.ComputeHits(snaps, Tracked(), 0, "entity.game.freeze_period");
        await Assert.That(hits).IsEquivalentTo(new List<int>
        {
            1
        });
    }

    /// <summary>A value condition where the target has no numeric value never matches (NaN guard).</summary>
    [Test]
    public async Task ComputeHits_NoNumericValue_NeverMatches()
    {
        // Target is the bool `alive` (col 1) → NumericValue is always null → fed NaN → `value <= 5`
        // is false at every message (NaN comparisons are false), so no hits — not a spurious match.
        List<int> hits = NodeBreakpointConditions.ComputeHits(Timeline(), Tracked(), 1, "value <= 5");
        await Assert.That(hits).IsEmpty();
    }

    /// <summary>An invalid (would-not-compile) condition yields no hits rather than throwing.</summary>
    [Test]
    public async Task ComputeHits_InvalidCondition_NoHits()
    {
        List<int> hits = NodeBreakpointConditions.ComputeHits(Timeline(), Tracked(), 0, "not_a_node >= 1");
        await Assert.That(hits).IsEmpty();
    }

    // ── Classification ────────────────────────────────────────────────────────

    [Test]
    public async Task Classify_DistinguishesBoolNumberText()
    {
        await Assert.That(NodeBreakpointConditions.Classify(new GenericBoolNode("b")))
            .IsEqualTo(NodeBreakpointConditions.ValueKind.Bool);
        await Assert.That(NodeBreakpointConditions.Classify(new GenericValueNode<int>("n")))
            .IsEqualTo(NodeBreakpointConditions.ValueKind.Number);
        await Assert.That(NodeBreakpointConditions.Classify(new GenericValueNode<string>("s")))
            .IsEqualTo(NodeBreakpointConditions.ValueKind.Text);
    }

    // ── Autocomplete identifiers ───────────────────────────────────────────────

    /// <summary>Every referenceable tracked node plus the value/active keywords, sorted ordinal.</summary>
    [Test]
    public async Task AvailableIdentifiers_IncludesTrackedNodesPlusKeywords()
    {
        IReadOnlyList<string> ids = NodeBreakpointConditions.AvailableIdentifiers(Tracked());
        await Assert.That(ids).Contains("value");
        await Assert.That(ids).Contains("active");
        await Assert.That(ids).Contains("kills");
        await Assert.That(ids).Contains("alive");
        await Assert.That(ids).Contains("entity.game.freeze_period");
        // Sorted ordinal — the editor list relies on a stable order.
        await Assert.That(ids.SequenceEqual(ids.OrderBy(x => x, StringComparer.Ordinal))).IsTrue();
    }

    /// <summary>A non-referenceable node kind (None) is excluded from the suggestion set.</summary>
    [Test]
    public async Task AvailableIdentifiers_ExcludesNonReferenceableKinds()
    {
        IReadOnlyList<StateNode> tracked = [new GenericValueNode<DummyEnum>("phase")];
        IReadOnlyList<string> ids = NodeBreakpointConditions.AvailableIdentifiers(tracked);
        await Assert.That(ids).DoesNotContain("phase");
        await Assert.That(ids).Contains("value"); // keywords still present
    }

    // ── Picker snippets ────────────────────────────────────────────────────────

    /// <summary>
    ///     Every node kind's picked snippet must parse against a universe containing that node —
    ///     otherwise the visual picker would insert text that immediately errors in the editor. This
    ///     pins the picker→editor contract: pick a node, get a condition you can keep or edit.
    /// </summary>
    [Test]
    public async Task SuggestPickSnippet_EveryKind_RoundTripsThroughValidate()
    {
        // Bool — active → `== true`. (Snippets reference the node by name, so the `value` target
        // column is irrelevant here — pass 0.)
        IReadOnlyList<StateNode> boolTracked = [new GenericBoolNode("alive")];
        string boolOn = NodeBreakpointConditions.SuggestPickSnippet(
            "alive", NodeBreakpointConditions.ValueKind.Bool, true, null, null);
        await Assert.That(boolOn).IsEqualTo("alive == true");
        await Assert.That(NodeBreakpointConditions.Validate(boolOn, boolTracked, 0)).IsNull();

        // Bool — inactive → `== false`.
        string boolOff = NodeBreakpointConditions.SuggestPickSnippet(
            "alive", NodeBreakpointConditions.ValueKind.Bool, false, null, null);
        await Assert.That(boolOff).IsEqualTo("alive == false");
        await Assert.That(NodeBreakpointConditions.Validate(boolOff, boolTracked, 0)).IsNull();

        // Number with a current value → `== <n>` (integral formats without a decimal point).
        IReadOnlyList<StateNode> numTracked = [new GenericValueNode<int>("kills")];
        string numVal = NodeBreakpointConditions.SuggestPickSnippet(
            "kills", NodeBreakpointConditions.ValueKind.Number, true, 3, "3");
        await Assert.That(numVal).IsEqualTo("kills == 3");
        await Assert.That(NodeBreakpointConditions.Validate(numVal, numTracked, 0)).IsNull();

        // Number with a fractional value → invariant decimal, still parses.
        string numFrac = NodeBreakpointConditions.SuggestPickSnippet(
            "kills", NodeBreakpointConditions.ValueKind.Number, true, 87.5, "87.5");
        await Assert.That(numFrac).IsEqualTo("kills == 87.5");
        await Assert.That(NodeBreakpointConditions.Validate(numFrac, numTracked, 0)).IsNull();

        // Number with NO current value → bare name (don't fabricate `== 0`); a bare node ref parses.
        string numBare = NodeBreakpointConditions.SuggestPickSnippet(
            "kills", NodeBreakpointConditions.ValueKind.Number, false, null, null);
        await Assert.That(numBare).IsEqualTo("kills");
        await Assert.That(NodeBreakpointConditions.Validate(numBare, numTracked, 0)).IsNull();

        // Text with a value → quoted string comparison.
        IReadOnlyList<StateNode> textTracked = [new GenericValueNode<string>("mapname")];
        string textVal = NodeBreakpointConditions.SuggestPickSnippet(
            "mapname", NodeBreakpointConditions.ValueKind.Text, true, null, "de_mirage");
        await Assert.That(textVal).IsEqualTo("mapname == \"de_mirage\"");
        await Assert.That(NodeBreakpointConditions.Validate(textVal, textTracked, 0)).IsNull();

        // Entity-context bool (dotted name) → `entity.game.freeze_period == true`, parses as one ref.
        IReadOnlyList<StateNode> entityTracked = [new GenericBoolNode("entity.game.freeze_period")];
        string entity = NodeBreakpointConditions.SuggestPickSnippet(
            "entity.game.freeze_period", NodeBreakpointConditions.ValueKind.Bool, true, null, null);
        await Assert.That(entity).IsEqualTo("entity.game.freeze_period == true");
        await Assert.That(NodeBreakpointConditions.Validate(entity, entityTracked, 0)).IsNull();
    }

    /// <summary>
    ///     A bool breakpoint node with <c>value == true</c> fires on the rising edge of its own active
    ///     state (the user's "NoDeathsYet" case — proves the kind-typed `value` proxy reads the bool
    ///     lane, not a fabricated numeric one).
    /// </summary>
    [Test]
    public async Task ComputeHits_ValueEqualsTrue_OnBoolTarget_RisingEdge()
    {
        // Target `alive` (col 1): T T F T T T → value==true at 0,1,3,4,5 → rises at 0, re-rises at 3.
        List<int> hits = NodeBreakpointConditions.ComputeHits(Timeline(), Tracked(), 1, "value == true");
        await Assert.That(hits).IsEquivalentTo(new List<int>
        {
            0,
            3
        });
    }

    // ── Input-event + mixed substrate (Phase F) ─────────────────────────────────

    // A player_death input event with the given fire indices; payloadAt resolves each to a death event.
    // Pass typeof(GameEvent) as parameterType (and envelope fixtures to payloadAt) for the
    // envelope-typed shape the host now compiles game events against.
    private static Dictionary<string, NodeBreakpointConditions.InputEventInfo> DeathInput(
        Type? parameterType, params int[] fireIndices)
    {
        EventRegistration reg = EventRegistry.Build().GetEvent("player_death")!;
        return new Dictionary<string, NodeBreakpointConditions.InputEventInfo>(StringComparer.OrdinalIgnoreCase)
        {
            ["player_death"] = new(reg.EventType, reg.Fields, fireIndices, parameterType)
        };
    }

    private static Dictionary<string, NodeBreakpointConditions.InputEventInfo> DeathInput(
        params int[] fireIndices) => DeathInput(null, fireIndices);

    private static PlayerDeathEvent Death(bool isHeadshot) =>
        TestGameEvents.PlayerDeathPayload(headshot: isHeadshot);

    /// <summary>A pure input-event condition is discrete over the event's fires whose payload matches.</summary>
    [Test]
    public async Task ComputeHits_InputEventOnly_DiscreteOverMatchingFires()
    {
        // Fires at 1,3,5; headshot at 1 and 5 → hits [1,5] (NOT a rising edge — each fire is its own).
        Func<int, object?> payloadAt = i => i is 1 or 5 ? Death(true)
            : i is 3 ? Death(false)
            : null;

        List<int> hits = NodeBreakpointConditions.ComputeHits(
            Timeline(), Tracked(), 1, "input.player_death.Headshot == true",
            DeathInput(1, 3, 5), payloadAt);

        await Assert.That(hits).IsEquivalentTo(new List<int>
        {
            1,
            5
        });
    }

    /// <summary>
    ///     The headline capability: a MIXED condition intersects node state and the input event at the
    ///     SAME fire — a <c>value == true</c> state term joined with an
    ///     <c>input.player_death.Headshot == true</c> event term hits only the kills where this (bool)
    ///     node was active AND the kill was a headshot. Two independent breakpoints (state OR event,
    ///     unioned across all messages) can't express this.
    ///     <para>
    ///         The state term is read <b>pre-event</b> (the snapshot row <em>before</em> the fire),
    ///         which is what makes this work on the flagship <c>NoDeathsYet</c> node — a node the
    ///         <c>player_death</c> event itself deactivates. At the fatal kill the node is already
    ///         inactive in <c>snaps[i]</c>, but still active in the pre-event row <c>snaps[i-1]</c>.
    ///         This timeline distinguishes the two readings: pre-event → <c>[1, 5]</c>; the old
    ///         post-event reading would have matched <em>nothing</em> — every fire's own row
    ///         (<c>snaps[1]</c>, <c>snaps[3]</c>, <c>snaps[5]</c>) is already deactivated.
    ///     </para>
    /// </summary>
    [Test]
    public async Task ComputeHits_Mixed_StateAndEvent_PreEventStateAtFire()
    {
        // Target col 1 = `alive` (bool); `value` = alive's PRE-event state at the fire (snaps[i-1]).
        // alive column:  T F F F T T  — fires at 1,3,5 (all headshots).
        //   fire @1: pre = snaps[0].alive = T → HIT   (the NoDeathsYet case: active before, dead at)
        //   fire @3: pre = snaps[2].alive = F → miss
        //   fire @5: pre = snaps[4].alive = T → HIT
        NodeSnapshot[][] snaps =
        [
            [Num(0), Bool(true), Bool(false)], // 0: alive (pre-state for the fire at 1)
            [Num(0), Bool(false), Bool(false)], // 1: fire — death deactivated `alive`; pre was active → HIT
            [Num(0), Bool(false), Bool(false)],
            [Num(0), Bool(false), Bool(false)], // 3: fire — pre (idx2) inactive → miss
            [Num(0), Bool(true), Bool(false)], // 4: alive again (pre-state for the fire at 5)
            [Num(0), Bool(false), Bool(false)] // 5: fire — pre (idx4) active → HIT
        ];
        Func<int, object?> payloadAt = i => i is 1 or 3 or 5 ? Death(true) : null;

        List<int> hits = NodeBreakpointConditions.ComputeHits(
            snaps, Tracked(), 1, "value == true && input.player_death.Headshot == true",
            DeathInput(1, 3, 5), payloadAt);

        await Assert.That(hits).IsEquivalentTo(new List<int>
        {
            1,
            5
        });
    }

    /// <summary>
    ///     A mixed condition whose input event fires at the very first message has no prior snapshot
    ///     row, so the pre-event state is an all-inactive baseline (not <c>snaps[0]</c>, which is the
    ///     post-event row). Here the node is <em>post-event</em> active at message 0, but the baseline
    ///     reads it inactive — so <c>value == false</c> matches, pinning that the first-message fire
    ///     reads the baseline rather than the post-event row.
    /// </summary>
    [Test]
    public async Task ComputeHits_Mixed_FireAtFirstMessage_ReadsInactiveBaseline()
    {
        NodeSnapshot[][] snaps =
        [
            [Num(0), Bool(true), Bool(false)] // 0: post-event alive=T, but pre-event baseline is inactive
        ];
        Func<int, object?> payloadAt = i => i == 0 ? Death(true) : null;

        // value==false reads the inactive baseline → matches; if it (wrongly) read snaps[0] (alive=T) it wouldn't.
        List<int> hits = NodeBreakpointConditions.ComputeHits(
            snaps, Tracked(), 1, "value == false && input.player_death.Headshot == true",
            DeathInput(0), payloadAt);

        await Assert.That(hits).IsEquivalentTo(new List<int>
        {
            0
        });
    }

    /// <summary>An input-event condition with no matching input event resolves to no hits.</summary>
    [Test]
    public async Task ComputeHits_InputEvent_NoMatchingInputEdge_NoHits()
    {
        List<int> hits = NodeBreakpointConditions.ComputeHits(
            Timeline(), Tracked(), 1, "input.player_death.Headshot == true",
            new Dictionary<string, NodeBreakpointConditions.InputEventInfo>(), _ => Death(true));
        await Assert.That(hits).IsEmpty();
    }

    [Test]
    [Arguments("input.player_death.Headshot == true")]
    [Arguments("value == true && input.player_death.Headshot == true")] // mixed
    [Arguments("input.player_death.DmgHealth > 50")]
    public async Task Validate_InputAndMixed_AreValid(string expr) =>
        await Assert.That(NodeBreakpointConditions.Validate(expr, Tracked(), 1, DeathInput(1)))
            .IsNull().Because($"'{expr}' references the node's value and its player_death input");

    [Test]
    public async Task Validate_InputEvent_UnknownField_IsRejected() =>
        await Assert.That(NodeBreakpointConditions.Validate(
            "input.player_death.NotAField == 1", Tracked(), 1, DeathInput(1))).IsNotNull();

    [Test]
    public async Task Validate_InputEvent_NoMatchingInputEdge_IsRejected() =>
        await Assert.That(NodeBreakpointConditions.Validate(
                "input.player_death.Headshot == true", Tracked(), 1,
                new Dictionary<string, NodeBreakpointConditions.InputEventInfo>())).IsNotNull()
            .Because("a node with no direct player_death input edge can't condition on it");

    [Test]
    public async Task Validate_TwoDistinctInputEvents_IsRejected() =>
        await Assert.That(NodeBreakpointConditions.Validate(
                "input.player_death.Headshot == true && input.weapon_fire.Silenced == true",
                Tracked(), 1, DeathInput(1))).IsNotNull()
            .Because("only one event fires per message, so two input events can't both hold");

    /// <summary>An "input." inside a STRING literal is a value, not an input reference (no false positive).</summary>
    [Test]
    public async Task Validate_InputInsideStringLiteral_IsPureStateNotInputRef()
    {
        IReadOnlyList<StateNode> tracked = [new GenericValueNode<string>("mapname")];
        // targetColumn 0 = text node → `value == "input.de_inferno"` is a plain text comparison.
        await Assert.That(NodeBreakpointConditions.Validate(
                "value == \"input.de_inferno\"", tracked, 0)).IsNull()
            .Because("the input.* scan must ignore quoted string contents");
    }

    // ── Envelope parameter: per-fire transport (input.<event>.tick) ─────────────
    // A game-event input compiles against the GameEvent envelope (InputEventInfo.ParameterType), so
    // `input.<event>.tick` resolves off the fire the same way `event.tick` does in a ruleset.

    [Test]
    public async Task Validate_InputEventTick_WithEnvelope_IsValid() =>
        await Assert.That(NodeBreakpointConditions.Validate(
                "input.player_death.tick > 1000", Tracked(), 1,
                DeathInput(typeof(GameEvent), 1))).IsNull()
            .Because("transport resolves off the envelope parameter");

    [Test]
    public async Task Validate_InputEventTick_PayloadTyped_IsRejected() =>
        await Assert.That(NodeBreakpointConditions.Validate(
                "input.player_death.tick > 1000", Tracked(), 1, DeathInput(1))).IsNotNull()
            .Because("tick is not a wire field; without the envelope there is nothing to resolve it against");

    [Test]
    public async Task ComputeHits_InputEventTick_DiscreteOverLateFires()
    {
        // Fires at 1,3,5 with ServerTicks 500/1500/2500 → tick > 1000 hits [3,5].
        Func<int, object?> payloadAt = i => i switch
        {
            1 => TestGameEvents.PlayerDeath(serverTick: 500),
            3 => TestGameEvents.PlayerDeath(serverTick: 1500),
            5 => TestGameEvents.PlayerDeath(serverTick: 2500),
            _ => null
        };

        List<int> hits = NodeBreakpointConditions.ComputeHits(
            Timeline(), Tracked(), 1, "input.player_death.tick > 1000",
            DeathInput(typeof(GameEvent), 1, 3, 5), payloadAt);

        await Assert.That(hits).IsEquivalentTo(new List<int>
        {
            3,
            5
        });
    }

    [Test]
    public async Task ComputeHits_InputMixed_WireFieldAndTransport_OffTheSameFire()
    {
        // Headshot AND tick > 1000 must hold on the SAME fire: 1 = early headshot, 3 = late
        // no-headshot, 5 = late headshot → only 5.
        Func<int, object?> payloadAt = i => i switch
        {
            1 => TestGameEvents.PlayerDeath(headshot: true, serverTick: 500),
            3 => TestGameEvents.PlayerDeath(headshot: false, serverTick: 1500),
            5 => TestGameEvents.PlayerDeath(headshot: true, serverTick: 2500),
            _ => null
        };

        List<int> hits = NodeBreakpointConditions.ComputeHits(
            Timeline(), Tracked(), 1,
            "input.player_death.Headshot == true && input.player_death.tick > 1000",
            DeathInput(typeof(GameEvent), 1, 3, 5), payloadAt);

        await Assert.That(hits).IsEquivalentTo(new List<int>
        {
            5
        });
    }

    private enum DummyEnum
    {
        A
    }
}
