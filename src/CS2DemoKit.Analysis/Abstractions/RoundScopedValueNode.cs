namespace CS2DemoKit.Analysis.Abstractions;

/// <summary>
///     A <see cref="ValueNode{T}" /> that resets to a default value at round boundaries.
///     The evaluator calls <see cref="Reset" /> on all round-scoped nodes when a new round starts.
/// </summary>
public abstract class RoundScopedValueNode<T> : ValueNode<T>, IRoundScopedNode
{
    protected RoundScopedValueNode(T defaultValue)
    {
        DefaultValue = defaultValue;
        SetValue(defaultValue);
    }

    /// <summary>The value this node returns to on each round boundary.</summary>
    public T DefaultValue { get; }

    /// <inheritdoc />
    public void Reset() => SetValue(DefaultValue);
}
