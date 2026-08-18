namespace CS2DemoKit.Analysis.Abstractions;

/// <summary>
///     Marker for nodes whose state should be cleared at round boundaries. The evaluator calls
///     <see cref="Reset" /> on each implementation when a new round begins.
/// </summary>
public interface IRoundScopedNode
{
    /// <summary>Returns the node to its default state for the start of a new round.</summary>
    void Reset();
}
