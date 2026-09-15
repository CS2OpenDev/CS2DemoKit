# Load-pipeline baseline

Reference point for demo-load performance. Re-measure against this before claiming a change
helped, and update it when one lands.

    commit    ecfc03d (merge of #25)
    runs      50 (10 rounds x 5 demos), zero failures, 919 s
    machine   10 cores (6P + 4E), .NET 10, Release, workstation GC
    corpus    5 full-match demos, 288-294 MB, 121k-149k frames
    load      1.44 to 3.59 throughout

Raw rows in `baseline-ecfc03d.csv`. Reproduce with
`dotnet run --project tools/CS2DemoKit.Bench -c Release -- sweep`.
Medians below, because one thermal or scheduling excursion moves a mean and leaves a median alone.

## Where the time goes

| phase | median | share |
|---|---|---|
| parse | 383.5 ms | 21.2% |
| build | 21.4 ms | 1.2% |
| **evaluate** | **1398.5 ms** | **77.2%** |
| total | 1813 ms | |

**Evaluation is the only phase with real headroom left.** Parse was 34% of load before #25 and
is 21% now; its remaining cost is spread thin (see the pass split below) and the one structural
lever there has been exhausted.

## Full table

| metric | median | mean | sd | min | max |
|---|---|---|---|---|---|
| parse | 383.5 | 388.4 | 14.5 | 368.0 | 421.3 |
| pass 1, header scan | 14.8 | 15.4 | 1.2 | 14.3 | 17.8 |
| pass 2, decompress + proto | 333.0 | 336.0 | 11.7 | 318.8 | 366.6 |
| pass 3, enrich | 36.5 | 36.8 | 3.1 | 31.8 | 43.8 |
| parse GC pause | 187.2 | 189.6 | 10.5 | 174.0 | 215.1 |
| parse allocation (MB) | 794.5 | 800.6 | 12.6 | 782.9 | 819.6 |
| parse gen0 / gen1 / gen2 | 76 / 20 / 1 | | | | |
| retained by demo (MB) | 602.0 | 608.0 | 25.5 | 573.7 | 655.2 |
| build | 21.4 | 21.5 | 0.5 | 20.6 | 22.8 |
| evaluate | 1398.5 | 1424.8 | 101.7 | 1272.3 | 1655.7 |
| evaluate GC pause | 425.8 | 433.0 | 20.9 | 406.4 | 490.1 |
| evaluate allocation (MB) | 629.7 | 637.3 | 19.0 | 618.1 | 672.9 |
| evaluate gen0 / gen1 / gen2 | 81 / 29 / 1 | | | | |
| full `InnerMessages` walk | 95.9 | 95.4 | 6.4 | 84.7 | 118.4 |
| walk allocation (MB) | 118.0 | 121.6 | 5.9 | 116.3 | 132.1 |

Per demo:

| demo | parse | evaluate | retained | frames |
|---|---|---|---|---|
| ...0265700985_129 | 385 ms | 1368 ms | 594 MB | 123,283 |
| ...1685184808_389 | 379 ms | 1337 ms | 577 MB | 123,467 |
| ...1163782782_410 | 378 ms | 1579 ms | 602 MB | 121,233 |
| ...1857131197_392 | 413 ms | 1372 ms | 652 MB | 149,109 |
| ...2122969707_408 | 386 ms | 1418 ms | 614 MB | 130,670 |

## The collector is a third of the pipeline

    parse      383 ms, of which 187 ms (49%) is GC pause
    evaluate  1399 ms, of which 426 ms (30%) is GC pause

613 ms of the 1813 ms total is the collector, under the GC mode a consumer gets by default. The
parse half of that is what #25 already halved; the evaluate half is untouched.

Evaluation allocates 630 MB and drives 81 gen0 and 29 gen1 collections. Whether those are
survivors or transient garbage is the open question, and it is the one worth answering first:
in parse the cost turned out to be survivorship rather than volume, and the fix followed from
that rather than from anything about decode speed.

Answered below: survivors, and the same shape as parse.

## Per-pawn digest deltas

The entity digest precompute was ~55% of evaluation and ~40% of load. It built a full per-pawn
readout for every frame and held the whole stream until evaluation consumed it. On a 123k-frame
demo that is 2.7M boxed cells, of which 2874 differ from the previous frame. The consumer folds
them into a last-value-per-(provider, slot) map, so the other 99.9% were writing a key the value
it already held. Carrying only changed cells reproduces the fold exactly.

Evaluation is 72 to 81% of load across the wide corpus, so it is still where the headroom is.

Interleaved A/B, same tree with only the three library files reverted on the before arm, so both
arms run an identical harness. 3 rounds x 15 demos x 2 variants, 90 runs, zero failures, 1618 s.
Raw rows in `digest-delta-wide.csv`. Order flips each round so drift hits both arms.

The corpus is deliberately wider than the 5 above, which all sat within 6 MB of each other at the
library's median size and so could not have detected a result that only holds at one scale. These
15 span 40 to 524 MB and 36k to 229k frames, across all 13 maps in the local library.

Figures are the best of 3 per demo per arm. The noise here is one-sided: a collection landing
inside a timed window adds several hundred ms and nothing removes time, so the minimum is the
least contaminated estimate. Medians over 3 are not enough on a bimodal distribution, which is
what produced a spurious +44.9% "regression" on map 405 in the first pass of this sweep; ten runs
per arm on that demo put it at -20.2%, with both arms bimodal and the low mode clearly separated
(`digest-delta-405-bimodal.csv`).

| metric | median change | range | demos improved |
|---|---|---|---|
| **evaluate** | **-22.1%** | -11.6% to -33.4% | 15/15 |
| evaluate allocation | -13.0% | -4.3% to -14.5% | 15/15 |
| **total load** | **-17.2%** | -6.2% to -26.3% | 15/15 |
| parse (control) | +0.1% | | |
| retained (control) | +0.3% | | |

An earlier version of this section reported -31.2% evaluate and -24.9% load. That was measured on
the 5-demo corpus alone, which happened to hold three demos in the high-gain group. The wider
corpus is the number to trust.

Allocation is the hardest of these: it is a counter rather than a wall clock, so it barely moves
between runs. It also carries the mechanism. The saving scales with the retained digest stream, so
the 36k-frame demo gains 4.3% while the 229k-frame demos gain 14.2%. A win that did not scale that
way would not be the win this change claims to be.

Parse and retained are the controls, and both sit on zero at the median. Two per-demo outliers are
worth naming rather than hiding: parse drifts +23.5% on the smallest demo, which is 40 ms on a
180 ms phase, and retained drifts +53.3% on map 407, whose before arm is bimodal at
[123, 123, 192] MB against a steady 190 MB after. Retained is captured after parse and before
evaluation runs at all, so a change living entirely inside evaluation cannot move it; that column
is measurement instability by construction.

This is a collector win, not a decode win: every provider is still read for every live pawn on
every frame. It also depends on the shipped providers changing rarely.

The position providers added for #5 are the case where that stops holding, and they are now
measured rather than predicted. Counting cells the digest emits on the 123,283-frame demo:

| provider set | cells emitted |
|---|---|
| the six shipped providers | 14,455 |
| plus `entity.pawn.pos_z` | 379,576 |
| plus all three axes | 1,442,280 |

One axis is 26x, three are 100x. Providers are gated in by name, so a ruleset that reads no axis
pays none of it and the figures above stand; a ruleset that reads one should have this phase
re-measured against it rather than against this baseline.

Still open here: the precompute gives each chunk worker its own `EntityStateLayer`, so worker
count and retained entity state are coupled. Measured at ~26 MB and ~25 ms of pause per extra
worker, with pause rising monotonically in chunk count while wall time traces a U. Six chunks beat
the shipped `Environment.ProcessorCount` on a 6P+4E machine, but the wall-time signal was inside
the run-to-run spread at two runs per point, so it is untouched and wants its own sweep.

Ruled out on the way, both cheaply: worker load imbalance (max/median chunk wall time 1.02-1.08,
so the P/E-core asymmetry is not biting) and the serial layer bootstrap (0.8 ms).

Ruled out already: snapshot capture. `AnalysisOptions.CaptureSnapshots` defaults to on, and
turning it off does not make evaluation faster.

## Eight-wide BVH build

The tree the visibility engine builds per map changed twice at once: binary median split became
eight-wide, and the builder gained a parallel path. Those are two wins, they multiply, and at a
machine's core count either one can be dressed up as the whole of it. They are measured apart here.

    machine   AMD Ryzen 9 7950X3D, 16 cores / 32 logical, 31 GB, Windows 11, otherwise idle
    runtime   .NET 10, Release, workstation GC, Environment.ProcessorCount = 32
    tree      working tree at 936b83e, branch feature/aim-rating-providers
    bakes     <map>/collision.tris from the app checkout's assets directory
    runs      1 cold build then 9 warm rounds per arm, arms interleaved, order flipped per round

Raw output in `bvh-build-7950x3d.txt`. Reproduce with

    dotnet run --project tools/CS2DemoKit.Bench -c Release -- \
        rays <bake> --rays 100000 --rounds 9 --threads 32

**This is not the machine the tables above were taken on** (that one is 10 cores, 6P + 4E). Nothing
here is comparable with anything above it, and the ratios are the only figures that travel at all:
the parallelism column is a function of the core count and will read lower on a smaller machine.

### Wall-clock

The binary builder is serial by construction and takes no thread count, so only the serial
eight-wide column compares with it like for like. That column, and only that column, is the
topology. Medians of the nine warm rounds.

| bake | triangles | binary, 1 thread | wide, 1 thread | topology | wide, 32 threads | parallelism | product |
|---|---|---|---|---|---|---|---|
| de_nuke | 191,628 | 143.5 ms | 121.9 ms | 1.18x | 27.0 ms | 4.52x | 5.32x |
| de_dust2 | 435,649 | 391.9 ms | 337.2 ms | 1.16x | 50.7 ms | 6.64x | 7.72x |
| de_overpass | 713,341 | 784.4 ms | 708.2 ms | 1.11x | 88.9 ms | 7.97x | 8.82x |
| de_ancient | 967,742 | 1137.8 ms | 1072.9 ms | 1.06x | 126.6 ms | 8.47x | 8.99x |

**The topology is worth 1.06 to 1.18x, and shrinks as the bake grows; the rest is the thread
count.** The product column is what a serial run of the old builder against a default run of the new
one reports, and it is the number to avoid quoting: it charges the topology with the parallel
build's win, and it moves with the core count of whoever ran it.

Cold builds are one sample each and are not summarised into a ratio above. They land anywhere from
0.74x to 1.66x their arm's warm median: above it on the smallest bake, where the builder's own JIT
tier-up dominates, and consistently below it on the two largest, which one sample per arm cannot
explain. Read them from the raw output, not from here.

Nine rounds is a floor, not a luxury. At five rounds the de_overpass binary arm read
674/1300/1701/1257/1425 ms and put the topology at 1.72x and the product at 12.95x; at nine rounds
it settles to 1.11x and 8.82x with every round inside 670 to 801 ms. A single wall-clock sample per
arm, which is what this verb used to take, cannot separate a 1.1x effect from that.

### Memory

`build --live` samples the live set during the build, forcing a compacting collection before each
sample, and reports the high-water mark over a baseline read the same way. The bake's triangle soup
is resident when the baseline is taken, so it is in neither arm's figure. Both arms serial, three
rounds, repeatable to 0.1 MB.

| bake | binary live peak | wide live peak | binary allocated | wide allocated |
|---|---|---|---|---|
| de_nuke | 19.0 MB | 14.9 MB | 37.0 MB | 27.8 MB |
| de_dust2 | 43.2 MB | 33.8 MB | 79.6 MB | 64.7 MB |
| de_overpass | 70.8 MB | 55.3 MB | 143.2 MB | 105.9 MB |
| de_ancient | 96.0 MB | 75.4 MB | 169.1 MB | 146.3 MB |

On de_ancient that is 96.0 to 75.4 MB, a 20.6 MB saving, 1.27x. The eight-wide arm's live peak sits
within 1.7 MB of its retained size on every bake and lands exactly on it for de_ancient, which is
the segment pooling doing what it claims: the builder never holds much more than the finished tree
at once. The binary builder peaks at 2.8 to 3.5x its retained size, in intermediates it allocates
and drops.

**Retained is not comparable between the arms and is not a win here.** The binary tree holds the
caller's triangle soup by reference; the eight-wide tree copies what it needs and lets the soup go.
So on de_ancient the binary arm's 27.4 MB excludes 34.8 MB the tree still depends on and the
eight-wide arm's 75.4 MB excludes nothing. Counting the soup on both sides, the finished trees are
62.2 MB and 75.4 MB and the eight-wide one is the larger object. The saving is in the builder's
transient peak, not in what it leaves behind.

### The instrument, checked

**Large object heap compaction.** Every live-set reading here (the baseline, the retained size and
each `--live` sample) asks for `GCLargeObjectHeapCompactionMode.CompactOnce` first, because both
builders work in large arrays and a gen2 leaves the large object heap uncompacted by default. It
turned out to change nothing on this machine: with the compaction removed, de_ancient reads 96.0 and
75.4 MB, identical to the figures above. It stays because without it the difference between two arms
with different large-object habits is part fragmentation and part live set, with no way to size the
shares, and that cannot be settled after the fact from a figure already taken.

**The sampler's own cost.** `--live` holds a core and forces a blocking collection per sample, which
suspends a parallel build's workers, so a parallel arm read this way is not comparable with a serial
one. Measured rather than assumed: the de_ancient eight-wide peak reads 76.9 to 77.0 MB on 32
threads against 75.4 MB serial, so the perturbation is about 1.5 MB, and the memory table above is
taken at `--threads 1` on both arms regardless.

**Tree identity across thread counts.** The structural digest is the same at 1 thread and at 32 on
all four bakes (de_ancient `43AF9BBF399B09F9`, de_overpass `652353CE415B180B`, de_dust2
`17A84C526631ED31`, de_nuke `54882E14EFAA64DF`), so the parallel column is the same tree built
faster and not a different tree.

### Figures that do not reproduce

A set of build figures circulated before this section existed: 267 to 45 ms on de_nuke through 921
to 117 ms on de_ancient, and a 182.4 to 75.4 MB live peak on de_ancient. The wall-clock pairs are
the product column above, not the topology, and their ratios (5.9x to 7.9x) fall in the same range
as this machine's product column, 5.3x to 9.0x. The 75.4 MB reproduces exactly, on the nose, at
`--threads 1`. The 182.4 MB does not reproduce at any thread
count here: the binary builder's live peak on de_ancient is 96.0 MB and its total allocation 169.1
MB, and no reading of either builder from this tool lands on 182.4.

## Paths: the forward reader against the retained parse

    commit    0a617f9
    runs      105 (1 round x 15 demos x 7 arms), zero failures, zero digest mismatches, 437 s
    machine   10 cores (6P + 4E), .NET 10, Release, workstation GC
    corpus    15 full-match demos, 40 to 524 MB, 36k to 229k frames

Raw rows in `paths-0a617f9.csv`. Reproduce with
`dotnet run --project tools/CS2DemoKit.Bench -c Release -- paths --rounds 1`.

Each arm is one way of consuming a demo, run in its own process with a sampler thread that
records the heap (`GC.GetTotalMemory(false)`, live data plus garbage not yet collected) and the
working set every 20 ms. The two replay arms and the two scoreboard arms fold what they produce
into a digest and the orchestrator drops both rows of a pair that disagrees; on this corpus no
pair did. Wall-clock includes JIT, because a warm-up pass would leave its heap committed and the
sampler would report that instead of the arm, so the times compare arms with each other and
with demo size, not with the load-pipeline table above.

Peak heap in MB against demo size, the smallest and largest demos and three between:

| demo | size | frames | message-scan | game-events | entity-replay | materialised-replay | scoreboard-stream | scoreboard-materialised |
|---|---|---|---|---|---|---|---|---|
| ...1164257366_406 | 40 MB | 35,731 | 8.8 | 8.0 | 38.6 | 217.1 | 50.0 | 438.0 |
| ...0748090338_404 | 204 MB | 91,566 | 10.5 | 10.6 | 43.5 | 778.3 | 59.1 | 910.2 |
| ...1163782782_410 | 280 MB | 121,233 | 10.9 | 11.6 | 68.5 | 1009.5 | 80.7 | 1180.3 |
| ...0449092279_123 | 432 MB | 196,844 | 11.2 | 11.0 | 49.9 | 1336.4 | 64.7 | 1622.6 |
| ...1522348072_129 | 524 MB | 228,902 | 11.1 | 11.1 | 81.1 | 1704.0 | 101.2 | 2036.9 |

**The reader's arms are flat.** Across a 13x range of demo size the structure scan and the
event scan sit between 8 and 12 MB of heap, the tracker walk between 39 and 81 MB, and the
shipped rulesets between 50 and 101 MB. What growth there is follows the match rather than the
file: the tracker's live entity set and the per-player state the rules keep are larger for a
longer match with more players seen. The retained parse grows with the file on every arm, 3.1
to 5.4x the demo's size in heap for the materialised replay (the decoded frames of the whole
demo) and 3.8 to 11x for the scoreboard with snapshots on, which adds one row per dispatched
message on top.

