#region

using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Analysis.Graphs;
using CS2DemoKit.Analysis.Nodes;
using CS2DemoKit.Analysis.Profiles;
using CS2DemoKit.Parser;
using CS2DemoKit.Parser.GameEvents;

using CS2OpenSchema.Protos;

#endregion

using CS2DemoKit.TestSupport;

namespace CS2DemoKit.Analysis.Tests;

/// <summary>
///     <c>match_live</c>'s two activations, on hand-built frames with no demo file.
///     <c>round_active</c> and <c>round_number</c> are parented on this node, so its activation
///     bounds theirs. The second case pins the freeze-end arm as a no-op where <c>$match_start</c>
///     is recorded, which is the whole corpus that worked before it existed.
/// </summary>
[Category("Unit")]
public class MatchLiveActivationTests
{
    /// <summary>A demo whose recording began mid-match: freeze-ends, never a match start.</summary>
    [Test]
    public async Task FreezeEnd_WithoutMatchStart_ActivatesMatchLiveAndCountsRounds()
    {
        (bool live, int rounds, bool roundActive) = Evaluate(
            Frame(0, 1000, TestGameEvents.RoundFreezeEnd(0, 1000)),
            Frame(1, 2000, TestGameEvents.RoundFreezeEnd(1, 2000)),
            Frame(2, 3000, TestGameEvents.RoundFreezeEnd(2, 3000)));

        await Assert.That(live).IsTrue()
            .Because("a recording with no $match_start must still go live at the first round's freeze-end");
        await Assert.That(roundActive).IsTrue()
            .Because("round_active is parented on match_live, so it is dead whenever match_live is");
        await Assert.That(rounds).IsEqualTo(3)
            .Because("round_number is parented on match_live and must count the freeze-end that activated it");
    }

    /// <summary>
    ///     The same three rounds behind a recorded <c>begin_new_match</c>. Identical counts: the
    ///     activation is idempotent, so adding it changed nothing on the demos that carry the marker.
    /// </summary>
    [Test]
    public async Task MatchStart_BeforeFreezeEnd_CountsTheSameRounds()
    {
        (bool live, int rounds, bool roundActive) = Evaluate(
            Frame(0, 10, BeginNewMatch(0, 10)),
            Frame(1, 1000, TestGameEvents.RoundFreezeEnd(1, 1000)),
            Frame(2, 2000, TestGameEvents.RoundFreezeEnd(2, 2000)),
            Frame(3, 3000, TestGameEvents.RoundFreezeEnd(3, 3000)));

        await Assert.That(live).IsTrue();
        await Assert.That(roundActive).IsTrue();
        await Assert.That(rounds).IsEqualTo(3)
            .Because("the freeze-end activation must not double-count a round on a demo with a match start");
    }

    /// <summary>Warmup deaths stay out: nothing is live before the first freeze-end.</summary>
    [Test]
    public async Task BeforeTheFirstFreezeEnd_MatchLiveIsStillFalse()
    {
        (bool live, int rounds, _) = Evaluate(Frame(0, 500, TestGameEvents.PlayerTeam(3, 3, frameNumber: 0, serverTick: 500)));

        await Assert.That(live).IsFalse()
            .Because("the freeze-end bound is what keeps pre-match deaths out of a death count");
        await Assert.That(rounds).IsEqualTo(0);
    }

    private static GameEvent BeginNewMatch(int frameNumber, int serverTick) =>
        new("begin_new_match", -1, frameNumber, serverTick, serverTick, new BeginNewMatchEvent());

    private static (bool Live, int Rounds, bool RoundActive) Evaluate(params DemoFrame[] frames)
    {
        BuildResult build = DemoAnalysis.Build(new AnalysisTarget(64, new Cs2GotvProfile()), []);
        new StateGraphEvaluator(build.Graph, null, build.PlayerContextIndex, build.EntityScanner).Evaluate(frames);

        return (Node<BoolNode>(build, "MatchLive").IsActive,
            Node<GenericValueNode<int>>(build, "RoundNumber").Value,
            Node<BoolNode>(build, "RoundActive").IsActive);
    }

    private static DemoFrame Frame(int frameNumber, int serverTick, params GameEvent[] fires) => new()
    {
        CommandKind = EDemoCommands.DemPacket,
        FrameNumber = frameNumber,
        ServerTick = serverTick,
        RawStart = 0,
        RawLength = 1,
        HeaderLength = 1,
        IsCompressed = false,
        MessageList = [.. fires.Select(GameEventMessage.ForSynthesizedEvent)]
    };

    private static T Node<T>(BuildResult build, string name) where T : StateNode =>
        build.Nodes.OfType<T>().Single(n => n.Name == name);
}
