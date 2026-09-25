#region

using System.Diagnostics.CodeAnalysis;
using CS2DemoKit.Analysis.Abstractions;

#endregion

namespace CS2DemoKit.Analysis.Graphs;

/// <summary>
///     The rule graph as a consumer draws it: the game-scope nodes and edges of a build joined with
///     every materialised player's, each node carrying where it came from, and each edge already
///     resolved to its two view nodes, so nothing has to be joined by <see cref="StateNode" />
///     identity or by name.
///     <para>
///         <see cref="FromRun" /> is the graph a run ran: the build's nodes and descriptors, the
///         three <see cref="BuildResult.ExternalNodes" />, and every <see cref="AnalysisRun.MaterializedPlayers" />
///         entry. <see cref="CollapsePlayers" /> folds the per-player copies into one node per
///         template position. <see cref="FromBuild(BuildResult, bool, string)" /> draws a build before any run, previewing each
///         per-player template once.
///     </para>
///     <para>
///         A view is built only when a consumer asks for one; a run never builds it. Building it reads
///         the graph and does not change it, with the one exception documented on
///         <see cref="FromBuild(BuildResult, bool, string)" />.
///     </para>
/// </summary>
public sealed class RuleGraph
{
    private static readonly IReadOnlySet<string> _noChains = new HashSet<string>(StringComparer.Ordinal);

    private readonly Dictionary<string, RuleGraphNode> _byKey;
    private readonly Dictionary<StateNode, RuleGraphNode> _byNode;

    private RuleGraph(IReadOnlyList<RuleGraphNode> nodes, IReadOnlyList<RuleGraphEdge> edges, IReadOnlyList<string> diagnostics)
    {
        Nodes = nodes;
        Edges = edges;
        Diagnostics = diagnostics;
        _byKey = new Dictionary<string, RuleGraphNode>(nodes.Count, StringComparer.Ordinal);
        _byNode = new Dictionary<StateNode, RuleGraphNode>(nodes.Count, ReferenceEqualityComparer.Instance);
        foreach (RuleGraphNode node in nodes)
        {
            _byKey[node.Key] = node;
            foreach (StateNode instance in node.Instances)
            {
                _byNode[instance] = node;
            }
        }
    }

    /// <summary>Every node: the game scope in build order, then the external nodes, then each player's nodes.</summary>
    public IReadOnlyList<RuleGraphNode> Nodes { get; }

    /// <summary>Every edge, each resolved to two of <see cref="Nodes" />.</summary>
    public IReadOnlyList<RuleGraphEdge> Edges { get; }

    /// <summary>
    ///     What the view could not draw as the engine describes it: a template preview that did not
    ///     materialise, an edge on the graph with no descriptor (drawn as
    ///     <see cref="GraphEdgeKind.Undescribed" />), a descriptor naming a node the view does not have.
    ///     Empty for a build the builder made.
    /// </summary>
    public IReadOnlyList<string> Diagnostics { get; }

    /// <summary>The view node for <paramref name="node" />, which after <see cref="CollapsePlayers" /> is the one standing for all its copies.</summary>
    public bool TryGetNode(StateNode node, [NotNullWhen(true)] out RuleGraphNode? info) =>
        _byNode.TryGetValue(node, out info);

    /// <summary>The view node with <paramref name="key" /> (see <see cref="RuleGraphNode.Key" />).</summary>
    public bool TryGetNode(string key, [NotNullWhen(true)] out RuleGraphNode? info) =>
        _byKey.TryGetValue(key, out info);

