namespace CS2DemoKit.Analysis.Abstractions;

/// <summary>
///     Non-generic base for synthesized entity-state change events. Carries the metadata
///     common to every change (tick, old/new value) so edges that don't care about the
///     concrete marker type can introspect uniformly. Edges that DO care subscribe to
///     the closed-generic <see cref="EntityValueChangedEvent{TMarker}" /> directly —
///     the dispatch key (<c>typeof(EntityValueChangedEvent&lt;TMarker&gt;)</c>) is
///     specific to each (entity class, field path) pair, so rule-chain dispatch routes
///     each subscription independently with no shared dispatch list overhead.
/// </summary>
public abstract class EntityValueChangedEvent
{
    /// <summary>The current value read from the entity state at <see cref="Tick" />.</summary>
    public object? NewValue { get; init; }

    /// <summary>The previous value cached by the scanner, or <c>null</c> on first observation.</summary>
    public object? OldValue { get; init; }

    /// <summary>Demo tick at which the change was observed.</summary>
    public int Tick { get; init; }
}

/// <summary>
///     Strongly-typed entity change event keyed by a marker type. Each (entity class, field)
///     pair has its own empty marker class (e.g. <c>CCSGameRulesFreezePeriodMarker</c>), giving
///     each subscription its own slot in the evaluator's dispatch index.
/// </summary>
/// <typeparam name="TMarker">
///     Empty marker class identifying the entity field this event describes. Defined alongside
///     the <c>IEntityValueProvider</c> that produces these events.
/// </typeparam>
public sealed class EntityValueChangedEvent<TMarker> : EntityValueChangedEvent
{
}
