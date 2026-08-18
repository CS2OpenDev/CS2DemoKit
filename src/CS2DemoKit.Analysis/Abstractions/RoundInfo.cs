namespace CS2DemoKit.Analysis.Abstractions;

/// <summary>
///     Immutable summary of a single round, derived from
///     <c>round_freeze_end</c> / <c>round_officially_ended</c> CS2 events.
/// </summary>
/// <param name="RoundNumber">1-based round number in recording order.</param>
/// <param name="StartTick">Tick of the <c>round_freeze_end</c> event (buy time over, live play begins).</param>
/// <param name="EndTick">
///     Tick of the <c>round_officially_ended</c> event, or <c>null</c> if the round was
///     still in progress when the demo ended (no closing event was found).
/// </param>
/// <param name="Winner">
///     Winning team (2 = T, 3 = CT) derived from the <c>round_end</c> event,
///     or <c>null</c> if no <c>round_end</c> was found within the round or the round is unclosed.
/// </param>
/// <param name="Reason">Round-end reason code from <c>round_end</c>, or <c>0</c> if unavailable.</param>
public sealed record RoundInfo(
    int RoundNumber,
    int StartTick,
    int? EndTick,
    int? Winner,
    int Reason);
