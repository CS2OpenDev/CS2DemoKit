#region

using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Analysis.Clips;
using CS2DemoKit.Parser;
using CS2DemoKit.Parser.GameEvents;

#endregion

using CS2DemoKit.TestSupport;

namespace CS2DemoKit.Analysis.Tests;

/// <summary>
///     The packaged clip pipeline: the frame-clock round deriver, the
///     surfacing policy, the window math, and the planner that composes them. Every subtle rule the
///     extraction had to preserve verbatim is pinned here — they are the rules whose violation
///     produces clips that are quietly a second or a round wrong rather than obviously broken.
/// </summary>
[Category("Unit")]
public class ClipPipelineTests
{
    private const int Rate = 64;

    // ── Round derivation (frame clock) ────────────────────────────────────────

    private static GameEvent FreezeEnd(int serverTick, int gameTick) =>
        TestGameEvents.RoundFreezeEnd(serverTick: serverTick, gameTick: gameTick, eventId: 0);

    private static GameEvent OfficiallyEnded(int serverTick, int gameTick) =>
        TestGameEvents.RoundOfficiallyEnded(serverTick: serverTick, gameTick: gameTick, eventId: 0);

    [Test]
    public async Task ClipRounds_TakesGameTick_NotAbsoluteServerTick()
    {
        // The absolute clock runs ahead of the frame clock by ParsedDemo.ServerStartTick. A deriver
        // that read ServerTick would floor every clip lead-in ~ServerStartTick too late — the clip
        // would start after its own highlight on any demo whose recording didn't begin at tick 0.
        const int serverStartTick = 100_000;
        List<GameEvent> events =
        [
            FreezeEnd(serverStartTick + 500, 500),
            OfficiallyEnded(serverStartTick + 900, 900),
            FreezeEnd(serverStartTick + 1500, 1500),
            OfficiallyEnded(serverStartTick + 1900, 1900)
        ];

        IReadOnlyList<ClipRound> rounds = ClipRounds.Derive(events);

        int[] expectedStarts = [500, 1500];
        int[] expectedNumbers = [1, 2];
        await Assert.That(rounds.Select(r => r.StartTickFrameClock)).IsEquivalentTo(expectedStarts);
        await Assert.That(rounds.Select(r => r.Number)).IsEquivalentTo(expectedNumbers);
    }

    [Test]
    public async Task ClipRounds_OpensARoundPerFreezeEnd_IncludingAnUnclosedFinalRound()
    {
        // Numbering is per freeze-end, unconditionally — NOT the close-driven numbering of the
        // absolute-clock DemoAnalyzer walk. Round count is persisted and shown, so this must not drift.
        List<GameEvent> events =
        [
            FreezeEnd(500, 500),
            OfficiallyEnded(900, 900),
            FreezeEnd(1500, 1500), // no close: demo cut short mid-round
            FreezeEnd(2500, 2500)  // and a restart with no close either
        ];

        IReadOnlyList<ClipRound> rounds = ClipRounds.Derive(events);

        await Assert.That(rounds.Count).IsEqualTo(3);
        await Assert.That(rounds[2]).IsEqualTo(new ClipRound(3, 2500));
    }

    [Test]
    public async Task ClipRounds_NoFreezeEnds_IsEmpty()
    {
        // CS2 never emits round_start; a deriver matching that name yields this on EVERY demo, which
        // silently disables the round floor instead of failing.
        await Assert.That(ClipRounds.Derive([OfficiallyEnded(900, 900)])).IsEmpty();
    }

    // ── Surfacing policy ──────────────────────────────────────────────────────

    private static HighlightFired Fired(
        string id, int slot, int round, int score, HighlightKind kind = HighlightKind.Highlight,
        string? group = null, int tick = 1000) =>
        new("rs", id, 0, tick, slot, "player", round, id, score, kind, group);

    [Test]
    public async Task Surface_DropsHiddenFirings()
    {
        // Hidden firings are counting-only (their .count feeds a rating stat); they are never a moment.
        List<HighlightFired> fired =
        [
            Fired("kast", 1, 3, 50, HighlightKind.Hidden),
            Fired("ace", 1, 3, 90),
            Fired("whiff", 2, 3, 40, HighlightKind.Lowlight)
        ];

        IReadOnlyList<HighlightFired> surfaced = HighlightSurfacing.Surface(fired);

        string[] expected = ["ace", "whiff"];
        await Assert.That(surfaced.Select(e => e.HighlightId)).IsEquivalentTo(expected);
    }