**Working set is the mapping plus the heap.** The reader arms memory-map the file and touch
every page, so their working set is the file size plus a near-constant amount: about 60 MB for
the structure scan, 125 to 160 MB for the stream scoreboard. Subtract `size_mb` from
`peak_working_set_mb` to see the process's own. `peak_private_mb` reads zero on macOS and is
only informative elsewhere.

**Wall-clock is where the stream still pays.** On this corpus the stream scoreboard takes 1.3
to 2.0x the materialised one: the reader decodes one frame at a time on the reading thread,
the entity digests are produced sequentially in step with the evaluator, and the dialect probe
is one more pass over the file. The tracker walk off the reader, by contrast, runs in 0.75 to
1.0x the materialised walk's time, because the entity-replay plan never decodes what the
tracker does not read. The stream scoreboard's gap is the cost the windowed read-ahead and the
pipelined digest producer exist to take back; re-run `paths` after them and put the two
scoreboard columns side by side.

### After the pipelined digest producer

    runs      60 sampled + 30 live (1 round x 15 demos), zero failures, zero digest mismatches
    rows      paths-pipelined.csv, paths-pipelined-live.csv (CS2DEMOKIT_PATHS_LIVE=1)

The stream scoreboard now folds entity digests two chunks ahead of the loop on pooled trackers
and releases a frame's entity and string-table payloads once folded. Same five demos, the
stream arm before and after, the retained parse for scale:

