#region

using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Analysis.Building;
using CS2DemoKit.Analysis.Config;
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
/// <param name="Edges">Visualization descriptors for every edge.</param>
/// <param name="Chains">All chain (conjunction) nodes — used by the timeline view.</param>
/// <param name="RelevantMessageTypes">
///     Set of message types the graph subscribes to; the evaluator can short-circuit other
///     messages.
/// </param>
/// <param name="GroupHints">Hints to the visualization layer for clustering nodes by group name.</param>
/// <param name="PlayerContextIndex">Per-player context tracking, when any rule needs cross-player state.</param>
/// <param name="EntityScanner">
///     Lazy entity-state scanner; <c>null</c> when no rule references any
///     <c>IEntityValueProvider</c>.
/// </param>
/// <param name="NodeChains">
///     Game-scoped node → <c>_chain_{id}</c> membership, a Rulesets v1 chain concept. Always
///     <c>null</c> since the v1 chain layer was removed — consumers (the Analysis-graph chain
///     filter) already degrade to per-player scoping on <c>null</c>, which has been the
///     production behaviour since the v2 cutover. Kept as a slot so the UI plumbing stays
///     uniform; candidates for a future v2 membership surface.
/// </param>
/// <param name="EdgeBacking">
///     Maps each game-scoped graph-edge descriptor to the <see cref="StateEdge" /> the evaluator
///     fires for it, by reference identity (the two are created together in the build loop). Only
///     trigger-backed graph edges appear — conjunction/disjunction/rising-edge descriptors have no
///     <see cref="StateEdge" /> and are absent. Drives edge graph-breakpoints (descriptor →
///     <see cref="StateEdge" /> → <c>EvaluationResult.AppliedMessagesByEdge</c>). Per-player table
///     edges are out of scope and not mapped. <c>null</c> when no edge was backed.
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
    IReadOnlyList<ConjunctionNode> Chains,
    IReadOnlySet<Type> RelevantMessageTypes,
    IReadOnlyList<NodeGroupHint> GroupHints,
    PlayerContextIndex? PlayerContextIndex = null,
    // Lazy-activated entity-state scanner — null when no rule references any
    // registered IEntityValueProvider's ContextName. Bench-parity tripwire:
    // EntityIntegrationTests.EntityScanner_NotAllocated_WhenNoRulesReference.
    EntityChangeScanner? EntityScanner = null,
    IReadOnlyDictionary<StateNode, IReadOnlySet<string>>? NodeChains = null,
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
}

/// <summary>Visualization hint: a named cluster grouping a set of nodes for display.</summary>
/// <param name="GroupName">Cluster label shown in the graph view.</param>
/// <param name="Members">Nodes that belong to this cluster.</param>
public sealed record NodeGroupHint(string GroupName, IReadOnlyList<StateNode> Members);

/// <summary>Describes one edge to the visualization layer.</summary>
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
    string? ConditionLabel = null);