    /// <summary>
    ///     The graph of a build before any run: its game scope, and, with
    ///     <paramref name="includeTemplates" />, each per-player template materialised once at slot 0
    ///     under <paramref name="templatePlayerName" />. The previewed nodes have a
    ///     <see cref="RuleGraphNode.Template" /> but no <see cref="RuleGraphNode.PlayerSlot" />, and are
    ///     keyed as <see cref="CollapsePlayers" /> keys them.
    ///     <para>
    ///         The preview runs the builder's per-player factory, which keeps state on the builder
    ///         while it runs. Do not call this while a run over the same build is going: build the
    ///         view before the run starts, or use <see cref="FromRun" /> after it. One after the other
    ///         is safe; the preview leaves nothing behind that a run reads.
    ///     </para>
    ///     <para>
    ///         A template that cannot materialise without a demo (a settle-site entity read on a build
    ///         with no entity scanner) is reported in <see cref="Diagnostics" /> and left out; the game
    ///         scope is still returned.
    ///     </para>
    /// </summary>
    public static RuleGraph FromBuild(BuildResult build, bool includeTemplates = true, string templatePlayerName = "each player") =>
        FromBuild(build, includeTemplates, templatePlayerName, false);

    /// <summary>As <see cref="FromBuild(BuildResult, bool, string)" />; with <paramref name="rethrow" />, a template that fails to materialise throws.</summary>
    internal static RuleGraph FromBuild(BuildResult build, bool includeTemplates, string templatePlayerName, bool rethrow)
    {
        ArgumentNullException.ThrowIfNull(build);
        Assembler assembler = new(build);
        if (includeTemplates)
        {
            IReadOnlyList<PerPlayerNodeTemplate> templates = build.Graph.PerPlayerTemplates;
            for (int i = 0; i < templates.Count; i++)
            {
                PerPlayerNodeTemplate.MaterializedPlayer preview;
                try
                {
                    preview = templates[i].Materialize(0, 0, templatePlayerName);
                }
                catch (InvalidOperationException ex) when (!rethrow)
                {
                    assembler.Diagnostics.Add($"template {i} did not materialise without a demo: {ex.Message}");
                    continue;
                }

                assembler.AddPlayer(preview, null, templatePlayerName, i);
            }
        }

        return assembler.Finish();
    }

    /// <summary>The graph <paramref name="run" /> ran: its build's game scope joined with every player it materialised.</summary>
    public static RuleGraph FromRun(AnalysisRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        Assembler assembler = new(run.Build);
        foreach (PerPlayerNodeTemplate.MaterializedPlayer player in run.MaterializedPlayers)
        {
            assembler.AddPlayer(player, player.PlayerSlot, player.PlayerName, player.TemplateIndex);
        }

        return assembler.Finish();
    }

