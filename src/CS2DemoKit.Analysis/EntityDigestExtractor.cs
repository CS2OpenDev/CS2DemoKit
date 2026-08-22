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
    ///     Per-player-provider values that CHANGED this frame, as (slot, values indexed by provider
    ///     list order). A null entry means "no update" and is skipped when merged into the pre-frame
    ///     snapshot; a pawn whose values all held contributes no row at all.
    ///     <para>
    ///         Deltas rather than a full readout because the consumer
    ///         (<c>EntityChangeScanner.MergePreFrameSnapshot</c>) folds these into a running
    ///         last-value-per-(provider, slot) map, so a value equal to the one already folded is a
    ///         no-op. On the shipped provider set roughly one cell in a thousand actually changes, and
    ///         materializing the other 999 cost a boxed value each for the whole demo's digest stream.
    ///         A provider that changes every frame (a position, say) degrades this to the full readout
    ///         plus a comparison.
    ///     </para>
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
///     One decode stream's memory of the last per-pawn value emitted for each (slot, provider), so
///     <see cref="EntityDigestExtractor.Build" /> can emit only the cells that changed.
///     <para>
///         One instance per stream and never shared: one per parallel chunk worker, one per sequential
///         scanner. A worker starting at a checkpoint has no history, so its first frame re-emits every
///         live cell. That is redundant, not wrong: the consumer folds the values into
///         <c>EntityChangeScanner._preFrameSnapshot</c>, and re-writing a key with the value it already
///         holds is a no-op.
///     </para>
/// </summary>
internal sealed class PerPawnDeltaState(int providerCount)
{
    // Distinguishes "never recorded" from "recorded null". A provider legitimately reads null (entity
    // not spawned, field unseen), and that first null must count as a change.
    private static readonly object Unset = new();

    private object?[]?[] _bySlot = new object?[]?[64];

    /// <summary>
    ///     Records <paramref name="value" /> for (<paramref name="slot" />, <paramref name="provider" />)
    ///     and returns whether it differs from the last value recorded for that cell.
    /// </summary>
    public bool Record(int slot, int provider, object? value)
    {
        if ((uint)slot >= (uint)_bySlot.Length)
        {
            Array.Resize(ref _bySlot, Math.Max(slot + 1, _bySlot.Length * 2));
        }

        object?[]? row = _bySlot[slot];
        if (row is null)
        {
            row = new object?[providerCount];
            Array.Fill(row, Unset);
            _bySlot[slot] = row;
        }

        object? previous = row[provider];
        if (!ReferenceEquals(previous, Unset) && Equals(previous, value))
        {
            return false;
        }

        row[provider] = value;
        return true;
    }
}

/// <summary>
///     Builds an <see cref="EntityFrameDigest" /> from a layer's current (post-seek) entity state. This is
///     the single source of truth for digest extraction, shared by the sequential scanner
///     (<c>EntityChangeScanner.BuildDigest</c>) and the parallel chunk decoder
///     (<c>ParallelDigestProducer</c>). Singletons and molotovs come out identical by construction;
///     per-pawn rows depend on the caller's <see cref="PerPawnDeltaState" />, so those agree once folded
///     rather than row for row.
/// </summary>
internal static class EntityDigestExtractor
{
    /// <summary>
    ///     Extracts the per-frame digest: per-player provider values per live pawn (one
    ///     <see cref="CSPlayerPawn" /> wrapper per pawn dispatched to every provider), singleton provider
    ///     values, and live molotov projectiles with their resolved thrower slot.
    /// </summary>
    /// <param name="layer">The layer to read the current (post-seek) entity state from.</param>
    /// <param name="perPlayerProviders">Per-player providers, read once per live pawn in list order.</param>
    /// <param name="singletonProviders">Singleton providers, read once per frame in list order.</param>
    /// <param name="emitMolotovThrows">When true, the digest includes live <c>CMolotovProjectile</c>s.</param>
    /// <param name="delta">
    ///     The caller's per-stream cell memory. When supplied, <see cref="EntityFrameDigest.PerPawn" />
    ///     carries only the cells that changed since the previous frame in that stream, with unchanged
    ///     positions left null; a pawn whose values all held emits no row at all. Pass <c>null</c> for the
    ///     full per-frame readout.
    /// </param>
    internal static EntityFrameDigest Build(
        EntityStateLayer layer,
        IReadOnlyList<IPerPlayerEntityValueProvider> perPlayerProviders,
        IReadOnlyList<IEntityValueProvider> singletonProviders,
        bool emitMolotovThrows,
        PerPawnDeltaState? delta = null)
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

                // Allocated on first write, so an all-unchanged pawn costs nothing. Every provider is
                // still read: the change is what gets stored, not what gets computed.
                object?[]? values = null;
                for (int p = 0; p < providerCount; p++)
                {
                    object? value = perPlayerProviders[p].ReadForPawn(tracker, wrapper);
                    if (delta is null || delta.Record(slot, p, value))
                    {
                        (values ??= new object?[providerCount])[p] = value;
                    }
                }

                if (values is not null)
                {
                    d.PerPawn.Add((slot, values));
                }
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

        // Must go through IndexOf. A dead pawn's m_hController is the 24-bit invalid handle, and
        // masking it raw yields slot 16382, which this method's contract says should be -1. Nothing
        // downstream re-checks, and unlike a table lookup there is no empty slot to save it.
        int controllerIdx = PawnLookup.IndexOf(PawnLookup.TryUnboxHandle(controllerHandle));
        return controllerIdx <= 0 ? -1 : controllerIdx - 1;
    }
}
