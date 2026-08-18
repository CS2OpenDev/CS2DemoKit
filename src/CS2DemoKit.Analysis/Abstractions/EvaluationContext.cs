#region

using CS2DemoKit.Parser;
using CS2DemoKit.Parser.GameEvents;

#endregion

namespace CS2DemoKit.Analysis.Abstractions;

/// <summary>
///     Passed to every <see cref="StateEdge.TryApply" /> call. Represents one message
///     being processed by the evaluator loop.
/// </summary>
/// <param name="message">The current message being evaluated.</param>
/// <param name="frame">The demo frame the message belongs to.</param>
public readonly struct EvaluationContext(NetMessage message, DemoFrame frame)
{
    /// <summary>The current message being dispatched (game event, net message, or synthesized event).</summary>
    public NetMessage Message { get; } = message;

    /// <summary>The demo frame containing the current <see cref="Message" />.</summary>
    public DemoFrame Frame { get; } = frame;

    /// <summary>The game tick of the frame containing the current message.</summary>
    public int GameTick => Frame.ServerTick;

    /// <summary>
    ///     The game-event fire backing the current message, or <c>null</c> when the message is not a
    ///     game event. Compiled rule expressions bind to the fire rather than its payload record, so
    ///     that per-fire transport stays addressable — a <c>capture: event.tick</c> reads the tick off
    ///     here, and no payload record carries it.
    /// </summary>
    public GameEvent? Fire => (Message as GameEventMessage)?.DecodedEvent;

    /// <summary>
    ///     Convenience accessor: returns the decoded game event as <typeparamref name="T" /> if the
    ///     current message is a <see cref="GameEventMessage" /> with a matching payload, otherwise <c>null</c>.
    /// </summary>
    public T? GameEventAs<T>() where T : GameEvent
    {
        if (Message is GameEventMessage { DecodedEvent: not null and T typed })
        {
            return typed;
        }

        return null;
    }
}
