#region

using CS2DemoKit.Parser.GameEvents;
using Google.Protobuf;

#endregion

namespace CS2DemoKit.Analysis.Abstractions;

/// <summary>
///     Base class for boolean edges whose condition is evaluated against a specific net message
///     payload type. Applies <see cref="Effect" /> to <see cref="Destination" /> when the condition
///     is met. Parallel to <see cref="GameEventEdge{TEvent}" /> but for non-game-event messages
///     (e.g. <c>CDemoFileHeader</c>, <c>CSVCMsg_ServerInfo</c>).
/// </summary>
/// <typeparam name="TPayload">
///     The protobuf payload type this edge operates on. Used as the dispatch key — the evaluator
///     only invokes this edge when the current message's payload matches this type.
/// </typeparam>
/// <param name="source">Must be active for this edge to be eligible.</param>
/// <param name="destination">Receives <paramref name="effect" /> when condition is met.</param>
/// <param name="effect">Activate or Deactivate the destination node.</param>
public abstract class NetMessageEdge<TPayload>(StateNode source, BoolNode destination, EdgeEffect effect) : StateEdge(source) where TPayload : IMessage
{
    /// <inheritdoc />
    public override sealed EdgeEffect? DeclaredEffect => Effect;

    /// <summary>The bool node that receives the effect when the condition is met.</summary>
    public BoolNode Destination { get; } = destination;

    /// <summary>The effect applied to <see cref="Destination" /> on a successful evaluation.</summary>
    public EdgeEffect Effect { get; } = effect;

    /// <inheritdoc />
    public override sealed Type MessageType => typeof(TPayload);

    /// <inheritdoc />
    public override StateNode? WrittenNode => Destination;

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

        if (!Evaluate(context, msg))
        {
            return false;
        }

        if (Effect == EdgeEffect.Activate)
        {
            Destination.Activate();
        }
        else
        {
            Destination.Deactivate();
        }

        OnAppliedSuccessfully(context);
        return true;
    }

    /// <inheritdoc />
    public override sealed bool TryApplyDirect(object payload, EvaluationContext context)
    {
        TPayload msg = (TPayload)payload;
        if (!Evaluate(context, msg))
        {
            return false;
        }

        if (Effect == EdgeEffect.Activate)
        {
            Destination.Activate();
        }
        else
        {
            Destination.Deactivate();
        }

        OnAppliedSuccessfully(context);
        return true;
    }

    /// <summary>
    ///     Returns <c>true</c> if this edge's condition is met for the given payload.
    ///     The payload type check has already been performed by the base class.
    /// </summary>
    protected abstract bool Evaluate(EvaluationContext context, TPayload payload);

    /// <summary>
    ///     Hook called after the effect is applied. Override to install side-effects
    ///     (e.g. flip a per-round suppression guard for first-wins multi-event triggers).
    /// </summary>
    protected virtual void OnAppliedSuccessfully(EvaluationContext context)
    {
    }
}
