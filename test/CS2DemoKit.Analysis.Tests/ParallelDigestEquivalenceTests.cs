#region

using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Analysis.Plugins;
using CS2DemoKit.Parser;
using CS2DemoKit.TestSupport;

#endregion

namespace CS2DemoKit.Analysis.Tests;

/// <summary>
///     The sharp correctness gate for parallel entity decode.
///     <para>
///         Builds the per-frame <see cref="EntityFrameDigest" /> array two ways. Sequentially, emitting the
///         full per-frame readout, and in parallel (<see cref="ParallelDigestProducer" />), which emits only
///         changed cells from each chunk worker. Both must agree on every frame. Singletons and the
///         molotov list are compared element-wise. Per-pawn values are compared as the FOLD: the running
///         last-value-per-(provider, slot) map that <c>EntityChangeScanner.MergePreFrameSnapshot</c> derives,
///         which is the only per-pawn state any rule reads.
///     </para>
///     <para>
///         Folding is what the assertion has to compare, not the raw rows: a worker has no history before its
///         checkpoint, so it re-emits every live cell on its chunk's first frame, and rows differ there by
///         construction. Comparing the fold is the stronger statement anyway: it judges the value the
///         consumer ends up with rather than the encoding it arrived in. The digest seam already proved the
///         sequential readout drives byte-identical golden output, so <em>same fold ⟹ parallel → golden</em>
///         by composition; a mismatch points at the exact frame (hence chunk) and cell that diverged.
///     </para>
///     <para>
///         The provider set deliberately includes all four per-player providers AND
///         <c>emitMolotov: true</c>, so the gate exercises the two riskiest checkpoint reconstructions: the
///         active-weapon CLASS two-hop (handle → weapon entity → ClassName) and the molotov thrower-slot
///         chain (m_hThrower → pawn → m_hController → slot).
///     </para>
/// </summary>
[NotInParallel]
[Category("Integration")]
public class ParallelDigestEquivalenceTests
{
    // Per-worker provider factories. Order is fixed (the digest's per-pawn value arrays and Singletons[]
    // are positionally indexed), and each call returns FRESH instances so a parallel worker never shares a
    // provider with another (FreezePeriodProvider caches a mutable entity index).
    private static IReadOnlyList<IPerPlayerEntityValueProvider> NewPerPlayer() =>
    [
        new PawnHealthProvider(),
        new PawnArmorProvider(),
        new PawnEquipmentValueProvider(),
        new ActiveWeaponProvider()
    ];

    private static IReadOnlyList<IEntityValueProvider> NewSingletons() =>
    [
        new FreezePeriodProvider()
    ];

