#region

using CS2DemoKit.Parser.GameEvents;

#endregion

namespace CS2DemoKit.Analysis.Abstractions;

/// <summary>
///     Base class for boolean edges whose condition is evaluated against a specific CS2 game event.
///     Applies <see cref="Effect" /> to <see cref="Destination" /> when the condition is met.
/// </summary>
/// <typeparam name="TEvent">
///     The concrete <see cref="GameEvent" /> subtype this edge operates on.
///     Used as the dispatch key — the evaluator only invokes this edge when a game event of
///     exactly this type is processed.
/// </typeparam>
/// <param name="source">Must be active for this edge to be eligible.</param>
/// <param name="destination">Receives <paramref name="effect" /> when condition is met.</param>
/// <param name="effect">Activate or Deactivate the destination node.</param>
public abstract class GameEventEdge<TEvent>(StateNode source, BoolNode destination, EdgeEffect effect) : StateEdge(source) where TEvent : class
{
    /// <inheritdoc />
    public override sealed EdgeEffect? DeclaredEffect => Effect;

    /// <summary>The bool node that receives the effect when the condition is met.</summary>
    public BoolNode Destination { get; } = destination;

    /// <summary>The effect applied to <see cref="Destination" /> on a successful evaluation.</summary>
    public EdgeEffect Effect { get; } = effect;

    /// <inheritdoc />
    public override sealed Type MessageType => typeof(TEvent);

    /// <inheritdoc />
    public override StateNode? WrittenNode => Destination;

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

        if (!Evaluate(context, evt))
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
        TEvent evt = (TEvent)payload;
        if (!Evaluate(context, evt))
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
    ///     Returns <c>true</c> if this edge's condition is met for the given game event.
    ///     The message type check has already been performed by the base class.
    /// </summary>
    protected abstract bool Evaluate(EvaluationContext context, TEvent gameEvent);

    /// <summary>
    ///     Hook called after the effect is applied. Override to install side-effects
    ///     (e.g. flip a per-round suppression guard for first-wins multi-event triggers).
    /// </summary>
    protected virtual void OnAppliedSuccessfully(EvaluationContext context)
    {
    }
}
