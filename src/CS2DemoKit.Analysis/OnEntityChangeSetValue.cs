#region

using CS2DemoKit.Analysis.Abstractions;

#endregion

namespace CS2DemoKit.Analysis;

/// <summary>
///     Value-setting edge keyed on a synthesized <see cref="EntityValueChangedEvent{TMarker}" />.
///     Sets the target <see cref="ValueNode{TValue}" /> when the scanner emits a change matching
///     <typeparamref name="TMarker" />, deriving the new value via a caller-supplied selector
///     and optionally filtering with a predicate.
///     <para>
///         Parallel to <c>OnGameEventSetValue&lt;TEvent, TValue&gt;</c> /
///         <c>OnNetMessageSetValue&lt;TPayload, TValue&gt;</c> but without the protobuf
///         constraint — synthesized events ride on <see cref="EntityChangeMessage" />.
///     </para>
/// </summary>
public sealed class OnEntityChangeSetValue<TMarker, TValue>(
    StateNode source,
    ValueNode<TValue> target,
    Func<EntityValueChangedEvent<TMarker>, TValue> selector,
    Func<EntityValueChangedEvent<TMarker>, bool>? condition = null,
    BoolNode? suppressionGuard = null) : StateEdge(source)
{
    /// <inheritdoc />
    public override EdgeEffect? DeclaredEffect => EdgeEffect.SetValue;

    /// <inheritdoc />
    public override Type MessageType => typeof(EntityValueChangedEvent<TMarker>);

    /// <inheritdoc />
    public override StateNode? WrittenNode => target;

    /// <inheritdoc />
    public override bool TryApply(EvaluationContext context)
    {
        if (context.Message is not EntityChangeMessage ecm)
        {
            return false;
        }

        if (ecm.ChangeEvent is not EntityValueChangedEvent<TMarker> evt)
        {
            return false;
        }

        if (suppressionGuard?.IsActive == true)
        {
            return false;
        }

        if (condition is not null && !condition(evt))
        {
            return false;
        }

        target.SetValue(selector(evt));
        suppressionGuard?.Activate();
        return true;
    }

    /// <inheritdoc />
    public override bool TryApplyDirect(object payload, EvaluationContext context)
    {
        EntityValueChangedEvent<TMarker> evt = (EntityValueChangedEvent<TMarker>)payload;
        if (suppressionGuard?.IsActive == true)
        {
            return false;
        }

        if (condition is not null && !condition(evt))
        {
            return false;
        }

        target.SetValue(selector(evt));
        suppressionGuard?.Activate();
        return true;
    }
}
