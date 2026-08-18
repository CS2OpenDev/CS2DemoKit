#region

using CS2DemoKit.Parser;
using CS2DemoKit.Parser.GameEvents;

#endregion

namespace CS2DemoKit.Analysis.Abstractions;

/// <summary>
///     Read-only view of an analysed demo passed to the state graph evaluator.
///     <para>
///         The interface deliberately omits mutable entity state — callers should use
///         <see cref="CreateEntityLayer" /> to obtain a fresh, forward-only
///         <see cref="EntityStateLayer" />.
///     </para>
/// </summary>
public interface IDemoContext
{
    /// <summary>The parsed demo this context was built from (zero-copy reference).</summary>
    ParsedDemo Demo { get; }

    /// <summary>
    ///     Rounds derived from <c>round_freeze_end</c> / <c>round_officially_ended</c> pairs.
    /// </summary>
    IReadOnlyList<RoundInfo> Rounds { get; }

    /// <summary>
    ///     Creates a new <see cref="EntityStateLayer" /> positioned at tick 0.
    ///     Each call returns an independent instance — safe to use from parallel rule branches.
    /// </summary>
    EntityStateLayer CreateEntityLayer();

    /// <summary>
    ///     Returns all events in the inclusive tick range
    ///     [<paramref name="fromTick" />, <paramref name="toTick" />].
    ///     O(log n + k) via binary search.
    /// </summary>
    IReadOnlyList<GameEvent> EventsInRange(int fromTick, int toTick);

    /// <summary>
    ///     Returns all events whose concrete runtime type is exactly <typeparamref name="T" />,
    ///     in tick order.  O(1) on repeat calls (results are cached per type).
    ///     <para>
    ///         <b>Exact-type lookup:</b> calling <c>EventsOfType&lt;GameEvent&gt;()</c> or any
    ///         abstract intermediate type returns an empty list because events are indexed by
    ///         their concrete type.  Use
    ///         <see cref="Demo" /><c>.AllGameEvents.OfType&lt;T&gt;()</c> to query by base type.
    ///     </para>
    /// </summary>
    IReadOnlyList<GameEvent> EventsOfType<T>() where T : class;
}
