#region

using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Analysis.Nodes;

#endregion

namespace CS2DemoKit.Analysis.Edges;

/// <summary>
///     At round end, recomputes a set of expression-rule nodes. One instance per
///     concrete event in the active profile's <c>$round_end</c> binding.
///     Idempotent — recompute writes a deterministic value, so multi-event
///     subscription is safe without a guard.
/// </summary>
public sealed class ComputeOnRoundEndEdge(StateNode source, ComputedStatNode[] nodes, Type messageType) : StateEdge(source)
{
    /// <inheritdoc />
    public override EdgeEffect? DeclaredEffect => EdgeEffect.SetValue;

    /// <summary>
    ///     Every recomputed node must be declared as written — undeclared writes never reach the
    ///     evaluator's written-batch, so snapshot rows keep the initial value forever (the
    ///     empty-ADR/KAST%/HLTV scoreboard bug: live nodes were right, snapshots frozen at 0).
    /// </summary>
    public override IReadOnlyList<StateNode>? AdditionalWrittenNodes => nodes;

    /// <inheritdoc />
    public override Type MessageType => messageType;

    /// <inheritdoc />
    public override bool TryApply(EvaluationContext context) => false;

    /// <inheritdoc />
    public override bool TryApplyDirect(object payload, EvaluationContext context)
    {
        foreach (ComputedStatNode node in nodes)
        {
            node.Recompute();
        }

        return true;
    }
}
