namespace CS2DemoKit.Analysis.Abstractions;

/// <summary>
///     A directed, conditional link between nodes in the analysis graph.
/// </summary>
/// <param name="source">Must be active for this edge to be eligible.</param>
public abstract class StateEdge(StateNode source)
{
    /// <summary>
    ///     Additional nodes this edge writes to beyond <see cref="WrittenNode" />. Used by the
    ///     topological sort to ensure correct ordering for multi-write edges (e.g., enrichment
    ///     edges that populate multiple transient nodes in a single evaluation).
    /// </summary>
    public virtual IReadOnlyList<StateNode>? AdditionalWrittenNodes => null;

    /// <summary>
    ///     The declared effect of this edge. Used by the evaluator's topological sort to
    ///     determine correct evaluation order within a dispatch slot. Activate/SetValue edges
    ///     are ordered before edges that source from their written node. Deactivate edges are
    ///     ordered after.
    /// </summary>
    public virtual EdgeEffect? DeclaredEffect => null;

    /// <summary>
    ///     Nodes this edge's condition reads beyond the implicit <see cref="Source" /> read
    ///     (read-aware topological ordering). Within a dispatch slot, the
    ///     topological sort orders this edge after Activate/SetValue writers of every declared
    ///     node and before Deactivate writers — the same rule the <see cref="Source" /> read
    ///     already gets. The v1 builder declares no reads (<c>null</c>), which leaves the sort's
    ///     constraint graph — and therefore evaluation order — identical to the pre-A1 engine.
    ///     Declaring a node that is also the <see cref="Source" /> is harmless: duplicate
    ///     ordering constraints collapse in the sort.
    /// </summary>
    public virtual IReadOnlyList<StateNode>? DeclaredReads => null;

    /// <summary>
    ///     The dispatch key for this edge. The evaluator only calls <see cref="TryApply" />
    ///     when the current message's dispatch key matches this type.
    /// </summary>
    public abstract Type MessageType { get; }

    /// <summary>
    ///     Times this edge applied (fired) during the current or most recent evaluation.
    ///     Always-on trace tier: owned by <c>StateGraphEvaluator</c> — zeroed at
    ///     evaluation start, incremented in the applied branch. Shares the graph's existing
    ///     single-evaluation invariant: a graph is driven by one evaluation at a time, so
    ///     this is deliberately not thread-safe.
    /// </summary>
    public int FireCount { get; internal set; }

    /// <summary>
    ///     The node that must be active for this edge to be evaluated.
    /// </summary>
    public StateNode Source { get; } = source;

    /// <summary>
    ///     The node this edge writes to when its condition is met. Used to build the
    ///     conjunction propagation index — the evaluator uses this to know which
    ///     conjunction nodes to recompute after each message type.
    ///     Returns <c>null</c> for edges that do not have a single obvious destination.
    /// </summary>
    public virtual StateNode? WrittenNode => null;

    /// <summary>
    ///     Evaluates the edge's condition against the current context and, if satisfied,
    ///     applies the edge's effect. Returns <c>true</c> if the effect was applied.
    /// </summary>
    public abstract bool TryApply(EvaluationContext context);

    /// <summary>
    ///     Fast path: receives the pre-extracted typed payload, skipping redundant type checks.
    ///     Base classes override this; custom edges fall through to <see cref="TryApply" />.
    /// </summary>
    public virtual bool TryApplyDirect(object payload, EvaluationContext context) =>
        TryApply(context);
}
