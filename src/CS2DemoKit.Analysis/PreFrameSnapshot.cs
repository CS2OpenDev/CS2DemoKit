#region

using System.Numerics;
using CS2DemoKit.Analysis.Plugins;

#endregion

namespace CS2DemoKit.Analysis;

/// <summary>
///     The scanner's running last-value-per-(column, slot) map, unboxed. Digest rows fold into it
///     cell by cell; a consumer reads one cell back boxed through <see cref="GetBoxed" />, which is
///     the only place a value is boxed on the whole per-pawn path, and it runs per event rather
///     than per frame.
///     <para>
///         Semantics are the boxed dictionary's: a cell absent from a row leaves the held value
///         alone, a slot never written reads null, and nothing is ever cleared.
///     </para>
/// </summary>
internal sealed class PreFrameSnapshot
{
    private TypedSlotRow?[] _bySlot = new TypedSlotRow?[64];

    /// <param name="layout">The scanner's column plan; only compatible digests may be folded.</param>
    public PreFrameSnapshot(DigestColumnLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        Layout = layout;
    }

    /// <summary>The column plan the snapshot is indexed by.</summary>
    public DigestColumnLayout Layout { get; }

    /// <summary>
    ///     Writes every present cell of every row into the map. The caller has already checked
    ///     that <paramref name="rows" />' layout is compatible with <see cref="Layout" />.
    /// </summary>
    public void Fold(PerPawnColumns rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        DigestColumnLayout layout = Layout;
        int words = layout.Words;
        for (int r = 0; r < rows.Count; r++)
        {
            TypedSlotRow? target = null;
            for (int w = 0; w < words; w++)
            {
                ulong bits = rows.PresentWord(r, w);
                if (bits == 0)
                {
                    continue;
                }

                target ??= RowFor(rows.SlotAt(r));
                target.Recorded[w] |= bits;
                while (bits != 0)
                {
                    int column = (w << 6) + BitOperations.TrailingZeroCount(bits);
                    bits &= bits - 1;
                    int index = layout.KindIndex[column];
                    switch (layout.Kinds[column])
                    {
                        case PawnCellKind.Int:
                        case PawnCellKind.Bool:
                            target.Ints[index] = rows.GetInt(r, column);
                            break;
                        case PawnCellKind.Float:
                            target.Floats[index] = rows.GetFloat(r, column);
                            break;
                        default:
                            target.Strings[index] = rows.GetString(r, column);
                            break;
                    }
                }
            }
        }
    }

    /// <summary>The held value for (<paramref name="column" />, <paramref name="slot" />) boxed in its declared type, or null when never written.</summary>
    public object? GetBoxed(int column, int slot)
    {
        if ((uint)slot >= (uint)_bySlot.Length || _bySlot[slot] is not { } row)
        {
            return null;
        }

        if ((row.Recorded[column >> 6] & (1UL << (column & 63))) == 0)
        {
            return null;
        }

        int index = Layout.KindIndex[column];
        return Layout.Kinds[column] switch
        {
            PawnCellKind.Int => row.Ints[index],
            PawnCellKind.Bool => row.Ints[index] != 0 ? PawnCellBoxes.True : PawnCellBoxes.False,
            PawnCellKind.Float => row.Floats[index],
            _ => row.Strings[index]
        };
    }

    private TypedSlotRow RowFor(int slot)
    {
        if ((uint)slot >= (uint)_bySlot.Length)
        {
            Array.Resize(ref _bySlot, Math.Max(slot + 1, _bySlot.Length * 2));
        }

        return _bySlot[slot] ??= new TypedSlotRow(Layout);
    }
}
