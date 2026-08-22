#region

using System.Diagnostics;
using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Analysis.Building;
using CS2DemoKit.Analysis.Diagnostics;
using CS2DemoKit.Analysis.Events;
using CS2DemoKit.Analysis.Graphs;
using CS2DemoKit.Analysis.Nodes;
using CS2DemoKit.Parser;
using CS2DemoKit.Parser.GameEvents;
using Microsoft.Extensions.Logging;

#endregion

namespace CS2DemoKit.Analysis;

/// <summary>
///     Evaluates a <see cref="StateGraph" /> against a parsed demo, producing a
///     <see cref="RuleChainTimeline" /> recording every conjunction node rising-edge event.
///     Supports dynamic per-player node materialization via <see cref="PerPlayerNodeTemplate" />.
/// </summary>
public sealed class StateGraphEvaluator
{
    // The up-front parallel decode is ~70% of eval wall-time, so it drives the first 70% of
    // the determinate progress bar and the per-frame consume loop drives the rest. Approximate (the
    // decode/loop split varies by machine + GC mode) but enough for a smooth bar instead of a 0%-then-race.
    private const double PrecomputeShare = 0.7;

    // Per-player chain-satisfaction nodes ("_chain_{id}") → owning player, registered at
    // materialization (the only place node→player is known). Consulted once per rising edge to
    // stamp attribution into RuleChainEvent; game-scoped chain nodes miss → null slot/name.
    private readonly Dictionary<StateNode, (int Slot, string Name)> _chainNodePlayers = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<Type, List<ConjunctionNode>> _conjunctionIndex;
    private readonly ParsedDemo? _demo;
    private readonly Dictionary<Type, List<DisjunctionNode>> _disjunctionIndex;
    private readonly Dictionary<Type, HashSet<StateNode>> _dispatchKeyToSources = [];

    // ── Diagnostics ─────────────────────────────────────────────────────────
    private readonly Dictionary<StateEdge, int> _edgeIds = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<Type, List<StateEdge>> _edgeIndex;

    // ── Entity-state scanner (lazy-activated; null when no rule references entity contexts) ──
    private readonly EntityChangeScanner? _entityScanner;
    private readonly StateGraph _graph;

    // Live computes whose reads were dirtied since the last settle. Accumulates across a message's
    // edge phase (ProcessWrittenNodes) and its logic-settle phase (rising-edge counter writes +
    // round reset), then drains in CheckLogicNodesInstrumented. Cleared at the end of that settle.
    private readonly HashSet<ComputedStatNode> _liveComputeDirty = new(ReferenceEqualityComparer.Instance);

    // Per-message once-fired latch (the duplicate-fire guard + hard frequency cap): a live compute
    // recomputes AT MOST ONCE per evaluated message, no matter how many of its reads were dirtied or
    // how many readers exist. Cleared per message in CheckLogicNodesInstrumented. The compute's Value
    // is memoized between recomputes, so downstream reads are O(1) and never trigger a recompute.
    private readonly HashSet<ComputedStatNode> _liveComputeFiredThisMessage = new(ReferenceEqualityComparer.Instance);

    // Every registered live compute, in registration (≈ author/document) order. The settle loop fires
    // in this order so a live compute reading another live compute observes the upstream's fresh value
    // when the upstream is authored first (the planner builds computes in document order). Iterating
    // this stable list — not the unordered dirty set — also lets a downstream compute re-dirtied by an
    // upstream fire recompute within the SAME pass.
    private readonly List<ComputedStatNode> _liveComputeList = [];

    // Always-on per-node live recompute counts (mirrors _risingEdgeFireCounts): the number of times
    // each live compute was recomputed in the current/most-recent evaluation. Reset per evaluation.
    private readonly Dictionary<ComputedStatNode, int> _liveComputeRecomputeCounts = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<Type> _liveDispatchKeys = [];

    // Coarse, human-readable lifecycle logging (unified diagnostics pillar). Resolved from the ambient
    // DiagnosticsLog factory at construction — a NullLogger until the App wires a real one at startup,
    // which always happens before the first analysis runs. The firehose stays on EvaluatorEventSource.
    private readonly ILogger _log = DiagnosticsLog.CreateLogger(EvaluatorLog.Category);
    private readonly List<GraphEdgeDescriptor> _materializedEdgeDescriptors = [];
    private readonly List<StateNode> _materializedNodeList = [];

    private readonly List<PerPlayerNodeTemplate.MaterializedPlayer> _materializedPlayers = [];
    private readonly HashSet<int> _materializedSlots = [];

    // ── Match-restart baselines ─────────────────────────────────────────────
    // A repeated begin_new_match (a server restarting the match after a warmup/knife round) must
    // discard everything match-scoped stats have accumulated — measured: a knife round counted
    // into the real match's round wins scored a 24-round match 14–11 over "25" rounds. Captured at
    // materialization time, which is exactly the declared-default state: materialization completes
    // before any of the player's edges can dispatch. One restorer per value-bearing node; nodes
    // with nothing match-accumulated (round-scoped, live derivations, pulls) capture none.
    private readonly List<(StateNode Node, Action Restore)> _matchRestartBaselines = [];
    private readonly Dictionary<StateNode, int> _nodeIds = new(ReferenceEqualityComparer.Instance);

    // ── Opt-in LIVE computes ─────────────────────────────────────────────────
    // A compute authored `compute: { live: true }` recomputes during the eval loop's dirty-settle
    // stage as its DECLARED READS change, instead of once at round end. This machinery is ADDITIVE:
    // when no live compute is registered (_hasAnyLiveCompute == false) the settle loop below is the
    // exact pre-live-compute drain, so a graph with zero live computes evaluates byte-identically.
    //
    // readNode -> the live computes that read it. A write to any read schedules its computes.
    private readonly Dictionary<StateNode, List<ComputedStatNode>> _nodeToLiveComputes = new(ReferenceEqualityComparer.Instance);

    // ── Logic node dirty tracking ───────────────────────────────────────────
    private readonly Dictionary<StateNode, List<object>> _nodeToLogicDependents = new(ReferenceEqualityComparer.Instance);

    private readonly List<object> _pendingLogicRecompute = [];

    private readonly IReadOnlyList<PerPlayerNodeTemplate> _perPlayerTemplates;

    private readonly PlayerContextIndex? _playerContextIndex;

    // Multi-action per trigger (registration used to silently last-wins).
    // A1 (rich highlight emission): each entry carries EITHER the plain arm (Invoke) or the
    // context arm (InvokeWithContext, handed the firing site's (frameIdx, tick) — frame clock).
    private readonly Dictionary<StateNode, List<RegisteredRisingEdgeAction>> _risingEdgeActions = new(ReferenceEqualityComparer.Instance);

    // Always-on rising-edge fire counters, keyed by trigger node.
    // Seeded to 0 at both registration sites so never-fired triggers are present in the map.
    private readonly Dictionary<StateNode, int> _risingEdgeFireCounts = new(ReferenceEqualityComparer.Instance);

    // Per-message once-fired latch. A conjunction can flip true→false→true within
    // one message (the dirty routing below recomputes logic nodes mid-drain), which produces a
    // second rising edge — without the latch its actions double-fire. Cleared per message in
    // CheckLogicNodesInstrumented.
    private readonly HashSet<StateNode> _risingEdgeFiredThisMessage = new(ReferenceEqualityComparer.Instance);
    private readonly List<IRoundScopedNode> _roundScopedNodes = [];

    // ── Dispatch filtering ─────────────────────────────────────────────────
    private readonly Dictionary<StateNode, HashSet<Type>> _sourceToDispatchKeys = new(ReferenceEqualityComparer.Instance);

    // ── Transient node tracking ─────────────────────────────────────────────
    private readonly Dictionary<Type, List<ITransientNode>> _transientNodesPerKey = [];
    private readonly List<StateNode> _writtenBatch = [];
    private bool _hasAnyLiveCompute;

    // A1: the per-evaluation HighlightFired list published through HighlightsFired — reset at
    // evaluation start, swapped in at successful completion.
    private int _materializedPlayerCount;
    private int _nextEdgeId;
    private int _nextNodeId;

    /// <param name="graph">The compiled rule-chain graph to evaluate.</param>
    /// <param name="demo">Optional parsed demo for player-roster lookups during per-player materialization.</param>
    /// <param name="playerContextIndex">Optional cross-player state index for enrichment edges.</param>
    /// <param name="entityScanner">Optional entity-state scanner for rules that read networked entity fields.</param>
    public StateGraphEvaluator(StateGraph graph, ParsedDemo? demo = null,
        PlayerContextIndex? playerContextIndex = null,
        EntityChangeScanner? entityScanner = null)
    {
        _graph = graph;
        _demo = demo;
        _playerContextIndex = playerContextIndex;
        _entityScanner = entityScanner;
        _edgeIndex = BuildEdgeIndex(graph.Edges);
        _conjunctionIndex = BuildLogicNodeIndex(graph.Edges, graph.ConjunctionNodes);
        _disjunctionIndex = BuildLogicNodeIndex(graph.Edges, graph.DisjunctionNodes);
        _perPlayerTemplates = graph.PerPlayerTemplates;

        foreach ((StateNode trigger, List<(Action Invoke, StateNode? Writes)> actions) in graph.RisingEdgeActions)
        {
            // Copy the list: the graph may be shared across evaluators and later mutated.
            _risingEdgeActions[trigger] =
                [.. actions.Select(a => new RegisteredRisingEdgeAction(a.Invoke, null, a.Writes))];
            _risingEdgeFireCounts[trigger] = 0;
        }

        // A1: context-arm registrations (Action<int,int> — handed (frameIdx, tick) at fire time).
        // Merged AFTER the plain actions so, per trigger, plain registrants keep firing first.
        foreach ((StateNode trigger, List<(Action<int, int> Invoke, StateNode? Writes)> actions) in graph.ContextRisingEdgeActions)
        {
            if (!_risingEdgeActions.TryGetValue(trigger, out List<RegisteredRisingEdgeAction>? merged))
            {
                _risingEdgeActions[trigger] = merged = [];
                _risingEdgeFireCounts[trigger] = 0;
            }

            merged.AddRange(actions.Select(a => new RegisteredRisingEdgeAction(null, a.Invoke, a.Writes)));
        }

        // Graph-scoped live computes (per-player ones register in MaterializeSlot).
        foreach (LiveComputeRegistration reg in graph.LiveComputes)
        {
            RegisterLiveCompute(reg.Compute, reg.Reads);
        }

        foreach (StateEdge edge in graph.Edges)
        {
            if (edge is IRoundScopedNode rsEdge)
            {
                _roundScopedNodes.Add(rsEdge);
            }

            if (edge.WrittenNode is ITransientNode transient)
            {
                RegisterTransientNode(edge.MessageType, transient);
            }

            if (edge.AdditionalWrittenNodes is { } additionalNodes)
            {
                foreach (StateNode node in additionalNodes)
                {
                    if (node is ITransientNode additionalTransient)
                    {
                        RegisterTransientNode(edge.MessageType, additionalTransient);
                    }
                }
            }
        }

        BuildDispatchFilterIndex(graph.Edges);
        RebuildLiveDispatchKeys();
        BuildLogicDependencyMap(graph.ConjunctionNodes, graph.DisjunctionNodes);

        if (demo?.Players is not null && _perPlayerTemplates.Count > 0)
        {
            MaterializeKnownPlayers(demo);
        }

        if (EvaluatorEventSource.Log.IsEnabled())
        {
            EmitRegistrationEvents(graph);
        }
    }

