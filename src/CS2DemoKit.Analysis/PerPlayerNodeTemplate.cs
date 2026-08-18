#region

using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Analysis.Graphs;
using CS2DemoKit.Analysis.RulesetsV2.Model;
using CS2DemoKit.Parser;

#endregion

namespace CS2DemoKit.Analysis;

/// <summary>
///     Declares per-player nodes and edges at graph-build time. During evaluation, the evaluator
///     calls <see cref="Materialize" /> for each newly-discovered player slot, producing concrete
///     nodes, edges, column assignments, and edge descriptors.
/// </summary>
public sealed class PerPlayerNodeTemplate(Func<int, int, string, ParsedDemo?, PerPlayerNodeTemplate.MaterializedPlayer> factory)
{
    /// <summary>
    ///     Invokes the underlying factory to produce concrete nodes, edges, column assignments,
    ///     and edge descriptors for a single player. Called once per newly-discovered player slot.
    /// </summary>
    /// <param name="playerSlot">The CS2 player slot (0...N-1) for the materialized player.</param>
    /// <param name="playerIndex">Materialization order index — used to assign columns deterministically.</param>
    /// <param name="playerName">Display name for this player, included in node subtitles.</param>
    /// <param name="demo">Optional reference to the parsed demo for callers that need wider context.</param>
    public MaterializedPlayer Materialize(int playerSlot, int playerIndex, string playerName, ParsedDemo? demo) =>
        factory(playerSlot, playerIndex, playerName, demo);

    /// <summary>The materialization output for one player: nodes, edges, column assignments, and edge descriptors.</summary>
    /// <param name="PlayerSlot">The CS2 player slot this materialization is for.</param>
    /// <param name="PlayerName">Display name for the player.</param>
    /// <param name="Nodes">Concrete state nodes produced by the template for this player.</param>
    /// <param name="Edges">Concrete state edges wiring the nodes together.</param>
    /// <param name="ColumnAssignments">Mappings from nodes to player-table columns.</param>
    /// <param name="EdgeDescriptors">Visualization descriptors for the produced edges.</param>
    /// <param name="RisingEdgeActions">Optional rising-edge callbacks installed against trigger nodes.</param>
    /// <param name="ContextRisingEdgeActions">
    ///     Optional context-arm rising-edge callbacks (A1 highlight emission): the evaluator invokes
    ///     each with the firing site's <c>(frameIndex, tick)</c> — frame clock, the values a
    ///     <c>RuleChainEvent</c> is stamped with. Additive alongside <paramref name="RisingEdgeActions" />
    ///     on the same trigger (plain actions fire first); <c>null</c> for the common no-highlight case.
    /// </param>
    /// <param name="LiveComputes">
    ///     Optional live computes for this player: each re-evaluates live as its declared
    ///     reads go dirty rather than once at round end. <c>null</c> when the player materialized no
    ///     <c>compute: { live: true }</c> stat (the common case) — the evaluator's live interleave stays
    ///     dormant.
    /// </param>
    /// <param name="TemplateIndex">Index of the template that produced this materialization, when multiple are in use.</param>
    /// <param name="NodesByRuleId">
    ///     Rule-id → node map for this player, from the template's local lookup: bare rule ids
    ///     (per-player chain rules, contexts, and inherited game-scoped ids) plus chain-qualified
    ///     <c>chain.rule</c> aliases for every per-player chain's rules (dedup-aware — an alias
    ///     resolves to the shared node when its rule was structurally deduplicated). Drives
    ///     configured-output metric resolution. <c>null</c> for hand-built fixtures that don't need it.
    /// </param>
    public readonly record struct MaterializedPlayer(
        int PlayerSlot,
        string PlayerName,
        IReadOnlyList<StateNode> Nodes,
        IReadOnlyList<StateEdge> Edges,
        IReadOnlyList<PerPlayerColumnAssignment> ColumnAssignments,
        IReadOnlyList<GraphEdgeDescriptor> EdgeDescriptors,
        IReadOnlyList<(StateNode Trigger, Action Action, StateNode? Writes)>? RisingEdgeActions = null,
        int TemplateIndex = 0,
        IReadOnlyDictionary<string, StateNode>? NodesByRuleId = null,
        IReadOnlyList<LiveComputeRegistration>? LiveComputes = null,
        IReadOnlyList<(StateNode Trigger, Action<int, int> Action, StateNode? Writes)>? ContextRisingEdgeActions = null);
}

/// <summary>Maps a per-player node to a named column in the player stats table.</summary>
/// <param name="Node">The per-player node whose value populates the column.</param>
/// <param name="ColumnName">Display label for the column header.</param>
/// <param name="GroupName">Optional group name used to cluster related columns.</param>
/// <param name="ChainId">
///     Optional <c>_chain_{id}</c> join-key of the per-player chain that declared this column.
///     Lets the graph-filter feature emphasize / inert a chain's columns without a relayout.
///     <c>null</c> for columns not associated with a chain.
/// </param>
/// <param name="IsRoundScoped">
///     True when the column's node resets at round boundaries — either the node itself is
///     <see cref="IRoundScopedNode" /> or it is a logic node reset via a
///     <c>RoundScopedLogicNodeReset</c> wrapper edge (which the node type alone can't reveal).
///     Projectors MUST use this flag, not a node-type check, to split round vs game tables.
/// </param>
/// <param name="Format">
///     The column's <c>as:</c> display formatting for a tick-valued value (v2 <c>show:</c>
///     scoreboard <c>as:</c>). <see cref="ColumnValueFormat.None" /> (the default, and every v1
///     column) leaves the projected value byte-identical; the projectors apply non-<c>None</c>
///     formats at the demo's tick rate when reading the cell.
/// </param>
public sealed record PerPlayerColumnAssignment(
    StateNode Node,
    string ColumnName,
    string? GroupName = null,
    string? ChainId = null,
    bool IsRoundScoped = false,
    ColumnValueFormat Format = ColumnValueFormat.None);
