#region

using CS2DemoKit.Parser.GameEvents;

#endregion

namespace CS2DemoKit.Analysis.Events;

/// <summary>
///     Synthesized by <c>EntityChangeScanner</c> on the frame the server decides a round: the frame
///     <c>CCSGameRules.m_iRoundWinStatus</c> goes from 0 to 2 (terrorists won) or 3
///     (counter-terrorists won). Valve matchmaking demos carry no <c>round_end</c>, and the round's
///     close, <c>round_officially_ended</c>, comes 448 ticks later, by which time the status has
///     already reset to 0. This is the one moment the server's own verdict is readable, so the
///     winner and reason are taken here.
///     <para>
///         <b>Dispatched after the frame's own messages, not before them</b> as the other
///         synthesized events are. The kill that decides a round is often delivered in the same
///         frame as the status change, and "decided" has to follow what decided it: dispatched
///         first, a rule reading alive counts on this event would still see the last victim alive.
///     </para>
///     <para>
///         <b>All three clocks carry the frame tick</b>, the convention every event the scanner
///         synthesizes follows (see <see cref="MolotovThrownEvent" /> for the reasoning): the
///         scanner runs off the frame clock and has no <c>ServerStartTick</c> to build the absolute
///         server tick with. So <c>event.tick</c> on this view is the frame clock, the same value
///         <c>event.frame_tick</c> reads.
///     </para>
/// </summary>
/// <param name="FrameNumber">The frame-clock tick the round was decided on (not a frame index).</param>
/// <param name="ServerTick">The same tick; see the clock note above.</param>
/// <param name="GameTick">The same tick.</param>
/// <param name="Winner">The winning side: 2 for the terrorists, 3 for the counter-terrorists.</param>
/// <param name="Reason">
///     The engine's round-end reason (<c>m_eRoundWinReason</c>): 1 the bomb exploded, 7 bomb defused, 8
///     and 9 elimination, 12 time ran out, 17 and 18 a surrender. Those are the values measured on
///     matchmaking demos, not the engine's whole list.
/// </param>
/// <param name="RoundsPlayed">Rounds decided this match, this one included (<c>m_totalRoundsPlayed</c>).</param>
public sealed record RoundDecidedEvent(int FrameNumber, int ServerTick, int GameTick, int Winner, int Reason,
        int RoundsPlayed)
    : GameEvent("round_decided", -1, FrameNumber, ServerTick, GameTick)
{
    /// <inheritdoc />
    public override IReadOnlyList<(string Name, string Value, string WireType)> GetDecodedFields() =>
        [F("Winner", Winner), F("Reason", Reason), F("RoundsPlayed", RoundsPlayed)];
}
