# Profiling & Instrumentation — CS2DemoKit

Performance instrumentation for the three perf-critical layers (**parser**, **entity tracking**,
**analysis engine**) is **off by default** and must be explicitly enabled. Enabling is a **runtime
switch** — there is no profiling build. A default run pays only a single predicted branch per
instrumented seam when the switch is off, and the runtime diagnostic sources idle at a single branch
with no listener attached — the accepted price of having one uniform mechanism (no build-time `#if`
anywhere).

## The two switches

Both live in the Parser assembly — the lowest common layer every other project references — so
`EntityTracking`, `Analysis` and your own host all read the exact same flag. They are deliberately
independent: you can take the decode trace without paying for timing instrumentation, and the timing
profile without the trace polluting the measurement.

| Switch | Env var | Gates |
|---|---|---|
| `CS2DemoKit.Parser.Profiling.Enabled` | `CS2DEMOKIT_PROFILE` | Parse-pipeline, entity-decode and evaluator timing/allocation accumulators. |
| `CS2DemoKit.Parser.Tracing.Enabled` | `CS2DEMOKIT_TRACE_DECODE` | The entity-decode bit-misalignment trace — the per-op/per-field record the decoder keeps for one in-flight `CSVCMsg_PacketEntities`. |

Both env vars accept `1` / `true` / `yes` (case-insensitive) and are resolved once, the first time the
flag is touched. The `DEMOVIEWER_PROFILE` / `DEMOVIEWER_TRACE_DECODE` spellings these switches shipped
under are still honoured, second in precedence, so existing scripts keep working. Either flag can also
be set from code:

```csharp
CS2DemoKit.Parser.Profiling.Enabled = true;
CS2DemoKit.Parser.Tracing.Enabled   = true;   // independent — neither implies the other
```

Default (env unset, flag not flipped): everything off.

**Threading contract — set before the run.** Both flags are read on `Parallel.For` worker threads
(parse pass-2, the parallel digest producer). Set them *before* the run they govern begins; the
`Parallel.For` fork is a full memory barrier, so every worker observes the pre-fork value. A plain
`bool` is sufficient under this contract (the hot fan-out sites additionally snapshot it into a local
before forking). One profiled run at a time — the accumulators are process-static and assume the
single-run convention.

## Runtime sources (always shipped, near-free when off)

The analysis evaluator publishes three runtime sources that cost a single branch when nobody listens.
They are gated on listener attachment, not on `Profiling.Enabled`:

- **`EventSource` `CS2DemoKit.Analysis.Evaluator`** — per-frame / per-message / lifecycle trace events
  (`NodeRegistered`, `EdgeRegistered`, `FrameProcessed`, `MessageProcessed`, `EdgeEvaluated`,
  `LogicNodeRecomputed`, `PlayerMaterialized`, `EvaluationStarted`, `EvaluationCompleted`,
  `RoundReset`, `DispatchSlotSorted`, `UndeclaredEdgeEffect`). The two per-edge/per-node events are
  `EventLevel.Verbose`; the rest are `Informational` or `Warning`.
- **`Meter` `CS2DemoKit.Analysis.Evaluator`** — counters (`analysis.messages.processed`,
  `analysis.edges.evaluated`, `analysis.edges.fired`, `analysis.logic_nodes.recomputed`,
  `analysis.players.materialized`) plus the `analysis.frame.duration_ms` histogram.
- **`ActivitySource` `CS2DemoKit.Analysis`** — phase-timeline spans (`analysis.eval` ⊃
  `analysis.precompute`). `StartActivity` returns `null` when nothing is sampling, so the spans are
  near-free by default. A host can nest its own spans (read / parse / build) on the same source to get
  the full pipeline in one timeline.

Internally the evaluator's per-message `Counter.Add` block is guarded on the runtime's own
`Instrument.Enabled` check ("is any `MeterListener` subscribed?"), so the default path pays one bool
read instead of four `Counter.Add`. The frame-duration histogram records whenever **either** an
EventSource trace **or** a Meter listener is attached.

**Provider names (for filtering).** The evaluator's events and counters share the EventSource/Meter
name **`CS2DemoKit.Analysis.Evaluator`** (evaluator-scoped). The phase-timeline spans use the broader
ActivitySource name **`CS2DemoKit.Analysis`** (whole-pipeline-scoped). Filter on the name matching the
data you want; they are intentionally distinct because their scopes differ.

## One-env-var report on exit — `ProfilingSession`

