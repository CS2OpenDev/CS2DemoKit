#region

using CS2DemoKit.Analysis.Observation;

#endregion

namespace CS2DemoKit.Analysis;

/// <summary>
///     Recycles one digest stream's per-frame storage: the <see cref="PerPawnColumns" /> a frame's
///     rows land in and the projectile row list. The producer rents on its own thread; the
///     consumer returns from the evaluation thread once it has folded the digest, so a stream
///     that emits a row per pawn every frame allocates a few frames' worth of storage per run
///     instead of one per frame.
///     <para>
///         One instance per producing stream (a pipelined worker, or the sequential scanner),
///         shared across the chunks that stream builds. Rows returned here are dead: the consumer
///         has copied every cell it keeps.
///     </para>
/// </summary>
internal sealed class DigestStoragePool
{
    private readonly Stack<PerPawnColumns> _columns = new();
    private readonly object _gate = new();
    private readonly Stack<List<ProjectileRow>> _projectiles = new();

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

    /// <summary>An empty row list, recycled when one is free.</summary>
    public List<ProjectileRow> RentProjectiles()
    {
        List<ProjectileRow>? rows;
        lock (_gate)
        {
            _projectiles.TryPop(out rows);
        }

        return rows ?? new List<ProjectileRow>(4);
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

    /// <summary>Takes back a projectile row list the consumer has finished with.</summary>
    public void Return(List<ProjectileRow> rows)
    {
        rows.Clear();
        lock (_gate)
        {
            _projectiles.Push(rows);
        }
    }
}
