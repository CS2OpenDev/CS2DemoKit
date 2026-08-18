namespace CS2DemoKit.Analysis.Abstractions;

/// <summary>
///     A <see cref="BoolNode" /> that resets to a default value at round boundaries.
///     Unlike <see cref="RoundScopedValueNode{T}" /> with <c>T=bool</c>, this uses
///     <see cref="BoolNode.IsActive" /> semantics where <c>IsActive == Value</c>.
/// </summary>
public abstract class RoundScopedBoolNode : BoolNode, IRoundScopedNode
{
    protected RoundScopedBoolNode(bool defaultValue)
    {
        DefaultValue = defaultValue;
        if (defaultValue)
        {
            Activate();
        }
        else
        {
            Deactivate();
        }
    }

    /// <summary>The boolean state this node returns to on each round boundary.</summary>
    public bool DefaultValue { get; }

    /// <inheritdoc />
    public void Reset()
    {
        if (DefaultValue)
        {
            Activate();
        }
        else
        {
            Deactivate();
        }
    }
}
