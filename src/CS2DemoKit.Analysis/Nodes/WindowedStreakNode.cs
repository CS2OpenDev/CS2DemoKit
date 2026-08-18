#region

using CS2DemoKit.Analysis.Abstractions;

#endregion

namespace CS2DemoKit.Analysis.Nodes;

/// <summary>
///     Counts event streaks within a tick window. When events occur within
///     <see cref="_windowTicks" /> of each other, the streak extends. When the gap
///     exceeds the window (or round ends), the streak is finalized: if it reached
///     <see cref="_minStreak" />, the counter increments.
/// </summary>
public sealed class WindowedStreakNode : ValueNode<int>, IRoundScopedNode
{
    private readonly int _minStreak;
    private readonly int _windowTicks;

    /// <param name="name">Unique display name for diagnostics and the rule chain timeline.</param>
    /// <param name="subtitle">Optional secondary label (e.g. player name) displayed below the name.</param>
    /// <param name="windowTicks">Maximum tick gap between consecutive events for the streak to extend.</param>
    /// <param name="minStreak">Minimum streak length that counts as a completed streak when finalized.</param>
    public WindowedStreakNode(string name, string? subtitle, int windowTicks = 640, int minStreak = 2)
    {
        Name = name;
        Subtitle = subtitle;
        _windowTicks = windowTicks;
        _minStreak = minStreak;
        SetValue(0);
    }

    /// <summary>Length of the streak currently being accumulated (zero between streaks).</summary>
    public int CurrentStreakLength { get; private set; }

    /// <summary>Tick of the most recent event seen in the current streak, or -1 between streaks.</summary>
    public int LastEventTick { get; private set; } = -1;

    /// <inheritdoc />
    public override string Name { get; }

    /// <inheritdoc />
    public override string? Subtitle { get; }

    /// <inheritdoc />
    public void Reset()
    {
        if (CurrentStreakLength >= _minStreak)
        {
            SetValue(Value + 1);
        }

        LastEventTick = -1;
        CurrentStreakLength = 0;
    }

    /// <summary>
    ///     Called on each matching event. If the gap to <see cref="LastEventTick" /> is within
    ///     the window, extends the current streak; otherwise finalizes the previous streak (incrementing
    ///     the counter if it met <c>minStreak</c>) and starts a new one of length 1.
    /// </summary>
    public void ExtendOrFinalize(int tick)
    {
        if (LastEventTick >= 0 && tick - LastEventTick <= _windowTicks)
        {
            CurrentStreakLength++;
        }
        else
        {
            if (CurrentStreakLength >= _minStreak)
            {
                SetValue(Value + 1);
            }

            CurrentStreakLength = 1;
        }

        LastEventTick = tick;
    }
}
