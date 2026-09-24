#region

using System.Numerics;
using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Analysis.Plugins;
using CS2DemoKit.Analysis.Visibility;
using CS2DemoKit.Parser.EntityTracking;

#endregion

namespace CS2DemoKit.Analysis;

/// <summary>
///     Builds an <see cref="EntityFrameDigest" /> from a layer's current (post-seek) entity state. This is
///     the single source of truth for digest extraction, shared by the sequential scanner
///     (<c>EntityChangeScanner.BuildDigest</c>) and the pipelined producer's chunk workers
///     (<c>PipelinedDigestSource</c>). Singletons and molotovs come out identical by construction;
///     per-pawn rows depend on the caller's <see cref="PerPawnDeltaState" />, so those agree once folded
///     rather than row for row.
///     <para>
///         Per pawn, every column is read through its typed cell reader where the provider has one
///         (every shipped provider does) and compared unboxed against the stream's last value; a
///         provider without a typed reader is read boxed and unboxed into its declared column, which
///         is correct but pays the allocation this path otherwise avoids. The per-frame sweep passes
///         its state through <see cref="PawnLookup.ForEachLivePawn{TState}" /> with a static
///         callback, so a frame allocates nothing per pawn beyond the rows it emits.
///     </para>
/// </summary>
internal static class EntityDigestExtractor
{
    /// <summary>
    ///     Extracts the per-frame digest: changed per-player provider cells per live pawn, singleton
    ///     provider values, and live molotov projectiles with their resolved thrower slot.
    /// </summary>
    /// <param name="layer">The layer to read the current (post-seek) entity state from.</param>
    /// <param name="delta">
    ///     The caller's per-stream cell memory, which also carries the column layout (hence the
    ///     per-player providers, read in column order) and the read context. With
    ///     <see cref="PerPawnDeltaState.Dedup" /> on, <see cref="EntityFrameDigest.PerPawn" /> carries
    ///     only the cells that changed since the previous frame in that stream; off, the full per-frame
    ///     readout.
    /// </param>
    /// <param name="singletonProviders">Singleton providers, read once per frame in list order.</param>
    /// <param name="emitMolotovThrows">When true, the digest includes live <c>CMolotovProjectile</c>s.</param>
    /// <param name="captureSmokes">
    ///     When true, the digest carries this frame's active smoke clouds (see
    ///     <see cref="EntityFrameDigest.Smokes" />). Off by default because only the visibility
    ///     transition scan has any use for them. Shares the molotov visit when both are on.
    /// </param>
    /// <param name="projectiles">
    ///     The caller's slot index for the projectile classes, which turns the molotov and smoke
    ///     visit from a walk over every live entity into a read of the few slots that hold one.
    ///     Null falls back to the full walk; the two produce byte-identical digests (the parity
    ///     tests pin it), so the walk stays as the oracle rather than as a path anything ships on.
    /// </param>
    /// <param name="pawns">
    ///     The caller's pawn slot index, which turns the per-pawn sweep from a walk over every live
    ///     entity into a read of the dozen slots that hold one. Null falls back to the full walk,
    ///     which produces the same digest.
    /// </param>
    internal static EntityFrameDigest Build(
        EntityStateLayer layer,
        PerPawnDeltaState delta,
        IReadOnlyList<IEntityValueProvider> singletonProviders,
        bool emitMolotovThrows,
        bool captureSmokes = false,
        ProjectileSlotIndex? projectiles = null,
        PawnSlotIndex? pawns = null)
    {
        ArgumentNullException.ThrowIfNull(layer);
        ArgumentNullException.ThrowIfNull(delta);
        ArgumentNullException.ThrowIfNull(singletonProviders);

        EntityTracker tracker = layer.Tracker;
        EntityFrameDigest d = new()
        {
            // Stamp decode integrity at build time. LastEntityError is sticky per tracker, so on
            // the sequential path every digest from the first error onward is flagged; on the parallel
            // path each chunk worker flags from its own first error (the scanner's consume latch makes
            // that sticky across chunk boundaries).
            DecodeCompromised = tracker.LastEntityError is not null
        };

        if (delta.Layout.Count > 0)
        {
            if (pawns is not null)
            {
                pawns.ForEachLivePawn(tracker, new PawnSweep(tracker, delta, d),
                    static (sweep, slot, pawn) => ReadPawn(sweep, slot, pawn));
            }
            else
            {
                PawnLookup.ForEachLivePawn(tracker, new PawnSweep(tracker, delta, d),
                    static (sweep, slot, pawn) => ReadPawn(sweep, slot, pawn));
            }

            delta.LastRowCount = d.PerPawn.Count;
        }

        d.Singletons = singletonProviders.Count > 0 ? new object?[singletonProviders.Count] : [];
        for (int i = 0; i < singletonProviders.Count; i++)
        {
            d.Singletons[i] = singletonProviders[i].Read(layer);
        }

        d.ControllerTeams = ReadControllerTeams(tracker, delta);

        if (emitMolotovThrows || captureSmokes)
        {
            if (projectiles is null)
            {
                // The reference walk: one pass serves both classes, in ascending index order.
                foreach ((int idx, EntityState ent) in tracker.CurrentEntities.AllIndexed())
                {
                    VisitProjectile(d, tracker, idx, ent, emitMolotovThrows, captureSmokes);
                }
            }
            else
            {
                // Same visit over the same entities in the same order, read from the index
                // instead of found by elimination among a few hundred live entities.
                projectiles.Sync(tracker);
                foreach (int idx in projectiles.Slots)
                {
                    if (tracker.CurrentEntities[idx] is { } ent)
                    {
                        VisitProjectile(d, tracker, idx, ent, emitMolotovThrows, captureSmokes);
                    }
                }
            }
        }

        return d;
    }