| demo | size | stream wall before | stream wall after | materialised wall | stream live peak | stream sampled peak | one tracker |
|---|---|---|---|---|---|---|---|
| ...1164257366_406 | 40 MB | 2.1 s | 1.3 s | 1.3 s | 98 MB | 155 MB | 12.3 MB |
| ...0748090338_404 | 204 MB | 3.7 s | 3.5 s | 2.9 s | 132 MB | 247 MB | 12.6 MB |
| ...1163782782_410 | 280 MB | 4.8 s | 4.2 s | 3.7 s | 144 MB | 301 MB | 17.2 MB |
| ...0449092279_123 | 432 MB | 5.2 s | 4.5 s | 3.6 s | 135 MB | 296 MB | 12.9 MB |
| ...1522348072_129 | 524 MB | 7.8 s | 5.7 s | 4.5 s | 164 MB | 377 MB | 16.2 MB |

**Wall-clock.** Across the corpus the stream scoreboard runs at 1.0 to 1.35x the materialised
one, down from 1.3 to 2.0x. What remains is serial: the reader decodes one frame at a time and
the evaluator dispatches one message at a time, so a third and fourth worker were measured to
buy a few percent more for about 35 MB each and the default stays at two.
`AnalysisOptions.MaxDegreeOfParallelism` raises it; one selects the sequential producer.