    [Test]
    public async Task Surface_CollapsesAGroupFamilyToItsTopTierPerPlayerRound()
    {
        List<HighlightFired> fired =
        [
            Fired("triple", 1, 3, 60, group: "multikill"),
            Fired("quad", 1, 3, 75, group: "multikill"),
            Fired("ace", 1, 3, 90, group: "multikill"),
            Fired("triple", 2, 3, 60, group: "multikill"), // other player: untouched
            Fired("triple", 1, 4, 60, group: "multikill"), // other round: untouched
            Fired("clutch", 1, 3, 55)                      // ungrouped: always passes
        ];

        IReadOnlyList<HighlightFired> surfaced = HighlightSurfacing.Surface(fired);

        string[] expected = ["ace", "clutch"];
        await Assert.That(surfaced.Where(e => e.PlayerSlot == 1 && e.RoundNumber == 3)
            .Select(e => e.HighlightId)).IsEquivalentTo(expected);
        await Assert.That(surfaced.Count).IsEqualTo(4);
    }

    [Test]
    public async Task Surface_KeepsSameScoreFiringsOfOneGroup()
    {
        // Strictly LOWER tiers are superseded. Two rapid doubles in one round are two moments.
        List<HighlightFired> fired =
        [
            Fired("double", 1, 3, 60, group: "multikill", tick: 1000),
            Fired("double", 1, 3, 60, group: "multikill", tick: 4000)
        ];

        await Assert.That(HighlightSurfacing.Surface(fired).Count).IsEqualTo(2);
    }

    [Test]
    public async Task Surface_IsIdempotent()
    {
        // A consumer that surfaces at store time and a planner that surfaces again must agree.
        List<HighlightFired> fired =
        [
            Fired("kast", 1, 3, 50, HighlightKind.Hidden),
            Fired("triple", 1, 3, 60, group: "multikill"),
            Fired("ace", 1, 3, 90, group: "multikill")
        ];

        IReadOnlyList<HighlightFired> once = HighlightSurfacing.Surface(fired);

        await Assert.That(HighlightSurfacing.Surface(once)).IsEquivalentTo(once);
    }

    // ── Window math ───────────────────────────────────────────────────────────

    [Test]
    public async Task Compute_FloorsBeforeOffset_NotAfter()
    {
        // THE ordering rule: every clamp runs in the frame clock and the offset is added last. Were
        // the demo-start floor applied in CS2-tick space (max(0, x + offset)), a non-zero offset would
        // skew exactly the case the shim exists for.
        const int offset = 500;

        (long start, _) = ClipWindows.Compute(100, null, Rate, 15, 5, 100_000, offset);

        // frame clock: max(0, 100 − 960) = 0 → 0 + 500. NOT max(0, 100 − 960 + 500) = 0.
        await Assert.That(start).IsEqualTo(offset);
    }

    [Test]
    public async Task Compute_RoundStartFloorsTheLeadIn()
    {
        (long start, _) = ClipWindows.Compute(5000, 4800, Rate, 15, 5, 100_000);

        await Assert.That(start).IsEqualTo(4800); // 5000 − 960 = 4040 would cross into the prior round
    }

    [Test]
    public async Task Compute_ClipStartReachesBackEarlierOnly_AndStaysInsideTheRound()
    {
        // Reach-back: the first kill of a 4K precedes the lead-in → start there.
        (long reached, _) = ClipWindows.Compute(6000, null, Rate, 15, 5, 100_000, 0, 4600);
        await Assert.That(reached).IsEqualTo(4600);

        // …but only EARLIER. A clipStart inside the lead-in must never shrink the window.
        (long leadInWins, _) = ClipWindows.Compute(6000, null, Rate, 15, 5, 100_000, 0, 5500);
        await Assert.That(leadInWins).IsEqualTo(6000 - 960);

        // …and the round floor still bounds it: reach-back applies BEFORE the floor.
        (long floored, _) = ClipWindows.Compute(6000, 4800, Rate, 15, 5, 100_000, 0, 4600);
        await Assert.That(floored).IsEqualTo(4800);
    }

    [Test]
    public async Task Compute_ClampsTheLeadOutAtDemoEnd_AndNeverEmitsAnEmptyWindow()
    {
        (_, long end) = ClipWindows.Compute(99_900, null, Rate, 15, 5, 100_000);
        await Assert.That(end).IsEqualTo(100_000);

        // Degenerate demo (tickCount 0, no padding): a ≥1-tick window, never start == end.
        (long dStart, long dEnd) = ClipWindows.Compute(0, null, Rate, 0, 0, 0);
        await Assert.That(dEnd).IsEqualTo(dStart + 1);
    }

