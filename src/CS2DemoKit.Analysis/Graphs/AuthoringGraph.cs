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
///         only the shared scaffolding (the root, the contexts and every event enrichment), while the
///         ruleset's own per-player stats live in un-materialized <see cref="PerPlayerNodeTemplate" />s and
///         never appear. A bare <c>count: kill</c> would show all of the scaffolding and none of the
///         author's actual rules.
///     </para>
///     <para>
///         This helper draws the build through <see cref="RuleGraph.FromBuild(BuildResult, bool, string)" />,
///         which previews each per-player template once (slot 0, demo-less), anchors on the ruleset's
///         <em>declared</em> outputs (every stat, every tally target, and each highlight with its
///         <c>.count</c>, found through the <c>{ruleset}.{id}</c> rule ids), then keeps only those anchors
///         plus their transitive <em>upstream</em>: the sources that fire into them and the nodes they read.
///         The walk stops at the root and at the state outside the graph, which every rule would otherwise
///         reach. Unreferenced scaffolding is dropped, so the graph's size tracks the ruleset's real
///         complexity — a single kill stat is two nodes (<c>Root → kills</c>), and gating/<c>when:</c>
///         conditions grow it naturally. Materialized per-player nodes are flagged
///         (<see cref="AuthoringGraphNode.IsPerPlayer" />) so the view can mark "this materializes per
///         player", and a read is drawn as its own edge (<see cref="AuthoringGraphEdge.IsRead" />).
///     </para>
/// </summary>
public static class AuthoringGraph
{
    /// <summary>
    ///     Builds the focused authoring graph from <paramref name="build" /> (a demo-less
    ///     <c>RuleChainBuilder.Build</c> output) and the <paramref name="rulesets" /> it was built
    ///     from — the latter supply the declared stat/highlight ids that anchor the filter.
    /// </summary>
    /// <exception cref="InvalidOperationException">A per-player template cannot materialise without a demo.</exception>
    public static AuthoringGraphModel Build(BuildResult build, IReadOnlyCollection<CheckedRuleset> rulesets)
    {
        // The author's declared outputs: every stat, every tally target (a tally has no node of its
        // own; its buckets are what a scoreboard shows), and every highlight with its .count.
        HashSet<string> declaredIds = new(StringComparer.Ordinal);
        foreach (CheckedRuleset rs in rulesets)
        {
            foreach (CheckedStat s in rs.Stats)
            {
                declaredIds.Add(s.StatId);
                foreach ((int _, string target) in s.TallyThresholds ?? [])
                {
                    declaredIds.Add(target);
                }
            }

            foreach (CheckedHighlight h in rs.Highlights)
            {
                declaredIds.Add(h.HighlightId);
                declaredIds.Add(h.CountNodeId);
            }
        }

        RuleGraph view = RuleGraph.FromBuild(build, true, "each player", true);

        HashSet<RuleGraphNode> anchors = new(ReferenceEqualityComparer.Instance);
        foreach (RuleGraphNode node in view.Nodes)
        {
            if (node.RuleIds.Any(key => IsDeclared(key, declaredIds)))
            {
                anchors.Add(node);
            }
        }

        // Keep only anchors + their transitive upstream (sources and reads). This is the reduction: a
        // node survives iff it feeds a declared output, so unreferenced scaffolding falls away.
        Dictionary<RuleGraphNode, List<RuleGraphNode>> upstream = new(ReferenceEqualityComparer.Instance);
        foreach (RuleGraphEdge edge in view.Edges)
        {
            if (!upstream.TryGetValue(edge.Destination, out List<RuleGraphNode>? list))
            {
                upstream[edge.Destination] = list = [];
            }

            list.Add(edge.Source);
            foreach (StateNode read in edge.Descriptor.Reads)
            {
                if (view.TryGetNode(read, out RuleGraphNode? reader))
                {
                    list.Add(reader);
                }
            }
        }

        HashSet<RuleGraphNode> keep = new(anchors, ReferenceEqualityComparer.Instance);
        Queue<RuleGraphNode> frontier = new(anchors);
        while (frontier.Count > 0)
        {
            RuleGraphNode current = frontier.Dequeue();
            if (!upstream.TryGetValue(current, out List<RuleGraphNode>? feeders))
            {
                continue;
            }

            foreach (RuleGraphNode feeder in feeders)
            {
                if (feeder.Scope == RuleGraphScope.External || !keep.Add(feeder))
                {
                    continue;
                }

                // The root is kept, so the graph stays anchored, but has nothing upstream to walk.
                if (feeder.Origin != RuleGraphNodeOrigin.Root)
                {
                    frontier.Enqueue(feeder);
                }
            }
        }

        // Emit kept nodes in a stable order, then the edges among them.
        Dictionary<RuleGraphNode, int> index = new(ReferenceEqualityComparer.Instance);
        List<AuthoringGraphNode> nodeModels = [];
        foreach (RuleGraphNode n in view.Nodes)
        {
            if (!keep.Contains(n))
            {
                continue;
            }

            index[n] = nodeModels.Count;
            nodeModels.Add(new AuthoringGraphNode(
                n.Node.Name, n.Node.Subtitle, n.Node is RootNode, n.Scope == RuleGraphScope.Player,
                n.Node.GetDisplayValue(), n.HighlightChains));
        }

        List<AuthoringGraphEdge> edgeModels = [];
        HashSet<(int, int, string)> seenEdges = [];
        foreach (RuleGraphEdge e in view.Edges)
        {
            if (!index.TryGetValue(e.Destination, out int dst))
            {
                continue;
            }

            GraphEdgeDescriptor d = e.Descriptor;
            if (index.TryGetValue(e.Source, out int src) && seenEdges.Add((src, dst, d.Label)))
            {
                edgeModels.Add(new AuthoringGraphEdge(src, dst, d.Label, d.Effect, d.ConditionLabel));
            }

            foreach (StateNode read in d.Reads)
            {
                if (view.TryGetNode(read, out RuleGraphNode? reader) && index.TryGetValue(reader, out int readIndex)
                                                                    && seenEdges.Add((readIndex, dst, d.Label)))
                {
                    edgeModels.Add(new AuthoringGraphEdge(readIndex, dst, d.Label, d.Effect, d.ConditionLabel)
                    {
                        IsRead = true
                    });
                }
            }
        }

        return new AuthoringGraphModel(nodeModels, edgeModels);
    }

    /// <summary>Whether a rule-id key equals, or is <c>{prefix}.</c>-qualified by, a declared id.</summary>
    private static bool IsDeclared(string key, HashSet<string> declaredIds)
    {
        if (declaredIds.Contains(key))
        {
            return true;
        }

        foreach (string id in declaredIds)
        {
            if (key.EndsWith("." + id, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
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
    /// <param name="ChainIds">
    ///     The highlights this node is part of, by <c>_chain_{highlight}</c> name
    ///     (<see cref="RuleGraphNode.HighlightChains" />): not the ruleset join key a table column's
    ///     <see cref="PerPlayerColumnAssignment.ChainId" /> carries.
    /// </param>
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
        string? ConditionLabel)
    {
        /// <summary>
        ///     True when the source is a node the destination's edge reads (a gate, a tally's stat, a
        ///     compute's input) rather than the node it fires from.
        /// </summary>
        public bool IsRead { get; init; }
    }

    /// <summary>The focused, demo-less authoring graph: filtered nodes + the edges among them.</summary>
    public sealed record AuthoringGraphModel(
        IReadOnlyList<AuthoringGraphNode> Nodes,
        IReadOnlyList<AuthoringGraphEdge> Edges);
}
