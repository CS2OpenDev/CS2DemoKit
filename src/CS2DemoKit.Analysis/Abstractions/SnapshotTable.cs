namespace CS2DemoKit.Analysis.Abstractions;

/// <summary>
///     The per-message node-snapshot table: one logical row of <see cref="NodeSnapshot" /> cells per
///     evaluated message, one column per tracked node. Storage is chunked copy-on-write: a row is an
///     array of fixed-size column chunks, unchanged rows share the previous row's chunk array by
///     reference, and a dirty message clones only the chunks holding dirty columns. That keeps the
///     dominant snapshot cost proportional to <c>dirty rows × dirty chunks</c> instead of
///     <c>dirty rows × total column count</c> — the full-row-clone form allocated hundreds of MiB per
///     demo once per-player highlight nodes widened the table (405 MiB → ~25 MiB on the D4 bench).
///     <para>
///         Columns appended by late player materialization are absent from earlier rows' chunk
///         coverage; the indexer returns the column's at-materialization default for those reads,
///         which replaces the old end-of-run padding pass byte-for-byte (padding filled the same
///         defaults eagerly). Cells past a row's coverage inside the static width read as
///         <c>default(NodeSnapshot)</c> — the same value the eager rows started from.
///     </para>
/// </summary>
public sealed class SnapshotTable
{
    /// <summary>Columns per chunk. 64 × 24-byte cells ≈ 1.5 KB — small enough that a lone dirty column stays cheap.</summary>
    public const int ChunkSize = 64;

    /// <summary>log2(<see cref="ChunkSize" />), for index math.</summary>
    public const int ChunkShift = 6;

    /// <summary><see cref="ChunkSize" /> − 1, for index math.</summary>
    public const int ChunkMask = ChunkSize - 1;

    private readonly NodeSnapshot[] _lateDefaults; // by (column − _staticWidth): at-materialization defaults
    private readonly NodeSnapshot[]?[][] _rows;    // [message] → chunk array; chunk slots may be null (never-dirty coverage)
    private readonly int _staticWidth;

    /// <summary>
    ///     Wraps evaluator-produced chunk rows. <paramref name="lateDefaults" /> carries the
    ///     at-materialization default for every column appended after the static prefix.
    /// </summary>
    public SnapshotTable(NodeSnapshot[]?[][] rows, int staticWidth, NodeSnapshot[] lateDefaults, int width)
    {
        _rows = rows;
        _staticWidth = staticWidth;
        _lateDefaults = lateDefaults;
        Width = width;
    }

    /// <summary>Number of messages (logical rows).</summary>
    public int Count => _rows.Length;

    /// <summary>Final column count (every tracked node, including late-materialized ones).</summary>
    public int Width { get; }

    /// <summary>
    ///     The cell for <paramref name="column" /> at <paramref name="messageIndex" />. Columns beyond
    ///     the row's stored coverage read as their defaults (see class doc) — callers may index any
    ///     column &lt; <see cref="Width" /> at any message.
    /// </summary>
    public NodeSnapshot this[int messageIndex, int column]
    {
        get
        {
            NodeSnapshot[]?[] chunks = _rows[messageIndex];
            int c = column >> ChunkShift;
            if ((uint)c < (uint)chunks.Length)
            {
                NodeSnapshot[]? chunk = chunks[c];
                int o = column & ChunkMask;
                if (chunk is not null && o < chunk.Length)
                {
                    return chunk[o];
                }
            }

            int late = column - _staticWidth;
            return (uint)late < (uint)_lateDefaults.Length ? _lateDefaults[late] : default;
        }
    }

    /// <summary>
    ///     Copies one full-width row out as a plain array — for the sampled-row consumers (final
    ///     snapshot, per-round samples, UI seek) that hand a whole row to row-shaped helpers.
    ///     Allocates <see cref="Width" /> cells; not for per-message scans — index cells directly there.
    /// </summary>
    public NodeSnapshot[] MaterializeRow(int messageIndex)
    {
        NodeSnapshot[] row = new NodeSnapshot[Width];
        for (int i = 0; i < Width; i++)
        {
            row[i] = this[messageIndex, i];
        }

        return row;
    }

    /// <summary>
    ///     Fixture support: lets hand-built row arrays flow into <see cref="SnapshotTable" />
    ///     parameters unwrapped (equivalent to <see cref="FromRows" />). Production code never
    ///     holds full-row arrays — the evaluator produces chunked tables directly.
    /// </summary>
    public static implicit operator SnapshotTable(NodeSnapshot[][] rows) => FromRows(rows);

    /// <summary>
    ///     Builds a table from plain full rows (no chunk sharing) — the test-fixture and
    ///     hand-construction path. Ragged rows are honoured: cells beyond a row's length read as
    ///     <c>default(NodeSnapshot)</c>, matching the pre-table guard convention.
    /// </summary>
    public static SnapshotTable FromRows(NodeSnapshot[][] rows)
    {
        int width = 0;
        foreach (NodeSnapshot[] row in rows)
        {
            width = Math.Max(width, row.Length);
        }

        NodeSnapshot[]?[][] chunked = new NodeSnapshot[]?[rows.Length][];
        for (int r = 0; r < rows.Length; r++)
        {
            NodeSnapshot[] row = rows[r];
            int chunkCount = (row.Length + ChunkMask) >> ChunkShift;
            NodeSnapshot[]?[] chunks = new NodeSnapshot[]?[chunkCount];
            for (int c = 0; c < chunkCount; c++)
            {
                int baseCol = c << ChunkShift;
                int len = Math.Min(ChunkSize, row.Length - baseCol);
                NodeSnapshot[] chunk = new NodeSnapshot[len];
                Array.Copy(row, baseCol, chunk, 0, len);
                chunks[c] = chunk;
            }

            chunked[r] = chunks;
        }

        return new SnapshotTable(chunked, width, [], width);
    }
}
