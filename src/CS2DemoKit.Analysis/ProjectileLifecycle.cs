#region

using System.Runtime.InteropServices;
using CS2DemoKit.Analysis.Observation;

#endregion

namespace CS2DemoKit.Analysis;

/// <summary>
///     Turns the per-frame projectile rows a digest carries into a lifecycle: which rows appeared
///     this frame, which projectiles live last frame are gone, and the live set with each thrower
///     held from its first resolution. Lives on the sequential consume side, next to the molotov
///     dedup, because a pipelined worker re-primes from a checkpoint and cannot tell a projectile
///     it has just met from one that was created; only the one continuous consumer can.
///     <para>
///         The semantics are <see cref="Parser.EntityTracking.ProjectileSampler" />'s: a projectile
///         is one <c>(EntityIndex, Serial, ClassName)</c>, it is created on the first frame its row
///         appears, removed on the first frame its slot is empty or holds another serial or class,
///         and its removed row is the last one seen. Every list is ascending by entity index, and a
///         slot that changed hands within one frame removes before it creates.
///     </para>
/// </summary>
internal sealed class ProjectileLifecycle
{
    // Ascending by entity index, the order the digest rows arrive in. _live and _next swap
    // every step; a row holds a reference, so copying one list into the other is not a memcpy.
    private List<ProjectileRow> _live = [];
    private readonly List<ProjectileRow> _created = [];
    private readonly List<ProjectileRow> _removed = [];
    private List<ProjectileRow> _next = [];

    /// <summary>Every live projectile after the last <see cref="Step" />, ascending entity index, throwers held.</summary>
    public ReadOnlySpan<ProjectileRow> Live => CollectionsMarshal.AsSpan(_live);

    /// <summary>Projectiles whose <c>(EntityIndex, Serial)</c> first appeared on the last <see cref="Step" />.</summary>
    public ReadOnlySpan<ProjectileRow> Created => CollectionsMarshal.AsSpan(_created);

    /// <summary>Projectiles live before the last <see cref="Step" /> and gone after it, each as last seen.</summary>
    public ReadOnlySpan<ProjectileRow> Removed => CollectionsMarshal.AsSpan(_removed);

    /// <summary>Forgets every projectile, for the start of a run.</summary>
    public void Reset()
    {
        _live.Clear();
        _created.Clear();
        _removed.Clear();
    }

    /// <summary>
    ///     Consumes one frame's rows, which must be ascending by entity index with no index twice,
    ///     as <see cref="EntityFrameDigest.Projectiles" /> is.
    /// </summary>
    public void Step(ReadOnlySpan<ProjectileRow> rows)
    {
        _created.Clear();
        _removed.Clear();
        _next.Clear();

        int l = 0;
        for (int r = 0; r < rows.Length; r++)
        {
            ProjectileRow row = rows[r];

            // Everything live below this row's index is gone from the digest.
            while (l < _live.Count && _live[l].EntityIndex < row.EntityIndex)
            {
                _removed.Add(_live[l++]);
            }

            if (l < _live.Count && _live[l].EntityIndex == row.EntityIndex)
            {
                ProjectileRow last = _live[l++];
                if (last.Serial == row.Serial && string.Equals(last.ClassName, row.ClassName, StringComparison.Ordinal))
                {
                    // First resolution wins, as the sampler's does: the chain breaks when the
                    // thrower dies mid-flight and must not re-resolve through a reused handle.
                    _next.Add(last.ThrowerSlot >= 0 ? row with { ThrowerSlot = last.ThrowerSlot } : row);
                    continue;
                }

                _removed.Add(last);
            }

            _created.Add(row);
            _next.Add(row);
        }

        while (l < _live.Count)
        {
            _removed.Add(_live[l++]);
        }

        (_live, _next) = (_next, _live);
    }
}
