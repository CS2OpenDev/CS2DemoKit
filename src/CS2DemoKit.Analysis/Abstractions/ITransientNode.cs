namespace CS2DemoKit.Analysis.Abstractions;

/// <summary>
///     Marks a node as event-scoped transient state. Transient nodes are reset to their
///     default before each dispatch of their associated event type and are excluded from
///     snapshot capture (via <see cref="ISnapshotExcludedNode" />).
/// </summary>
public interface ITransientNode : ISnapshotExcludedNode
{
    void Reset();
}
