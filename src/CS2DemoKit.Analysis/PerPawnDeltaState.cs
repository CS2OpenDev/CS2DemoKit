#region

using CS2DemoKit.Analysis.Plugins;

#endregion

namespace CS2DemoKit.Analysis;

/// <summary>
///     One player slot's cells in a <see cref="DigestColumnLayout" />, unboxed: int and bool
///     columns in <see cref="Ints" />, floats in <see cref="Floats" />, strings in
///     <see cref="Strings" />, each indexed by the layout's <see cref="DigestColumnLayout.KindIndex" />.
///     The two masks carry what the cell values alone cannot: <see cref="Recorded" /> says a
///     column has been written at all, and <see cref="WasNull" /> says the last write was "no
///     value", which is the distinction the delta encoding needs to count a first null as a change
///     and a null after a value as another one.
/// </summary>
internal sealed class TypedSlotRow
{
    /// <summary>Int and bool cells, bools as 0 or 1.</summary>
    public readonly int[] Ints;

    /// <summary>Float cells.</summary>
    public readonly float[] Floats;

    /// <summary>String cells.</summary>
    public readonly string?[] Strings;

    /// <summary>Bit per column: the column has been written at least once.</summary>
    public readonly ulong[] Recorded;

    /// <summary>Bit per column: the last write was "no value". Meaningful only where <see cref="Recorded" /> is set.</summary>
    public readonly ulong[] WasNull;

    /// <summary>Allocates a row sized exactly to the layout; kinds with no columns get the shared empty array.</summary>
    public TypedSlotRow(DigestColumnLayout layout)
    {
        Ints = layout.IntCount > 0 ? new int[layout.IntCount] : [];
        Floats = layout.FloatCount > 0 ? new float[layout.FloatCount] : [];
        Strings = layout.StringCount > 0 ? new string?[layout.StringCount] : [];
        Recorded = layout.Words > 0 ? new ulong[layout.Words] : [];
        WasNull = layout.Words > 0 ? new ulong[layout.Words] : [];
    }
}

/// <summary>
///     One decode stream's memory of the last per-pawn value emitted for each (slot, column), so
///     <see cref="EntityDigestExtractor.Build" /> can emit only the cells that changed, compared
///     unboxed. It also owns the stream's <see cref="PawnReadContext" /> and the scratch the
///     extractor needs per pawn, so a frame allocates nothing but the rows it emits.
///     <para>
///         One instance per stream and never shared: one per parallel chunk worker, one per
///         sequential scanner. A worker starting at a checkpoint has no history, so its first frame
///         re-emits every live cell. That is redundant, not wrong: the consumer folds the values
///         into its pre-frame snapshot, and re-writing a cell with the value it already holds is a
///         no-op.
///     </para>
///     <para>
///         With <see cref="Dedup" /> off every record counts as a change, which turns the stream
///         into the full per-frame readout the equivalence tests use as ground truth.
///     </para>
/// </summary>
internal sealed class PerPawnDeltaState
{
    private TypedSlotRow?[] _bySlot = new TypedSlotRow?[64];

    /// <summary>The controller team last read per slot, -1 unseen; the digest carries the set only when one changed.</summary>
    internal int[] ControllerTeams { get; } = Unseen();

    private static int[] Unseen()
    {
        int[] teams = new int[64];
        Array.Fill(teams, -1);
        return teams;
    }

    /// <param name="layout">The column plan every row of this stream follows.</param>
    /// <param name="dedup">When false, every recorded cell reads as changed (the full readout).</param>
    public PerPawnDeltaState(DigestColumnLayout layout, bool dedup = true)
    {
        ArgumentNullException.ThrowIfNull(layout);
        Layout = layout;
        Dedup = dedup;
        PresentScratch = new ulong[Math.Max(1, layout.Words)];
    }

    /// <summary>The column plan every row of this stream follows.</summary>
    public DigestColumnLayout Layout { get; }

    /// <summary>Whether unchanged cells are suppressed.</summary>
    public bool Dedup { get; }