    // ── Public evaluate methods ───────────────────────────────────────────────

    /// <summary>
    ///     Per-trigger rising-edge action fire counts for the current or most recent
    ///     evaluation (always-on). Keys are the trigger logic nodes (the
    ///     <c>_chain_{id}</c> conjunctions); never-fired triggers are present with 0.
    ///     Complements <see cref="StateEdge.FireCount" />, which covers dispatched edges —
    ///     rising-edge actions are keyed by trigger node, not edge.
    /// </summary>
    public IReadOnlyDictionary<StateNode, int> RisingEdgeFireCounts => _risingEdgeFireCounts;

    /// <summary>
    ///     Every <see cref="HighlightFired" /> record emitted by the current or most recent
    ///     evaluation, in firing order (A1 rich highlight emission). Populated in BOTH modes —
    ///     bare <see cref="Evaluate" /> included (that is the point: the Highlights pipeline's
    ///     scan mode is snapshot-free). Empty when the graph carries no v2 highlights. Reset at
    ///     the start of each evaluation.
    /// </summary>
    public IReadOnlyList<HighlightFired> HighlightsFired { get; private set; } = [];

    /// <summary>
    ///     Per-live-compute recompute counts for the current or most recent evaluation.
    ///     Keys are the live <see cref="ComputedStatNode" />s; a compute that never recomputed is
    ///     present with 0. The duplicate-fire guard bounds each key's increment to at most once per
    ///     evaluated message, so this is the observable proof of the hard frequency cap.
    /// </summary>
    public IReadOnlyDictionary<ComputedStatNode, int> LiveComputeRecomputeCounts => _liveComputeRecomputeCounts;

    /// <summary>
    ///     Registers a live compute: maps each read node to the compute so a write to
    ///     any read schedules a recompute, and flips <see cref="_hasAnyLiveCompute" /> so the settle
    ///     interleave activates. Called from the constructor (graph-scoped) and MaterializeSlot
    ///     (per-player). Idempotent per (compute, read) pair.
    /// </summary>
    private void RegisterLiveCompute(ComputedStatNode compute, IReadOnlyList<StateNode> reads)
    {
        _hasAnyLiveCompute = true;
        if (_liveComputeRecomputeCounts.TryAdd(compute, 0))
        {
            _liveComputeList.Add(compute);
        }

        foreach (StateNode read in reads)
        {
            if (!_nodeToLiveComputes.TryGetValue(read, out List<ComputedStatNode>? list))
            {
                _nodeToLiveComputes[read] = list = [];
            }

            if (!list.Contains(compute))
            {
                list.Add(compute);
            }
        }
    }

    /// <summary>
    ///     Runs the evaluator over the demo frames and returns a timeline of every chain
    ///     activation/deactivation. Does not capture per-message snapshots — use
    ///     <see cref="EvaluateWithSnapshots" /> when seek/inspect is needed.
    /// </summary>
    /// <param name="frames">The demo's frame list.</param>
    /// <param name="maxDegreeOfParallelism">
    ///     Caps the up-front parallel entity decode's worker count; <c>null</c> = unbounded. See
    ///     <see cref="AnalysisOptions.MaxDegreeOfParallelism" /> for when to set it.
    /// </param>
    /// <param name="cancellationToken">Checked once per frame; a canceled run throws and returns nothing.</param>
    public RuleChainTimeline Evaluate(IReadOnlyList<DemoFrame> frames,
        int? maxDegreeOfParallelism = null,
        CancellationToken cancellationToken = default)
    {
        List<RuleChainEvent> events = new();
        EvaluateCore(frames, null, null, events, maxDegreeOfParallelism, cancellationToken);
        return new RuleChainTimeline(events);
    }

    /// <summary>
    ///     Runs the evaluator and additionally captures per-message <see cref="NodeSnapshot" /> values
    ///     for every node in <paramref name="staticTrackedNodes" />, plus all materialized per-player
    ///     nodes. Used by the visualization layer to seek to any message without replaying from start.
    /// </summary>
    /// <param name="frames">The demo's frame list.</param>
    /// <param name="staticTrackedNodes">The nodes whose values are captured per message.</param>
    /// <param name="progress">Fraction-complete in [0, 1].</param>
    /// <param name="maxDegreeOfParallelism">
    ///     Caps the up-front parallel entity decode's worker count; <c>null</c> = unbounded. See
    ///     <see cref="AnalysisOptions.MaxDegreeOfParallelism" /> for when to set it.
    /// </param>
    /// <param name="cancellationToken">Checked once per frame; a canceled run throws and returns nothing.</param>
    public EvaluationResult EvaluateWithSnapshots(
        IReadOnlyList<DemoFrame> frames,
        IReadOnlyList<StateNode> staticTrackedNodes,
        IProgress<double>? progress = null,
        int? maxDegreeOfParallelism = null,
        CancellationToken cancellationToken = default)
    {
        int totalMessageCapacity = 0;
        for (int f = 0; f < frames.Count; f++)
        {
            totalMessageCapacity += frames[f].DecodedMessages.Count;
        }

        List<RuleChainEvent> events = new();
        SnapshotState snap = new(staticTrackedNodes, totalMessageCapacity);

        EvaluateCore(frames, snap, progress, events, maxDegreeOfParallelism, cancellationToken);

        // Late-materialized players appended tracked nodes mid-run, so earlier chunk rows cover
        // fewer columns than the final node count. There is no padding pass: the SnapshotTable
        // reader serves uncovered cells from each column's at-materialization default —
        // byte-identical to the values the old eager padding wrote into cloned full rows.
        SnapshotTable table = new(
            snap.Snapshots.ToArray(),
            staticTrackedNodes.Count,
            snap.NodeDefaults.ToArray(),
            snap.TrackedNodes.Count);

        return new EvaluationResult(
            new RuleChainTimeline(events),
            table,
            snap.Messages,
            snap.TrackedNodes,
            _materializedPlayers,
            _materializedEdgeDescriptors,
            snap.AppliedByEdge);
    }

    /// <summary>
    ///     Zeroes all always-on fire counters at evaluation start. _edgeIndex holds
    ///     every registered edge exactly once (one MessageType key per edge); edges and actions
    ///     materialized mid-run are freshly constructed (FireCount == 0 / seeded 0), so
    ///     registration covers them. Enumerator structs only — zero allocation.
    /// </summary>
    private void ResetFireCounters()
    {
        foreach (List<StateEdge> list in _edgeIndex.Values)
        {
            for (int i = 0; i < list.Count; i++)
            {
                list[i].FireCount = 0;
            }
        }

        foreach (StateNode trigger in _risingEdgeActions.Keys)
        {
            _risingEdgeFireCounts[trigger] = 0;
        }

        // Live-compute recompute counts + transient dirty set are per-evaluation too.
        if (_hasAnyLiveCompute)
        {
            for (int i = 0; i < _liveComputeList.Count; i++)
            {
                _liveComputeRecomputeCounts[_liveComputeList[i]] = 0;
            }

            _liveComputeDirty.Clear();
            _liveComputeFiredThisMessage.Clear();
        }
    }

