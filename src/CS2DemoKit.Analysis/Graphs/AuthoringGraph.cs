#region

using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Analysis.RulesetsV2.Resolve;

#endregion

namespace CS2DemoKit.Analysis.Graphs;

/// <summary>
///     Builds the <em>authoring-focused</em> state graph for a set of composed v2 rulesets — the graph the
///     Workbench renders while you edit, with NO demo and NO evaluation.
///     <para>
///         A raw <see cref="BuildResult" /> is a poor authoring view: demo-less, <c>build.Nodes</c> holds
///         only the always-on shared scaffolding (round/context nodes + every event enrichment), while the
///         ruleset's own per-player stats live in un-materialized <see cref="PerPlayerNodeTemplate" />s and
///         never appear. A bare <c>count: kill</c> would show ~36 scaffolding nodes and zero of the author's
///         actual rules.
///     </para>
///     <para>
///         This helper fixes both: it materializes each per-player template once (slot 0, demo-less — the
///         path <c>RulesetV2ShowScoreboardTests</c> already exercises), anchors on the ruleset's
///         <em>declared</em> stat/highlight output nodes (via column assignments + the
///         <c>{ruleset}.{id}</c> rule-id maps), then keeps only those anchors plus their transitive
///         <em>upstream</em> inputs (the events/gates that feed them). Unreferenced scaffolding is dropped,
///         so the graph's size tracks the ruleset's real complexity — a single kill stat is two nodes
///         (<c>Root → kills</c>), and gating/<c>when:</c> conditions grow it naturally. Materialized
///         per-player nodes are flagged (<see cref="AuthoringGraphNode.IsPerPlayer" />) so the view can mark
///         "this materializes per player".
///     </para>
/// </summary>
public static class AuthoringGraph
{
    private static readonly IReadOnlySet<string> _emptyChainKeys = new HashSet<string>();

    /// <summary>
    ///     Builds the focused authoring graph from <paramref name="build" /> (a demo-less
    ///     <c>RuleChainBuilder.Build</c> output) and the <paramref name="rulesets" /> it was built
    ///     from — the latter supply the declared stat/highlight ids that anchor the filter.
    /// </summary>
    public static AuthoringGraphModel Build(BuildResult build, IReadOnlyCollection<CheckedRuleset> rulesets)
    {
        // The author's declared outputs — every stat/highlight id across the composed rulesets.
        HashSet<string> declaredIds = [];
        foreach (CheckedRuleset rs in rulesets)
        {
            foreach (CheckedStat s in rs.Stats)
            {
                declaredIds.Add(s.StatId);
            }

            foreach (CheckedHighlight h in rs.Highlights)
            {
                declaredIds.Add(h.HighlightId);
            }
        }

        // Combined universe: game-scope nodes/edges + one demo-less materialization of every per-player
        // template. Per-player nodes are tracked so the view can flag them.
        List<StateNode> ordered = [.. build.Nodes];
        HashSet<StateNode> known = new(build.Nodes, ReferenceEqualityComparer.Instance);
        HashSet<StateNode> perPlayer = new(ReferenceEqualityComparer.Instance);
        List<GraphEdgeDescriptor> edges = [.. build.Edges];
        HashSet<StateNode> anchors = new(ReferenceEqualityComparer.Instance);

        foreach (PerPlayerNodeTemplate template in build.Graph.PerPlayerTemplates)
        {
            PerPlayerNodeTemplate.MaterializedPlayer p = template.Materialize(0, 0, "each player", null);
            foreach (StateNode n in p.Nodes)
            {
                perPlayer.Add(n);
                if (known.Add(n))
                {
                    ordered.Add(n);
                }
            }

            edges.AddRange(p.EdgeDescriptors);

            // Anchor on this template's declared outputs: surfaced columns + declared rule-id nodes.
            foreach (PerPlayerColumnAssignment c in p.ColumnAssignments)
            {
                anchors.Add(c.Node);
            }

            AnchorDeclared(p.NodesByRuleId, declaredIds, anchors);
        }

        // Game-scope declared outputs (match-scoped stats/highlights) anchor too.
        AnchorDeclared(build.GameNodesByRuleId, declaredIds, anchors);

        // Keep only anchors + their transitive upstream inputs (predecessors). This is the reduction: a
        // node survives iff it feeds a declared output, so unreferenced scaffolding falls away.
        Dictionary<StateNode, List<StateNode>> predecessors = new(ReferenceEqualityComparer.Instance);
        foreach (GraphEdgeDescriptor e in edges)
        {
            if (!predecessors.TryGetValue(e.Destination, out List<StateNode>? list))
            {
                predecessors[e.Destination] = list = [];
            }

            list.Add(e.Source);
        }

        HashSet<StateNode> keep = new(anchors, ReferenceEqualityComparer.Instance);
        Queue<StateNode> frontier = new(anchors);
        while (frontier.Count > 0)
        {
            StateNode cur = frontier.Dequeue();
            if (predecessors.TryGetValue(cur, out List<StateNode>? ps))
            {
                foreach (StateNode s in ps)
                {
                    if (keep.Add(s))
                    {
                        frontier.Enqueue(s);
                    }
                }
            }
        }

        // Emit kept nodes in a stable order, then the edges among them.
        Dictionary<StateNode, int> index = new(ReferenceEqualityComparer.Instance);
        List<AuthoringGraphNode> nodeModels = [];
        foreach (StateNode n in ordered)
        {
            if (!keep.Contains(n))
            {
                continue;
            }

            IReadOnlySet<string> chainIds =
                build.NodeChains is not null && build.NodeChains.TryGetValue(n, out IReadOnlySet<string>? keys)
                    ? keys
                    : _emptyChainKeys;

            index[n] = nodeModels.Count;
            nodeModels.Add(new AuthoringGraphNode(
                n.Name, n.Subtitle, n is RootNode, perPlayer.Contains(n), n.GetDisplayValue(), chainIds));
        }

        List<AuthoringGraphEdge> edgeModels = [];
        HashSet<(int, int, string)> seenEdges = [];
        foreach (GraphEdgeDescriptor e in edges)
        {
            if (index.TryGetValue(e.Source, out int src) && index.TryGetValue(e.Destination, out int dst)
                                                         && seenEdges.Add((src, dst, e.Label)))
            {
                edgeModels.Add(new AuthoringGraphEdge(src, dst, e.Label, e.Effect, e.ConditionLabel));
            }
        }

        return new AuthoringGraphModel(nodeModels, edgeModels);
    }

