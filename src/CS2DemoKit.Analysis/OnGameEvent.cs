#region

using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Parser.GameEvents;

#endregion

namespace CS2DemoKit.Analysis;

/// <summary>
///     A concrete <see cref="GameEventEdge{TEvent}" /> whose condition is an optional predicate.
///     Use this for simple edges that fire whenever a given event type occurs (optionally filtered
///     by a predicate), without needing a dedicated subclass.
/// </summary>
/// <typeparam name="TEvent">The game event type that triggers this edge.</typeparam>
/// <example>
///     <code>
/// // Fires on every round_freeze_end, no additional condition.
/// new OnGameEvent&lt;RoundFreezeEndEvent&gt;(graph.Root, roundActive, EdgeEffect.Activate)
/// 
/// // Fires only when the planter matches a specific player slot.
/// new OnGameEvent&lt;BombPlantedEvent&gt;(roundActive, bombPlanted, EdgeEffect.Activate, e => e.Userid == slot)
/// </code>
/// </example>
/// <param name="source">Must be active for this edge to be eligible.</param>
/// <param name="destination">Receives <paramref name="effect" /> when condition is met.</param>
/// <param name="effect">Activate or Deactivate the destination node.</param>
/// <param name="condition">Optional predicate — if null, any event of <typeparamref name="TEvent" /> satisfies the edge.</param>
/// <param name="suppressionGuard">
///     Optional first-wins-per-round guard. When non-null, the edge fires only if the guard
///     is inactive; on a successful fire it activates the guard. Use a
///     round-scoped bool node so the guard auto-resets at round boundaries.
/// </param>
/// <param name="sourceGate">
///     Optional parent when: gate — the edge fires only while the gate's condition holds against
///     the parent node's current value (fire-time evaluation for same-message count-gated rules).
/// </param>
/// <param name="declaredReads">
///     Read-aware ordering: the sibling nodes this edge's condition reads beyond the implicit
///     <see cref="StateEdge.Source" /> read. The Rulesets v2 planner passes the checked stat's
///     resolved read set so the evaluator's read-aware topological sort orders this edge after its
///     readers' writers within a dispatch slot. The v1 builder leaves this <c>null</c>, which keeps
///     the pre-A1 ordering behaviour unchanged.
/// </param>
public sealed class OnGameEvent<TEvent>(
    StateNode source,
    BoolNode destination,
    EdgeEffect effect,
    Func<GameEvent, bool>? condition = null,
    BoolNode? suppressionGuard = null,
    IConditionalEdge? sourceGate = null,
    IReadOnlyList<StateNode>? declaredReads = null) : GameEventEdge<TEvent>(source, destination, effect) where TEvent : class
{
    /// <inheritdoc />
    public override IReadOnlyList<StateNode>? DeclaredReads => declaredReads;

    /// <inheritdoc />
    protected override bool Evaluate(EvaluationContext context, TEvent gameEvent)
    {
        if (suppressionGuard?.IsActive == true)
        {
            return false;
        }

        // Parent when: gate, evaluated at FIRE time against the parent's current value (the
        // topological sort orders the parent's writers first, so same-message semantics hold —
        // e.g. capture-on-Nth-kill patterns).
        if (sourceGate is { IsSatisfied: false })
        {
            return false;
        }

        // The compiled condition binds to the FIRE, not the payload — per-fire transport (the tick
        // an `event.tick` reference reads) lives on the envelope, not the payload record.
        return condition is null || condition(context.Fire!);
    }

    /// <inheritdoc />
    protected override void OnAppliedSuccessfully(EvaluationContext context) =>
        suppressionGuard?.Activate();
}