For an in-proc report **dumped on exit** with no external tooling, use
`CS2DemoKit.Analysis.Diagnostics.ProfilingSession`. It attaches the Meter + ActivitySource listeners
for the whole session and, on `Dispose`, writes a combined report (phase timeline + evaluator
counters) to a `TextWriter` — `Console.Out` by default.

```csharp
using CS2DemoKit.Analysis.Diagnostics;

// Returns null (no listeners, no cost) unless CS2DEMOKIT_PROFILE is truthy:
using ProfilingSession? session = ProfilingSession.StartFromEnvironment();

// … parse + analyse …
// Report prints when `session` is disposed at end of scope.
```

`new ProfilingSession(output)` attaches unconditionally if you want the report without consulting the
environment. Note that the session covers only the Meter/Activity sources — the parse and entity
accumulator trees are gated separately on `Profiling.Enabled`, which the same `CS2DEMOKIT_PROFILE`
env var also resolves, so setting the env var at process start lights up both.

The report goes to `Console.Out`, so it is only visible from a host with a console. It is a
**whole-session aggregate** (every run summed, printed once at dispose) — use `dotnet-counters` for a
live per-moment view.

## Attaching `dotnet-counters` / `dotnet-trace` to your host

Because the runtime sources ship in the default binary, you can attach to a **running** process with
the standard .NET diagnostics CLI — no rebuild, no application code:

```sh
dotnet tool install --global dotnet-trace      # one-time
dotnet tool install --global dotnet-counters   # one-time

# Live counters (substitute your own host's process name):
dotnet-counters monitor --name YourHost \
  --counters CS2DemoKit.Analysis.Evaluator,System.Runtime

# Collect a trace (the EventSource + CPU samples + GC) → open in PerfView / speedscope:
dotnet-trace collect --name YourHost \
  --providers CS2DemoKit.Analysis.Evaluator:0xFFFFFFFFFFFFFFFF:4,Microsoft-DotNETCore-SampleProfiler,System.Runtime
```

`--name` takes the process name; `--process-id` takes the PID. The `:4` on the provider spec is
`EventLevel.Informational` — raise it to `:5` to pick up the two Verbose per-edge/per-node events.

## Reading a snapshot programmatically

```csharp
// Turn profiling on before the run (or set CS2DEMOKIT_PROFILE=1 at startup):
CS2DemoKit.Parser.Profiling.Enabled = true;

// Parse pipeline (after a parse done with profiling on):
ParseProfilingSnapshot p = ParseProfilingSnapshot.Read();   // .Enabled == false if that parse was unprofiled
// Entity decode (after a replay done with profiling on):
EntityProfilingSnapshot e = tracker.GetProfilingSnapshot();
// Analysis-side per-frame scanner phases:
ScannerProfilingSnapshot s = scanner.GetProfilingSnapshot();
```

Each snapshot's `.Enabled` reflects whether **that data was captured with profiling on** — it is
latched when the instrumented region begins, not read live at read time, so toggling the flag after a
run never misreports a snapshot. Tick fields are raw `Stopwatch` timestamps — convert with
`Stopwatch.GetElapsedTime` / `Stopwatch.Frequency`.

Reading the trees correctly:

- **`ParseProfilingSnapshot`** — `Pass1HeaderTicks`/`Pass1Alloc` and `Pass3EnrichTicks`/`Pass3Alloc`
  are exact (both passes are sequential). `Pass2WallTicks` is the wall-clock of the parallel decode
  span; its per-worker allocation is deliberately not attributed here, because there is no correct
  outside-the-loop figure. Take a `dotnet-trace` CPU sample for the decompress-vs-parse split.
- **`EntityProfilingSnapshot`** — the intervals are **nested, not disjoint**: `PacketEntitiesTicks`
  brackets the whole `PacketEntities` decode and contains `FieldPathTicks` + `FieldValueTicks` +
  `DescriptorBuildTicks` plus per-entity prelude overhead. Report them as a tree with an explicit
  unattributed remainder.
- **`ScannerProfilingSnapshot`** — `SeekTicks` is the outer cost of advancing the entity layer one
  frame and transitively contains the tracker-internal decode that `EntityProfilingSnapshot` reports
  separately. Under the parallel precompute path the decode runs up front on throwaway worker
  trackers, so `SeekTicks`/`SnapshotTicks` and the tracker sub-tree read ~0 and the cost lands in
  `PrecomputeTicks` instead. That is expected, not a regression. `ProviderPollTicks` and
  `ProjectileScanTicks` are legacy sub-phases folded into the snapshot/digest build and are now
  always zero.