    [Test]
    public async Task RoundStartFor_TakesTheLatestRoundAtOrBeforeTheEvent()
    {
        List<ClipRound> rounds = [new(1, 1000), new(2, 8000), new(3, 16_000)];

        await Assert.That(ClipWindows.RoundStartFor(rounds, 9000)).IsEqualTo(8000);
        await Assert.That(ClipWindows.RoundStartFor(rounds, 16_000)).IsEqualTo(16_000); // inclusive
        await Assert.That(ClipWindows.RoundStartFor(rounds, 500)).IsNull();             // warmup firing
    }

    // ── Coalescing ────────────────────────────────────────────────────────────

    private static ClipWindows.Candidate Candidate(
        string demo, string steam, int round, long start, long end, string title) =>
        new(demo, steam, "raw", round, start, end, title);

    [Test]
    public async Task Coalesce_MergesOverlappingAndTouchingWindowsWithinOnePlayerRound()
    {
        List<ClipWindows.Clip> clips = ClipWindows.Coalesce(
        [
            Candidate("a.dem", "1", 1, 100, 200, "first"),
            Candidate("a.dem", "1", 1, 150, 260, "second"),  // overlaps → merges
            Candidate("a.dem", "1", 1, 260, 300, "third"),   // TOUCHES the merged end → merges
            Candidate("a.dem", "1", 1, 900, 950, "later"),   // disjoint → its own clip
            Candidate("a.dem", "2", 1, 150, 260, "other player"),
            Candidate("a.dem", "1", 2, 150, 260, "other round")
        ]);

        ClipWindows.Clip merged = clips.Single(c => c.Titles.Count == 3);
        await Assert.That(merged.StartTick).IsEqualTo(100);
        await Assert.That(merged.EndTick).IsEqualTo(300);
        string[] expectedTitles = ["first", "second", "third"];
        await Assert.That(merged.Titles).IsEquivalentTo(expectedTitles);
        await Assert.That(clips.Count).IsEqualTo(4); // merged + later + other player + other round
    }

    [Test]
    public async Task Coalesce_IsOrderIndependentAndCaseInsensitiveOnThePath()
    {
        ClipWindows.Candidate a = Candidate("A.dem", "1", 1, 100, 200, "x");
        ClipWindows.Candidate b = Candidate("a.dem", "1", 1, 200, 300, "y");

        List<ClipWindows.Clip> forward = ClipWindows.Coalesce([a, b]);
        List<ClipWindows.Clip> reversed = ClipWindows.Coalesce([b, a]);

        // One clip either way: a casing-variant path must not split one demo's candidates.
        await Assert.That(forward.Count).IsEqualTo(1);
        await Assert.That(forward[0].StartTick).IsEqualTo(100);
        await Assert.That(forward[0].EndTick).IsEqualTo(300);
        await Assert.That(reversed[0].StartTick).IsEqualTo(forward[0].StartTick);
        await Assert.That(reversed[0].EndTick).IsEqualTo(forward[0].EndTick);
    }

    [Test]
    public async Task Coalesce_IsTranslationInvariant()
    {
        // Why the offset can safely move to emission: shifting every candidate by a constant cannot
        // change which ones merge.
        const int offset = 5000;
        List<ClipWindows.Candidate> baseline =
        [
            Candidate("a.dem", "1", 1, 100, 200, "x"),
            Candidate("a.dem", "1", 1, 150, 260, "y"),
            Candidate("a.dem", "1", 1, 900, 950, "z")
        ];

        List<ClipWindows.Clip> plain = ClipWindows.Coalesce(baseline);
        List<ClipWindows.Clip> shifted = ClipWindows.Coalesce(
            baseline.Select(c => c with { StartTick = c.StartTick + offset, EndTick = c.EndTick + offset }));

        await Assert.That(shifted.Select(c => c.StartTick - offset))
            .IsEquivalentTo(plain.Select(c => c.StartTick).ToArray());
        await Assert.That(shifted.Select(c => c.Titles.Count))
            .IsEquivalentTo(plain.Select(c => c.Titles.Count).ToArray());
    }

    // ── Planner ───────────────────────────────────────────────────────────────

    [Test]
    public async Task Plan_SurfacesFloorsAndCoalesces_InTheFrameClock()
    {
        ClipDemo demo = new("a.dem", Rate, 100_000, [new ClipRound(1, 4800)]);
        List<ClipHighlight> staged =
        [
            new(5000, 1, "76561198000000001", "s1mple", "double kill"),
            new(5100, 1, "76561198000000001", "s1mple", "trade"),          // overlaps → merges
            new(60_000, 5, "76561198000000002", "ZywOo", "clutch")
        ];

        ClipPlan plan = ClipPlanner.Plan(demo, staged, new ClipPlanOptions(15, 5));

        await Assert.That(plan.Clips.Count).IsEqualTo(2);
        PlannedClip first = plan.Clips[0];
        await Assert.That(first.StartTickFrameClock).IsEqualTo(4800); // round floor, no offset applied
        string[] expectedTitles = ["double kill", "trade"];
        await Assert.That(first.Titles).IsEquivalentTo(expectedTitles);
        await Assert.That(first.TickRate).IsEqualTo(Rate);
        await Assert.That(plan.Clips[1].PlayerNameRaw).IsEqualTo("ZywOo");
    }

