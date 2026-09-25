#region

using CS2DemoKit.Analysis.Abstractions;

#endregion

namespace CS2DemoKit.Analysis.Graphs;

/// <summary>
///     What a <see cref="GraphEdgeDescriptor" /> draws: which part of the engine wired it, and so
///     whether a <see cref="StateEdge" /> backs it (<see cref="GraphEdgeDescriptor.Edge" />).
///     <para>
///         Members may be added in a minor release, one per new kind of engine wiring. A consumer
///         that switches on this must handle a value it does not know, for example by drawing it
///         like <see cref="Undescribed" />.
///     </para>
/// </summary>
public enum GraphEdgeKind
{
    /// <summary>
    ///     A rule's event or net-message trigger writing its node, or a guard it sets. Backed by the
    ///     edge.
    /// </summary>
    Trigger,

    /// <summary>
    ///     One source of a conjunction or disjunction input. No edge: the logic node recomputes when a
    ///     source is written. A multi-source input draws one row per source.
    /// </summary>
    LogicInput,

    /// <summary>
    ///     An action the evaluator runs on a logic node's rising edge: a highlight's <c>.count</c>
    ///     bump, or its emission, drawn to <see cref="ExternalState.Highlights" />. No edge.
    /// </summary>
    RisingEdgeAction,

    /// <summary>A node a live compute reads, drawn into the compute. No edge.</summary>
    LiveComputeRead,

    /// <summary>A built-in event enrichment writing its transient nodes. Backed by the edge.</summary>
    Enrichment,

    /// <summary>
    ///     Built-in bookkeeping that writes only per-player state (team, alive, connected, bomb
    ///     carrier, the server's round verdict), drawn to <see cref="ExternalState.PlayerContext" />.
    ///     Backed by the edge.
    /// </summary>
    PlayerState,

    /// <summary>
    ///     The entity scanner writing a provider's value node, drawn from
    ///     <see cref="ExternalState.EntityState" />. No edge: the scanner writes the node directly.
    /// </summary>
    EntityValue,

    /// <summary>
    ///     A node read on demand rather than written: a team aggregate or clutch facet from
    ///     <see cref="ExternalState.PlayerContext" />, an entity pull from
    ///     <see cref="ExternalState.EntityState" />, or the two buckets a rate divides. No edge.
    /// </summary>
    Pull,

    /// <summary>The bomb plant writing the plant-site round facts. Backed by the edge.</summary>
    RoundFact,

    /// <summary>
    ///     The evaluator deactivating a round-scoped guard or logic node at each round boundary. Drawn
    ///     as a self-loop labelled <c>round reset</c>. Backed by the edge.
    /// </summary>
    RoundReset,

    /// <summary>A <c>count:</c> stat's companion stamping the tick of its first event. Backed by the edge.</summary>
    FirstTick,

    /// <summary>A round-end event recomputing a <c>compute:</c> stat. Backed by the edge.</summary>
    RoundEndCompute,

    /// <summary>
    ///     A <c>tally:</c> bumping its bucket at round end. Drawn from the graph root, which is its real
    ///     source, with the tallied stat in <see cref="GraphEdgeDescriptor.Reads" />. Backed by the edge.
    /// </summary>
    Tally,

    /// <summary>The freeze-end economy sums (<c>round.team.equipment</c>, <c>.money</c>). Backed by the edge.</summary>
    FreezeEndEconomy,

    /// <summary>A team ruleset's roster, written at each freeze end. Backed by the edge.</summary>
    FreezeEndRoster,

    /// <summary>
    ///     The round-end recompute of a flag whose <c>when:</c> reads an entity pull. Backed by the
    ///     edge.
    /// </summary>
    EntitySettle,

    /// <summary>
    ///     An edge on the graph with no descriptor, which <see cref="RuleGraph" /> draws so it is not
    ///     lost. The builder never produces one; a hand-built graph can.
    /// </summary>
    Undescribed
}
