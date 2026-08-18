#region

using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Analysis.Nodes;
using CS2DemoKit.Parser;
using CS2DemoKit.Parser.GameEvents;

#endregion

using CS2DemoKit.TestSupport;

namespace CS2DemoKit.Analysis.Tests;

/// <summary>
///     Rising-edge dirty routing + multi-action battery — hand-built graphs, no
///     demo file.
///     <list type="bullet">
///         <item>
///             <b>Multi-action:</b> registering a second action on the same trigger fires BOTH
///             (registration used to silently last-wins) — graph-level and per-player paths.
///         </item>
///         <item>
///             <b>Once-fired latch:</b> a conjunction flipping true→false→true within ONE
///             message fires its rising-edge actions exactly once (the routed recomputes make
///             this flip constructible; without the latch the second rise double-fires). The
///             latch is per message — a genuine fall + rise across messages fires again.
///         </item>
///         <item>
///             <b>Dirty routing:</b> a rising-edge action's declared write is visible to a
///             later-ordered logic reader in the SAME message (pre-A3b the reader was never
///             recomputed — action writes bypassed the dirty pipeline entirely).
///         </item>
///     </list>
///     Timeline events are deliberately NOT latched: every rising edge is still recorded
///     (unchanged v1 semantics) — the latch guards action invocation only.
/// </summary>
[Category("Unit")]
public class RisingEdgeRoutingTests
{
    private static DemoFrame Frame(params NetMessage[] msgs) => new()
    {
        Command = "DEM_Packet",
        FrameNumber = 0,
        ServerTick = 0,
        RawStart = 0,
        RawLength = 1,
        HeaderLength = 1,
        IsCompressed = false,
        MessageList = [.. msgs]
    };

    /// <summary>A player_team message whose Team field carries the value to write.</summary>
    private static GameEventMessage WriteX(int team) => GameEventMessage.ForSynthesizedEvent(
        TestGameEvents.PlayerTeam(-1, (byte)team));

    // ── Multi-action registration ────────────────────────────────────────────

    /// <summary>
    ///     Two actions registered on ONE trigger both fire on its rising edge — the last-wins
    ///     pin. FAILS pre-fix (only the second action fires). The shared fire counter still
    ///     counts trigger fires (1), not action invocations.
    /// </summary>
    [Test]
    public async Task TwoActionsOnOneTrigger_BothFire()
    {
        GenericValueNode<int> x = new("x");
        GenericValueNode<int> counterA = new("counter_a");
        GenericValueNode<int> counterB = new("counter_b");

        StateGraph graph = new();
        graph.AddEdge(new OnGameEventSetValue<PlayerTeamEvent, int>(graph.Root, x, e => e.Of<PlayerTeamEvent>().Team));
        ConjunctionNode trigger = new("multi_action_trigger",
            ConditionalEdge.From(x, v => v == 1, "== 1"));
        graph.AddConjunction(trigger);
        graph.AddRisingEdgeAction(trigger, () => counterA.SetValue(counterA.Value + 1), counterA);
        graph.AddRisingEdgeAction(trigger, () => counterB.SetValue(counterB.Value + 1), counterB);

        StateGraphEvaluator evaluator = new(graph);
        evaluator.Evaluate([Frame(WriteX(1))]);

        await Assert.That(counterA.Value).IsEqualTo(1)
            .Because("the FIRST registered action must fire — this is the silent last-wins pin");
        await Assert.That(counterB.Value).IsEqualTo(1)
            .Because("the second registered action must fire too");
        await Assert.That(evaluator.RisingEdgeFireCounts[trigger]).IsEqualTo(1)
            .Because("the fire counter counts trigger fires, not action invocations");
    }

