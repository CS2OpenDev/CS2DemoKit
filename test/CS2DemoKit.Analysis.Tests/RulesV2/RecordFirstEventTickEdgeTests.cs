#region

using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Analysis.Edges;
using CS2DemoKit.Analysis.Nodes;
using CS2DemoKit.Parser;
using CS2DemoKit.Parser.GameEvents;

#endregion

using CS2DemoKit.TestSupport;

namespace CS2DemoKit.Analysis.Tests.RulesV2;

/// <summary>
///     Approach-1 clip-start mechanism: <see cref="RecordFirstEventTickEdge" /> stamps a round-scoped
///     int node with the frame-clock <c>GameTick</c> of the FIRST matching event of the round,
///     write-once, gated by the same condition as the companion <c>count:</c> increment. This is the
///     tick the highlight emission closure reads into <see cref="HighlightFired.ClipStartTick" /> so a
///     multi-kill clip can reach back past the reel lead-in to its first kill. Hand-built graph, no
///     demo file — the wiring through the full <c>RuleChainBuilder</c> is covered by the demo-backed
///     <c>HighlightFiredEmissionTests</c>.
/// </summary>
[Category("Unit")]
public class RecordFirstEventTickEdgeTests
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

    /// <summary>A player_team event carrying the GameTick (frame clock) the edge records, plus a Team facet.</summary>
    private static GameEventMessage Evt(int gameTick, int team) => GameEventMessage.ForSynthesizedEvent(
        TestGameEvents.PlayerTeam(0, (byte)team, gameTick: gameTick));

    [Test]
    public async Task RecordsFirstMatchingTick_WriteOnce()
    {
        GenericRoundScopedValueNode<int> firstTick =
            new("__first_tick", RecordFirstEventTickEdge.Sentinel);

        StateGraph graph = new();
        graph.AddEdge(new RecordFirstEventTickEdge(graph.Root, firstTick, typeof(PlayerTeamEvent), null));

        new StateGraphEvaluator(graph).Evaluate([
            Frame(0, 10, Evt(100, 2)), // first event → recorded
            Frame(1, 20, Evt(200, 2)), // later events must NOT overwrite
            Frame(2, 30, Evt(300, 2))
        ]);

        await Assert.That(firstTick.Value).IsEqualTo(100)
            .Because("the FIRST matching event's GameTick is stamped, write-once");
    }

    [Test]
    public async Task ConditionGates_RecordsFirstEventThatMatches()
    {
        GenericRoundScopedValueNode<int> firstTick =
            new("__first_tick", RecordFirstEventTickEdge.Sentinel);

        // Mirror a count's enemy-kill facet: only team == 2 events count.
        Func<GameEvent, bool> condition = e => e.Of<PlayerTeamEvent>().Team == 2;

        StateGraph graph = new();
        graph.AddEdge(new RecordFirstEventTickEdge(graph.Root, firstTick, typeof(PlayerTeamEvent), condition));

        new StateGraphEvaluator(graph).Evaluate([
            Frame(0, 10, Evt(100, 1)), // condition fails → skipped, node stays unset
            Frame(1, 20, Evt(200, 2)), // first MATCH → recorded
            Frame(2, 30, Evt(300, 2))
        ]);

        await Assert.That(firstTick.Value).IsEqualTo(200)
            .Because("the first event PASSING the condition sets the tick, not the first event overall");
    }

    [Test]
    public async Task NoMatchingEvent_LeavesSentinel()
    {
        GenericRoundScopedValueNode<int> firstTick =
            new("__first_tick", RecordFirstEventTickEdge.Sentinel);
        Func<GameEvent, bool> condition = e => e.Of<PlayerTeamEvent>().Team == 2;

        StateGraph graph = new();
        graph.AddEdge(new RecordFirstEventTickEdge(graph.Root, firstTick, typeof(PlayerTeamEvent), condition));

        new StateGraphEvaluator(graph).Evaluate([
            Frame(0, 10, Evt(100, 1)),
            Frame(1, 20, Evt(200, 3))
        ]);

        await Assert.That(firstTick.Value).IsEqualTo(RecordFirstEventTickEdge.Sentinel)
            .Because("with no matching event the node stays unset — a reader treats the sentinel as no clip-start");
    }
}
