#region

using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Analysis.Nodes;

#endregion

namespace CS2DemoKit.Analysis.Edges;

/// <summary>
///     Drives a SETTLE-time recompute of the <c>flag: when:</c> logic node(s) that gate on a
///     writer-less <see cref="EntityValuePullNode" />. A pull-node is always
///     <see cref="StateNode.IsActive" /> and has no writer edge, so the evaluator's logic-recompute index
///     (keyed by the event types that WRITE a logic node's input sources) never buckets the enclosing
///     <see cref="ConjunctionNode" /> / <see cref="DisjunctionNode" /> under any message — the flag would
///     be evaluated once at init and then frozen. This edge fires on each profile <c>$round_end</c>
///     concrete event and declares the pull-node(s) as written, which (a) buckets the dependent flag
///     logic node under <c>round_end</c> in the logic-recompute index and (b) at runtime marks that logic
///     node's inputs dirty — so the evaluator recomputes the flag at round end, reading the pull-node's
///     live round-end entity value. That is the documented <c>when:</c> SETTLE point (flag-eval), the
///     counterpart to <see cref="ComputeOnRoundEndEdge" />'s round-end compute recompute.
///     <para>
///         One instance per concrete <c>$round_end</c> event (idempotent — it writes nothing, only
///         schedules the recompute). The pull-nodes are <see cref="ISnapshotExcludedNode" />, so declaring
///         them written adds no snapshot column; the effect is purely the dependent-logic recompute.
///     </para>
/// </summary>
public sealed class EntityPullNodeSettleEdge(StateNode source, StateNode[] pullNodes, Type messageType)
    : StateEdge(source)
{
    /// <inheritdoc />
    public override EdgeEffect? DeclaredEffect => EdgeEffect.SetValue;

    /// <summary>
    ///     The pull-node(s) the dependent flag logic node reads. Declaring them written both indexes the
    ///     flag under this edge's <c>round_end</c> message type and marks the flag's inputs dirty at
    ///     runtime, so the logic-settle pass recomputes the flag reading their live round-end values.
    /// </summary>
    public override IReadOnlyList<StateNode>? AdditionalWrittenNodes => pullNodes;

    /// <inheritdoc />
    public override Type MessageType => messageType;

    /// <inheritdoc />
    public override bool TryApply(EvaluationContext context) => false;

    /// <inheritdoc />
    public override bool TryApplyDirect(object payload, EvaluationContext context) => true;
}