    private const string ControllerClass = "CCSPlayerController";
    private const int MaxSlots = 64;

    // Controllers occupy entity indices 1..64, one per slot. Sixty-four indexed reads, no walk,
    // and an array only on the frames where a slot's team differs from the last one read.
    private static int[]? ReadControllerTeams(EntityTracker tracker, PerPawnDeltaState delta)
    {
        int[] last = delta.ControllerTeams;
        bool changed = false;
        EntitySet entities = tracker.CurrentEntities;
        for (int slot = 0; slot < MaxSlots; slot++)
        {
            int team = entities[slot + 1] is { } controller && controller.ClassName == ControllerClass
                ? controller.TryGet<int>("m_iTeamNum") ?? -1
                : -1;
            if (team != last[slot])
            {
                last[slot] = team;
                changed = true;
            }
        }

        return changed ? (int[])last.Clone() : null;
    }

    // The per-entity half of the projectile visit, shared by the walk and the indexed read so the
    // two cannot drift: a molotov is recorded with its thrower, otherwise a billowing smoke is
    // recorded as a sphere. Any other class is a no-op, which is what lets the walk pass every
    // live entity through it.
    private static void VisitProjectile(
        EntityFrameDigest d, EntityTracker tracker, int idx, EntityState ent,
        bool emitMolotovThrows, bool captureSmokes)
    {
        if (emitMolotovThrows && ent.ClassName == ProjectileSlotIndex.MolotovClass)
        {
            d.AddMolotov(idx, ent.Serial, PawnLookup.ResolveThrowerSlot(tracker, ent));
            return;
        }

        // The gate (m_nSmokeEffectTickBegin > 0 for a billowing cloud, a non-degenerate
        // detonation position) is the analyzer's own, so the transition scan and the
        // accumulating analyzer cannot disagree about which clouds are active.
        if (captureSmokes && VisibilityAnalyzer.TryActiveSmoke(ent, out Vector4 sphere))
        {
            d.AddSmoke(sphere);
        }
    }

