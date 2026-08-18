namespace CS2DemoKit.Analysis.Building;

/// <summary>
///     An <see cref="IEntityValueAt" /> that resolves every read to <c>null</c> — the placeholder passed
///     to predicates that compile to the entity-accessor shape but never actually read entities
///     (pure-event / bare-<c>player</c> conditions). Entity-read conditions get a positioned
///     <see cref="EntityValueCache" /> instead. Shared by the edge breakpoint path and the node
///     input-event matcher so neither allocates its own.
/// </summary>
public sealed class NoopEntityValueAt : IEntityValueAt
{
    /// <summary>The shared no-op instance.</summary>
    public static readonly NoopEntityValueAt Instance = new();

    private NoopEntityValueAt()
    {
    }

    /// <inheritdoc />
    public object? GetValue(string providerName, int slot) => null;
}
