#region

using System.Numerics;
using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Analysis.Events;
using CS2DemoKit.Analysis.Plugins;
using CS2DemoKit.Parser;
using CS2DemoKit.Parser.GameEvents;
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
        string path = DemoTestHelper.RequireDemo(DemoTestHelper.SampleDemoFileName);
        ParsedDemo demo = DemoTestHelper.GetOrParse(path);
        IReadOnlyList<DemoFrame> frames = demo.Frames;

        Console.WriteLine($"Demo: {Path.GetFileName(path)}  frames={frames.Count:N0}");
        // Note: this test runs the PRODUCTION chunking, which coarsens to ~Environment.ProcessorCount
        // chunks — so its own layout is the runner's. Coarsening is correctness-neutral (intermediate full
        // packets decode via the normal PE-skipped SeekToTick path), which is what this assertion proves on
        // whatever layout the runner picks; ParallelDigest_FoldsToTheSameSnapshot_AtEveryChunkTarget below
        // is where the layout stops being the runner's and gets swept.
        IReadOnlyList<ParallelDigestProducer.Chunk> chunks =
            ParallelDigestProducer.PlanChunks(frames, out int schemaPrefixEnd);
        Console.WriteLine($"chunks={chunks.Count}  schemaPrefixEnd={schemaPrefixEnd}  " +
                          $"checkpoints={chunks.Count(c => c.CheckpointFrameIndex >= 0)}");

        // ── Sequential reference: one layer, SeekToTick + Build per frame, emitting the FULL per-frame
        //    readout (dedup off). That is the ground truth the fold below is judged against. ──
        EntityFrameDigest[] sequential = BuildSequential(frames, dedup: false);

        // ── Parallel under test. ──
        EntityFrameDigest[] parallel = ParallelDigestProducer.Produce(
            frames, NewPerPlayer, NewSingletons, true);

        // ── The scanner's own sequential arm: ONE delta stream over every frame, no chunk resets.
        //    That is what EntityChangeScanner.BuildDigest does on the fallback path, and it is a
        //    different shape from the parallel arm (which restarts its cell memory per chunk). ──
        EntityFrameDigest[] sequentialDelta = BuildSequential(frames, dedup: true);

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
        Dictionary<(int Provider, int Slot), object?> seqDeltaSnapshot = [];
        int seqDeltaMismatchFrames = 0;
        string? firstSeqDelta = null;
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

            if (s.Molotovs.Length > 0)
            {
                framesWithMolotovs++;
            }

            // Per-pawn is compared as the FOLD, not row-for-row: the parallel path emits only changed
            // cells and each worker re-emits everything on its chunk's first frame, so the raw rows
            // legitimately differ. What must not differ is the pre-frame snapshot the consumer derives
            // from them, which is what every rule actually reads.
            Fold(seqSnapshot, s);
            Fold(parSnapshot, p);

            // The scanner's arm, judged against the same ground truth.
            Fold(seqDeltaSnapshot, sequentialDelta[n]);
            if (DiffSnapshots(seqSnapshot, seqDeltaSnapshot) is { } sd)
            {
                seqDeltaMismatchFrames++;
                firstSeqDelta ??= $"frame {n}: {sd}";
            }

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

        // ── Deduped molotov-event equivalence: replay the consume's dedup (the first RESOLVED sighting of
        //    each (index,serial) wins) over BOTH digest arrays and compare the resulting (throw-frame, slot)
        //    event stream. This is the value golden actually consumes. ──
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
        Console.WriteLine($"sequential-delta snapshot mismatch frames: {seqDeltaMismatchFrames:N0}"
                          + (firstSeqDelta is null ? "" : $"  first: {firstSeqDelta}"));

        await Assert.That(pawnMismatchFrames).IsEqualTo(0);
        await Assert.That(seqDeltaMismatchFrames).IsEqualTo(0)
            .Because("one continuous delta stream is what EntityChangeScanner.BuildDigest produces on "
                     + "the sequential fallback, and it must fold to the same snapshot");
        await Assert.That(molotovRawMismatchFrames).IsEqualTo(0);
        await Assert.That(dedupDiff).IsNull();

        // Sanity: the comparison actually saw entity data (guards against a vacuous all-empty pass).
        await Assert.That(framesWithPawns).IsGreaterThan(0);
        await Assert.That(seqSnapshot.Count).IsGreaterThan(0)
            .Because("an empty fold would make the snapshot comparison pass while checking nothing");
        Console.WriteLine($"folded snapshot keys: sequential={seqSnapshot.Count} parallel={parSnapshot.Count}");
    }

    /// <summary>
    ///     The same equivalence at several chunk layouts, against one sequential reference. This is the
    ///     half of the gate the test above cannot give: a chunk boundary is where a worker primes from a
    ///     checkpoint and re-emits every live cell, so it is the one position a reconstruction bug can hide
    ///     at, and left to the planner's default the boundaries land wherever the runner's core count puts
    ///     them. A bug that only bites when a chunk starts near an entity lifecycle event — a pawn
    ///     spawning, a molotov's creation frame, a smoke's first billowing tick — would then be reachable
    ///     on a 32-core box and unreachable on a two-core one, or the reverse.
    ///     <para>
    ///         Sweeping the target moves every boundary: the planner strides through the clash-free full
    ///         packets, so a different target picks a different subset of them, not a subset of the same
    ///         one. The machine's own target is swept too, so whatever layout production would pick on this
    ///         runner is in the set rather than only the small ones.
    ///     </para>
    ///     <para>
    ///         Matches how <see cref="TriangleBvhParallelBuildTests" /> sweeps the degree of parallelism
    ///         over the other half of this PR's fan-out: there the partition is explicit arithmetic and the
    ///         gate varies it, and a decode whose partition is only ever the runner's is the weaker claim.
    ///     </para>
    /// </summary>
    [Test]
    public async Task ParallelDigest_FoldsToTheSameSnapshot_AtEveryChunkTarget()
    {
        string path = DemoTestHelper.RequireDemo(DemoTestHelper.SampleDemoFileName);
        ParsedDemo demo = DemoTestHelper.GetOrParse(path);
        IReadOnlyList<DemoFrame> frames = demo.Frames;
        Console.WriteLine($"Demo: {Path.GetFileName(path)}  frames={frames.Count:N0}");

        EntityFrameDigest[] sequential = BuildSequential(frames);
        List<(int Frame, int Index, int Serial, int Slot)> seqEvents = DedupMolotovEvents(sequential);

        Dictionary<(int Provider, int Slot), object?> reference = [];
        foreach (EntityFrameDigest d in sequential)
        {
            Fold(reference, d);
        }

        await Assert.That(reference.Count).IsGreaterThan(0)
            .Because("an empty fold would make every comparison below pass while checking nothing");

        HashSet<string> layouts = [];
        foreach (int target in (int[]) [1, 2, 3, 5, Environment.ProcessorCount])
        {
            IReadOnlyList<ParallelDigestProducer.Chunk> chunks =
                ParallelDigestProducer.PlanChunks(frames, out _, target);
            layouts.Add(string.Join(",", chunks.Select(c => c.Start)));

            EntityFrameDigest[] parallel = ParallelDigestProducer.Produce(
                frames, NewPerPlayer, NewSingletons, true, target);

            Dictionary<(int Provider, int Slot), object?> seqSnapshot = [];
            Dictionary<(int Provider, int Slot), object?> parSnapshot = [];
            int mismatchFrames = 0;
            string? firstMismatch = null;
            for (int n = 0; n < frames.Count; n++)
            {
                Fold(seqSnapshot, sequential[n]);
                Fold(parSnapshot, parallel[n]);
                string? diff = DiffSnapshots(seqSnapshot, parSnapshot)
                               ?? DiffSingletons(sequential[n], parallel[n])
                               ?? DiffMolotovsRaw(sequential[n], parallel[n]);
                if (diff is null)
                {
                    continue;
                }

                mismatchFrames++;
                firstMismatch ??= $"frame {n} (tick {frames[n].ServerTick}): {diff}";
            }

            string? dedupDiff = DiffMolotovEventStreams(seqEvents, DedupMolotovEvents(parallel));
            Console.WriteLine($"target={target,3}  chunks={chunks.Count,3}  mismatch frames={mismatchFrames:N0}  "
                              + $"molotov events equal: {dedupDiff is null}"
                              + (firstMismatch is null ? "" : $"  first: {firstMismatch}"));

            await Assert.That(parallel.Length).IsEqualTo(sequential.Length);
            await Assert.That(mismatchFrames).IsEqualTo(0)
                .Because($"a chunk target of {target} ({chunks.Count} chunks) moved the fold the consumer reads");
            await Assert.That(dedupDiff).IsNull()
                .Because($"a chunk target of {target} changed which molotov throw golden would consume");
        }

        await Assert.That(layouts.Count).IsGreaterThan(1)
            .Because("the targets must actually produce different chunk boundaries, or the sweep is one "
                     + "layout asserted several times");
    }

    /// <summary>
    ///     The active-smoke list is the one digest field with no delta encoding and no dedup: the
    ///     visibility transition scan reads it whole on every sampled tick, and an empty list does not
    ///     read as missing data, it reads as "no smoke was in the way". A parallel path that lost the
    ///     clouds would report every through-smoke sightline as a spot and nothing would complain.
    ///     Compared frame for frame, unlike per-pawn values, because smoke is absolute state at the
    ///     seeked tick rather than a delta.
    /// </summary>
    [Test]
    public async Task ParallelDigest_CarriesTheSameActiveSmokesAs_SequentialDigest()
    {
        string path = DemoTestHelper.RequireDemo(DemoTestHelper.SampleDemoFileName);
        ParsedDemo demo = DemoTestHelper.GetOrParse(path);
        IReadOnlyList<DemoFrame> frames = demo.Frames;

        EntityFrameDigest[] sequential = BuildSequential(frames, captureSmokes: true);
        EntityFrameDigest[] parallel = ParallelDigestProducer.Produce(
            frames, NewPerPlayer, NewSingletons, true, true);

        int framesWithSmoke = 0;
        int mismatchFrames = 0;
        string? firstMismatch = null;
        for (int n = 0; n < frames.Count; n++)
        {
            ReadOnlySpan<Vector4> s = sequential[n].Smokes;
            ReadOnlySpan<Vector4> p = parallel[n].Smokes;
            if (s.Length > 0)
            {
                framesWithSmoke++;
            }

            bool same = s.Length == p.Length;
            for (int i = 0; same && i < s.Length; i++)
            {
                same = s[i] == p[i];
            }

            if (!same)
            {
                mismatchFrames++;
                firstMismatch ??= $"frame {n}: sequential={s.Length} parallel={p.Length}";
            }
        }

        Console.WriteLine($"frames carrying active smoke: {framesWithSmoke:N0} / {frames.Count:N0}");
        await Assert.That(mismatchFrames).IsEqualTo(0).Because(firstMismatch ?? "");
        await Assert.That(framesWithSmoke).IsGreaterThan(0)
            .Because("a demo with no smoke at all would make this comparison pass while checking nothing");
    }

    /// <summary>
    ///     The dedup rule <see cref="DedupMolotovEvents" /> replays, stated directly against the scanner:
    ///     a molotov whose thrower has not resolved on the frame the projectile first appears must still
    ///     throw on the frame that does resolve it, and must throw exactly once after that.
    ///     <para>
    ///         <c>m_hThrower</c> is not reliably networked on the creation frame. A pawn killed on that
    ///         same frame reports the 24-bit invalid handle, which <c>PawnLookup.IndexOf</c> folds to -1
    ///         and the digest carries as <c>ThrowerSlot = -1</c>; the next frame the handle settles.
    ///         Recording the (index, serial) as seen on the unresolved sighting dropped that throw for the
    ///         rest of the run, and the only symptom was a <c>player_stats</c> <c>molotov_used</c> count
    ///         one short — no exception, no diagnostic, a plausible number.
    ///     </para>
    ///     <para>
    ///         Digests are hand-built rather than decoded: the deferred resolution needs a kill landing on
    ///         a projectile's creation frame, which no bundled demo is guaranteed to contain, and a gate
    ///         that only fires when the sample demo happens to have one is not a gate.
    ///     </para>
    /// </summary>
    [Test]
    [Category("Unit")]
    public async Task MolotovThrow_DefersUntilTheThrowerResolves_ThenEmitsOnce()
    {
        static EntityFrameDigest Frame(params (int Index, int Serial, int Slot)[] molotovs)
        {
            EntityFrameDigest d = new();
            foreach ((int index, int serial, int slot) in molotovs)
            {
                d.AddMolotov(index, serial, slot);
            }

            return d;
        }

        // (70,3) resolves one frame late; (71,1) never resolves; (72,2) resolves on its first frame.
        // All three stay alive to the end, so a dedup that failed to record an emission would re-throw.
        EntityFrameDigest?[] digests =
        [
            Frame((70, 3, -1), (71, 1, -1)),
            Frame((70, 3, 5), (71, 1, -1)),
            Frame((70, 3, 5), (71, 1, -1), (72, 2, 8)),
            Frame((70, 3, 5), (71, 1, -1), (72, 2, 8))
        ];

        EntityChangeScanner scanner = new(new EntityStateLayer([]), [], null, true);
        scanner.SetPrecomputedDigests(digests);

        List<(int Frame, int Tick, int Slot)> thrown = [];
        for (int n = 0; n < digests.Length; n++)
        {
            foreach (NetMessage msg in scanner.AdvanceAndPollAt(n, 100 + n))
            {
                if (msg is GameEventMessage { DecodedEvent: MolotovThrownEvent mt })
                {
                    thrown.Add((n, mt.GameTick, mt.PlayerSlot));
                }
            }
        }

        Console.WriteLine($"molotov_thrown: {string.Join(", ", thrown)}");

        await Assert.That(thrown).IsEquivalentTo(
            new List<(int Frame, int Tick, int Slot)> { (1, 101, 5), (2, 102, 8) })
            .Because("the late-resolving throw belongs to frame 1 (where the slot first resolved), the "
                     + "immediate one to frame 2, and the projectile whose thrower never resolves throws "
                     + "nothing; deduping on first sighting instead of on emission loses the first of these");
    }

    /// <summary>
    ///     The sequential reference path: drive ONE forward-only layer through every frame with the same
    ///     <c>SeekToTick</c> + <see cref="EntityDigestExtractor.Build" /> the scanner's
    ///     <c>BuildDigest</c> uses, capturing the digest at each frame.
    /// </summary>
    private static EntityFrameDigest[] BuildSequential(
        IReadOnlyList<DemoFrame> frames, bool dedup = false, bool captureSmokes = false)
    {
        EntityStateLayer layer = new(frames);
        IReadOnlyList<IPerPlayerEntityValueProvider> perPlayer = NewPerPlayer();
        IReadOnlyList<IEntityValueProvider> singletons = NewSingletons();
        PerPawnDeltaState delta = new(DigestColumnLayout.For(perPlayer), dedup);

        EntityFrameDigest[] digests = new EntityFrameDigest[frames.Count];
        for (int n = 0; n < frames.Count; n++)
        {
            layer.SeekToTick(frames[n].ServerTick);
            digests[n] = EntityDigestExtractor.Build(layer, delta, singletons, true, captureSmokes);
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
        PerPawnColumns rows = d.PerPawn;
        for (int r = 0; r < rows.Count; r++)
        {
            int slot = rows.SlotAt(r);
            for (int p = 0; p < rows.Layout.Count; p++)
            {
                if (rows.IsPresent(r, p))
                {
                    snapshot[(p, slot)] = rows.GetBoxed(r, p);
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
        if (a.Molotovs.Length != b.Molotovs.Length)
        {
            return $"molotov-count {a.Molotovs.Length} vs {b.Molotovs.Length}";
        }

        for (int i = 0; i < a.Molotovs.Length; i++)
        {
            if (!a.Molotovs[i].Equals(b.Molotovs[i]))
            {
                return $"molotov[{i}] {a.Molotovs[i]} vs {b.Molotovs[i]}";
            }
        }

        return null;
    }

    /// <summary>
    ///     Replays the consume's dedup over a digest array: each (index, serial) produces one event on
    ///     the first frame it appears WITH A RESOLVED THROWER, carrying that slot — exactly what
    ///     <c>EntityChangeScanner.ConsumeMolotovs</c> emits. The dedup is keyed on emission, not on
    ///     first sighting, so a projectile seen before its <c>m_hThrower</c> has settled is not
    ///     consumed by that sighting and still throws on the frame that resolves it; one that never
    ///     resolves produces no event at all, which is what golden sees.
    ///     <para>
    ///         Dropping the unresolved sightings here costs the comparison nothing: a divergence in
    ///         WHICH frame resolved a throw is already caught frame for frame by
    ///         <see cref="DiffMolotovsRaw" />, which compares the raw per-frame lists including the
    ///         slot, and it shows up here too as a moved throw-frame.
    ///     </para>
    /// </summary>
    private static List<(int Frame, int Index, int Serial, int Slot)> DedupMolotovEvents(EntityFrameDigest[] digests)
    {
        HashSet<(int, int)> seen = [];
        List<(int Frame, int Index, int Serial, int Slot)> events = [];
        for (int n = 0; n < digests.Length; n++)
        {
            foreach ((int idx, int serial, int slot) in digests[n].Molotovs)
            {
                if (slot >= 0 && seen.Add((idx, serial)))
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