**Memory, two figures.** The sampled peak (`GC.GetTotalMemory(false)` every 20 ms) is live
data plus garbage the collector has not got to yet, and with two workers allocating in
parallel the garbage is most of it: 155 to 377 MB against a live peak of 98 to 164 MB. The live
column is the one to hold against demo size, and it holds: a 13x larger file costs 1.7x the
live heap, which is the longer match's larger entity set and more players seen, not the file.
What a worker costs is the `tracker-prime` arm: one curated tracker primed from the signon
prefix and a checkpoint holds 11 to 19 MB, mostly its own copy of the parsed schema, which is
why the trackers are pooled and re-primed rather than built per chunk, and why sharing one
parsed schema between trackers is the next thing worth taking from this number.

**The window is a reader feature, not a run feature.** `ParseOptions.ReadAheadFrames` makes the
event scan about 2.7x faster on a full match (0.70 s to 0.26 s on the 280 MB demo, a decode-bound
read) and does nothing for the entity walk or the scoreboard, which are bound by the fold. So
`DemoAnalysis.Run` leaves it off, and the reader arms above were taken without it.

### After the probe stop, the reader thread and the tuned defaults

    runs      120 sampled + 15 live (1 round x 15 demos), zero failures, zero digest mismatches
    rows      paths-tuned.csv, paths-tuned-live.csv (CS2DEMOKIT_PATHS_LIVE=1)

