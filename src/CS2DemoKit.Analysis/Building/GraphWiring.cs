#region

using System.Text;
using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Analysis.Edges;
using CS2DemoKit.Analysis.Graphs;
using CS2DemoKit.Analysis.Nodes;
using CS2DemoKit.Analysis.Registry;

#endregion

namespace CS2DemoKit.Analysis.Building;

/// <summary>
///     The one way the builder wires anything into a graph: every <see cref="StateEdge" /> it adds and
///     every piece of wiring that is not one (logic inputs, rising-edge actions, live computes, entity
///     values, on-demand pulls) goes through here, and each gets its
///     <see cref="GraphEdgeDescriptor" /> in the same call. A builder method that takes a wiring
///     cannot add an edge without describing it.
///     <para>
///         A game-scope wiring forwards each edge to its <see cref="StateGraph" /> as it arrives, so
///         the graph sees them in call order. A collecting wiring (a per-player template, the
///         <c>for: match</c> and <c>for: each_team</c> blocks) keeps them in <see cref="Edges" /> for
///         the caller to register at its usual point. Either way the edge order is the call order, and
///         the wiring never touches the relevant-message set.
///     </para>
/// </summary>
internal sealed class GraphWiring
{
    // Per enrichment and bookkeeping edge class, what it does with the state outside the graph. The
    // edges mutate PlayerContextIndex through method calls the edge's written-node declarations
    // cannot express, so this is the only place that knows. GraphDescriptorCoverageTests fails when
    // an edge class taking a PlayerContextIndex or an EntityChangeScanner is missing from it.
    private static readonly Dictionary<Type, ExternalAccess> _externalAccess = new()
    {
        [typeof(KillTeamEnrichmentEdge)] = ExternalAccess.WritesPlayerContext,
        [typeof(ClutchEnrichmentEdge)] = ExternalAccess.WritesPlayerContext,
        [typeof(HurtTeamEnrichmentEdge)] = ExternalAccess.WritesPlayerContext | ExternalAccess.ReadsEntityState,
        [typeof(BlindEnrichmentEdge)] = ExternalAccess.WritesPlayerContext,
        [typeof(HurtBulletEnrichmentEdge)] = ExternalAccess.WritesPlayerContext,
        [typeof(ShotEnrichmentEdge)] = ExternalAccess.WritesPlayerContext,
        [typeof(SprayKillEnrichmentEdge)] = ExternalAccess.WritesPlayerContext,
        [typeof(SpottedEnrichmentEdge)] = ExternalAccess.WritesPlayerContext,
        [typeof(AimShotContextEdge)] = ExternalAccess.WritesPlayerContext | ExternalAccess.ReadsEntityState,
        [typeof(PlayerTeamEdge)] = ExternalAccess.WritesPlayerContext,
        [typeof(BombPlantedEdge)] = ExternalAccess.WritesPlayerContext,
        [typeof(BombDefusedEdge)] = ExternalAccess.WritesPlayerContext,
        [typeof(BombExplodedEdge)] = ExternalAccess.WritesPlayerContext,
        [typeof(HealthResetEdge)] = ExternalAccess.WritesPlayerContext,
        [typeof(PlayerDisconnectEdge)] = ExternalAccess.WritesPlayerContext,
        [typeof(PlayerConnectEdge)] = ExternalAccess.WritesPlayerContext,
        [typeof(PlayerSpawnConnectivityEdge)] = ExternalAccess.WritesPlayerContext,
        [typeof(RoundDecidedEdge)] = ExternalAccess.WritesPlayerContext,
        [typeof(ClutchResolutionEnrichmentEdge)] = ExternalAccess.ReadsPlayerContext,
        [typeof(RoundEndEnrichmentEdge)] = ExternalAccess.ReadsPlayerContext,
        [typeof(SideRosterFreezeEndEdge)] = ExternalAccess.ReadsPlayerContext,
        [typeof(PlayerEconomyFreezeEndEdge)] = ExternalAccess.ReadsPlayerContext | ExternalAccess.ReadsEntityState
    };

    private readonly GraphExternals _externals;
    private readonly StateGraph? _forwardTo;
    private readonly EventRegistry _registry;

    /// <summary>A wiring over <paramref name="externals" />; forwards each edge to <paramref name="forwardTo" /> when given, else collects.</summary>
    internal GraphWiring(EventRegistry registry, GraphExternals externals, StateGraph? forwardTo = null)
    {
        _registry = registry;
        _externals = externals;
        _forwardTo = forwardTo;
    }

    internal GraphExternals Externals => _externals;

    /// <summary>The collected edges, in call order; empty for a forwarding wiring.</summary>
    internal List<StateEdge> Edges { get; } = [];

    /// <summary>Every descriptor recorded, in call order.</summary>
    internal List<GraphEdgeDescriptor> Descriptors { get; } = [];

