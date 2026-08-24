# CS2DemoKit.Parser

A zero-copy parser for CS2 (Counter-Strike 2) `.dem` files. Parses the frame stream directly off
the input buffer with no intermediate copies, decodes 272 typed game events, and includes
`EntityTracker` for stateful entity replay (player positions, health, weapons, …). Typed entity
wrappers (`CSPlayerPawn`, `CSGameRules`, `WeaponAWP`, …) come from the companion
`CS2OpenDev.Sdk.Entities` package and bind over this runtime through the
`Entities/SdkAbstractions` seam (`LensBoundReader` / `TrackerEntityWorld`). Targets `net10.0`.

This package has no knowledge of rules, stats, or highlights — see `CS2DemoKit.Analysis` for that.
Dependencies: `Google.Protobuf`, `Snappier`, and `CS2OpenDev.Sdk.Entities.Abstractions` (the
entity read contract the seam implements).

## Quickstart

```csharp
using CS2DemoKit.Parser;
using CS2DemoKit.Parser.GameEvents;

// A stable file already fully written to disk — the memory-mapped path avoids putting the whole
// file on the managed heap.
ParsedDemo demo = MemoryMappedDemoSource.ParseFile(path);

foreach (GameEvent evt in demo.AllGameEvents)
{
    if (evt is PlayerDeathEvent death)
    {
        PlayerInfo? victim = demo.Players.GetValueOrDefault(death.VictimSlot);
        PlayerInfo? killer = demo.Players.GetValueOrDefault(death.KillerSlot);
        Console.WriteLine($"tick {death.GameTick}: {killer?.SteamId64} killed {victim?.SteamId64} with {death.Weapon}");
    }
}
```

`ParsedDemo.Players` is keyed by player **slot** (`int`), the same key every typed game event uses
for its slot-shaped fields (`VictimSlot`, `KillerSlot`, …) — join through it to get `PlayerInfo`
(`SteamId64`, `Name`, `Team`, …).

## Tick clocks — read this before touching ticks

| Property | Clock | Notes |
|---|---|---|
| `DemoFrame.ServerTick` (`int`) | **frame clock** | Despite the name, this holds the game tick in CS2 — pre-game frames carry a large negative sentinel, gameplay frames run 1, 2, 3, … There is no `DemoFrame.Tick`. |
| `DemoFrame.GameTick` (`int?`) | **frame clock** | An alias of `ServerTick`, set by the parser after the header decodes. |
| `GameEvent.GameTick` (`int`) | **frame clock** | Same clock as the two above. |
| `GameEvent.ServerTick` (`int`) | **absolute** engine tick | Convert to frame clock with `GameEvent.ServerTick - ParsedDemo.ServerStartTick`. |

**Rule: never subtract `ParsedDemo.ServerStartTick` from a value that is already frame clock**
(`DemoFrame.ServerTick`/`GameTick`, `GameEvent.GameTick`). Only the absolute `GameEvent.ServerTick`
needs that conversion.

## Input: buffer vs. file path

`DemoParser.Parse(ReadOnlyMemory<byte> data, DemoProfile? profileOverride = null)` is the parser's
one entry point. `MemoryMappedDemoSource` wraps a local file as a `ReadOnlyMemory<byte>` without
materializing it as a managed array; `MemoryMappedDemoSource.ParseFile(path)` is the one-line
convenience over `Open` + `Parse`.

- **Uploaded / in-flight demos → `byte[]`.** `DemoParser.Parse(bytes.AsMemory())` snapshots the
  bytes up front, so it's safe even if the source is still being written or copied concurrently.
- **Stable files already fully on disk → `MemoryMappedDemoSource`.** Cheaper (~166 MB avoided on
  the large-object heap for a 180 MB demo), but **only for files that will not be written to while
  mapped**: a concurrent truncation while a page is mapped raises an uncatchable
  `AccessViolationException` that kills the process — no `try`/`catch` around the read fires. Don't
  map a file mid-download or mid-copy.
- The memory-mapped path rejects files over `int.MaxValue` (~2 GB) — a single `ReadOnlyMemory<byte>`
  can't address more. The `byte[]` path has no explicit guard, but is bounded by the same
  `int`-typed frame offsets internally, so plan for the same ceiling either way.
- A parse retains roughly **2.5× the input file's size** in the returned `ParsedDemo` (frames, decoded
  proto messages, event/player indexes). Size worker pools and per-process demo-concurrency limits
  against that multiplier, not the raw file size.
- Per-parse control (0.8+): `DemoParser.Parse(data, new ParseOptions { ... }, profileOverride)` —
  `CancellationToken` (checked at pass boundaries and per frame in the parallel pass),
  `MaxDegreeOfParallelism` for the parallel decode
  pass (null/≤0 = unbounded), throttled `IProgress<double>`, and `OnUnknownMessage`, a per-parse
  callback that doesn't cross-talk between concurrent parses the way the static
  `OnUnknownMessageType` event does. Still gate the number of concurrent *demos* with your own
  `SemaphoreSlim` sized to the ~2.5× memory multiplier.
- Scoring an untrusted upload: set `ParseOptions.CountDropSites = true` — silently-dropped
  net-messages then surface on `ParsedDemo.Warnings` as `ParseWarningCodes.NetMessageDropped`
  entries (top offenders + remainder, each with a `Count`), emitted after the parse's own
  structural warnings so they never displace them.