    /// <summary>
    ///     Folds every player's copy of a template node into one node keyed <c>t{template}/{ordinal}</c>,
    ///     whose <see cref="RuleGraphNode.Node" /> is the first player's copy and whose
    ///     <see cref="RuleGraphNode.Instances" /> lists every copy in slot order. Game, team and
    ///     external nodes are unchanged. Edges between the same two collapsed nodes with the same label
    ///     and kind become one, and every edge's endpoints are nodes of the returned view.
    /// </summary>
    /// <exception cref="InvalidOperationException">Two copies under one template position differ in type or name.</exception>
    public RuleGraph CollapsePlayers()
    {
        List<RuleGraphNode> nodes = [];
        Dictionary<RuleGraphNode, RuleGraphNode> map = new(ReferenceEqualityComparer.Instance);
        Dictionary<NodeTemplateKey, List<RuleGraphNode>> groups = [];
        List<NodeTemplateKey> order = [];

        foreach (RuleGraphNode node in Nodes)
        {
            if (node.Scope != RuleGraphScope.Player || node.Template is not { } template)
            {
                nodes.Add(node);
                map[node] = node;
                continue;
            }

            if (!groups.TryGetValue(template, out List<RuleGraphNode>? group))
            {
                groups[template] = group = [];
                order.Add(template);
            }

            group.Add(node);
        }

        foreach (NodeTemplateKey template in order)
        {
            List<RuleGraphNode> copies = [.. groups[template].OrderBy(n => n.PlayerSlot ?? -1)];
            RuleGraphNode first = copies[0];
            HashSet<string> chains = new(StringComparer.Ordinal);
            List<StateNode> instances = [];
            foreach (RuleGraphNode copy in copies)
            {
                if (copy.Node.GetType() != first.Node.GetType() || copy.Node.Name != first.Node.Name)
                {
                    throw new InvalidOperationException(
                        $"template {template.TemplateIndex} position {template.Ordinal} is '{first.Node.Name}' "
                        + $"({first.Node.GetType().Name}) for one player and '{copy.Node.Name}' "
                        + $"({copy.Node.GetType().Name}) for another; the template is not slot-independent.");
                }

                chains.UnionWith(copy.HighlightChains);
                instances.AddRange(copy.Instances);
            }

            RuleGraphNode collapsed = first with
            {
                Key = TemplateKey(template),
                PlayerSlot = null,
                PlayerName = null,
                HighlightChains = chains,
                Instances = instances
            };
            nodes.Add(collapsed);
            foreach (RuleGraphNode copy in copies)
            {
                map[copy] = collapsed;
            }
        }

        List<RuleGraphEdge> edges = [];
        HashSet<(string, string, string, GraphEdgeKind)> seen = [];
        foreach (RuleGraphEdge edge in Edges)
        {
            RuleGraphNode source = map[edge.Source];
            RuleGraphNode destination = map[edge.Destination];
            if (!seen.Add((source.Key, destination.Key, edge.Descriptor.Label, edge.Descriptor.Kind)))
            {
                continue;
            }

            edges.Add(edge.Scope == RuleGraphScope.Player
                ? edge with
                {
                    Key = edge.Template is { } t ? $"t{t.TemplateIndex}/e{t.Ordinal}" : edge.Key,
                    Source = source,
                    Destination = destination,
                    PlayerSlot = null
                }
                : edge);
        }

        return new RuleGraph(nodes, edges, Diagnostics);
    }

    private static string TemplateKey(NodeTemplateKey template) => $"t{template.TemplateIndex}/{template.Ordinal}";

    // Two passes: collect every node as a draft with its keys and provenance, then, once every edge
    // is known, walk the highlight chains back over them and seal the drafts into view nodes.
    private sealed class Assembler
    {
        private readonly BuildResult _build;
        private readonly Dictionary<StateNode, Draft> _drafts = new(ReferenceEqualityComparer.Instance);
        private readonly List<Draft> _order = [];
        private readonly List<(string Key, GraphEdgeDescriptor Descriptor, RuleGraphScope Scope, int? Slot, EdgeTemplateKey? Template)> _edges = [];
        private readonly Dictionary<StateNode, List<string>> _ruleIds = new(ReferenceEqualityComparer.Instance);
        private int _groups;

        internal Assembler(BuildResult build)
        {
            _build = build;
            if (build.GameNodesByRuleId is { } game)
            {
                AddRuleIds(game);
            }

            if (build.TeamNodesByRuleId is { } team)
            {
                foreach (IReadOnlyDictionary<string, StateNode> side in team.Values)
                {
                    AddRuleIds(side);
                }
            }

            for (int i = 0; i < build.Nodes.Count; i++)
            {
                StateNode node = build.Nodes[i];
                NodeProvenance? provenance = build.Provenance is { } p && p.TryGetValue(node, out NodeProvenance found) ? found : null;
                RuleGraphNodeOrigin origin = node is RootNode ? RuleGraphNodeOrigin.Root : provenance?.Origin ?? RuleGraphNodeOrigin.Unknown;
                RuleGraphScope scope = provenance?.TeamSide is not null ? RuleGraphScope.Team : RuleGraphScope.Game;
                AddDraft(new Draft($"g/{i}", node, scope, origin, provenance, null, null, null, -1));
            }

            foreach (ExternalStateNode external in build.ExternalNodes)
            {
                AddDraft(new Draft($"x/{external.Name}", external, RuleGraphScope.External, RuleGraphNodeOrigin.External, null, null, null, null, -1));
            }

            AddEdges(build.Edges, build.Graph.Edges, "g", RuleGraphScope.Game, null, null);
        }

