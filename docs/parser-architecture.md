# Parser Architecture

A guided tour of `CS2DemoKit.Parser` — the library that turns a raw `.dem`
byte buffer into typed CS2 demo data for the analysis engine and any other
downstream consumer.

## TL;DR

`DemoParser.Parse(ReadOnlyMemory<byte>)` consumes a CS2 demo file and returns a
`ParsedDemo`: a flat list of `DemoFrame`s plus enriched indexes (game events,
players, schema, server metadata, demo profile). The parser runs three passes
(sequential header scan, parallel proto parse, sequential enrichment), is
zero-allocation on the per-frame hot path for uncompressed payloads, and is
entity-state-agnostic — `svc_PacketEntities.entity_data` is left as opaque
bytes. Entity replay is a separate opt-in layer (`EntityTracker`) on top.

Start reading at [`DemoParser.cs`](../src/CS2DemoKit.Parser/DemoParser.cs), at
`Parse` / `ParseCore`.

---

## 1. High-level architecture

```
                        ┌────────────────────────────────────────┐
                        │  Caller (Analysis / your own host)     │
                        └────────────────────────────────────────┘
                                       │
                                       │  byte[]  →  ParsedDemo
                                       ▼
        ┌─────────────────────────────────────────────────────────────┐
        │                     CS2DemoKit.Parser                       │
        │                                                             │
        │   DemoParser.Parse                                          │
        │     ├─ Pass 1: sequential header scan (LEB128, no allocs)   │
        │     ├─ Pass 2: parallel proto parse (Parallel.For + Snappy) │
        │     └─ Pass 3: sequential enrichment (events, players,      │
        │               schema, metadata, profile)                    │
        │                                                             │
        │   Output: ParsedDemo                                        │
        │     ├─ Frames: IReadOnlyList<DemoFrame>                     │
        │     │   └─ InnerMessages: NetMessage / GameEventMessage     │
        │     ├─ AllGameEvents                                        │
        │     ├─ Players, Schema, MapName, TickInterval, Profile…     │
        │     └─ (entity state is NOT populated here)                 │
        │                                                             │
        │   Opt-in: EntityTracking/EntityTracker                      │
        │     - Replays Frames forward                                │
        │     - Decodes svc_PacketEntities entity_data bit stream     │
        │     - Maintains EntitySet (16 384 slots × EntityState)      │
        └─────────────────────────────────────────────────────────────┘
                                       │
                                       ▼
   ┌──────────────────────────┐   ┌──────────────────────────────────┐
   │ CS2DemoKit.Analysis      │   │ Any other consumer               │
   │ DemoAnalyzer →           │   │ — iterates Frames / InnerMessages│
   │   DemoContext            │   │ — reads AllGameEvents            │
   │   StateGraphEvaluator    │   │ — drives EntityTracker itself    │
   │   rulesets (YAML)        │   │ — hex/byte views via             │
   │   EntityChangeScanner    │   │   DownstreamUtilities            │
   └──────────────────────────┘   └──────────────────────────────────┘
```

### Where the parser ends

The parser library has no UI references, no analysis-engine references, and
does not own a "current tick" or "live entity state." It is a pure
input-bytes-to-typed-records function — every output is a value type or
immutable list. Consumers maintain replay state if they need it.

Entity-state replay lives behind its own namespace boundary,
`CS2DemoKit.Parser.EntityTracking`, in
[`src/CS2DemoKit.Parser/EntityTracking/`](../src/CS2DemoKit.Parser/EntityTracking/).
It contains `EntityTracker`, `EntityState`, `EntitySet`, the `FieldDecoder`
family, the `FieldPath` family, and `HuffmanNode`. The line it draws is: the
parser owns "raw bytes → typed shapes," and entity replay is downstream of
that boundary.

`RuntimeSchema`, `RuntimeSerializer`, and `RuntimeField` sit on the parser side
of that line, in
[`src/CS2DemoKit.Parser/Entities/`](../src/CS2DemoKit.Parser/Entities/) under
the `CS2DemoKit.Parser.Entities` namespace — they're wire-format
schema-interpretation types, not replay-time state, and the parse pipeline
needs them to interpret `CSVCMsg_FlattenedSerializer` on its own.

`EntityTracker` is constructed explicitly by callers and never invoked by
`DemoParser.Parse`. That is the contract that matters: parsing (cheap,
parallelisable, deterministic) and replay (sequential, schema-dependent,
mutable) do not know about each other, and a caller that only wants frames and
game events never pays for replay.

---

## 2. The parse pipeline

Both `DemoParser.Parse` overloads funnel into `ParseCore`
([DemoParser.cs](../src/CS2DemoKit.Parser/DemoParser.cs)), which runs three
passes. The pass naming is taken verbatim from that method's own comment
banners.

### Pass 1 — sequential header scan

Walks the input bytes from offset 16 (after the `"PBDEMS2\0"` magic + 8 bytes
of fixed-offset fields) and decodes each frame's three-varint header
(`command`, `tick`, `size`) using `Leb128Utils.ParseFrameHeader`. For every
frame it records a `FrameDescriptor`:

| Field | Purpose |
|---|---|
| `RawStart` | Byte offset of this frame in the .dem buffer |
| `HeaderLength` | Bytes consumed by the three varints |
| `Command` | `EDemoCommands` with the compressed flag stripped |
| `Tick` | Frame tick (game tick in CS2) |
| `RawPayloadSize` | Compressed-or-not payload size |
| `IsCompressed` | True iff bit 0x40 was set in the raw command varint |
| `RawPayload` | **Zero-copy** `ReadOnlyMemory<byte>` slice of the caller's buffer |

This pass is sequential because each frame's position depends on the previous
frame's `Size`. It is intentionally allocation-minimal — only the
`List<FrameDescriptor>` grows. Initial capacity is estimated as
`Math.Max(64, data.Length / 250)` to avoid `List<T>` resizing during the scan.

Loop terminates on:
- `DEM_Stop` command (no payload follows),
- truncated header (`ParseFrameHeader` returns `-1`),
- truncated payload (next frame would extend past EOF).

The frame-size varint is validated; `size < 0` (i.e. >2 GB after sign cast)
raises `InvalidDataException`.

### Pass 2 — parallel proto parse

```csharp
DemoFrame[] results = new DemoFrame[frameDescs.Count];
Parallel.For(0, frameDescs.Count, i => {
    // decompress + parse proto + populate result slot
});
```

Each frame's protobuf decode is fully independent: there is no shared mutable
state, no cross-frame ordering requirement at this stage. The output array is
pre-sized to `frameDescs.Count`, so workers write to disjoint indexes without
locking.

The per-frame work:
1. **Snappy decompression** — performed here (in the worker) iff
   `IsCompressed`. `Snappy.DecompressToArray` is stateless and thread-safe.
   For uncompressed frames the payload is passed straight through as a
   zero-copy slice of the caller's buffer; no new heap buffer is allocated.
2. **ParseFrame** — wraps the payload in a single-segment
   `ReadOnlySequence<byte>` (a pure struct construction) and dispatches by
   `EDemoCommands` value to the appropriate generated `MessageParser<T>`.

Frame-type dispatch falls into three categories:

| Category | Examples | Handling |
|---|---|---|
| Direct-payload | `DEM_FileHeader`, `DEM_SendTables`, `DEM_ClassInfo`, `DEM_StringTables`, `DEM_FileInfo`, … | Whole payload is one proto message → wrapped in a single `NetMessage` with `DecompressedStart=0`. |
| Multiplexed | `DEM_Packet`, `DEM_SignonPacket` | Outer is `CDemoPacket`; inner `.data` is a bitstream of `(UBitVar typeId, UVarInt32 size, bytes payload)` triples. `ParseInnerMessages` walks the bitstream and emits one `NetMessage` per inner. |
| Checkpoint | `DEM_FullPacket` | Outer is `CDemoFullPacket`. Emits one `NetMessage` for the `CDemoStringTables` snapshot (entry 0), followed by inner messages parsed from the nested `CDemoPacket.data`. |