    /// <summary>
    ///     The per-player materialization path is additive too: a template registering two
    ///     actions on one trigger fires both. FAILS pre-fix (last-wins in MaterializeSlot).
    /// </summary>
    [Test]
    public async Task PerPlayerTemplate_TwoActionsOnOneTrigger_BothFire()
    {
        List<(GenericValueNode<int> A, GenericValueNode<int> B)> counters = [];

        StateGraph graph = new();
        graph.AddPerPlayerTemplate(new PerPlayerNodeTemplate((slot, _, name, _) =>
        {
            GenericValueNode<int> x = new($"x_p{slot}");
            GenericValueNode<int> counterA = new($"counter_a_p{slot}");
            GenericValueNode<int> counterB = new($"counter_b_p{slot}");
            counters.Add((counterA, counterB));
            OnGameEventSetValue<PlayerTeamEvent, int> edge = new(graph.Root, x, e => e.Of<PlayerTeamEvent>().Team,
                e => e.Of<PlayerTeamEvent>().UserId == slot);
            ConjunctionNode trigger = new($"pp_trigger_p{slot}",
                ConditionalEdge.From(x, v => v == 2, "== 2"));
            return new PerPlayerNodeTemplate.MaterializedPlayer(
                slot, name, [x, counterA, counterB, trigger], [edge], [], [],
                [
                    (trigger, () => counterA.SetValue(counterA.Value + 1), counterA),
                    (trigger, () => counterB.SetValue(counterB.Value + 1), counterB)
                ]);
        }));

        GameEventMessage teamEvent = GameEventMessage.ForSynthesizedEvent(
            TestGameEvents.PlayerTeam(5, 2));
        new StateGraphEvaluator(graph).Evaluate([Frame(teamEvent)]);

        await Assert.That(counters).HasCount().EqualTo(1);
        await Assert.That(counters[0].A.Value).IsEqualTo(1)
            .Because("the first per-player action must fire — MaterializeSlot used to last-wins");
        await Assert.That(counters[0].B.Value).IsEqualTo(1);
    }

    // ── Once-fired latch ─────────────────────────────────────────────────────

    /// <summary>
    ///     The verified duplicate-fire hazard: the target conjunction flips true→false→true
    ///     within ONE message (a flipper's routed action write knocks its input down, a
    ///     restorer's routed write brings it back), producing TWO rising edges. The latch keeps
    ///     the action at exactly one invocation. Events stay unlatched: both rises are recorded.
    /// </summary>
    [Test]
    public async Task ConjunctionFlippingTrueFalseTrue_WithinOneMessage_FiresActionExactlyOnce()
    {
        GenericValueNode<int> x = new("x");
        GenericValueNode<int> y = new("y");
        GenericValueNode<int> count = new("target_count");

        StateGraph graph = new();
        graph.AddEdge(new OnGameEventSetValue<PlayerTeamEvent, int>(graph.Root, x, e => e.Of<PlayerTeamEvent>().Team));

        // Bucket order (order of AddConjunction) is load-bearing: target recomputes first
        // (rise #1), then the flipper knocks x down and hands y to the restorer; the drain
        // then recomputes target at x=0 (fall) before the restorer sets x=1 (rise #2).
        ConjunctionNode target = new("latch_target", ConditionalEdge.From(x, v => v == 1, "== 1"));
        ConjunctionNode flipper = new("latch_flipper", ConditionalEdge.From(x, v => v == 1, "== 1"));
        ConjunctionNode restorer = new("latch_restorer", ConditionalEdge.From(y, v => v == 1, "== 1"));
        graph.AddConjunction(target);
        graph.AddConjunction(flipper);
        graph.AddConjunction(restorer);

        graph.AddRisingEdgeAction(target, () => count.SetValue(count.Value + 1), count);
        graph.AddRisingEdgeAction(flipper, () => x.SetValue(0), x);
        graph.AddRisingEdgeAction(flipper, () => y.SetValue(1), y);
        graph.AddRisingEdgeAction(restorer, () => x.SetValue(1), x);

        StateGraphEvaluator evaluator = new(graph);
        RuleChainTimeline timeline = evaluator.Evaluate([Frame(WriteX(1))]);

        await Assert.That(timeline.CountFor("latch_target")).IsEqualTo(2)
            .Because("the scenario must genuinely produce two rising edges in one message — "
                     + "one recorded event per rise (events are not latched)");
        await Assert.That(count.Value).IsEqualTo(1)
            .Because("the per-message once-fired latch: the action fires exactly once despite "
                     + "the true-false-true flip");
        await Assert.That(evaluator.RisingEdgeFireCounts[target]).IsEqualTo(1);
        await Assert.That(x.Value).IsEqualTo(1)
            .Because("the restorer's routed write must land (sanity: the flip really happened)");
        await Assert.That(target.IsActive).IsTrue();
    }