        internal List<string> Diagnostics { get; } = [];

        internal void AddPlayer(PerPlayerNodeTemplate.MaterializedPlayer player, int? slot, string? name, int templateIndex)
        {
            if (player.NodesByRuleId is { } ruleIds)
            {
                AddRuleIds(ruleIds);
            }

            int group = _groups++;
            string prefix = slot is { } s ? $"p{s}/t{templateIndex}" : $"t{templateIndex}";
            for (int i = 0; i < player.Nodes.Count; i++)
            {
                NodeProvenance? provenance = player.Provenance is { } p && i < p.Count ? p[i] : null;
                AddDraft(new Draft($"{prefix}/{i}", player.Nodes[i], RuleGraphScope.Player,
                    provenance?.Origin ?? RuleGraphNodeOrigin.Unknown, provenance, slot, name,
                    new NodeTemplateKey(templateIndex, i), group));
            }

            AddEdges(player.EdgeDescriptors, player.Edges, prefix, RuleGraphScope.Player, slot, templateIndex);
        }

        internal RuleGraph Finish()
        {
            // Resolve every edge's endpoints first; a descriptor naming a node the view does not
            // have is reported and dropped rather than drawn to nowhere.
            List<(string Key, GraphEdgeDescriptor Descriptor, Draft Source, Draft Destination, RuleGraphScope Scope, int? Slot, EdgeTemplateKey? Template)> resolved = [];
            Dictionary<Draft, List<Draft>> predecessors = new(ReferenceEqualityComparer.Instance);
            foreach ((string key, GraphEdgeDescriptor d, RuleGraphScope scope, int? slot, EdgeTemplateKey? template) in _edges)
            {
                if (!_drafts.TryGetValue(d.Source, out Draft? source) || !_drafts.TryGetValue(d.Destination, out Draft? destination))
                {
                    Diagnostics.Add($"edge {key} ({d.Source.Name} -> {d.Destination.Name}) names a node the view does not have");
                    continue;
                }

                RuleGraphScope edgeScope = scope == RuleGraphScope.Game
                                           && (source.Scope == RuleGraphScope.Team || destination.Scope == RuleGraphScope.Team)
                    ? RuleGraphScope.Team
                    : scope;
                resolved.Add((key, d, source, destination, edgeScope, slot, template));

                if (!predecessors.TryGetValue(destination, out List<Draft>? list))
                {
                    predecessors[destination] = list = [];
                }

                list.Add(source);
                foreach (StateNode read in d.Reads)
                {
                    if (_drafts.TryGetValue(read, out Draft? reader))
                    {
                        list.Add(reader);
                    }
                }
            }

            MarkHighlightChains(predecessors);

            Dictionary<Draft, RuleGraphNode> sealedNodes = new(ReferenceEqualityComparer.Instance);
            List<RuleGraphNode> nodes = new(_order.Count);
            foreach (Draft draft in _order)
            {
                RuleGraphNode node = draft.Seal(_ruleIds.TryGetValue(draft.Node, out List<string>? ids) ? ids : []);
                sealedNodes[draft] = node;
                nodes.Add(node);
            }

            List<RuleGraphEdge> edges = new(resolved.Count);
            foreach ((string key, GraphEdgeDescriptor d, Draft source, Draft destination, RuleGraphScope scope, int? slot, EdgeTemplateKey? template) in resolved)
            {
                edges.Add(new RuleGraphEdge(key, sealedNodes[source], sealedNodes[destination], d, scope, slot, template));
            }

            return new RuleGraph(nodes, edges, Diagnostics);
        }

