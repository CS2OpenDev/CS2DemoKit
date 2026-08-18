#region

using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Analysis.Nodes;
using CS2DemoKit.Parser.GameEvents;

#endregion

namespace CS2DemoKit.Analysis.Edges;

/// <summary>
///     Drives a <see cref="WindowedStreakNode" /> from game events. On each matching
///     event, calls <see cref="WindowedStreakNode.ExtendOrFinalize" /> with the current tick.
/// </summary>
public sealed class WindowedStreakEdge(
    StateNode source,
    WindowedStreakNode node,
    Type messageType,
    Delegate? condition) : StateEdge(source)
{
    // The builder compiles event conditions with parameterType: typeof(GameEvent), so the
    // delegate IS a Func<GameEvent, bool>; casting once here replaces the per-fire
    // DynamicInvoke (arg-array + boxed-return allocation, reflection dispatch).
    private readonly Func<GameEvent, bool>? _condition = (Func<GameEvent, bool>?)condition;
    /// <inheritdoc />
    public override EdgeEffect? DeclaredEffect => EdgeEffect.SetValue;

    /// <inheritdoc />
    public override Type MessageType => messageType;

    /// <inheritdoc />
    public override StateNode? WrittenNode => node;

    /// <inheritdoc />
    public override bool TryApply(EvaluationContext context)
    {
        if (context.Message is not GameEventMessage gem)
        {
            return false;
        }

        GameEvent evt = gem.DecodedEvent;

        // Payload for the type gate (that is what messageType names), fire for the compiled condition
        // and the tick — both reach per-fire transport. A synthesized event is its own subject.
        object subject = evt.Payload ?? evt;
        if (subject.GetType() != messageType)
        {
            return false;
        }

        if (_condition is not null && !_condition(evt))
        {
            return false;
        }

        node.ExtendOrFinalize(evt.GameTick);
        return true;
    }
}
