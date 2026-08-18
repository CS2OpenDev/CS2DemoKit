namespace CS2DemoKit.Analysis.Abstractions;

/// <summary>
///     A lightweight snapshot of one node's state at a specific point in message evaluation.
///     Captured after every individual message; used by the visualization layer to seek
///     to any message without replaying the graph from scratch.
/// </summary>
/// <param name="isActive">The node's boolean active state at this moment.</param>
/// <param name="displayValue">
///     The node's current display value, or <c>null</c> for pure boolean nodes.
///     Non-null for <see cref="ValueNode{T}" /> — shows the value at this moment in time.
/// </param>
/// <param name="numericValue">
///     The node's value as a <see cref="float" /> at this moment, or <c>null</c> for bool /
///     non-numeric nodes. Read by graph-breakpoint <c>value</c> conditions so they evaluate against
///     the snapshot with no re-eval. <see cref="float" /> keeps the per-slot cost at 8 bytes.
/// </param>
public readonly struct NodeSnapshot(bool isActive, string? displayValue = null, float? numericValue = null)
{
    /// <summary>The node's boolean active state at the moment this snapshot was captured.</summary>
    public bool IsActive { get; } = isActive;

    /// <summary>The node's display value at the moment of capture, or <c>null</c> for pure bool nodes.</summary>
    public string? DisplayValue { get; } = displayValue;

    /// <summary>The node's numeric value at capture (for breakpoint <c>value</c> conditions), or <c>null</c>.</summary>
    public float? NumericValue { get; } = numericValue;
}