    [Test]
    public async Task ParallelDigest_FoldsToTheSameSnapshotAs_SequentialDigest()
    {
        string path = DemoTestHelper.RequireDemo();
        ParsedDemo demo = DemoTestHelper.GetOrParse(path);
        IReadOnlyList<DemoFrame> frames = demo.Frames;

        Console.WriteLine($"Demo: {Path.GetFileName(path)}  frames={frames.Count:N0}");
        // Note: PlanChunks coarsens to ~Environment.ProcessorCount chunks, so this gate's chunk layout
        // depends on the runner's core count — it does NOT pin a specific chunking. That's fine: coarsening
        // is correctness-neutral (intermediate full packets decode via the normal PE-skipped SeekToTick
        // path), which is exactly what this equivalence assertion proves on whatever layout the runner picks.
        IReadOnlyList<ParallelDigestProducer.Chunk> chunks =
            ParallelDigestProducer.PlanChunks(frames, out int schemaPrefixEnd);
        Console.WriteLine($"chunks={chunks.Count}  schemaPrefixEnd={schemaPrefixEnd}  " +
                          $"checkpoints={chunks.Count(c => c.CheckpointFrameIndex >= 0)}");

        // ── Sequential reference: one layer, SeekToTick + Build per frame, emitting the FULL per-frame
        //    readout (no delta state). That is the ground truth the fold below is judged against. ──
        EntityFrameDigest[] sequential = BuildSequential(frames);

        // ── Parallel under test. ──
        EntityFrameDigest[] parallel = ParallelDigestProducer.Produce(
            frames, NewPerPlayer, NewSingletons, true);

        await Assert.That(parallel.Length).IsEqualTo(sequential.Length);

        // ── Categorize ALL divergences (don't stop at the first) by the digest field that diverged, so we
        //    can tell apart a per-pawn/singleton break (fatal — those are consumed every frame without
        //    dedup) from a raw-molotov re-resolution (the per-frame molotov list is deduped by the consume,
        //    so only the FIRST-seen ThrowerSlot per (index,serial) is golden-relevant). ──
        int pawnMismatchFrames = 0;
        int singletonMismatchFrames = 0;
        int molotovRawMismatchFrames = 0;
        int framesWithPawns = 0;
        int framesWithMolotovs = 0;
        Dictionary<(int Provider, int Slot), object?> seqSnapshot = [];
        Dictionary<(int Provider, int Slot), object?> parSnapshot = [];
        string? firstPawnOrSingleton = null;
        int firstPawnOrSingletonFrame = -1;
        string? firstMolotovRaw = null;
        int firstMolotovRawFrame = -1;
        for (int n = 0; n < frames.Count; n++)
        {
            EntityFrameDigest s = sequential[n];
            EntityFrameDigest p = parallel[n];
            if (s.PerPawn.Count > 0)
            {
                framesWithPawns++;
            }

            if (s.Molotovs.Count > 0)
            {
                framesWithMolotovs++;
            }

            // Per-pawn is compared as the FOLD, not row-for-row: the parallel path emits only changed
            // cells and each worker re-emits everything on its chunk's first frame, so the raw rows
            // legitimately differ. What must not differ is the pre-frame snapshot the consumer derives
            // from them, which is what every rule actually reads.
            Fold(seqSnapshot, s);
            Fold(parSnapshot, p);
            string? pawnSingle = DiffSnapshots(seqSnapshot, parSnapshot) ?? DiffSingletons(s, p);
            if (pawnSingle is not null)
            {
                pawnMismatchFrames++;
                if (DiffOnlySingleton(s, p))
                {
                    singletonMismatchFrames++;
                }

                firstPawnOrSingleton ??= pawnSingle;
                if (firstPawnOrSingletonFrame < 0)
                {
                    firstPawnOrSingletonFrame = n;
                }
            }

            string? molRaw = DiffMolotovsRaw(s, p);
            if (molRaw is not null)
            {
                molotovRawMismatchFrames++;
                firstMolotovRaw ??= molRaw;
                if (firstMolotovRawFrame < 0)
                {
                    firstMolotovRawFrame = n;
                }
            }
        }

        // ── Deduped molotov-event equivalence: replay the consume's dedup (first (index,serial) wins) over
        //    BOTH digest arrays and compare the resulting (creation-frame, slot) event stream. This is the
        //    value golden actually consumes (ConsumeMolotovs skips already-seen molotovs). ──
        List<(int Frame, int Index, int Serial, int Slot)> seqEvents = DedupMolotovEvents(sequential);
        List<(int Frame, int Index, int Serial, int Slot)> parEvents = DedupMolotovEvents(parallel);
        string? dedupDiff = DiffMolotovEventStreams(seqEvents, parEvents);

        Console.WriteLine($"compared {frames.Count:N0} frames " +
                          $"({framesWithPawns:N0} w/pawns, {framesWithMolotovs:N0} w/molotovs)");
        Console.WriteLine($"snapshot/singleton mismatch frames: {pawnMismatchFrames:N0} " +
                          $"(of which singleton-only: {singletonMismatchFrames:N0})");
        if (firstPawnOrSingleton is not null)
        {
            DemoFrame f = frames[firstPawnOrSingletonFrame];
            Console.WriteLine($"  first @ frame {firstPawnOrSingletonFrame} (tick {f.ServerTick}): {firstPawnOrSingleton}");
        }

        Console.WriteLine($"raw per-frame molotov-list mismatch frames: {molotovRawMismatchFrames:N0}");
        if (firstMolotovRaw is not null)
        {
            DemoFrame f = frames[firstMolotovRawFrame];
            Console.WriteLine($"  first @ frame {firstMolotovRawFrame} (tick {f.ServerTick}): {firstMolotovRaw}");
        }

        Console.WriteLine($"deduped molotov events: sequential={seqEvents.Count} parallel={parEvents.Count}  " +
                          $"event-stream equal: {dedupDiff is null}");
        if (dedupDiff is not null)
        {
            Console.WriteLine($"  DEDUP DIFF: {dedupDiff}");
        }

        // Strict element-wise digest equivalence: per-pawn + singleton + the raw per-frame molotov list all
        // match on every frame. (The dedup-aware molotov stream is computed too and is necessarily equal when
        // the raw list is; it's asserted as an explicit statement of the consume-relevant invariant and was
        // the lens that originally localized the instancebaseline checkpoint bug.) Since Step 1 proved the
        // sequential digest drives byte-identical golden, parallel == sequential ⟹ parallel → golden.
        await Assert.That(pawnMismatchFrames).IsEqualTo(0);
        await Assert.That(molotovRawMismatchFrames).IsEqualTo(0);
        await Assert.That(dedupDiff).IsNull();

        // Sanity: the comparison actually saw entity data (guards against a vacuous all-empty pass).
        await Assert.That(framesWithPawns).IsGreaterThan(0);
        await Assert.That(seqSnapshot.Count).IsGreaterThan(0)
            .Because("an empty fold would make the snapshot comparison pass while checking nothing");
        Console.WriteLine($"folded snapshot keys: sequential={seqSnapshot.Count} parallel={parSnapshot.Count}");
    }