        // A highlight's chain is its _chain_ logic node and what feeds it. The walk goes back over
        // sources and reads, but not into the root or the external state (every trigger starts at
        // the root, so passing through it would mark everything), and it marks game scaffolding (an
        // enrichment, a context, an entity value, a round fact) without walking past it. The nodes
        // the highlight made itself, its .count, are added from provenance.
        private void MarkHighlightChains(Dictionary<Draft, List<Draft>> predecessors)
        {
            Dictionary<(int Group, string Chain), List<Draft>> made = [];
            foreach (Draft draft in _order)
            {
                if (draft.Provenance?.HighlightChain is { } chain)
                {
                    if (!made.TryGetValue((draft.Group, chain), out List<Draft>? list))
                    {
                        made[(draft.Group, chain)] = list = [];
                    }

                    list.Add(draft);
                }
            }

            foreach (Draft start in _order)
            {
                if (start.Node is not (ConjunctionNode or DisjunctionNode)
                    || !start.Node.Name.StartsWith("_chain_", StringComparison.Ordinal))
                {
                    continue;
                }

                string chain = start.Node.Name;
                HashSet<Draft> visited = new(ReferenceEqualityComparer.Instance) { start };
                Queue<Draft> frontier = new([start]);
                start.Chains.Add(chain);
                while (frontier.Count > 0)
                {
                    Draft current = frontier.Dequeue();
                    if (!predecessors.TryGetValue(current, out List<Draft>? feeders))
                    {
                        continue;
                    }

                    foreach (Draft feeder in feeders)
                    {
                        if (feeder.Origin is RuleGraphNodeOrigin.Root or RuleGraphNodeOrigin.External || !visited.Add(feeder))
                        {
                            continue;
                        }

                        feeder.Chains.Add(chain);
                        if (!IsScaffolding(feeder))
                        {
                            frontier.Enqueue(feeder);
                        }
                    }
                }

                if (made.TryGetValue((start.Group, chain), out List<Draft>? own))
                {
                    foreach (Draft draft in own)
                    {
                        draft.Chains.Add(chain);
                    }
                }
            }
        }

        private static bool IsScaffolding(Draft draft) =>
            draft.Scope is RuleGraphScope.Game or RuleGraphScope.Team
            && draft.Origin is RuleGraphNodeOrigin.Enrichment or RuleGraphNodeOrigin.EntityValue
                or RuleGraphNodeOrigin.RoundFact or RuleGraphNodeOrigin.Context;

        private void AddDraft(Draft draft)
        {
            if (_drafts.TryAdd(draft.Node, draft))
            {
                _order.Add(draft);
            }
        }

        private void AddRuleIds(IReadOnlyDictionary<string, StateNode> map)
        {
            foreach ((string id, StateNode node) in map)
            {
                if (!_ruleIds.TryGetValue(node, out List<string>? ids))
                {
                    _ruleIds[node] = ids = [];
                }

                if (!ids.Contains(id, StringComparer.Ordinal))
                {
                    ids.Add(id);
                }
            }
        }

        private void AddEdges(IReadOnlyList<GraphEdgeDescriptor> descriptors, IReadOnlyList<StateEdge> graphEdges,
            string prefix, RuleGraphScope scope, int? slot, int? templateIndex)
        {
            HashSet<StateEdge> described = new(ReferenceEqualityComparer.Instance);
            for (int i = 0; i < descriptors.Count; i++)
            {
                GraphEdgeDescriptor d = descriptors[i];
                if (d.Edge is { } edge)
                {
                    described.Add(edge);
                }

                _edges.Add(($"{prefix}/e{i}", d, scope, slot,
                    templateIndex is { } t ? new EdgeTemplateKey(t, i) : null));
            }

            for (int i = 0; i < graphEdges.Count; i++)
            {
                StateEdge edge = graphEdges[i];
                if (described.Contains(edge))
                {
                    continue;
                }

                StateNode destination = edge.WrittenNode
                                        ?? (edge.AdditionalWrittenNodes is { Count: > 0 } more ? more[0] : null)
                                        ?? (StateNode?)_build.ExternalNodes.FirstOrDefault(n => n.Kind == ExternalState.PlayerContext)
                                        ?? edge.Source;
                Diagnostics.Add($"edge {prefix}/u{i}: {edge.GetType().Name} from {edge.Source.Name} has no descriptor");
                _edges.Add(($"{prefix}/u{i}", new GraphEdgeDescriptor(edge.Source, destination, edge.MessageType.Name,
                    edge.DeclaredEffect ?? EdgeEffect.SetValue)
                {
                    Kind = GraphEdgeKind.Undescribed,
                    Edge = edge,
                    Reads = edge.DeclaredReads ?? []
                }, scope, slot, null));
            }
        }
    }

