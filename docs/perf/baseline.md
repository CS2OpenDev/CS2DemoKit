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
