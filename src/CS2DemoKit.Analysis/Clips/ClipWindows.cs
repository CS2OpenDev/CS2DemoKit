namespace CS2DemoKit.Analysis.Clips;

/// <summary>
///     Highlight → clip window math (docs/csvg-integration/implementation-plan.md §7.7). Pure functions, no CSVG
///     dependency. The load-bearing rule: the WHOLE window — paddings, round floor, demo-end
///     clamp — is computed in the <b>frame clock</b>, and any CS2-demo-tick conversion is applied
///     exactly once, at emission, by the consumer that talks to CS2. Mixing the spaces skews the
///     clamps whenever the offset ≠ 0 — the very case the shim exists for. Nothing here ever
///     subtracts <c>ParsedDemo.ServerStartTick</c>: <c>HighlightFired.Tick</c>,
///     <c>RuleChainEvent.Tick</c> and <c>GameEvent.GameTick</c> are already frame clock.
/// </summary>
public static class ClipWindows
{
    /// <summary>
    ///     Computes one highlight's window. All inputs are FRAME CLOCK except
    ///     <paramref name="tickOffset" />, which converts the finished window to CS2 demo ticks
    ///     (pass <c>0</c> — the default — to keep the window in the frame clock and convert later):
    ///     <code>
    ///     startFrame = max(0, roundStartFrameClock?, min(eventTick − leadIn×tickRate, clipStartFrameClock?))
    ///     endFrame   = min(tickCount, eventTick + leadOut×tickRate)
    ///     Start/EndTick = frame + tickOffset
    ///     </code>
    ///     The round start FLOORS the lead-in (a clip never reaches into the previous round);
    ///     the demo end clamps the lead-out. A degenerate demo yields a ≥1-tick window.
    ///     <para>
    ///         <paramref name="clipStartFrameClock" /> (the first contributing event of a count-based
    ///         highlight — e.g. the first kill of a 4K) pulls the start EARLIER than the lead-in would
    ///         reach, so a multi-event sequence longer than the lead-in still starts at its first event.
    ///         It is applied BEFORE the round-start floor, so a clip still never crosses into the prior
    ///         round. <c>null</c> = the pre-existing lead-in-only behavior.
    ///     </para>
    /// </summary>
    /// <param name="eventTickFrameClock">The highlight's firing tick (frame clock).</param>
    /// <param name="roundStartFrameClock">The lead-in floor (frame clock), or null for no floor.</param>
    /// <param name="tickRate">The demo's tick rate; ≤ 0 falls back to 64.</param>
    /// <param name="leadInSeconds">Seconds of context before the firing tick.</param>
    /// <param name="leadOutSeconds">Seconds of follow-through after it.</param>
    /// <param name="tickCount">The demo's tick count (frame clock) — the lead-out clamp.</param>
    /// <param name="tickOffset">Added to BOTH ends after every clamp; 0 = stay in the frame clock.</param>
    /// <param name="clipStartFrameClock">First contributing event's tick (frame clock), or null.</param>
    public static (long StartTick, long EndTick) Compute(
        int eventTickFrameClock,
        int? roundStartFrameClock,
        int tickRate,
        double leadInSeconds,
        double leadOutSeconds,
        int tickCount,
        int tickOffset = 0,
        int? clipStartFrameClock = null)
    {
        int rate = tickRate > 0 ? tickRate : 64;
        long eventTick = Math.Max(0, eventTickFrameClock);

        long startFrame = Math.Max(0, eventTick - (long)Math.Round(leadInSeconds * rate));

        // Reach the start back to the first contributing event when it precedes the lead-in — but
        // only earlier, never later (a clipStart AFTER the lead-in must not shrink the window). This
        // happens BEFORE the round-start floor below, so the floor still bounds it to this round.
        if (clipStartFrameClock is int clipStart)
        {
            startFrame = Math.Min(startFrame, Math.Max(0, clipStart));
        }

        if (roundStartFrameClock is int roundStart)
        {
            startFrame = Math.Max(startFrame, Math.Max(0, roundStart));
        }

        long endFrame = Math.Min(tickCount, eventTick + (long)Math.Round(leadOutSeconds * rate));
        if (endFrame <= startFrame)
        {
            endFrame = startFrame + 1;
        }

        return (startFrame + tickOffset, endFrame + tickOffset);
    }