    private sealed class Draft(
        string key,
        StateNode node,
        RuleGraphScope scope,
        RuleGraphNodeOrigin origin,
        NodeProvenance? provenance,
        int? slot,
        string? playerName,
        NodeTemplateKey? template,
        int group)
    {
        internal StateNode Node { get; } = node;
        internal RuleGraphScope Scope { get; } = scope;
        internal RuleGraphNodeOrigin Origin { get; } = origin;
        internal NodeProvenance? Provenance { get; } = provenance;
        internal int Group { get; } = group;
        internal HashSet<string> Chains { get; } = new(StringComparer.Ordinal);

        internal RuleGraphNode Seal(IReadOnlyList<string> ruleIds) =>
            new(key, Node, Scope, Origin, Provenance?.Ruleset, Provenance?.Owners ?? [], ruleIds,
                slot, playerName, Provenance?.TeamSide, template,
                Chains.Count > 0 ? Chains : _noChains, [Node]);
    }
}

/// <summary>Which part of the graph a <see cref="RuleGraphNode" /> or <see cref="RuleGraphEdge" /> belongs to.</summary>
public enum RuleGraphScope
{
    /// <summary>The shared game scope: the root, contexts, enrichments, and <c>for: match</c> rule nodes.</summary>
    Game,

    /// <summary>One side of a <c>for: each_team</c> ruleset (see <see cref="RuleGraphNode.TeamSide" />).</summary>
    Team,

    /// <summary>A node materialised for one player from a per-player template.</summary>
    Player,

    /// <summary>A stand-in for state outside the graph (<see cref="ExternalStateNode" />).</summary>
    External
}

/// <summary>What part of the engine made a <see cref="RuleGraphNode" />.</summary>
public enum RuleGraphNodeOrigin
{
    /// <summary>The graph's root.</summary>
    Root,

    /// <summary>A value node the entity scanner writes.</summary>
    EntityValue,

    /// <summary>A built-in event enrichment's transient node.</summary>
    Enrichment,

    /// <summary>A built-in context (round number, alive, survived), a team aggregate, or a team roster.</summary>
    Context,

    /// <summary>A plant-site round fact.</summary>
    RoundFact,

    /// <summary>A node a ruleset's stat or highlight made, companions included (guards, first-tick nodes, entity pulls, <c>.count</c>).</summary>
    Rule,

    /// <summary>An <see cref="ExternalStateNode" />.</summary>
    External,

    /// <summary>A node with no provenance, from a hand-built graph or template.</summary>
    Unknown
}

/// <summary>A node's position in its per-player template: which template, and its index in the materialised node list.</summary>
/// <param name="TemplateIndex">The template's index on the graph.</param>
/// <param name="Ordinal">The node's index in <see cref="PerPlayerNodeTemplate.MaterializedPlayer.Nodes" />.</param>
public readonly record struct NodeTemplateKey(int TemplateIndex, int Ordinal);

/// <summary>A per-player edge's position in its template: which template, and its descriptor's index.</summary>
/// <param name="TemplateIndex">The template's index on the graph.</param>
/// <param name="Ordinal">The descriptor's index in <see cref="PerPlayerNodeTemplate.MaterializedPlayer.EdgeDescriptors" />.</param>
public readonly record struct EdgeTemplateKey(int TemplateIndex, int Ordinal);

