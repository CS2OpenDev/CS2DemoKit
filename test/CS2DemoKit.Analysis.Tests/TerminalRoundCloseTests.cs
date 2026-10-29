#region

using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Analysis.Graphs;
using CS2DemoKit.Analysis.Nodes;
using CS2DemoKit.Analysis.Profiles;
using CS2DemoKit.Analysis.RulesetsV2.Model;
using CS2DemoKit.Analysis.Yaml;
using CS2DemoKit.Parser;
using CS2DemoKit.Parser.GameEvents;

using CS2OpenSchema.Protos;

#endregion

using CS2DemoKit.TestSupport;

namespace CS2DemoKit.Analysis.Tests;

/// <summary>
///     The terminal round close, on hand-built frames with no demo file. A recording whose last round
///     has no <c>$round_end</c> marker left that round uncounted: its win went to nobody and every
///     <c>per: match</c> compute stayed a round behind, because a non-live compute is only recomputed
///     by a round-end edge. The close is synthesized at end of stream, and the second case is the one
///     that matters most: the v2 <c>count:</c> planner emits an unguarded Increment edge per concrete
///     event, so nothing but the gate stops a round a real marker closed being counted twice.
/// </summary>
[Category("Unit")]
public class TerminalRoundCloseTests
{
    private const string CounterYaml =
        """
        ruleset: trc
        for: match
        stats:
          ends:
            count: round_ended
            per: match
          rounds:
            compute: "round.number"
            per: match
        """;

    /// <summary>A round left open, with the server already at the intermission: closed exactly once.</summary>
    [Test]
    public async Task OpenRoundPastPlay_IsClosedOnce()
    {
        (int ends, double rounds, bool active) = Evaluate(
            Frame(0, 10, MatchStart(0, 10)),
            Frame(1, 1000, TestGameEvents.RoundFreezeEnd(1, 1000)),
            Frame(2, 2000, Intermission(2, 2000)));

        await Assert.That(ends).IsEqualTo(1)
            .Because("the last round has no marker of its own, so the synthesized close is its only end");
        await Assert.That(active).IsFalse()
            .Because("the close must deactivate round_active like any round end");
        await Assert.That(rounds).IsEqualTo(1)
            .Because("a per: match compute is only recomputed by a round-end edge, so the close is what settles it");
    }

    /// <summary>
    ///     The double-count guard. A real marker already closed the round, so the close must not fire
    ///     a second one: <c>round_active</c> is false and that is the whole of the exclusion.
    /// </summary>
    [Test]
    public async Task RoundAlreadyClosedByAMarker_IsNotClosedTwice()
    {
        (int ends, double rounds, bool active) = Evaluate(
            Frame(0, 10, MatchStart(0, 10)),
            Frame(1, 1000, TestGameEvents.RoundFreezeEnd(1, 1000)),
            Frame(2, 1500, TestGameEvents.RoundOfficiallyEnded(2, 1500)),
            Frame(3, 2000, Intermission(3, 2000)));

        await Assert.That(ends).IsEqualTo(1)
            .Because("round_active is false once a marker closed the round, which is what keeps the count at one");
        await Assert.That(active).IsFalse();
        await Assert.That(rounds).IsEqualTo(1);
    }

    /// <summary>
    ///     A recording cut mid-round. The server never left play, so the round is not closed: closing
    ///     it would manufacture a round out of a truncated file.
    /// </summary>
    [Test]
    public async Task OpenRoundStillInPlay_IsLeftOpen()
    {
        (int ends, _, bool active) = Evaluate(
            Frame(0, 10, MatchStart(0, 10)),
            Frame(1, 1000, TestGameEvents.RoundFreezeEnd(1, 1000)),
            Frame(2, 2000, TestGameEvents.RoundFreezeEnd(2, 2000)));

        await Assert.That(ends).IsEqualTo(0)
            .Because("a demo cut mid-round must not have its final round finalised");
        await Assert.That(active).IsTrue();
    }

    /// <summary>A pre-match period alone is not a round, so there is nothing to close.</summary>
    [Test]
    public async Task NoRoundEverStarted_IsNotClosed()
    {
        (int ends, _, bool active) = Evaluate(Frame(0, 500, Intermission(0, 500)));

        await Assert.That(ends).IsEqualTo(0);
        await Assert.That(active).IsFalse();
    }

    // match_live opens on $match_start, and every demo on this profile records one. Omitting it
    // left round_active dead, so the close had nothing to act on and the scenario was not the one
    // the test names.
    private static GameEvent MatchStart(int frameNumber, int serverTick) =>
        new("begin_new_match", -1, frameNumber, serverTick, serverTick, new BeginNewMatchEvent());

    private static GameEvent Intermission(int frameNumber, int serverTick) =>
        new("cs_intermission", -1, frameNumber, serverTick, serverTick, new CsIntermissionEvent());

    private static (int Ends, double Rounds, bool RoundActive) Evaluate(params DemoFrame[] frames)
    {
        RuleConfigLoadResult load = YamlConfigLoader.LoadDocuments([("trc.rules.yaml", CounterYaml)]);
        BuildResult build = DemoAnalysis.Build(new AnalysisTarget(64, new Cs2GotvProfile()), load.Rulesets);
        new StateGraphEvaluator(build.Graph, null, build.PlayerContextIndex, build.EntityScanner)
        {
            RoundNumberNode = build.GameNodesByRuleId?.GetValueOrDefault("round_number"),
            RoundActiveNode = build.GameNodesByRuleId?.GetValueOrDefault("round_active"),
            GameplayPhaseNode = build.GameNodesByRuleId?.GetValueOrDefault("gameplay_phase")
        }.Evaluate(frames);

        return ((int)(Node(build, "trc.ends").GetNumericValue() ?? -1),
            Node(build, "trc.rounds").GetNumericValue() ?? -1,
            Node(build, "round_active").IsActive);
    }

    private static DemoFrame Frame(int frameNumber, int serverTick, params GameEvent[] fires) => new()
    {
        CommandKind = EDemoCommands.DemPacket,
        FrameNumber = frameNumber,
        ServerTick = serverTick,
        GameTick = serverTick,
        RawStart = 0,
        RawLength = 1,
        HeaderLength = 1,
        IsCompressed = false,
        MessageList = [.. fires.Select(GameEventMessage.ForSynthesizedEvent)]
    };

    private static StateNode Node(BuildResult build, string ruleId) =>
        build.GameNodesByRuleId?.GetValueOrDefault(ruleId)
        ?? throw new InvalidOperationException(
            $"no node for '{ruleId}'; have: {string.Join(", ", build.GameNodesByRuleId?.Keys ?? [])}");
}
