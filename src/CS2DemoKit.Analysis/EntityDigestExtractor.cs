#region

using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Analysis.Plugins;
using CS2OpenDev.Sdk.Entities;
using CS2DemoKit.Parser.EntityTracking;

#endregion

namespace CS2DemoKit.Analysis;

/// <summary>
///     The per-frame entity readout the scanner consumes — everything the analysis layer reads off the
///     entity set for one frame, decoupled from the decode that produced it. Built by
///     <see cref="EntityDigestExtractor" /> from a post-seek <see cref="EntityStateLayer" />, whether the
///     decode ran sequentially or in a parallel chunk worker. The (stateful) consume
///     path reads only this — never the live layer — so it is identical for either decode.
/// </summary>
internal sealed class EntityFrameDigest
{
    /// <summary>Live CMolotovProjectiles this frame: (entity index, serial, resolved thrower slot or -1).</summary>
    public readonly List<(int Index, int Serial, int ThrowerSlot)> Molotovs = [];

    /// <summary>
    ///     Per live pawn this frame: its slot and the per-player-provider values (indexed by the
    ///     provider list order; a null entry means "no value" and is skipped when merged into the
    ///     pre-frame snapshot).
    /// </summary>
    public readonly List<(int Slot, object?[] Values)> PerPawn = [];

    /// <summary>Singleton provider values this frame (indexed by the singleton-provider list order; null = no value yet).</summary>
    public object?[] Singletons = [];

    /// <summary>
    ///     True when the producing tracker had recorded an entity-decode error
    ///     (<see cref="EntityTracker.LastEntityError" />) by the time this digest was built — i.e. the
    ///     entity state behind <see cref="PerPawn" /> is no longer trustworthy (on a bit-misaligned
    ///     demo the per-pawn values freeze at their last successfully-decoded state). The scanner stops
    ///     folding <see cref="PerPawn" /> into the pre-frame snapshot from the first compromised digest
    ///     onward, so consumers see event-tracked fallbacks instead of silently-stale entity values;
    ///     singleton and molotov consumption are deliberately unaffected. This is decode-integrity
    ///     hardening — the EnemyDmg-overcount fix itself is the same-frame guard in
    ///     <c>HurtTeamEnrichmentEdge</c>.
    ///     <para>
    ///         The flag is per-producing-tracker, so a parallel chunk worker that re-primed from a
    ///         checkpoint AFTER an earlier chunk's error reports <c>false</c> again — the scanner's
    ///         sequential consume latches instead (see <c>EntityChangeScanner.MergePreFrameSnapshot</c>),
    ///         which is what restores the sequential single-tracker behaviour the goldens were
    ///         verified against.
    ///     </para>
    /// </summary>
    public bool DecodeCompromised;
}

/// <summary>
///     Builds an <see cref="EntityFrameDigest" /> from a layer's current (post-seek) entity state. This is
///     the single source of truth for digest extraction, shared by the sequential scanner
///     (<c>EntityChangeScanner.BuildDigest</c>) and the parallel chunk decoder
///     (<c>ParallelDigestProducer</c>), so both produce byte-identical digests by construction.
/// </summary>
internal static class EntityDigestExtractor
{
    /// <summary>
    ///     Extracts the per-frame digest: per-player provider values per live pawn (one
    ///     <see cref="CSPlayerPawn" /> wrapper per pawn dispatched to every provider), singleton provider
    ///     values, and live molotov projectiles with their resolved thrower slot.
    /// </summary>
    internal static EntityFrameDigest Build(
        EntityStateLayer layer,
        IReadOnlyList<IPerPlayerEntityValueProvider> perPlayerProviders,
        IReadOnlyList<IEntityValueProvider> singletonProviders,
        bool emitMolotovThrows)
    {
        EntityTracker tracker = layer.Tracker;
        EntityFrameDigest d = new()
        {
            // Stamp decode integrity at build time. LastEntityError is sticky per tracker, so on
            // the sequential path every digest from the first error onward is flagged; on the parallel
            // path each chunk worker flags from its own first error (the scanner's consume latch makes
            // that sticky across chunk boundaries).
            DecodeCompromised = tracker.LastEntityError is not null
        };

        int providerCount = perPlayerProviders.Count;
        if (providerCount > 0)
        {
            PawnLookup.ForEachLivePawn(tracker, (slot, pawn) =>
            {
                CSPlayerPawn wrapper = SdkEntityWorlds.Wrap<CSPlayerPawn>(tracker, pawn)!;
                object?[] values = new object?[providerCount];
                for (int p = 0; p < providerCount; p++)
                {
                    values[p] = perPlayerProviders[p].ReadForPawn(tracker, wrapper);
                }

                d.PerPawn.Add((slot, values));
            });
        }

        d.Singletons = singletonProviders.Count > 0 ? new object?[singletonProviders.Count] : [];
        for (int i = 0; i < singletonProviders.Count; i++)
        {
            d.Singletons[i] = singletonProviders[i].Read(layer);
        }

        if (emitMolotovThrows)
        {
            foreach ((int idx, EntityState ent) in tracker.CurrentEntities.AllIndexed())
            {
                if (ent.ClassName != "CMolotovProjectile")
                {
                    continue;
                }

                d.Molotovs.Add((idx, ent.Serial, ResolveThrowerSlot(tracker, ent)));
            }
        }

        return d;
    }

    /// <summary>
    ///     Resolves a projectile's thrower to a player slot via the validated chain
    ///     <c>m_hThrower → pawn → m_hController → slot</c> (slot = controller index − 1). Returns
    ///     <c>-1</c> when the handle is missing or doesn't resolve to a controller-bound pawn.
    /// </summary>
    internal static int ResolveThrowerSlot(EntityTracker tracker, EntityState projectile)
    {
        // Single-key seen-gated read via the indexer instead of projectile.Fields, which rebuilds the
        // ENTIRE per-entity dict projection on every access (per live molotov per frame). The indexer
        // returns null for an unseen field (the _seen[] bitvector gates every lane and it falls through
        // to the fallback dict), byte-identical to the old Fields.TryGetValue-false path; a received
        // handle flows on unchanged. Mirrors the FreezePeriodProvider seen-gated swap.
        object? throwerHandle = projectile["m_hThrower"];
        if (throwerHandle is null)
        {
            return -1;
        }

        EntityState? pawn = PawnLookup.ResolveHandle(tracker, throwerHandle);

        // m_hController is NOT a clean indexer swap: the control flow returns -1 only on ABSENT and
        // lets a present-null fall through to TryUnboxHandle, a shape the indexer cannot reproduce
        // (it collapses absent and present-null). EntityState.TryGetValue keeps that distinction with
        // Fields' exact resolution order, without materialising the whole per-entity dict projection —
        // which this call site was doing per live molotov per frame.
        if (pawn is null || !pawn.TryGetValue("m_hController", out object? controllerHandle))
        {
            return -1;
        }

        uint controller = PawnLookup.TryUnboxHandle(controllerHandle);
        if (controller == 0)
        {
            return -1;
        }

        return (int)(controller & PawnLookup.EntityIndexMask) - 1;
    }
}
