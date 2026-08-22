# Load-pipeline baseline

Reference point for demo-load performance. Re-measure against this before claiming a change
helped, and update it when one lands.

    commit    ecfc03d (merge of #25)
    runs      50 (10 rounds x 5 demos), zero failures, 919 s
    machine   10 cores (6P + 4E), .NET 10, Release, workstation GC
    corpus    5 full-match demos, 288-294 MB, 121k-149k frames
    load      1.44 to 3.59 throughout

Raw rows in `baseline-ecfc03d.csv`. Reproduce with `tools/CS2DemoKit.Bench/run-baseline.sh`.
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

Interleaved A/B, same tree one commit apart, 5 rounds x 5 demos x 2 variants, 50 runs, zero
failures, 900 s. Raw rows in `digest-delta-8804862.csv`. Order flips each round so drift hits both
arms. Load ran 1.59-4.81 (higher than the sweep above), but the control arm's median evaluate came
out at 1395.1 ms against this baseline's 1398.5, so the two are comparable.

| metric | before | after | change |
|---|---|---|---|
| parse (control, untouched) | 388.4 ms | 380.4 ms | -2.1% |
| build | 21.3 ms | 21.3 ms | 0.0% |
| **evaluate** | **1395.1 ms** | **959.3 ms** | **-31.2%** |
| evaluate GC pause | 424.6 ms | 280.4 ms | -34.0% |
| evaluate allocation | 629.8 MB | 549.2 MB | -12.8% |
| retained by demo (control) | 601.4 MB | 602.3 MB | +0.1% |
| evaluate gen0 / gen1 / gen2 | 81 / 29 / 1 | 70 / 21 / 0 | |
| **total load** | **1814.8 ms** | **1363.0 ms** | **-24.9%** |

Per demo, median evaluate: -32.1%, -31.0%, -22.7%, -34.0%, -32.5%.

Parse and retained are the controls. Both are outside the change and both held, which is what
says the evaluate delta is real rather than a shifted measurement window.

This is a collector win, not a decode win: every provider is still read for every live pawn on
every frame. It also depends on the shipped providers changing rarely. A provider that moves every
frame, which is what positional predicates (#5) would add, degrades this to the old cost plus a
comparison, and the phase would need re-measuring.

Still open here: the precompute gives each chunk worker its own `EntityStateLayer`, so worker
count and retained entity state are coupled. Measured at ~26 MB and ~25 ms of pause per extra
worker, with pause rising monotonically in chunk count while wall time traces a U. Six chunks beat
the shipped `Environment.ProcessorCount` on a 6P+4E machine, but the wall-time signal was inside
the run-to-run spread at two runs per point, so it is untouched and wants its own sweep.

Ruled out on the way, both cheaply: worker load imbalance (max/median chunk wall time 1.02-1.08,
so the P/E-core asymmetry is not biting) and the serial layer bootstrap (0.8 ms).

Ruled out already: snapshot capture. `AnalysisOptions.CaptureSnapshots` defaults to on, and
turning it off does not make evaluation faster.

## Reading these numbers later

**Compare like for like.** Absolute values here are only valid for this machine, quiet. An
earlier set of sweeps in the same corpus read 10-15% slower throughout because Adobe Creative
Cloud was consuming four cores; the comparisons drawn from them survived because every variant
was interleaved and hit the contention equally, but their absolute figures did not.

**Watch the `load1` column.** Rows are stamped with the 1-minute load average. Discard outliers
rather than averaging them in.

**Medians, and the distribution.** A GC-bound pipeline is bimodal when a collection lands inside
a timed window: modes hundreds of milliseconds apart with nothing between them. A mean over that
is meaningless and a median over too few runs is unstable. Report the spread.