Three changes since the table above, in the order the numbers asked for them. The dialect
probe stops once the first round has decided the dialect instead of reading the whole file,
which was 0.7 s on a full match. The stream is read and decoded on its own thread into a
bounded queue of chunks, so decoding overlaps the evaluator's dispatch instead of taking turns
with it. And with the decode now the thread that bounds a run, `DemoAnalysis.Run` gives the
reader a 1024-frame window and the producer three workers, the point at which the fold
disappears behind the read and the dispatch on this machine. Same five demos:

| demo | size | retained wall | forward wall | forward / retained | retained peak heap | forward live peak | forward sampled peak |
|---|---|---|---|---|---|---|---|
| ...1164257366_406 | 40 MB | 1.1 s | 0.9 s | 0.85x | 437 MB | 154 MB | 244 MB |
| ...0748090338_404 | 204 MB | 2.6 s | 2.6 s | 1.02x | 911 MB | 202 MB | 346 MB |
| ...1163782782_410 | 280 MB | 3.2 s | 2.7 s | 0.83x | 1179 MB | 231 MB | 486 MB |
| ...0449092279_123 | 432 MB | 3.1 s | 4.0 s | 1.28x | 1621 MB | 222 MB | 484 MB |
| ...1522348072_129 | 524 MB | 4.0 s | 4.2 s | 1.05x | 1975 MB | 274 MB | 485 MB |

**Wall-clock.** Over the corpus the forward path now takes a median 0.88x of the retained
parse's time, from 0.69x to 1.28x on single runs, where it started this series at 1.3 to 2.0x.
Its floor is the larger of decode and dispatch rather than their sum, and the fold is hidden
behind both. The two demos it still loses on are single-round outliers of the kind the
`load1` column exists to explain; re-measure before reading anything into them.

**Memory.** The forward path's live peak is 154 to 274 MB across the 13x range of file size,
against 437 to 1975 MB retained. The rise from the previous section's 98 to 164 MB is the third
worker (one tracker and one chunk, about 20 MB) and the decode window with its partitions
(about 40 MB), which is what the wall-clock above was bought with. `MaxDegreeOfParallelism`
lowers it: one worker is the sequential producer at about 80 MB live on the same demo, two
workers without a window about 150 MB.

**What the sweet spot looks like.** Each worker beyond the count that hides the fold costs
memory and returns nothing, and the window helps only when the read is on its own thread.
On another machine the count moves with the core count; the knobs are the option and, for a
caller building its own `DemoReader`, `ParseOptions.ReadAheadFrames`.

### After the allocation pass

    runs      30 sampled + 30 live (1 round x 15 demos x 2 arms), zero failures, zero digest mismatches
    rows      paths-alloc.csv, paths-alloc-live.csv (CS2DEMOKIT_PATHS_LIVE=1)

