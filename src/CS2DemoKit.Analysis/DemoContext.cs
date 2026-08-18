#region

using System.Collections.Concurrent;
using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Parser;
using CS2DemoKit.Parser.EntityTracking;
using CS2DemoKit.Parser.GameEvents;

#endregion

namespace CS2DemoKit.Analysis;

/// <summary>
///     Immutable, fully-indexed snapshot of a parsed demo.
///     Built by <see cref="DemoAnalyzer" /> — never constructed directly by external code.
///     <para>
///         All data is zero-copy from <see cref="Demo" />; the type-keyed index and round list
///         are pre-built at construction time so every query is O(1) or O(log n).
///     </para>
///     <para>
///         Implements <see cref="IDemoContext" /> which is the surface passed to rule plugins.
///         The additional <see cref="EntityState" /> property is available for advanced use in
///         the Analysis layer; it is intentionally absent from <see cref="IDemoContext" /> to
///         avoid exposing mutable state to plugins.
///     </para>
/// </summary>
public sealed class DemoContext : IDemoContext
{
    // Per-type typed cache: Type → IReadOnlyList<T> stored as object for type erasure.
    private readonly ConcurrentDictionary<Type, object> _typedCache = new();
    private readonly Dictionary<Type, IReadOnlyList<GameEvent>> _typeIndex;

    internal DemoContext(
        ParsedDemo demo,
        List<RoundInfo> rounds,
        EntityTracker entityState,
        Dictionary<Type, IReadOnlyList<GameEvent>> typeIndex)
    {
        Demo = demo;
        Rounds = rounds;
        EntityState = entityState;
        _typeIndex = typeIndex;
    }

    /// <summary>
    ///     Entity state after a full replay of <see cref="Demo" />.
    ///     Use <see cref="CreateEntityLayer" /> for per-tick forward-only seeks.
    ///     <para>
    ///         When built via <see cref="DemoAnalyzer.BuildEventContext" /> (fast path with no
    ///         entity replay) this is an empty, freshly constructed tracker.
    ///     </para>
    /// </summary>
    public EntityTracker EntityState { get; }

    /// <inheritdoc />
    public EntityStateLayer CreateEntityLayer() => new(Demo.Frames);

    /// <inheritdoc />
    public ParsedDemo Demo { get; }

    /// <inheritdoc />
    public IReadOnlyList<GameEvent> EventsInRange(int fromTick, int toTick)
    {
        IReadOnlyList<GameEvent> all = Demo.AllGameEvents;
        if (all.Count == 0 || fromTick > toTick)
        {
            return [];
        }

        int lo = LowerBound(all, fromTick);
        int hi = UpperBound(all, toTick);
        if (lo >= hi)
        {
            return [];
        }

        return all is List<GameEvent> concreteList
            ? concreteList.GetRange(lo, hi - lo)
            : all.Skip(lo).Take(hi - lo).ToList();
    }

    /// <inheritdoc cref="IDemoContext.EventsOfType{T}" />
    public IReadOnlyList<GameEvent> EventsOfType<T>() where T : class
    {
        // Keyed on the PAYLOAD type. Every fire is a GameEvent now, so indexing on the envelope's
        // runtime type would put the whole demo under one key.
        //
        // Returns the envelopes rather than the payloads, because callers routinely need both
        // halves — the payload for the event's own fields, and the envelope for the tick the fire
        // happened on. Reach the payload with `e.Payload is T`.
        object cached = _typedCache.GetOrAdd(typeof(T), _ =>
            _typeIndex.TryGetValue(typeof(T), out IReadOnlyList<GameEvent>? raw)
                ? raw
                : (IReadOnlyList<GameEvent>)Array.Empty<GameEvent>());

        return (IReadOnlyList<GameEvent>)cached;
    }

    /// <inheritdoc />
    public IReadOnlyList<RoundInfo> Rounds { get; }

    // ── Binary search helpers ─────────────────────────────────────────────────

    /// <summary>First index where event.ServerTick &gt;= <paramref name="tick" />.</summary>
    private static int LowerBound(IReadOnlyList<GameEvent> list, int tick)
    {
        int lo = 0, hi = list.Count;
        while (lo < hi)
        {
            int mid = lo + (hi - lo >> 1);
            if (list[mid].ServerTick < tick)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        return lo;
    }

    /// <summary>First index where event.ServerTick &gt; <paramref name="tick" /> (exclusive upper bound).</summary>
    private static int UpperBound(IReadOnlyList<GameEvent> list, int tick)
    {
        int lo = 0, hi = list.Count;
        while (lo < hi)
        {
            int mid = lo + (hi - lo >> 1);
            if (list[mid].ServerTick <= tick)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        return lo;
    }
}
