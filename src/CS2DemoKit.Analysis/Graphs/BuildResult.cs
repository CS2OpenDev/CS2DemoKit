#region

using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Analysis.Building;
using CS2DemoKit.Analysis.Config;
using CS2DemoKit.Analysis.Profiles;
using CS2DemoKit.Analysis.Registry;
using CS2DemoKit.Analysis.RulesetsV2.Resolve;

#endregion

namespace CS2DemoKit.Analysis.Graphs;

// EntityChangeScanner lives in the parent CS2DemoKit.Analysis namespace.

/// <summary>
///     The output of <see cref="RuleChainBuilder.Build" />: a ready-to-evaluate graph plus all metadata the runtime
///     needs.
/// </summary>
/// <param name="Graph">The fully-wired <see cref="StateGraph" /> with all nodes and edges registered.</param>
/// <param name="Nodes">All nodes in the graph in dependency-sorted order.</param>
/// <param name="Edges">
///     The game scope's descriptors: at least one for every edge on <see cref="Graph" />, drawn from
///     its real source, plus the game-scope wiring that is not an edge (see
///     <see cref="GraphEdgeDescriptor" /> and <see cref="GraphEdgeKind" />). A row can point at one
///     of <see cref="ExternalNodes" />. The per-player rows are on each materialised player.
/// </param>
/// <param name="RelevantMessageTypes">
///     Set of message types the graph subscribes to; the evaluator can short-circuit other
///     messages.
/// </param>
/// <param name="PlayerContextIndex">Per-player context tracking, when any rule needs cross-player state.</param>
/// <param name="EntityScanner">
///     Lazy entity-state scanner; <c>null</c> when no rule references any
///     <c>IEntityValueProvider</c>.
/// </param>
/// <param name="EdgeBacking">
///     Maps each game-scope descriptor that a <see cref="StateEdge" /> backs to that edge, by
///     reference identity; several rows map to one edge when it writes several nodes. Wiring with no
///     edge (logic inputs, rising-edge actions, pulls) is absent. Drives edge graph-breakpoints
///     (descriptor → <see cref="StateEdge" /> → <c>EvaluationResult.AppliedMessagesByEdge</c>).
///     <see cref="GraphEdgeDescriptor.Edge" /> carries the same edge on the row itself, and on the
///     per-player rows this map does not cover. <c>null</c> when no edge was backed.
/// </param>
/// <param name="GameNodesByRuleId">
///     Game-scoped rule-id → node map for configured-output metric resolution. Keys are bare rule
///     ids (mirroring the builder's lookup — a structurally-deduplicated rule resolves under every
///     declaring chain's rule id) plus chain-qualified <c>chain.rule</c> aliases for every game
///     chain's rules. Built-in context rules (e.g. <c>round_number</c>) appear under their bare id.
///     <c>null</c> when nothing was built (empty config).
/// </param>
/// <param name="Outputs">
///     The output-table declarations this build produced (v2 <c>show: tables</c> lowering).
///     Consumed by <c>AnalysisRun.ProjectConfiguredOutputs</c>. <c>null</c> when none.
/// </param>
/// <param name="RulesetCoverage">
///     Additive Rulesets v2 member: per-profile coverage skips
///     — v2 nodes whose view did not bind on the active demo-source profile, dropped rather than
///     silently zeroed. <c>null</c> for a pure-v1 build or a v2 build where every view bound.
///     Consumers surface it as a diagnostic row; never a silent zero.
/// </param>
public sealed record BuildResult(
    StateGraph Graph,
    IReadOnlyList<StateNode> Nodes,
    IReadOnlyList<GraphEdgeDescriptor> Edges,
    IReadOnlySet<Type> RelevantMessageTypes,
    PlayerContextIndex? PlayerContextIndex = null,
    // Lazy-activated entity-state scanner — null when no rule references any
    // registered IEntityValueProvider's ContextName. Bench-parity tripwire:
    // EntityIntegrationTests.EntityScanner_NotAllocated_WhenNoRulesReference.
    EntityChangeScanner? EntityScanner = null,
    IReadOnlyDictionary<GraphEdgeDescriptor, StateEdge>? EdgeBacking = null,
    IReadOnlyDictionary<string, StateNode>? GameNodesByRuleId = null,
    IReadOnlyList<OutputDef>? Outputs = null,
    IReadOnlyList<RulesetCoverageDiagnostic>? RulesetCoverage = null)
{
    /// <summary>
    ///     Every diagnostic the v2 composition step produced for this build, attributed to its
    ///     ruleset. Composition is tolerant: a ruleset that fails cross-reference
    ///     validation, resolution, or a cycle check is dropped and the rest still build — so a
    ///     consumer that never reads this cannot tell a rule that scored zero from a rule that was
    ///     never compiled ("silently-missing feats"). Empty for a build with no v2 documents, and
    ///     for a clean composition.
    ///     <para>
    ///         Populated by <c>DemoAnalysis.Build</c>, which owns the composition step. A caller
    ///         that composes itself and drives
    ///         <c>RuleChainBuilder.Build</c> directly gets an empty list here and should read
    ///         <c>RulesetComposition.Result.AttributedDiagnostics</c> instead. Distinct from
    ///         <see cref="RulesetCoverage" />, which reports legitimate per-profile view-binding
    ///         skips rather than broken documents.
    ///     </para>
    /// </summary>
    public IReadOnlyList<RulesetCompositionDiagnostic> RulesetDiagnostics { get; init; } = [];

    /// <summary>
    ///     The rulesets composition dropped from this build, each with the diagnostics explaining
    ///     why. Empty when every supplied document composed. The ids here are absent from
    ///     the graph entirely: their stats and highlights produce no nodes and can never fire.
    /// </summary>
    public IReadOnlyList<ExcludedRuleset> ExcludedRulesets { get; init; } = [];

    /// <summary>The source profile the graph was built for, dialect included.</summary>
    public DemoSourceProfile Profile { get; init; } = DemoSourceProfileRegistry.DefaultFallback;

    /// <summary>
    ///     How <see cref="Profile" /> was chosen. Set by <c>DemoAnalysis.Build</c>; a build driven
    ///     through <see cref="RuleChainBuilder" /> directly reports <see cref="ProfileResolutionKind.Explicit" />.
    /// </summary>
    public ProfileResolutionKind ProfileResolution { get; init; } = ProfileResolutionKind.Explicit;

    /// <summary>The registry the graph's edges were resolved against; what a decode plan maps edge types back through.</summary>
    public EventRegistry? Events { get; init; }

    /// <summary>
    ///     The <c>for: each_team</c> rulesets' nodes, per side (2 = T, 3 = CT): side → the qualified
    ///     <c>{ruleset}.{stat}</c> node map, the per-side twin of <see cref="GameNodesByRuleId" />.
    ///     Configured <c>team_round</c> / <c>team_match</c> tables resolve their columns here.
    ///     <c>null</c> when the build has no team ruleset.
    /// </summary>
    public IReadOnlyDictionary<int, IReadOnlyDictionary<string, StateNode>>? TeamNodesByRuleId { get; init; }

    /// <summary>
    ///     Per side, the node holding the side's roster (the connected players' slots, ascending) as of
    ///     the last freeze end: what a <c>team_round</c> table's <c>slots</c> dimension reads.
    ///     <c>null</c> when the build has no team ruleset.
    /// </summary>
    public IReadOnlyDictionary<int, StateNode>? TeamRosterNodes { get; init; }

    /// <summary>
    ///     The dispatch types of every message that can move the <c>round_number</c> context: the
    ///     concrete events of <c>$round_freeze_end</c>, <c>$match_start</c> and <c>$match_end</c> (the
    ///     triggers of <c>round_number</c> and of the <c>match_live</c> gate it is parented on), plus
    ///     <c>round_freeze_end</c> and <c>begin_new_match</c> themselves. A forward run samples its
    ///     configured tables just before these, which is where a snapshot run's round rows come from.
    /// </summary>
    public IReadOnlySet<Type> RoundBoundaryTypes { get; init; } = new HashSet<Type>();

    /// <summary>
    ///     The stand-ins for state kept outside the node graph (<see cref="ExternalState" />), one of
    ///     each, which descriptors in <see cref="Edges" /> and in the materialised players can point
    ///     at. They are not in <see cref="Nodes" />: join descriptors to <see cref="Nodes" /> and these
    ///     together, or a row that writes per-player state is dropped.
    /// </summary>
    public IReadOnlyList<ExternalStateNode> ExternalNodes { get; init; } = [];

    /// <summary>
    ///     Where the builder made each node of <see cref="Nodes" />; <c>null</c> for a hand-built
    ///     result. Read by <see cref="RuleGraph" />.
    /// </summary>
    internal IReadOnlyDictionary<StateNode, NodeProvenance>? Provenance { get; init; }
}