    /// <summary>
    ///     The latch is per MESSAGE: a genuine fall on one message and a rise on a later message
    ///     fires the action again.
    /// </summary>
    [Test]
    public async Task Latch_ClearsBetweenMessages()
    {
        GenericValueNode<int> x = new("x");
        GenericValueNode<int> count = new("target_count");

        StateGraph graph = new();
        graph.AddEdge(new OnGameEventSetValue<PlayerTeamEvent, int>(graph.Root, x, e => e.Of<PlayerTeamEvent>().Team));
        ConjunctionNode target = new("cross_message_target",
            ConditionalEdge.From(x, v => v == 1, "== 1"));
        graph.AddConjunction(target);
        graph.AddRisingEdgeAction(target, () => count.SetValue(count.Value + 1), count);

        StateGraphEvaluator evaluator = new(graph);
        evaluator.Evaluate([
            Frame(WriteX(1)), // rise → fire
            Frame(WriteX(0)), // fall
            Frame(WriteX(1)) //  rise again → must fire again
        ]);

        await Assert.That(count.Value).IsEqualTo(2)
            .Because("the once-fired latch is scoped to a single message, not the whole run");
        await Assert.That(evaluator.RisingEdgeFireCounts[target]).IsEqualTo(2);
    }

    // ── Dirty routing: same-message visibility ───────────────────────────────

    /// <summary>
    ///     The dirty-routing win: a rising-edge action's write to a counter is visible to a
    ///     later-ordered logic reader of that counter within the SAME message. FAILS pre-fix —
    ///     the counter is written by no edge, so the reader was never bucketed for recompute
    ///     and stayed inactive forever.
    /// </summary>
    [Test]
    public async Task RisingEdgeWrite_VisibleToLaterOrderedReader_SameMessage()
    {
        GenericValueNode<int> x = new("x");
        GenericValueNode<int> count = new("highlight_count");

        StateGraph graph = new();
        graph.AddEdge(new OnGameEventSetValue<PlayerTeamEvent, int>(graph.Root, x, e => e.Of<PlayerTeamEvent>().Team));
        ConjunctionNode highlight = new("highlight", ConditionalEdge.From(x, v => v == 1, "== 1"));
        ConjunctionNode reader = new("count_reader", ConditionalEdge.From(count, v => v >= 1, ">= 1"));
        graph.AddConjunction(highlight);
        graph.AddConjunction(reader);
        graph.AddRisingEdgeAction(highlight, () => count.SetValue(count.Value + 1), count);

        RuleChainTimeline timeline = new StateGraphEvaluator(graph).Evaluate([Frame(WriteX(1))]);

        await Assert.That(reader.IsActive).IsTrue()
            .Because("the routed rising-edge write must recompute the counter's reader in the "
                     + "same message");
        await Assert.That(timeline.CountFor("count_reader")).IsEqualTo(1);
        await Assert.That(timeline.Events.Single(e => e.ChainName == "count_reader").FrameIndex)
            .IsEqualTo(0)
            .Because("the reader's rise must land on the SAME message as the highlight's");
        await Assert.That(timeline.CountFor("highlight")).IsEqualTo(1);
    }

    /// <summary>
    ///     Undeclared writes (writes: null) still work exactly as before for the action itself —
    ///     the value lands, no routing occurs, and nothing throws. (The v1 builder always
    ///     declares; this pins the tolerant contract for hand-built graphs.)
    /// </summary>
    [Test]
    public async Task UndeclaredWrite_StillInvokesAction()
    {
        GenericValueNode<int> x = new("x");
        GenericValueNode<int> count = new("undeclared_count");

        StateGraph graph = new();
        graph.AddEdge(new OnGameEventSetValue<PlayerTeamEvent, int>(graph.Root, x, e => e.Of<PlayerTeamEvent>().Team));
        ConjunctionNode trigger = new("undeclared_trigger",
            ConditionalEdge.From(x, v => v == 1, "== 1"));
        graph.AddConjunction(trigger);
        graph.AddRisingEdgeAction(trigger, () => count.SetValue(count.Value + 1));

        new StateGraphEvaluator(graph).Evaluate([Frame(WriteX(1))]);

        await Assert.That(count.Value).IsEqualTo(1);
    }
}