Inner-message ID dispatch is via `ParseNetMessage` — a single `switch` on
`typeId` covering `NET_Messages`, `Bidirectional_Messages`, `EBaseGameEvents`,
and `SVC_Messages`. A type ID the switch doesn't know is dropped from the
message list, but not silently: it is reported through the static
`DemoParser.OnUnknownMessageType` event, or per-parse (and without cross-talk
between concurrent parses) via `ParseOptions.OnUnknownMessage`. Set
`ParseOptions.CountDropSites` to have the drops tallied onto
`ParsedDemo.Warnings` instead.

A proto decode that throws is caught and yields `null`, so that slot drops out
of the list too — the pass never throws for a single bad inner message.

#### Allocation discipline

- Inner-message payload bytes are read into pooled `ArrayPool<byte>` rentals,
  passed to `Parser.ParseFrom`, then returned. Google.Protobuf copies all
  data out of the input during parse, so immediate return is safe.
- The proto name → string maps are `FrozenDictionary`s built once at class
  initialisation from `OriginalNameAttribute` reflection.
- `ReadOnlySequence<byte>` wrapping is a pure struct operation, no heap
  allocation.

### Pass 3 — sequential enrichment

`Enrich` walks the populated `results[]` in order, accumulating per-demo metadata and
promoting raw game-event slots to typed ones. Sequential because:

- Some messages (e.g. game events without a preceding `EventList`) cannot be
  decoded without earlier-in-the-stream context.
- `RuntimeSchema` is built lazily from the first `CDemoSendTables`.
- Player team assignments come from `PlayerTeamEvent`s and must be applied
  last-write-wins in tick order.

Specific transforms applied during enrichment:

| Message type | Effect |
|---|---|
| `CDemoFileHeader` | Populates `MapName`, `ServerName`, `ClientName`, `GameDirectory`, `BuildNum`, `ServerStartTick`, `PatchVersion`, `DemoVersionName`, `DemoVersionGuid`, `Addons`. Sets `eventDecoder.ServerStartTick` for `serverTick → gameTick` translation. |
| `CDemoFileInfo` | If `PlaybackTicks > 0`, this becomes the authoritative `TickCount` (otherwise falls back to max-tick-seen). |
| `CSVCMsg_ServerInfo` | Overrides `TickInterval` (default `1/64`); fills `MapName` when header was missing. |
| `CDemoSendTables` | Builds the `RuntimeSchema` (once) via `TryExtractSchema` → `BitBuffer` to strip size prefix → `CSVCMsg_FlattenedSerializer.Parser.ParseFrom`. |
| `CMsgSource1LegacyGameEventList` | Loads the per-event-id key schema into `GameEventDecoder`. |
| `CMsgSource1LegacyGameEvent` | Decoded to a typed `GameEvent` record; the `NetMessage` slot in the list is **replaced in place** with a `GameEventMessage` that carries both the raw payload and the decoded event. |
| `CDemoStringTables`, `CSVCMsg_CreateStringTable`, `CSVCMsg_UpdateStringTable` | Fed into `StringTableProcessor` which extracts players from the `userinfo` table. |

Post-pass fix-ups:
- `f.GameTick = f.ServerTick` for every frame. In CS2 demos the per-frame tick
  varint already IS the game tick; this alias exists for clarity in downstream
  code.
- `PlayerInfo.Team` is filled in from the last `player_team` event per
  controller slot.
- `DemoProfile` is computed via `DemoSourceClassifier.Classify` from the
  header strings unless a `profileOverride` was passed to `Parse`.

### Zero-copy slicing of input bytes

The caller controls the lifetime of the demo buffer. `DemoParser.Parse` takes
`ReadOnlyMemory<byte>` and the entire pipeline reads through it without
copying for uncompressed payloads. For compressed payloads, Snappy
decompression allocates a fresh `byte[]` per frame (worker-local; collected
after `ParseFrame` returns).

The `PayloadStart` / `PayloadLength` / `RawStart` / `RawLength` fields on
`DemoFrame` are byte offsets into the original demo buffer. Callers can hex-
dump a frame via `demoBytes[frame.RawStart .. frame.RawStart + frame.RawLength]`
without re-parsing.

`DownstreamUtilities.GetDecompressedPayload(frame, demoBytes)`
([DownstreamUtilities.cs](../src/CS2DemoKit.Parser/DownstreamUtilities.cs))
is the on-demand decompressor: it accepts the original raw bytes (the parser
does not retain them) and either Snappy-inflates the slice or returns a copy
of the uncompressed bytes. A byte-level viewer uses this to produce a frame's
payload lazily, only for the frame it is showing, instead of the parser
retaining every decompressed buffer.

`DownstreamUtilities.ExtractInnerMessageBytes(frame, decompressedPayload)`
is the dual: given a decompressed payload, walk the inner-message bitstream
and return one `byte[]?` per inner message.
`ExtractInnerMessageBytesAligned` is the byte-aligned variant, and
`ExtractInnerMessageSlices` returns `(TypeId, Bytes)` pairs rather than a
positional array.

### When Snappy decompression happens

| Stage | What | Why |
|---|---|---|
| Pass 1 | Never | Headers carry compression flag but no payload work. |
| Pass 2 | Compressed frames only, in worker thread | Independent and parallel — perfect work to push off-main. |
| On demand | `DownstreamUtilities.GetDecompressedPayload` | Consumers re-inflate one frame at a time, so the parser doesn't retain ~1 GB of decompressed buffers. |

---

## 3. The output types

### `ParsedDemo`

[`ParsedDemo.cs`](../src/CS2DemoKit.Parser/ParsedDemo.cs) — the
top-level immutable output. Constructed only by `DemoParser.Enrich`. Fields:

| Member | Description |
|---|---|
| `Frames` | All `DemoFrame`s in recording order. |
| `AllGameEvents` | Flat tick-ordered list of decoded `GameEvent`s. |
| `Players` | `IReadOnlyDictionary<int, PlayerInfo>` keyed by **player slot** (0–63) — the controller entity index, which is also the `userid` field in game events and the slot-shaped fields on typed event records. |
| `Schema` | `RuntimeSchema?` from the first `DEM_SendTables` (null if absent). |
| `MapName`, `TickCount`, `TickInterval`, `TickRate`, `Duration` | Match-level metadata. |
| `ServerName`, `ClientName`, `GameDirectory`, `BuildNumber`, `ServerStartTick`, `PatchVersion`, `DemoVersionName`, `DemoVersionGuid`, `Addons` | Raw header fields. |
| `Profile` | `DemoProfile` (auto-classified or override). |
| `Warnings` | `IReadOnlyList<ParseWarning>` — non-fatal structured diagnostics from a damaged or unusual demo, capped at 256 per parse. |

`TickRate` is derived as `Round(1 / TickInterval)`. `Duration` is
`TickCount × TickInterval` as a `TimeSpan`.

### `DemoFrame`

[`DemoFrame.cs`](../src/CS2DemoKit.Parser/DemoFrame.cs) — one
top-level `EDemoCommands` entry. Fields are all `required init` (immutable
after construction except `GameTick`, which is set in pass 3):

| Member | Description |
|---|---|
| `Command` | Proto name like `"DEM_Packet"`. |
| `ServerTick` | Game tick in CS2 (see naming note below). |
| `GameTick` | Alias for `ServerTick`; populated in pass 3. |
| `FrameNumber` | Zero-based index in `ParsedDemo.Frames`. |
| `RawStart`, `RawLength` | Byte offsets in the raw `.dem` buffer (covers header + payload). |
| `HeaderLength`, `PayloadStart`, `PayloadLength` | Byte offsets/lengths within the frame. |
| `IsCompressed` | True iff payload was Snappy-compressed on disk. |
| `InnerMessages` | Read-only view of `NetMessage` sub-components. |

#### Inner-message shape per `Command`

| `Command` | Inner messages |
|---|---|
| `DEM_SyncTick`, `DEM_Stop` | Empty. |
| `DEM_FileHeader`, `DEM_SendTables`, `DEM_ClassInfo`, `DEM_StringTables`, `DEM_FileInfo`, `DEM_UserCmd`, `DEM_ConsoleCmd`, `DEM_CustomData`, … | One entry; `MessageTypeName == Command`; `DecompressedStart == 0`. |
| `DEM_Packet`, `DEM_SignonPacket` | The multiplexed `NET_/Bi/SVC/GE` net messages from `CDemoPacket.data`. |
| `DEM_FullPacket` | Entry 0 is the `CDemoStringTables` snapshot, entries 1..N are the nested `CDemoPacket.data` messages. |

