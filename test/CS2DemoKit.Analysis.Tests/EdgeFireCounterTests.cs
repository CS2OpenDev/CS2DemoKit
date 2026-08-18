#region

using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Analysis.Building;
using CS2DemoKit.Analysis.Graphs;
using CS2DemoKit.Analysis.Registry;
using CS2DemoKit.Analysis.Tests.RulesV2;
using CS2DemoKit.Parser;
using CS2DemoKit.TestSupport;

#endregion

namespace CS2DemoKit.Analysis.Tests;

/// <summary>
///     Always-on fire counters (the trace tier).
///     Two counter families, both reset at evaluation start and live on every path
///     including the bare <c>Evaluate</c> (no snapshots) one:
///     <list type="number">
///         <item>
///             <see cref="StateEdge.FireCount" /> — incremented in the applied branch,
///             adjacent to the aggregate <c>edgesFired</c> counter.
///         </item>
///         <item>
///             <see cref="StateGraphEvaluator.RisingEdgeFireCounts" /> — per-trigger counts
///             of rising-edge action invocations (highlight <c>_chain_</c> counters are not
///             edge-driven, so <see cref="StateEdge.FireCount" /> alone would miss them).
///         </item>
///     </list>
///     These power the fire-count badges and never-fired lint; budget is &lt;1 ms per run and
///     zero eval-path allocation (bench-gated, not asserted here). Re-hosted on the built-in
///     context edges + a v2 ruleset fixture after the Rulesets v1 chain layer was removed.
/// </summary>
[Category("Unit")]
[NotInParallel]
public class EdgeFireCounterTests
{
    // v2 fixture: a per-round kill counter, a flag over it, and a highlight whose per-round
    // rising edge produces the `_chain_fp_kill_round` timeline chain — the rising-edge-counter
    // surface the removed v1 on_satisfied fixture used to provide.
    private const string FireProbeRuleset =
        """
        ruleset: fire_probe
        title: Fire probe
        summary: Fire-counter probe fixture.
        for: each_player
        stats:
          kills_round:
            count: kill
            per: round
          had_kill:
            flag:
              when: "kills_round > 0"
            per: round
        highlights:
          fp_kill_round:
            when: had_kill
            per: match
            title: "kill round for {player.name}"
        """;

    // The unconditional built-in gameplay_phase Set edge on round_freeze_end — a root-sourced
    // game-scoped trigger edge that applies on every matching event, so its FireCount is
    // deterministic per run (fires once per round in any demo).
    private static StateEdge ResolveFreezeEndPhaseEdge(BuildResult build)
    {
        StateNode phase = build.Nodes.First(n => n.Name == "GameplayPhase");
        GraphEdgeDescriptor desc = build.Edges.First(e =>
            ReferenceEquals(e.Destination, phase)
            && e.Label == "round_freeze_end"
            && e.ConditionLabel is null);
        return build.EdgeBacking![desc];
    }

    /// <summary>
    ///     Counters start at zero: fresh edges carry <c>FireCount == 0</c>. Pure structural
    ///     build (built-in contexts only) — no demo required.
    /// </summary>
    [Test]
    public async Task FireCounts_AreZero_BeforeEvaluation()
    {
        EventRegistry registry = EventRegistry.Build();
        RuleChainBuilder builder = new(registry);
        BuildResult build = builder.Build();

        await Assert.That(build.EdgeBacking).IsNotNull()
            .Because("the built-in context rules produce game-scoped trigger edges");
        foreach (StateEdge edge in build.EdgeBacking!.Values)
        {
            await Assert.That(edge.FireCount).IsEqualTo(0);
        }
    }

