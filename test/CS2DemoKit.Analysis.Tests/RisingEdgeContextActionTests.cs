#region

using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Analysis.Nodes;
using CS2DemoKit.Parser;
using CS2DemoKit.Parser.GameEvents;

#endregion

using CS2DemoKit.TestSupport;

namespace CS2DemoKit.Analysis.Tests;

/// <summary>
///     A1 (rich highlight emission) evaluator battery: the additive context-arm rising-edge
///     action (<c>Action&lt;int, int&gt;</c>, handed the firing <c>(frameIdx, tick)</c>) — hand-built
///     graphs, no demo file. Covers BOTH registration paths (the graph-level
///     <c>StateGraph.AddRisingEdgeAction</c> overload and the per-player template's
///     <c>ContextRisingEdgeActions</c>), the correctness of the passed frame index and frame-clock
///     tick, and the no-regression contract for plain-<see cref="Action" /> registrants sharing the
///     same trigger (plain fires first, both fire, one trigger fire count).
/// </summary>
[Category("Unit")]
public class RisingEdgeContextActionTests
{
    private static DemoFrame Frame(int frameNumber, int serverTick, params NetMessage[] msgs) => new()
    {
        Command = "DEM_Packet",
        FrameNumber = frameNumber,
        ServerTick = serverTick,
        RawStart = 0,
        RawLength = 1,
        HeaderLength = 1,
        IsCompressed = false,
        MessageList = [.. msgs]
    };

    /// <summary>A player_team message whose Team field carries the value to write.</summary>
    private static GameEventMessage WriteX(int team, int slot = -1) => GameEventMessage.ForSynthesizedEvent(
        TestGameEvents.PlayerTeam(slot, (byte)team));

    /// <summary>
    ///     Graph-level registration (evaluator constructor path): the context action receives the
    ///     firing frame's zero-based index and its <c>ServerTick</c> (frame clock), the co-registered
    ///     plain action still fires (first), and the trigger's fire count counts one fire.
    /// </summary>
    [Test]
    public async Task GraphLevel_ContextAction_ReceivesFrameIndexAndTick_PlainActionUnaffected()
    {
        GenericValueNode<int> x = new("x");
        List<string> order = [];
        List<(int FrameIdx, int Tick)> received = [];

        StateGraph graph = new();
        graph.AddEdge(new OnGameEventSetValue<PlayerTeamEvent, int>(graph.Root, x, e => e.Of<PlayerTeamEvent>().Team));
        ConjunctionNode trigger = new("ctx_trigger", ConditionalEdge.From(x, v => v == 1, "== 1"));
        graph.AddConjunction(trigger);
        graph.AddRisingEdgeAction(trigger, () => order.Add("plain"));
        graph.AddRisingEdgeAction(trigger, (frameIdx, tick) =>
        {
            order.Add("context");
            received.Add((frameIdx, tick));
        });

        StateGraphEvaluator evaluator = new(graph);
        evaluator.Evaluate([
            Frame(0, 100, WriteX(0)), // no rise
            Frame(1, 200, WriteX(0)), // no rise
            Frame(2, 300, WriteX(1)) //  rise here → (frameIdx 2, tick 300)
        ]);

        await Assert.That(received).HasCount().EqualTo(1)
            .Because("the context action must fire exactly once on the rising edge");
        await Assert.That(received[0].FrameIdx).IsEqualTo(2)
            .Because("the context arm must receive the firing frame's zero-based index");
        await Assert.That(received[0].Tick).IsEqualTo(300)
            .Because("the context arm must receive the firing frame's ServerTick (frame clock)");
        await Assert.That(order).HasCount().EqualTo(2)
            .Because("both the plain and the context registrant must fire on the one rising edge");
        await Assert.That(order[0]).IsEqualTo("plain")
            .Because("plain registrants fire before context arms (merge order: plain first)");
        await Assert.That(order[1]).IsEqualTo("context");
        await Assert.That(evaluator.RisingEdgeFireCounts[trigger]).IsEqualTo(1)
            .Because("the fire counter counts trigger fires, not per-arm invocations");
    }

