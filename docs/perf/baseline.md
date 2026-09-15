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