    /// <summary>The per-pawn read context the extractor binds before dispatching to the layout's readers.</summary>
    public PawnReadContext Context { get; } = new();

    /// <summary>Per-row present mask the extractor fills while reading one pawn; one allocation per stream.</summary>
    internal ulong[] PresentScratch { get; }

    /// <summary>
    ///     How many rows the previous frame emitted, which sizes the next frame's row storage: a
    ///     stream that emits a row per live pawn every frame allocates exactly once per frame, and
    ///     one that emits a row every few hundred frames never over-reserves by more than a frame.
    /// </summary>
    internal int LastRowCount { get; set; }

    /// <summary>The slot's row, created on first use; slots beyond 63 grow the table.</summary>
    public TypedSlotRow RowFor(int slot)
    {
        if ((uint)slot >= (uint)_bySlot.Length)
        {
            Array.Resize(ref _bySlot, Math.Max(slot + 1, _bySlot.Length * 2));
        }

        return _bySlot[slot] ??= new TypedSlotRow(Layout);
    }

    /// <summary>
    ///     Records an int or bool cell and returns whether it differs from the last value recorded
    ///     for that (slot, column). A first record is a change; a value after "no value" is a change;
    ///     "no value" after a value is a change; "no value" after "no value" is not.
    /// </summary>
    public bool RecordInt(TypedSlotRow row, int column, bool hasValue, int value)
    {
        int word = column >> 6;
        ulong bit = 1UL << (column & 63);
        int index = Layout.KindIndex[column];
        bool changed = Differs(row, word, bit, hasValue) || (hasValue && row.Ints[index] != value);
        if (changed)
        {
            Commit(row, word, bit, hasValue);
            if (hasValue)
            {
                row.Ints[index] = value;
            }
        }

        return changed;
    }

    /// <summary>
    ///     Records a float cell; see <see cref="RecordInt" />. Equality is <see cref="float.Equals(float)" />,
    ///     the same relation the boxed digest applied, so NaN equals NaN and negative zero equals zero.
    /// </summary>
    public bool RecordFloat(TypedSlotRow row, int column, bool hasValue, float value)
    {
        int word = column >> 6;
        ulong bit = 1UL << (column & 63);
        int index = Layout.KindIndex[column];
        bool changed = Differs(row, word, bit, hasValue) || (hasValue && !row.Floats[index].Equals(value));
        if (changed)
        {
            Commit(row, word, bit, hasValue);
            if (hasValue)
            {
                row.Floats[index] = value;
            }
        }

        return changed;
    }

    /// <summary>Records a string cell, null meaning "no value"; see <see cref="RecordInt" />. Equality is ordinal.</summary>
    public bool RecordString(TypedSlotRow row, int column, string? value)
    {
        int word = column >> 6;
        ulong bit = 1UL << (column & 63);
        int index = Layout.KindIndex[column];
        bool hasValue = value is not null;
        bool changed = Differs(row, word, bit, hasValue)
                       || (hasValue && !string.Equals(row.Strings[index], value, StringComparison.Ordinal));
        if (changed)
        {
            Commit(row, word, bit, hasValue);
            if (hasValue)
            {
                row.Strings[index] = value;
            }
        }

        return changed;
    }

    // The mask half of the comparison: every case that decides "changed" without looking at the
    // stored value. When it returns false and hasValue is set, the caller compares the value.
    private bool Differs(TypedSlotRow row, int word, ulong bit, bool hasValue)
    {
        if (!Dedup || (row.Recorded[word] & bit) == 0)
        {
            return true;
        }

        bool wasNull = (row.WasNull[word] & bit) != 0;
        return hasValue ? wasNull : !wasNull;
    }

    private static void Commit(TypedSlotRow row, int word, ulong bit, bool hasValue)
    {
        row.Recorded[word] |= bit;
        if (hasValue)
        {
            row.WasNull[word] &= ~bit;
        }
        else
        {
            row.WasNull[word] |= bit;
        }
    }
}
