namespace CS2DemoKit.Analysis.RulesetsV2.Model;

/// <summary>
///     Which clock a tick-valued stat's value is on. A demo carries two: the frame clock
///     (<c>DemoFrame</c> indices, timeline events, highlights, <c>GameEvent.GameTick</c>) and the
///     server clock a wire event is stamped with (<c>GameEvent.ServerTick</c>), which is higher by
///     the demo's <c>ServerStartTick</c>.
/// </summary>
public enum TickClock
{
    /// <summary>Not a bare tick read, or a value derived from one: the clock is not known.</summary>
    None = 0,

    /// <summary>The server clock: <c>event.tick</c> on a wire event.</summary>
    Server,

    /// <summary>
    ///     The frame clock: <c>event.frame_tick</c> on any event, or <c>event.tick</c> on a
    ///     synthesized event, which has no server stamp.
    /// </summary>
    Frame
}