/// <summary>
///     Describes one drawn edge of the graph: a source, one destination, and what connects them.
///     <para>
///         The builder records at least one for every <see cref="StateEdge" /> it adds, with the
///         edge's real <see cref="StateEdge.Source" />, and one for each piece of wiring that is not
///         a <see cref="StateEdge" /> (a logic input, a rising-edge action, a live compute's read, an
///         entity value, an on-demand pull). <see cref="Kind" /> says which.
///     </para>
///     <para>
///         An edge that writes several nodes fans out to one descriptor per written node, in the
///         order <see cref="StateEdge.WrittenNode" />, <see cref="StateEdge.AdditionalWrittenNodes" />,
///         then what the builder knows the edge also writes: the first-wins guard a trigger sets, and
///         <see cref="ExternalState.PlayerContext" /> for an edge that updates per-player state. An
///         edge that writes no node at all is drawn to <see cref="ExternalState.PlayerContext" />, and
///         a round reset is a self-loop. So <see cref="Destination" /> is never null, and may be one of
///         <see cref="BuildResult.ExternalNodes" />.
///     </para>
///     <para>
///         Several descriptors can share one <see cref="Edge" />. Its <see cref="StateEdge.FireCount" />
///         and its applied messages belong to the edge, not the row: resolve them through
///         <see cref="Edge" /> and do not sum them across the rows of one edge.
///     </para>
///     <para>
///         <see cref="Label" /> is the event the edge fires on, as the rule named it where the rule
///         did (the concrete event of a logical one), otherwise the registered name of the edge's
///         dispatch type, otherwise that type's name in snake case. A round reset is labelled
///         <c>round reset</c>; wiring with no event has an empty label.
///     </para>
/// </summary>
/// <param name="Source">Edge source node.</param>
/// <param name="Destination">Edge destination node.</param>
/// <param name="Label">Display label shown on the edge.</param>
/// <param name="Effect">Effect the edge applies (Activate / Deactivate / SetValue).</param>
/// <param name="ConditionLabel">Optional human-readable condition expression shown on hover.</param>
public sealed record GraphEdgeDescriptor(
    StateNode Source,
    StateNode Destination,
    string Label,
    EdgeEffect Effect,
    string? ConditionLabel = null)
{
    /// <summary>What this row draws. Defaults to <see cref="GraphEdgeKind.Trigger" /> for a hand-built descriptor.</summary>
    public GraphEdgeKind Kind { get; init; }

    /// <summary>
    ///     The <see cref="StateEdge" /> this row draws, whose <see cref="StateEdge.FireCount" /> and
    ///     applied messages are the row's; <c>null</c> for wiring that is not a
    ///     <see cref="StateEdge" /> (see <see cref="GraphEdgeKind" />).
    /// </summary>
    public StateEdge? Edge { get; init; }

    /// <summary>
    ///     The nodes the edge reads besides <see cref="Source" />: its declared reads, the sources of
    ///     its <c>while:</c> gate, the guard it checks, the stat a tally or a round-end compute reads,
    ///     and the external state it consults. Empty when it reads nothing else. It does not model the
    ///     identifiers a compiled condition resolves by name at fire time.
    /// </summary>
    public IReadOnlyList<StateNode> Reads { get; init; } = [];
}