    /// <summary>The collected plain rising-edge actions.</summary>
    internal List<(StateNode Trigger, Action Action, StateNode? Writes)> RisingEdgeActions { get; } = [];

    /// <summary>The collected context-arm rising-edge actions.</summary>
    internal List<(StateNode Trigger, Action<int, int> Action, StateNode? Writes)> ContextRisingEdgeActions { get; } = [];

    /// <summary>The collected live computes.</summary>
    internal List<LiveComputeRegistration> LiveComputes { get; } = [];

    /// <summary>
    ///     Adds <paramref name="edge" /> (forwarded or collected) and records one descriptor per node it
    ///     writes, all carrying the edge. See <see cref="GraphEdgeDescriptor" /> for the fan-out order,
    ///     the label fallback and what <see cref="GraphEdgeDescriptor.Reads" /> holds.
    /// </summary>
    /// <param name="edge">The edge.</param>
    /// <param name="kind">What it is.</param>
    /// <param name="label">The event name the call site holds; <c>null</c> falls back to the dispatch type's name.</param>
    /// <param name="condition">The condition text to show, if any.</param>
    /// <param name="extraReads">Nodes the edge reads that it does not declare: gate sources, a guard, a tally's source.</param>
    /// <param name="extraWrites">Nodes the edge writes that it does not declare: a first-wins guard it sets.</param>
    /// <param name="effect">The effect on its declared writes, when the call site knows it better than the edge.</param>
    internal void AddEdge(StateEdge edge, GraphEdgeKind kind, string? label = null, string? condition = null,
        IReadOnlyList<StateNode>? extraReads = null, IReadOnlyList<StateNode>? extraWrites = null,
        EdgeEffect? effect = null)
    {
        if (_forwardTo is not null)
        {
            _forwardTo.AddEdge(edge);
        }
        else
        {
            Edges.Add(edge);
        }

        _externalAccess.TryGetValue(edge.GetType(), out ExternalAccess access);
        IReadOnlyList<StateNode> reads = Reads(edge, extraReads, access);
        string text = label ?? LabelFor(edge.MessageType);

        if (edge is RoundScopedLogicNodeReset reset)
        {
            Descriptors.Add(new GraphEdgeDescriptor(edge.Source, reset.WrappedNode, text, EdgeEffect.Deactivate, condition)
            {
                Kind = kind,
                Edge = edge,
                Reads = reads
            });
            return;
        }

        EdgeEffect declared = effect ?? edge.DeclaredEffect ?? EdgeEffect.SetValue;
        int before = Descriptors.Count;
        void Row(StateNode destination, EdgeEffect rowEffect)
        {
            for (int i = before; i < Descriptors.Count; i++)
            {
                if (ReferenceEquals(Descriptors[i].Destination, destination))
                {
                    return;
                }
            }

            Descriptors.Add(new GraphEdgeDescriptor(edge.Source, destination, text, rowEffect, condition)
            {
                Kind = kind,
                Edge = edge,
                Reads = reads
            });
        }

        if (edge.WrittenNode is { } written)
        {
            Row(written, declared);
        }

        if (edge.AdditionalWrittenNodes is { } additional)
        {
            foreach (StateNode node in additional)
            {
                Row(node, declared);
            }
        }

        if (extraWrites is not null)
        {
            foreach (StateNode node in extraWrites)
            {
                Row(node, EdgeEffect.Activate);
            }
        }

        if ((access & ExternalAccess.WritesPlayerContext) != 0 || Descriptors.Count == before)
        {
            Row(_externals.PlayerContext, EdgeEffect.SetValue);
        }
    }

    /// <summary>One <see cref="GraphEdgeKind.LogicInput" /> row per source of every input of <paramref name="logic" />.</summary>
    internal void DescribeLogicNode(BoolNode logic, EdgeEffect effect, IEnumerable<IConditionalEdge> inputs)
    {
        foreach (IConditionalEdge input in inputs)
        {
            foreach (StateNode source in input.Sources)
            {
                Descriptors.Add(new GraphEdgeDescriptor(source, logic, "", effect, input.ConditionLabel)
                {
                    Kind = GraphEdgeKind.LogicInput
                });
            }
        }
    }

    /// <summary>Collects a plain rising-edge action on <paramref name="trigger" /> that writes <paramref name="writes" />, and draws it.</summary>
    internal void AddRisingEdgeAction(StateNode trigger, Action action, StateNode writes)
    {
        RisingEdgeActions.Add((trigger, action, writes));
        Descriptors.Add(new GraphEdgeDescriptor(trigger, writes, "", EdgeEffect.SetValue, "rising edge")
        {
            Kind = GraphEdgeKind.RisingEdgeAction
        });
    }

