#region

using CS2DemoKit.Analysis.Abstractions;

#endregion

namespace CS2DemoKit.Analysis.Nodes;

/// <summary>
///     A concrete <see cref="ValueNode{T}" /> with an externally-supplied name and subtitle. Used
///     when the rule builder needs a named typed-value node without subclassing.
/// </summary>
/// <param name="name">Unique display name for diagnostics and the rule chain timeline.</param>
/// <param name="subtitle">Optional secondary label (e.g. player name) displayed below the name.</param>
public sealed class GenericValueNode<T>(string name, string? subtitle = null) : ValueNode<T>
{
    /// <inheritdoc />
    public override string Name { get; } = name;

    /// <inheritdoc />
    public override string? Subtitle { get; } = subtitle;
}
