#region

using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Analysis.Catalog;
using CS2DemoKit.Analysis.Nodes;
using CS2DemoKit.Analysis.RulesetsV2.Compile;
using CS2DemoKit.Analysis.RulesetsV2.Model;
using CS2DemoKit.Analysis.RulesetsV2.Resolve;
using CS2DemoKit.Analysis.Yaml;
using CS2DemoKit.Parser;
using CS2DemoKit.Parser.GameEvents;

#endregion

using CS2DemoKit.TestSupport;

namespace CS2DemoKit.Analysis.Tests.RulesV2;

/// <summary>
///     Opt-in LIVE compute battery. Two halves:
///     <list type="bullet">
///         <item>
///             <b>Evaluator semantics</b> (hand-built graphs, no demo): a live compute reading a
///             rising-edge counter recomputes against the POST-write value (settle ordering); the
///             duplicate-fire guard + hard cap bound it to one recompute per message (Problems 2/3);
///             a live value feeding a logic node drives that node in the SAME message and a genuine
///             feedback loop converges (fixpoint); and a graph with ZERO live computes still evaluates
///             (the additive gate).
///         </item>
///         <item>
///             <b>Identity</b> (resolver + hasher): a live and a non-live compute over the same
///             formula hash apart (cadence is identity-bearing), while two live twins share; and the
///             mapper/resolver surface the <c>live:</c> flag.
///         </item>
///     </list>
/// </summary>
[Category("Unit")]
public class LiveComputeTests
{
    // ── Identity (resolver + hasher) ──────────────────────────────────────────

    private const string CadenceYaml = """
                                       ruleset: cadence_probe
                                       for: each_player
                                       stats:
                                         base:
                                           count: kill
                                           per: round
                                         live_ratio:
                                           compute: { value: base, live: true }
                                           per: round
                                         roundend_ratio:
                                           compute: base
                                           per: round
                                         live_ratio_twin:
                                           compute: { value: base, live: true }
                                           per: round
                                       """;

    private static DemoFrame Frame(params NetMessage[] msgs) => new()
    {
        Command = "DEM_Packet",
        FrameNumber = 0,
        ServerTick = 0,
        RawStart = 0,
        RawLength = 1,
        HeaderLength = 1,
        IsCompressed = false,
        MessageList = [.. msgs]
    };

    /// <summary>A player_team message whose Team field carries the value to write.</summary>
    private static GameEventMessage WriteX(int team) => GameEventMessage.ForSynthesizedEvent(
        TestGameEvents.PlayerTeam(-1, (byte)team));

    // ── Evaluator semantics ───────────────────────────────────────────────────

    /// <summary>
    ///     The verifier's motivating case: a live compute reads a RISING-EDGE counter. The counter is
    ///     written by a rising-edge action DURING the logic drain; the live compute must recompute
    ///     against its post-write value (10 × 1 = 10), not the stale pre-write 0. There is no round_end
    ///     event in the frames, so a round-end compute would stay 0 — the non-zero value proves live
    ///     cadence, and proves the recompute is ordered after the counter write.
    /// </summary>
    [Test]
    public async Task LiveCompute_ReadsRisingEdgeCounter_SeesPostWriteValue()
    {
        GenericValueNode<int> x = new("x");
        GenericValueNode<int> counter = new("kast_counter");
        ComputedStatNode compute = new("live_metric", null, () => counter.Value * 10.0);

        StateGraph graph = new();
        graph.AddEdge(new OnGameEventSetValue<PlayerTeamEvent, int>(graph.Root, x, e => e.Of<PlayerTeamEvent>().Team));
        ConjunctionNode trigger = new("counter_trigger", ConditionalEdge.From(x, v => v == 1, "== 1"));
        graph.AddConjunction(trigger);
        graph.AddRisingEdgeAction(trigger, () => counter.SetValue(counter.Value + 1), counter);
        graph.AddLiveCompute(compute, [counter]);

        StateGraphEvaluator evaluator = new(graph);
        evaluator.Evaluate([Frame(WriteX(1))]);

        await Assert.That(compute.Value).IsEqualTo(10.0)
            .Because("the live compute must observe the counter's POST-rising-edge value (1), not the "
                     + "stale pre-write 0 — no round_end fired, so a round-end compute would stay 0");
        await Assert.That(evaluator.LiveComputeRecomputeCounts[compute]).IsEqualTo(1);
    }

