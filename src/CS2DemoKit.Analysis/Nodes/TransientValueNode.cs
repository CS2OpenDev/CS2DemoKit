#region

using CS2DemoKit.Analysis.Abstractions;

#endregion

namespace CS2DemoKit.Analysis.Nodes;

/// <summary>
///     A typed value node that is reset to its default before each dispatch of its
///     associated event type. Excluded from snapshot capture. Always active once the
///     first event of its type has been processed — gating is done by companion
///     <see cref="TransientBoolNode" /> instances, not by this node's activation state.
/// </summary>
public sealed class TransientValueNode<T>(string name, T defaultValue = default!, string? subtitle = null) : ValueNode<T>, ITransientNode
{
    /// <inheritdoc />
    public override string Name { get; } = name;

    /// <inheritdoc />
    public override string? Subtitle { get; } = subtitle;

    /// <inheritdoc />
    public void Reset() => SetValue(defaultValue);
}
