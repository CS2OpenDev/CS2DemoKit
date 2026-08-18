#region

using System.Globalization;
using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Parser;

#endregion

namespace CS2DemoKit.Analysis.Clips;

/// <summary>
///     Clip-window padding policy (docs/csvg-integration/implementation-plan.md §7.7). Seconds, converted against the demo's
///     own tick rate — never a hardcoded 64.
/// </summary>
/// <param name="LeadInSeconds">Context before the firing tick.</param>
/// <param name="LeadOutSeconds">Follow-through after it.</param>
/// <param name="FloorAtRoundStart">
///     When true (the default), the lead-in is floored at the enclosing round's start so a clip
///     never reaches into the previous round. See <see cref="ClipRounds" /> — the frame-clock round
///     authority the floor is taken from.
/// </param>
public sealed record ClipPlanOptions(
    double LeadInSeconds,
    double LeadOutSeconds,
    bool FloorAtRoundStart = true);

/// <summary>
///     The demo-level facts the window math needs. All ticks are FRAME CLOCK.
/// </summary>
/// <param name="DemoPath">Identifies the demo; also the coalescing/sort key (case-insensitive).</param>
/// <param name="TickRate">The demo's tick rate; ≤ 0 falls back to 64.</param>
/// <param name="TickCount">The demo's tick count — the lead-out clamp.</param>
/// <param name="Rounds">The demo's rounds (<see cref="ClipRounds.Derive(ParsedDemo)" />).</param>
public sealed record ClipDemo(
    string DemoPath,
    int TickRate,
    int TickCount,
    IReadOnlyList<ClipRound> Rounds)
{
    /// <summary>
    ///     Builds the demo facts from a parsed demo, deriving rounds in the frame clock.
    /// </summary>
    /// <param name="demo">The parsed demo.</param>
    /// <param name="demoPath">The path the plan should carry (the file the consumer will play).</param>
    public static ClipDemo FromParsed(ParsedDemo demo, string demoPath)
    {
        ArgumentNullException.ThrowIfNull(demo);
        return new ClipDemo(demoPath, demo.TickRate, demo.TickCount, ClipRounds.Derive(demo));
    }
}

/// <summary>
///     One highlight staged for clipping, already attributed to a player. All ticks are FRAME CLOCK
///     (<c>HighlightFired.Tick</c> is emitted in that clock — never <c>− ServerStartTick</c>).
/// </summary>
/// <param name="TickFrameClock">The firing tick (frame clock) the window is centred on.</param>
/// <param name="RoundNumber">Round attribution — the coalescing scope, with demo + player.</param>
/// <param name="SteamId64">The attributed player's SteamID64 as a string ("" when unresolved).</param>
/// <param name="PlayerNameRaw">RAW in-demo name — the <c>spec_player</c> currency; never sanitized.</param>
/// <param name="Title">The rendered highlight title (merged-clip labels).</param>
/// <param name="ClipStartTickFrameClock">
///     First contributing event of a count-based highlight (frame clock), or null. Reaches the
///     window start EARLIER than the lead-in would, still floored by the round start.
/// </param>
public sealed record ClipHighlight(
    int TickFrameClock,
    int RoundNumber,
    string SteamId64,
    string PlayerNameRaw,
    string Title,
    int? ClipStartTickFrameClock = null);

/// <summary>One demo's staged highlights — the unit a multi-demo plan is assembled from.</summary>
/// <param name="Demo">The demo's facts.</param>
/// <param name="Highlights">Its staged highlights.</param>
public sealed record ClipDemoHighlights(ClipDemo Demo, IReadOnlyList<ClipHighlight> Highlights);

/// <summary>
///     One planned clip: a coalesced window over one (demo, player, round), in the <b>frame
///     clock</b>. A consumer that plays this in CS2 converts to CS2 demo ticks at emission — once,
///     at the boundary (the D2 <c>TickOffset</c> shim); see <see cref="ClipWindows" />.
/// </summary>
/// <param name="DemoPath">The demo to load.</param>
/// <param name="SteamId64">The attributed player ("" when unresolved).</param>
/// <param name="PlayerNameRaw">RAW in-demo name (spectate target).</param>
/// <param name="RoundNumber">Round attribution.</param>
/// <param name="StartTickFrameClock">Window start — FRAME CLOCK.</param>
/// <param name="EndTickFrameClock">Window end — FRAME CLOCK.</param>
/// <param name="TickRate">The demo's tick rate (playback timeouts derive from it).</param>
/// <param name="Titles">Every contributing highlight's title, in merge order.</param>
public sealed record PlannedClip(
    string DemoPath,
    string SteamId64,
    string PlayerNameRaw,
    int RoundNumber,
    long StartTickFrameClock,
    long EndTickFrameClock,
    int TickRate,
    IReadOnlyList<string> Titles);

/// <summary>
///     A neutral clip plan: the finished output of highlights → surfacing → windows → coalescing,
///     carrying no CS2/renderer/UI types. Clips are ordered by (demo path, start tick) ascending.
/// </summary>
/// <param name="Clips">The planned clips.</param>
public sealed record ClipPlan(IReadOnlyList<PlannedClip> Clips)
{
    /// <summary>An empty plan.</summary>
    public static ClipPlan Empty { get; } = new([]);
}

