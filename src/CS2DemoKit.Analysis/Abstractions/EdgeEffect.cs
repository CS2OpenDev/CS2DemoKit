namespace CS2DemoKit.Analysis.Abstractions;

/// <summary>
///     The effect an edge applies to its destination node when its condition is satisfied.
/// </summary>
public enum EdgeEffect
{
    /// <summary>Calls <see cref="BoolNode.Activate" /> on the destination.</summary>
    Activate,

    /// <summary>Calls <see cref="BoolNode.Deactivate" /> on the destination.</summary>
    Deactivate,

    /// <summary>Sets the value of a <see cref="ValueNode{T}" /> destination.</summary>
    SetValue,

    /// <summary>A conditional input edge to a <see cref="ConjunctionNode" />.</summary>
    Conjunction,

    /// <summary>A conditional input edge to a <see cref="DisjunctionNode" />.</summary>
    Disjunction
}