    /// <summary>
    ///     Collects a context-arm rising-edge action on <paramref name="trigger" /> that appends a
    ///     highlight record, and draws it to <see cref="ExternalState.Highlights" />.
    /// </summary>
    internal void AddHighlightEmission(StateNode trigger, Action<int, int> action)
    {
        ContextRisingEdgeActions.Add((trigger, action, null));
        Descriptors.Add(new GraphEdgeDescriptor(trigger, _externals.Highlights, "", EdgeEffect.SetValue, "rising edge")
        {
            Kind = GraphEdgeKind.RisingEdgeAction
        });
    }

    /// <summary>Collects a live compute and draws each node it reads into it.</summary>
    internal void AddLiveCompute(ComputedStatNode compute, IReadOnlyList<StateNode> reads)
    {
        LiveComputes.Add(new LiveComputeRegistration(compute, reads));
        foreach (StateNode read in reads)
        {
            Descriptors.Add(new GraphEdgeDescriptor(read, compute, "", EdgeEffect.SetValue, "live")
            {
                Kind = GraphEdgeKind.LiveComputeRead
            });
        }
    }

    /// <summary>Draws the entity scanner writing <paramref name="node" />, the value node of provider <paramref name="contextName" />.</summary>
    internal void DescribeEntityValue(StateNode node, string contextName) =>
        Descriptors.Add(new GraphEdgeDescriptor(_externals.EntityState, node, contextName, EdgeEffect.SetValue)
        {
            Kind = GraphEdgeKind.EntityValue
        });

    /// <summary>Draws <paramref name="to" /> reading <paramref name="from" /> on demand.</summary>
    internal void DescribePull(StateNode from, StateNode to, string label) =>
        Descriptors.Add(new GraphEdgeDescriptor(from, to, label, EdgeEffect.SetValue)
        {
            Kind = GraphEdgeKind.Pull
        });

    /// <summary>
    ///     What <paramref name="edgeType" /> does with the state outside the graph, or <c>null</c> when
    ///     the table does not list it. Test seam for the coverage gate.
    /// </summary>
    internal static (bool WritesPlayerContext, bool ReadsPlayerContext, bool ReadsEntityState)? ExternalAccessOf(Type edgeType) =>
        _externalAccess.TryGetValue(edgeType, out ExternalAccess access)
            ? ((access & ExternalAccess.WritesPlayerContext) != 0,
                (access & (ExternalAccess.WritesPlayerContext | ExternalAccess.ReadsPlayerContext)) != 0,
                (access & ExternalAccess.ReadsEntityState) != 0)
            : null;

    private IReadOnlyList<StateNode> Reads(StateEdge edge, IReadOnlyList<StateNode>? extraReads, ExternalAccess access)
    {
        IReadOnlyList<StateNode> declared = edge.DeclaredReads ?? [];
        List<StateNode>? merged = null;

        void Add(StateNode node)
        {
            if (ReferenceEquals(node, edge.Source) || Contains(declared, node) || (merged is not null && Contains(merged, node)))
            {
                return;
            }

            merged ??= [.. declared];
            merged.Add(node);
        }

        if (extraReads is not null)
        {
            foreach (StateNode node in extraReads)
            {
                Add(node);
            }
        }

        // An edge that updates per-player state reads it too: every one of them looks the player up
        // before it writes.
        if ((access & (ExternalAccess.WritesPlayerContext | ExternalAccess.ReadsPlayerContext)) != 0)
        {
            Add(_externals.PlayerContext);
        }

        if ((access & ExternalAccess.ReadsEntityState) != 0)
        {
            Add(_externals.EntityState);
        }

        return merged ?? declared;
    }

    private static bool Contains(IReadOnlyList<StateNode> nodes, StateNode node)
    {
        foreach (StateNode n in nodes)
        {
            if (ReferenceEquals(n, node))
            {
                return true;
            }
        }

        return false;
    }

    private string LabelFor(Type messageType)
    {
        if (messageType == typeof(void))
        {
            return "round reset";
        }

        if (_registry.TryGetName(messageType, out string name, out _))
        {
            return name;
        }

        // A synthesized event (round_decided, bullet_damage, an entity change) is not registered:
        // its type name, less the suffix, in snake case.
        Type type = messageType.IsGenericType ? messageType.GetGenericArguments()[0] : messageType;
        string bare = type.Name.EndsWith("Event", StringComparison.Ordinal) ? type.Name[..^5] : type.Name;
        StringBuilder sb = new(bare.Length + 4);
        for (int i = 0; i < bare.Length; i++)
        {
            char c = bare[i];
            if (char.IsUpper(c) && i > 0)
            {
                sb.Append('_');
            }

            sb.Append(char.ToLowerInvariant(c));
        }

        return sb.ToString();
    }

    [Flags]
    private enum ExternalAccess
    {
        None = 0,
        WritesPlayerContext = 1,
        ReadsPlayerContext = 2,
        ReadsEntityState = 4
    }
}