    /// <summary>
    ///     Live cadence tracks the counter across messages: after each increment the compute's value
    ///     follows, and it recomputes exactly once per message that changed its read.
    /// </summary>
    [Test]
    public async Task LiveCompute_TracksCounter_AcrossMessages()
    {
        GenericValueNode<int> x = new("x");
        GenericValueNode<int> counter = new("counter");
        ComputedStatNode compute = new("live_metric", null, () => counter.Value);

        StateGraph graph = new();
        graph.AddEdge(new OnGameEventSetValue<PlayerTeamEvent, int>(graph.Root, x, e => e.Of<PlayerTeamEvent>().Team));
        ConjunctionNode trigger = new("counter_trigger", ConditionalEdge.From(x, v => v == 1, "== 1"));
        graph.AddConjunction(trigger);
        graph.AddRisingEdgeAction(trigger, () => counter.SetValue(counter.Value + 1), counter);
        graph.AddLiveCompute(compute, [counter]);

        StateGraphEvaluator evaluator = new(graph);
        // Rise on msgs 0 and 2 (each +1); msg 1 is a fall (x=0) that does not touch the counter.
        evaluator.Evaluate([Frame(WriteX(1)), Frame(WriteX(0)), Frame(WriteX(1))]);

        await Assert.That(compute.Value).IsEqualTo(2.0)
            .Because("the counter incremented twice, and the live compute tracked it");
        await Assert.That(evaluator.LiveComputeRecomputeCounts[compute]).IsEqualTo(2)
            .Because("one recompute per message that dirtied a read (the two rises) — the fall did not");
    }

    /// <summary>
    ///     Duplicate-fire guard + hard frequency cap: two edges write the compute's read node in ONE
    ///     message, so its read is dirtied twice. The compute still recomputes exactly ONCE that
    ///     message (the per-message latch), yet lands the final value — the cap dedups within-tick
    ///     dirties into a single recompute after inputs settle.
    /// </summary>
    [Test]
    public async Task LiveCompute_MultipleDirtiesInOneMessage_RecomputesOnce()
    {
        GenericValueNode<int> counter = new("counter");
        ComputedStatNode compute = new("live_metric", null, () => counter.Value);

        StateGraph graph = new();
        // Two edges on the same dispatch key both write `counter` on one message (last write wins:
        // Team). The point is the compute is dirtied twice but recomputes once.
        graph.AddEdge(new OnGameEventSetValue<PlayerTeamEvent, int>(graph.Root, counter, e => e.Of<PlayerTeamEvent>().Team));
        graph.AddEdge(new OnGameEventSetValue<PlayerTeamEvent, int>(graph.Root, counter, e => e.Of<PlayerTeamEvent>().Team + 100));
        graph.AddLiveCompute(compute, [counter]);

        StateGraphEvaluator evaluator = new(graph);
        evaluator.Evaluate([Frame(WriteX(5))]);

        await Assert.That(evaluator.LiveComputeRecomputeCounts[compute]).IsEqualTo(1)
            .Because("two dirties of the read in one message dedup into a single recompute (the hard cap)");
        await Assert.That(compute.Value).IsEqualTo(105.0)
            .Because("the single recompute still reads the settled final value (the last edge's write)");
    }

    /// <summary>
    ///     Fixpoint: a live compute's value feeds a <c>when:</c>-style logic node, which must flip in
    ///     the SAME message (logic → live-compute → logic), AND a genuine feedback edge (the logic
    ///     node's rising-edge action re-increments the counter the compute reads) converges — the
    ///     per-message cap keeps the compute at one recompute so the settle terminates.
    /// </summary>
    [Test]
    public async Task LiveCompute_FeedsLogicNode_AndFeedbackConverges()
    {
        GenericValueNode<int> x = new("x");
        GenericValueNode<int> counter = new("counter");
        ComputedStatNode compute = new("live_metric", null, () => counter.Value);

        StateGraph graph = new();
        graph.AddEdge(new OnGameEventSetValue<PlayerTeamEvent, int>(graph.Root, x, e => e.Of<PlayerTeamEvent>().Team));
        ConjunctionNode counterTrigger = new("counter_trigger", ConditionalEdge.From(x, v => v == 1, "== 1"));
        // The logic node reads the LIVE compute, so it can only flip via the fixpoint interleave.
        ConjunctionNode reader = new("compute_reader", ConditionalEdge.From(compute, v => v >= 1, ">= 1"));
        graph.AddConjunction(counterTrigger);
        graph.AddConjunction(reader);
        graph.AddRisingEdgeAction(counterTrigger, () => counter.SetValue(counter.Value + 1), counter);
        // Genuine feedback: the reader's rise pushes the counter again — the compute is re-dirtied but
        // latched, so the settle must converge rather than loop.
        graph.AddRisingEdgeAction(reader, () => counter.SetValue(counter.Value + 1), counter);
        graph.AddLiveCompute(compute, [counter]);

        StateGraphEvaluator evaluator = new(graph);
        RuleChainTimeline timeline = evaluator.Evaluate([Frame(WriteX(1))]);

        await Assert.That(reader.IsActive).IsTrue()
            .Because("the live compute's value must drive its logic reader in the same message (fixpoint)");
        await Assert.That(timeline.CountFor("compute_reader")).IsEqualTo(1);
        await Assert.That(evaluator.LiveComputeRecomputeCounts[compute]).IsEqualTo(1)
            .Because("the per-message cap holds even under a feedback loop — the settle converged");
    }

