#region

using CS2DemoKit.Analysis.Abstractions;

#endregion

namespace CS2DemoKit.Analysis.Nodes;

/// <summary>
///     A boolean node that is reset to inactive before each dispatch of its associated
///     event type. Excluded from snapshot capture.
/// </summary>
/// <param name="name">Unique display name for diagnostics and the rule chain timeline.</param>
/// <param name="subtitle">Optional secondary label (e.g. player name) displayed below the name.</param>
public sealed class TransientBoolNode(string name, string? subtitle = null) : BoolNode, ITransientNode
{
    /// <inheritdoc />
    public override string Name { get; } = name;

    /// <inheritdoc />
    public override string? Subtitle { get; } = subtitle;

    /// <inheritdoc />
    public void Reset() => Deactivate();
}
