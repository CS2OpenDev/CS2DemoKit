#region

using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Analysis.Events;
using CS2DemoKit.Analysis.Graphs;
using CS2DemoKit.Analysis.Nodes;
using CS2DemoKit.Analysis.Output;
using CS2DemoKit.Analysis.Plugins;
using CS2DemoKit.Parser;
using CS2DemoKit.Parser.GameEvents;
using CS2DemoKit.TestSupport;

#endregion

namespace CS2DemoKit.Analysis.Tests.RoundFacts;

/// <summary>
///     The synthesized <c>round_decided</c> (issue #54, piece 3): one per observed 0 → 2/3 step of
///     the game rules' round-win status, dispatched after the frame's own messages, carrying the
///     server's winner and reason, and latched so the round-end enrichment reports the verdict
///     instead of deriving one.
/// </summary>
[Category("Unit")]
public class RoundDecidedTests
{
    private static readonly string[] _latchProviders =
    [
        EntityChangeScanner.RoundWinStatusContext,
        EntityChangeScanner.RoundWinReasonContext,
        EntityChangeScanner.TotalRoundsPlayedContext
    ];

    /// <summary>A scanner tracking the three singletons round_decided is read from, in that order.</summary>
    private static EntityChangeScanner LatchScanner()
    {
        List<(IEntityValueProvider, StateNode)> tracked = [];
        foreach (string name in _latchProviders)
        {
            IEntityValueProvider provider = BuiltinProviderSpecs.CreateGameRulesProviders().Single(p => p.ContextName == name);
            tracked.Add((provider, new GenericValueNode<int>(name)));
        }

        return new EntityChangeScanner(new EntityStateLayer([]), tracked);
    }

    private static EntityFrameDigest Digest(int status, int reason, int played) =>
        new() { Singletons = [status, reason, played] };

    private static DemoFrame Frame(int tick, params NetMessage[] msgs) => new()
    {
        CommandKind = EDemoCommands.DemPacket,
        FrameNumber = tick,
        ServerTick = tick,
        RawStart = 0,
        RawLength = 1,
        HeaderLength = 1,
        IsCompressed = false,
        MessageList = [.. msgs]
    };

    [Test]
    public async Task Scanner_EmitsOnePostFrameEvent_PerObservedDecision()
    {
        EntityChangeScanner scanner = LatchScanner();
        scanner.InjectDigests(
        [
            Digest(0, 0, 0), Digest(3, 8, 1), Digest(3, 8, 1), Digest(0, 0, 1), Digest(0, 0, 1), Digest(2, 9, 2),
            Digest(0, 0, 2)
        ]);

        List<RoundDecidedEvent> decided = [];
        for (int frame = 0; frame < 7; frame++)
        {
            IReadOnlyList<NetMessage> preFrame = scanner.AdvanceAndPollAt(frame, 100 + frame);
            await Assert.That(preFrame.Any(m => m is GameEventMessage { DecodedEvent: RoundDecidedEvent })).IsFalse()
                .Because("round_decided rides the post-frame list, never the pre-frame one");
            decided.AddRange(scanner.TakePostFrameMessages().OfType<GameEventMessage>()
                .Select(m => m.DecodedEvent).OfType<RoundDecidedEvent>());
        }

        await Assert.That(decided.Count).IsEqualTo(2);
        await Assert.That(decided[0]).IsEqualTo(new RoundDecidedEvent(101, 101, 101, 3, 8, 1));
        await Assert.That(decided[1]).IsEqualTo(new RoundDecidedEvent(105, 105, 105, 2, 9, 2));
    }

    /// <summary>A demo that starts inside a decided round has no decision to report.</summary>
    [Test]
    public async Task Scanner_DoesNotEmit_OnAFirstReadThatIsAlreadyDecided()
    {
        EntityChangeScanner scanner = LatchScanner();
        scanner.InjectDigests([Digest(3, 8, 5), Digest(3, 8, 5), Digest(0, 0, 5), Digest(2, 1, 6)]);

        int count = 0;
        for (int frame = 0; frame < 4; frame++)
        {
            scanner.AdvanceAndPollAt(frame, frame);
            count += scanner.TakePostFrameMessages().Count;
        }

        await Assert.That(count).IsEqualTo(1);
    }

    /// <summary>
    ///     The round-deciding kill and the status change often arrive in one frame. The decision is
    ///     dispatched after the kill, so a rule reading alive counts on round_decided sees the kill.
    /// </summary>
    [Test]
    public async Task Evaluator_DispatchesRoundDecided_AfterASameFrameKill()
    {
        EntityChangeScanner scanner = LatchScanner();
        scanner.InjectDigests([Digest(0, 0, 0), Digest(3, 8, 1)]);

        StateGraph graph = new();
        List<string> log = [];
        graph.AddEdge(new RecordingEdge(graph.Root, typeof(PlayerDeathEvent), log, _ => "death"));
        graph.AddEdge(new RecordingEdge(graph.Root, typeof(RoundDecidedEvent), log,
            p => p is RoundDecidedEvent e ? $"decided {e.Winner}/{e.Reason}/{e.RoundsPlayed}" : "?"));

        new StateGraphEvaluator(graph, null, null, scanner).Evaluate(
        [
            Frame(10),
            Frame(11, GameEventMessage.ForSynthesizedEvent(TestGameEvents.PlayerDeath(1, 6, -1, "ak47")))
        ]);

        await Assert.That(string.Join(", ", log)).IsEqualTo("death, decided 3/8/1");
    }