#### Naming gotcha — `ServerTick`

Despite the name, `DemoFrame.ServerTick` is the **game tick** (gameplay
starts at 1). Pre-recording frames use a single large negative sentinel
(`-1 - server_start_tick`). The frame header's wire-format `0xFFFFFFFF`
sentinel decodes to `-1` via the `Tick = (int)tick` cast in `FrameHeader`.

The `GameTick` alias was added so downstream code can be explicit without
breaking the existing API. They always have the same value; either is fine.

### `NetMessage` and `GameEventMessage`

[`NetMessage.cs`](../src/CS2DemoKit.Parser/NetMessage.cs)

```csharp
public class NetMessage {
    public string  MessageTypeName     { get; init; }   // "svc_PacketEntities", etc.
    public IMessage Payload             { get; init; }  // typed Google.Protobuf message
    public int?    DecompressedStart    { get; init; }  // for hex highlighting
    public int?    DecompressedLength   { get; init; }
}
```

Not `sealed`: `GameEventMessage` and the Analysis layer's
`EntityChangeMessage` both extend it.

`MessageTypeName` is the **lowercase snake_case** proto name (e.g.
`"svc_PacketEntities"`, not `"CSVCMsg_PacketEntities"`). Callers that group or
colour messages by family prefix-match on `net_/svc_/cs_/DEM_/CDem`, so the
raw proto name is what they expect to find here.

`Payload` is non-generic `IMessage` because the list holds heterogeneous
types. Cast or pattern-match to specific generated proto types as needed.

`DecompressedStart` / `DecompressedLength` are **byte-approximate** offsets
within the decompressed frame payload, intended for hex highlighting. For
multiplexed packet frames the inner-message bitstream is not byte-aligned, so
the start can drift by ±1 byte from the true bit position. See the comments
on `NetMessage.DecompressedStart` for the exact semantics.

[`GameEventMessage.cs`](../src/CS2DemoKit.Parser/GameEvents/GameEventMessage.cs)
extends `NetMessage` with a single extra property: `GameEvent DecodedEvent`.
Every `CMsgSource1LegacyGameEvent` slot in `DemoFrame.MessageList` is
**replaced in place** during pass 3 with a `GameEventMessage` instance.
Callers can pattern-match:

```csharp
foreach (var msg in frame.InnerMessages) {
    if (msg is GameEventMessage gem) {
        switch (gem.DecodedEvent.Payload) {
            case PlayerDeathEvent pd: ...;
            case WeaponFireEvent  wf: ...;
        }
    }
}
```

### `GameEvent`

[`GameEvent.cs`](../src/CS2DemoKit.Parser/GameEvents/GameEvent.cs)
is a **non-generic envelope**: the per-fire transport context, plus the typed
payload record the SDK materialised for it.

```csharp
public record GameEvent(
    string Name, int EventId, int FrameNumber,
    int ServerTick, int GameTick, object? Payload = null);
```

The payload records (`PlayerDeathEvent`, `WeaponFireEvent`, `PlayerHurtEvent`, …)
ship in the **`CS2OpenDev.Sdk.GameEvents`** package under the
`CS2OpenSchema.Events` namespace. Nothing is generated here — a new upstream
event arrives by bumping the package version.

