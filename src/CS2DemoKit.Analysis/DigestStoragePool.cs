#region

using CS2DemoKit.Analysis.Observation;

#endregion

namespace CS2DemoKit.Analysis;

/// <summary>
///     One digest stream's per-frame storage: the <see cref="PerPawnColumns" /> a frame's rows
///     land in, recycled, and the projectile rows, appended into shared blocks. The producer
///     rents on its own thread; the consumer returns columns from the evaluation thread once it
///     has folded the digest, so a stream that emits a row per pawn every frame allocates a few
///     frames' worth of storage per run instead of one per frame.
///     <para>
///         Projectile rows are not recycled. Over a retained demo the chunks run tens of
///         thousands of frames ahead of the consumer, so a per-frame list rented here almost
///         never found a returned one and every rent was an allocation taken under the lock; a
///         block holds hundreds of frames' rows and is garbage once its digests are.
///     </para>
///     <para>
///         One instance per producing stream (a pipelined worker, or the sequential scanner),
///         shared across the chunks that stream builds. Rows returned here are dead: the consumer
///         has copied every cell it keeps.
///     </para>
/// </summary>
internal sealed class DigestStoragePool
{
    /// <summary>Rows per projectile block: under the large-object threshold at the row's size.</summary>
    internal const int ProjectileBlockRows = 512;

    private readonly Stack<PerPawnColumns> _columns = new();
    private readonly object _gate = new();

    // Producer thread only. The digest being filled owns [_used - count, _used) of _block.
    private ProjectileRow[] _block = [];
    private int _used;

    /// <summary>
    ///     A zero-row instance bound to <paramref name="layout" />, recycled when one is free and
    ///     created at <paramref name="capacity" /> rows otherwise.
    /// </summary>
    public PerPawnColumns RentColumns(DigestColumnLayout layout, int capacity)
    {
        PerPawnColumns? columns;
        lock (_gate)
        {
            _columns.TryPop(out columns);
        }

        if (columns is null)
        {
            return new PerPawnColumns(layout, capacity, this);
        }

        columns.Rebind(layout);
        return columns;
    }

    /// <summary>
    ///     Appends <paramref name="row" /> to the digest whose storage the three refs describe. A
    ///     digest's rows stay contiguous in one block: when the current block fills mid-digest its
    ///     rows so far move to the head of a fresh one. One digest is filled at a time, and
    ///     completely, before the next starts.
    /// </summary>
    public void AppendProjectile(ref ProjectileRow[]? block, ref int start, ref int count, in ProjectileRow row)
    {
        if (count == 0)
        {
            block = _block;
            start = _used;
        }
        else if (!ReferenceEquals(block, _block) || start + count != _used)
        {
            throw new InvalidOperationException("a digest's projectile rows must be appended before the next digest starts");
        }

        if (_used == _block.Length)
        {
            ProjectileRow[] fresh = new ProjectileRow[Math.Max(ProjectileBlockRows, count + 1)];
            if (count > 0)
            {
                Array.Copy(block!, start, fresh, 0, count);
            }

            _block = fresh;
            _used = count;
            block = fresh;
            start = 0;
        }

        _block[_used++] = row;
        count++;
    }

    /// <summary>Takes back row storage the consumer has finished with.</summary>
    public void Return(PerPawnColumns columns)
    {
        columns.Reset();
        lock (_gate)
        {
            _columns.Push(columns);
        }
    }
}
