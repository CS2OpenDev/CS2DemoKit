# CS2DemoKit

.NET libraries for parsing and analysing Counter-Strike 2 demo files. Three packages, no UI
dependencies, `net10.0`.

| Package | What it gives you |
|---|---|
| `CS2DemoKit.Parser` | The demo parse pipeline: frames and net messages, 270-plus typed game events, entity tracking with tick seeking, and typed entity reads through the CS2OpenDev entity contract. |
| `CS2DemoKit.Analysis` | A rule-driven analysis engine over a parsed demo: state-graph evaluator, per-player stats, highlights, clip planning, and a 3D line-of-sight engine. Depends on the parser. |
| `CS2DemoKit.Analysis.Rules` | The rules DSL's semantic core — lexer, parser, canonical AST, resolver, typed checker and canonical hashing. Zero dependencies, for editors and validation services. |

`CS2DemoKit.Analysis` sits at the top of the dependency graph, so installing it pulls a known-good
set of all three. Intra-family dependencies are exact-pinned; upgrade the family together.

## Quick start

```csharp
using CS2DemoKit.Parser;
using CS2DemoKit.Parser.GameEvents;
using CS2OpenSchema.Events;

ParsedDemo demo = MemoryMappedDemoSource.ParseFile("match.dem");

foreach (GameEvent evt in demo.AllGameEvents)
{
    if (evt.Payload is PlayerDeathEvent death)
    {
        Console.WriteLine($"tick {evt.GameTick}: {death.Attacker} killed {death.UserId} with {death.Weapon}");
    }
}
```

Each `GameEvent` is an envelope — frame and tick metadata plus a `Payload` holding the typed record
from the CS2OpenDev SDK. Synthesized events (entity-derived fires that never appeared on the wire)
carry a null payload, which is why the pattern match is the access route rather than a cast.

Analysis runs rulesets over a parsed demo. Four baseline rulesets ship embedded in the assembly, so
a consumer with no rules directory on disk still gets working output:

```csharp
using CS2DemoKit.Analysis;
using CS2DemoKit.Analysis.Yaml;

var rules = YamlConfigLoader.LoadShippedEmbedded();
AnalysisReport report = DemoAnalysis.Build(demo, rules.Rulesets).Analyze();
```

## Tick clocks — read this before comparing ticks

CS2 demos carry two clocks and mixing them produces results that look plausible and are wrong.

- **Frame clock** — the index used by `DemoFrame`, rule-chain events, and highlights. Starts at zero
  for the demo file.
- **Absolute server tick** — what the game server stamped. `GameEvent.ServerTick` is on this clock;
  `ParsedDemo.ServerStartTick` converts between them.

`GameEvent.GameTick`, `RuleChainEvent.Tick` and `HighlightFired.Tick` are already frame clock. Do
not subtract `ServerStartTick` from them.

## Subtick input

`svc_UserCmds` is about 90% of the net messages in a demo (2.3 million on a 290 MB file) and is
read by almost nothing. It does not appear in `DemoFrame.InnerMessages`. The payloads are kept
verbatim in a slab arena on the frame and decoded by `SubTickExtractor`, which is the supported
way to read them:

```csharp
List<SubTickEvent> input = SubTickExtractor.Extract(demo.Frames);
```

The reason is GC, not decode cost. A payload-per-message representation means one surviving
object per message, and collection cost scales with the number of live objects rather than their
bytes. Holding the same bytes in a few hundred large arrays cuts parse time about 40% and halves
GC pause under workstation GC, which is what a consumer gets unless the host app opts into server
GC.

If you enumerate `InnerMessages` expecting to find `svc_UserCmds` there, that is the one place
this shows through.

## Malformed demos

Demos arrive truncated, corrupted mid-stream, and occasionally hostile. The policy is one rule:

**The parser throws only when the input is not a CS2 demo.** Bad magic bytes, or a file too short to
hold a header, get an `InvalidDataException`, because there is no partial result worth returning.
Everything else degrades: you get the frames that decoded, plus a warning saying what was lost.

That means a corrupt byte at minute 40 costs you minute 40 onward, not the whole match.

Read `ParsedDemo.Health`, not `Warnings.Count`:

| `Health` | Meaning |
|---|---|
| `Clean` | Every byte the parser looked at decoded. |
| `Degraded` | Something was lost on *this* side: a message type this parser has no case for, or diagnostics past the cap. The demo is not implicated. |
| `Damaged` | Part of the demo's own data did not decode, or the recording is incomplete. Present values are trustworthy; absences may be damage rather than fact. |

The distinction is load-bearing. A demo recorded on a build newer than your parser drops net messages
and reports `Degraded` while being perfectly good, so gating a "this demo may be damaged" banner on
`Warnings.Count > 0` fires on every new-build demo. `ParseWarningCodes.SeverityOf` holds the grading.

### Enforced limits

