#region

using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Analysis.Nodes;
using CS2DemoKit.Parser;
using CS2DemoKit.Parser.GameEvents;

#endregion

using CS2DemoKit.TestSupport;

namespace CS2DemoKit.Analysis.Tests;

/// <summary>
///     Read-aware topological ordering battery — pure in-memory graphs, no demo file.
///     <list type="bullet">
///         <item>
///             <b>Reader-after-writer:</b> an edge declaring a read of node X via
///             <see cref="StateEdge.DeclaredReads" /> is ordered after X's SetValue writer within
///             the same dispatch slot, regardless of insertion order — and before X's Deactivate
///             writer (the generalized Deactivate-after-readers rule). Controls pin that WITHOUT
///             the declaration, insertion order wins (the pre-A1 contract, unchanged for empty
///             declarations).
///         </item>
///         <item>
///             <b>Same-event read cycle:</b> two edges on one event whose declared reads form a
///             cycle are a build error naming both stats and suggesting the <c>after:</c> fix-it.
///         </item>
///         <item>
///             <b>A1 detail (b), the AdditionalWrittenNodes indexing gap:</b> a logic node whose
///             input reads a node written ONLY via <see cref="StateEdge.AdditionalWrittenNodes" />
///             must be recomputed on the writing edge's message — in both the constructor-time
///             <c>BuildLogicNodeIndex</c> path and the per-player
///             <c>RegisterConjunction</c>/<c>RegisterDisjunction</c> path. Pre-fix, both paths
///             bucketed logic nodes by <see cref="StateEdge.WrittenNode" /> only, so the flip was
///             silently deferred past the writing message.
///         </item>
///     </list>
/// </summary>
[Category("Unit")]
public class DeclaredReadsTopologyTests
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

    private static GameEventMessage TeamEvent(int slot) => GameEventMessage.ForSynthesizedEvent(
        TestGameEvents.PlayerTeam(slot, 2));

    // ── Reader-after-writer ordering ─────────────────────────────────────────

    /// <summary>
    ///     The reader edge is inserted FIRST but declares a read of the writer's node — the sort
    ///     must run the writer first, so the reader observes the value written in the SAME message.
    /// </summary>
    [Test]
    public async Task DeclaredRead_OrdersReaderAfterWriter_DespiteInsertionOrder()
    {
        GenericValueNode<int> written = new("written_stat");
        List<(bool Active, int Value)> observed = [];

        StateGraph graph = new();
        graph.AddEdge(new ObservingEdge(graph.Root, written, observed, [written]));
        graph.AddEdge(new SetValueEdge(graph.Root, written, 42));

        new StateGraphEvaluator(graph).Evaluate([Frame(TeamEvent(1))]);

        await Assert.That(observed).HasCount().EqualTo(1);
        await Assert.That(observed[0].Active).IsTrue()
            .Because("the declared read must order the reader after the SetValue writer");
        await Assert.That(observed[0].Value).IsEqualTo(42);
    }

    /// <summary>
    ///     Control for the ordering test: the same graph WITHOUT the declaration keeps insertion
    ///     order (reader first ⇒ observes the unset default) — the pre-A1 contract that empty
    ///     <see cref="StateEdge.DeclaredReads" /> preserves.
    /// </summary>
    [Test]
    public async Task NoDeclaredRead_KeepsInsertionOrder()
    {
        GenericValueNode<int> written = new("written_stat");
        List<(bool Active, int Value)> observed = [];

        StateGraph graph = new();
        graph.AddEdge(new ObservingEdge(graph.Root, written, observed, null));
        graph.AddEdge(new SetValueEdge(graph.Root, written, 42));

        new StateGraphEvaluator(graph).Evaluate([Frame(TeamEvent(1))]);

        await Assert.That(observed).HasCount().EqualTo(1);
        await Assert.That(observed[0].Active).IsFalse()
            .Because("without a declared read, insertion order wins and the reader runs first");
    }

    /// <summary>
    ///     Deactivate generalization: a Deactivate writer of X inserted BEFORE a declared reader
    ///     of X must be ordered after it, so the reader observes the still-active value.
    /// </summary>
    [Test]
    public async Task DeclaredRead_OrdersReaderBeforeDeactivateWriter()
    {
        GenericBoolNode flag = new("deactivated_stat");
        flag.Activate();
        List<(bool Active, int Value)> observed = [];

        StateGraph graph = new();
        graph.AddEdge(new DeactivateEdge(graph.Root, flag));
        graph.AddEdge(new ObservingBoolEdge(graph.Root, flag, observed, [flag]));

        new StateGraphEvaluator(graph).Evaluate([Frame(TeamEvent(1))]);

        await Assert.That(observed).HasCount().EqualTo(1);
        await Assert.That(observed[0].Active).IsTrue()
            .Because("Deactivate writers are ordered after declared readers");
        await Assert.That(flag.IsActive).IsFalse()
            .Because("the deactivation itself must still apply, after the read");
    }

    // ── Same-event read cycle ⇒ build error ──────────────────────────────────

    /// <summary>
    ///     Two edges on one event, each declaring a read of the stat the other writes: building
    ///     the evaluator throws, and the error names BOTH stats plus the <c>after:</c> fix-it.
    /// </summary>
    [Test]
    public async Task SameEventReadCycle_IsBuildError_NamingBothStats()
    {
        GenericValueNode<int> statA = new("cycle_stat_a");
        GenericValueNode<int> statB = new("cycle_stat_b");

        StateGraph graph = new();
        graph.AddEdge(new SetValueEdge(graph.Root, statA, 1, [statB]));
        graph.AddEdge(new SetValueEdge(graph.Root, statB, 1, [statA]));

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => _ = new StateGraphEvaluator(graph));

        Console.WriteLine(error.Message);
        await Assert.That(error.Message).Contains("cycle_stat_a")
            .Because("the cycle error must name the first stat");
        await Assert.That(error.Message).Contains("cycle_stat_b")
            .Because("the cycle error must name the second stat");
        await Assert.That(error.Message).Contains("after:")
            .Because("the error must suggest the `after: <stat>` fix-it as the explicit tie-break");
    }

    /// <summary>
    ///     A self-read (an edge declaring a read of the node it writes — the self-add sum shape)
    ///     is NOT a cycle: the sort skips self-constraints, and the build succeeds.
    /// </summary>
    [Test]
    public async Task SelfRead_IsNotACycle()
    {
        GenericValueNode<int> stat = new("self_add_stat");

        StateGraph graph = new();
        graph.AddEdge(new SetValueEdge(graph.Root, stat, 1, [stat]));
        graph.AddEdge(new SetValueEdge(graph.Root, stat, 2));

        StateGraphEvaluator evaluator = new(graph);
        evaluator.Evaluate([Frame(TeamEvent(1))]);

        await Assert.That(stat.IsActive).IsTrue();
    }

    // ── A1 detail (b): AdditionalWrittenNodes indexing gap ───────────────────

    /// <summary>
    ///     Constructor path (<c>BuildLogicNodeIndex</c>): a conjunction reading a node written
    ///     ONLY via <see cref="StateEdge.AdditionalWrittenNodes" /> must recompute — and rise —
    ///     on the very message whose edge wrote it. FAILS pre-fix (no bucket for the message
    ///     type ⇒ no recompute ⇒ no timeline event).
    /// </summary>
    [Test]
    public async Task AdditionalWrittenNode_ReachesLogicNodeIndex_ConstructorPath()
    {
        GenericBoolNode primary = new("primary_unread");
        GenericValueNode<int> extra = new("additional_written");

        StateGraph graph = new();
        graph.AddEdge(new MultiWriteEdge(graph.Root, primary, extra, 7));

        ConjunctionNode cj = new("additional_written_reader",
            ConditionalEdge.From(extra, v => v == 7, "== 7"));
        graph.AddConjunction(cj);

        RuleChainTimeline timeline = new StateGraphEvaluator(graph).Evaluate([Frame(TeamEvent(1))]);

        await Assert.That(cj.IsActive).IsTrue()
            .Because("the conjunction must recompute on the message that wrote its input via AdditionalWrittenNodes");
        await Assert.That(timeline.CountFor("additional_written_reader")).IsEqualTo(1)
            .Because("the rising edge must land on the writing message, not a later one");
    }

    /// <summary>
    ///     Per-player path (<c>RegisterConjunction</c>): the same gap existed for conjunctions
    ///     registered at player materialization. The template materializes an edge whose
    ///     conjunction reads only an additional-written node; the materializing message itself
    ///     fires the edge, and the conjunction must rise on that message. FAILS pre-fix.
    /// </summary>
    [Test]
    public async Task AdditionalWrittenNode_ReachesLogicNodeIndex_PerPlayerPath()
    {
        List<ConjunctionNode> materializedConjunctions = [];

        StateGraph graph = new();
        graph.AddPerPlayerTemplate(new PerPlayerNodeTemplate((slot, _, name, _) =>
        {
            GenericBoolNode primary = new($"primary_unread_p{slot}");
            GenericValueNode<int> extra = new($"additional_written_p{slot}");
            MultiWriteEdge edge = new(graph.Root, primary, extra, 7);
            ConjunctionNode cj = new($"additional_written_reader_p{slot}",
                ConditionalEdge.From(extra, v => v == 7, "== 7"));
            materializedConjunctions.Add(cj);
            return new PerPlayerNodeTemplate.MaterializedPlayer(
                slot, name, [primary, extra, cj], [edge], [], []);
        }));

        RuleChainTimeline timeline = new StateGraphEvaluator(graph).Evaluate([Frame(TeamEvent(3))]);

        await Assert.That(materializedConjunctions).HasCount().EqualTo(1);
        await Assert.That(materializedConjunctions[0].IsActive).IsTrue()
            .Because("RegisterConjunction must bucket the conjunction under the edge's message type "
                     + "when the read node is only in AdditionalWrittenNodes");
        await Assert.That(timeline.CountFor("additional_written_reader_p3")).IsEqualTo(1);
    }

    /// <summary>
    ///     Per-player disjunction path (<c>RegisterDisjunction</c>): mirror of the conjunction pin.
    /// </summary>
    [Test]
    public async Task AdditionalWrittenNode_ReachesLogicNodeIndex_PerPlayerDisjunctionPath()
    {
        List<DisjunctionNode> materializedDisjunctions = [];

        StateGraph graph = new();
        graph.AddPerPlayerTemplate(new PerPlayerNodeTemplate((slot, _, name, _) =>
        {
            GenericBoolNode primary = new($"dj_primary_unread_p{slot}");
            GenericValueNode<int> extra = new($"dj_additional_written_p{slot}");
            MultiWriteEdge edge = new(graph.Root, primary, extra, 7);
            DisjunctionNode dj = new($"dj_additional_written_reader_p{slot}",
                ConditionalEdge.From(extra, v => v == 7, "== 7"));
            materializedDisjunctions.Add(dj);
            return new PerPlayerNodeTemplate.MaterializedPlayer(
                slot, name, [primary, extra, dj], [edge], [], []);
        }));

        RuleChainTimeline timeline = new StateGraphEvaluator(graph).Evaluate([Frame(TeamEvent(4))]);

        await Assert.That(materializedDisjunctions).HasCount().EqualTo(1);
        await Assert.That(materializedDisjunctions[0].IsActive).IsTrue();
        await Assert.That(timeline.CountFor("dj_additional_written_reader_p4")).IsEqualTo(1);
    }

    // ── Test edges ────────────────────────────────────────────────────────────

    /// <summary>Writes a constant into a value node on every <see cref="PlayerTeamEvent" />.</summary>
    private sealed class SetValueEdge(
        StateNode source,
        GenericValueNode<int> target,
        int value,
        IReadOnlyList<StateNode>? declaredReads = null) : StateEdge(source)
    {
        /// <inheritdoc />
        public override EdgeEffect? DeclaredEffect => EdgeEffect.SetValue;

        /// <inheritdoc />
        public override IReadOnlyList<StateNode>? DeclaredReads => declaredReads;

        /// <inheritdoc />
        public override Type MessageType => typeof(PlayerTeamEvent);

        /// <inheritdoc />
        public override StateNode? WrittenNode => target;

        /// <inheritdoc />
        public override bool TryApply(EvaluationContext context)
        {
            target.SetValue(value);
            return true;
        }
    }

    /// <summary>Deactivates a bool node on every <see cref="PlayerTeamEvent" />.</summary>
    private sealed class DeactivateEdge(StateNode source, GenericBoolNode target) : StateEdge(source)
    {
        /// <inheritdoc />
        public override EdgeEffect? DeclaredEffect => EdgeEffect.Deactivate;

        /// <inheritdoc />
        public override Type MessageType => typeof(PlayerTeamEvent);

        /// <inheritdoc />
        public override StateNode? WrittenNode => target;

        /// <inheritdoc />
        public override bool TryApply(EvaluationContext context)
        {
            target.Deactivate();
            return true;
        }
    }

    /// <summary>Observes a value node's (IsActive, Value) at fire time and records it.</summary>
    private sealed class ObservingEdge(
        StateNode source,
        GenericValueNode<int> observedNode,
        List<(bool Active, int Value)> log,
        IReadOnlyList<StateNode>? declaredReads) : StateEdge(source)
    {
        /// <inheritdoc />
        public override IReadOnlyList<StateNode>? DeclaredReads => declaredReads;

        /// <inheritdoc />
        public override Type MessageType => typeof(PlayerTeamEvent);

        /// <inheritdoc />
        public override bool TryApply(EvaluationContext context)
        {
            log.Add((observedNode.IsActive, observedNode.Value));
            return true;
        }
    }

    /// <summary>Observes a bool node's activation at fire time and records it.</summary>
    private sealed class ObservingBoolEdge(
        StateNode source,
        GenericBoolNode observedNode,
        List<(bool Active, int Value)> log,
        IReadOnlyList<StateNode>? declaredReads) : StateEdge(source)
    {
        /// <inheritdoc />
        public override IReadOnlyList<StateNode>? DeclaredReads => declaredReads;

        /// <inheritdoc />
        public override Type MessageType => typeof(PlayerTeamEvent);

        /// <inheritdoc />
        public override bool TryApply(EvaluationContext context)
        {
            log.Add((observedNode.IsActive, 0));
            return true;
        }
    }

    /// <summary>
    ///     Writes a primary bool (read by nothing) AND an additional value node — the enrichment
    ///     multi-write shape that exposed the indexing gap.
    /// </summary>
    private sealed class MultiWriteEdge(
        StateNode source,
        GenericBoolNode primary,
        GenericValueNode<int> extra,
        int value) : StateEdge(source)
    {
        /// <inheritdoc />
        public override IReadOnlyList<StateNode>? AdditionalWrittenNodes => [extra];

        /// <inheritdoc />
        public override EdgeEffect? DeclaredEffect => EdgeEffect.Activate;

        /// <inheritdoc />
        public override Type MessageType => typeof(PlayerTeamEvent);

        /// <inheritdoc />
        public override StateNode? WrittenNode => primary;

        /// <inheritdoc />
        public override bool TryApply(EvaluationContext context)
        {
            primary.Activate();
            extra.SetValue(value);
            return true;
        }
    }
}