    /// <summary>
    ///     The plan's required known-corpus test: on the BARE <c>Evaluate</c> path (no snapshot
    ///     machinery — proving the counters are always-on), a firing edge's count is nonzero,
    ///     the highlight's rising-edge counts are nonzero, and their sum across materialized
    ///     players equals the timeline's event count for the highlight chain (every action
    ///     invoke is paired with one <see cref="RuleChainEvent" /> at the same site).
    ///     DEMO_PATH-gated.
    /// </summary>
    [Test]
    public async Task FireCounts_NonzeroOnFiringRules_OnBareEvaluatePath()
    {
        string path = DemoTestHelper.RequireDemo();
        ParsedDemo parsed = DemoTestHelper.GetOrParse(path);

        BuildResult build = V2KindGoldenSupport.CompileV2(parsed, FireProbeRuleset);

        StateGraphEvaluator evaluator = new(build.Graph, parsed, build.PlayerContextIndex, build.EntityScanner);
        RuleChainTimeline timeline = evaluator.Evaluate(parsed.Frames);

        StateEdge edge = ResolveFreezeEndPhaseEdge(build);
        await Assert.That(edge.FireCount).IsGreaterThan(0)
            .Because("every match has round_freeze_end events, so the built-in phase edge fired");

        int risingCount = evaluator.RisingEdgeFireCounts
            .Where(kv => kv.Key.Name == "_chain_fp_kill_round")
            .Sum(kv => kv.Value);
        await Assert.That(risingCount).IsGreaterThan(0)
            .Because("some player gets a kill in some round, so the highlight chain rises");

        int timelineCount = timeline.Events.Count(e => e.ChainName == "_chain_fp_kill_round");
        await Assert.That(risingCount).IsEqualTo(timelineCount)
            .Because("every rising-edge action invoke is paired with exactly one timeline event");
    }

    /// <summary>
    ///     Correctness oracle: on the snapshot path the always-on counter must agree exactly
    ///     with the pre-existing opt-in applied recorder
    ///     (<see cref="EvaluationResult.AppliedMessagesByEdge" />) — both instrument the same
    ///     applied branch, and drift between them would corrupt the badge layer. DEMO_PATH-gated.
    /// </summary>
    [Test]
    public async Task FireCounts_MatchAppliedRecorder_OnSnapshotPath()
    {
        string path = DemoTestHelper.RequireDemo();
        ParsedDemo parsed = DemoTestHelper.GetOrParse(path);

        EventRegistry registry = EventRegistry.Build();
        RuleChainBuilder builder = new(registry, parsed);
        BuildResult build = builder.Build();

        StateGraphEvaluator evaluator = new(build.Graph, parsed, build.PlayerContextIndex, build.EntityScanner);
        EvaluationResult result = evaluator.EvaluateWithSnapshots(parsed.Frames, build.Nodes);

        StateEdge edge = ResolveFreezeEndPhaseEdge(build);
        await Assert.That(result.AppliedMessagesByEdge!.TryGetValue(edge, out List<int>? hits)).IsTrue();
        await Assert.That(edge.FireCount).IsEqualTo(hits!.Count)
            .Because("the always-on counter and the opt-in recorder instrument the same branch");

        // Global agreement: every recorded edge's count matches its hit-list length.
        foreach ((StateEdge recorded, List<int> recordedHits) in result.AppliedMessagesByEdge!)
        {
            await Assert.That(recorded.FireCount).IsEqualTo(recordedHits.Count);
        }
    }

    /// <summary>
    ///     Counters reset at evaluation start, not accumulate across runs: a second
    ///     <c>Evaluate</c> on the same evaluator yields the same count for the root-sourced,
    ///     unconditional round_freeze_end phase edge (which fires identically each run) — not
    ///     double. DEMO_PATH-gated.
    /// </summary>
    [Test]
    public async Task FireCounts_ResetAtEvaluationStart()
    {
        string path = DemoTestHelper.RequireDemo();
        ParsedDemo parsed = DemoTestHelper.GetOrParse(path);

        EventRegistry registry = EventRegistry.Build();
        RuleChainBuilder builder = new(registry, parsed);
        BuildResult build = builder.Build();

        StateGraphEvaluator evaluator = new(build.Graph, parsed, build.PlayerContextIndex, build.EntityScanner);
        StateEdge edge = ResolveFreezeEndPhaseEdge(build);

        evaluator.Evaluate(parsed.Frames);
        int firstEdgeCount = edge.FireCount;
        await Assert.That(firstEdgeCount).IsGreaterThan(0);

        evaluator.Evaluate(parsed.Frames);
        await Assert.That(edge.FireCount).IsEqualTo(firstEdgeCount)
            .Because("run 2 must reset then re-count, not accumulate to 2× run 1");
    }
}
