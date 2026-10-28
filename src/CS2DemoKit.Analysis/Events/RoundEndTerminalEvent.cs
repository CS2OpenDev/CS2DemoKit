#region

using CS2DemoKit.Parser.GameEvents;

#endregion

namespace CS2DemoKit.Analysis.Events;

/// <summary>
///     Synthesized by the evaluator at end of stream for the last round of a recording that carries
///     no marker for it: a round still open while the server has already left play. It is the final
///     entry of the GOTV family's <c>$round_end</c> binding, so every round-end stat and every
///     round-end compute closes that round exactly as a real marker would.
///     <para>
///         <b>Exclusive by the evaluator's gate, not by the binding.</b> The v2 <c>count:</c>
///         planner emits one unguarded Increment edge per concrete event, so two markers in one
///         round count it twice. This one fires only while <c>round_active</c> is still active,
///         which is false on every round a real marker already closed, and only once per run.
///     </para>
///     <para>
///         <b>All three clocks carry the last frame's tick</b>, the convention every synthesized
///         event follows (see <see cref="CadenceTickEvent" />).
///     </para>
/// </summary>
/// <param name="FrameNumber">The frame-clock tick of the last frame of the run (not a frame index).</param>
/// <param name="ServerTick">The same tick.</param>
/// <param name="GameTick">The same tick.</param>
public sealed record RoundEndTerminalEvent(int FrameNumber, int ServerTick, int GameTick)
    : GameEvent("round_end_terminal", -1, FrameNumber, ServerTick, GameTick);
