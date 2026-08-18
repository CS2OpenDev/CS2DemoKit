#region

using CS2DemoKit.Parser.GameEvents;
using Google.Protobuf;

#endregion

namespace CS2DemoKit.Analysis.Abstractions;

/// <summary>
///     Base class for edges that update a <see cref="ValueNode{TValue}" /> when a specific
///     net message payload type is processed. Parallel to <see cref="GameEventValueEdge{TEvent,TValue}" />
///     but for non-game-event messages (e.g. <c>CDemoFileHeader</c>, <c>CSVCMsg_ServerInfo</c>).
/// </summary>
/// <typeparam name="TPayload">
///     The protobuf payload type this edge operates on. Used as the dispatch key — the evaluator
///     only invokes this edge when the current message's payload matches this type.
/// </typeparam>
/// <typeparam name="TValue">The type of the value node this edge writes to.</typeparam>
/// <param name="source">Must be active for this edge to be eligible.</param>
/// <param name="target">The value node to update.</param>
public abstract class NetMessageValueEdge<TPayload, TValue>(StateNode source, ValueNode<TValue> target) : StateEdge(source) where TPayload : IMessage
{
    /// <inheritdoc />
    public override sealed EdgeEffect? DeclaredEffect => EdgeEffect.SetValue;

    /// <inheritdoc />
    public override sealed Type MessageType => typeof(TPayload);

    /// <summary>The value node this edge writes to when its condition is met.</summary>
    protected ValueNode<TValue> Target { get; } = target;

    /// <inheritdoc />
    public override StateNode? WrittenNode => Target;

    /// <inheritdoc />
    public override sealed bool TryApply(EvaluationContext context)
    {
        if (context.Message is GameEventMessage)
        {
            return false;
        }

        if (context.Message.Payload is not TPayload msg)
        {
            return false;
        }

        if (!ShouldApply(context, msg))
        {
            return false;
        }

        Target.SetValue(GetNewValue(context, msg));
        OnAppliedSuccessfully(context);
        return true;
    }

    /// <inheritdoc />
    public override sealed bool TryApplyDirect(object payload, EvaluationContext context)
    {
        TPayload msg = (TPayload)payload;
        if (!ShouldApply(context, msg))
        {
            return false;
        }

        Target.SetValue(GetNewValue(context, msg));
        OnAppliedSuccessfully(context);
        return true;
    }

    /// <summary>
    ///     Returns the new value to write to <see cref="Target" />.
    ///     Only called when <see cref="ShouldApply" /> returns <c>true</c>.
    /// </summary>
    protected abstract TValue GetNewValue(EvaluationContext context, TPayload payload);

    /// <summary>
    ///     Hook called after the value is set. Override to install side-effects
    ///     (e.g. flip a per-round suppression guard for first-wins multi-event triggers).
    /// </summary>
    protected virtual void OnAppliedSuccessfully(EvaluationContext context)
    {
    }

    /// <summary>
    ///     Returns <c>true</c> if the edge should apply for this specific payload.
    ///     Default: always apply. Override to add filtering conditions.
    /// </summary>
    protected virtual bool ShouldApply(EvaluationContext context, TPayload payload) => true;
}
