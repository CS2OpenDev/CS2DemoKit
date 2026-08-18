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
///         Builds the per-frame <see cref="EntityFrameDigest" /> array two ways — sequentially (drive one
///         <see cref="EntityStateLayer" /> with <c>SeekToTick</c> + <see cref="EntityDigestExtractor.Build" />
///         per frame, exactly what <c>EntityChangeScanner.BuildDigest</c> does) and in parallel
///         (<see cref="ParallelDigestProducer" />) — and asserts they are element-wise identical for every
///         frame/field. The digest seam already proved the sequential digest drives byte-identical golden output, so
///         <em>parallel == sequential ⟹ parallel → golden</em> by composition; a mismatch points at the
///         exact frame (hence chunk) and field that diverged.
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
    public async Task ParallelDigest_IsElementWiseIdenticalTo_SequentialDigest()
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

        // ── Sequential reference: one layer, SeekToTick + Build per frame (the scanner's mechanism). ──
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

            string? pawnSingle = DiffPawnAndSingleton(s, p);
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
        Console.WriteLine($"per-pawn/singleton mismatch frames: {pawnMismatchFrames:N0} " +
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
    ///     Returns null when the per-pawn AND singleton portions of the two digests are identical, else a
    ///     short first-divergence description. These feed the snapshot fold + singleton change-detection
    ///     every frame with no dedup, so they must match exactly.
    /// </summary>
    private static string? DiffPawnAndSingleton(EntityFrameDigest a, EntityFrameDigest b)
    {
        // Singletons.
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

        // Per-pawn (same order — ForEachLivePawn walks the occupied list ascending in both paths).
        if (a.PerPawn.Count != b.PerPawn.Count)
        {
            return $"perpawn-count {a.PerPawn.Count} vs {b.PerPawn.Count}";
        }

        for (int i = 0; i < a.PerPawn.Count; i++)
        {
            (int slotA, object?[] valsA) = a.PerPawn[i];
            (int slotB, object?[] valsB) = b.PerPawn[i];
            if (slotA != slotB)
            {
                return $"perpawn[{i}] slot {slotA} vs {slotB}";
            }

            if (valsA.Length != valsB.Length)
            {
                return $"perpawn[{i}] slot {slotA} value-count {valsA.Length} vs {valsB.Length}";
            }

            for (int v = 0; v < valsA.Length; v++)
            {
                if (!Equals(valsA[v], valsB[v]))
                {
                    return $"perpawn slot {slotA} provider[{v}] {Fmt(valsA[v])} vs {Fmt(valsB[v])}";
                }
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