    /// <summary>Adds every node whose rule-id key equals or is <c>{prefix}.</c>-qualified by a declared id.</summary>
    private static void AnchorDeclared(
        IReadOnlyDictionary<string, StateNode>? nodesByRuleId,
        HashSet<string> declaredIds,
        HashSet<StateNode> anchors)
    {
        if (nodesByRuleId is null)
        {
            return;
        }

        foreach ((string key, StateNode node) in nodesByRuleId)
        {
            foreach (string id in declaredIds)
            {
                if (key == id || key.EndsWith("." + id, StringComparison.Ordinal))
                {
                    anchors.Add(node);
                    break;
                }
            }
        }
    }

    /// <summary>One node in the authoring graph — pre-resolved display data (no engine identity leaks out).</summary>
    /// <param name="Name">The node's rule/state name, rendered in the box.</param>
    /// <param name="Subtitle">Optional secondary label.</param>
    /// <param name="IsRoot">True for the graph's single root/entry node.</param>
    /// <param name="IsPerPlayer">
    ///     True when this node came from a materialized per-player template — it materializes once per player
    ///     at evaluation time. The view flags these so authors can tell per-player rules from shared ones.
    /// </param>
    /// <param name="DisplayValue">Pre-eval display value (skeleton), or null for boolean nodes.</param>
    /// <param name="ChainIds">The <c>_chain_{id}</c> membership keys (game-scoped chains; empty for per-player).</param>
    public sealed record AuthoringGraphNode(
        string Name,
        string? Subtitle,
        bool IsRoot,
        bool IsPerPlayer,
        string? DisplayValue,
        IReadOnlySet<string> ChainIds);

    /// <summary>One directed edge, by node index into <see cref="AuthoringGraphModel.Nodes" />.</summary>
    public sealed record AuthoringGraphEdge(
        int Source,
        int Destination,
        string Label,
        EdgeEffect Effect,
        string? ConditionLabel);

    /// <summary>The focused, demo-less authoring graph: filtered nodes + the edges among them.</summary>
    public sealed record AuthoringGraphModel(
        IReadOnlyList<AuthoringGraphNode> Nodes,
        IReadOnlyList<AuthoringGraphEdge> Edges);
}
