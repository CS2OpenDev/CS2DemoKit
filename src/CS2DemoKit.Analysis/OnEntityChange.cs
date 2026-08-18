#region

using CS2DemoKit.Analysis.Abstractions;

#endregion

namespace CS2DemoKit.Analysis;

/// <summary>
///     Boolean-effect edge keyed on a synthesized <see cref="EntityValueChangedEvent{TMarker}" />.
///     Activates or deactivates the destination bool node when the scanner emits a change matching
///     <typeparamref name="TMarker" />, optionally filtered by a predicate over the new value.
///     <para>
///         Parallel to <c>OnNetMessage&lt;TPayload&gt;</c> but without the <c>TPayload : IMessage</c>
///         constraint — synthesized events ride on <see cref="EntityChangeMessage" />, not the
///         protobuf payload slot.
///     </para>
/// </summary>
public sealed class OnEntityChange<TMarker>(
    StateNode source,
    BoolNode destination,
    EdgeEffect effect,
    Func<EntityValueChangedEvent<TMarker>, bool>? condition = null,
    BoolNode? suppressionGuard = null) : StateEdge(source)
{
    /// <inheritdoc />
    public override EdgeEffect? DeclaredEffect => effect;

    /// <inheritdoc />
    public override Type MessageType => typeof(EntityValueChangedEvent<TMarker>);


    /// <inheritdoc />
    public override StateNode? WrittenNode => destination;

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

        if (effect == EdgeEffect.Activate)
        {
            destination.Activate();
        }
        else
        {
            destination.Deactivate();
        }

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

        if (effect == EdgeEffect.Activate)
        {
            destination.Activate();
        }
        else
        {
            destination.Deactivate();
        }

        suppressionGuard?.Activate();
        return true;
    }
}
