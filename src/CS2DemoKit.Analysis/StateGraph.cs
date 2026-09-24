#region

using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Analysis.Nodes;

#endregion

namespace CS2DemoKit.Analysis;

/// <summary>
///     A "live compute" registration: a <see cref="ComputedStatNode" /> that
///     re-evaluates LIVE (during the eval loop's dirty-settle stage) whenever any of its declared
///     <paramref name="Reads" /> — the sibling/context/counter nodes its formula reads — go dirty,
///     instead of once at round end. The evaluator maps each read node to the compute so a write to
///     any read schedules exactly one recompute per message (see the hard cap in
///     <see cref="StateGraphEvaluator" />).
/// </summary>
/// <param name="Compute">The compute node to recompute live.</param>
/// <param name="Reads">The graph nodes the compute's formula reads (its live dependency set).</param>
public readonly record struct LiveComputeRegistration(
    ComputedStatNode Compute,
    IReadOnlyList<StateNode> Reads);

/// <summary>
///     A mutable container of <see cref="StateEdge" />s and <see cref="ConjunctionNode" />s that
///     together define the analysis graph. Pass to <see cref="StateGraphEvaluator" /> to run
///     against a demo.
/// </summary>
public sealed class StateGraph
{
    private readonly List<ConjunctionNode> _conjunctions = [];
    private readonly Dictionary<StateNode, List<(Action<int, int> Invoke, StateNode? Writes)>> _contextRisingEdgeActions = new(ReferenceEqualityComparer.Instance);
    private readonly List<DisjunctionNode> _disjunctions = [];
    private readonly List<StateEdge> _edges = [];
    private readonly List<LiveComputeRegistration> _liveComputes = [];
    private readonly List<PerPlayerNodeTemplate> _perPlayerTemplates = [];
    private readonly List<StateNode> _ruleNodes = [];
    private List<(StateNode Node, Action Restore)>? _ruleNodeBaselines;
    private readonly Dictionary<StateNode, List<(Action Invoke, StateNode? Writes)>> _risingEdgeActions = new(ReferenceEqualityComparer.Instance);
    internal IReadOnlyList<ConjunctionNode> ConjunctionNodes => _conjunctions;
    internal IReadOnlyList<DisjunctionNode> DisjunctionNodes => _disjunctions;

    internal IReadOnlyList<StateEdge> Edges => _edges;
    internal IReadOnlyList<PerPlayerNodeTemplate> PerPlayerTemplates => _perPlayerTemplates;
    internal IReadOnlyDictionary<StateNode, List<(Action Invoke, StateNode? Writes)>> RisingEdgeActions => _risingEdgeActions;

    /// <summary>
    ///     The context-arm rising-edge registrations (
    ///     <see cref="AddRisingEdgeAction(StateNode, Action{int, int}, StateNode?)" />).
    ///     Kept separate from <see cref="RisingEdgeActions" /> so existing plain-<see cref="Action" />
    ///     registrants are untouched; the evaluator merges both per trigger (plain first).
    /// </summary>
    internal IReadOnlyDictionary<StateNode, List<(Action<int, int> Invoke, StateNode? Writes)>> ContextRisingEdgeActions => _contextRisingEdgeActions;

    /// <summary>
    ///     The shared collector the v2 highlight emission closures append <see cref="HighlightFired" />
    ///     records to (Highlights pipeline work item A1). Owned by the graph because the emission
    ///     closures are created at build/materialization time (they cannot see the evaluator); the
    ///     evaluator clears it at the start of every evaluation and snapshots it at the end — so a
    ///     graph re-evaluated (or shared across sequential evaluators) never accumulates stale
    ///     firings. Concurrent evaluation of one graph is unsupported, as it always was (game-scoped
    ///     nodes are shared mutable state).
    /// </summary>
    internal List<HighlightFired> HighlightSink { get; } = [];

    /// <summary>Graph-scoped live computes (per-player ones ride the materialized player).</summary>
    internal IReadOnlyList<LiveComputeRegistration> LiveComputes => _liveComputes;

    /// <summary>
    ///     The static nodes a ruleset built directly onto the graph (a <c>for: match</c> or
    ///     <c>for: each_team</c> stat, its guards and companions), as opposed to a per-player template
    ///     that materializes during evaluation. The evaluator gives them what a materialized node
    ///     gets: the round reset for the round-scoped ones, and a restore to the build-time value on
    ///     a match restart. Built-in context rules are not listed; they reset through their own
    ///     triggers.
    /// </summary>
    internal IReadOnlyList<StateNode> RuleNodes => _ruleNodes;

    /// <summary>
    ///     True when the evaluator must register a player context for every slot an event names even
    ///     though no per-player template exists: a build whose rulesets are all <c>for: match</c> or
    ///     <c>for: each_team</c> still needs live teams and alive state for the enrichments, the team
    ///     aggregates and the round-end winner.
    /// </summary>
    public bool TracksPlayers { get; set; }

    /// <summary>
    ///     The always-active entry node. All entry edges (those with no prerequisite) should
    ///     use this as their source to keep the graph fully connected.
    /// </summary>
    public RootNode Root { get; } = new();

