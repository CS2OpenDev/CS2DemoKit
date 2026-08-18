#region

using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Parser.GameEvents;

#endregion

namespace CS2DemoKit.Analysis;

/// <summary>
///     A concrete <see cref="GameEventValueEdge{TEvent,TValue}" /> that folds a scalar event value
///     into the target node as a running <b>min</b> or <b>max</b> over the aggregation window — the
///     runtime behind a Rulesets v2 <c>capture: … , keep: min | max</c> stat (pre-freeze gap G2).
///     <para>
///         Mirrors the bucket min/max reducer (<see cref="Nodes.KeyedCounterNode.Combine" />): an
///         <b>unseen</b> window takes the first value verbatim rather than reducing it against the value
///         node's phantom 0 (which would corrupt an all-positive or all-negative series). The
///         "seen" state is a caller-supplied <see cref="BoolNode" /> — a round-scoped node for a
///         <c>per: round</c> capture, so it auto-resets with the extremum at each round boundary, or a
///         plain node for <c>per: match</c>. Unlike the <c>keep: first</c> suppression guard, "seen"
///         does <b>not</b> block subsequent fires — it only distinguishes the initializing write from
///         the reducing writes.
///     </para>
/// </summary>
/// <typeparam name="TEvent">The game event type that triggers this edge.</typeparam>
/// <typeparam name="TValue">The numeric value type (<see cref="int" /> or <see cref="double" />).</typeparam>
/// <param name="source">Must be active for this edge to be eligible.</param>
/// <param name="target">The value node holding the running extremum.</param>
/// <param name="element">Derives the candidate value from the game event.</param>
/// <param name="condition">Optional filter; the edge only applies when this returns <c>true</c>.</param>
/// <param name="seenGuard">
///     The window "seen" flag. Inactive ⇒ the next successful fire initializes the node to the
///     candidate; active ⇒ the fire reduces (min/max) the candidate against the current value. The
///     edge activates it on every successful fire (idempotent once set). Round-scoped for
///     <c>per: round</c>, plain for <c>per: match</c>.
/// </param>
/// <param name="keepMax"><c>true</c> for <c>keep: max</c> (running maximum); <c>false</c> for <c>keep: min</c>.</param>
/// <param name="declaredReads">
///     Read-aware ordering: the sibling nodes this edge's condition/value selector reads beyond the implicit
///     source read, so the evaluator's read-aware topological sort orders this edge after its readers'
///     writers within a dispatch slot. <c>null</c> keeps the pre-A1 ordering.
/// </param>
public sealed class OnGameEventReduceValue<TEvent, TValue>(
    StateNode source,
    ValueNode<TValue> target,
    Func<GameEvent, TValue> element,
    Func<GameEvent, bool>? condition,
    BoolNode seenGuard,
    bool keepMax,
    IReadOnlyList<StateNode>? declaredReads = null) : GameEventValueEdge<TEvent, TValue>(source, target)
    where TEvent : class
{
    private static readonly Comparer<TValue> _valueComparer = Comparer<TValue>.Default;

    /// <inheritdoc />
    public override IReadOnlyList<StateNode>? DeclaredReads => declaredReads;

    /// <inheritdoc />
    protected override TValue GetNewValue(EvaluationContext context, TEvent gameEvent)
    {
        TValue candidate = element(context.Fire!);
        if (!seenGuard.IsActive)
        {
            // Unseen window: first value initializes (never min/max against the phantom 0).
            return candidate;
        }

        int cmp = _valueComparer.Compare(candidate, Target.Value);
        bool takeCandidate = keepMax ? cmp > 0 : cmp < 0;
        return takeCandidate ? candidate : Target.Value;
    }

    /// <inheritdoc />
    protected override void OnAppliedSuccessfully(EvaluationContext context) => seenGuard.Activate();

    /// <inheritdoc />
    protected override bool ShouldApply(EvaluationContext context, TEvent gameEvent) =>
        condition is null || condition(context.Fire!);
}
