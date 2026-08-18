#region

using CS2DemoKit.Analysis.Abstractions;
using Google.Protobuf;

#endregion

namespace CS2DemoKit.Analysis;

/// <summary>
///     A concrete <see cref="NetMessageValueEdge{TPayload,TValue}" /> that sets the target value
///     node by applying a caller-supplied selector function to the message payload.
/// </summary>
/// <example>
///     <code>
/// // Capture the map name from the demo file header
/// new OnNetMessageSetValue&lt;CDemoFileHeader, string&gt;(
///     graph.Root, mapNameNode, hdr => hdr.MapName)
/// </code>
/// </example>
/// <param name="source">Must be active for this edge to be eligible.</param>
/// <param name="target">The value node to update.</param>
/// <param name="selector">Derives the new value from the message payload.</param>
/// <param name="condition">
///     Optional predicate over the payload; when non-null the edge applies only if it returns
///     <c>true</c>. Mirrors <c>OnGameEventSetValue</c> (work item 0.4a — net-message conditions
///     were previously silently ignored). Parameter position (between selector and guard)
///     matters: RuleChainBuilder binds these constructors positionally via Activator.
/// </param>
/// <param name="suppressionGuard">
///     Optional first-wins-per-round guard. When non-null, the edge fires only if the guard
///     is inactive; on a successful fire it activates the guard. Use a round-scoped bool node
///     so the guard auto-resets at round boundaries.
/// </param>
public sealed class OnNetMessageSetValue<TPayload, TValue>(
    StateNode source,
    ValueNode<TValue> target,
    Func<TPayload, TValue> selector,
    Func<TPayload, bool>? condition = null,
    BoolNode? suppressionGuard = null) : NetMessageValueEdge<TPayload, TValue>(source, target)
    where TPayload : IMessage
{
    /// <inheritdoc />
    protected override TValue GetNewValue(EvaluationContext context, TPayload payload) =>
        selector(payload);

    /// <inheritdoc />
    protected override void OnAppliedSuccessfully(EvaluationContext context) =>
        suppressionGuard?.Activate();

    /// <inheritdoc />
    protected override bool ShouldApply(EvaluationContext context, TPayload payload)
    {
        if (suppressionGuard?.IsActive == true)
        {
            return false;
        }

        return condition is null || condition(payload);
    }
}