    /// <summary>
    ///     The round-start floor for an event: the latest round start at or before the event tick
    ///     (both FRAME CLOCK — see <see cref="ClipRounds" />, the round authority for clip work).
    ///     Null when no round precedes it (warmup firings).
    /// </summary>
    /// <param name="rounds">The demo's rounds; order is irrelevant (the whole list is scanned).</param>
    /// <param name="eventTickFrameClock">The highlight's firing tick (frame clock).</param>
    public static int? RoundStartFor(IReadOnlyList<ClipRound> rounds, int eventTickFrameClock)
    {
        ArgumentNullException.ThrowIfNull(rounds);

        int? best = null;
        foreach (ClipRound round in rounds)
        {
            if (round.StartTickFrameClock <= eventTickFrameClock
                && (best is null || round.StartTickFrameClock > best))
            {
                best = round.StartTickFrameClock;
            }
        }

        return best;
    }

    /// <summary>
    ///     Coalesces overlapping (or touching) candidate windows per (demo, player, round) —
    ///     §7.7: back-to-back kills become ONE clip, titles concatenated — and returns the final
    ///     plan sorted by (demo, StartTick) ascending, ready for compilation ordering.
    ///     <para>
    ///         Order-independent by construction: every group is start-sorted before merging, so two
    ///         identical candidate sets produce identical clips regardless of enumeration order. Keep
    ///         it that way — presentation ordering belongs to the caller, never in here.
    ///     </para>
    ///     <para>
    ///         Translation-invariant: because merging compares candidates against each other only, a
    ///         uniform tick offset applied to every candidate cannot change which ones merge.
    ///     </para>
    /// </summary>
    /// <param name="candidates">The per-highlight windows (all in ONE tick space).</param>
    public static List<Clip> Coalesce(IEnumerable<Candidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        List<Clip> clips = [];
        // Case-insensitive on the path half of the key — the same comparer discipline as the
        // final sort below and the cache store's row keying (a casing-variant path must not
        // split one demo's candidates into two groups).
        foreach (IGrouping<(string DemoPath, string SteamId64, int RoundNumber), Candidate> group in candidates
                     .GroupBy(
                         c => (DemoPath: c.DemoPath.ToUpperInvariant(), c.SteamId64, c.RoundNumber),
                         c => c))
        {
            Candidate? current = null;
            List<string> titles = [];
            foreach (Candidate candidate in group.OrderBy(c => c.StartTick))
            {
                if (current is not null && candidate.StartTick <= current.EndTick)
                {
                    current = current with
                    {
                        EndTick = Math.Max(current.EndTick, candidate.EndTick)
                    };
                    titles.Add(candidate.Title);
                    continue;
                }

                if (current is not null)
                {
                    clips.Add(ToClip(current, titles));
                }

                current = candidate;
                titles = [candidate.Title];
            }

            if (current is not null)
            {
                clips.Add(ToClip(current, titles));
            }
        }

        return [.. clips.OrderBy(c => c.DemoPath, StringComparer.OrdinalIgnoreCase).ThenBy(c => c.StartTick)];

        static Clip ToClip(Candidate candidate, List<string> titles)
        {
            return new Clip(
                candidate.DemoPath, candidate.SteamId64, candidate.PlayerName, candidate.RoundNumber,
                candidate.StartTick, candidate.EndTick, [.. titles]);
        }
    }

    /// <summary>
    ///     One clip candidate (a single highlight's window).
    /// </summary>
    /// <param name="DemoPath">Row demo path.</param>
    /// <param name="SteamId64">Attributed player (cache join).</param>
    /// <param name="PlayerName">RAW in-demo name (spectate currency).</param>
    /// <param name="RoundNumber">Round attribution (the coalescing scope).</param>
    /// <param name="StartTick">Window start, in whatever tick space <see cref="Compute" /> was given.</param>
    /// <param name="EndTick">Window end, in the same space as <paramref name="StartTick" />.</param>
    /// <param name="Title">The highlight's rendered title (merged-clip labels).</param>
    public sealed record Candidate(
        string DemoPath,
        string SteamId64,
        string PlayerName,
        int RoundNumber,
        long StartTick,
        long EndTick,
        string Title);

    /// <summary>An emitted clip — one or more overlapping candidates merged (§7.7 coalescing).</summary>
    /// <param name="DemoPath">Row demo path.</param>
    /// <param name="SteamId64">Attributed player.</param>
    /// <param name="PlayerName">RAW in-demo name.</param>
    /// <param name="RoundNumber">Round attribution.</param>
    /// <param name="StartTick">Window start, in the candidates' tick space.</param>
    /// <param name="EndTick">Window end, in the candidates' tick space.</param>
    /// <param name="Titles">Every contributing highlight's title, in merge order.</param>
    public sealed record Clip(
        string DemoPath,
        string SteamId64,
        string PlayerName,
        int RoundNumber,
        long StartTick,
        long EndTick,
        IReadOnlyList<string> Titles);
}