    /// <summary>
    ///     Reads every column for one pawn, records each against the stream's memory, and appends a
    ///     row when anything changed. A column whose read went from a value to "no value" counts as
    ///     changed (so the row is emitted) but is not present in it, which is the boxed digest's
    ///     "null cell means no update" restated.
    /// </summary>
    private static void ReadPawn(PawnSweep sweep, int slot, EntityState pawn)
    {
        PerPawnDeltaState delta = sweep.Delta;
        DigestColumnLayout layout = delta.Layout;
        PawnReadContext context = delta.Context;
        context.Bind(sweep.Tracker, pawn);

        TypedSlotRow row = delta.RowFor(slot);
        ulong[] present = delta.PresentScratch;
        Array.Clear(present);

        bool anyChanged = false;
        PawnCellKind[] kinds = layout.Kinds;
        for (int p = 0; p < kinds.Length; p++)
        {
            bool hasValue;
            bool changed;
            switch (kinds[p])
            {
                case PawnCellKind.Int:
                case PawnCellKind.Bool:
                    hasValue = ReadInt(layout, p, context, out int intValue);
                    changed = delta.RecordInt(row, p, hasValue, intValue);
                    break;
                case PawnCellKind.Float:
                    hasValue = ReadFloat(layout, p, context, out float floatValue);
                    changed = delta.RecordFloat(row, p, hasValue, floatValue);
                    break;
                default:
                    string? text = ReadString(layout, p, context);
                    hasValue = text is not null;
                    changed = delta.RecordString(row, p, text);
                    break;
            }

            if (changed)
            {
                anyChanged = true;
                if (hasValue)
                {
                    present[p >> 6] |= 1UL << (p & 63);
                }
            }
        }

        if (!anyChanged)
        {
            return;
        }

        EntityFrameDigest digest = sweep.Digest;
        if (ReferenceEquals(digest.PerPawn, PerPawnColumns.Empty))
        {
            digest.PerPawn = new PerPawnColumns(layout, Math.Max(PerPawnColumns.InitialCapacity, delta.LastRowCount));
        }

        digest.PerPawn.AppendRow(slot, row, present);
    }

    private static bool ReadInt(DigestColumnLayout layout, int column, PawnReadContext context, out int value)
    {
        if (layout.IntReaders[column] is { } reader)
        {
            return reader.TryReadInt(context, out value);
        }

        IPerPlayerEntityValueProvider provider = layout.Providers[column];
        object? boxed = ReadBoxed(provider, context);
        switch (boxed)
        {
            case null:
                value = 0;
                return false;
            case int i when layout.Kinds[column] == PawnCellKind.Int:
                value = i;
                return true;
            case bool b when layout.Kinds[column] == PawnCellKind.Bool:
                value = b ? 1 : 0;
                return true;
            default:
                throw Mismatch(provider, boxed);
        }
    }

    private static bool ReadFloat(DigestColumnLayout layout, int column, PawnReadContext context, out float value)
    {
        if (layout.FloatReaders[column] is { } reader)
        {
            return reader.TryReadFloat(context, out value);
        }

        IPerPlayerEntityValueProvider provider = layout.Providers[column];
        object? boxed = ReadBoxed(provider, context);
        switch (boxed)
        {
            case null:
                value = 0f;
                return false;
            case float f:
                value = f;
                return true;
            default:
                throw Mismatch(provider, boxed);
        }
    }

    private static string? ReadString(DigestColumnLayout layout, int column, PawnReadContext context)
    {
        if (layout.StringReaders[column] is { } reader)
        {
            return reader.TryReadString(context, out string? value) ? value : null;
        }

        IPerPlayerEntityValueProvider provider = layout.Providers[column];
        object? boxed = ReadBoxed(provider, context);
        return boxed switch
        {
            null => null,
            string s => s,
            _ => throw Mismatch(provider, boxed)
        };
    }

    // The boxed contract, for a provider with no typed reader. A provider reading leaves the SDK
    // wrapper cannot resolve (position's CBodyComponent pair) takes the raw state instead.
    private static object? ReadBoxed(IPerPlayerEntityValueProvider provider, PawnReadContext context) =>
        provider is IPawnStateReader stateReader
            ? stateReader.ReadForPawnState(context.Tracker, context.Pawn)
            : provider.ReadForPawn(context.Tracker, context.Wrapper);

    private static InvalidOperationException Mismatch(IPerPlayerEntityValueProvider provider, object boxed) =>
        new($"per-player provider '{provider.Name}' declares value type {provider.ValueType.Name} but read a "
            + $"{boxed.GetType().Name}; the digest stores each column in its declared type");

    /// <summary>What one frame's pawn sweep carries into the static callback.</summary>
    private readonly record struct PawnSweep(EntityTracker Tracker, PerPawnDeltaState Delta, EntityFrameDigest Digest);
}
