#region

using CS2DemoKit.Analysis.Abstractions;

#endregion

namespace CS2DemoKit.Analysis.Nodes;

/// <summary>
///     Wraps a <see cref="BoolNode" /> so the evaluator can deactivate it at round boundaries.
///     Used for logic (conjunction / disjunction) nodes that need a round-scoped reset without
///     subclassing the round-scoped node hierarchy.
/// </summary>
/// <param name="node">The boolean logic node whose state should be cleared each round.</param>
public sealed class RoundScopedLogicNodeReset(BoolNode node) : StateEdge(node), IRoundScopedNode
{
    /// <inheritdoc />
    public override Type MessageType => typeof(void);

    /// <summary>The boolean logic node this reset edge wraps.</summary>
    public BoolNode WrappedNode { get; } = node;

    /// <inheritdoc />
    public void Reset() => WrappedNode.Deactivate();

    /// <inheritdoc />
    public override bool TryApply(EvaluationContext context) => false;
}
