#region

using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Analysis.Nodes;
using CS2DemoKit.Parser.GameEvents;

#endregion

namespace CS2DemoKit.Analysis.Edges;

/// <summary>
///     Trigger edge for a keyed bucket counter (v2 <c>count by:</c>): on each matching
///     <typeparamref name="TEvent" /> (source active, condition passes, guard clear), evaluates
///     the key selector and folds the delta into that bucket via
///     <see cref="KeyedCounterNode.Combine" /> under the node's reduce mode — the delta is +1 for
///     <c>increment</c> or the compiled <c>value:</c> expression for <c>add</c>. An <c>Add</c>-mode
///     node (the v1 path and every count/sum bucket) reduces byte-identically to the old
///     <see cref="KeyedCounterNode.Add" /> accumulate.
/// </summary>
/// <param name="source">Must be active for this edge to be eligible (the rule's parent gate).</param>
/// <param name="target">The keyed-counter node to accumulate into.</param>
/// <param name="keySelector">Compiled <c>key:</c> expression (e.g. <c>event.Weapon</c>).</param>
/// <param name="deltaSelector">
///     Compiled <c>value:</c> expression for <c>add</c> triggers; <c>null</c> means <c>increment</c>
///     semantics (+1 per fire).
/// </param>
/// <param name="condition">Optional filter; the edge only applies when this returns <c>true</c>.</param>
/// <param name="suppressionGuard">
///     Optional first-wins-per-round guard for multi-event <c>$logical</c> expansions (same
///     contract as <see cref="OnGameEventSetValue{TEvent,TValue}" />).
/// </param>
public sealed class KeyedCounterEdge<TEvent>(
    StateNode source,
    KeyedCounterNode target,
    Func<GameEvent, string> keySelector,
    Func<GameEvent, double>? deltaSelector = null,
    Func<GameEvent, bool>? condition = null,
    BoolNode? suppressionGuard = null) : StateEdge(source)
    where TEvent : class
{
    /// <inheritdoc />
    public override EdgeEffect? DeclaredEffect => EdgeEffect.SetValue;

    /// <inheritdoc />
    public override Type MessageType => typeof(TEvent);

    /// <inheritdoc />
    public override StateNode? WrittenNode => target;

    /// <summary>The first-wins guard is also written — undeclared writes bypass dirty tracking.</summary>
    public override IReadOnlyList<StateNode>? AdditionalWrittenNodes =>
        suppressionGuard is null ? null : [suppressionGuard];

    /// <inheritdoc />
    public override bool TryApply(EvaluationContext context)
    {
        if (context.Message is not GameEventMessage gem || gem.DecodedEvent.Payload is not TEvent evt)
        {
            return false;
        }

        return Apply(gem.DecodedEvent);
    }

    /// <inheritdoc />
    public override bool TryApplyDirect(object payload, EvaluationContext context) =>
        Apply(context.Fire!);

    // Takes the FIRE: every delegate here is compiled against the envelope so that per-fire transport
    // stays addressable, and the payload is only ever the dispatch gate.
    private bool Apply(GameEvent evt)
    {
        if (suppressionGuard?.IsActive == true)
        {
            return false;
        }

        if (condition is not null && !condition(evt))
        {
            return false;
        }

        target.Combine(keySelector(evt), deltaSelector?.Invoke(evt) ?? 1.0);
        suppressionGuard?.Activate();
        return true;
    }
}
