#region

using CS2DemoKit.Analysis.Abstractions;

#endregion

namespace CS2DemoKit.Analysis.Graphs;

/// <summary>Which store outside the node graph an <see cref="ExternalStateNode" /> stands for.</summary>
public enum ExternalState
{
    /// <summary>
    ///     The per-player state the engine keeps beside the graph: team, alive, connected, health, the
    ///     clutch and spray state, the round's decided winner. Enrichment and bookkeeping edges write
    ///     it; the team aggregates and clutch facets read it on demand.
    /// </summary>
    PlayerContext,

    /// <summary>
    ///     The entity scanner's per-frame view of the game rules and the players' pawns and
    ///     controllers. It writes the provider value nodes; entity pulls and a few enrichments read it.
    /// </summary>
    EntityState,

    /// <summary>The run's highlight records, which a highlight's emission appends to.</summary>
    Highlights
}

/// <summary>
///     A stand-in node for state the engine keeps outside the node graph, so a descriptor can draw an
///     edge that writes or reads it. Every build has one of each <see cref="ExternalState" />, listed
///     in <see cref="BuildResult.ExternalNodes" />.
///     <para>
///         It is never registered on the graph and never appears in <see cref="BuildResult.Nodes" />
///         or a materialised player's nodes, so it is not evaluated, snapshotted or counted. It is
///         always active, like the root.
///     </para>
/// </summary>
public sealed class ExternalStateNode : BoolNode
{
    /// <summary>Creates the stand-in for <paramref name="kind" />, already active.</summary>
    public ExternalStateNode(ExternalState kind)
    {
        Kind = kind;
        Name = kind switch
        {
            ExternalState.PlayerContext => "player_context",
            ExternalState.EntityState => "entity_state",
            ExternalState.Highlights => "highlights",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
        };
        Activate();
    }

    /// <summary>Which store this node stands for.</summary>
    public ExternalState Kind { get; }

    /// <inheritdoc />
    public override string Name { get; }
}

/// <summary>The three external nodes of one build, created together so every scope draws to the same instances.</summary>
internal sealed class GraphExternals
{
    internal ExternalStateNode PlayerContext { get; } = new(ExternalState.PlayerContext);

    internal ExternalStateNode EntityState { get; } = new(ExternalState.EntityState);

    internal ExternalStateNode Highlights { get; } = new(ExternalState.Highlights);

    internal IReadOnlyList<ExternalStateNode> All => [PlayerContext, EntityState, Highlights];
}
