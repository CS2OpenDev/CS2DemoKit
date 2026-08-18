#region

using CS2DemoKit.Analysis.Abstractions;

#endregion

namespace CS2DemoKit.Analysis.Nodes;

/// <summary>
///     A concrete <see cref="BoolNode" /> with an externally-supplied name and subtitle. Used when
///     the rule builder needs a named boolean-state node without subclassing.
/// </summary>
/// <param name="name">Unique display name for diagnostics and the rule chain timeline.</param>
/// <param name="subtitle">Optional secondary label (e.g. player name) displayed below the name.</param>
public sealed class GenericBoolNode(string name, string? subtitle = null) : BoolNode
{
    /// <inheritdoc />
    public override string Name { get; } = name;

    /// <inheritdoc />
    public override string? Subtitle { get; } = subtitle;
}
