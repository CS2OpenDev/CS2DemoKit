#region

using CS2DemoKit.Parser;
using CS2DemoKit.Parser.GameEvents;

#endregion

namespace CS2DemoKit.Analysis.Clips;

/// <summary>
///     One round boundary in the <b>demo/frame clock</b> — the only clock clip work runs in.
/// </summary>
/// <param name="Number">
///     Sequential round number starting at 1, counted per <c>round_freeze_end</c> in demo order
///     (warmup restarts and match-restart rounds are numbered too — the clip math looks rounds up
///     BY TICK, so offsets against the scoreboard's idea of "round 1" are harmless).
/// </param>
/// <param name="StartTickFrameClock">
///     The round's opening tick in the FRAME clock (<c>GameEvent.GameTick</c>). Never
///     <c>GameEvent.ServerTick</c>, and never <c>ServerTick − ParsedDemo.ServerStartTick</c>
///     applied twice: the frame clock is what <see cref="ClipWindows" /> floors lead-in against.
/// </param>
public sealed record ClipRound(int Number, int StartTickFrameClock);

/// <summary>
///     THE round authority for clip work: derives round boundaries in the <b>frame clock</b>.
///     Every clip-pipeline consumer — window floors, round
///     attribution, cached round lists — derives rounds here so that two of them can never disagree
///     about where a round starts.
///     <para>
///         Careful: CS2 does not emit Source1's <c>round_start</c>. A round OPENS with
///         <c>round_freeze_end</c> (<see cref="RoundFreezeEndEvent" />); matching the string
///         "round_start" yields an empty list on every CS2 demo, which silently disables the
///         clip lead-in floor rather than failing loudly.
///     </para>
///     <para>
///         The absolute-clock variant is <c>DemoAnalyzer</c>'s internal round derivation, surfaced
///         as <see cref="DemoContext.Rounds" /> (<c>RoundInfo</c>): it carries winner/reason and
///         round CLOSE ticks, and its ticks are absolute engine <c>ServerTick</c>s. The two are
///         deliberately separate — this one is smaller, opens a round per freeze-end unconditionally,
///         and is the one whose numbers/ticks are persisted by clip consumers.
///     </para>
/// </summary>
public static class ClipRounds
{
    /// <summary>
    ///     Derives the demo's round boundaries in the FRAME clock. One pass over
    ///     <see cref="ParsedDemo.AllGameEvents" />.
    /// </summary>
    /// <param name="demo">The parsed demo.</param>
    /// <returns>Rounds in demo order, numbered from 1; empty for a demo with no freeze-ends.</returns>
    public static IReadOnlyList<ClipRound> Derive(ParsedDemo demo)
    {
        ArgumentNullException.ThrowIfNull(demo);
        return Derive(demo.AllGameEvents);
    }

    /// <summary>
    ///     Derives round boundaries in the FRAME clock from an already-materialized event list —
    ///     the same walk as <see cref="Derive(ParsedDemo)" />, for callers holding only the events.
    /// </summary>
    /// <param name="events">The demo's game events, in demo order.</param>
    /// <returns>Rounds in demo order, numbered from 1.</returns>
    public static IReadOnlyList<ClipRound> Derive(IReadOnlyList<GameEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);

        List<ClipRound> rounds = [];
        int roundNumber = 0;
        foreach (GameEvent gameEvent in events)
        {
            if (gameEvent.Payload is RoundFreezeEndEvent)
            {
                // GameTick, not ServerTick: this list is FRAME CLOCK end to end.
                rounds.Add(new ClipRound(++roundNumber, gameEvent.GameTick));
            }
        }

        return rounds;
    }
}
