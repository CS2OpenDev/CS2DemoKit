#region

using CS2DemoKit.Analysis.Abstractions;

#endregion

namespace CS2DemoKit.Analysis.Nodes;

/// <summary>
///     A boolean <b>pulse</b> node for windowed multi-kill highlights (the <c>burst:</c> kind). It
///     goes active for exactly one dispatch on the event that completes a burst — <c>minCount</c>
///     matching events whose first-to-last span is within <c>windowTicks</c> — then, as an
///     <see cref="ITransientNode" />, auto-clears (<see cref="BoolNode.Deactivate" />) before the next
///     dispatch of its event type, so the highlight's rising-edge machinery fires exactly once at the
///     completing event's tick (the correct clip anchor).
///     <para>
///         Unlike <see cref="WindowedStreakNode" /> (a match counter finalized at round end / on a
///         window break), this is a TRUE sliding window: the test is <c>tick − oldest-of-last-minCount
///         ≤ windowTicks</c>, so N events spread just beyond the window do not qualify. A <c>_fired</c>
///         latch holds across a continuous burst so one sustained spray fires ONCE (kills that keep
///         landing inside the rolling window are the same burst); the latch re-arms only when a
///         qualifying window no longer forms (the sequence broke), letting a genuinely new burst fire.
///     </para>
///     <para>
///         <see cref="IRoundScopedNode" /> clears the ring + latch at round boundaries and never emits.
///         Snapshot-excluded via <see cref="ITransientNode" /> : <see cref="ISnapshotExcludedNode" />.
///         The two <c>Reset()</c> methods are DISTINCT (explicit interface impls): the evaluator calls
///         the transient one per dispatch (StateGraphEvaluator, transient-reset loop) and the
///         round-scoped one per round (round-scoped reset loop).
///     </para>
/// </summary>
public sealed class WindowedHighlightPulseNode : BoolNode, ITransientNode, IRoundScopedNode
{
    private readonly int _minCount;
    private readonly int[] _ring; // the last _minCount matching ticks, oldest at [0]
    private readonly int _windowTicks;
    private int _count;           // live entries in _ring (saturates at _minCount)
    private bool _fired;          // already pulsed for the current (still-open) burst

    /// <param name="name">Unique display name for diagnostics and the rule chain timeline.</param>
    /// <param name="subtitle">Optional secondary label (e.g. player name) displayed below the name.</param>
    /// <param name="windowTicks">Maximum first-to-last tick span for the last <paramref name="minCount" /> events to count as a burst.</param>
    /// <param name="minCount">The number of matching events (within the window) that completes a burst. Clamped to &gt;= 2.</param>
    public WindowedHighlightPulseNode(string name, string? subtitle, int windowTicks = 640, int minCount = 3)
    {
        Name = name;
        Subtitle = subtitle;
        _windowTicks = windowTicks;
        _minCount = Math.Max(2, minCount);
        _ring = new int[_minCount];
        SetValue(false);
    }

    /// <inheritdoc />
    public override string Name { get; }

    /// <inheritdoc />
    public override string? Subtitle { get; }

    /// <summary>Round boundary: full clear of the ring + latch. Never emits (round end is not a highlight).</summary>
    void IRoundScopedNode.Reset()
    {
        _count = 0;
        _fired = false;
        Deactivate();
    }

    /// <summary>Per-dispatch: clear the one-shot pulse so the next completing event is a fresh rising edge.</summary>
    void ITransientNode.Reset() => Deactivate();

    /// <summary>
    ///     Called on each matching event. Appends the tick to the ring (keeping the last
    ///     <see cref="_minCount" />), then pulses active iff those events span within the window and the
    ///     current burst has not already fired.
    /// </summary>
    public void Observe(int tick)
    {
        if (_count < _minCount)
        {
            _ring[_count++] = tick;
        }
        else
        {
            for (int i = 1; i < _minCount; i++)
            {
                _ring[i - 1] = _ring[i];
            }

            _ring[_minCount - 1] = tick;
        }

        bool inWindow = _count >= _minCount && tick - _ring[0] <= _windowTicks;

        if (!inWindow)
        {
            // No qualifying window right now — the current burst has broken; re-arm for the next.
            _fired = false;
            return;
        }

        if (!_fired)
        {
            Activate(); // one-shot pulse at the completing event's tick
            _fired = true;
        }
    }
}