    /// <summary>
    ///     On the sample: three decisions, the first in the warmup before the second
    ///     begin_new_match. Winners, reasons and frames as read off the game rules.
    /// </summary>
    [Test]
    [Category("Integration")]
    [NotInParallel]
    public async Task Sample_ThreeDecisions_WithTheServersWinnerAndReason()
    {
        ParsedDemo demo = RoundFactsTestSupport.Sample();
        BuildResult build = DemoAnalysis.Build(demo, RoundFactsTestSupport.Load(WinnerLists));
        List<string> log = [];
        build.Graph.AddEdge(new RecordingEdge(build.Graph.Root, typeof(RoundDecidedEvent), log,
            p => p is RoundDecidedEvent e ? $"{e.GameTick}:{e.Winner}/{e.Reason}" : "?"));
        AnalysisRun run = DemoAnalysis.Evaluate(demo, build);

        await Assert.That(string.Join(" ", log)).IsEqualTo("3636:3/8 13564:2/9 17469:3/8");

        // The round-end enrichment reports the latched verdict: its winners are round_decided's,
        // for the rounds after the restart both lists keep.
        MetricRow row = RoundFactsTestSupport.Table(run, "winners", demo).Rows.Single();
        string? atDecision = row.Values["Decided"]?.ToString();
        string? atClose = row.Values["Closed"]?.ToString();
        Console.WriteLine($"[round_decided] decided={atDecision} closed={atClose} reasons={row.Values["Reasons"]}");
        await Assert.That(atDecision).IsNotNull();
        await Assert.That(atDecision!.StartsWith(atClose ?? "<none>", StringComparison.Ordinal)).IsTrue();
        await Assert.That(row.Values["Reasons"]?.ToString()).IsEqualTo(row.Values["ReasonsAtClose"]?.ToString());
    }

    /// <summary>
    ///     With no win-status provider registered, nothing synthesizes round_decided and the round-end
    ///     winner falls back to its derivation, which agrees with the latch on the sample.
    /// </summary>
    [Test]
    [Category("Integration")]
    [NotInParallel]
    public async Task Sample_WithoutTheGameRulesProviders_FallsBackToDerivation()
    {
        ParsedDemo demo = RoundFactsTestSupport.Sample();
        EntityValueProviderRegistry freezeOnly = new();
        freezeOnly.Register(new FreezePeriodProvider());
        AnalysisOptions options = new() { EntityProviders = freezeOnly };

        AnalysisRun derived = RoundFactsTestSupport.Run(demo, options, WinnerLists);
        AnalysisRun latched = RoundFactsTestSupport.Run(demo, WinnerLists);

        MetricRow d = RoundFactsTestSupport.Table(derived, "winners", demo).Rows.Single();
        MetricRow l = RoundFactsTestSupport.Table(latched, "winners", demo).Rows.Single();
        await Assert.That(d.Values["Decided"]).IsNull();
        await Assert.That(d.Values["Closed"]?.ToString()).IsEqualTo(l.Values["Closed"]?.ToString());
        // Derived winners carry no reason.
        await Assert.That((d.Values["ReasonsAtClose"]?.ToString() ?? "").Split(',').All(r => r is "0" or "")).IsTrue();
    }

    private const string WinnerLists = """
        ruleset: winners
        for: match
        stats:
          decided:
            capture: event.Winner
            on: round_decided
            keep: list
            per: match
          reasons:
            capture: event.Reason
            on: round_decided
            keep: list
            per: match
          closed:
            capture: enrich.round.winner_side
            on: round_ended
            match: { has_winner: true }
            keep: list
            per: match
          reasons_at_close:
            capture: enrich.round.win_reason
            on: round_ended
            match: { has_winner: true }
            keep: list
            per: match
        show:
          tables:
            winners:
              per: match
              columns:
                - { stat: decided, label: Decided }
                - { stat: reasons, label: Reasons }
                - { stat: closed, label: Closed }
                - { stat: reasons_at_close, label: ReasonsAtClose }
        """;

    /// <summary>Records one line per dispatched payload of its message type, in dispatch order.</summary>
    private sealed class RecordingEdge(StateNode source, Type type, List<string> log, Func<object, string> describe)
        : StateEdge(source)
    {
        public override Type MessageType => type;

        public override bool TryApply(EvaluationContext context) => false;

        public override bool TryApplyDirect(object payload, EvaluationContext context)
        {
            log.Add(describe(payload));
            return false;
        }
    }
}
