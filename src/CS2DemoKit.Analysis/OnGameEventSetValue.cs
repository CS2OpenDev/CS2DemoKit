#region

using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Parser.GameEvents;

#endregion

namespace CS2DemoKit.Analysis;

/// <summary>
///     A concrete <see cref="GameEventValueEdge{TEvent,TValue}" /> that sets the target value
///     node by applying a caller-supplied selector function to the game event.
/// </summary>
/// <example>
///     <code>
/// // Increment the round counter on every round_start
/// new OnGameEventSetValue&lt;RoundStartEvent, int&gt;(
///     graph.Root, roundNumber, _ => roundNumber.Value + 1)
/// 
/// // Capture the map name from a hypothetical map-info event
/// new OnGameEventSetValue&lt;MapInfoEvent, string&gt;(
///     graph.Root, mapName, evt => evt.MapName)
/// </code>
/// </example>
/// <param name="source">Must be active for this edge to be eligible.</param>
/// <param name="target">The value node to update.</param>
/// <param name="selector">Derives the new value from the game event.</param>
/// <param name="condition">Optional filter; edge only applies when this returns <c>true</c>.</param>
/// <param name="suppressionGuard">
///     Optional first-wins-per-round guard. When non-null, the edge fires only if the guard
///     is inactive; on a successful fire it activates the guard. Use a round-scoped bool node
///     so the guard auto-resets at round boundaries.
/// </param>
/// <param name="sourceGate">
///     Optional parent when: gate — the edge fires only while the gate's condition holds against
///     the parent node's current value (fire-time evaluation for same-message count-gated rules).
/// </param>
/// <param name="declaredReads">
///     Read-aware ordering: the sibling nodes this edge's condition/value selector reads beyond the implicit
///     <see cref="StateEdge.Source" /> read. The Rulesets v2 planner passes the checked stat's
///     resolved read set so the evaluator's read-aware topological sort orders this edge after its
///     readers' writers within a dispatch slot. The v1 builder leaves this <c>null</c>, which keeps
///     the pre-A1 ordering behaviour unchanged.
/// </param>
public sealed class OnGameEventSetValue<TEvent, TValue>(
    StateNode source,
    ValueNode<TValue> target,
    Func<GameEvent, TValue> selector,
    Func<GameEvent, bool>? condition = null,
    BoolNode? suppressionGuard = null,
    IConditionalEdge? sourceGate = null,
    IReadOnlyList<StateNode>? declaredReads = null) : GameEventValueEdge<TEvent, TValue>(source, target)
    where TEvent : class
{
    /// <inheritdoc />
    public override IReadOnlyList<StateNode>? DeclaredReads => declaredReads;

    /// <inheritdoc />
    protected override TValue GetNewValue(EvaluationContext context, TEvent gameEvent) =>
        selector(context.Fire!);

    /// <inheritdoc />
    protected override void OnAppliedSuccessfully(EvaluationContext context) =>
        suppressionGuard?.Activate();

    /// <inheritdoc />
    protected override bool ShouldApply(EvaluationContext context, TEvent gameEvent)
    {
        if (suppressionGuard?.IsActive == true)
        {
            return false;
        }

        // Parent when: gate — see OnGameEvent<TEvent>.Evaluate.
        if (sourceGate is { IsSatisfied: false })
        {
            return false;
        }

        return condition is null || condition(context.Fire!);
    }
}
