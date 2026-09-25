#region

using CS2DemoKit.Analysis.Plugins;

#endregion

namespace CS2DemoKit.Analysis;

/// <summary>
///     The per-pawn rows of one frame's digest, stored unboxed: a slot per row, a present mask per
///     row, and the cells laid out row-major inside one array per storage kind. A cell is read only
///     where its present bit is set; the storage behind an absent cell is whatever the emitting
///     stream last held there and means nothing.
///     <para>
///         Sized for the frame it describes. <see cref="Empty" /> is the shared, immutable
///         zero-row instance every digest starts with, so a frame where nothing changed allocates
///         no row storage at all; the first row allocates storage for
///         <see cref="InitialCapacity" /> rows or the producing stream's previous row count,
///         whichever is larger, and each kind's array is exactly as wide as that kind's column
///         count (a kind with no columns shares the empty array).
///     </para>
/// </summary>
internal sealed class PerPawnColumns
{
    /// <summary>Row capacity of a freshly created instance with no better hint.</summary>
    public const int InitialCapacity = 4;

    private int _capacity;
    private float[] _floats;
    private int[] _ints;
    private ulong[] _present;
    private int[] _slots;
    private string?[] _strings;

    /// <param name="layout">The column plan of every row.</param>
    /// <param name="capacity">Rows to reserve up front.</param>
    internal PerPawnColumns(DigestColumnLayout layout, int capacity)
    {
        ArgumentNullException.ThrowIfNull(layout);
        Layout = layout;
        _capacity = capacity;
        _slots = capacity > 0 ? new int[capacity] : [];
        _present = capacity > 0 && layout.Words > 0 ? new ulong[capacity * layout.Words] : [];
        _ints = capacity > 0 && layout.IntCount > 0 ? new int[capacity * layout.IntCount] : [];
        _floats = capacity > 0 && layout.FloatCount > 0 ? new float[capacity * layout.FloatCount] : [];
        _strings = capacity > 0 && layout.StringCount > 0 ? new string?[capacity * layout.StringCount] : [];
    }

    /// <summary>The shared zero-row instance. Appending to it throws.</summary>
    public static PerPawnColumns Empty { get; } = new(DigestColumnLayout.Empty, 0);

    /// <summary>The column plan of every row.</summary>
    public DigestColumnLayout Layout { get; }

    /// <summary>Number of rows.</summary>
    public int Count { get; private set; }

    /// <summary>The player slot of <paramref name="row" />.</summary>
    public int SlotAt(int row)
    {
        CheckRow(row);
        return _slots[row];
    }

    /// <summary>Whether <paramref name="column" /> carries a value in <paramref name="row" />.</summary>
    public bool IsPresent(int row, int column)
    {
        CheckRow(row);
        return (_present[row * Layout.Words + (column >> 6)] & (1UL << (column & 63))) != 0;
    }

    /// <summary>One 64-column word of a row's present mask.</summary>
    internal ulong PresentWord(int row, int word) => _present[row * Layout.Words + word];

    /// <summary>The int or bool cell (bools as 0/1). Meaningful only where <see cref="IsPresent" />.</summary>
    public int GetInt(int row, int column) => _ints[row * Layout.IntCount + Layout.KindIndex[column]];

    /// <summary>The float cell. Meaningful only where <see cref="IsPresent" />.</summary>
    public float GetFloat(int row, int column) => _floats[row * Layout.FloatCount + Layout.KindIndex[column]];

    /// <summary>The string cell. Meaningful only where <see cref="IsPresent" />.</summary>
    public string? GetString(int row, int column) => _strings[row * Layout.StringCount + Layout.KindIndex[column]];

    /// <summary>
    ///     The cell boxed in its declared CLR type, or null where absent. This is the shape the
    ///     boxed digest carried and the shape tests compare in; the hot path never calls it.
    /// </summary>
    public object? GetBoxed(int row, int column)
    {
        if (!IsPresent(row, column))
        {
            return null;
        }

        return Layout.Kinds[column] switch
        {
            PawnCellKind.Int => GetInt(row, column),
            PawnCellKind.Bool => GetInt(row, column) != 0 ? PawnCellBoxes.True : PawnCellBoxes.False,
            PawnCellKind.Float => GetFloat(row, column),
            _ => GetString(row, column)
        };
    }

