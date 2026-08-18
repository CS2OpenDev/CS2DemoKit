#region

using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Analysis.Nodes;
using CS2DemoKit.Parser;
using Google.Protobuf.WellKnownTypes;
using Type = System.Type;

#endregion

namespace CS2DemoKit.Analysis.Tests;

/// <summary>
///     Snapshot-capture regression tests for <see cref="StateGraphEvaluator" />.
///     Built around a minimal in-memory graph — no demo file required, runs in
///     milliseconds — so the snapshot invariants are exercised directly rather
///     than indirectly via a 10-minute end-to-end run.
///     <para>
///         <b>Multi-write snapshot regression:</b> edges with
///         <see cref="StateEdge.AdditionalWrittenNodes" /> once wrote their values
///         but the per-message snapshot never marked those nodes dirty —
///         retrospective inspection of <see cref="EvaluationResult.MessageSnapshots" />
///         saw the default forever. Six enrichment edges (HurtTeam, KillTeam,
///         Blind, Clutch, RoundEnd, ClutchResolution) all hit this. Bench
///         parity was unaffected because bench reads live node values, not
///         snapshots; the bug only surfaced when the weapon-enrichment
///         e2e test inspected a TransientValueNode via the snapshot path.
///     </para>
/// </summary>
[Category("Unit")]
public class StateGraphEvaluatorSnapshotTests
{
    /// <summary>
    ///     Direct check that <c>ProcessWrittenNodes</c> marks dirty bits for
    ///     both <c>WrittenNode</c> and every <c>AdditionalWrittenNodes</c>
    ///     entry after an edge fires. Bypasses the frame loop entirely and
    ///     invokes the internal evaluation path via reflection so the test
    ///     stays focused on the snapshot-dirty contract, independent of the
    ///     surrounding dispatch wiring (a previous attempt at a full
    ///     frame-driven test hit dispatch issues unrelated to the audit
    ///     finding).
    /// </summary>
    [Test]
    public async Task ProcessWrittenNodes_MarksDirty_ForWrittenAndAdditionalNodes()
    {
        TransientBoolNode primaryBool = new("test.primary");
        TransientValueNode<string> additional = new("test.additional", "<default>");

        StateGraph graph = new();
        ProbeEdge edge = new(graph.Root, primaryBool, additional);
        graph.AddEdge(edge);

        StateGraphEvaluator evaluator = new(graph);

        // Reach into the evaluator's internals: build the dirty[] + nodeToIndex
        // pair the way EvaluateWithSnapshots would, then call the dispatch
        // method directly with our message. Reflection-based so we don't have
        // to publicly expose EvaluateEdgesInstrumented.
        Type evalType = typeof(StateGraphEvaluator);
        MethodInfo evaluateMethod = evalType.GetMethod("EvaluateEdgesInstrumented",
            BindingFlags.NonPublic | BindingFlags.Instance)!;

        Dictionary<StateNode, int> nodeToIndex = new(ReferenceEqualityComparer.Instance)
        {
            [primaryBool] = 0,
            [additional] = 1
        };
        bool[] dirty = new bool[2];

        DemoFrame frame = MakeBlankFrame();
        EvaluationContext ctx = new(new ProbeMessage(), frame);

        int edgesEvaluated = 0, edgesFired = 0;
        // EvaluateEdgesInstrumented(ctx, key, verbose, ref edgesEval, ref edgesFired, dirty,
        //                           nodeToIndex, appliedRecorder, currentMessageIndex)
        object?[] args =
        [
            ctx,
            typeof(ProbeMessage),
            false, // verbose
            edgesEvaluated, // ref
            edgesFired, // ref
            dirty,
            nodeToIndex,
            null, // appliedRecorder — not exercised here
            -1 // currentMessageIndex
        ];
        evaluateMethod.Invoke(evaluator, args);
        edgesEvaluated = (int)args[3]!;
        edgesFired = (int)args[4]!;

        Console.WriteLine($"edgesEvaluated={edgesEvaluated}  edgesFired={edgesFired}");
        Console.WriteLine($"AppliedCount={edge.AppliedCount}  DirectCallCount={edge.DirectCallCount}");
        Console.WriteLine($"primaryBool.IsActive={primaryBool.IsActive}");
        Console.WriteLine($"additional value={additional.GetDisplayValue()}");
        Console.WriteLine($"dirty[primary]={dirty[0]}  dirty[additional]={dirty[1]}");

        // Edge must have fired exactly once.
        await Assert.That(edge.AppliedCount).IsEqualTo(1);
        await Assert.That(edgesFired).IsEqualTo(1);

        // Live values are updated.
        await Assert.That(primaryBool.IsActive).IsTrue();
        await Assert.That(additional.GetDisplayValue()).IsEqualTo("written-by-edge");

        // The multi-write invariant: BOTH nodes have their dirty bits set so the
        // snapshot loop captures them.
        await Assert.That(dirty[0]).IsTrue()
            .Because("WrittenNode dirty bit must be set");
        await Assert.That(dirty[1]).IsTrue()
            .Because("AdditionalWrittenNodes dirty bit must be set — this is the undeclared-write regression");
    }

    private static DemoFrame MakeBlankFrame() => new()
    {
        Command = "DEM_FakeProbe",
        FrameNumber = 0,
        ServerTick = 0,
        RawStart = 0,
        RawLength = 1,
        HeaderLength = 1,
        IsCompressed = false
    };

    /// <summary>Minimal NetMessage subclass used as the dispatch payload.</summary>
    /// <remarks>Initializes a new <see cref="ProbeMessage" /> instance.</remarks>
    [method: SetsRequiredMembers]
    private sealed class ProbeMessage() : NetMessage("DEM_FakeProbe", new Empty())
    {
    }

    /// <summary>
    ///     Minimal edge that writes one primary node and one additional node.
    ///     The primary is a bool (Activate); the additional is a string
    ///     value-node. Both should appear in the snapshot post-fire.
    /// </summary>
    private sealed class ProbeEdge(StateNode source, TransientBoolNode primary, TransientValueNode<string> additional) : StateEdge(source)
    {
        /// <summary>Number of times <c>TryApply</c> ran and returned <c>true</c>.</summary>
        public int AppliedCount;

        /// <summary>Number of times <c>TryApplyDirect</c> was invoked by the dispatcher.</summary>
        public int DirectCallCount;

        /// <inheritdoc />
        public override IReadOnlyList<StateNode>? AdditionalWrittenNodes => [additional];

        /// <inheritdoc />
        public override EdgeEffect? DeclaredEffect => EdgeEffect.Activate;

        /// <inheritdoc />
        public override Type MessageType => typeof(ProbeMessage);

        /// <inheritdoc />
        public override StateNode? WrittenNode => primary;

        /// <inheritdoc />
        public override bool TryApply(EvaluationContext context)
        {
            AppliedCount++;
            primary.Activate();
            additional.SetValue("written-by-edge");
            return true;
        }

        /// <inheritdoc />
        public override bool TryApplyDirect(object payload, EvaluationContext context)
        {
            DirectCallCount++;
            return TryApply(context);
        }
    }
}