    /// <summary>
    ///     The single evaluation loop behind both public methods (they were ~120-line near-duplicates
    ///     that drifted; the snapshot-bug comment in <see cref="EvaluateEdgesInstrumented" /> records
    ///     what that cost). <paramref name="snap" /> is null on the bare path — every snapshot-side branch is a
    ///     null check, the same nullable-optional convention the edge dispatcher already uses on this
    ///     hot path.
    /// </summary>
    private void EvaluateCore(
        IReadOnlyList<DemoFrame> frames,
        SnapshotState? snap,
        IProgress<double>? progress,
        List<RuleChainEvent> events,
        int? maxDegreeOfParallelism,
        CancellationToken cancellationToken)
    {
        using Activity? evalSpan = AnalysisDiagnostics.ActivitySource.StartActivity("analysis.eval");
        // One timestamp per analysis run (not per frame) — negligible, used only for the completion log line.
        long runStart = Stopwatch.GetTimestamp();
        bool trace = EvaluatorEventSource.Log.IsEnabled();
        bool meter = EvaluatorMetrics.Enabled;
        // Guard the per-message Counter.Add on a Meter-listener check (one bool read when off,
        // vs four Counter.Add). Also drive the FrameDurationMs histogram off `timeFrame` so a
        // dotnet-counters consumer gets per-frame timing even with no EventSource trace attached.
        bool timeFrame = trace || meter;
        long totalEdgesFired = 0;
        int totalMessages = 0;

        // Fire counters are per-evaluation. Edges/actions registered before this
        // point (constructor-time MaterializeKnownPlayers included) may carry counts from a
        // previous run of this evaluator — or of another evaluator over the same shared graph.
        ResetFireCounters();

        // A1: the highlight sink is per-evaluation too — the emission closures append to the
        // graph-owned collector (they were created at build time and cannot see this evaluator),
        // so clear it here exactly like the fire counters above. The published list resets with
        // it: HighlightsFired must never serve a PREVIOUS run's records mid-run or after a
        // cancelled/failed run.
        _graph.HighlightSink.Clear();
        HighlightsFired = [];

        bool logStart = _log.IsEnabled(LogLevel.Information);
        if (trace || logStart)
        {
            // Compute the graph dimensions once and feed both the machine channel (EventSource) and the
            // human channel (ILogger) — only when at least one is actually listening.
            int edgeCount = _edgeIndex.Values.Sum(l => l.Count);
            int nodeCount = snap?.TrackedNodes.Count ?? 0;
            if (trace)
            {
                EvaluatorEventSource.Log.EvaluationStarted(frames.Count, edgeCount, nodeCount);
            }

            if (logStart)
            {
                EvaluatorLog.EvaluationStarted(_log, frames.Count, edgeCount, nodeCount);
            }
        }

        // Decode the entity stream in parallel up front so the per-frame
        // AdvanceAndPollAt below consumes a precomputed digest instead of driving the layer
        // sequentially. Golden-preserving (digests proven element-wise identical to the sequential
        // ones). This moved the bulk of the eval cost ahead of the loop, so it OWNS the first
        // PrecomputeShare of the progress bar (reporting per chunk) — otherwise the bar would sit
        // at 0% for the whole parallel decode then race to 100%.
        _entityScanner?.PrecomputeParallelDigests(frames,
            progress is null ? null : p => progress.Report(p * PrecomputeShare),
            maxDegreeOfParallelism,
            cancellationToken);

        for (int frameIdx = 0; frameIdx < frames.Count; frameIdx++)
        {
            // Frame granularity is the cancellation quantum: cheap (one volatile read per ~dozens
            // of messages) and bounds cancel latency to a single frame's work.
            cancellationToken.ThrowIfCancellationRequested();

            // Determinate-progress feedback for the UI. Report ~every 2048 frames so the
            // background eval shows a moving bar instead of an indeterminate spinner; frameIdx is a
            // good linear proxy because the per-frame consume cost is roughly uniform. The loop owns
            // the tail [PrecomputeShare, 1] (the parallel decode owned the head). Progress<T>.Report
            // marshals to the UI thread; ~73 posts is negligible. Null on headless/bare → zero cost.
            if (progress is not null && (frameIdx & 2047) == 0)
            {
                progress.Report(PrecomputeShare + (1.0 - PrecomputeShare) * frameIdx / frames.Count);
            }

            DemoFrame frame = frames[frameIdx];
            long frameStart = timeFrame ? Stopwatch.GetTimestamp() : 0;
            int frameMessageCount = 0;

            // Shared per-message tail: metrics, per-frame counters, and (snapshot mode) the
            // post-message snapshot row + message record.
            void FinishMessage(NetMessage message, int evaluated, int fired, int logic)
            {
                totalEdgesFired += fired;
                frameMessageCount++;
                totalMessages++;

                if (meter)
                {
                    EvaluatorMetrics.MessagesProcessed.Add(1);
                    EvaluatorMetrics.EdgesEvaluated.Add(evaluated);
                    EvaluatorMetrics.EdgesFired.Add(fired);
                    EvaluatorMetrics.LogicNodesRecomputed.Add(logic);
                }

                snap?.CaptureAfterMessage(frame, message, fired > 0);
            }

            // ── Synthesize entity-state change events (lazy: scanner is null when no rule
            //    references an entity context). Each synthesized message participates in the
            //    snapshot/dirty bookkeeping exactly like a real frame message. ──
            if (_entityScanner is not null)
            {
                IReadOnlyList<NetMessage> synthesized = _entityScanner.AdvanceAndPollAt(frameIdx, frame.ServerTick);
                for (int s = 0; s < synthesized.Count; s++)
                {
                    NetMessage syntheticMsg = synthesized[s];

                    // Synthesized game events (molotov_thrown) must materialize
                    // their player exactly like real-message events below — a player whose first
                    // qualifying activity is entity-derived was otherwise silently dropped. The
                    // TrackNewlyMaterializedNodes call is mandatory in snapshot mode (a past
                    // bug class — undeclared writes: untracked mid-run nodes break row padding).
                    if (syntheticMsg is GameEventMessage sgem)
                    {
                        MaterializeNewPlayers(sgem.DecodedEvent);
                        snap?.TrackNewlyMaterializedNodes(_materializedNodeList);
                    }

                    Type sKey = GetDispatchKey(syntheticMsg);
                    int sEvaluated = 0, sFired = 0, sLogic = 0;
                    EvaluateEdgesInstrumented(new EvaluationContext(syntheticMsg, frame), sKey,
                        trace, ref sEvaluated, ref sFired,
                        snap?.Dirty, snap?.NodeToIndex, snap?.AppliedByEdge, snap?.Snapshots.Count ?? -1);
                    CheckLogicNodesInstrumented(events, sKey, frameIdx, frame.ServerTick,
                        trace, ref sLogic, snap);

                    FinishMessage(syntheticMsg, sEvaluated, sFired, sLogic);
                }
            }

            // DecodedMessages, not InnerMessages: the stored svc_UserCmds entries are synthesized on
            // access and their dispatch key is the storage wrapper's type, which matches no rule.
            IReadOnlyList<NetMessage> msgs = frame.DecodedMessages;
            for (int m = 0; m < msgs.Count; m++)
            {
                NetMessage message = msgs[m];
                Type key = GetDispatchKey(message);
                long msgStart = trace ? Stopwatch.GetTimestamp() : 0;

                if (message is GameEventMessage gem)
                {
                    MaterializeNewPlayers(gem.DecodedEvent);
                    snap?.TrackNewlyMaterializedNodes(_materializedNodeList);
                }

                if (key == typeof(RoundFreezeEndEvent))
                {
                    ResetRoundScopedNodes();
                    snap?.MarkRoundScopedDirty(_roundScopedNodes);
                }
                else if (key == typeof(BeginNewMatchEvent))
                {
                    ResetForMatchRestart(snap);
                }

                int edgesEvaluated = 0, edgesFired = 0, logicRecomputed = 0;
                EvaluateEdgesInstrumented(new EvaluationContext(message, frame), key,
                    trace, ref edgesEvaluated, ref edgesFired,
                    snap?.Dirty, snap?.NodeToIndex, snap?.AppliedByEdge, snap?.Snapshots.Count ?? -1);
                CheckLogicNodesInstrumented(events, key, frameIdx, frame.ServerTick,
                    trace, ref logicRecomputed, snap);

                if (trace)
                {
                    EvaluatorEventSource.Log.MessageProcessed(frameIdx, key.Name,
                        edgesEvaluated, edgesFired, logicRecomputed,
                        Stopwatch.GetTimestamp() - msgStart);
                }

                FinishMessage(message, edgesEvaluated, edgesFired, logicRecomputed);
            }

            if (timeFrame)
            {
                long frameTicks = Stopwatch.GetTimestamp() - frameStart;
                if (trace)
                {
                    EvaluatorEventSource.Log.FrameProcessed(frameIdx, frameMessageCount, frameTicks);
                }

                if (meter)
                {
                    EvaluatorMetrics.FrameDurationMs.Record(
                        (double)frameTicks / Stopwatch.Frequency * 1000.0);
                }
            }
        }

        // A1: snapshot the collected highlight firings for this evaluation (copy — the graph's
        // sink is cleared by the NEXT evaluation over this graph, ours must stay stable).
        HighlightsFired = _graph.HighlightSink.Count > 0 ? [.. _graph.HighlightSink] : [];

        if (trace)
        {
            EvaluatorEventSource.Log.EvaluationCompleted(totalMessages, (int)totalEdgesFired, 0);
        }

        if (_log.IsEnabled(LogLevel.Information))
        {
            double elapsedMs = Stopwatch.GetElapsedTime(runStart).TotalMilliseconds;
            EvaluatorLog.EvaluationCompleted(_log, totalMessages, (int)totalEdgesFired, elapsedMs);
        }
    }

    private void AddLogicDependent(StateNode source, object logicNode)
    {
        if (!_nodeToLogicDependents.TryGetValue(source, out List<object>? list))
        {
            _nodeToLogicDependents[source] = list = [];
        }

        if (!list.Contains(logicNode))
        {
            list.Add(logicNode);
        }
    }

    private static void ApplyWriteOrdering(StateNode? written, int writerIdx,
        Dictionary<StateNode, List<int>> sourceToEdges, List<int>[] adjForward,
        int[] inDegree, bool isDeactivate)
    {
        if (written is null)
        {
            return;
        }

        if (!sourceToEdges.TryGetValue(written, out List<int>? readers))
        {
            return;
        }

        foreach (int ri in readers)
        {
            if (ri == writerIdx)
            {
                continue;
            }

            if (isDeactivate)
            {
                adjForward[ri].Add(writerIdx);
                inDegree[writerIdx]++;
            }
            else
            {
                adjForward[writerIdx].Add(ri);
                inDegree[ri]++;
            }
        }
    }

    // ── Dispatch filter infrastructure ──────────────────────────────────────

    private void BuildDispatchFilterIndex(IReadOnlyList<StateEdge> edges)
    {
        foreach (StateEdge edge in edges)
        {
            TrackEdgeForDispatchFilter(edge);
        }
    }

    private static Dictionary<Type, List<StateEdge>> BuildEdgeIndex(IReadOnlyList<StateEdge> edges)
    {
        Dictionary<Type, List<StateEdge>> index = new();
        foreach (StateEdge edge in edges)
        {
            if (!index.TryGetValue(edge.MessageType, out List<StateEdge>? list))
            {
                index[edge.MessageType] = list = [];
            }

            list.Add(edge);
        }

        TopologicalSortEdges(index);
        return index;
    }

    // Multi-source inputs: dirty-marking unions ALL of a conditional input's sources, so a write to any
    // one of an N-source predicate's nodes marks the owning logic node's inputs dirty. Single-
    // source inputs yield [Source] and behave exactly as before.
    private void BuildLogicDependencyMap(
        IReadOnlyList<ConjunctionNode> conjunctions,
        IReadOnlyList<DisjunctionNode> disjunctions)
    {
        foreach (ConjunctionNode cj in conjunctions)
        {
            foreach (IConditionalEdge input in cj.Inputs)
            {
                foreach (StateNode source in input.Sources)
                {
                    AddLogicDependent(source, cj);
                }
            }
        }

        foreach (DisjunctionNode dj in disjunctions)
        {
            foreach (IConditionalEdge input in dj.Inputs)
            {
                foreach (StateNode source in input.Sources)
                {
                    AddLogicDependent(source, dj);
                }
            }
        }
    }