    /// <summary>
    ///     Adds a conjunction node to the graph. Returns <c>this</c> for fluent chaining.
    /// </summary>
    public StateGraph AddConjunction(ConjunctionNode node)
    {
        _conjunctions.Add(node);
        return this;
    }

    /// <summary>Adds a disjunction (OR) node. Returns <c>this</c> for fluent chaining.</summary>
    public StateGraph AddDisjunction(DisjunctionNode node)
    {
        _disjunctions.Add(node);
        return this;
    }

    /// <summary>Adds an edge to the graph. Returns <c>this</c> for fluent chaining.</summary>
    public StateGraph AddEdge(StateEdge edge)
    {
        _edges.Add(edge);
        return this;
    }

    /// <summary>
    ///     Adds a per-player node template. During evaluation, the evaluator materializes
    ///     concrete nodes and edges from this template for each newly-discovered player slot.
    /// </summary>
    public StateGraph AddPerPlayerTemplate(PerPlayerNodeTemplate template)
    {
        _perPlayerTemplates.Add(template);
        return this;
    }

    /// <summary>
    ///     Installs a callback to invoke exactly on the rising edge of <paramref name="trigger" />
    ///     (at most once per message — the evaluator's once-fired latch). Used by counter rules'
    ///     <c>OnSatisfied</c> increments and similar one-shot actions. Registrations
    ///     are additive — a second action on the same trigger fires alongside the first (this used
    ///     to silently last-wins).
    /// </summary>
    public StateGraph AddRisingEdgeAction(StateNode trigger, Action action, StateNode? writes = null)
    {
        // The written node must be declared so the write routes through the dirty pipeline
        // (snapshot dirty-tracking, logic-dependent recompute, dispatch-key liveness — the same
        // undeclared-write bug class as ComputeOnRoundEndEdge: an on_satisfied counter otherwise
        // projects its initial value forever and same-message readers never see it).
        if (!_risingEdgeActions.TryGetValue(trigger, out List<(Action Invoke, StateNode? Writes)>? actions))
        {
            _risingEdgeActions[trigger] = actions = [];
        }

        actions.Add((action, writes));
        return this;
    }

    /// <summary>
    ///     Installs a context-arm rising-edge callback: like
    ///     <see cref="AddRisingEdgeAction(StateNode, Action, StateNode?)" /> but the evaluator passes
    ///     the firing site's <c>(frameIndex, tick)</c> — the zero-based demo frame index and that
    ///     frame's <c>ServerTick</c> (frame clock, the same values a <c>RuleChainEvent</c> is stamped
    ///     with). Additive alongside plain-<see cref="Action" /> registrations on the same trigger:
    ///     for one trigger, plain actions fire first (in registration order), then context actions —
    ///     all under the same per-message once-fired latch. Used by the v2 highlight emission (A1) to
    ///     collect <see cref="HighlightFired" /> records without threading evaluator state into the
    ///     builder's closures.
    /// </summary>
    public StateGraph AddRisingEdgeAction(StateNode trigger, Action<int, int> action, StateNode? writes = null)
    {
        if (!_contextRisingEdgeActions.TryGetValue(trigger, out List<(Action<int, int> Invoke, StateNode? Writes)>? actions))
        {
            _contextRisingEdgeActions[trigger] = actions = [];
        }

        actions.Add((action, writes));
        return this;
    }

    /// <summary>
    ///     Registers a static node a ruleset built onto the graph (see <see cref="RuleNodes" />), so
    ///     the evaluator resets it at each round boundary when it is round-scoped and restores it on
    ///     a match restart. Returns <c>this</c> for fluent chaining.
    /// </summary>
    public StateGraph AddRuleNode(StateNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        _ruleNodes.Add(node);
        _ruleNodeBaselines = null;
        return this;
    }

    /// <summary>
    ///     The match-restart baselines of <see cref="RuleNodes" />, captured once, on first use, from
    ///     the build-time values, and shared by every evaluator of this graph so a second evaluation
    ///     restores to the same values the first did.
    /// </summary>
    internal IReadOnlyList<(StateNode Node, Action Restore)> RuleNodeBaselines(Func<StateNode, Action?> capture)
    {
        if (_ruleNodeBaselines is null)
        {
            List<(StateNode, Action)> baselines = new(_ruleNodes.Count);
            foreach (StateNode node in _ruleNodes)
            {
                if (capture(node) is { } restore)
                {
                    baselines.Add((node, restore));
                }
            }

            _ruleNodeBaselines = baselines;
        }

        return _ruleNodeBaselines;
    }

    /// <summary>
    ///     Registers an opt-in live compute: <paramref name="compute" /> re-evaluates
    ///     during evaluation whenever any node in <paramref name="reads" /> is written, rather than
    ///     once at round end. A graph with zero live computes evaluates byte-identically to before this
    ///     API existed (the evaluator's dirty-settle interleave is gated on their presence).
    /// </summary>
    public StateGraph AddLiveCompute(ComputedStatNode compute, IReadOnlyList<StateNode> reads)
    {
        _liveComputes.Add(new LiveComputeRegistration(compute, reads));
        return this;
    }
}
