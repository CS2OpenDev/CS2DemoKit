#region

using CS2DemoKit.Parser.GameEvents;

#endregion

namespace CS2DemoKit.TestSupport;

/// <summary>
///     Reaches the payload record under a <see cref="GameEvent" /> fire.
/// </summary>
/// <remarks>
///     Compiled rule expressions bind to the fire rather than its payload, so that per-fire transport
///     (the tick a <c>capture: event.tick</c> reads) stays addressable. Hand-written test predicates
///     handed to the same edge constructors therefore receive the fire too, and reach their wire fields
///     through here.
/// </remarks>
public static class GameEventPayloadExtensions
{
    /// <summary>The fire's payload as <typeparamref name="T" />.</summary>
    public static T Of<T>(this GameEvent fire) where T : class => (T)fire.Payload!;
}
