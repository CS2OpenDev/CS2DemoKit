# Migrating to 0.12.0

0.12.0 adds a forward path and moves the engine onto it: `DemoAnalysis.Run(path, rules)` reads a
demo once, front to back, decodes only what the rule graph consumes, and keeps nothing behind the
evaluation loop. The retained path, `DemoParser.Parse` then `DemoAnalysis.Run(demo, rules)`, is
still there for tools that seek and inspect after the run, and produces byte-identical output.

This page is the order in which a consumer meets the changes. The per-break reference, one entry
per surface that moved, is the 0.12.0 block under
[Compatibility notes in `releasing.md`](releasing.md#compatibility-notes-worth-carrying-into-a-release).
The numbers behind the design are in [`perf/baseline.md`](perf/baseline.md). Everything below was
worked through on DemoViewer.NET, a desktop consumer that seeks, snapshots and hand-builds frames
in its tests, and took three commits.

## 1. Pin the family together

```xml
<PackageVersion Include="CS2DemoKit.Parser" Version="0.12.0"/>
<PackageVersion Include="CS2DemoKit.Analysis" Version="0.12.0"/>
<PackageVersion Include="CS2DemoKit.Analysis.Rules" Version="0.12.0"/>
```

Intra-family pins are exact, so a mixed set fails to restore rather than half-working.

## 2. If you only parse and run, nothing changes at the call site

```csharp
ParsedDemo demo = MemoryMappedDemoSource.ParseFile(path);
AnalysisRun run = DemoAnalysis.Run(demo, rules.Rulesets);
```

compiles and behaves as on 0.11.0: snapshots on, `run.Snapshots` populated, `run.Highlights` and
`run.ProjectConfiguredOutputs(demo)` as before. Four things happen underneath that you may notice:

- `DemoParser.Parse` runs through the same decode loop as the forward reader. Output is identical
  (the parity tests hold every frame, message and digest equal); `demo.Provenance` reports
  `DecodeSource.DemoParserParse` as before.
- A player's team is seeded from `CCSPlayerController.m_iTeamNum` as the entity scanner first
  sees it, not from the `OldTeam` of the first `player_team` event. On every corpus demo the two
  agree; a slot that never gets a `player_team` event now has its real team instead of 0. Any
  `for: each_player` ruleset therefore builds the entity scanner, so a build that used to run
  without one now decodes entities.
- Enum-typed fields and `GameTick_t` fields come back boxed as `int` rather than `ulong`, so the
  0xFFFFFFFE tick sentinel reads -2. Entity handles are still `ulong` with the `HandleIndex`
  marker.
- A retained run's `Provenance.Digest` reports `Pipelined`; the up-front producer is gone.

## 3. Compile errors, in the order they show up

**`EvaluationResult.Messages` holds `MessageRef`, not `(DemoFrame, NetMessage)`.** The record is
`MessageRef(int FrameIndex, int Tick, int Ordinal, NetMessage Message)`. Every dictionary keyed by
`DemoFrame` reference to recover a frame index can go:

```csharp
// 0.11.0
Dictionary<DemoFrame, int> frameIndexOf = new(ReferenceEqualityComparer.Instance);
for (int i = 0; i < demo.Frames.Count; i++) frameIndexOf[demo.Frames[i]] = i;
(DemoFrame frame, NetMessage msg) = result.Messages[idx];
int frameIndex = frameIndexOf[frame];

// 0.12.0
MessageRef m = result.Messages[idx];
int frameIndex = m.FrameIndex;      // demo.Frames[m.FrameIndex] is the frame, m.Tick its ServerTick
```

**`DemoFrame.CommandKind` is required; `Command` has no setter.** A hand-built frame sets the
enum and, if anything reads it, `GameTick`:

```csharp
new DemoFrame { CommandKind = EDemoCommands.DemPacket, ServerTick = tick, GameTick = tick, ... }
```

`Command` is computed from `CommandKind` and still compares as a string if you want it to.

**`RuleChainBuilder` and `StateGraphEvaluator` do not take a demo.** The builder takes an
`AnalysisTarget` (tick rate and resolved profile); the evaluator takes an `IDemoFrameSource`:

```csharp
new RuleChainBuilder(registry, AnalysisTarget.From(demo), ...)     // or new AnalysisTarget(tickRate, profile)
evaluator.Evaluate(demo.AsFrameSource(), ...)
```

The `profile` parameter is gone; `RuleChainBuilder.ObservedEvents` and
`PlayerContextIndex.InitialTeamBySlot` with it. `DemoAnalysis.Build(demo, rules)` and
`Evaluate(demo, build)` are unchanged and do this for you.

**`AnalysisOptions.CaptureSnapshots` is `bool?`.** Null means on over a `ParsedDemo` and off over
a stream. Code that read it as a `bool` gets `options.CaptureSnapshots ?? true`.

**`ParsedDemo`'s constructor is public.** A test fixture that bound the internal constructor by
reflection calls it directly; three optional parameters follow the seventeen positional ones
(`plan`, `provenance`, `warnings`).

**`DemoAnalyzer`, `DemoContext`, `IDemoContext` and `RoundInfo` are gone.** Nothing in the
repository called them. The replacements are one-liners: an eagerly replayed tracker is
`new EntityStateLayer(demo.Frames).SeekToTick(tick)`, `EventsOfType<T>()` is `demo.AllGameEvents`
filtered on `e.Payload is T`, and `EventsInRange` is that list sliced on `ServerTick`.

**`BuildResult.EntityScanner.Layer` no longer seeks.** It is fed by the producer during the run
and throws on `SeekToTick`. A caller that seeked it builds its own layer over `demo.Frames`, as
above; `EntityStateLayer.PrimeFromCheckpoint(int, int)` is gone in favour of the frame-based
overload.

**`PerPlayerNodeTemplate.Materialize(slot, index, name)`.** The trailing `ParsedDemo` argument is
gone.

**`DigestProducerKind.ParallelUpFront` and `EntityChangeScanner.AdvanceAndPoll(int)` are gone.**
A `switch` that named the enum member loses the arm. A host that walked a scanner frame by frame
calls `AdvanceAndPollAt`, which still seeks a list-backed layer when called outside an evaluation.
`PrecomputeParallelDigests` keeps its signature but folds on every call, so a benchmark that calls
it per iteration measures every iteration; `ScannerProfilingSnapshot.PrecomputeTicks` and
`PrecomputeAlloc` now read the producer's fold cost summed over its workers, on either path
(`FoldTicks` and `FoldAlloc` are the same numbers under their new name). A label that called that
figure "precompute" or compared it to wall-clock is stale.

**`PawnLookup.ResolveHandle` has a `uint` overload.** A `cref` to it must name a signature.

## 4. Behaviour that moved without a compile error

**Per-player nodes materialise during the run, not at construction.** `AnalysisRun.MaterializedPlayers`
and `FinalNodes` are populated in both capture modes, so read them rather than walking the graph
after `Build`. Order is materialisation order; sort by slot if a table depends on it. A build of an
entity-reading ruleset no longer needs a demo (the scanner requirement that `Build(target, ...)`
used to throw is gone), so a "try without a demo, retry with one" fallback is dead code: build
once, with the demo's target when one is bound so the tick rate and profile are the real ones.

**The `EntityState` indexer boxes typed lanes.** Vectors, angles, 64-bit scalars and handles are
stored unboxed on typed lanes, and `state["m_steamID"]` hands back a fresh box on every read where
0.11.0 handed back a stored reference. A hot loop or an allocation budget reads them typed:

```csharp
if (controller.TryGet<ulong>("m_steamID") is { } steamId) ...
Vector3 origin = pawn.Get<Vector3>("m_vecOrigin");
```

`EntityState.Fields`, the indexer and `TryGetValue` keep the boxed shape for a schema that maps a
field elsewhere.

**`StoreUnlensedFields` is off in the engine.** The tracker the engine builds keeps no fallback
dictionary for fields no lens rule names. A tracker you build yourself keeps the 0.11.0 default.

**`AnalysisOptions.MaxDegreeOfParallelism` is the digest worker count on either source.** Unset,
it is three over a reader and two fewer than the core count over a retained demo; one selects the
sequential producer. It no longer chooses between producers.

## 5. Taking the forward path

Nothing above requires it. Take it when the consumer does not seek: batch stats, a service, a CLI.

```csharp
AnalysisRun run = DemoAnalysis.Run(path, rules.Rulesets, options);
```

opens a `DemoReader`, resolves the source profile (explicit `options.Profile`, else a bounded
vocabulary probe, else the header alone), builds the graph, narrows the decode to what the graph
consumes (`DemoAnalysis.PlanDecode(build)`), and evaluates with every frame dropped behind the
loop. The same steps are available separately when a graph is compiled once and run over many
demos:

```csharp
using DemoReader reader = DemoReader.OpenFile(path);
BuildResult build = DemoAnalysis.Build(reader, rules.Rulesets);
reader.Configure(DemoAnalysis.PlanDecode(build));
AnalysisRun run = DemoAnalysis.Evaluate(reader, build);
```

What a consumer gives up on the stream, and where it went:

| retained | forward |
|---|---|
| `demo.MapName`, `TickRate`, `Players` | `run.Demo` (`DemoDescriptor`, final once the run ends) |
| `run.Snapshots` on by default | off by default; `CaptureSnapshots = true` turns it on at one row and one `NetMessage` per dispatched message, which is the retention the stream exists to drop |
| `run.ProjectConfiguredOutputs(demo)` | `run.ProjectConfiguredOutputs()` |
| `demo.AllGameEvents`, `demo.Frames`, seeking | not available; walk the reader yourself under a `DecodePlan`, or keep the retained path |
| profile resolved from every event in the file | `run.Provenance.ProfileResolution` says which of explicit, vocabulary probe, or header-only ran; `run.Provenance.Dialect` counts the round-end markers seen |

`run.Provenance` on either path says what actually ran: source kind, profile and how it was
resolved, the decode plan, the digest producer, whether snapshots were captured, frames and
messages consumed.

For a batch host, two runtime settings the library cannot apply are worth 24% together on every
corpus demo: `DOTNET_gcConcurrent=0` (or `<ConcurrentGarbageCollection>false</ConcurrentGarbageCollection>`)
and `DOTNET_GCgen0size=4000000`. The Analysis README's
[Garbage collection](../src/CS2DemoKit.Analysis/README.md#garbage-collection) section has the
measurement and the caveat for a host with a UI thread.

## 6. What did not change

`EntitySeekService`-style seeking over `demo.Frames`, `EntityTracker.SnapshotCurrentFields`,
`PeekEntityUpdates`, `StoreClassFilter`, the checkpoint trio (`ResetEntitiesKeepSchema`,
`ProcessFullPacketCheckpoint`, `LoadInstanceBaselineSnapshot`), `DownstreamUtilities`,
`DemoParser.OnUnknownMessageType`, `YamlConfigLoader`, the rulesets language and the shipped
rulesets, and every output projection. The rules-output goldens are byte-identical to 0.11.0's on
both paths.
