# Feature request: a selective, non-retaining decode path for CS2DemoKit

**Status:** implemented in 0.12.0. `DecodePlan` and `ParseOptions.Plan` declare what a parse decodes, `DemoReader` walks a demo forward under that plan and keeps nothing the caller let go of, `DemoFrame.InnerMessageHeaders` counts a frame without decoding it, and `ParsedDemo.Plan` and `Provenance` make the chosen path visible. Measured by the bench's `paths` verb; see `docs/perf/baseline.md`.
**Raised from:** the cross-parser measurement session of 2026-09-14, `results/published/parser-post-20260914/`
**Scope:** what is being asked for and why. Deliberately says nothing about how to build it.

---

## The request, in one paragraph

CS2DemoKit should offer a way for a caller to say what it needs from a demo, and get only that,
without the parser decoding, materialising and retaining the parts nobody asked for. Today there is
one path: parse everything, keep everything, hand back a complete object graph. That path is the
right answer for some callers and an expensive answer for most of them.

This is a request for an **additional** capability, not a replacement. The materialising model is
load-bearing for the entity layer, the analysis engine and any interactive tool, and nothing here
suggests weakening it.

---

## Why

### Most jobs need a small fraction of what the parser produces

A caller tallying kills does not need entity state. A caller counting messages does not need game
events decoded. A caller extracting one round does not need the other twenty-nine. In each case the
work is done and kept anyway, because there is no way to decline it.

### The cost is measurable, and we measured it

From the session above: four arms, ten demos spanning 52 MB to 524 MB, nine maps, ten warm rounds
each, correctness-gated before any timing. The comparison is against demofile-net, which is the
fairest yardstick available: same language, same runtime, same collector, same JIT, so any
difference is a design difference and nothing else.

On **game events**, serial, median across the corpus:

| | CS2DemoKit | demofile-net |
|---|---|---|
| Wall clock | 1847.6 ms | 2168.4 ms |
| Managed allocation | 765 MB | 320 MB |
| GC pause | 191 ms | 10 ms |
| Gen2 collections | 3 | 0 |
| Peak RSS | 821 MB | 127 MB |

On **entity replay** the shape is the same: 3365 ms against 3748 ms, 978 MB allocated against
541 MB, 244 ms of GC pause against 18 ms.

Two things stand out.

**We win the wall clock and pay for it twice over in memory.** The margin is 1.17x on game events
and 1.11x on entity replay. The allocation gap is 2.4x and the GC-pause gap is roughly 19x. Around a
tenth of our serial game-events time is collector pause that the other implementation does not pay
at all. The parse is genuinely faster; the collector hands most of the advantage back.

**Our memory grows with the demo; theirs does not.** Splitting resident memory into
memory-mapped-file pages and private memory, on the same workload:

| demo size | CS2DemoKit private | demofile-net private |
|---|---|---|
| 256 MB | 527 MB | 82 MB |
| 524 MB | 960 MB | 83 MB |

That flat line is the whole point. A caller that streams holds roughly the same amount of memory
whether the demo is 50 MB or 500 MB. A caller using CS2DemoKit holds something proportional to the
demo, on every workload, whether or not the workload benefits from it.

### Some jobs cannot be expressed at all today

While building the benchmark harness we found we could not implement a "walk the frames and count
messages by type, decode nothing" workload through the library's normal path, because decoding is
not separable from enumeration. We ended up writing that walk by hand against the public low-level
primitives. That worked, but a job that simple should not require going around the library.

The same limitation shows up in a smaller way elsewhere: a caller who wants only game events still
pays for every message body to be decoded and for a runtime schema to be built.

### It affects who can use the library, not just how fast it is

Memory proportional to demo size sets a ceiling on how many demos can be processed concurrently, and
on what hardware. A batch job over a season of matches, a container with a fixed memory limit, a
service parsing several demos at once. All of these are shaped more by the retention curve than by
the wall clock. A caller who cannot afford 800 MB per demo currently has no smaller option, however
little of the demo they actually need.

---

## What we are asking for

In terms of outcomes rather than mechanism:

1. **A caller can declare what it needs** before parsing starts, and the parser does not pay for the
   rest.
2. **What is not needed is not retained.** The benefit should show up in memory held, not only in
   time saved.
3. **Enumeration without decoding is expressible**: walking structure while leaving payloads alone
   should be a supported thing to ask for, not something to be reconstructed from primitives.
4. **The existing model stays exactly as it is** for callers who want the full graph and random
   access. This is an additional path, chosen explicitly.
5. **The choice is visible in the result**, so a caller can tell which path produced what it is
   holding, and so a measurement can say which path it measured.

## What we are not asking for

- Not a rewrite, and not a change to the current default.
- Not a specific API, options object, or execution model. That is the maintainer's call and this
  document deliberately stops short of it.
- Not a request to match any other library's numbers. The materialising design buys real
  capability, and the request is to make that a choice rather than an obligation.

---

## How we would know it worked

The harness that produced the numbers above can answer this directly, and the bar is a memory bar
rather than a speed one:

- On a workload that needs only game events, **private memory stops tracking demo size**: the 527
  MB and 960 MB figures above collapse toward a flat line.
- **Allocation and GC pause fall substantially** on workloads that do not use the full graph.
- The correctness gate still passes, unchanged. A faster path that changes a count is not a faster
  path. Every number above was taken only after all arms were shown to produce byte-identical
  results on the same demo, and that is the standard any new path should be held to.
- Wall clock on the full-graph path does not regress.

---

## Caveats on the evidence

- One machine, one OS, ten demos, all GOTV matchmaking from a single source. Broad enough to show
  the retention curve; not a claim about every workload or every recording source.
- Peak RSS on both memory-mapping arms includes resident mapped-file pages, which is why the table
  above uses private memory for the size-scaling comparison: that is the figure that reflects what
  the parser holds rather than how it reads.
- The per-component breakdown of our allocation has not been measured. We know the aggregate and we
  know the structural causes; we have not attributed bytes to specific structures, and this document
  does not guess at it.