    /// <summary>
    ///     Appends a row copied from <paramref name="source" />, marking the columns in
    ///     <paramref name="present" /> as carrying a value. The whole row is copied, absent cells
    ///     included; only the mask says which cells mean anything.
    /// </summary>
    internal void AppendRow(int slot, TypedSlotRow source, ReadOnlySpan<ulong> present)
    {
        if (ReferenceEquals(this, Empty))
        {
            throw new InvalidOperationException("PerPawnColumns.Empty is shared and immutable; create an instance to append to");
        }

        if (Count == _capacity)
        {
            Grow();
        }

        int row = Count++;
        _slots[row] = slot;
        DigestColumnLayout layout = Layout;
        if (layout.Words > 0)
        {
            present.Slice(0, layout.Words).CopyTo(_present.AsSpan(row * layout.Words, layout.Words));
        }

        if (layout.IntCount > 0)
        {
            Array.Copy(source.Ints, 0, _ints, row * layout.IntCount, layout.IntCount);
        }

        if (layout.FloatCount > 0)
        {
            Array.Copy(source.Floats, 0, _floats, row * layout.FloatCount, layout.FloatCount);
        }

        if (layout.StringCount > 0)
        {
            Array.Copy(source.Strings, 0, _strings, row * layout.StringCount, layout.StringCount);
        }
    }

    /// <summary>
    ///     Builds rows from the boxed shape (<c>(slot, one object per column, null = absent)</c>),
    ///     for tests that hand-build digests. A cell whose CLR type is not the column's declared
    ///     type throws, so a fixture cannot quietly feed an int to a float column.
    /// </summary>
    public static PerPawnColumns FromBoxedRows(DigestColumnLayout layout, IReadOnlyList<(int Slot, object?[] Values)> rows)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(rows);
        if (rows.Count == 0)
        {
            return Empty;
        }

        PerPawnColumns columns = new(layout, rows.Count);
        TypedSlotRow scratch = new(layout);
        ulong[] present = new ulong[Math.Max(1, layout.Words)];
        foreach ((int slot, object?[] values) in rows)
        {
            if (values.Length != layout.Count)
            {
                throw new ArgumentException(
                    $"row for slot {slot} has {values.Length} cells but the layout has {layout.Count} columns", nameof(rows));
            }

            Array.Clear(present);
            for (int p = 0; p < values.Length; p++)
            {
                object? cell = values[p];
                if (cell is null)
                {
                    continue;
                }

                int index = layout.KindIndex[p];
                switch (layout.Kinds[p])
                {
                    case PawnCellKind.Int:
                        scratch.Ints[index] = cell is int i ? i : throw Mismatch(layout, p, cell);
                        break;
                    case PawnCellKind.Bool:
                        scratch.Ints[index] = cell is bool b ? (b ? 1 : 0) : throw Mismatch(layout, p, cell);
                        break;
                    case PawnCellKind.Float:
                        scratch.Floats[index] = cell is float f ? f : throw Mismatch(layout, p, cell);
                        break;
                    default:
                        scratch.Strings[index] = cell is string s ? s : throw Mismatch(layout, p, cell);
                        break;
                }

                present[p >> 6] |= 1UL << (p & 63);
            }

            columns.AppendRow(slot, scratch, present);
        }

        return columns;
    }

    private static ArgumentException Mismatch(DigestColumnLayout layout, int column, object cell) =>
        new($"column {column} ('{layout.Names[column]}') is a {layout.Kinds[column]} column but the boxed cell is a {cell.GetType().Name}");

    private void CheckRow(int row)
    {
        if ((uint)row >= (uint)Count)
        {
            throw new ArgumentOutOfRangeException(nameof(row), row, $"row must be in 0..{Count - 1}");
        }
    }

    private void Grow()
    {
        int next = Math.Max(InitialCapacity, _capacity * 2);
        DigestColumnLayout layout = Layout;
        Array.Resize(ref _slots, next);
        if (layout.Words > 0)
        {
            Array.Resize(ref _present, next * layout.Words);
        }

        if (layout.IntCount > 0)
        {
            Array.Resize(ref _ints, next * layout.IntCount);
        }

        if (layout.FloatCount > 0)
        {
            Array.Resize(ref _floats, next * layout.FloatCount);
        }

        if (layout.StringCount > 0)
        {
            Array.Resize(ref _strings, next * layout.StringCount);
        }

        _capacity = next;
    }
}

/// <summary>
///     Shared boxes for bools and small ints, so boxing a cell read every frame does not allocate.
///     The int range and the reasoning that sharing is safe are the parser's
///     <c>CS2DemoKit.Parser.EntityTracking.Boxes</c>, which this mirrors because that class is
///     internal to the parser.
/// </summary>
internal static class PawnCellBoxes
{
    private const int IntMin = -128;
    private const int IntCount = 1152; // [-128, 1023]
    private static readonly object[] s_ints = CreateInts();

    /// <summary>Boxed <c>true</c>.</summary>
    public static readonly object True = true;

    /// <summary>Boxed <c>false</c>.</summary>
    public static readonly object False = false;

    /// <summary>A boxed <paramref name="value" />, shared when it is in range and freshly allocated otherwise.</summary>
    public static object Int(int value)
    {
        uint index = (uint)(value - IntMin);
        return index < IntCount ? s_ints[index] : value;
    }

    private static object[] CreateInts()
    {
        object[] table = new object[IntCount];
        for (int i = 0; i < IntCount; i++)
        {
            table[i] = i + IntMin;
        }

        return table;
    }
}