    /// <summary>
    ///     The sequential reference path: drive ONE forward-only layer through every frame with the same
    ///     <c>SeekToTick</c> + <see cref="EntityDigestExtractor.Build" /> the scanner's
    ///     <c>BuildDigest</c> uses, capturing the digest at each frame.
    /// </summary>
    private static EntityFrameDigest[] BuildSequential(IReadOnlyList<DemoFrame> frames)
    {
        EntityStateLayer layer = new(frames);
        IReadOnlyList<IPerPlayerEntityValueProvider> perPlayer = NewPerPlayer();
        IReadOnlyList<IEntityValueProvider> singletons = NewSingletons();

        EntityFrameDigest[] digests = new EntityFrameDigest[frames.Count];
        for (int n = 0; n < frames.Count; n++)
        {
            layer.SeekToTick(frames[n].ServerTick);
            digests[n] = EntityDigestExtractor.Build(layer, perPlayer, singletons, true);
        }

        return digests;
    }

    /// <summary>
    ///     Folds one digest's per-pawn rows into a running last-value-per-(provider, slot) map, exactly as
    ///     <c>EntityChangeScanner.MergePreFrameSnapshot</c> does, including skipping nulls, which is what
    ///     makes an unchanged cell in a delta row a no-op rather than an erasure.
    /// </summary>
    private static void Fold(Dictionary<(int Provider, int Slot), object?> snapshot, EntityFrameDigest d)
    {
        foreach ((int slot, object?[] values) in d.PerPawn)
        {
            for (int p = 0; p < values.Length; p++)
            {
                if (values[p] is not null)
                {
                    snapshot[(p, slot)] = values[p];
                }
            }
        }
    }

