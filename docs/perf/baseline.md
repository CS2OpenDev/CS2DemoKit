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