Six commits driven by an allocation-by-type profile of the forward run
(`CS2DEMOKIT_PATHS_ALLOCTICK=1` on a `path-measure` child prints the top types from the
runtime's allocation-tick events), each gated on identical digests across paths, the goldens
and the corpus parity arm. The 280 MB demo (...410), forward run, allocation after each step:

| step | commit | allocated | what went |
|---|---|---|---|
| tuned defaults (the section above) | d0fb392 | 799 MB | |
| zero-copy payload reads, fallback store off, one schema shared across trackers | b662eb2 | 452 MB | a byte[] copy per entity and string-table message, a dictionary entry and a box per unlensed field, two of three schema parses |
| typed Vector3 lane | 24e15d2 | 384 MB | 77 MB of boxed vectors and angles |
| pawn slot index | 55ebd7b | 364 MB | a walk over every live entity per frame |
| typed ulong lane, handles typed, enums to int | 383d999 | 349 MB | 23 MB of boxed ulong |
| array element cache grows from eight | cf90c98 | 330 MB | 24 MB of 1024-slot descriptor arrays, held for the run |

What is left is the parser's: 125 MB of byte[] is protobuf's copy of every message payload,
15 MB of `CSVCMsg_PacketEntities` and 12 MB of `ByteString` are the messages, 8 MB of
`SnappyDecompressor` is one per call, 8 MB of strings are decoded and dropped. The ring buffer
for entity bytes in the refinement is what would move the byte[] figure; it is on hold.

Same five demos as above, single runs, the default collector:

| demo | size | retained wall | forward wall | forward / retained | retained peak heap | forward live peak | forward sampled peak |
|---|---|---|---|---|---|---|---|
| ...1164257366_406 | 40 MB | 0.9 s | 0.8 s | 0.84x | 271 MB | 91 MB | 144 MB |
| ...0748090338_404 | 204 MB | 2.2 s | 1.8 s | 0.85x | 906 MB | 115 MB | 186 MB |
| ...1163782782_410 | 280 MB | 2.5 s | 1.9 s | 0.77x | 1028 MB | 127 MB | 239 MB |
| ...0449092279_123 | 432 MB | 3.0 s | 2.3 s | 0.78x | 1536 MB | 129 MB | 203 MB |
| ...1522348072_129 | 524 MB | 3.3 s | 4.0 s | 1.20x | 1791 MB | 150 MB | 262 MB |

**Wall-clock.** A median 0.89x of the retained parse over the corpus, 0.66x to 1.27x on single
runs. The retained parse shares the tracker and got faster too, a median 0.86x of its own time
in the previous section, so the ratio held while both moved. The spread is the collector: the
same demo measured 3.2 s and 1.8 s on consecutive single runs under the default settings, which
is the bimodality the closing section warns about and what the next section is for.

**Memory.** Live peak 91 to 150 MB across the corpus, from 154 to 274 MB in the previous
section, against 271 to 1791 MB retained. A forward run allocates 124 to 551 MB in total; the
retained parse 221 to 1706 MB.

### GC configuration

    runs      15 demos x 1 round x scoreboard-stream per configuration
    rows      paths-gc-default.csv, paths-gc-nonconc.csv, paths-gc-gen0-64m.csv, paths-gc-nonconc-gen0.csv, paths-gc-batch.csv, paths-gc-batch-gen0.csv

Measured last on purpose: a collector setting flatters whichever allocation profile it is
measured against, so it waited until the profile stopped moving. Workstation GC throughout (the
bench sets `DOTNET_gcServer=0`). Totals over the 15 demos, forward run, one round each:

| configuration | set by | total wall | total pause | gen0 per run, 40 MB to 524 MB demo | faster than the default |
|---|---|---|---|---|---|
| default: concurrent workstation | | 35.2 s | 3.26 s | 16 to 74 | |
| non-concurrent | `DOTNET_gcConcurrent=0` at startup | 29.7 s | 1.07 s | 4 to 9 | 11 of 15 |
| gen0 budget 64 MB | `DOTNET_GCgen0size=4000000` at startup | 29.2 s | 1.48 s | 3 to 11 | 12 of 15 |
| both | | 26.7 s | 1.05 s | 3 to 7 | 15 of 15 |
| `GCLatencyMode.Batch` set by the engine for the run | `GCSettings.LatencyMode` at run time | 36.7 s | 3.14 s | 12 to 63 | 6 of 15 |
| the same plus the gen0 budget | | 35.0 s | 1.24 s | 3 to 9 | 7 of 15 |

Why the startup knobs work: concurrent workstation GC caps the gen0 budget at a few MB so that
its background collections stay short, and a forward run allocates 100 to 500 MB of short-lived
frames, so that is a gen0 collection every few frames, each suspending the reader thread and
three digest workers. Non-concurrent GC lifts the cap and drops the background collections; the
gen0 knob lifts the cap on its own.

**Decision.** The engine leaves the collector alone. The configuration that wins everywhere is
two startup settings, and a library cannot apply either: the runtime reads `gcConcurrent` and
`GCgen0size` before any managed code runs, and the one collector knob that can be flipped at run
time, `GCSettings.LatencyMode`, was measured in both combinations above and is a wash: on its
own it stops the background collections but keeps the small gen0 budget, so the collections stay
as frequent and become blocking, and with the budget lifted alongside it it still measured no
better than the default, which the numbers do not explain. A host that runs the forward path as
a batch job sets `DOTNET_gcConcurrent=0`
or `<ConcurrentGarbageCollection>false</ConcurrentGarbageCollection>` in its project, and
`DOTNET_GCgen0size=4000000` (hex bytes, 64 MB) in its environment, and gets the 24%. A host with
a UI thread keeps concurrent GC and takes the gen0 knob alone, which was worth 12 of 15 on its
own. `src/CS2DemoKit.Analysis/README.md` says the same in fewer words.

### One producer

    runs      10 sampled (1 round x 5 demos x 2 arms), zero failures, zero digest mismatches
    rows      paths-one-producer.csv (the demos were symlinked into a scratch directory, so size_mb reads 0.0)

The up-front parallel producer that decoded a retained demo's digests before the first frame
was evaluated is gone; the pipelined producer that served the forward reader serves the
`ParsedDemo` too, with the same chunks, the same prime and the same fold. Over a list it
releases nothing and cuts its chunks from the frame count to about twice the worker count,
because every chunk costs a checkpoint prime and the frames are resident either way: with the
stream's 1024-frame chunks the retained arm ran 15 to 20% slower than the producer it replaced
(39 primes where that one did 10), and the derived size closes that. Same five demos, before
(`paths-alloc.csv`) and after, single runs:

| demo | size | retained wall before | after | retained peak heap before | after | forward wall before | after |
|---|---|---|---|---|---|---|---|
| ...1164257366_406 | 40 MB | 0.9 s | 0.9 s | 271 MB | 238 MB | 0.8 s | 0.7 s |
| ...0748090338_404 | 204 MB | 2.2 s | 2.4 s | 906 MB | 868 MB | 1.8 s | 1.4 s |
| ...1163782782_410 | 280 MB | 2.5 s | 2.5 s | 1028 MB | 1030 MB | 1.9 s | 2.0 s |
| ...0449092279_123 | 432 MB | 3.0 s | 2.7 s | 1536 MB | 1497 MB | 2.3 s | 3.8 s |
| ...1522348072_129 | 524 MB | 3.3 s | 3.3 s | 1791 MB | 1788 MB | 4.0 s | 4.7 s |

**The worker default.** Over a list the default is two fewer than the core count, never below
the stream's three: retained arm, three rounds each on the 280 and 524 MB demos, three workers
sit at the back (2.55 s and 3.15 s medians) while six through ten are within noise of each other
(2.41 to 2.56 s and 2.93 to 3.12 s) and of the retired producer built and measured in the same
session (2.49 s and 3.13 s). The retained peak heap is the `ParsedDemo` and its snapshots, so
it moves only on the small demos, where the digest array the old producer held for every frame
was a visible share of it.

**The control.** Nothing on the forward path changed in a way that should move it, and the
single-run rows above move both ways because the default collector is bimodal on this arm (the
base build measured 2.7 s and 3.8 s on consecutive runs of the 524 MB demo). Three rounds each,
base build against this one, same session, forward arm medians: 1.75 s against 1.72 s (204 MB),
3.26 s against 2.74 s (432 MB), 3.78 s against 2.58 s (524 MB). The sampled forward peak is 30
to 80 MB lower on every demo: the schema probe, a throwaway layer replayed over the first chunk
on the reader thread, is gone, and the check runs on the fold worker's own tracker.

### Two decode loops

    runs      30 sampled (3 rounds x 5 demos x 2 arms) and 10 live (1 round), zero failures, zero digest mismatches
    rows      paths-decode-loops.csv (sampled), paths-decode-loops-live.csv (live)
    load      2.6 to 4.9 throughout

`DemoParser.Parse` decodes in three passes over the whole file: a sequential header scan, a
`Parallel.For` over every frame, then enrichment. `DemoReader` scans one window of
`ReadAheadFrames` headers, decodes the window with the same `Parallel.For` into pooled partitions,
and hands the frames out in order with enrichment running on the consumer's thread.
`DemoReader.Materialize()` as shipped delegates to `DemoParser.Parse`, so the `materialise` arm
walks the reader itself with one window over the whole file and keeps every frame, which is what
a `ParsedDemo` holds; the reader sizes its window arrays up front, so the window has to be a
number, and 262,144 is above the corpus's largest frame count. The `parse` arm is
`DemoParser.Parse` over `File.ReadAllBytes`. Both arms fold the frame count, every frame's command
and tick, and every decoded message's type id into one digest, and no pair disagreed. Wall is the
median of three; peak heap is the sampled median and the live-set round.

| demo | size | frames | parse wall | materialise wall | ratio | parse peak heap, sampled / live | materialise peak heap, sampled / live | parse alloc | materialise alloc |
|---|---|---|---|---|---|---|---|---|---|
| ...1164257366_406 | 40 MB | 35,731 | 0.25 s | 0.26 s | 1.04x | 179 / 166 MB | 145 / 139 MB | 136 MB | 102 MB |
| ...0748090338_404 | 204 MB | 91,566 | 0.74 s | 0.74 s | 1.00x | 749 / 715 MB | 518 / 492 MB | 594 MB | 363 MB |
| ...1163782782_410 | 280 MB | 121,233 | 0.81 s | 0.85 s | 1.05x | 1014 / 950 MB | 697 / 662 MB | 793 MB | 473 MB |
| ...0449092279_123 | 432 MB | 196,844 | 1.16 s | 1.08 s | 0.93x | 1305 / 1296 MB | 809 / 771 MB | 1242 MB | 745 MB |
| ...1522348072_129 | 524 MB | 228,902 | 1.21 s | 1.22 s | 1.01x | 1612 / 1549 MB | 1007 / 952 MB | 1481 MB | 868 MB |

The reader's loop runs at 0.93x to 1.05x the whole-file parse's wall over the five demos, median
1.01x. The three runs' spreads overlap on four demos (the 280 MB demo's parse runs span 0.80 to
0.91 s, its reader runs 0.76 to 0.86 s); on the 432 MB demo the reader's three runs, 1.07 to
1.10 s, all sit below the parse's 1.11 to 1.17 s. The parse arm's heap carries the file's
`byte[]` where the reader maps the file and carries it in working set instead: peak heap less the
file size is 139, 545, 734, 873 and 1088 MB for the parse arm against 145, 518, 697, 809 and 1007
MB for the reader. What remains of the gap grows with the file, and it is the descriptor list
`Parse` pre-sizes at one entry per 250 bytes of file (40 bytes each: 7, 34, 47, 72 and 88 MB here)
less the reader's fixed 12.5 MB of window arrays, to within 6 MB on every demo. The reader
allocates 34 to 613 MB less per run, which is the file's bytes plus that list. Collector pauses are
within 15% of each other on every demo, gen2 counts are equal except on the 524 MB demo (3 against
4 or 5), and working set is 6 MB higher on the reader for the 40 MB demo and 22 to 74 MB lower
on the other four.