/// <summary>One node of a <see cref="RuleGraph" />.</summary>
/// <param name="Key">
///     Stable across runs of the same rulesets and profile: <c>g/{index in BuildResult.Nodes}</c>,
///     <c>x/{external name}</c>, <c>p{slot}/t{template}/{ordinal}</c> for a player's node, and
///     <c>t{template}/{ordinal}</c> once collapsed or in a template preview. Key selections and
///     breakpoints by it: names repeat across players and rulesets.
/// </param>
/// <param name="Node">The engine node (after <see cref="RuleGraph.CollapsePlayers" />, the first player's copy).</param>
/// <param name="Scope">Which part of the graph it belongs to.</param>
/// <param name="Origin">What part of the engine made it.</param>
/// <param name="Ruleset">
///     The bare id of the ruleset that made it, for a rule node. Its <c>_chain_{ruleset}</c> join key
///     is what <see cref="PerPlayerColumnAssignment.ChainId" /> carries.
/// </param>
/// <param name="Owners">
///     The <c>{ruleset}.{stat}</c> or <c>{ruleset}.{highlight}</c> declarers that resolve to this
///     node, the one whose build made it first. A companion (a guard, a first-tick node, an entity
///     pull, a highlight's <c>.count</c>) is owned by the stat or highlight that made it. Empty for
///     scaffolding.
/// </param>
/// <param name="RuleIds">Every key of the build's and the player's rule-id maps that resolves to this node.</param>
/// <param name="PlayerSlot">The player's slot, for a materialised player's node; <c>null</c> otherwise.</param>
/// <param name="PlayerName">The player's name, or the preview name for a template preview.</param>
/// <param name="TeamSide">2 or 3 for a node of a <c>for: each_team</c> side.</param>
/// <param name="Template">The node's template position, for a per-player node.</param>
/// <param name="HighlightChains">
///     The highlights this node is part of, by <c>_chain_{highlight}</c> name (what
///     <see cref="RuleChainEvent.ChainName" /> carries): the highlight's logic node, what feeds it back
///     to the root and the game scaffolding, and its <c>.count</c>. Not the ruleset join key in
///     <see cref="PerPlayerColumnAssignment.ChainId" />, which is <c>_chain_{ruleset}</c>; a ruleset and
///     a highlight that share an id produce the same string with different meanings.
/// </param>
/// <param name="Instances">Every engine node this view node stands for: one, or every player's copy once collapsed.</param>
public sealed record RuleGraphNode(
    string Key,
    StateNode Node,
    RuleGraphScope Scope,
    RuleGraphNodeOrigin Origin,
    string? Ruleset,
    IReadOnlyList<string> Owners,
    IReadOnlyList<string> RuleIds,
    int? PlayerSlot,
    string? PlayerName,
    int? TeamSide,
    NodeTemplateKey? Template,
    IReadOnlySet<string> HighlightChains,
    IReadOnlyList<StateNode> Instances);

/// <summary>One edge of a <see cref="RuleGraph" />, resolved to its two view nodes.</summary>
/// <param name="Key">Stable like <see cref="RuleGraphNode.Key" />: <c>g/e{i}</c>, <c>p{slot}/t{template}/e{i}</c>, <c>t{template}/e{i}</c>.</param>
/// <param name="Source">The source view node.</param>
/// <param name="Destination">The destination view node.</param>
/// <param name="Descriptor">
///     The engine's descriptor. Its <see cref="GraphEdgeDescriptor.Edge" /> is where the fire count
///     and applied messages live; several view edges can share one engine edge.
/// </param>
/// <param name="Scope">Which part of the graph it belongs to.</param>
/// <param name="PlayerSlot">The player's slot, for a materialised player's edge.</param>
/// <param name="Template">The edge's template position, for a per-player edge.</param>
public sealed record RuleGraphEdge(
    string Key,
    RuleGraphNode Source,
    RuleGraphNode Destination,
    GraphEdgeDescriptor Descriptor,
    RuleGraphScope Scope,
    int? PlayerSlot,
    EdgeTemplateKey? Template);