## Parse warnings

`ParsedDemo.Warnings` (`IReadOnlyList<ParseWarning>`) carries non-fatal, structured diagnostics —
a damaged demo still yields a usable partial parse, but the damage is no longer silent. Each
`ParseWarning` has a stable `Code` from `ParseWarningCodes` (`StringTableCreateFailed`,
`StringTableUpdateFailed`, `StringTableTruncated`, `PlayerInfoUnreadable`), a human `Message`, and
an optional `Tick`.

The list is capped at 256 entries per parse; once the cap is hit, a final entry with code
`ParseWarningCodes.WarningsTruncated` reports how many further warnings were suppressed — it is
always the last entry when present.

**Pool-consumer caveat:** the accumulator behind `Warnings` is thread-affine (`[ThreadStatic]`),
drained into the `ParsedDemo` you get back and reset on that same thread. If a parse **throws**
before constructing its `ParsedDemo`, its warnings are left on that thread; the next successful
parse on the *same* thread drains them along with its own, so a dead parse's warnings can end up
misattributed to whatever parse runs next on that thread. This only bites if you run parses on a
reused thread pool (e.g. `Task.Run` over a shared pool) and something upstream swallows the
exception from a failed parse — treat `Warnings` on the following result on that thread as suspect
in that case.

## Entity tracking

Build a lens-bound tracker in one call: `EntityTrackerFactory.CreateCurated()` binds the
generated Schema Lens (omitting it does not throw — lane-routed reads silently degrade to the
fallback dict, so prefer the factory). To read through typed wrappers, register the
`CS2OpenDev.Sdk.Entities` factories via `TrackerEntityWorld.RegisterWrapper` (or bind a wrapper
per entity with `new TrackerEntityWorld(tracker).CreateReader(binding, state)`).
`ReplayTo`/`ReplayToIndex` replay from frame 0 on **every** call; for forward walks use
`EntityStateLayer` in `CS2DemoKit.Analysis` instead. Decode diagnostics go to
`EntityTracker.DecodeDiagnosticSink` (`Action<string>`, defaults to `Console.WriteLine`) — redirect
or silence it per tracker in batch services. `PositionUtil.CellToWorld` (namespace
`CS2DemoKit.Parser.EntityTracking`) is the oracle-pinned pawn cell→world reconstruction and
`PawnLookup` beside it resolves pawn↔slot. `TickMapper` and `TickBoundaries.FrameIndices` cover
demo-tick mapping and tick-boundary frame indexing.

### Player trajectories

`PositionSampler.Walk` is those pieces assembled: it steps the tracker one frame at a time,
enumerates live pawns, resolves each to a slot, and reconstructs world position.

```csharp
foreach (PositionSample s in PositionSampler.Walk(demo, frameStride: 8))
{
    // s.FrameIndex, s.Tick, s.PlayerSlot, s.Position (Vector3), s.Place
}
```

`frameStride` subsamples the output only. Every frame is still decoded, because entity state is
delta-encoded and skipping a frame's deltas corrupts the frames after it, so the stride buys memory
and downstream work rather than decode time. At 64 tick a player covers roughly 4 units per frame,
so a stride of 8 draws a path to about 32 units at an eighth of the points.

Measured on a 223,628-frame match, Release, parse excluded: 1,635,249 samples in 3.6 s at stride 1,
and 204,411 in 2.3 s at stride 8. The stride cuts points 8x and wall time by a third, which is the
decode floor showing through. `Walk` is lazy, so a consumer that folds rather than collects pays
that time without the ~50 MB the full list retains.

`maxFrames` stops the walk early. There is deliberately no way to start it late: entity state is
delta-encoded, so a walk beginning anywhere but frame 0 reports positions built on deltas it never
saw. For one round's path, walk from the start and filter by `PositionSample.Tick`.

Do **not** reach for the `player.pos_x/y/z` rule providers to extract paths. They emit into the
digest per change, which is 26x the cells of the whole shipped provider set for one axis and 100x
for all three (`docs/perf/baseline.md`). Coordinates in rules are for predicates at events; this
walk is for trajectories.

## Working with raw net messages

Generated Valve proto types (`CDemoPacket`, `CSVCMsg_PacketEntities`, `CCSUsrMsg_*`, …) live in
`CS2OpenSchema.Protos`, not the global namespace — add the `using` to pattern-match
`NetMessage.Payload`. They ship in the `CS2OpenDev.Protos` package, which comes along as a
dependency of this one:

```csharp
using CS2OpenSchema.Protos;

foreach (NetMessage msg in frame.InnerMessages)
{
    if (msg.Payload is CSVCMsg_PacketEntities entities)
    {
        // ...
    }
}
```

## Opt-in diagnostics

Two environment variables gate diagnostics that are off by default and cost nothing when unset:
`CS2DEMOKIT_PROFILE=1` enables the parse-profiling accumulator, `CS2DEMOKIT_TRACE_DECODE=1`
enables verbose entity-decode tracing. Both are settable in code as well, via `Profiling.Enabled`
and `Tracing.Enabled` — set them before the run they govern begins.

The `DEMOVIEWER_*` spellings these switches shipped under are still honoured, second in
precedence, so existing scripts keep working.

## License

MIT. Contains code adapted from [demofile-net](https://github.com/saul/demofile-net) (MIT) —
see `THIRD-PARTY-NOTICES.md` in the repo for the full attribution and file list.