`Payload` is `object?` rather than generic because a demo's event stream is
heterogeneous and has to sit in one list. (The SDK's own `GameEventEnvelope<T>`
is generic, which is why we don't use it directly.) Pattern-match to reach a
specific event, as in the snippet above.

**Synthesized events** carry a `null` payload and instead subclass `GameEvent`
directly, declaring their own fields — these are fires the Analysis layer
derives from entity state rather than from the wire, e.g.
`MolotovThrownEvent`.

Anything consuming an event has to handle both shapes. The rule throughout the
codebase is `Payload ?? fire`: the payload is the subject for a wire event, the
fire itself for a subclass. That single expression is what dispatch keys, type
gates, and compiled rule expressions all key on.

Events whose `EventId` has no schema entry fall back to `UnknownGameEvent`
carrying a `Dictionary<string,object>` of decoded fields.

The decoder ([`GameEventDecoder.cs`](../src/CS2DemoKit.Parser/GameEvents/GameEventDecoder.cs))
extracts CS2-specific key types:
- Type 8 = entity/pawn handle (32-bit, in `val_long`),
- Type 9 = controller slot index (16-bit, in `val_short`).

Both are absent from the CS:GO proto spec; CS2 still emits them.

### No display tree

The parser stops at typed `IMessage` payloads. It does not build a
display-oriented node tree over them — a consumer that wants one (a payload
inspector with per-field byte ranges, say) walks the `IMessage` itself at
display time and uses `DownstreamUtilities.Scan` to recover the wire byte
ranges. That is a deliberate boundary: byte-range annotation is presentation,
and presentation is not this library's job.

The parser's own `Models/` directory holds only `SubTickEvent.cs`,
`SubTickExtractor.cs`, `TickGroup.cs` — see
[`Models/SubTickExtractor.cs`](../src/CS2DemoKit.Parser/Models/SubTickExtractor.cs).

---

## 4. Bit-level primitives

Three load-bearing files implement the wire-format reading. A one-bit mistake
in any of them corrupts everything downstream, so change them with care and
run the parser suite afterwards.

### `Leb128Utils`

[`LEB128Utils.cs`](../src/CS2DemoKit.Parser/LEB128Utils.cs) —
allocation-free ULEB128 utilities. The hot path is
`ParseFrameHeader(ReadOnlySpan<byte>, out FrameHeader)` which fully unrolls
the three frame-header varints into one method with a single bounds check at
the top. When ≥5 bytes remain, the JIT eliminates the per-byte bounds checks;
near the tail it falls back to `DecodeVarintSlow`.

Single-byte varint values (0–127, the common case) take a 2-line fast path
on every entry point (`TryReadUInt32`, `TryReadUInt64`, `TrySkip`). The
multi-byte cores are `NoInlining` to keep call sites compact.

Used by:
- `DemoParser` for frame headers + the byte-form `FindBytesField` helper.
- `DownstreamUtilities.Scan` for top-level field scanning.

### `FrameHeader`

[`FrameHeader.cs`](../src/CS2DemoKit.Parser/FrameHeader.cs) — a
`readonly struct` that holds `(Command, Tick, Size, IsCompressed)`. The
compressed flag (bit `0x40`) is stripped from the raw command varint at
decode time so the rest of the pipeline sees a clean `EDemoCommands`
value. Constructed only via `Leb128Utils.ParseFrameHeader`.

### `BitBuffer`

[`BitBuffer.cs`](../src/CS2DemoKit.Parser/BitBuffer.cs) — a
`ref struct` bit-level reader over a `ReadOnlySpan<byte>`. Adapted verbatim
from demofile-net (MIT) with minor changes:
- Namespace adjusted,
- `Read3BitNormal` returns `System.Numerics.Vector3` (not Source's `SDK Vector`),
- `ReadBytes(int)` overload added.

Used pervasively in the entity-state layer (path-op Huffman reads, per-field
bit decoders) and by `DemoParser` for the inner-message bitstream
multiplexer. Bit-level correctness here is critical — every value type in
CS2's flattened serializer reads a specific number of bits, and a one-bit
misalignment cascades into garbage.

### `DownstreamUtilities`

[`DownstreamUtilities.cs`](../src/CS2DemoKit.Parser/DownstreamUtilities.cs)
is the stable convenience-API surface for consumers that need to extract or
display the parser's intermediate bytes. None of it is used by the parse
pipeline itself.

It bundles:

- `GetDecompressedPayload(frame, demoBytes)` — Snappy-inflates a frame's
  payload on demand from the raw bytes the caller still owns.
- `ExtractInnerMessageBytes(frame, decompressedPayload)` — re-walks the
  inner-message bitstream to return one `byte[]?` per inner message.
- `Scan(byte[])` — scans the top-level fields of a protobuf-encoded byte
  array **without** deserialising the values, returning a `FieldSpan`
  (field number, wire type, start, end) per top-level field occurrence.
  Does not recurse into nested messages — re-invoke it on a sub-message's
  bytes to descend one level.
- `TryGetPayloadRange` / `TryReadFixed32Value` / `TryReadFixed64Value` /
  `TryReadVarintValue` — sibling helpers for slicing out the value bytes
  of a `FieldSpan`.
- `TryReadQuickInfo` — pulls map/server/client name and demo version out of a
  file (or just its leading bytes) without a full parse.

The scanner is what lets a consumer compute exact byte ranges: given a field
path through a decoded proto, walk back to the wire bytes and produce a
`(start, length)` range. Byte-range correctness is load-bearing for
visually-driven debugging.

---

## 5. The entity-state layer

Entity state is decoded by a separate, opt-in layer:
[`src/CS2DemoKit.Parser/EntityTracking/`](../src/CS2DemoKit.Parser/EntityTracking/).
`DemoParser.Parse` does not run it; nothing in the base parse pipeline depends
on it. Callers instantiate `EntityTracker` themselves (via
`EntityTrackerFactory.CreateCurated()`, which binds the Schema Lens resolver)
and feed it the frames.

### Why separate from the main parse

1. **Cost.** Entity decoding is sequential by nature (every delta depends on
   the previous state of the entity) and CPU-intensive (each entity carries
   tens to hundreds of bit-level fields per tick). Forcing it into the parse
   path would block parallel decode of independent frames.
2. **Consumer choice.** The analysis engine sometimes needs it
   (`DemoAnalyzer.BuildContext`) and sometimes doesn't
   (`BuildEventContext`, the fast path for event-only rules). An interactive
   viewer only needs it for the frame it is showing; a batch tool that only
   wants game events never needs it at all.
3. **Fragility isolation.** Entity decoding is the part that can go wrong on
   an unusual recording — see
   [the POV-demo account below](#pov-recordings-and-delta-on-unknown). Keeping
   it out of the base pipeline means game-event-derived stats (player_death,
   player_hurt, weapon_fire, …) remain correct regardless of entity-decode
   success.

### `RuntimeSchema` / `RuntimeSerializer` / `RuntimeField`

The schema describing every networked entity class. Parsed from
`CSVCMsg_FlattenedSerializer` (embedded in `CDemoSendTables`, size-prefixed:
read a uvarint, then `ParseFrom` the next N bytes).

| Type | Purpose |
|---|---|
| [`RuntimeSchema`](../src/CS2DemoKit.Parser/Entities/RuntimeSchema.cs) | Top-level: symbol table + `(name, version) → RuntimeSerializer`. `GetSerializer(name)` is the lookup entry point. |
| [`RuntimeSerializer`](../src/CS2DemoKit.Parser/Entities/RuntimeSerializer.cs) | One entity class. Carries `Name`, `Version`, `Fields[]`. |
| [`RuntimeField`](../src/CS2DemoKit.Parser/Entities/RuntimeField.cs) | One field in a serializer: `TypeName`, `Encoder`, `BitCount`, `Low/High`, `EncodeFlags`, `ChildSerializerName`, `PolymorphicTypes`, `SendNode`, and a derived `FieldShape` (Atomic / Ptr / PolymorphicPtr / Vector / FixedArray / PlainStruct). |

Schema parsing is a three-pass operation
([`RuntimeSchema.Parse`](../src/CS2DemoKit.Parser/Entities/RuntimeSchema.cs)):
all leaf `RuntimeField`s built first, then `RuntimeSerializer`s indexed,
then `ResolveChildSerializer` is called on every field so child references
resolve into actual `RuntimeSerializer` instances.

**Note on schema duplication.** The base parser's enrichment pass also calls
`TryExtractSchema` to populate `ParsedDemo.Schema`.
`EntityTracker.ProcessSendTables` performs the same decoding when it sees the
same `CDemoSendTables` frame. This double-build is intentional: it keeps the
parser stateless of the entity replay (which makes the parse pass cleanly
parallel) at the cost of parsing the schema proto twice when both are active.

### `EntityTracker`

[`EntityTracker.cs`](../src/CS2DemoKit.Parser/EntityTracking/EntityTracker.cs)
— the stateful replay engine, covering schema management, class registry,
instance baseline decoding, field-descriptor building, field-path Huffman
decode, per-field bit decode, the SDK typed-wrapper dispatch, and the
decode-failure diagnostic instrumentation.

State held:
- `Schema` — `RuntimeSchema?` (built on first `CDemoSendTables`).
- `_classIdToName` — `Dictionary<int, string>` (built from `CDemoClassInfo`;
  exposed read-only as `ClassIdMap` / `AvailableClasses`).
- `_serverClassBits` — `(int)Math.Log2(serverInfo.MaxClasses) + 1`; the number
  of bits used on the wire to encode a class ID. **Only**
  `CSVCMsg_ServerInfo` writes this; `CDemoClassInfo` and `CSVCMsg_ClassInfo`
  intentionally do not (there is a comment on the field saying so).
- `_instanceBaselines` — `Dictionary<int, byte[]>` (per-class baseline blobs
  from the `instancebaseline` string table).
- `_fieldDescs` — `Dictionary<string, List<FieldDescriptor>>` (cached
  per-class compiled decoder lists; built lazily on first entity-create).
- `CurrentEntities` — `EntitySet`, the live entity table (see below).
- `CurrentTick`, `CurrentFrameIndex` — last-processed positions.
- `LastEntityError` — most recent decode-failure ToString (null if healthy).
- `DeltaUnknownCount` — diagnostic counter of delta-on-unknown-entity events;
  non-zero signals a POV-style stream.

#### Seek modes

| Method | Behaviour | Use case |
|---|---|---|
| `Replay(frames)` | Process every frame in order. | Full-replay stats build (`DemoAnalyzer.BuildContext`). |
| `ReplayTo(targetTick, frames)` | Process all frames with `tick <= targetTick`. | Tick-keyed seeking (rare; can hit multiple frames sharing a tick). |
| `ReplayToIndex(frameIndex, frames)` | Process frames `[0..frameIndex]` inclusive. | Frame-accurate seeking — preferred over `ReplayTo` because DEM_FullPacket frames can share ticks. |
| `AdvanceOneFrame(frame)` | Process exactly one frame. | Forward walks that already own the cursor. |
| `ReplayToIndexWithSnapshot(snapshotAt, frameIndex, frames)` | Advance to `snapshotAt`, snapshot all fields, then continue to `frameIndex`. | Pre-event reads (e.g. preHitHp before damage is applied). |
| `PeekEntityUpdates(msg)` | Read-only decode of a single `CSVCMsg_PacketEntities` returning a `List<EntityUpdateInfo>` without mutating `CurrentEntities`. | "Show me the entity diff in this packet" without rewinding. |
| `SnapshotCurrentFields()` | Deep-copy the entire `EntitySet` to `Dictionary<int, Dictionary<string, object?>>`. | Anywhere a non-mutating live view is needed. |

Note that `ReplayTo` and `ReplayToIndex` replay from frame 0 on **every**
call — they are seeks, not steps. A forward walk should use `AdvanceOneFrame`,
or `EntityStateLayer` in `CS2DemoKit.Analysis`, which keeps its own cursor and
replays only the frames between where it is and where you asked for.

`Replay`, `ReplayTo`, and `ReplayToIndex` all funnel into `ProcessFrame`
which iterates `frame.InnerMessages` and dispatches each `Payload` to the
right handler.

#### Important behaviour: `DEM_FullPacket` is a checkpoint, not new data

`ProcessFrame` **skips** `CSVCMsg_PacketEntities` inside `DEM_FullPacket` frames because
those packets re-deliver state we have already received from prior
`DEM_Packet` frames. Replaying them double-creates entities and cascades
into bit-misalignment ~5 packets later.

### `FieldPath` and `FieldPathEncoding`

CS2 entity deltas address fields by a **path** through the (nested) field
tree of an entity class. The path is a sequence of integers, capped at
**7 entries** ([`FieldPath.cs`](../src/CS2DemoKit.Parser/EntityTracking/FieldPath.cs)):

```csharp
private int _path0; ... private int _path6;  // 7 slots, struct-inlined
```

A 7-slot cap is what demofile-net uses (verified against its `FieldPath.cs`).
7 is sufficient for every observed real CS2 entity; an 8th-slot push
indicates upstream bit misalignment.

[`FieldPathEncoding.cs`](../src/CS2DemoKit.Parser/EntityTracking/FieldPathEncoding.cs)
defines **40 path-mutation opcodes** (`PlusOne`, `PushOneLeftDeltaZeroRightZero`,
`PopAllButOnePlusOne`, `NonTopoComplex`, `FieldPathEncodeFinish`, etc.) with
empirical frequencies. The decoder is a pre-built Huffman tree
([`HuffmanNode.cs`](../src/CS2DemoKit.Parser/EntityTracking/HuffmanNode.cs)):
each op is read bit-by-bit until a leaf, then its `Reader` mutates the path
in place. The tree is built once at static-class init.

`FieldPathEncodeFinish` (frequency 25 474, second only to `PlusOne` at 36 271)
has a `null` Reader — that's the sentinel meaning "no more field paths in this
entity".

### `FieldDecoder` and `FieldDecoderFactory`

[`FieldDecoder.cs`](../src/CS2DemoKit.Parser/EntityTracking/FieldDecoder.cs)
defines three delegate shapes:

```csharp
internal delegate object?  FieldDecoder(ref BitBuffer buffer);  // boxed
internal delegate int      IntDecoder(ref BitBuffer buffer);    // unboxed
internal delegate float    FloatDecoder(ref BitBuffer buffer);  // unboxed
```

[`FieldDecoderFactory.cs`](../src/CS2DemoKit.Parser/EntityTracking/FieldDecoderFactory.cs)
inspects a `RuntimeField` and returns the right decoder. `TryCreateInt` /
`TryCreateFloat` are tried first for scalar types to avoid boxing on the
hot decode path; everything else falls back to the boxed `Create`.

The factory handles every CS2 wire-encoded scalar type:
- Integers — `bool`, `uint8`, `int32`, `CEntityIndex`, `CUtlStringToken`,
  `CGlobalSymbol`, `CPlayerSlot`, `GameTick`, etc.
- Floats — `float32`, `GameTime` (raw 32-bit), `CNetworkedQuantizedFloat`
  (bc<32 quantised, bc≥32 raw — the raw case is easy to get wrong and
  produces plausible-looking garbage when you do).
- Complex (boxed) — strings, `Vector`/`QAngle`, `Color`, encoder-specific
  paths (`coord`, `simtime`, `runetime`).

### `EntitySet` and `EntityState`

[`EntitySet.cs`](../src/CS2DemoKit.Parser/EntityTracking/EntitySet.cs) —
a fixed `EntityState?[16384]` (matching Source's `MAX_EDICTS = 1 << 14`).
Enumeration helpers: `All()`, `AllInPvs()`, `OfClass(className)`,
`AllIndexed()`, `AllInPvsIndexed()`, `Snapshot()`.

`GetOrCreate(index, className, serial)` reuses an existing slot **only when
the class name matches** — a slot reused with a different class would point
at the wrong serializer and silently misalign the wire bit stream.

[`EntityState.cs`](../src/CS2DemoKit.Parser/EntityTracking/EntityState.cs)
holds one entity's networked fields. Storage is three typed **lane arrays**
plus a fallback dictionary, so the common scalar reads never box:

```csharp
int[]?    _intLane;      // int-shaped fields
float[]?  _floatLane;    // float-shaped fields
object?[] _objectLane;   // everything else (strings, vectors, handles)
Dictionary<string, object?>? _fallback;   // fields with no planned slot
```

`Fields` (the public read API) merges the lanes and the fallback into a fresh
dictionary on each call — display-only, never use on hot paths. `Get<T>` /
`TryGet<T>` / `this[path]` are the cheap typed readers. §5b covers how a field
gets its lane and slot.

Empirical field-storage conventions:
- Handles arrive as `UInt64`,
- Bools as `Int32` (0/1),
- Sub-entities flattened under `m_pXxxServices.m_yyy`,
- Arrays use `[N]` not `.NNN`.

### POV recordings and delta-on-unknown

A POV (client-recorded) demo differs from a GOTV/HLTV relay stream in a way
that matters here: the wire sends DELTAS for entities the recording client
never received an `ENTERPVS` for. The tracker hits `state is null` on those
deltas and skips with `continue`, which consumes only the prelude bits
(`UBitVar entityIndex` + 2 flags = 8–16 bits) and not the path-op + value bits
the wire encoded for that entity.

That skip is the pragmatic choice, not a proof of safety — it is the reason
the counter exists:

- **`EntityTracker.DeltaUnknownCount`** — public counter of delta-on-unknown
  events across the whole replay. Zero on a relay stream; large on a POV
  recording. Read it before trusting per-tick entity values from a demo you
  did not classify.
- **`EntityTracker.LastEntityError`** — non-null means the decoder threw and
  stopped. Always check it after a replay.

demofile-net takes the other branch and `throw`s on delta-on-non-existent, so
it never produces a partially-decoded POV replay at all.

**What is unaffected either way:** every typed game event is decoded by
`GameEventDecoder` from the `CMsgSource1LegacyGameEvent` proto stream, which
does not depend on entity tracking. `ParsedDemo.AllGameEvents` is always
populated, so any stat derived from kills, deaths, damage or shots is correct
regardless of what the entity decoder did.

**What is at risk if decode does go wrong:** per-tick HP / armor / weapon /
position reads off `EntityTracker.CurrentEntities`, and anything computed from
them.

**Diagnosing it.** Set `Tracing.Enabled` (or `CS2DEMOKIT_TRACE_DECODE=1`) and
re-run: the decoder keeps a per-packet trace of every path-op and field read
with bit positions, and on the first `LastEntityError` it dumps the tail of
that trace plus an "outliers" view ranking reads by
`|bitsConsumed − expectedBits|`. Decode is deterministic in the demo bytes, so
the failure reproduces exactly. Trace output goes to
`EntityTracker.DecodeDiagnosticSink` (an `Action<string>`, `Console.WriteLine`
by default) — redirect or silence it per tracker in a batch service.

---

## 5b. Schema Lens — the typed-wrapper layer

Sitting on top of the entity-state layer is **Schema Lens** — a wire-stable
mapping between volatile CS2 engine field names and a stable C# Tier-3 wrapper
API.

### The 3-tier mapping

```
[ Tier 1: bitstream ]  svc_PacketEntities.entity_data
            │                (Huffman path ops + per-field bit decode)
            ▼
[ Tier 2: lane/slot bridge ]  EntityState lanes
            │   • _intLane     : int[]
            │   • _floatLane   : float[]
            │   • _objectLane  : object?[]
            │   • _intSeen / _floatSeen / _objectSeen bitvectors
            │   • _fallback   : Dictionary<string, object?>
            ▼
[ Tier 3: typed wrappers ]   CCSPlayerPawn, CCSPlayerController, …
                (live views over the lanes)
```

The runtime owns Tier 1 (`EntityTracker` + decoder family, §5 above) and
Tier 2 (`EntityState` lanes, this section). Tier 3 is the **SDK-emitted typed
wrappers** (`CS2OpenDev.Sdk.Entities`, bound over the runtime through the
[`Entities/SdkAbstractions/`](../src/CS2DemoKit.Parser/Entities/SdkAbstractions/)
seam). The only codegen-emitted file on this side is the Schema Lens registry,
`Entities/Generated/SchemaLens.Generated.cs`.

### Architectural steer: lanes are mutable truth, `Fields` is projection

Each lane has a parallel `_seen[]` bitvector so the projection can distinguish
"lane default 0/0.0/null" from "not received yet" — the seen-tracking contract
that lets snapshot diffs ignore unwritten cells.

The public `EntityState.Fields` API (an
`IReadOnlyDictionary<string, object?>`) is a **computed projection**: on every
call it materialises a fresh dictionary by merging the lane cells whose
`_seen[]` bit is set with the `_fallback` dict, keyed on the per-class path
map. Every consumer that reads `state.Fields["…"]` depends on that projection
producing exactly the wire-name keys a plain dictionary would have — the
Analysis suite's `SchemaKeysAssertionTests` is what holds that bar. The
projection is display-only, never used on hot paths.

Hot-path readers go through the path-keyed API:

```csharp
state.Get<int>("m_iHealth");     // routes to int lane via slot map
state.TryGet<float>("m_flStamina", out var s);
state["m_hController"];          // returns raw boxed handle
```

Typed wrapper reads bypass the path lookup entirely. The SDK contract addresses
fields by **ordinal**; `LensOrdinalMap` translates
`contract ordinal → Lens canonical path → (lane, slot)` once per (binding,
shape) pair at class-bind time, and `LensBoundReader` then reads through
`state.GetIntSlot(slot)` / `GetFloatSlot(slot)` / `GetObjectSlot(slot)` with
nothing but two array indexes on the read itself.

The subtlety that map exists for: `ClassShape.PathToSlot` is keyed by the
**wire** spelling of the demo being replayed (the `Fields` projection has to
reproduce wire-name keys), while contract ordinals are keyed by the
**canonical** Lens spelling. Each ordinal resolves through a candidate list —
canonical path first, then every historical spelling aliased to it — and the
first candidate the shape knows wins. A current demo hits the canonical
spelling; a pre-rename demo hits the alias; either way the ordinal reads the
field. An ordinal none of whose candidates appear in the shape resolves to
`SlotAddr.Fallback` and is probed against the entity's fallback dictionary at
read time.

### Descriptor walk consults the Lens at first sighting

`EntityTracker.BuildFieldDescs` walks the `RuntimeSchema` spine the first
time it sees a serializer and produces the `FieldDescriptor` tree the
decoder uses. The Schema Lens hooks in here in two places:

1. **Pre-pass slot reservation.** Before any descriptor `Allocate(...)` runs,
   `EntityTracker.PreReserveLensSlots` walks the same spine and reserves
   every Lens-pinned slot via `ClassShapeBuilder.ReserveLensSlot`. This
   answers the auto-increment-vs-pin collision that would otherwise let a
   freshly-walked descriptor steal a slot the codegen already published as
   the canonical home for `m_iHealth`. The auto-increment branch skips
   reserved slots.
2. **Per-field LensSlot lookup.** During the walk, each leaf descriptor
   consults the tracker's `LensResolver` (a
   `LensSlotRule? (string serializerName, string enginePath)` delegate,
   installed by `BindLensResolver` — which is what
   `EntityTrackerFactory.CreateCurated()` does for you). If the resolver
   returns a rule, the descriptor takes the rule's slot; otherwise it falls
   back to the auto-incrementing lane assignment. Unmapped fields go to the
   `Fallback` lane (the dict path), preserving every consumer that reads via
   `state.Fields["…"]`.

The cost of the Lens consult is paid once per (class, field) pair on first
sighting and cached in the per-class `ClassShape`.

### Codegen → runtime contract

The codegen output and the runtime tracker meet at three surfaces:

| Surface | Owner | Notes |
|---|---|---|
| `GeneratedLensRegistry.Load()` → `LensState` | Codegen (`Entities/Generated/SchemaLens.Generated.cs`) | A pure in-memory reconstruction — no JSON parsing, no file I/O — of every aliasing + slot decision. |
| `GeneratedLensRegistry.LensHash` | Codegen | sha256 over the canonical-form `LensState`, compared against a runtime-recomputed hash. Mismatch ⇒ regenerate. |
| `LensResolverBridge.Build(LensState)` | Entities-side bridge | Returns the `Func<string, string, LensSlotRule?>` the runtime calls, so `EntityTracker` never has to name `LensState` directly. |

Slot planning runs inside the `--schemalens` emit; `PreReserveLensSlots`
consumes the resulting lens state directly. There is no per-class emitted
slot-constant file.

### Lane routing: honour the wire

A Lens rule declares a lane, but the rule does not get the final say. The
descriptor walk routes a field to the lane its **wire decoder naturally
produces**, and only takes the declared lane when the two agree or when the
rule carries an explicit coercion transform (`CastToInt` / `CastToFloat` /
`CastToUInt64`). The reason is precision: a `uint64`-wire `m_steamID` declared
as the int lane would silently lose its top 32 bits on every read.

`LensTransform.HandleIndex` is the case that most looks like an exception and
isn't. It is declared on handle fields, but for lane purposes it is treated as
identity — CS2 networks handles as varint `UInt64` / `UInt32`, which decode via
the raw-uint64 path and therefore land boxed on the object lane, and
`Fields["m_hController"]` keeps returning that raw boxed integer. The masking,
index/serial split and sentinel checks all happen later, in the typed wrapper's
getter. The seam reader in between,
`LensBoundReader.TryReadEntityHandle`, only width-folds to `uint` with
unchecked casts — no mask, no split, no sentinel interpretation, so the raw
packed value crosses the seam undecoded.

### Bootstrap pattern

The consumer at the top of the wire is responsible for wiring the runtime to
its Tier-3 face. In this repo that is Analysis. The pattern, applied once per
`EntityTracker`:

```csharp
var tracker = EntityTrackerFactory.CreateCurated();   // binds the Schema Lens resolver
SdkEntityWorlds.For(tracker);                         // (Analysis layer) registers the SDK
                                                      // wrapper factories for Get<T>/Resolve<T>
```

Factory registration goes through `TrackerEntityWorld.RegisterWrapper`, which
installs the SDK package's own `EntityWrapperRegistry` factories — one factory
per class, replacement-on-reregister. After registration, the four public
Tier-3 APIs on `EntityTracker` light up:

| API | Returns | Semantics |
|---|---|---|
| `Get<T>(int slot)` | `T?` | Live view; every read re-resolves through the current `EntityState`. |
| `Snapshot<T>(int slot)` | `T?` | Wrapper over a detached `FreezeCopy` — scalar reads are frozen; handle companions resolve live through the wrapper's world. |
| `ResolveHandle<T>(int rawHandle)` | `T?` | Masks, sentinel-checks, dereferences via `CurrentEntities[index]`. |
| `GetFieldMeta(string className, string path)` | `RuntimeField?` | For tools / diagnostics. |

`Get<T>` is constrained `where T : class` deliberately: the tracker never
names a wrapper base type (production factories produce
`CS2OpenDev.Sdk.Entities.EntityWrapper` subclasses the tracker cannot
reference). The factory registry handles the cast.

### Known limits of the lane path

- **Snapshots are one entity deep.** `Snapshot<T>` / `SnapshotNode` freeze a
  single entity (a detached `FreezeCopy` plus a frozen `Fields` clone). There
  is no recursive nested-handle freeze — SDK wrappers resolve handles live
  through `IEntityWorld` instead, so a handle followed off a snapshot reads
  current state, not frozen state.
- **Arrays are not lane-backed.** Array elements keep the per-element
  fallback-dict path: `m_pWeaponServices.m_hMyWeapons[i]` reads go through
  `state.Fields`, bracket-indexed, not through a slot. The end state is a
  single object-lane slot holding a typed `IReadOnlyList<TElement>`; that is
  not what happens today.
- **The lens covers a curated class set, not the whole entity zoo.** The
  registry pins 61 classes — the player pawn and controller, game rules, teams,
  the weapon and grenade hierarchies down to concrete classes (`CAK47`,
  `CWeaponAWP`, `CMolotovProjectile`, …). Anything outside that set decodes
  fine; its fields just land in the fallback dictionary rather than a lane.

---

## 6. Downstream consumers

### An interactive demo inspector

Worth spelling out, because it exercises the parts of the API that a batch
consumer never touches. A frame-browser / payload-inspector application over
this library looks like:

1. **Parse** — `DemoParser.Parse(bytes.AsMemory())` on file open, then bind
   `parsed.Frames` directly; frames are immutable, so the list is safe to hand
   to a view.
2. **Per-message byte ranges** — on frame selection, call
   `DownstreamUtilities.GetDecompressedPayload(frame, demoBytes)` to inflate
   that one frame, then `ExtractInnerMessageBytes(frame, payload)` (or the
   `…Aligned` variant) to get one `byte[]?` per inner message. This is why the
   parser does not retain decompressed payloads: the consumer re-inflates on
   demand, one frame at a time.
3. **Entity tracking on selection** — own a single `EntityTracker` and advance
   it with `ReplayToIndex(frameIndex, frames)` as the selection moves; use
   `ReplayToIndexWithSnapshot` to capture pre-frame state, and
   `PeekEntityUpdates` to show the entity diff carried by one packet without
   disturbing the live set.
4. **Hex highlighting** — `NetMessage.DecompressedStart` / `DecompressedLength`
   locate a message inside the decompressed payload;
   `DownstreamUtilities.Scan` plus the `TryRead*Value` helpers turn a field
   path through the decoded proto back into a `(start, length)` byte range.

The library supplies every byte offset such a tool needs and none of the
presentation.

### The Analysis engine

Entry point: [`DemoAnalyzer.cs`](../src/CS2DemoKit.Analysis/DemoAnalyzer.cs).
Builds a [`DemoContext`](../src/CS2DemoKit.Analysis/DemoContext.cs)
from a `ParsedDemo`.

Three construction modes:

| Method | Replays entities? | Use case |
|---|---|---|
| `DemoAnalyzer.BuildContext(demo)` | Yes — full `EntityTracker.Replay` | Stat rules that need entity-state reads. |
| `DemoAnalyzer.BuildEventContext(demo)` | No — empty tracker | Event-only rules (much faster). |
| `DemoAnalyzer.BuildContextAsync(demo)` | Yes, on the thread pool | Async callers that must not block. |

`DemoContext` carries:
- `Demo` — the original `ParsedDemo`,
- `Rounds` — derived from `RoundFreezeEndEvent` / `RoundOfficiallyEndedEvent` /
  `RoundEndEvent`,
- `EntityState` — the `EntityTracker` (empty on the event-only path),
- A type-keyed event index (`EventsOfType<T>()` → `IReadOnlyList<T>`,
  O(1) per type-key with caching),
- `EventsInRange(fromTick, toTick)` — binary-search slice.
- `CreateEntityLayer()` — returns a fresh `EntityStateLayer` so each
  parallel rule branch can seek independently (see below).

[`EntityStateLayer`](../src/CS2DemoKit.Analysis/Abstractions/EntityStateLayer.cs)
wraps an `EntityTracker` for **incremental forward-only** seeking. Each
parallel rule branch calls `CreateEntityLayer()` to get its own
single-threaded layer (the underlying tracker is not thread-safe). Seeking
backward is a no-op; `Reset()` rebuilds the tracker from frame 0.

[`EntityChangeScanner`](../src/CS2DemoKit.Analysis/EntityChangeScanner.cs)
runs per-evaluator and synthesises `EntityChangeMessage` events (a `NetMessage`
subclass) when registered field providers cross emission edges — the "edge
detection" layer that lets rulesets react to entity-state changes.

Plugged in via the `StateGraphEvaluator` and the YAML ruleset loader; the
specifics are out of scope for this doc — see the `CS2DemoKit.Analysis`
package README and [`RULES_AUTHORING.md`](RULES_AUTHORING.md).

### The tools

Two, both code generators for committed artifacts. Neither is part of the
runtime.

| Tool | What it does |
|---|---|
| [`tools/CS2DemoKit.Codegen`](../tools/CS2DemoKit.Codegen/) | Derives the Schema Lens from the pinned `CS2OpenDev.Sdk.Entities` package and emits `Entities/Generated/SchemaLens.Generated.cs`, the lane-binding lens registry. Typed wrappers themselves ship in that SDK package; game-event records come from `CS2OpenDev.Sdk.GameEvents`. Nothing is generated for either here. |
| [`tools/CS2DemoKit.RulesCatalog`](../tools/CS2DemoKit.RulesCatalog/) | Generates `src/CS2DemoKit.Analysis/Rules/catalog.json` and the v2 editor schema `cs2demokit-rules.schema.json` from the engine's own registries. `--check` exits non-zero if the committed files are stale. |

---

## 7. Wire-format notes

### The generated proto types

The parser runs **no protoc**. The generated Valve message types
(`CDemoPacket`, `CSVCMsg_PacketEntities`, `CCSUsrMsg_*`, …) ship prebuilt in
the **`CS2OpenDev.Protos`** package, in the `CS2OpenSchema.Protos` namespace
rather than the global one — so pattern-matching `NetMessage.Payload` needs
that `using`. There is no build step to run when the schema changes; there is
a package version to bump.

For reading the `.proto` definitions themselves, the upstream reference is
[`SteamDatabase/GameTracking-CS2`](https://github.com/SteamDatabase/GameTracking-CS2/blob/master/Protobufs/).

### Where the Huffman tree for FieldPath encoding lives

The 40 ops + frequencies are hand-coded in
[`FieldPathEncoding.cs`](../src/CS2DemoKit.Parser/EntityTracking/FieldPathEncoding.cs)
(static `HuffmanRoot` field). The static constructor builds the tree once
at first access. Frequencies are copied verbatim from demofile-net.

### How demofile-net maps to our code

[demofile-net](https://github.com/saul/demofile-net) is the MIT-licensed
.NET CS2 demo parser we treat as the **ground-truth oracle** for parser
output. It is never taken as a dependency — comparison only — but keeping a
local clone as a sibling checkout is worth it for side-by-side reading.

Direct ports of demofile-net code (MIT) in our parser:

| Our file | Their file |
|---|---|
| [`BitBuffer.cs`](../src/CS2DemoKit.Parser/BitBuffer.cs) | `BitBuffer.cs` — verbatim with namespace + minor changes |
| [`FieldPath.cs`](../src/CS2DemoKit.Parser/EntityTracking/FieldPath.cs) | `FieldPath.cs` — verbatim |
| [`FieldPathEncoding.cs`](../src/CS2DemoKit.Parser/EntityTracking/FieldPathEncoding.cs) | adapted (op list + Huffman build) |
| [`HuffmanNode.cs`](../src/CS2DemoKit.Parser/EntityTracking/HuffmanNode.cs) | adapted |
| [`FieldDecoderFactory.cs`](../src/CS2DemoKit.Parser/EntityTracking/FieldDecoderFactory.cs) | adapted from `FieldDecode.cs` |
| `EntityTracker.cs` | adapted from `DemoParser.Entities.cs` |

`THIRD-PARTY-NOTICES.md` at the repo root carries the authoritative
attribution and file list.

Key architectural differences:

- **They use codegen.** Each entity class has a generated C# class with
  per-field strongly-typed decoders. We use **runtime schema** (parsed at
  startup from `CSVCMsg_FlattenedSerializer`) so we don't need a build
  step when the schema changes.
- **They panic on misaligned state** (e.g. throw on delta-on-non-existent).
  We catch into `LastEntityError`, so a demo whose entity state goes off the
  rails still yields complete game events (§5).
- **`_serverClassBits` is only written from `CSVCMsg_ServerInfo`** in both
  codebases.

---

## 8. Tooling reference

| Command | What it gives you | When to use |
|---|---|---|
| `dotnet run --project test/CS2DemoKit.Parser.Tests` | Parser unit + integration tests (TUnit). Demo-dependent tests resolve a `.dem` from `DEMO_PATH`, a `TestData/` folder beside the assembly, or the committed sample in `tests/assets/`. | Default verification after parser changes. |
| `dotnet run --project test/CS2DemoKit.Analysis.Tests` | Analysis + rules tests, including the golden fixtures under `tests/fixtures/`. These skip rather than fail without a full-match demo. | Verification after analysis or ruleset changes. |
| `dotnet run --project tools/CS2DemoKit.Codegen -- --schemalens --state <sdk>/schema-lens/state.json` | Regenerates `Entities/Generated/SchemaLens.Generated.cs` from the pinned SDK package. | After an SDK pin bump, or on a `LensHash` mismatch. |
| `dotnet run --project tools/CS2DemoKit.RulesCatalog` (`--check`) | Regenerates `Rules/catalog.json` + `cs2demokit-rules.schema.json`; `--check` fails instead of writing. | After changing the engine's view/context/provider registries. |
| `CS2DEMOKIT_TRACE_DECODE=1` | The per-packet entity-decode bit trace, dumped on the first decode error via `EntityTracker.DecodeDiagnosticSink`. | Root-causing a bit misalignment (§5). |
| `CS2DEMOKIT_PROFILE=1` | Parse + entity-decode + evaluator profiling accumulators and, where a host uses `ProfilingSession`, a report on exit. | Performance work — see [`profiling.md`](profiling.md). |

---

## 9. Glossary

| Term | Meaning |
|---|---|
| **tick** | One server simulation step. CS2 matchmaking is 64-tick (`TickInterval = 1/64 s`). `DemoFrame.ServerTick` is the **game tick** (gameplay starts at 1, pre-game uses a negative sentinel). `serverTick` in some contexts means the absolute server tick (`gameTick + ServerStartTick`). |
| **frame** | One `EDemoCommands` entry in the .dem file. `DemoFrame`. Multiple frames can share a tick (e.g. `DEM_FullPacket` is interleaved at the same tick as the next regular packet). |
| **PVS** (Potentially Visible Set) | The server-side cull of entities that are sent to a given client/observer based on map geometry. An entity "enters PVS" when it starts being networked to the recipient, "leaves PVS" when it stops. |
| **FullPacket** (`DEM_FullPacket`) | A seek checkpoint frame written every N ticks. Bundles a `CDemoStringTables` snapshot with a re-broadcast `CDemoPacket` containing the full current entity state. **The parser library decodes them; the entity tracker explicitly skips the `PacketEntities` inside them to avoid double-delivery.** |
| **ENTERPVS** | A flag in `svc_PacketEntities.entity_data` indicating "new entity entering this slot." Wire shape: `classId (N bits) + serial (17 bits) + UVarInt32 spawngroup`. |
| **LEAVEPVS** | A flag indicating "entity leaving this slot." May or may not be combined with `FHDR_DELETE` for full destruction. |
| **baseline** (instance baseline) | Per-class initial field-value blob shipped via the `instancebaseline` string table. Applied before the entity's own bytes on first ENTERPVS so that unset fields take class defaults. |
| **delta** | A `svc_PacketEntities` update for an existing entity — sends only changed fields. Encoded as a list of `(field-path-op, value)` pairs against the previous state. |
| **field path** | A path through the (possibly nested) field tree of an entity class. Up to 7 integers; encoded on the wire as a Huffman-coded stream of mutation opcodes. |
| **flattened serializer** | The CS2 entity schema (`CSVCMsg_FlattenedSerializer`), shipped once per demo in `CDemoSendTables`. Describes every networked class, its fields, types, and per-field encoding metadata. |
| **GOTV / HLTV** | The two CS2 broadcast-relay modes. GOTV is the Valve in-engine relay; HLTV is the pro-broadcast relay. They emit slightly different event sets — see `DemoFeatureSet`. |
| **POV demo** | A first-person client-recorded demo (as opposed to a server-relayed GOTV/HLTV stream). The recording client only sees what was in its own PVS, which is what makes entity-state decoding harder on these — see §5. |
| **UBitVar / UVarInt32** | Source-engine bit-level varint encodings. `UBitVar` is the 6/10/14/34-bit encoding used for type IDs and entity indices. `UVarInt32` is standard protobuf varint (multiples of 8 bits). |

---

## 10. Where to start reading

### "I want to add a new game-event handler."

1. Confirm the event has a record in `CS2OpenSchema.Events` — that is, in the
   `CS2OpenDev.Sdk.GameEvents` package. Nothing is generated locally, so if the
   record is missing the fix is a package bump, or an upstream ask if the SDK
   doesn't know the event either.
2. Read the payload off the envelope — `gem.DecodedEvent.Payload is TheNewEvent`
   over `frame.InnerMessages.OfType<GameEventMessage>()`, or
   `DemoContext.EventsOfType<TheNewEvent>()`, which takes the **payload** type
   and hands back the `GameEvent` envelopes carrying it.
3. Field names are the SDK's property names, reachable from rules as
   `event.<Property>`. `src/CS2DemoKit.Analysis/Rules/catalog.json` is
   generated by reflecting over those records, so it is the authoritative
   spelling — check there rather than guessing from the wire name
   (`dmg_health` → `DmgHealth`, `noreplay` → `NoReplay`).
4. If the event fires on the wire but the SDK has no record for it, report it
   upstream as a curated-supplement candidate. Until it ships, such fires
   decode as `UnknownGameEvent` carrying a `Dictionary<string, object>` of
   decoded fields.

### "I want to fix a decode bug."

1. Reproduce with `CS2DEMOKIT_TRACE_DECODE=1` set (or `Tracing.Enabled = true`
   before the replay). If the bug is entity-state-related, the first decode
   error dumps the trace tail plus the bit-consumption outlier ranking through
   `EntityTracker.DecodeDiagnosticSink`.
2. For wire-format issues, the entry points are:
   - **Bit-level read shape:** [`EntityTracking/FieldDecoderFactory.cs`](../src/CS2DemoKit.Parser/EntityTracking/FieldDecoderFactory.cs)
     — the factory that picks a decoder per type.
   - **Field-path opcodes:** [`EntityTracking/FieldPathEncoding.cs`](../src/CS2DemoKit.Parser/EntityTracking/FieldPathEncoding.cs)
     — all 40 path-mutation ops.
   - **Per-frame dispatch:** `ProcessNetMessage` in
     [`EntityTracking/EntityTracker.cs`](../src/CS2DemoKit.Parser/EntityTracking/EntityTracker.cs).
3. Compare bit-by-bit against a local demofile-net checkout (`src/DemoFile/`
   — start at `DemoParser.Entities.cs`). Read-only; never wire it as a
   dependency.
4. Add a TUnit test in `test/CS2DemoKit.Parser.Tests/`. Heavy parser
   tests need `[NotInParallel]`.

### "I want to consume entity state in a tool."

1. `ParsedDemo parsed = MemoryMappedDemoSource.ParseFile(demoPath);` — or
   `DemoParser.Parse(bytes.AsMemory())` if the file may still be being written.
2. `var tracker = EntityTrackerFactory.CreateCurated();` — the factory binds the
   Schema Lens resolver. A bare `new EntityTracker()` still decodes, but every
   lane-routed read silently degrades to the fallback dictionary.
3. Either:
   - Full replay: `tracker.Replay(parsed.Frames);` then walk
     `tracker.CurrentEntities.AllInPvs()`.
   - Seek to a frame: `tracker.ReplayToIndex(targetFrameIdx, parsed.Frames);`
     — remember this replays from 0 each call. For a forward walk, use
     `AdvanceOneFrame`, or `EntityStateLayer` from `CS2DemoKit.Analysis`.
4. To read through typed wrappers, register the SDK factories on the tracker
   (`SdkEntityWorlds.For(tracker)` in the Analysis layer, or
   `TrackerEntityWorld.RegisterWrapper` directly), then use
   `tracker.Get<T>(slot)` / `ResolveHandle<T>(rawHandle)`.
5. **Always check** `tracker.LastEntityError` afterwards — non-null means the
   tracker hit a decode error and stopped. Check `DeltaUnknownCount` too if the
   demo's provenance is unknown (§5). Game events from `parsed.AllGameEvents`
   are complete either way.

### "I want to add a new player stat."

This is an Analysis-engine task, not a parser task. The parser already
exposes everything you need via `ParsedDemo`. Start with
[`RULES_AUTHORING.md`](RULES_AUTHORING.md) — most stats are a YAML ruleset and
no code at all. For the ones that genuinely need code, look at
`src/CS2DemoKit.Analysis/PlayerStats/` for existing stat plugins and
`src/CS2DemoKit.Analysis/Rules/player_stats.rules.yaml` for how the shipped
per-player stats are declared.
