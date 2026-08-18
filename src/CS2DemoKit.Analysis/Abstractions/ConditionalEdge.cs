namespace CS2DemoKit.Analysis.Abstractions;

/// <summary>
///     Factory for creating <see cref="ConditionalEdge{T}" /> instances with type inference.
/// </summary>
public static class ConditionalEdge
{
    /// <summary>Creates a conditional edge from a typed value node.</summary>
    public static IConditionalEdge From<T>(ValueNode<T> source, Func<T, bool> condition, string label)
        => new ConditionalEdge<T>(source, condition, label);

    /// <summary>
    ///     Convenience overload for <see cref="BoolNode" /> sources. The condition is implicitly
    ///     <c>v =&gt; v</c> (satisfied when the node is active).
    /// </summary>
    public static IConditionalEdge From(BoolNode source, string label = "active")
        => new ConditionalEdge<bool>(source, v => v, label);

    /// <summary>
    ///     Creates an N-source conditional edge: satisfied when ALL
    ///     <paramref name="sources" /> are active AND <paramref name="predicate" /> returns
    ///     <c>true</c>. The predicate closes over the typed source nodes and is only invoked
    ///     once every source is active, so it may read <c>node.Value</c> without null/default
    ///     guards. A 1-element source list is behavior-identical to a single-source edge.
    /// </summary>
    /// <param name="sources">Every node the predicate reads; must be non-empty.</param>
    /// <param name="predicate">The compiled condition over the sources' current values.</param>
    /// <param name="label">Human-readable label shown on the edge in the graph visualisation.</param>
    public static IConditionalEdge FromAll(IReadOnlyList<StateNode> sources, Func<bool> predicate, string label)
        => new MultiSourceConditionalEdge(sources, predicate, label);
}

/// <summary>
///     An N-source <see cref="IConditionalEdge" />: satisfied when every declared source is
///     active and the compiled predicate holds.
///     The strict generalization of <see cref="ConditionalEdge{T}" /> — a 1-element source list
///     evaluates identically to a single-source edge testing the same node.
/// </summary>
public sealed class MultiSourceConditionalEdge : IConditionalEdge
{
    private readonly Func<bool> _predicate;

    /// <summary>Creates the edge from its declared sources and compiled predicate.</summary>
    /// <param name="sources">Every node the predicate reads; must be non-empty.</param>
    /// <param name="predicate">
    ///     The condition over the sources' current values. Invoked only when all sources are
    ///     active, so it may read node values without activation guards.
    /// </param>
    /// <param name="conditionLabel">Human-readable label shown on the edge in the graph visualisation.</param>
    /// <exception cref="ArgumentException"><paramref name="sources" /> is empty.</exception>
    public MultiSourceConditionalEdge(IReadOnlyList<StateNode> sources, Func<bool> predicate, string conditionLabel)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(predicate);
        if (sources.Count == 0)
        {
            throw new ArgumentException(
                "a multi-source conditional edge needs at least one source node", nameof(sources));
        }

        Sources = sources;
        _predicate = predicate;
        ConditionLabel = conditionLabel;
    }

    /// <inheritdoc />
    public string ConditionLabel { get; }

    /// <inheritdoc />
    public bool IsSatisfied
    {
        get
        {
            for (int i = 0; i < Sources.Count; i++)
            {
                if (!Sources[i].IsActive)
                {
                    return false;
                }
            }

            return _predicate();
        }
    }

    /// <inheritdoc />
    public StateNode Source => Sources[0];

    /// <inheritdoc />
    public IReadOnlyList<StateNode> Sources { get; }
}

/// <summary>
///     A typed <see cref="IConditionalEdge" /> that tests a <see cref="ValueNode{T}" /> against a
///     <see cref="Func{T,TResult}" /> predicate.
/// </summary>
public sealed class ConditionalEdge<T>(ValueNode<T> source, Func<T, bool> condition, string conditionLabel) : IConditionalEdge
{
    /// <inheritdoc />
    public string ConditionLabel { get; } = conditionLabel;

    /// <inheritdoc />
    public bool IsSatisfied => source.IsActive && condition(source.Value);

    /// <inheritdoc />
    public StateNode Source => source;
}
