#region

using System.Numerics;

#endregion

namespace CS2DemoKit.Parser.EntityTracking;

/// <summary>
///     Reconstructs a pawn's world position from CS2's cell-coordinate encoding. There is NO
///     <c>m_vecOrigin</c> leaf on a real pawn; position lives on <c>CBodyComponent</c> as cell
///     indices (<c>m_cell{X,Y,Z}</c>, uint16) plus an in-cell offset (<c>m_vec{X,Y,Z}</c>,
///     quantized float).
///     <para>
///         <b>The constant is LIFTED FROM THE demofile-net ORACLE, not guessed.</b> Decompiling
///         <c>DemoFile.Game.Cs.CNetworkOriginCellCoordQuantizedVector</c> (the project's ground-truth
///         oracle) gives the exact reconstruction:
///         <code>
///         private const int CELL_WIDTH = 512;
///         Vector =&gt; ( (CellX - 32) * 512 + X, (CellY - 32) * 512 + Y, (CellZ - 32) * 512 + Z )
///         </code>
///         So <c>world_axis = (cell - 32) * 512 + offset</c>. The cell multiplier is <b>512</b>, not
///         1024 — a 1024 multiplier is the classic mis-derivation, and it is wrong. The effective
///         <c>WORLD_HALF_EXTENT</c> is <c>32 * 512 = 16384 = 1&lt;&lt;14</c>, which independently
///         matches the non-cell <c>CNetworkOriginQuantizedVector</c> range <c>[-16384, 16384]</c> in
///         the CS2 schema. The <c>[0,1024]</c> in-cell offset range coexisting with a 512 cell width
///         is the engine's real encoding (cells overlap), not a contradiction.
///     </para>
///     <para>
///         This is the single verified home for the constant-sensitive math: reconstruct positions
///         here rather than re-rolling the formula at each call site.
///     </para>
/// </summary>
public static class PositionUtil
{
    /// <summary>World units per cell along each axis (oracle-pinned).</summary>
    public const int CellWidth = 512;

    /// <summary>
    ///     Cell-grid centring offset: <c>32 * CellWidth = 16384 = 1&lt;&lt;14</c>. Equals the named
    ///     <c>WORLD_HALF_EXTENT</c> the design flags as the load-bearing constant — derived here from
    ///     the oracle's literal <c>(cell - 32) * 512</c> form, not from a guessed half-extent.
    /// </summary>
    public const int WorldHalfExtent = 32 * CellWidth; // 16384 = 1<<14

    /// <summary>
    ///     Reconstructs world position from a pawn's <c>CBodyComponent</c> cell + offset fields.
    ///     Returns null when any of the six fields is absent/unseen (a pre-spawn / dormant pawn).
    ///     <para>
    ///         Read path since the SDK cutover: the six leaves are Lens-curated onto typed lanes
    ///         (cells int lane, offsets float
    ///         lane), so a Lens-bound tracker serves this per-entity-per-tick hot path with
    ///         seen-aware typed slot reads and ZERO boxing. The boxed coercion path below
    ///         survives as the fallback for unlensed trackers and other entity classes, where
    ///         the wire type of a cell index varies by encoder (ushort/int/uint cells,
    ///         float/double offsets).
    ///     </para>
    ///     <para>
    ///         Also usable as a method group wherever a <c>Func&lt;EntityState, Vector3?&gt;</c>
    ///         position resolver is wanted, notably <c>VisibilityAnalyzer.Analyze</c>, which takes
    ///         one so it does not have to own the cell constant.
    ///     </para>
    /// </summary>
    public static Vector3? CellToWorld(EntityState pawn)
    {
        if (!TryCellRead(pawn, "CBodyComponent.m_cellX", out int cx) ||
            !TryCellRead(pawn, "CBodyComponent.m_cellY", out int cy) ||
            !TryCellRead(pawn, "CBodyComponent.m_cellZ", out int cz) ||
            !TryOffsetRead(pawn, "CBodyComponent.m_vecX", out float ox) ||
            !TryOffsetRead(pawn, "CBodyComponent.m_vecY", out float oy) ||
            !TryOffsetRead(pawn, "CBodyComponent.m_vecZ", out float oz))
        {
            return null;
        }

        return new Vector3(Axis(cx, ox), Axis(cy, oy), Axis(cz, oz));
    }

    /// <summary>Typed int-lane read when the leaf is Lens-mapped; boxed coercion otherwise.</summary>
    private static bool TryCellRead(EntityState entity, string path, out int cell)
    {
        if (entity.Shape is { } shape
            && shape.PathToSlot.TryGetValue(path, out SlotAddr addr)
            && addr.Lane == LaneKind.Int)
        {
            return entity.TryGetIntSlot(addr.Slot, out cell);
        }

        return TryCell(entity[path], out cell);
    }

    /// <summary>Typed float-lane read when the leaf is Lens-mapped; boxed coercion otherwise.</summary>
    private static bool TryOffsetRead(EntityState entity, string path, out float offset)
    {
        if (entity.Shape is { } shape
            && shape.PathToSlot.TryGetValue(path, out SlotAddr addr)
            && addr.Lane == LaneKind.Float)
        {
            return entity.TryGetFloatSlot(addr.Slot, out offset);
        }

        return TryOffset(entity[path], out offset);
    }

    /// <summary>The single-axis reconstruction: <c>(cell - 32) * 512 + offset</c> (oracle formula).</summary>
    public static float Axis(int cell, float offset) => (cell - 32) * (float)CellWidth + offset;

    private static bool TryCell(object? value, out int cell)
    {
        switch (value)
        {
            case ushort u:
                cell = u;
                return true;
            case short s:
                cell = s;
                return true;
            case int i:
                cell = i;
                return true;
            case uint u:
                cell = (int)u;
                return true;
            case long l:
                cell = (int)l;
                return true;
            case ulong u:
                cell = (int)u;
                return true;
            case byte b:
                cell = b;
                return true;
            default:
                cell = 0;
                return false;
        }
    }

    private static bool TryOffset(object? value, out float offset)
    {
        switch (value)
        {
            case float f:
                offset = f;
                return true;
            case double d:
                offset = (float)d;
                return true;
            case int i:
                offset = i;
                return true;
            case long l:
                offset = l;
                return true;
            default:
                offset = 0;
                return false;
        }
    }
}
