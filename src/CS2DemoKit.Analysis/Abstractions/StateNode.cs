namespace CS2DemoKit.Analysis.Abstractions;

/// <summary>
///     Abstract base for all named nodes in the analysis graph.
/// </summary>
/// <remarks>
///     All concrete nodes must derive from either <see cref="BoolNode" /> (boolean state) or
///     <see cref="ValueNode{T}" /> (typed value). <see cref="StateNode" /> itself carries no
///     activation state — that is the responsibility of subclasses.
/// </remarks>
public abstract class StateNode
{
    /// <summary>Whether this node is currently considered active.</summary>
    public abstract bool IsActive { get; }

    /// <summary>The unique display name of this node, used in diagnostics and the rule chain timeline.</summary>
    public abstract string Name { get; }

    /// <summary>
    ///     Optional secondary label for display below the node name (e.g. player name).
    /// </summary>
    public virtual string? Subtitle => null;

    /// <summary>
    ///     Returns a string representation of the node's current value for display in the
    ///     visualisation, or <c>null</c> for boolean nodes (the renderer falls back to
    ///     "ACTIVE" / "inactive"). Overridden by <see cref="ValueNode{T}" />.
    /// </summary>
    public virtual string? GetDisplayValue() => null;

    /// <summary>
    ///     The node's current value as a <see cref="float" /> for numeric comparison (captured into
    ///     <c>NodeSnapshot.NumericValue</c> and read by graph-breakpoint <c>value</c> conditions), or
    ///     <c>null</c> for bool / non-numeric nodes. <see cref="float" /> (not <see cref="double" />)
    ///     keeps the per-slot snapshot footprint at 8 bytes and is exact for integer counters.
    ///     Overridden by <see cref="ValueNode{T}" />.
    /// </summary>
    public virtual float? GetNumericValue() => null;
}