    [Test]
    public async Task Plan_WithoutTheRoundFloor_UsesTheFullLeadIn()
    {
        ClipDemo demo = new("a.dem", Rate, 100_000, [new ClipRound(1, 4800)]);
        List<ClipHighlight> staged = [new(5000, 1, "1", "p", "t")];

        ClipPlan plan = ClipPlanner.Plan(demo, staged, new ClipPlanOptions(15, 5, false));

        await Assert.That(plan.Clips[0].StartTickFrameClock).IsEqualTo(5000 - 960);
    }

    // The one-call overload — the reference consumer's whole clip path (parse → analyse → plan).
    // Its roster attribution is the only NEW code in the extraction, so pin it: the roster wins over
    // HighlightFired.PlayerName (a legacy/placeholder firing name must not beat the freshly parsed
    // roster), an unknown slot degrades to the fired name with an empty SteamID rather than throwing,
    // and Hidden firings never reach the plan at all.
    private static ParsedDemo Demo(IReadOnlyDictionary<int, PlayerInfo> players, params GameEvent[] events) =>
        new([], events, players, null, "de_anytown", 100_000, 1f / 64f, "test", "test", "csgo",
            0, 0, 0, "valve_demo_2", "", "", DemoProfile.Unknown);

    [Test]
    public async Task Plan_FromParsedDemo_SurfacesAttributesFromTheRosterAndFloorsAtTheDerivedRound()
    {
        Dictionary<int, PlayerInfo> players = new()
        {
            [0] = new PlayerInfo(0, "s1mple", 76561198000000001UL, 0, 2, false)
        };
        ParsedDemo demo = Demo(players, FreezeEnd(104_800, 4800));
        List<HighlightFired> fired =
        [
            new("rs", "kast", 0, 5000, 0, "stale name", 1, "counted", 50, HighlightKind.Hidden),
            new("rs", "ace", 0, 5000, 0, "stale name", 1, "ace", 90, HighlightKind.Highlight)
        ];

        ClipPlan plan = ClipPlanner.Plan(demo, "/d/a.dem", fired, new ClipPlanOptions(15, 5));

        await Assert.That(plan.Clips.Count).IsEqualTo(1).Because("the Hidden firing is never a clip");
        PlannedClip clip = plan.Clips[0];
        await Assert.That(clip.PlayerNameRaw).IsEqualTo("s1mple");
        await Assert.That(clip.SteamId64).IsEqualTo("76561198000000001");
        // Round floor came from the demo's own freeze-end, read in the FRAME clock (4800), not the
        // absolute 104_800 — which would have floored the clip past its own highlight.
        await Assert.That(clip.StartTickFrameClock).IsEqualTo(4800);
        await Assert.That(clip.TickRate).IsEqualTo(Rate);
    }

    [Test]
    public async Task Plan_FromParsedDemo_UnknownSlot_FallsBackToTheFiringName()
    {
        ParsedDemo demo = Demo(new Dictionary<int, PlayerInfo>());
        List<HighlightFired> fired =
        [
            new("rs", "ace", 0, 5000, 7, "disconnected guy", 1, "ace", 90, HighlightKind.Highlight)
        ];

        ClipPlan plan = ClipPlanner.Plan(demo, "/d/a.dem", fired, new ClipPlanOptions(15, 5));

        await Assert.That(plan.Clips[0].PlayerNameRaw).IsEqualTo("disconnected guy");
        await Assert.That(plan.Clips[0].SteamId64).IsEqualTo("");
    }

    [Test]
    public async Task Plan_KeepsDemosApart()
    {
        // Same player, same round number, same ticks — two demos must never merge into one clip.
        ClipDemoHighlights a = new(
            new ClipDemo("a.dem", Rate, 100_000, []), [new ClipHighlight(5000, 1, "1", "p", "a")]);
        ClipDemoHighlights b = new(
            new ClipDemo("b.dem", Rate, 100_000, []), [new ClipHighlight(5000, 1, "1", "p", "b")]);

        ClipPlan plan = ClipPlanner.Plan([a, b], new ClipPlanOptions(15, 5));

        await Assert.That(plan.Clips.Count).IsEqualTo(2);
        string[] expectedPaths = ["a.dem", "b.dem"];
        await Assert.That(plan.Clips.Select(c => c.DemoPath)).IsEquivalentTo(expectedPaths);
    }
}
