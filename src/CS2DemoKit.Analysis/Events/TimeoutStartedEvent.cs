#region

using CS2DemoKit.Parser.GameEvents;

#endregion

namespace CS2DemoKit.Analysis.Events;

/// <summary>
///     Synthesized by <c>EntityChangeScanner</c> on the frame a timeout starts: the frame one of the game
///     rules' timeout flags (<c>m_bTerroristTimeOutActive</c>, <c>m_bCTTimeOutActive</c>,
///     <c>m_bTechnicalTimeOut</c>) rises from false to true. GOTV carries no wire event for either edge. A run tracks the
///     flags only when a ruleset subscribes to <c>timeout_started</c> or <c>timeout_ended</c>.
///     <para>
///         A recording that starts inside a timeout reports no start for it, only its end. All three
///         clocks carry the frame tick, the convention of every event the scanner synthesizes (see
///         <see cref="RoundDecidedEvent" />).
///     </para>
/// </summary>
/// <param name="FrameNumber">The frame-clock tick of the edge (not a frame index).</param>
/// <param name="ServerTick">The same tick.</param>
/// <param name="GameTick">The same tick.</param>
/// <param name="Side">
///     2 for a terrorist tactical timeout, 3 for a counter-terrorist one, 0 for a technical timeout,
///     which belongs to neither side. Some servers raise the technical flag on the same frame as a
///     tactical one, which then fires two starts on that frame.
/// </param>
/// <param name="TimeoutsLeft">
///     The side's tactical timeouts left as read on the same frame (on the 31 tournament demos measured the server had already taken this timeout off the count, so the
///     first of three reads 2; the starting count is the server's configuration (1, 3 and 4 were seen)); -1 for a technical
///     timeout, or when the run registers no count provider for the side. The count follows the side, so it swaps with the teams at the half.
/// </param>
public sealed record TimeoutStartedEvent(int FrameNumber, int ServerTick, int GameTick, int Side, int TimeoutsLeft)
    : GameEvent("timeout_started", -1, FrameNumber, ServerTick, GameTick)
{
    /// <inheritdoc />
    public override IReadOnlyList<(string Name, string Value, string WireType)> GetDecodedFields() =>
        [F("Side", Side), F("TimeoutsLeft", TimeoutsLeft)];
}
