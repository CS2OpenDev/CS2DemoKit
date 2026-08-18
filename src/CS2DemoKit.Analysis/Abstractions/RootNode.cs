namespace CS2DemoKit.Analysis.Abstractions;

/// <summary>
///     The always-active entry point of a <c>StateGraph</c>.
///     Every entry edge — one with no prerequisite node — uses <see cref="RootNode" /> as its source,
///     ensuring the graph is fully connected with no orphaned nodes.
/// </summary>
public sealed class RootNode : BoolNode
{
    /// <summary>Creates a new <see cref="RootNode" /> and immediately activates it.</summary>
    public RootNode() => Activate();

    /// <inheritdoc />
    public override string Name => "Root";
}