#### After the unification

    commit    d527da4
    runs      30 sampled on each build (3 passes x 5 demos x 2 arms, the builds alternated per pass), 10 live on this build, zero failures, zero digest mismatches
    rows      paths-one-loop.csv (this build, sampled), paths-one-loop-live.csv (live), paths-one-loop-base.csv (776de80, sampled)
    load      2.6 to 4.5 throughout

`DemoParser.Parse` is now `DemoReader.Materialize()` over a reader it opens: one window over every
frame, through the same `ScanWindow` and `DecodeWindow` the `materialise` arm pulls through
`ReadFrames`, then the cursor over the result. The `parse` arm is that call over the file's bytes;
the `materialise` arm's mechanics did not change, which makes it the control for the session. The
base build and this one ran three passes alternated in one session rather than against the rows
above, because the machine was not quiet: the lock screen's wallpaper kept `WindowServer` busy
throughout, and a first run of this build alone read 1.03x to 1.19x against those rows. Wall is
the median of three; peak heap is the sampled median and the live-set round on this build.

| demo | size | frames | parse wall, 776de80 | parse wall | ratio | parse peak heap, sampled / live | parse alloc | materialise wall, 776de80 | materialise wall |
|---|---|---|---|---|---|---|---|---|---|
| ...1164257366_406 | 40 MB | 35,731 | 0.26 s | 0.26 s | 0.99x | 180 / 165 MB | 137 MB | 0.27 s | 0.26 s |
| ...0748090338_404 | 204 MB | 91,566 | 0.68 s | 0.69 s | 1.02x | 729 / 689 MB | 586 MB | 0.68 s | 0.68 s |
| ...1163782782_410 | 280 MB | 121,233 | 0.78 s | 0.85 s | 1.09x | 1007 / 954 MB | 784 MB | 0.79 s | 0.81 s |
| ...0449092279_123 | 432 MB | 196,844 | 1.14 s | 1.18 s | 1.03x | 1296 / 1235 MB | 1230 MB | 1.10 s | 1.15 s |
| ...1522348072_129 | 524 MB | 228,902 | 1.26 s | 1.24 s | 0.98x | 1589 / 1508 MB | 1462 MB | 1.23 s | 1.27 s |

