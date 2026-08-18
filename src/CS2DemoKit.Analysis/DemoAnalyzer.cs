#region

using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Parser;
using CS2DemoKit.Parser.EntityTracking;
using CS2DemoKit.Parser.GameEvents;

#endregion

namespace CS2DemoKit.Analysis;

/// <summary>
///     Builds a <see cref="DemoContext" /> from a <see cref="ParsedDemo" />.
///     No proto decoding occurs here — all data was decoded by the parser.
/// </summary>
public static class DemoAnalyzer
{
    /// <summary>
    ///     Builds a fully indexed, entity-replayed <see cref="DemoContext" /> synchronously.
    ///     For large demos the entity replay may block for hundreds of milliseconds; use
    ///     <see cref="BuildContextAsync" /> to replay on the thread pool.
    ///     <list type="number">
    ///         <item>Builds a type-keyed event index.</item>
    ///         <item>Derives <see cref="RoundInfo" /> list from CS2 round-boundary events.</item>
    ///         <item>Fully replays <see cref="EntityTracker" /> over all frames.</item>
    ///     </list>
    /// </summary>
    public static DemoContext BuildContext(ParsedDemo demo)
    {
        Dictionary<Type, IReadOnlyList<GameEvent>> typeIndex = BuildTypeIndex(demo.AllGameEvents);
        List<RoundInfo> rounds = DeriveRounds(demo.AllGameEvents);

        EntityTracker tracker = new();
        tracker.Replay(demo.Frames);

        return new DemoContext(demo, rounds, tracker, typeIndex);
    }

    /// <summary>
    ///     Builds a fully indexed, entity-replayed <see cref="DemoContext" /> asynchronously.
    ///     The entity replay runs on the thread pool, keeping the calling thread free.
    /// </summary>
    public static async Task<DemoContext> BuildContextAsync(
        ParsedDemo demo, CancellationToken ct = default)
    {
        Dictionary<Type, IReadOnlyList<GameEvent>> typeIndex = BuildTypeIndex(demo.AllGameEvents);
        List<RoundInfo> rounds = DeriveRounds(demo.AllGameEvents);

        EntityTracker tracker = await Task.Run(() =>
        {
            EntityTracker t = new();
            t.Replay(demo.Frames);
            return t;
        }, ct).ConfigureAwait(false);

        return new DemoContext(demo, rounds, tracker, typeIndex);
    }

    /// <summary>
    ///     Builds a <see cref="DemoContext" /> without replaying entity state.
    ///     Suitable for rules that only query game events; significantly faster than
    ///     <see cref="BuildContext" /> on large demos.
    ///     <see cref="DemoContext.EntityState" /> is present but empty (no frames replayed).
    /// </summary>
    public static DemoContext BuildEventContext(ParsedDemo demo)
    {
        Dictionary<Type, IReadOnlyList<GameEvent>> typeIndex = BuildTypeIndex(demo.AllGameEvents);
        List<RoundInfo> rounds = DeriveRounds(demo.AllGameEvents);

        return new DemoContext(demo, rounds, new EntityTracker(), typeIndex);
    }

    private static RoundInfo BuildRound(
        int roundNumber,
        int startTick,
        int? endTick,
        IReadOnlyList<GameEvent> allEvents,
        int startIndex, int endIndex)
    {
        // Derive winner and reason from the round_end event within the round range.
        int? winner = null;
        int reason = 0;
        for (int i = startIndex; i <= endIndex; i++)
        {
            if (allEvents[i].Payload is RoundEndEvent re)
            {
                winner = re.Winner;
                reason = re.Reason;
                break;
            }
        }

        return new RoundInfo(roundNumber, startTick, endTick, winner, reason);
    }

    // ── Type-keyed index ──────────────────────────────────────────────────────

    private static Dictionary<Type, IReadOnlyList<GameEvent>> BuildTypeIndex(
        IReadOnlyList<GameEvent> events)
    {
        Dictionary<Type, List<GameEvent>> staging = new();
        foreach (GameEvent e in events)
        {
            // Payload type, not envelope type: every fire is a GameEvent, and a
            // synthesized event with no payload indexes under its own runtime type.
            Type t = e.Payload?.GetType() ?? e.GetType();
            if (!staging.TryGetValue(t, out List<GameEvent>? list))
            {
                staging[t] = list = [];
            }

            list.Add(e);
        }

        return staging.ToDictionary(
            kvp => kvp.Key,
            kvp => (IReadOnlyList<GameEvent>)kvp.Value);
    }

    // ── Round derivation ──────────────────────────────────────────────────────

    /// <summary>
    ///     Derives a <see cref="RoundInfo" /> list from CS2 demo events.
    ///     <para>
    ///         CS2 does not emit Source1 <c>round_start</c> / <c>round_end</c> events.
    ///         Instead:
    ///         <list type="bullet">
    ///             <item><c>round_freeze_end</c> (<see cref="RoundFreezeEndEvent" />) opens a round.</item>
    ///             <item><c>round_officially_ended</c> (<see cref="RoundOfficiallyEndedEvent" />) closes it.</item>
    ///         </list>
    ///         Winner and reason are derived from the <c>round_end</c> (<see cref="RoundEndEvent" />)
    ///         event that fires between the two markers.
    ///         If the final round has no close event (demo cut short) it is included with
    ///         <see cref="RoundInfo.EndTick" /> = <c>null</c>.
    ///     </para>
    ///     <para>
    ///         Careful — <b>clock: absolute engine <c>ServerTick</c></b> — this is the ABSOLUTE-clock variant.
    ///         <see cref="RoundInfo.StartTick" />/<see cref="RoundInfo.EndTick" /> come from
    ///         <c>GameEvent.ServerTick</c>; subtract <c>ParsedDemo.ServerStartTick</c> to reach the
    ///         demo/frame clock. Clip work must NOT use these: its round authority is the frame-clock
    ///         <see cref="Clips.ClipRounds" />, whose numbering also differs (it opens a round per
    ///         <c>round_freeze_end</c> unconditionally, where this walk numbers on close).
    ///     </para>
    /// </summary>
    private static List<RoundInfo> DeriveRounds(IReadOnlyList<GameEvent> events)
    {
        List<RoundInfo> rounds = [];
        int roundNumber = 0;
        // Holds the round-start FIRE, not its payload: the only thing needed downstream is
        // ServerTick, which is per-fire transport context and lives on the envelope now that the
        // payload records model just what the schema declares.
        GameEvent? pendingStart = null;
        int pendingStartIndex = -1;

        for (int i = 0; i < events.Count; i++)
        {
            switch (events[i].Payload)
            {
                case RoundFreezeEndEvent:
                    // Close any unclosed previous round before opening a new one.
                    if (pendingStart is not null)
                    {
                        rounds.Add(BuildRound(++roundNumber, pendingStart.ServerTick, null,
                            events, pendingStartIndex, i - 1));
                    }

                    pendingStart = events[i];
                    pendingStartIndex = i;
                    break;

                case RoundOfficiallyEndedEvent when pendingStart is not null:
                    rounds.Add(BuildRound(++roundNumber, pendingStart.ServerTick, events[i].ServerTick,
                        events, pendingStartIndex, i));
                    pendingStart = null;
                    pendingStartIndex = -1;
                    break;
            }
        }

        // Unclosed round at end of demo.
        if (pendingStart is not null)
        {
            rounds.Add(BuildRound(++roundNumber, pendingStart.ServerTick, null,
                events, pendingStartIndex, events.Count - 1));
        }

        return rounds;
    }
}