    /// <summary>
    ///     The additive gate: a graph with ZERO live computes still evaluates its logic + rising-edge
    ///     actions exactly as before (this is the shape of the byte-identity guarantee at the unit
    ///     level — the live interleave stays dormant when nothing registered).
    /// </summary>
    [Test]
    public async Task NoLiveComputes_EvaluatesNormally()
    {
        GenericValueNode<int> x = new("x");
        GenericValueNode<int> counter = new("counter");

        StateGraph graph = new();
        graph.AddEdge(new OnGameEventSetValue<PlayerTeamEvent, int>(graph.Root, x, e => e.Of<PlayerTeamEvent>().Team));
        ConjunctionNode trigger = new("t", ConditionalEdge.From(x, v => v == 1, "== 1"));
        graph.AddConjunction(trigger);
        graph.AddRisingEdgeAction(trigger, () => counter.SetValue(counter.Value + 1), counter);

        StateGraphEvaluator evaluator = new(graph);
        RuleChainTimeline timeline = evaluator.Evaluate([Frame(WriteX(1))]);

        await Assert.That(counter.Value).IsEqualTo(1);
        await Assert.That(timeline.CountFor("t")).IsEqualTo(1);
        await Assert.That(evaluator.LiveComputeRecomputeCounts).IsEmpty()
            .Because("no live compute registered — the live machinery is inert");
    }

    /// <summary>
    ///     Cadence is identity-bearing (§6 row 8): a live compute and a non-live compute over the SAME
    ///     formula must hash apart — they are not behaviorally interchangeable, so the resolved-identity
    ///     dedup must not collapse them onto one node. Two live twins (differ only in id) still share.
    ///     Also pins the mapper/resolver surface: the mapping form sets <c>Live</c>, the scalar form
    ///     leaves it false, and both carry the same formula.
    /// </summary>
    [Test]
    public async Task LiveCadence_IsPartOfIdentity_NoFalseDedupWithRoundEnd()
    {
        CheckedRuleset rs = Compile(CadenceYaml);
        // The computes read sibling `base`, so its hash must be present before they hash (the planner's
        // dependency-ordered invariant, §6 row 6). Pre-seed it under both the bare and qualified keys.
        Dictionary<string, ReadOnlyMemory<byte>> map = new();
        MapStatHashSource source = new(map);
        CheckedStat baseStat = rs.Stats.Single(s => s.StatId == "base");
        byte[] baseHash = V2StatHasher.Hash(baseStat, source);
        map["base"] = baseHash;
        map["cadence_probe.base"] = baseHash;

        CheckedStat live = rs.Stats.Single(s => s.StatId == "live_ratio");
        CheckedStat roundEnd = rs.Stats.Single(s => s.StatId == "roundend_ratio");
        CheckedStat liveTwin = rs.Stats.Single(s => s.StatId == "live_ratio_twin");

        // Surface: the live: flag round-trips through mapper + resolver; the scalar form is round-end.
        await Assert.That(live.Live).IsTrue().Because("compute: { value, live: true } sets Live");
        await Assert.That(roundEnd.Live).IsFalse().Because("scalar compute: is round-end (Live == false)");
        await Assert.That(liveTwin.Live).IsTrue();

        // Hasher: cadence splits identity; same cadence + same formula still shares.
        await Assert.That(Hex(live, source)).IsNotEqualTo(Hex(roundEnd, source))
            .Because("a live and a non-live compute over the same formula must hash apart (row 8 cadence)");
        await Assert.That(Hex(live, source)).IsEqualTo(Hex(liveTwin, source))
            .Because("two live computes over the same formula (differ only in id) must still dedup");
    }

    private static string Hex(CheckedStat stat, MapStatHashSource source) =>
        Convert.ToHexStringLower(V2StatHasher.Hash(stat, source));

    private static CheckedRuleset Compile(string yaml)
    {
        RulesetDoc doc = RulesetDocumentLoader.Load(yaml, "test.rules.yaml").Doc
                         ?? throw new InvalidOperationException("test ruleset failed to map");
        CatalogScopeAdapter adapter = CatalogScopeAdapter.From(CatalogResource.Load());
        RulesetResolveResult resolved = CheckedRulesetDraft.Load(doc, adapter).Build(64.0, "Cs2GotvProfile");
        return resolved.Ruleset
               ?? throw new InvalidOperationException(
                   "test ruleset failed to resolve: " + string.Join("; ", resolved.Diagnostics));
    }
}