    private static Dictionary<Type, List<T>> BuildLogicNodeIndex<T>(
        IReadOnlyList<StateEdge> edges,
        IReadOnlyList<T> logicNodes) where T : class
    {
        // Extract Inputs from T (works for both ConjunctionNode and DisjunctionNode).
        static IReadOnlyList<IConditionalEdge> GetInputs(T node)
        {
            return node switch
            {
                ConjunctionNode cj => cj.Inputs,
                DisjunctionNode dj => dj.Inputs,
                _ => []
            };
        }

        Dictionary<StateNode, List<T>> sourceToNodes = new(
            ReferenceEqualityComparer.Instance);

        foreach (T node in logicNodes)
        {
            foreach (IConditionalEdge input in GetInputs(node))
            {
                // Multi-source inputs: recompute bucketing unions ALL of the input's sources — an edge
                // writing any one of an N-source predicate's nodes must recompute the logic node.
                foreach (StateNode source in input.Sources)
                {
                    if (!sourceToNodes.TryGetValue(source, out List<T>? list))
                    {
                        sourceToNodes[source] = list = [];
                    }

                    if (!list.Contains(node))
                    {
                        list.Add(node);
                    }
                }
            }
        }

        Dictionary<Type, List<T>> index = new();

        // Buckets the logic nodes reading `written` under the edge's dispatch key.
        void IndexWrittenNode(StateEdge edge, StateNode? written)
        {
            if (written is null)
            {
                return;
            }

            if (!sourceToNodes.TryGetValue(written, out List<T>? nodes))
            {
                return;
            }

            if (!index.TryGetValue(edge.MessageType, out List<T>? bucket))
            {
                index[edge.MessageType] = bucket = [];
            }

            foreach (T node in nodes)
            {
                if (!bucket.Contains(node))
                {
                    bucket.Add(node);
                }
            }
        }

        foreach (StateEdge edge in edges)
        {
            IndexWrittenNode(edge, edge.WrittenNode);

            // AdditionalWrittenNodes must reach the recompute index too.
            // Before this fold, a logic node whose input read a node written ONLY via
            // AdditionalWrittenNodes (the enrichment-edge multi-write pattern) was never bucketed
            // under the writing edge's dispatch key — its inputs were marked dirty by the written
            // batch, but no recompute ran on that message, so the flip was silently deferred to
            // whatever later message happened to touch the node (or never came).
            if (edge.AdditionalWrittenNodes is { } additional)
            {
                foreach (StateNode extra in additional)
                {
                    IndexWrittenNode(edge, extra);
                }
            }
        }

        return index;
    }

    private void CheckLogicNodesInstrumented(List<RuleChainEvent> events, Type key,
        int frameIdx, int tick, bool verbose, ref int logicRecomputed, SnapshotState? snap)
    {
        _pendingLogicRecompute.Clear();
        // The once-fired latch is per message — cleared here, at the single
        // entry point of the per-message logic pass.
        _risingEdgeFiredThisMessage.Clear();
        // The live-compute once-fired latch is likewise per message (the duplicate-fire
        // guard + hard frequency cap). No-op cost when no live compute is registered.
        if (_hasAnyLiveCompute)
        {
            _liveComputeFiredThisMessage.Clear();
        }

        if (_conjunctionIndex.TryGetValue(key, out List<ConjunctionNode>? conjunctions))
        {
            foreach (ConjunctionNode cj in conjunctions)
            {
                RecomputeLogicNode(cj, events, frameIdx, tick, verbose, ref logicRecomputed, snap);
            }
        }

        if (_disjunctionIndex.TryGetValue(key, out List<DisjunctionNode>? disjunctions))
        {
            foreach (DisjunctionNode dj in disjunctions)
            {
                RecomputeLogicNode(dj, events, frameIdx, tick, verbose, ref logicRecomputed, snap);
            }
        }

        if (!_hasAnyLiveCompute)
        {
            // Pre-A3a drain — byte-identical. This branch runs for every graph with zero live
            // computes (the v1 corpus, the pilot, every existing golden), so it is kept verbatim.
            while (_pendingLogicRecompute.Count > 0)
            {
                List<object> batch = _pendingLogicRecompute.ToList();
                _pendingLogicRecompute.Clear();
                foreach (object node in batch)
                {
                    if (node is ConjunctionNode cj)
                    {
                        RecomputeLogicNode(cj, events, frameIdx, tick, verbose, ref logicRecomputed, snap);
                    }
                    else if (node is DisjunctionNode dj)
                    {
                        RecomputeLogicNode(dj, events, frameIdx, tick, verbose, ref logicRecomputed, snap);
                    }
                }
            }

            return;
        }

        // Logic ⇄ live-compute fixpoint interleave.
        DrainLogicAndLiveComputes(events, frameIdx, tick, verbose, ref logicRecomputed, snap);
        _liveComputeDirty.Clear();
    }

    /// <summary>
    ///     The logic ⇄ live-compute fixpoint settle (runs only when at least one live
    ///     compute is registered; the zero-live path uses the verbatim pre-live-compute drain above). Each
    ///     outer iteration: (1) drains the logic recompute queue to fixpoint — on exit every
    ///     rising-edge counter this message writes holds its FINAL value, because rising-edge actions
    ///     fire inside <see cref="RecomputeLogicNode" /> during the drain; then (2) recomputes every
    ///     dirty, not-yet-latched live compute IN REGISTRATION ORDER, so each reads settled
    ///     inputs. A recompute's write is routed through the dirty pipeline
    ///     (<see cref="RouteLiveComputeWrite" />): it may enqueue logic (picked up by step 1 next
    ///     iteration) or re-dirty a downstream compute (picked up later in this same ordered pass, or
    ///     next iteration) — so a live value feeding logic/other computes re-evaluates to fixpoint. The
    ///     per-message latch caps each compute at one recompute, which also guarantees
    ///     termination: finitely many computes each fire at most once, so <c>progress</c> can be true
    ///     only finitely often.
    /// </summary>
    private void DrainLogicAndLiveComputes(List<RuleChainEvent> events, int frameIdx, int tick,
        bool verbose, ref int logicRecomputed, SnapshotState? snap)
    {
        bool progress = true;
        while (progress)
        {
            progress = false;

            // (1) Drain logic to fixpoint — identical body to the pre-live-compute drain.
            while (_pendingLogicRecompute.Count > 0)
            {
                List<object> batch = _pendingLogicRecompute.ToList();
                _pendingLogicRecompute.Clear();
                foreach (object node in batch)
                {
                    if (node is ConjunctionNode cj)
                    {
                        RecomputeLogicNode(cj, events, frameIdx, tick, verbose, ref logicRecomputed, snap);
                    }
                    else if (node is DisjunctionNode dj)
                    {
                        RecomputeLogicNode(dj, events, frameIdx, tick, verbose, ref logicRecomputed, snap);
                    }
                }
            }

            // (2) Fire dirty, un-latched live computes in registration order. Iterate the stable list
            //     (not the unordered dirty set), so a downstream compute re-dirtied by an upstream
            //     fire recomputes within this same pass with the upstream's fresh value.
            if (_liveComputeDirty.Count == 0)
            {
                continue;
            }

            for (int i = 0; i < _liveComputeList.Count; i++)
            {
                ComputedStatNode compute = _liveComputeList[i];
                if (!_liveComputeDirty.Contains(compute)
                    || _liveComputeFiredThisMessage.Contains(compute))
                {
                    continue;
                }

                // Duplicate-fire guard + hard frequency cap: at most one recompute per message per
                // compute, no matter how many reads dirtied it or how many readers exist.
                _liveComputeFiredThisMessage.Add(compute);
                compute.Recompute();
                _liveComputeRecomputeCounts[compute] = _liveComputeRecomputeCounts.GetValueOrDefault(compute) + 1;
                RouteLiveComputeWrite(compute, snap);
                progress = true;
            }
        }
    }

    // Routes a live compute's recompute write through the dirty pipeline (mirrors
    // RouteRisingEdgeWrite): snapshot dirty-tracking on the compute's own column, dispatch-key
    // liveness, logic-dependent recompute enqueue (so a live value feeding a when:/while: logic node
    // re-evaluates this message), and re-dirtying of any downstream live compute that reads it.
    private void RouteLiveComputeWrite(ComputedStatNode compute, SnapshotState? snap)
    {
        UpdateDispatchKeysForNode(compute);
        snap?.MarkDirty(compute);

        if (_nodeToLogicDependents.TryGetValue(compute, out List<object>? dependents))
        {
            foreach (object dep in dependents)
            {
                if (dep is ConjunctionNode cj)
                {
                    cj.MarkInputsDirty();
                }
                else if (dep is DisjunctionNode dj)
                {
                    dj.MarkInputsDirty();
                }

                _pendingLogicRecompute.Add(dep);
            }
        }

        MarkLiveComputeDirty(compute);
    }

