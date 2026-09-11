#region

using System.Numerics;
using System.Runtime.InteropServices;
using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Parser.EntityTracking;

#endregion

namespace CS2DemoKit.Analysis;

/// <summary>
///     The per-frame entity readout the scanner consumes: everything the analysis layer reads off
///     the entity set for one frame, decoupled from the decode that produced it. Built by
///     <see cref="EntityDigestExtractor" /> from a post-seek <see cref="EntityStateLayer" />, whether
///     the decode ran sequentially or in a parallel chunk worker. The (stateful) consume path reads
///     only this, never the live layer, so it is identical for either decode.
///     <para>
///         A digest for a frame where nothing happened allocates nothing beyond itself: the row
///         storage starts as the shared <see cref="PerPawnColumns.Empty" /> and the projectile and
///         smoke lists are created on the first entry.
///     </para>
/// </summary>
internal sealed class EntityFrameDigest
{
    private List<(int Index, int Serial, int ThrowerSlot)>? _molotovs;
    private List<Vector4>? _smokes;

    /// <summary>
    ///     Per-player-provider cells that CHANGED this frame, one row per pawn with at least one
    ///     changed column, cells unboxed in the producing stream's
    ///     <see cref="DigestColumnLayout" />. A column absent from a row means "no update" and is
    ///     skipped when merged into the pre-frame snapshot; a pawn whose values all held contributes
    ///     no row at all.
    ///     <para>
    ///         Deltas rather than a full readout because the consumer
    ///         (<c>EntityChangeScanner.MergePreFrameSnapshot</c>) folds these into a running
    ///         last-value-per-(column, slot) map, so a value equal to the one already folded is a
    ///         no-op. On the shipped provider set roughly one cell in a thousand actually changes. A
    ///         provider that changes every frame (a position, say) degrades this to the full readout
    ///         plus a comparison, which is now four bytes per cell rather than a box.
    ///     </para>
    /// </summary>
    public PerPawnColumns PerPawn = PerPawnColumns.Empty;

    /// <summary>Singleton provider values this frame (indexed by the singleton-provider list order; null = no value yet).</summary>
    public object?[] Singletons = [];

    /// <summary>
    ///     True when the producing tracker had recorded an entity-decode error
    ///     (<see cref="EntityTracker.LastEntityError" />) by the time this digest was built, meaning the
    ///     entity state behind <see cref="PerPawn" /> is no longer trustworthy (on a bit-misaligned
    ///     demo the per-pawn values freeze at their last successfully-decoded state). The scanner stops
    ///     folding <see cref="PerPawn" /> into the pre-frame snapshot from the first compromised digest
    ///     onward, so consumers see event-tracked fallbacks instead of silently-stale entity values;
    ///     singleton and molotov consumption are deliberately unaffected. This is decode-integrity
    ///     hardening; the EnemyDmg-overcount fix itself is the same-frame guard in
    ///     <c>HurtTeamEnrichmentEdge</c>.
    ///     <para>
    ///         The flag is per-producing-tracker, so a parallel chunk worker that re-primed from a
    ///         checkpoint AFTER an earlier chunk's error reports <c>false</c> again; the scanner's
    ///         sequential consume latches instead (see <c>EntityChangeScanner.MergePreFrameSnapshot</c>),
    ///         which is what restores the sequential single-tracker behaviour the goldens were
    ///         verified against.
    ///     </para>
    /// </summary>
    public bool DecodeCompromised;

    /// <summary>Live CMolotovProjectiles this frame: (entity index, serial, resolved thrower slot or -1).</summary>
    public ReadOnlySpan<(int Index, int Serial, int ThrowerSlot)> Molotovs =>
        _molotovs is null ? default : CollectionsMarshal.AsSpan(_molotovs);

    /// <summary>
    ///     Active smoke clouds this frame as spheres <c>(centre.xyz, radius)</c>, or empty when the
    ///     producing decode was not asked for them. Carried on the digest because the consumer that
    ///     needs them (the visibility transition scan) runs on the sequential consume side, where the
    ///     parallel path's entity set is long gone: without this the scan would have no way to know a
    ///     sightline passed through smoke, and every through-smoke sightline would read as a spot.
    /// </summary>
    public ReadOnlySpan<Vector4> Smokes => _smokes is null ? default : CollectionsMarshal.AsSpan(_smokes);

    /// <summary>Records one live molotov projectile.</summary>
    public void AddMolotov(int index, int serial, int throwerSlot) =>
        (_molotovs ??= new List<(int, int, int)>(4)).Add((index, serial, throwerSlot));

    /// <summary>Records one active smoke cloud.</summary>
    public void AddSmoke(Vector4 sphere) => (_smokes ??= new List<Vector4>(4)).Add(sphere);
}
