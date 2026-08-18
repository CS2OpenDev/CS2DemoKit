namespace CS2DemoKit.Analysis.Abstractions;

/// <summary>
///     A node whose state is a boolean flag. <see cref="StateNode.IsActive" /> equals the stored
///     boolean value, so <see cref="Activate" /> sets it to <c>true</c> and
///     <see cref="Deactivate" /> sets it to <c>false</c>.
/// </summary>
/// <remarks>
///     Prefer <see cref="BoolNode" /> over plain <see cref="StateNode" /> for all boolean state in
///     the graph. Value nodes that store typed data should extend <see cref="ValueNode{T}" /> directly.
/// </remarks>
public abstract class BoolNode : ValueNode<bool>
{
    /// <inheritdoc />
    /// <remarks>Returns the stored boolean value directly, not just whether a value has been set.</remarks>
    public override bool IsActive => Value;

    /// <summary>Sets the stored value to <c>true</c> if it isn't already.</summary>
    public void Activate()
    {
        if (!Value)
        {
            SetValue(true);
        }
    }

    /// <summary>Sets the stored value to <c>false</c> if it isn't already.</summary>
    public void Deactivate()
    {
        if (Value)
        {
            SetValue(false);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    ///     Returns <c>null</c> so the visualiser uses its "ACTIVE" / "inactive" fallback labels
    ///     rather than displaying the redundant string "True" or "False".
    /// </remarks>
    public override string? GetDisplayValue() => null;
}