    /// <summary>
    ///     The context arm's <c>(frameIdx, tick)</c> matches the timeline's
    ///     <see cref="RuleChainEvent" /> stamp for the same rising edge — the two records describe
    ///     one event and must agree (HighlightFired inherits RuleChainEvent.Tick semantics).
    /// </summary>
    [Test]
    public async Task GraphLevel_ContextActionArgs_MatchTimelineEventStamp()
    {
        GenericValueNode<int> x = new("x");
        List<(int FrameIdx, int Tick)> received = [];

        StateGraph graph = new();
        graph.AddEdge(new OnGameEventSetValue<PlayerTeamEvent, int>(graph.Root, x, e => e.Of<PlayerTeamEvent>().Team));
        ConjunctionNode trigger = new("_chain_ctx_stamp", ConditionalEdge.From(x, v => v == 1, "== 1"));
        graph.AddConjunction(trigger);
        graph.AddRisingEdgeAction(trigger, (frameIdx, tick) => received.Add((frameIdx, tick)));

        RuleChainTimeline timeline = new StateGraphEvaluator(graph).Evaluate([
            Frame(0, 64, WriteX(0)),
            Frame(1, 128, WriteX(1))
        ]);

        RuleChainEvent ev = timeline.Events.Single(e => e.ChainName == "_chain_ctx_stamp");
        await Assert.That(received).HasCount().EqualTo(1);
        await Assert.That(received[0].FrameIdx).IsEqualTo(ev.FrameIndex)
            .Because("the context arm's frameIdx must equal the RuleChainEvent's FrameIndex");
        await Assert.That(received[0].Tick).IsEqualTo(ev.Tick)
            .Because("the context arm's tick must equal the RuleChainEvent's Tick (same clock)");
    }

    /// <summary>
    ///     Per-player template registration (MaterializeSlot path): a template returning BOTH lists
    ///     fires the plain action and the context action, the context action receives the correct
    ///     <c>(frameIdx, tick)</c>, and the trigger's fire count counts one fire.
    /// </summary>
    [Test]
    public async Task PerPlayerTemplate_ContextAction_ReceivesFrameIndexAndTick_PlainActionUnaffected()
    {
        List<(int FrameIdx, int Tick)> received = [];
        List<GenericValueNode<int>> plainCounters = [];

        StateGraph graph = new();
        graph.AddPerPlayerTemplate(new PerPlayerNodeTemplate((slot, _, name, _) =>
        {
            GenericValueNode<int> x = new($"x_p{slot}");
            GenericValueNode<int> plainCounter = new($"plain_p{slot}");
            plainCounters.Add(plainCounter);
            OnGameEventSetValue<PlayerTeamEvent, int> edge = new(graph.Root, x, e => e.Of<PlayerTeamEvent>().Team,
                e => e.Of<PlayerTeamEvent>().UserId == slot);
            ConjunctionNode trigger = new($"_chain_pp_ctx_p{slot}",
                ConditionalEdge.From(x, v => v == 2, "== 2"));
            return new PerPlayerNodeTemplate.MaterializedPlayer(
                slot, name, [x, plainCounter, trigger], [edge], [], [],
                [
                    (trigger, () => plainCounter.SetValue(plainCounter.Value + 1), plainCounter)
                ],
                ContextRisingEdgeActions:
                [
                    (trigger, (frameIdx, tick) => received.Add((frameIdx, tick)), null)
                ]);
        }));

        // Frame 0 (tick 640) discovers slot 5 but doesn't satisfy; frame 1 (tick 704) rises.
        new StateGraphEvaluator(graph).Evaluate([
            Frame(0, 640, WriteX(1, 5)),
            Frame(1, 704, WriteX(2, 5))
        ]);

        await Assert.That(received).HasCount().EqualTo(1)
            .Because("the template-registered context action must fire exactly once");
        await Assert.That(received[0].FrameIdx).IsEqualTo(1)
            .Because("the context arm must receive the firing frame's index (materialization path)");
        await Assert.That(received[0].Tick).IsEqualTo(704)
            .Because("the context arm must receive the firing frame's ServerTick");
        await Assert.That(plainCounters).HasCount().EqualTo(1);
        await Assert.That(plainCounters[0].Value).IsEqualTo(1)
            .Because("the plain template action must be unaffected by the context-arm addition");
    }

    /// <summary>
    ///     A trigger with ONLY a context-arm registration (no plain action) fires and is counted —
    ///     both registration paths must seed the fire counter for context-only triggers.
    /// </summary>
    [Test]
    public async Task ContextOnlyTrigger_FiresAndCounts()
    {
        GenericValueNode<int> x = new("x");
        int fired = 0;

        StateGraph graph = new();
        graph.AddEdge(new OnGameEventSetValue<PlayerTeamEvent, int>(graph.Root, x, e => e.Of<PlayerTeamEvent>().Team));
        ConjunctionNode trigger = new("ctx_only_trigger", ConditionalEdge.From(x, v => v == 1, "== 1"));
        graph.AddConjunction(trigger);
        graph.AddRisingEdgeAction(trigger, (_, _) => fired++);

        StateGraphEvaluator evaluator = new(graph);
        evaluator.Evaluate([Frame(0, 32, WriteX(1))]);

        await Assert.That(fired).IsEqualTo(1);
        await Assert.That(evaluator.RisingEdgeFireCounts[trigger]).IsEqualTo(1)
            .Because("a context-only trigger must be seeded in the fire-count map");
    }
}