    /// <summary>Returns null when the two folded snapshots hold identical values, else a description.</summary>
    private static string? DiffSnapshots(
        Dictionary<(int Provider, int Slot), object?> a,
        Dictionary<(int Provider, int Slot), object?> b)
    {
        if (a.Count != b.Count)
        {
            return $"snapshot-key-count {a.Count} vs {b.Count}";
        }

        foreach (((int provider, int slot), object? valueA) in a)
        {
            if (!b.TryGetValue((provider, slot), out object? valueB))
            {
                return $"snapshot missing slot {slot} provider[{provider}] in parallel";
            }

            if (!Equals(valueA, valueB))
            {
                return $"snapshot slot {slot} provider[{provider}] {Fmt(valueA)} vs {Fmt(valueB)}";
            }
        }

        return null;
    }

    /// <summary>
    ///     Returns null when the singleton portions of the two digests are identical, else a short
    ///     description. Singletons are consumed every frame with no dedup, so they still match exactly.
    /// </summary>
    private static string? DiffSingletons(EntityFrameDigest a, EntityFrameDigest b)
    {
        if (a.Singletons.Length != b.Singletons.Length)
        {
            return $"singleton-count {a.Singletons.Length} vs {b.Singletons.Length}";
        }

        for (int i = 0; i < a.Singletons.Length; i++)
        {
            if (!Equals(a.Singletons[i], b.Singletons[i]))
            {
                return $"singleton[{i}] {Fmt(a.Singletons[i])} vs {Fmt(b.Singletons[i])}";
            }
        }

        return null;
    }

    /// <summary>True when the digests diverge in singletons (used to attribute a mismatch to singletons).</summary>
    private static bool DiffOnlySingleton(EntityFrameDigest a, EntityFrameDigest b)
    {
        if (a.Singletons.Length != b.Singletons.Length)
        {
            return true;
        }

        for (int i = 0; i < a.Singletons.Length; i++)
        {
            if (!Equals(a.Singletons[i], b.Singletons[i]))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Returns null when the raw per-frame molotov lists are identical, else a short description.</summary>
    private static string? DiffMolotovsRaw(EntityFrameDigest a, EntityFrameDigest b)
    {
        if (a.Molotovs.Count != b.Molotovs.Count)
        {
            return $"molotov-count {a.Molotovs.Count} vs {b.Molotovs.Count}";
        }

        for (int i = 0; i < a.Molotovs.Count; i++)
        {
            if (!a.Molotovs[i].Equals(b.Molotovs[i]))
            {
                return $"molotov[{i}] {a.Molotovs[i]} vs {b.Molotovs[i]}";
            }
        }

        return null;
    }

    /// <summary>
    ///     Replays the consume's dedup over a digest array: the first frame each (index, serial) appears
    ///     produces one event carrying its ThrowerSlot AT THAT FRAME — exactly what
    ///     <c>EntityChangeScanner.ConsumeMolotovs</c> uses (it skips already-seen molotovs). Slot &lt; 0 is
    ///     retained here so the comparison still catches a divergence in which throw resolved or not.
    /// </summary>
    private static List<(int Frame, int Index, int Serial, int Slot)> DedupMolotovEvents(EntityFrameDigest[] digests)
    {
        HashSet<(int, int)> seen = [];
        List<(int Frame, int Index, int Serial, int Slot)> events = [];
        for (int n = 0; n < digests.Length; n++)
        {
            foreach ((int idx, int serial, int slot) in digests[n].Molotovs)
            {
                if (seen.Add((idx, serial)))
                {
                    events.Add((n, idx, serial, slot));
                }
            }
        }

        return events;
    }

    /// <summary>Returns null when the two deduped molotov-event streams are identical, else a description.</summary>
    private static string? DiffMolotovEventStreams(
        List<(int Frame, int Index, int Serial, int Slot)> a,
        List<(int Frame, int Index, int Serial, int Slot)> b)
    {
        if (a.Count != b.Count)
        {
            return $"event-count {a.Count} vs {b.Count}";
        }

        for (int i = 0; i < a.Count; i++)
        {
            if (!a[i].Equals(b[i]))
            {
                return $"event[{i}] {a[i]} vs {b[i]}";
            }
        }

        return null;
    }

    private static string Fmt(object? o) => o?.ToString() ?? "null";
}
