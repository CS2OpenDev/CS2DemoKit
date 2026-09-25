#region

using CS2DemoKit.Analysis.Abstractions;

#endregion

namespace CS2DemoKit.Analysis.Graphs;

/// <summary>
///     Where the builder made a node: which part of the engine, and for a rule node, which rulesets'
///     stats or highlights declare it. <see cref="RuleGraph" /> reads it into
///     <see cref="RuleGraphNode" />.
/// </summary>
/// <param name="Origin">The part of the engine that made the node.</param>
/// <param name="Owners">
///     The <c>{ruleset}.{stat}</c> or <c>{ruleset}.{highlight}</c> declarers, the one whose build
///     made the node first; a stat another ruleset declares identically shares the node and is added
///     after it. Empty for scaffolding.
/// </param>
/// <param name="Ruleset">The first owner's ruleset id; <c>null</c> for scaffolding.</param>
/// <param name="TeamSide">2 or 3 for a node of a <c>for: each_team</c> side; <c>null</c> otherwise.</param>
/// <param name="HighlightChain">The <c>_chain_{highlight}</c> name for a node a highlight made; <c>null</c> otherwise.</param>
internal readonly record struct NodeProvenance(
    RuleGraphNodeOrigin Origin,
    IReadOnlyList<string> Owners,
    string? Ruleset,
    int? TeamSide,
    string? HighlightChain);

/// <summary>
///     Collects <see cref="NodeProvenance" /> while the builder runs. The builder marks a node list's
///     count before a block and stamps what the block appended after it, so no node creation site
///     changes.
/// </summary>
internal sealed class ProvenanceRecorder
{
    private readonly Dictionary<StateNode, (RuleGraphNodeOrigin Origin, List<string> Owners, string? Ruleset, int? TeamSide, string? Chain)> _entries =
        new(ReferenceEqualityComparer.Instance);

    /// <summary>Stamps every node of <paramref name="nodes" /> from index <paramref name="from" /> on that has no provenance yet.</summary>
    internal void Stamp(IReadOnlyList<StateNode> nodes, int from, RuleGraphNodeOrigin origin,
        string? owner = null, string? ruleset = null, int? teamSide = null, string? chain = null)
    {
        for (int i = from; i < nodes.Count; i++)
        {
            _entries.TryAdd(nodes[i], (origin, owner is null ? [] : [owner], ruleset, teamSide, chain));
        }
    }

    /// <summary>
    ///     Stamps what a stat's lowering appended as its rule nodes; when it appended nothing, the stat
    ///     was deduplicated onto an existing node, which gains it as a further owner.
    /// </summary>
    internal void StampRule(IReadOnlyList<StateNode> nodes, int from, string ruleset, string id,
        IReadOnlyDictionary<string, StateNode> nodesByRuleId, int? teamSide = null, string? chain = null)
    {
        string owner = $"{ruleset}.{id}";
        if (nodes.Count > from)
        {
            Stamp(nodes, from, RuleGraphNodeOrigin.Rule, owner, ruleset, teamSide, chain);
            return;
        }

        if (nodesByRuleId.TryGetValue(owner, out StateNode? shared)
            && _entries.TryGetValue(shared, out var entry)
            && !entry.Owners.Contains(owner, StringComparer.Ordinal))
        {
            entry.Owners.Add(owner);
        }
    }

    /// <summary>Everything stamped, by node.</summary>
    internal IReadOnlyDictionary<StateNode, NodeProvenance> ToDictionary()
    {
        Dictionary<StateNode, NodeProvenance> map = new(_entries.Count, ReferenceEqualityComparer.Instance);
        foreach ((StateNode node, var e) in _entries)
        {
            map[node] = new NodeProvenance(e.Origin, e.Owners, e.Ruleset, e.TeamSide, e.Chain);
        }

        return map;
    }

    /// <summary>The provenance of each of <paramref name="nodes" />, in order; <see cref="RuleGraphNodeOrigin.Unknown" /> for one never stamped.</summary>
    internal IReadOnlyList<NodeProvenance> Aligned(IReadOnlyList<StateNode> nodes)
    {
        NodeProvenance[] aligned = new NodeProvenance[nodes.Count];
        for (int i = 0; i < nodes.Count; i++)
        {
            aligned[i] = _entries.TryGetValue(nodes[i], out var e)
                ? new NodeProvenance(e.Origin, e.Owners, e.Ruleset, e.TeamSide, e.Chain)
                : new NodeProvenance(RuleGraphNodeOrigin.Unknown, [], null, null, null);
        }

        return aligned;
    }
}