    private void EmitRegistrationEvents(StateGraph graph)
    {
        foreach (StateEdge edge in graph.Edges)
        {
            int id = _nextEdgeId++;
            _edgeIds[edge] = id;
            EvaluatorEventSource.Log.EdgeRegistered(id,
                edge.Source.Name, edge.WrittenNode?.Name ?? "?", edge.MessageType.Name);
        }
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private void EvaluateEdgesInstrumented(EvaluationContext ctx, Type key,
        bool verbose, ref int edgesEvaluated, ref int edgesFired,
        bool[]? snapshotDirty = null, Dictionary<StateNode, int>? nodeToIndex = null,
        Dictionary<StateEdge, List<int>>? appliedRecorder = null, int currentMessageIndex = -1)
    {
        if (_transientNodesPerKey.TryGetValue(key, out List<ITransientNode>? transients))
        {
            foreach (ITransientNode t in transients)
            {
                t.Reset();
            }
        }

        if (!_liveDispatchKeys.Contains(key))
        {
            return;
        }

        if (!_edgeIndex.TryGetValue(key, out List<StateEdge>? edges))
        {
            return;
        }

        // The payload, not the fire: TryApplyDirect casts this straight to the edge's TEvent, which is
        // the payload type. Synthesized events declare their fields on the subclass and carry no
        // payload, so they pass through as themselves — the same rule GetDispatchKey applies.
        object payload = ctx.Message switch
        {
            GameEventMessage gem => gem.DecodedEvent.Payload ?? gem.DecodedEvent,
            EntityChangeMessage e => e.ChangeEvent,
            _ => ctx.Message.Payload
        };

        foreach (StateEdge edge in edges)
        {
            bool sourceActive = edge.Source.IsActive;
            if (!sourceActive)
            {
                if (verbose)
                {
                    edgesEvaluated++;
                    EvaluatorEventSource.Log.EdgeEvaluated(
                        GetOrRegisterEdgeId(edge), false, false);
                }

                continue;
            }

            edgesEvaluated++;
            bool applied = edge.TryApplyDirect(payload, ctx);
            if (applied)
            {
                edgesFired++;
                edge.FireCount++;

                // Side-effect-free breakpoint recording: note the global message index at
                // which this edge fired. Null on the bench/Evaluate path → byte-identical.
                // Edge applies are discrete events (NOT collapsed); the index is the slot this
                // message will occupy in MessageSnapshots/Messages (snapshot added after eval).
                if (appliedRecorder is not null)
                {
                    if (!appliedRecorder.TryGetValue(edge, out List<int>? hits))
                    {
                        appliedRecorder[edge] = hits = new List<int>();
                    }

                    hits.Add(currentMessageIndex);
                }

                if (edge.WrittenNode is not null)
                {
                    _writtenBatch.Add(edge.WrittenNode);
                }

                // Pre-existing snapshot bug: AdditionalWrittenNodes were registered for
                // transient-reset but never marked dirty in the per-message snapshot, so
                // every additional-node value (victim_health_before, capped_damage,
                // attacker_active_weapon, traded_player_slot, etc.) appeared frozen at its
                // default in retrospective snapshot inspection. Live values were correct
                // — bench evaluation reads live nodes — but UI playback and EvaluationResult
                // snapshots saw stale defaults.
                if (edge.AdditionalWrittenNodes is { } additional)
                {
                    foreach (StateNode addNode in additional)
                    {
                        _writtenBatch.Add(addNode);
                    }
                }
            }

            if (verbose)
            {
                EvaluatorEventSource.Log.EdgeEvaluated(
                    GetOrRegisterEdgeId(edge), true, applied);
            }
        }

        if (_writtenBatch.Count > 0)
        {
            ProcessWrittenNodes(snapshotDirty, nodeToIndex);
        }
    }

    // Every yielded slot may be a sentinel (-1 = no player, or 16-bit garbage like 65535 —
    // game-event slot keys pass through GameEventDecoder's ValShort with no range clamp);
    // the 0..63 range check lives at the single consumption site in MaterializeNewPlayers
    // (work item 0.4b hoisted it there — VictimSlot/PlayerSlot previously arrived unguarded
    // and materialized phantom players).
    private static IEnumerable<int> ExtractPlayerSlots(GameEvent gameEvent)
    {
        // The payload for a wire event; the fire itself for a synthesized one, which declares its
        // fields directly and carries no payload. Same subject rule GetDispatchKey applies — and the
        // reason MolotovThrownEvent below still matches.
        switch (gameEvent.Payload ?? gameEvent)
        {
            case PlayerDeathEvent death:
                yield return death.UserId;
                yield return death.Attacker;
                yield return death.Assister;

                break;

            case PlayerHurtEvent hurt:
                yield return hurt.UserId;
                yield return hurt.Attacker;

                break;

            case PlayerConnectEvent connect:
                yield return connect.UserId;

                break;

            case PlayerTeamEvent team:
                yield return team.UserId;

                break;

            // Synthesized entity-derived events (work item 0.4b): a player whose first
            // qualifying activity is a molotov throw must materialize like anyone else —
            // mid-match-start demos otherwise silently drop their events.
            case MolotovThrownEvent molotov:
                yield return molotov.PlayerSlot;

                break;
        }
    }

    // ── Core helpers ─────────────────────────────────────────────────────────

    /// <summary>
    ///     The type an edge is indexed and dispatched under. Edges declare
    ///     <see cref="StateEdge.MessageType" /> as the PAYLOAD type (<c>PlayerDeathEvent</c>), so a game
    ///     event has to key on its payload too — every fire is now the same <see cref="GameEvent" />
    ///     envelope, and keying on the envelope's runtime type would match no edge at all. A synthesized
    ///     event carries no payload and keys under its own subclass type, matching how
    ///     <c>DemoAnalyzer.BuildTypeIndex</c> indexes the same events.
    /// </summary>
    private static Type GetDispatchKey(NetMessage message) =>
        message switch
        {
            GameEventMessage gem => gem.DecodedEvent.Payload?.GetType() ?? gem.DecodedEvent.GetType(),
            EntityChangeMessage e => e.ChangeEvent.GetType(),
            _ => message.Payload.GetType()
        };

    private int GetOrRegisterEdgeId(StateEdge edge)
    {
        if (_edgeIds.TryGetValue(edge, out int id))
        {
            return id;
        }

        id = _nextEdgeId++;
        _edgeIds[edge] = id;
        EvaluatorEventSource.Log.EdgeRegistered(id,
            edge.Source.Name, edge.WrittenNode?.Name ?? "?", edge.MessageType.Name);
        return id;
    }

    private int GetOrRegisterNodeId(StateNode node)
    {
        if (_nodeIds.TryGetValue(node, out int id))
        {
            return id;
        }

        id = _nextNodeId++;
        _nodeIds[node] = id;
        EvaluatorEventSource.Log.NodeRegistered(id, node.Name,
            node.GetType().Name);
        return id;
    }

    private void MarkLogicDependentsDirty(StateNode node)
    {
        if (!_nodeToLogicDependents.TryGetValue(node, out List<object>? dependents))
        {
            return;
        }

        foreach (object dep in dependents)
        {
            if (dep is ConjunctionNode cj)
            {
                cj.MarkInputsDirty();
            }
            else if (dep is DisjunctionNode dj)
            {
                dj.MarkInputsDirty();
            }
        }
    }

    // Schedule every live compute reading `node` for recompute in the current
    // message's settle. No-op (one bool read) when no live compute is registered — the additive
    // gate that keeps the zero-live path byte-identical. The per-message once-fired latch (applied
    // in the settle loop) collapses many dirties of the same compute into one recompute.
    private void MarkLiveComputeDirty(StateNode node)
    {
        if (!_hasAnyLiveCompute)
        {
            return;
        }

        if (_nodeToLiveComputes.TryGetValue(node, out List<ComputedStatNode>? computes))
        {
            foreach (ComputedStatNode compute in computes)
            {
                _liveComputeDirty.Add(compute);
            }
        }
    }

    // ── Per-player materialization ────────────────────────────────────────────

    private void MaterializeKnownPlayers(ParsedDemo demo)
    {
        foreach ((int slot, PlayerInfo playerInfo) in demo.Players)
        {
            if (slot is < 0 or >= 64)
            {
                continue;
            }

            if (string.IsNullOrEmpty(playerInfo.Name))
            {
                continue;
            }

            if (playerInfo.Team < 2)
            {
                continue;
            }

            if (!_materializedSlots.Add(slot))
            {
                continue;
            }

            int initialTeam = playerInfo.Team;
            if (_playerContextIndex is not null
                && _playerContextIndex.InitialTeamBySlot.TryGetValue(slot, out int t))
            {
                initialTeam = t;
            }

            MaterializeSlot(slot, _materializedPlayerCount++, playerInfo.Name, initialTeam);
        }
    }

    private void MaterializeNewPlayers(GameEvent gameEvent)
    {
        if (_perPlayerTemplates.Count == 0)
        {
            return;
        }

        foreach (int slot in ExtractPlayerSlots(gameEvent))
        {
            // 0..63 sentinel guard hoisted from ExtractPlayerSlots' per-case checks so every
            // yielded slot is covered (-1 = no-player sentinel, >= 64 = 16-bit garbage; VictimSlot
            // and PlayerSlot previously arrived unguarded and materialized phantom players).
            // Must precede the seen-set add so sentinels never enter it. Mirrors the range check
            // in MaterializeKnownPlayers.
            if (slot is < 0 or >= 64)
            {
                continue;
            }

            if (!_materializedSlots.Add(slot))
            {
                continue;
            }

            int playerTeam = 0;
            if (_playerContextIndex is not null && _playerContextIndex.InitialTeamBySlot.TryGetValue(slot, out int initialTeam))
            {
                playerTeam = initialTeam;
            }
            else if (_demo is not null && _demo.Players.TryGetValue(slot, out PlayerInfo? pi))
            {
                playerTeam = pi.Team;
            }

            MaterializeSlot(slot, _materializedPlayerCount++, ResolvePlayerName(slot), playerTeam);
        }
    }

    /// <summary>
    ///     The shared per-slot materialization core (the known-roster and event-discovered paths
    ///     were ~85-line duplicates differing only in name/team resolution): registers the player
    ///     context, materializes every per-player template (edges, round-scoped resets, logic-node
    ///     registration, rising-edge actions, tracked bookkeeping), and re-sorts dispatch slots.
    /// </summary>
    private void MaterializeSlot(int slot, int playerIndex, string playerName, int initialTeam)
    {
        EvaluatorMetrics.PlayersMaterialized.Add(1);

        _playerContextIndex?.Register(slot,
            new PlayerContextIndex.PlayerContext(slot, initialTeam));

        for (int tplIdx = 0; tplIdx < _perPlayerTemplates.Count; tplIdx++)
        {
            PerPlayerNodeTemplate.MaterializedPlayer result = _perPlayerTemplates[tplIdx].Materialize(slot, playerIndex, playerName, _demo);
            result = result with
            {
                TemplateIndex = tplIdx
            };

            foreach (StateEdge edge in result.Edges)
            {
                RegisterEdge(edge);
                if (edge is IRoundScopedNode rsEdge)
                {
                    rsEdge.Reset();
                    _roundScopedNodes.Add(rsEdge);
                }
            }

            foreach (StateNode node in result.Nodes)
            {
                if (CreateMatchRestartRestorer(node) is { } restore)
                {
                    _matchRestartBaselines.Add((node, restore));
                }

                if (node is IRoundScopedNode rsn)
                {
                    rsn.Reset();
                    _roundScopedNodes.Add(rsn);
                }

                if (node is ConjunctionNode cj)
                {
                    RegisterConjunction(cj, result.Edges);
                }

                if (node is DisjunctionNode dj)
                {
                    RegisterDisjunction(dj, result.Edges);
                }

                if (node is ConjunctionNode or DisjunctionNode
                    && node.Name.StartsWith("_chain_", StringComparison.Ordinal))
                {
                    _chainNodePlayers[node] = (slot, playerName);
                }
            }

            if (result.RisingEdgeActions is not null)
            {
                foreach ((StateNode trigger, Action action, StateNode? writes) in result.RisingEdgeActions)
                {
                    // Additive registration (used to silently last-wins).
                    if (!_risingEdgeActions.TryGetValue(trigger, out List<RegisteredRisingEdgeAction>? actions))
                    {
                        _risingEdgeActions[trigger] = actions = [];
                        _risingEdgeFireCounts[trigger] = 0;
                    }

                    actions.Add(new RegisteredRisingEdgeAction(action, null, writes));
                }
            }

            // A1: context-arm registrations, merged after the plain list so per trigger the plain
            // actions fire first (the v2 highlight count bump precedes its emission collector, so
            // the emitted record observes the post-increment `.count`).
            if (result.ContextRisingEdgeActions is not null)
            {
                foreach ((StateNode trigger, Action<int, int> action, StateNode? writes) in result.ContextRisingEdgeActions)
                {
                    if (!_risingEdgeActions.TryGetValue(trigger, out List<RegisteredRisingEdgeAction>? actions))
                    {
                        _risingEdgeActions[trigger] = actions = [];
                        _risingEdgeFireCounts[trigger] = 0;
                    }

                    actions.Add(new RegisteredRisingEdgeAction(null, action, writes));
                }
            }

            // Register this player's live computes so their reads schedule recomputes.
            if (result.LiveComputes is { } perPlayerLiveComputes)
            {
                foreach (LiveComputeRegistration reg in perPlayerLiveComputes)
                {
                    RegisterLiveCompute(reg.Compute, reg.Reads);
                }
            }

            _materializedPlayers.Add(result);
            _materializedNodeList.AddRange(result.Nodes);
            _materializedEdgeDescriptors.AddRange(result.EdgeDescriptors);

            if (EvaluatorEventSource.Log.IsEnabled())
            {
                EvaluatorEventSource.Log.PlayerMaterialized(slot, playerName, tplIdx);
            }
        }

        ResortAllSlots();
    }

    private void ProcessWrittenNodes(bool[]? snapshotDirty = null,
        Dictionary<StateNode, int>? nodeToIndex = null)
    {
        foreach (StateNode node in _writtenBatch)
        {
            UpdateDispatchKeysForNode(node);
            MarkLogicDependentsDirty(node);
            MarkLiveComputeDirty(node);
            if (snapshotDirty is not null && nodeToIndex is not null
                                          && nodeToIndex.TryGetValue(node, out int idx))
            {
                snapshotDirty[idx] = true;
            }
        }

        _writtenBatch.Clear();
    }

    // Routes a rising-edge action's declared write through the dirty pipeline.
    // Mirrors ProcessWrittenNodes for a single node (dispatch-key liveness, logic-dependent
    // dirty-marking, snapshot dirty bit) and additionally enqueues the dependents for recompute
    // in the CURRENT message's drain loop — rising-edge writes happen inside the logic pass,
    // after the per-message conjunction buckets have already been walked, so without the enqueue
    // a later-ordered reader would not see the write until some future message.
    private void RouteRisingEdgeWrite(StateNode written, SnapshotState? snap)
    {
        UpdateDispatchKeysForNode(written);
        snap?.MarkDirty(written);

        // A live compute reading this rising-edge counter must recompute against its
        // POST-write value. Scheduling it here, then firing only
        // after the logic queue drains, guarantees the compute observes the final counter value.
        MarkLiveComputeDirty(written);

        if (!_nodeToLogicDependents.TryGetValue(written, out List<object>? dependents))
        {
            return;
        }

        foreach (object dep in dependents)
        {
            if (dep is ConjunctionNode cj)
            {
                cj.MarkInputsDirty();
            }
            else if (dep is DisjunctionNode dj)
            {
                dj.MarkInputsDirty();
            }

            _pendingLogicRecompute.Add(dep);
        }
    }

    private void RebuildLiveDispatchKeys()
    {
        _liveDispatchKeys.Clear();
        foreach ((Type key, HashSet<StateNode> sources) in _dispatchKeyToSources)
        {
            foreach (StateNode source in sources)
            {
                if (source.IsActive)
                {
                    _liveDispatchKeys.Add(key);
                    break;
                }
            }
        }
    }

    private void RecomputeDirtyLogicNodes()
    {
        foreach ((StateNode node, List<object> dependents) in _nodeToLogicDependents)
        {
            foreach (object dep in dependents)
            {
                if (dep is ConjunctionNode { IsActive: true } cj)
                {
                    cj.MarkInputsDirty();
                    cj.Recompute();
                }
                else if (dep is DisjunctionNode { IsActive: true } dj)
                {
                    dj.MarkInputsDirty();
                    dj.Recompute();
                }
            }
        }
    }

    private void RecomputeLogicNode(BoolNode logicNode,
        List<RuleChainEvent> events, int frameIdx, int tick,
        bool verbose, ref int logicRecomputed, SnapshotState? snap)
    {
        bool wasSatisfied = logicNode.IsActive;
        bool risingEdge;

        if (logicNode is ConjunctionNode cj)
        {
            risingEdge = cj.Recompute();
        }
        else if (logicNode is DisjunctionNode dj)
        {
            risingEdge = dj.Recompute();
        }
        else
        {
            return;
        }

        logicRecomputed++;

        if (verbose)
        {
            EvaluatorEventSource.Log.LogicNodeRecomputed(
                GetOrRegisterNodeId(logicNode), logicNode.IsActive, risingEdge);
        }

        if (risingEdge)
        {
            events.Add(_chainNodePlayers.TryGetValue(logicNode, out (int Slot, string Name) owner)
                ? new RuleChainEvent(logicNode.Name, frameIdx, tick, owner.Slot, owner.Name)
                : new RuleChainEvent(logicNode.Name, frameIdx, tick));

            // Multi-action per trigger, guarded by the per-message once-fired
            // latch — the dirty routing below can recompute this node again mid-drain, and a
            // true→false→true flip within one message would otherwise double-fire the actions.
            if (_risingEdgeActions.TryGetValue(logicNode, out List<RegisteredRisingEdgeAction>? actions)
                && _risingEdgeFiredThisMessage.Add(logicNode))
            {
                foreach ((Action? invoke, Action<int, int>? invokeWithContext, StateNode? writes) in actions)
                {
                    // A1: the context arm gets the firing site's (frameIdx, tick) — the same frame
                    // clock values stamped into the RuleChainEvent above. Exactly one arm is set.
                    invoke?.Invoke();
                    invokeWithContext?.Invoke(frameIdx, tick);
                    // The action's write routes through the dirty pipeline like
                    // any edge write — snapshot dirty-tracking (on_satisfied counters otherwise
                    // project their initial value forever — the old undeclared-write bug),
                    // dispatch-key liveness, and logic-dependent recompute, so a later-ordered
                    // reader in the SAME message sees the written value.
                    if (writes is { } written)
                    {
                        RouteRisingEdgeWrite(written, snap);
                    }
                }

                _risingEdgeFireCounts[logicNode]++;
            }
        }

        // A state flip must mark the logic node's OWN snapshot cell dirty — logic nodes are not
        // edge-written, so nothing else re-reads them (auto-activate bools like HasKAST or an
        // achievement's Achieved column otherwise project null forever).
        if (logicNode.IsActive != wasSatisfied)
        {
            snap?.MarkDirty(logicNode);
        }

        if (logicNode.IsActive != wasSatisfied &&
            _nodeToLogicDependents.TryGetValue(logicNode, out List<object>? dependents))
        {
            foreach (object dep in dependents)
            {
                if (dep is ConjunctionNode depCj)
                {
                    depCj.MarkInputsDirty();
                }
                else if (dep is DisjunctionNode depDj)
                {
                    depDj.MarkInputsDirty();
                }

                _pendingLogicRecompute.Add(dep);
            }
        }
    }

    private void RegisterConjunction(ConjunctionNode cj, IReadOnlyList<StateEdge> materializedEdges)
        => RegisterLogicNode(cj, cj.Inputs, materializedEdges, _conjunctionIndex);

    private void RegisterDisjunction(DisjunctionNode dj, IReadOnlyList<StateEdge> materializedEdges)
        => RegisterLogicNode(dj, dj.Inputs, materializedEdges, _disjunctionIndex);

    // The shared per-player registration core (RegisterConjunction/RegisterDisjunction were
    // copy-paste twins that BOTH carried the same indexing gap: they matched
    // inputs against edge.WrittenNode only, so an input reading a node written ONLY via
    // AdditionalWrittenNodes never bucketed the logic node under the writing edge's dispatch
    // key — mirror of the constructor-time BuildLogicNodeIndex gap, fixed the same way.
    private void RegisterLogicNode<T>(T node, IReadOnlyList<IConditionalEdge> inputs,
        IReadOnlyList<StateEdge> materializedEdges, Dictionary<Type, List<T>> index) where T : StateNode
    {
        foreach (IConditionalEdge input in inputs)
        {
            // Union ALL of the input's sources (single-source inputs yield [Source]).
            foreach (StateNode source in input.Sources)
            {
                AddLogicDependent(source, node);
                foreach (StateEdge edge in materializedEdges)
                {
                    if (!EdgeWrites(edge, source))
                    {
                        continue;
                    }

                    if (!index.TryGetValue(edge.MessageType, out List<T>? bucket))
                    {
                        index[edge.MessageType] = bucket = [];
                    }

                    if (!bucket.Contains(node))
                    {
                        bucket.Add(node);
                    }
                }
            }
        }
    }

    // True when the edge declares `node` among its written nodes (primary or additional).
    private static bool EdgeWrites(StateEdge edge, StateNode node)
    {
        if (ReferenceEquals(edge.WrittenNode, node))
        {
            return true;
        }

        if (edge.AdditionalWrittenNodes is { } additional)
        {
            foreach (StateNode extra in additional)
            {
                if (ReferenceEquals(extra, node))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private void RegisterEdge(StateEdge edge)
    {
        if (!_edgeIndex.TryGetValue(edge.MessageType, out List<StateEdge>? list))
        {
            _edgeIndex[edge.MessageType] = list = [];
        }

        list.Add(edge);
        TrackEdgeForDispatchFilter(edge);
        if (edge.Source.IsActive)
        {
            _liveDispatchKeys.Add(edge.MessageType);
        }

        if (edge.WrittenNode is ITransientNode transient)
        {
            RegisterTransientNode(edge.MessageType, transient);
        }

        if (edge.AdditionalWrittenNodes is { } additionalNodes)
        {
            foreach (StateNode node in additionalNodes)
            {
                if (node is ITransientNode additionalTransient)
                {
                    RegisterTransientNode(edge.MessageType, additionalTransient);
                }
            }
        }
    }

    private void RegisterTransientNode(Type dispatchKey, ITransientNode node)
    {
        if (!_transientNodesPerKey.TryGetValue(dispatchKey, out List<ITransientNode>? list))
        {
            _transientNodesPerKey[dispatchKey] = list = [];
        }

        if (!list.Contains(node))
        {
            list.Add(node);
        }
    }

    // ── Round-scoped reset ────────────────────────────────────────────────────

    private void ResetRoundScopedNodes()
    {
        foreach (IRoundScopedNode node in _roundScopedNodes)
        {
            node.Reset();
            if (node is StateNode sn)
            {
                MarkLogicDependentsDirty(sn);
                // A round reset zeroes round-scoped counters a live compute reads, so
                // schedule the compute to recompute against the reset value (this message's settle
                // picks it up, since ResetRoundScopedNodes runs just before this message's logic pass).
                MarkLiveComputeDirty(sn);
            }
            else if (node is RoundScopedLogicNodeReset adapter)
            {
                MarkLogicDependentsDirty(adapter.WrappedNode);
                MarkLiveComputeDirty(adapter.WrappedNode);
            }
        }

        RecomputeDirtyLogicNodes();

        RebuildLiveDispatchKeys();

        if (EvaluatorEventSource.Log.IsEnabled())
        {
            EvaluatorEventSource.Log.RoundReset(_roundScopedNodes.Count);
        }

        // Debug-level (source-gen guarded): the coarse per-round marker for the human log stream.
        EvaluatorLog.RoundReset(_log, _roundScopedNodes.Count);
    }

    // ── Match-restart reset ───────────────────────────────────────────────────

    /// <summary>
    ///     Runs when <c>begin_new_match</c> fires: the server is (re)starting the match, so
    ///     everything match-scoped stats have accumulated is discarded back to the materialization
    ///     baseline. On the common single-<c>begin_new_match</c> demo this is a no-op by
    ///     construction — the event precedes round 1's freeze-end and warmup accumulation is
    ///     already suppressed — but on a demo whose server restarts after a warmup/knife round
    ///     (measured: vitality-vs-fut-m3-nuke, <c>begin_new_match</c> at ticks 346 AND 4506) the
    ///     knife round's kills, deaths and round win otherwise count into the real match's totals
    ///     (a 24-round match scored 14–11 across "25" rounds).
    ///     <para>
    ///         Scope: per-player template nodes, round-scoped state, and
    ///         <see cref="PlayerContextIndex" /> round state. Game-scoped built-in context rules
    ///         reset declaratively via their own <c>$match_start</c> triggers (see
    ///         <c>BuiltinContexts</c>). Game-scoped v2 (<c>for: match</c>) stats are deliberately
    ///         untouched — no baseline exists for them, and fabricating one is worse than
    ///         documenting the gap.
    ///     </para>
    /// </summary>
    private void ResetForMatchRestart(SnapshotState? snap)
    {
        // Round machinery first: round-scoped nodes, first-wins guards, and their logic
        // dependents re-arm exactly as at a round boundary.
        ResetRoundScopedNodes();
        snap?.MarkRoundScopedDirty(_roundScopedNodes);

        foreach ((StateNode node, Action restore) in _matchRestartBaselines)
        {
            restore();
            MarkLogicDependentsDirty(node);
            MarkLiveComputeDirty(node);
            snap?.MarkDirty(node);
        }

        RecomputeDirtyLogicNodes();
        RebuildLiveDispatchKeys();

        _playerContextIndex?.ResetForMatchRestart();

        if (EvaluatorEventSource.Log.IsEnabled())
        {
            EvaluatorEventSource.Log.RoundReset(_matchRestartBaselines.Count);
        }
    }

    /// <summary>
    ///     Builds the restore action that returns <paramref name="node" /> to its
    ///     materialization-time state on a match restart, or <c>null</c> for nodes with nothing
    ///     match-accumulated: live derivations recompute from already-reset sources, entity pulls
    ///     read scanner state, and round-scoped nodes re-arm through the round machinery the
    ///     restart also runs.
    /// </summary>
    private static Action? CreateMatchRestartRestorer(StateNode node) => node switch
    {
        // Order matters: several of these are ValueNode<T> subclasses and must not fall through
        // to the generic capture arms.
        IRoundScopedNode => null,
        EntityValuePullNode => null,
        RoundTeamAggregateNode => null,
        KeyedRatioNode => null,
        KeyedCounterNode keyed => keyed.ResetForMatchRestart,
        ValueNode<int> v => CaptureValueBaseline(v),
        ValueNode<double> v => CaptureValueBaseline(v),
        ValueNode<float> v => CaptureValueBaseline(v),
        ValueNode<bool> v => CaptureValueBaseline(v), // BoolNode + conjunction/disjunction logic
        ValueNode<string> v => CaptureValueBaseline(v),
        ValueNode<IReadOnlyList<int>> v => CaptureValueBaseline(v), // appends are copy-on-write
        _ => null
    };

    /// <summary>
    ///     Captures the node's current (materialization-time) value and set-ness. Restoring an
    ///     unset node goes through <see cref="ValueNode{T}.ResetToUnset" /> — <c>SetValue</c>
    ///     latches activation, so restoring a never-set capture stat via it would fabricate an
    ///     active default where the projector should render "no value".
    /// </summary>
    private static Action CaptureValueBaseline<T>(ValueNode<T> node)
    {
        T initialValue = node.Value;
        bool wasSet = node.HasEverBeenSet;
        return () =>
        {
            if (wasSet)
            {
                node.SetValue(initialValue);
            }
            else
            {
                node.ResetToUnset();
            }
        };
    }

    private string ResolvePlayerName(int slot)
    {
        if (_demo?.Players.TryGetValue(slot, out PlayerInfo? info) == true && info.Name.Length > 0)
        {
            return info.Name;
        }

        return $"Player {slot}";
    }

    private void ResortAllSlots()
    {
        TopologicalSortEdges(_edgeIndex);
    }

    private static void TopologicalSortEdges(Dictionary<Type, List<StateEdge>> index)
    {
        foreach ((Type dispatchKey, List<StateEdge> list) in index)
        {
            if (list.Count <= 1)
            {
                continue;
            }

            int n = list.Count;
            List<int>[] adjForward = new List<int>[n];
            int[] inDegree = new int[n];
            for (int i = 0; i < n; i++)
            {
                adjForward[i] = [];
            }

            Dictionary<StateNode, List<int>> sourceToEdges = new(ReferenceEqualityComparer.Instance);
            for (int i = 0; i < n; i++)
            {
                if (!sourceToEdges.TryGetValue(list[i].Source, out List<int>? readers))
                {
                    sourceToEdges[list[i].Source] = readers = [];
                }

                readers.Add(i);
            }

            // Declared reads join the readers map, so a reader of node X is
            // ordered after X's Activate/SetValue writers (and before X's Deactivate writers)
            // exactly like an edge that Sources from X. Source-encoding stays where it exists —
            // a node declared here that is also the edge's Source is skipped (the constraint
            // already exists; duplicates would be harmless but are pointless parallel edges).
            // The v1 builder emits no DeclaredReads, so this loop is a no-op on the v1 corpus.
            for (int i = 0; i < n; i++)
            {
                if (list[i].DeclaredReads is not { Count: > 0 } declaredReads)
                {
                    continue;
                }

                foreach (StateNode read in declaredReads)
                {
                    if (!sourceToEdges.TryGetValue(read, out List<int>? readers))
                    {
                        sourceToEdges[read] = readers = [];
                    }

                    if (!readers.Contains(i))
                    {
                        readers.Add(i);
                    }
                }
            }

            for (int wi = 0; wi < n; wi++)
            {
                EdgeEffect? effect = list[wi].DeclaredEffect;
                bool isDeactivate = effect == EdgeEffect.Deactivate;

                ApplyWriteOrdering(list[wi].WrittenNode, wi, sourceToEdges, adjForward, inDegree, isDeactivate);

                if (list[wi].AdditionalWrittenNodes is { } additional)
                {
                    foreach (StateNode extra in additional)
                    {
                        ApplyWriteOrdering(extra, wi, sourceToEdges, adjForward, inDegree, isDeactivate);
                    }
                }

                if (effect is null && list[wi].WrittenNode is { } wn)
                {
                    if (EvaluatorEventSource.Log.IsEnabled())
                    {
                        EvaluatorEventSource.Log.UndeclaredEdgeEffect(list[wi].Source.Name, wn.Name);
                    }

                    // Warning (source-gen guarded) — surfaced in the human log stream too; static context,
                    // so it uses the shared cached logger rather than a per-instance one.
                    EvaluatorLog.UndeclaredEdgeEffect(EvaluatorLog.Shared, list[wi].Source.Name, wn.Name);
                }
            }

            bool hasDeps = false;
            for (int i = 0; i < n; i++)
            {
                if (inDegree[i] > 0)
                {
                    hasDeps = true;
                    break;
                }
            }

            if (!hasDeps)
            {
                continue;
            }

            Queue<int> queue = new();
            for (int i = 0; i < n; i++)
            {
                if (inDegree[i] == 0)
                {
                    queue.Enqueue(i);
                }
            }

            List<int> sorted = new(n);
            while (queue.Count > 0)
            {
                int idx = queue.Dequeue();
                sorted.Add(idx);
                foreach (int succ in adjForward[idx])
                {
                    if (--inDegree[succ] == 0)
                    {
                        queue.Enqueue(succ);
                    }
                }
            }

            if (sorted.Count < n)
            {
                // A same-event read cycle has no valid order — every rule in the
                // cycle reads a stat another one writes on the same event. Name each cycle
                // member by the stat it writes plus the stats it reads, and point at the
                // `after: <stat>` fix-it (the explicit tie-break) so the author can break the
                // cycle without guessing which constraint to remove.
                IEnumerable<string> cycleEdges = Enumerable.Range(0, n)
                    .Where(i => !sorted.Contains(i))
                    .Select(i =>
                    {
                        StateEdge e = list[i];
                        IEnumerable<string> reads = e.DeclaredReads is { Count: > 0 } dr
                            ? new[]
                            {
                                e.Source.Name
                            }.Concat(dr.Select(r => r.Name)).Distinct()
                            : [e.Source.Name];
                        return $"'{e.WrittenNode?.Name ?? "?"}' (writes '{e.WrittenNode?.Name ?? "?"}', "
                               + $"reads {string.Join(", ", reads.Select(r => $"'{r}'"))})";
                    });
                throw new InvalidOperationException(
                    $"Same-event read cycle in dispatch slot '{dispatchKey.Name}' between "
                    + $"{string.Join(" and ", cycleEdges)}: each stat reads a stat the other writes on the "
                    + "same event, so no evaluation order satisfies both. Break the tie explicitly by adding "
                    + "`after: <stat>` to the rule that should read the other's previous value.");
            }

            StateEdge[] temp = new StateEdge[n];
            for (int i = 0; i < n; i++)
            {
                temp[i] = list[sorted[i]];
            }

            list.Clear();
            list.AddRange(temp);

            if (EvaluatorEventSource.Log.IsEnabled())
            {
                EvaluatorEventSource.Log.DispatchSlotSorted(dispatchKey.Name, n,
                    adjForward.Sum(a => a.Count));
            }
        }
    }

    private void TrackEdgeForDispatchFilter(StateEdge edge)
    {
        Type key = edge.MessageType;
        StateNode source = edge.Source;

        if (!_sourceToDispatchKeys.TryGetValue(source, out HashSet<Type>? keys))
        {
            _sourceToDispatchKeys[source] = keys = [];
        }

        keys.Add(key);

        if (!_dispatchKeyToSources.TryGetValue(key, out HashSet<StateNode>? sources))
        {
            _dispatchKeyToSources[key] = sources = [];
        }

        sources.Add(source);
    }

    private void UpdateDispatchKeysForNode(StateNode node)
    {
        if (!_sourceToDispatchKeys.TryGetValue(node, out HashSet<Type>? keys))
        {
            return;
        }

        if (node.IsActive)
        {
            foreach (Type key in keys)
            {
                _liveDispatchKeys.Add(key);
            }
        }
        else
        {
            foreach (Type key in keys)
            {
                if (!_dispatchKeyToSources.TryGetValue(key, out HashSet<StateNode>? sources))
                {
                    continue;
                }

                bool anyActive = false;
                foreach (StateNode s in sources)
                {
                    if (s.IsActive)
                    {
                        anyActive = true;
                        break;
                    }
                }

                if (!anyActive)
                {
                    _liveDispatchKeys.Remove(key);
                }
            }
        }
    }

    /// <summary>
    ///     One registered rising-edge action: exactly one
    ///     of <paramref name="Invoke" /> (the plain arm) or <paramref name="InvokeWithContext" />
    ///     (the context arm, handed the firing <c>(frameIdx, tick)</c>) is non-null.
    /// </summary>
    private readonly record struct RegisteredRisingEdgeAction(
        Action? Invoke,
        Action<int, int>? InvokeWithContext,
        StateNode? Writes);

    /// <summary>
    ///     All snapshot-mode bookkeeping, in one place: the tracked-node list (static nodes + nodes
    ///     appended as players materialize), the per-node dirty flags, the unchanged-row reference
    ///     sharing, and the applied-edge recording. Null on the bare path.
    /// </summary>
    private sealed class SnapshotState
    {
        private bool _anyDirty = true;

        // How many entries of the evaluator's materialized-node list have been examined by
        // TrackNewlyMaterializedNodes (tracked OR skipped as snapshot-excluded).
        private int _consumedMaterializedCount;
        private NodeSnapshot[]?[]? _prevChunks;
        private readonly int _staticCount;

        public SnapshotState(IReadOnlyList<StateNode> staticTrackedNodes, int messageCapacity)
        {
            _staticCount = staticTrackedNodes.Count;
            TrackedNodes = new List<StateNode>(staticTrackedNodes);
            Snapshots = new List<NodeSnapshot[]?[]>(messageCapacity);
            Messages = new List<(DemoFrame, NetMessage)>(messageCapacity);

            NodeToIndex = new Dictionary<StateNode, int>(ReferenceEqualityComparer.Instance);
            for (int i = 0; i < staticTrackedNodes.Count; i++)
            {
                NodeToIndex[staticTrackedNodes[i]] = i;
            }

            Dirty = new bool[Math.Max(1024, staticTrackedNodes.Count)];
            Array.Fill(Dirty, true, 0, staticTrackedNodes.Count);
        }

        /// <summary>Static nodes + per-player nodes appended as they materialize (column authority).</summary>
        public List<StateNode> TrackedNodes { get; }

        /// <summary>At-materialization default per appended node — pads pre-materialization rows.</summary>
        public List<NodeSnapshot> NodeDefaults { get; } = new();

        /// <summary>Tracked node → snapshot column, by reference identity.</summary>
        public Dictionary<StateNode, int> NodeToIndex { get; }

        /// <summary>Per-column dirty flags since the last captured row (grown on materialization).</summary>
        public bool[] Dirty { get; private set; }

        /// <summary>
        ///     One chunk-array row per message, in message order (rows shared by reference when
        ///     unchanged; within a changed row, clean chunks stay shared with the previous row —
        ///     see <see cref="SnapshotTable" />). A null chunk slot means "no column in this chunk
        ///     was ever dirty at this row" — the table reader serves defaults for it.
        /// </summary>
        public List<NodeSnapshot[]?[]> Snapshots { get; }

        /// <summary>The (frame, message) pair for each snapshot row.</summary>
        public List<(DemoFrame, NetMessage)> Messages { get; }

        /// <summary>Per-edge fired message indices (graph-breakpoint support).</summary>
        public Dictionary<StateEdge, List<int>> AppliedByEdge { get; } =
            new(ReferenceEqualityComparer.Instance);

        /// <summary>
        ///     Appends any nodes the last materialization added (skipping snapshot-excluded ones:
        ///     transients and keyed counters), assigning their snapshot columns and recording their
        ///     at-materialization defaults.
        /// </summary>
        public void TrackNewlyMaterializedNodes(List<StateNode> materializedNodeList)
        {
            // Everything past the consumed-prefix cursor in the materialized list is new this
            // message. The cursor must be explicit: deriving it from TrackedNodes.Count (the old
            // form) undercounts once a node is SKIPPED, re-consuming (and double-tracking) the
            // tail of the previous player's nodes for every later player.
            for (int i = _consumedMaterializedCount; i < materializedNodeList.Count; i++)
            {
                StateNode node = materializedNodeList[i];
                if (node is ISnapshotExcludedNode)
                {
                    continue;
                }

                int idx = TrackedNodes.Count;
                TrackedNodes.Add(node);
                NodeToIndex[node] = idx;
                NodeDefaults.Add(new NodeSnapshot(node.IsActive, node.GetDisplayValue(), node.GetNumericValue()));
                if (idx >= Dirty.Length)
                {
                    bool[] grown = Dirty;
                    Array.Resize(ref grown, grown.Length * 2);
                    Dirty = grown;
                }

                Dirty[idx] = true;
                _anyDirty = true;
            }

            _consumedMaterializedCount = materializedNodeList.Count;
        }

        /// <summary>
        ///     Marks one node's snapshot column dirty (logic-node flips and rising-edge action
        ///     writes — mutations that don't flow through the edge written-batch).
        /// </summary>
        public void MarkDirty(StateNode node)
        {
            if (NodeToIndex.TryGetValue(node, out int idx))
            {
                Dirty[idx] = true;
                _anyDirty = true;
            }
        }

        /// <summary>
        ///     Marks every round-scoped node's column dirty after a round reset — including logic
        ///     nodes deactivated via <see cref="Nodes.RoundScopedLogicNodeReset" /> wrappers (the
        ///     node itself is not IRoundScopedNode, so the tracked walk alone misses it and an
        ///     achieved-style bool would linger active in later rounds' snapshots).
        /// </summary>
        public void MarkRoundScopedDirty(IReadOnlyList<IRoundScopedNode> roundScoped)
        {
            for (int i = 0; i < TrackedNodes.Count; i++)
            {
                if (TrackedNodes[i] is IRoundScopedNode)
                {
                    Dirty[i] = true;
                }
            }

            foreach (IRoundScopedNode rs in roundScoped)
            {
                if (rs is RoundScopedLogicNodeReset wrapper)
                {
                    MarkDirty(wrapper.WrappedNode);
                }
            }

            _anyDirty = true;
        }

        /// <summary>
        ///     Captures the post-message row: unchanged state re-uses the previous row's chunk array
        ///     by reference (the 32x snapshot win); otherwise the row clones ONLY the chunks holding
        ///     dirty columns (clean chunks stay shared with the previous row), then re-reads the
        ///     dirty columns. The old form cloned the full tracked width per dirty message — that
        ///     was the dominant eval allocation once per-player highlight nodes tripled the width.
        /// </summary>
        public void CaptureAfterMessage(DemoFrame frame, NetMessage message, bool anyEdgeFired)
        {
            if (anyEdgeFired)
            {
                _anyDirty = true;
            }

            int nodeCount = TrackedNodes.Count;
            if (_prevChunks is not null && !_anyDirty)
            {
                Snapshots.Add(_prevChunks);
            }
            else
            {
                int chunkCount = (nodeCount + SnapshotTable.ChunkMask) >> SnapshotTable.ChunkShift;
                NodeSnapshot[]?[] chunks = new NodeSnapshot[]?[chunkCount];
                int prevCount = 0;
                if (_prevChunks is not null)
                {
                    prevCount = Math.Min(_prevChunks.Length, chunkCount);
                    Array.Copy(_prevChunks, chunks, prevCount);
                }

                for (int i = 0; i < nodeCount; i++)
                {
                    if (!Dirty[i])
                    {
                        continue;
                    }

                    int c = i >> SnapshotTable.ChunkShift;
                    int baseCol = c << SnapshotTable.ChunkShift;
                    int len = Math.Min(SnapshotTable.ChunkSize, nodeCount - baseCol);
                    NodeSnapshot[]? owned = chunks[c];
                    bool sharedWithPrev = c < prevCount && ReferenceEquals(owned, _prevChunks![c]);
                    if (owned is null || sharedWithPrev || owned.Length < len)
                    {
                        NodeSnapshot[] fresh = new NodeSnapshot[len];
                        int copied = 0;
                        if (owned is not null)
                        {
                            copied = Math.Min(owned.Length, len);
                            Array.Copy(owned, fresh, copied);
                        }

                        // Cells newly covered by this row (grown width): late columns start at
                        // their at-materialization default — the same value the table reader
                        // serves for rows that never covered them.
                        for (int j = copied; j < len; j++)
                        {
                            int col = baseCol + j;
                            fresh[j] = col >= _staticCount && col - _staticCount < NodeDefaults.Count
                                ? NodeDefaults[col - _staticCount]
                                : default;
                        }

                        chunks[c] = fresh;
                    }

                    chunks[c]![i & SnapshotTable.ChunkMask] = new NodeSnapshot(
                        TrackedNodes[i].IsActive, TrackedNodes[i].GetDisplayValue(), TrackedNodes[i].GetNumericValue());
                }

                Array.Clear(Dirty, 0, nodeCount);
                _prevChunks = chunks;
                Snapshots.Add(chunks);
            }

            _anyDirty = false;
            Messages.Add((frame, message));
        }
    }
}

/// <summary>Full result from <see cref="StateGraphEvaluator.EvaluateWithSnapshots" />.</summary>
public sealed record EvaluationResult(
    RuleChainTimeline Timeline,
    SnapshotTable MessageSnapshots,
    IReadOnlyList<(DemoFrame Frame, NetMessage Message)> Messages,
    IReadOnlyList<StateNode> FinalTrackedNodes,
    IReadOnlyList<PerPlayerNodeTemplate.MaterializedPlayer> MaterializedPlayers,
    IReadOnlyList<GraphEdgeDescriptor> MaterializedEdgeDescriptors,
    // For each StateEdge that fired ≥ once, the sorted list of global message indices (into Messages
    // / MessageSnapshots) at which it applied. Drives edge graph-breakpoints — a clicked edge resolves
    // to its StateEdge via BuildResult.EdgeBacking. Null/empty on the bench Evaluate path.
    IReadOnlyDictionary<StateEdge, List<int>>? AppliedMessagesByEdge = null);
