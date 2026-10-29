#region

using CS2DemoKit.Parser.GameEvents;

#endregion

namespace CS2DemoKit.Analysis.Events;

/// <summary>
///     Synthesized by the evaluator at end of stream for a finished match whose server never sent
///     <c>cs_win_panel_match</c>. It is the final entry of the GOTV family's <c>$match_end</c>
///     binding, so <c>match_live</c> closes as a real win panel would close it.
///     <para>
///         <b>This moves no stat.</b> It fires after every death in the file, and nothing in the
///         shipped rules keys on <c>$match_end</c> beyond the deactivation itself. What it fixes is
///         <c>match_live</c> reading true at end of stream on a match that is over, measured on 10
///         of 388 demos.
///     </para>
///     <para>
///         <b>Exclusive by <c>match_live</c> itself.</b> <c>cs_win_panel_match</c> deactivates the
///         node, so a demo that sent one cannot reach this gate. Drop that condition and the gate
///         fires on 385 of 388.
///     </para>
///     <para>
///         <b>All three clocks carry the last frame's tick</b>, the convention every synthesized
///         event follows (see <see cref="CadenceTickEvent" />).
///     </para>
/// </summary>
/// <param name="FrameNumber">The frame-clock tick of the last frame of the run (not a frame index).</param>
/// <param name="ServerTick">The same tick.</param>
/// <param name="GameTick">The same tick.</param>
public sealed record MatchEndTerminalEvent(int FrameNumber, int ServerTick, int GameTick)
    : GameEvent("match_end_terminal", -1, FrameNumber, ServerTick, GameTick);