/// <summary>
///     The path from <c>AnalysisRun.Highlights</c> to a neutral <see cref="ClipPlan" />
///     (planning is public, execution is not): surface the firings, compute
///     each one's window in the frame clock, coalesce per (demo, player, round).
///     <para>
///         <b>Tick clock.</b> Everything in and out of this type is the demo/frame clock.
///         <c>HighlightFired.Tick</c>, <c>RuleChainEvent.Tick</c> and <c>GameEvent.GameTick</c> are
///         ALREADY frame clock — never subtract <c>ParsedDemo.ServerStartTick</c> from them (only
///         the absolute <c>GameEvent.ServerTick</c> converts). A consumer driving CS2 adds its
///         demo-tick offset exactly once, at emission.
///     </para>
/// </summary>
public static class ClipPlanner
{
    /// <summary>
    ///     The one-call path for a consumer holding a parsed demo and its analysis run: surfaces the
    ///     firings (<see cref="HighlightSurfacing.Surface" />), attributes each to a player through
    ///     the demo roster (by slot — falling back to <c>HighlightFired.PlayerName</c> and an empty
    ///     SteamID when the slot is not in the roster), and plans their windows.
    /// </summary>
    /// <param name="demo">The parsed demo the highlights came from.</param>
    /// <param name="demoPath">The path the plan should carry.</param>
    /// <param name="highlights">The run's highlights (<c>AnalysisRun.Highlights</c>), unsurfaced.</param>
    /// <param name="options">Padding policy.</param>
    public static ClipPlan Plan(
        ParsedDemo demo,
        string demoPath,
        IReadOnlyList<HighlightFired> highlights,
        ClipPlanOptions options)
    {
        ArgumentNullException.ThrowIfNull(demo);
        ArgumentNullException.ThrowIfNull(highlights);

        List<ClipHighlight> staged = [];
        foreach (HighlightFired fired in HighlightSurfacing.Surface(highlights))
        {
            demo.Players.TryGetValue(fired.PlayerSlot, out PlayerInfo? player);
            staged.Add(new ClipHighlight(
                fired.Tick,
                fired.RoundNumber,
                player is null ? "" : player.SteamId64.ToString(CultureInfo.InvariantCulture),
                // RAW name either way — the spectate target must be the exact in-demo spelling.
                string.IsNullOrEmpty(player?.Name) ? fired.PlayerName : player.Name,
                fired.RenderedTitle,
                fired.ClipStartTick));
        }

        return Plan(ClipDemo.FromParsed(demo, demoPath), staged, options);
    }

    /// <summary>Plans one demo's staged highlights.</summary>
    /// <param name="demo">The demo's facts.</param>
    /// <param name="highlights">Staged highlights, already attributed and surfaced.</param>
    /// <param name="options">Padding policy.</param>
    public static ClipPlan Plan(
        ClipDemo demo,
        IReadOnlyList<ClipHighlight> highlights,
        ClipPlanOptions options)
    {
        ArgumentNullException.ThrowIfNull(demo);
        ArgumentNullException.ThrowIfNull(highlights);

        return Plan([new ClipDemoHighlights(demo, highlights)], options);
    }

    /// <summary>
    ///     Plans staged highlights across several demos — one reel, many matches. Coalescing scope is
    ///     (demo, player, round), so demos never merge into one another.
    /// </summary>
    /// <param name="demos">Per-demo facts + staged highlights.</param>
    /// <param name="options">Padding policy.</param>
    public static ClipPlan Plan(IReadOnlyList<ClipDemoHighlights> demos, ClipPlanOptions options)
    {
        ArgumentNullException.ThrowIfNull(demos);
        ArgumentNullException.ThrowIfNull(options);

        List<ClipWindows.Candidate> candidates = [];
        Dictionary<string, int> tickRateByDemo = new(StringComparer.OrdinalIgnoreCase);

        foreach (ClipDemoHighlights entry in demos)
        {
            ClipDemo demo = entry.Demo;
            int rate = demo.TickRate > 0 ? demo.TickRate : 64;
            tickRateByDemo[demo.DemoPath] = rate;

            foreach (ClipHighlight highlight in entry.Highlights)
            {
                int? roundStart = options.FloorAtRoundStart
                    ? ClipWindows.RoundStartFor(demo.Rounds, highlight.TickFrameClock)
                    : null;

                // tickOffset 0: the plan stays in the frame clock end to end. The CS2 conversion is
                // the emitting consumer's, applied exactly once at its own boundary.
                (long start, long end) = ClipWindows.Compute(
                    highlight.TickFrameClock, roundStart, rate,
                    options.LeadInSeconds, options.LeadOutSeconds, demo.TickCount, 0,
                    highlight.ClipStartTickFrameClock);

                candidates.Add(new ClipWindows.Candidate(
                    demo.DemoPath, highlight.SteamId64, highlight.PlayerNameRaw,
                    highlight.RoundNumber, start, end, highlight.Title));
            }
        }

        List<PlannedClip> clips = [];
        foreach (ClipWindows.Clip clip in ClipWindows.Coalesce(candidates))
        {
            clips.Add(new PlannedClip(
                clip.DemoPath, clip.SteamId64, clip.PlayerName, clip.RoundNumber,
                clip.StartTick, clip.EndTick,
                tickRateByDemo.GetValueOrDefault(clip.DemoPath, 64),
                clip.Titles));
        }

        return new ClipPlan(clips);
    }
}
