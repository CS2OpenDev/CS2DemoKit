#region

using CS2DemoKit.Analysis.Abstractions;

#endregion

namespace CS2DemoKit.Analysis.Nodes;

/// <summary>
///     A concrete <see cref="RoundScopedValueNode{T}" /> with an externally-supplied name,
///     default value, and subtitle. Resets to its default value at each round boundary.
/// </summary>
/// <param name="name">Unique display name for diagnostics and the rule chain timeline.</param>
/// <param name="defaultValue">Value the node resets to at the start of each round.</param>
/// <param name="subtitle">Optional secondary label (e.g. player name) displayed below the name.</param>
public sealed class GenericRoundScopedValueNode<T>(string name, T defaultValue, string? subtitle = null) : RoundScopedValueNode<T>(defaultValue)
{
    /// <inheritdoc />
    public override string Name { get; } = name;

    /// <inheritdoc />
    public override string? Subtitle { get; } = subtitle;
}