Bounds exist to stop untrusted input allocating without bound. Exceeding one is never fatal: the
structure is rejected, a warning is recorded, and the parse continues.

| Limit | Value | Guards |
|---|---|---|
| `MaxStringDataBytes` | 16 MiB | Declared decompressed size of a string-table blob |
| `MaxEntriesPerTable` | 4096 | Declared entry count in a string table |
| `MinBitsPerEntry` | 3 | Entry count against bits actually present |
| `MaxInstanceBaselineBytes` | 16 MiB | Declared decompressed size of an instancebaseline blob |
| `MinBitsPerInstanceBaselineEntry` | 3 | Baseline entry count against bits present |
| `MaxFieldPaths` | 2048 | Runaway field-path decode on a misaligned entity |
| `MaxWarnings` | 256 | The warning channel itself |

Compressed sizes are checked *before* decompressing, since the declared length is what drives the
allocation and it is attacker-controlled.

### Entity replay

`ParsedDemo.Health` covers parsing. Entity replay runs later, against a live `EntityTracker`, and
reports separately through `EntityTracker.LastEntityError` (sticky, first error wins). The two cannot
share a channel: the parse-time warning store is drained when `ParsedDemo` is constructed, which has
already happened by the time a tracker replays. A demo can parse `Clean` and still hit a replay
error, so check both if you care about entity state.

## Rules

Four baseline rulesets are embedded in `CS2DemoKit.Analysis` and live in
`src/CS2DemoKit.Analysis/Rules/`:

| Ruleset | Computes |
|---|---|
| `kast` | Per-round combat stats and KAST%. |
| `player_stats` | Game-scoped aggregates, weapon categories, HLTV rating. Reads `kast.kast_pct`, so it always loads together with `kast`. |
| `weapon_stats` | Kills and enemy damage bucketed by weapon. |
| `post_plant_double` | Multi-kills after the bomb plant, with clip tick context. |

They double as the authoring samples and as the validation corpus — each has a pinned golden
fixture under `tests/fixtures/rules-v2/`. Write your own as `<name>.rules.yaml` documents; see
`src/CS2DemoKit.Analysis/Rules/examples/` and the JSON schema at
`src/CS2DemoKit.Analysis/Rules/cs2demokit-rules.schema.json` for editor validation. A ruleset whose id
matches a shipped one replaces it wholesale.

## Building

```sh
dotnet build CS2DemoKit.slnx
dotnet run --project test/CS2DemoKit.Parser.Tests -c Release
dotnet run --project test/CS2DemoKit.Analysis.Tests -c Release
```

Tests use TUnit, not xUnit or NUnit. A bare clone builds with no credentials: every dependency,
the CS2OpenDev family included, restores from nuget.org.

Demo-dependent tests resolve a `.dem` from `DEMO_PATH`, a `TestData/` folder beside the test
assembly, or `demos/`. The parser suite falls back to the committed sample in `tests/assets/`, so it
runs in a fresh clone; the analysis suite deliberately does not, because its fixtures are pinned to
a full match and the sample is a four-round trim — those tests skip instead of failing.

### A local demo corpus

Drop a handful of real demos into `demos/` (gitignored) and a large part of the suite stops
skipping and starts running against full matches instead of the four-round sample. Use full
match length demos from various builds and maps: the build-id suffix in the filename tracks the
protocol variant, which is where decode differences show up, and different maps exercise
different entity content. A short or abandoned match covers little the committed sample does not
already cover, so size is a poor proxy to select on. `MultiDemoCanaryTests` sweeps the directory
(capped at 25) and is the breadth check.

This is local-only for now. CI still runs on the committed sample alone, so a corpus run before
opening a PR is worth the minute it costs.

Tests whose expectations are specific to one match name that demo through
`RequireDemo(DemoTestHelper.ReferenceDemoFileName)` rather than taking whatever is in `demos/`, so
they skip cleanly rather than failing against a demo their numbers never described. If you add a
test that hardcodes a tick, a slot, or a count, name its demo the same way.

## Regenerating committed artifacts

```sh
# Rules catalog + editor schema (from the engine's own registries, not from the rule files)
dotnet run --project tools/CS2DemoKit.RulesCatalog

# Entity schema-lens registry (needs a sibling CS2OpenDev-SDK checkout for its state file)
dotnet run --project tools/CS2DemoKit.Codegen -- --schemalens --state ../CS2OpenDev-SDK/schema-lens/state.json
```

Both outputs are committed and gated by tests, so a stale regeneration fails the build rather than
shipping quietly.

## Licence

MIT. Portions of the bit-level decoder are adapted from
[demofile-net](https://github.com/saul/demofile-net) (also MIT) — see `THIRD-PARTY-NOTICES.md`.
Counter-Strike and Counter-Strike 2 are trademarks of Valve Corporation; this project is not
affiliated with or endorsed by Valve.