The parse arm runs at 0.98x to 1.09x the base build's wall, median 1.02x, with the same digest on
every row. The materialise arm runs at 0.99x to 1.05x in the same passes, median 1.03x, so the
parse arm is inside the session's drift. The one demo past 1.05x, the 280 MB one, has its three
parse runs at 0.77, 0.85 and 0.86 s against the base's 0.77, 0.78 and 0.80 s; the 524 MB demo's
overlap the other way. The parse arm allocates 1 to 20 MB less (137, 586, 784, 1230 and 1462 MB
against 136, 592, 794, 1242 and 1482), because the decode partitions are pooled across the
window's tasks where the old loop's local-init built one per task; its peak heap is within 19 MB
of the base, collector pauses within 8 ms, and the gen2 counts are the same. Against the rows
above, the reader's loop now decodes every frame that reaches a `ParsedDemo`.

## Reading these numbers later

**Compare like for like.** Absolute values here are only valid for this machine, quiet. An
earlier set of sweeps in the same corpus read 10-15% slower throughout because Adobe Creative
Cloud was consuming four cores; the comparisons drawn from them survived because every variant
was interleaved and hit the contention equally, but their absolute figures did not.

**Watch the `load1` column.** Rows are stamped with the 1-minute load average. Discard outliers
rather than averaging them in.

**Rows from before the ray path was measured do not compare with rows after it.** Every table
above was taken in the 27-column row shape. The row now has 34 columns (`vis` through `rays_cast`
were added), and every measurement, `vis=0` included, evaluates a fifth ruleset on top of the four
shipped ones: the bench's own `enemy_spotted` subscriber, loaded in both modes so the graph is the
same with and without a bake. `eval_ms` therefore moved for a reason that is not the library, and
a new sweep must be re-baselined rather than read against these tables. With `vis=1` the process
also holds the map's visibility engine through every timed phase; `retained_mb` and the
allocation deltas exclude it, since the baseline memory is read after the engine is built and
settled, but `eval_ms` includes the rays it answers. `compare` refuses a row whose columns or
`vis` disagree with its own header, so a CSV cannot hold both shapes.

**The replacement sweep has not been taken.** Every load-pipeline table above is still in the old
row shape, so it is the last measured state of the pipeline and not something a new sweep can be
read against. Taking the replacement needs the 15-demo corpus, which is not in the repository, and a
quiet machine; the BVH section is measured from bakes alone and does not stand in for it.

**Medians, and the distribution.** A GC-bound pipeline is bimodal when a collection lands inside
a timed window: modes hundreds of milliseconds apart with nothing between them. A mean over that
is meaningless and a median over too few runs is unstable. Report the spread.
