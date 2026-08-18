namespace CS2DemoKit.Analysis.Abstractions;

/// <summary>
///     A typed condition on one or more source nodes' current values, used as an input to a
///     <see cref="ConjunctionNode" /> or <see cref="DisjunctionNode" />. The edge is satisfied
///     when every source node is active and the condition predicate returns <c>true</c>.
///     Single-source conditions (<see cref="ConditionalEdge{T}" />) are the v1 shape;
///     <see cref="MultiSourceConditionalEdge" /> generalizes to N declared sources.
/// </summary>
public interface IConditionalEdge
{
    /// <summary>Human-readable label shown on the edge in the graph visualisation.</summary>
    string ConditionLabel { get; }

    /// <summary>Whether the condition is currently satisfied.</summary>
    bool IsSatisfied { get; }

    /// <summary>
    ///     The primary node this condition tests — for multi-source conditions, the first
    ///     declared source (kept for display and single-source consumers). Dirty-marking and
    ///     recompute bucketing must use <see cref="Sources" />, never this property alone.
    /// </summary>
    StateNode Source { get; }

    /// <summary>
    ///     Every node whose value this condition reads. The evaluator unions all
    ///     sources' writers when building its dirty-marking and recompute indexes, so a write to
    ///     ANY source triggers recomputation of the owning logic node. Single-source conditions
    ///     yield exactly <c>[Source]</c> — the default implementation — which keeps 1-source
    ///     edges behavior-identical to the pre-A2 contract.
    /// </summary>
    IReadOnlyList<StateNode> Sources => [Source];
}
