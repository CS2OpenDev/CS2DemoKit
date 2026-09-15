# Feature request: a forward-only hot path for analysis

**Status:** implemented in 0.12.0. `DemoAnalysis.Run(path, rules)` reads a demo forward through `IDemoFrameSource`, entity state is one tracker mutated in place per digest worker, previous ticks are not retained, and the shipped rulesets produce the same output on both paths (`ForwardPathParityTests`, byte for byte over the corpus). The retained `ParsedDemo` path stays for tools that seek. Measured by the bench's `paths` verb; see `docs/perf/baseline.md`.
**Raised from:** the cross-parser measurement session of 2026-09-14, `results/published/parser-post-20260914/`
**Companion to:** [`cs2demokit-selective-decode.md`](cs2demokit-selective-decode.md), related but a
different ask. That one is about not decoding what nobody wanted. This one is about how long entity
state has to live.
**Scope:** what is being asked for and why. It states the shape the request has to satisfy, and
stops short of saying how to build it.

---

## The request, in one paragraph

CS2DemoKit should offer a **forward-only** path where entity state is updated **in place** as the
demo is walked, rather than reconstructed from a fully materialised parse. Crucially, that path
should remain usable by the existing analysis engine and rules layers: the value of the library is
in what it can tell you about a match, and a fast path that cannot run the rules is a fast path
nobody reaches for.

The current model, where a whole demo is parsed and held so a caller can move forward and backward
through it, should stay exactly as it is. This is a second gear, not a replacement.

---

## Why

### The expensive workload is the one where we hold the most and gain the least

From the measurement session: ten demos, 52 MB to 524 MB, ten warm rounds each, correctness-gated
before any timing, against demofile-net (same language, same runtime, same collector, so any
difference is a design difference).

**Entity replay**, serial, median across the corpus:

| | CS2DemoKit | demofile-net |
|---|---|---|
| Wall clock | 3365.1 ms | 3747.5 ms |
| Managed allocation | 978 MB | 541 MB |
| GC pause | 244 ms | 18 ms |
| Gen2 collections | 4 | 1 |
| Peak RSS | 820.7 MB | 132.3 MB |

A 1.11x wall-clock margin, bought with 1.8x the allocation, roughly 13x the GC pause, and 6.2x the
resident memory. Entity replay is the most expensive thing the library does and it is where the
retention model costs the most for the least return.

### Holding the whole demo to replay it forward is a cost with no benefit realised

Entity replay is inherently a forward walk: apply each delta in order, observe the result. The
current shape requires the entire demo to be parsed and retained *first*, and only then replayed
over the retained frames. The retained graph is not what makes the replay work. It is a
precondition imposed on it.

A parser that applies deltas into a single mutable world holds memory bounded by the world, not by
the demo. That is why the other implementation's private memory is flat:

| demo size | CS2DemoKit private | demofile-net private |
|---|---|---|
| 256 MB | 527 MB | 82 MB |
| 524 MB | 960 MB | 83 MB |

Same workload, same machine. One line grows with the file; the other does not.

### Analysis is where this actually bites

Rules mostly ask a forward-shaped question: *at the moment this happened, what was true?* A kill is
classified against the state at the kill. A round outcome is decided by what had happened by the end
of that round. Very little of that needs the ability to go backwards, but today every rule pays for
the ability regardless, because the whole demo has to exist before `DemoAnalysis` can start.

That is the difference between a tool that can process a season of matches on a modest box and one
that cannot. Memory proportional to demo size sets a hard ceiling on concurrency, on container
sizing, and on how large a demo can be handled at all.

---

## The shape this has to satisfy

Stated as requirements, not as a design.

1. **Forward-only, in place.** Entity state is mutated as the walk proceeds. Previous ticks are not
   retained, and the caller is not expected to hold them.
2. **The analysis engine and rules layers work on it.** This is the requirement that makes the
   request worth making. A hot path that only produces raw entity state duplicates what a caller
   could already assemble; the point is to reach a finished scoreboard and the shipped rulesets
   through the cheap path.
3. **The existing forward/backward model is untouched.** Callers who need random access (interactive
   tools, anything re-examining a match) keep exactly what they have today, chosen explicitly.
4. **Results are identical, not merely similar.** The same demo through either path produces the same
   analysis output. A faster path that changes a number is not a faster path.
5. **The capability we have that others do not is preserved.** CS2DemoKit can distinguish a field
   that was never received from one that is genuinely zero, through seen-bit-gated access. That
   distinction is real, it is rare, and it should survive into the forward-only path rather than
   being traded away for speed.
6. **Which path produced a result is visible**, so a caller knows what it is holding and a
   measurement knows what it measured.

## Known constraint, named rather than solved

Not every rule is purely forward-looking. Some legitimately need to look back: "was this player
blinded in the last two seconds", anything spanning a round boundary, anything comparing against
earlier state. A forward-only path cannot serve an unbounded lookback, so there is a real question
about what such rules do: declare a bounded window, be excluded from the fast path, or something
else.

That question needs answering before this is buildable, and it is deliberately left open here. It is
the maintainer's call, and getting it wrong in a feature request is worse than leaving it open.

## What we are not asking for

- Not a replacement for the current model, and not a change to its default behaviour.
- Not a specific API, execution model, or threading arrangement.
- Not parity with any other library. The materialising design buys capability that a streaming one
  cannot offer; the ask is for a second gear, not a different vehicle.

---

## How we would know it worked

- On entity replay, **private memory stops tracking demo size**: the 527 MB and 960 MB figures
  collapse toward a flat line.
- **Allocation and GC pause fall substantially** on entity replay and on the end-to-end scoreboard
  path.
- The **correctness gate still passes unchanged**. Every number quoted above was taken only after
  all arms were shown to produce byte-identical entity digests on the same demo; a new path is held
  to the same bar.
- Analysis output through the forward-only path **matches the current path exactly** on the same
  demo.
- Wall clock on the existing path does not regress.

---

## Caveats on the evidence

- **The analysis end of this is unmeasured.** The session covered file read, message scan, game
  events and entity replay. The end-to-end scoreboard workload, the one this request is really
  aimed at, has not been benchmarked against anything, because no other parser in the comparison
  produces stats natively. The entity-replay numbers are the closest proxy available and they are
  not the same thing.
- One machine, one OS, ten demos, all GOTV matchmaking from a single source.
- Peak RSS on both memory-mapping arms includes resident mapped-file pages, which is why the
  size-scaling table uses private memory: that is the figure reflecting what the parser holds
  rather than how it reads.
- The per-component breakdown of our allocation has not been measured. The aggregate is known and
  the structural causes are understood; bytes have not been attributed to specific structures, and
  this document does not guess.
