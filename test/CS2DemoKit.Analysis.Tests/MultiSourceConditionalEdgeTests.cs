#region

using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Analysis.Building;
using CS2DemoKit.Analysis.Nodes;
using CS2DemoKit.Analysis.Registry;
using CS2DemoKit.Parser;
using CS2DemoKit.Parser.GameEvents;

#endregion

using CS2DemoKit.TestSupport;

namespace CS2DemoKit.Analysis.Tests;

/// <summary>
///     Multi-source conditional edge battery — hand-built graphs, no demo file.
///     <list type="bullet">
///         <item>
///             <b>Satisfaction contract:</b> an N-source edge is satisfied only when ALL sources
///             are active AND the predicate holds; the predicate is never invoked while any
///             source is inactive.
///         </item>
///         <item>
///             <b>Dirty-marking union:</b> a write to ANY source's writer — including writers
///             dispatched on different message types — recomputes the owning logic node on that
///             message (rising edges land exactly where the flipping write happens).
///         </item>
///         <item>
///             <b>Strict generalization:</b> a 1-element source list behaves identically to
///             today's single-source <see cref="ConditionalEdge{T}" /> over the same stream.
///         </item>
///         <item>
///             <b>Deliberate default:</b> multi-parent TRIGGERED rules remain a
///             build error — multi-source conditional gates must not quietly enable
///             them.
///         </item>
///     </list>
/// </summary>
[Category("Unit")]
public class MultiSourceConditionalEdgeTests
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

    /// <summary>Writes <paramref name="team" /> into the graph's 'a' node via a team-event edge.</summary>
    private static GameEventMessage WriteA(int team) => GameEventMessage.ForSynthesizedEvent(
        TestGameEvents.PlayerTeam(-1, (byte)team));

    /// <summary>Writes <paramref name="dmgHealth" /> into the graph's 'b' node via a death-event edge.</summary>
    private static GameEventMessage WriteB(int dmgHealth) => GameEventMessage.ForSynthesizedEvent(
        TestGameEvents.PlayerDeath(weapon: "ak47", dmgHealth: (short)dmgHealth));

    /// <summary>
    ///     The shared two-source fixture: node 'a' written by player_team edges, node 'b' by
    ///     player_death edges (deliberately different dispatch keys), and a conjunction over
    ///     <c>a + b &gt; 5</c> via one N-source conditional edge.
    /// </summary>
    private static (StateGraph Graph, GenericValueNode<int> A, GenericValueNode<int> B,
        ConjunctionNode Cj, Counter PredicateCalls) TwoSourceGraph()
    {
        GenericValueNode<int> a = new("a");
        GenericValueNode<int> b = new("b");
        Counter calls = new();

        StateGraph graph = new();
        graph.AddEdge(new OnGameEventSetValue<PlayerTeamEvent, int>(graph.Root, a, e => e.Of<PlayerTeamEvent>().Team));
        graph.AddEdge(new OnGameEventSetValue<PlayerDeathEvent, int>(graph.Root, b, e => e.Of<PlayerDeathEvent>().DmgHealth));

        ConjunctionNode cj = new("a_plus_b_gt_5",
            ConditionalEdge.FromAll([a, b], () =>
            {
                calls.Value++;
                return a.Value + b.Value > 5;
            }, "a + b > 5"));
        graph.AddConjunction(cj);

        return (graph, a, b, cj, calls);
    }

    // ── Satisfaction contract ────────────────────────────────────────────────

    /// <summary>
    ///     With only one source active the edge is unsatisfied AND the predicate is never
    ///     invoked; once both are active the predicate decides.
    /// </summary>
    [Test]
    public async Task NSourceEdge_RequiresAllSourcesActive_BeforePredicateRuns()
    {
        (StateGraph graph, _, _, ConjunctionNode cj, Counter calls) = TwoSourceGraph();
        StateGraphEvaluator evaluator = new(graph);

        RuleChainTimeline timeline = evaluator.Evaluate([
            Frame(WriteB(10)), // b active, a inactive — predicate must not run
            Frame(WriteA(3)) //   both active: 3 + 10 > 5 → rising edge here
        ]);

        await Assert.That(cj.IsActive).IsTrue();
        await Assert.That(calls.Value).IsGreaterThan(0)
            .Because("once all sources are active the predicate decides satisfaction");
        await Assert.That(timeline.CountFor("a_plus_b_gt_5")).IsEqualTo(1);
        await Assert.That(timeline.Events.Single(e => e.ChainName == "a_plus_b_gt_5").FrameIndex)
            .IsEqualTo(1)
            .Because("the rising edge must land on the message that activated the LAST source");
    }

    /// <summary>The predicate is not consulted while a source is inactive — sources gate first.</summary>
    [Test]
    public async Task NSourceEdge_PredicateNotInvoked_WhileAnySourceInactive()
    {
        (StateGraph graph, _, _, ConjunctionNode cj, Counter calls) = TwoSourceGraph();
        StateGraphEvaluator evaluator = new(graph);

        evaluator.Evaluate([Frame(WriteB(100))]); // only b active; 0 + 100 > 5 would be true

        await Assert.That(cj.IsActive).IsFalse()
            .Because("satisfied = ALL sources active AND predicate — one inactive source blocks");
        await Assert.That(calls.Value).IsEqualTo(0)
            .Because("the predicate must not run while a source is inactive");
    }

    /// <summary>Both sources active but the predicate false keeps the edge unsatisfied.</summary>
    [Test]
    public async Task NSourceEdge_AllActiveButPredicateFalse_IsUnsatisfied()
    {
        (StateGraph graph, _, _, ConjunctionNode cj, Counter calls) = TwoSourceGraph();
        StateGraphEvaluator evaluator = new(graph);

        RuleChainTimeline timeline = evaluator.Evaluate([
            Frame(WriteA(2)),
            Frame(WriteB(3)) // 2 + 3 = 5, not > 5
        ]);

        await Assert.That(cj.IsActive).IsFalse();
        await Assert.That(calls.Value).IsGreaterThan(0);
        await Assert.That(timeline.CountFor("a_plus_b_gt_5")).IsEqualTo(0);
    }

    // ── Dirty-marking union ──────────────────────────────────────────────────

    /// <summary>
    ///     The flipping write arrives through source B's writer (a different dispatch key than
    ///     A's writer): the conjunction must recompute — and rise — on exactly that message.
    /// </summary>
    [Test]
    public async Task DirtyUnion_RecomputesWhenSecondSourcesWriterFires()
    {
        (StateGraph graph, _, _, _, _) = TwoSourceGraph();
        StateGraphEvaluator evaluator = new(graph);

        RuleChainTimeline timeline = evaluator.Evaluate([
            Frame(WriteA(3)), //  a=3, b inactive
            Frame(WriteB(1)), //  3 + 1 = 4 → false
            Frame(WriteB(9)) //   3 + 9 = 12 → rising edge HERE, driven by b's writer alone
        ]);

        await Assert.That(timeline.CountFor("a_plus_b_gt_5")).IsEqualTo(1);
        await Assert.That(timeline.Events.Single(e => e.ChainName == "a_plus_b_gt_5").FrameIndex)
            .IsEqualTo(2)
            .Because("a write to source b alone must recompute the N-source predicate");
    }

    /// <summary>
    ///     Symmetric pin: the flipping write arrives through source A's writer. Together with the
    ///     B-side test this proves bucketing unions BOTH sources' writers.
    /// </summary>
    [Test]
    public async Task DirtyUnion_RecomputesWhenFirstSourcesWriterFires()
    {
        (StateGraph graph, _, _, _, _) = TwoSourceGraph();
        StateGraphEvaluator evaluator = new(graph);

        RuleChainTimeline timeline = evaluator.Evaluate([
            Frame(WriteA(0)), //  a=0, b inactive
            Frame(WriteB(1)), //  0 + 1 = 1 → false
            Frame(WriteA(8)) //   8 + 1 = 9 → rising edge HERE, driven by a's writer alone
        ]);

        await Assert.That(timeline.CountFor("a_plus_b_gt_5")).IsEqualTo(1);
        await Assert.That(timeline.Events.Single(e => e.ChainName == "a_plus_b_gt_5").FrameIndex)
            .IsEqualTo(2)
            .Because("a write to source a alone must recompute the N-source predicate");
    }

    // ── Strict generalization: 1-source parity ───────────────────────────────

    /// <summary>
    ///     A 1-element <see cref="MultiSourceConditionalEdge" /> and today's single-source
    ///     <see cref="ConditionalEdge{T}" /> produce identical timelines and identical final
    ///     state over the same message stream (rise, fall via value change, re-rise).
    /// </summary>
    [Test]
    public async Task OneSourceEdge_BehaviorIdenticalToSingleSourceContract()
    {
        static (StateGraph Graph, GenericValueNode<int> Node, ConjunctionNode Cj) BuildGraph(
            bool multiSource)
        {
            GenericValueNode<int> node = new("value");
            StateGraph graph = new();
            graph.AddEdge(new OnGameEventSetValue<PlayerTeamEvent, int>(graph.Root, node, e => e.Of<PlayerTeamEvent>().Team));
            IConditionalEdge input = multiSource
                ? ConditionalEdge.FromAll([node], () => node.Value > 2, "> 2")
                : ConditionalEdge.From(node, v => v > 2, "> 2");
            ConjunctionNode cj = new("parity_cj", input);
            graph.AddConjunction(cj);
            return (graph, node, cj);
        }

        static DemoFrame[] Stream()
        {
            return
            [
                Frame(WriteA(1)), // active, predicate false
                Frame(WriteA(3)), // rising
                Frame(WriteA(0)), // falling
                Frame(WriteA(5)) //  rising again
            ];
        }

        (StateGraph singleGraph, GenericValueNode<int> singleNode, ConjunctionNode singleCj) =
            BuildGraph(false);
        (StateGraph multiGraph, GenericValueNode<int> multiNode, ConjunctionNode multiCj) =
            BuildGraph(true);

        RuleChainTimeline singleTimeline = new StateGraphEvaluator(singleGraph).Evaluate(Stream());
        RuleChainTimeline multiTimeline = new StateGraphEvaluator(multiGraph).Evaluate(Stream());

        await Assert.That(multiTimeline.Events.Count).IsEqualTo(singleTimeline.Events.Count)
            .Because("a 1-element source list is the strict generalization — identical event counts");
        for (int i = 0; i < singleTimeline.Events.Count; i++)
        {
            await Assert.That(multiTimeline.Events[i]).IsEqualTo(singleTimeline.Events[i]);
        }

        await Assert.That(multiCj.IsActive).IsEqualTo(singleCj.IsActive);
        await Assert.That(multiNode.Value).IsEqualTo(singleNode.Value);
        await Assert.That(singleTimeline.CountFor("parity_cj")).IsEqualTo(2)
            .Because("the stream is designed to rise twice — a 0-event vacuous pass must fail");
    }

    // ── Construction contract ────────────────────────────────────────────────

    /// <summary>An empty source list is rejected at construction.</summary>
    [Test]
    public async Task EmptySourceList_IsConstructionError()
    {
        ArgumentException ex = Assert.Throws<ArgumentException>(() => _ = new MultiSourceConditionalEdge([], () => true, "empty"));

        await Assert.That(ex.Message).Contains("at least one source");
    }

    /// <summary>The primary <see cref="IConditionalEdge.Source" /> is the first declared source.</summary>
    [Test]
    public async Task PrimarySource_IsFirstDeclaredSource()
    {
        GenericValueNode<int> first = new("first");
        GenericValueNode<int> second = new("second");

        MultiSourceConditionalEdge edge = new([first, second], () => true, "test");

        await Assert.That(ReferenceEquals(edge.Source, first)).IsTrue();
        await Assert.That(edge.Sources.Count).IsEqualTo(2);
    }

    /// <summary>Mutable int box for counting predicate invocations from a closure.</summary>
    private sealed class Counter
    {
        /// <summary>The current count.</summary>
        public int Value;
    }
}
