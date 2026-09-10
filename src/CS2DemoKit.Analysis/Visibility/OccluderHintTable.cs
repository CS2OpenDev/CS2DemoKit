#region

#endregion

namespace CS2DemoKit.Analysis.Visibility;

/// <summary>
///     Last-occluder hints for every directed player pair and body anchor: the triangle that blocked
///     (viewer, target, anchor)'s sightline the last time it was cast, or -1. Handed to the pair loop
///     one row at a time so the ray for anchor <c>i</c> tries that triangle before it enters the BVH.
///     <para>
///         A hint changes how much work a verdict takes and never the verdict: the hinted test
///         is identical to the hint-free traversal on every ray, whatever the slot holds (see
///         <see cref="TriangleBvh.AnyHit(System.Numerics.Vector3, System.Numerics.Vector3, float, float, ref int)" />).
///         So the table is never cleared: a hint left over from a previous round is tested once
///         and replaced. It is mutable per evaluation and not shared between threads; a parallel
///         evaluation gives each worker its own.
///     </para>
///     <para>
///         It is 64 x 64 x 6 ints, 98,304 bytes, which is above the large-object heap threshold.
///         One per scanner and one per <c>VisibilityAnalyzer.Analyze</c> call is what it is
///         sized for; do not construct one per round, per player or per pair.
///     </para>
/// </summary>
internal sealed class OccluderHintTable
{
    /// <summary>Player slots with a row: 0 to <c>Slots - 1</c>, the same range the transition scanner holds state for.</summary>
    public const int Slots = VisibilityTransitionScanner.MaxSlots;

    private readonly int[] _hints;

    /// <summary>A table with every hint unset.</summary>
    public OccluderHintTable()
    {
        _hints = new int[Slots * Slots * PlayerVantage.MaxAnchors];
        Array.Fill(_hints, -1);
    }

    /// <summary>
    ///     The <see cref="PlayerVantage.MaxAnchors" /> hint slots for one directed pair, or an empty
    ///     span when either slot is outside the table, in which case the pair is cast without hints.
    /// </summary>
    /// <param name="viewerSlot">The viewer's player slot.</param>
    /// <param name="targetSlot">The target's player slot.</param>
    public Span<int> For(int viewerSlot, int targetSlot)
    {
        if ((uint)viewerSlot >= Slots || (uint)targetSlot >= Slots)
        {
            return Span<int>.Empty;
        }

        return _hints.AsSpan(((viewerSlot * Slots) + targetSlot) * PlayerVantage.MaxAnchors, PlayerVantage.MaxAnchors);
    }
}
