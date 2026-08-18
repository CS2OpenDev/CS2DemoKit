#region

using CS2DemoKit.Parser.GameEvents;

#endregion

namespace CS2DemoKit.Analysis.Abstractions;

/// <summary>
///     Base class for edges that update a <see cref="ValueNode{TValue}" /> when a specific
///     CS2 game event fires. Unlike <see cref="GameEventEdge{TEvent}" />, this edge sets a
///     typed value rather than toggling a boolean.
/// </summary>
/// <typeparam name="TEvent">The game event type that triggers this edge.</typeparam>
/// <typeparam name="TValue">The type of the value node this edge writes to.</typeparam>
/// <param name="source">Must be active for this edge to be eligible.</param>
/// <param name="target">The value node to update.</param>
public abstract class GameEventValueEdge<TEvent, TValue>(StateNode source, ValueNode<TValue> target) : StateEdge(source) where TEvent : class
{
    /// <inheritdoc />
    public override sealed EdgeEffect? DeclaredEffect => EdgeEffect.SetValue;

    /// <inheritdoc />
    public override sealed Type MessageType => typeof(TEvent);

    /// <summary>The value node this edge writes to when its condition is met.</summary>
    protected ValueNode<TValue> Target { get; } = target;

    /// <inheritdoc />
    public override StateNode? WrittenNode => Target;

    /// <inheritdoc />
    public override sealed bool TryApply(EvaluationContext context)
    {
        if (context.Message is not GameEventMessage gem)
        {
            return false;
        }

        if (gem.DecodedEvent.Payload is not TEvent evt)
        {
            return false;
        }

        if (!ShouldApply(context, evt))
        {
            return false;
        }

        Target.SetValue(GetNewValue(context, evt));
        OnAppliedSuccessfully(context);
        return true;
    }

    /// <inheritdoc />
    public override sealed bool TryApplyDirect(object payload, EvaluationContext context)
    {
        TEvent evt = (TEvent)payload;
        if (!ShouldApply(context, evt))
        {
            return false;
        }

        Target.SetValue(GetNewValue(context, evt));
        OnAppliedSuccessfully(context);
        return true;
    }

    /// <summary>
    ///     Returns the new value to write to <see cref="Target" />.
    ///     Only called when <see cref="ShouldApply" /> returns <c>true</c>.
    /// </summary>
    protected abstract TValue GetNewValue(EvaluationContext context, TEvent gameEvent);

    /// <summary>
    ///     Hook called after the value is set. Override to install side-effects
    ///     (e.g. flip a per-round suppression guard for first-wins multi-event triggers).
    /// </summary>
    protected virtual void OnAppliedSuccessfully(EvaluationContext context)
    {
    }

    /// <summary>
    ///     Returns <c>true</c> if the edge should apply for this specific event.
    ///     Default: always apply. Override to add filtering conditions.
    /// </summary>
    protected virtual bool ShouldApply(EvaluationContext context, TEvent gameEvent) => true;
}
