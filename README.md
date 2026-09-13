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

RuleConfigLoadResult rules = YamlConfigLoader.LoadShippedEmbedded();
AnalysisRun run = DemoAnalysis.Run(demo, rules.Rulesets);
```

`Run` is `Build` then `Evaluate`. Split them when you want to compile a graph once and evaluate it
against several demos, or to pass `AnalysisOptions` to only one of the two:

```csharp
BuildResult build = DemoAnalysis.Build(demo, rules.Rulesets);   // CS2DemoKit.Analysis.Graphs
AnalysisRun run = DemoAnalysis.Evaluate(demo, build);
```

### Line of sight — `enemy_spotted`

A rule can trigger on `enemy_spotted`, the rising edge where one player first comes into view of
another. Nothing on the wire says so; it is recomputed from baked map collision geometry. So unlike
every other `AnalysisOptions` knob, leaving `VisibilityEngine` null does not mean "use the default",
it means the capability is unavailable for that run and a rule subscribing to the event silently
never fires:

```csharp
using CS2DemoKit.Analysis.Visibility;

string? bake = CollisionAssetLocator.FindCollisionTris(demo.MapName);
AnalysisOptions options = new()
{
    VisibilityEngine = bake is null ? null : VisibilityEngine.Load(bake)
};

AnalysisRun run = DemoAnalysis.Run(demo, rules.Rulesets, options);
```

The bakes themselves are not in the packages — the geometry is Valve-derived and large, and ships
out of band. What the package does carry is the convention for finding it: `CollisionAssetLocator`
reads `CS2DEMOKIT_COLLISION_DIR` (`<dir>/<map>.tris` or `<dir>/<map>/collision.tris`), then walks up
from the binary for `assets/<map>/collision.tris`, and returns null on a miss rather than throwing,
so a host with no asset pack degrades instead of failing.

`Load` builds a BVH over the mesh — tenths of a second on the large bakes — so keep the engine
rather than rebuilding it per demo: it is immutable after construction and safe to share across
every run on that map. The facets a rule reads off the event, and the sentinel rule that governs
aggregating them, are in `docs/RULES_AUTHORING.md`.

## Tick clocks — read this before comparing ticks

CS2 demos carry two clocks and mixing them produces results that look plausible and are wrong.

- **Frame clock** — the index used by `DemoFrame`, rule-chain events, and highlights. Starts at zero
  for the demo file.
- **Absolute server tick** — what the game server stamped. `GameEvent.ServerTick` is on this clock;
  `ParsedDemo.ServerStartTick` converts between them.

`GameEvent.GameTick`, `RuleChainEvent.Tick` and `HighlightFired.Tick` are already frame clock. Do
not subtract `ServerStartTick` from them.

## Player input (`svc_UserCmds`)

`svc_UserCmds` is about 90% of the net messages in a demo (1.15 million on a 290 MB file) and is
read by almost nothing. It does not appear in `DemoFrame.InnerMessages`. Each payload is stored
verbatim in shared blocks on the frame.

For the interpreted view, `SubTickExtractor` reads the subtick move steps out of them:

```csharp
List<SubTickEvent> input = SubTickExtractor.Extract(demo.Frames);
```

Subtick moves are only one field of the message. It also carries view angles, movement, buttons,
weapon select, mouse deltas and the pawn handle, so for anything beyond subtick input take the raw
wire bytes and decode them yourself:

```csharp
for (int i = 0; i < frame.UserCmdsPayloadCount; i++)
{
    var cmds = CSVCMsg_UserCommands.Parser.ParseFrom(frame.GetUserCmdsPayload(i));
}
```

The reason for the split is GC, not decode cost. A payload-per-message representation means one
surviving object per message, and collection cost scales with the number of live objects rather
than their bytes. Holding the same bytes in a few hundred large arrays cuts parse time about 40%
and halves GC pause under workstation GC, which is what a consumer gets unless the host app opts
into server GC.

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

`docs/RULES_AUTHORING.md` is the guide to the format itself — the stat kinds, gating, contexts and
highlights, which events carry which clock, and the facets that read a sentinel rather than a zero
when there was nothing to measure. The geometry-backed views (`enemy_spotted` and what hangs off it)
need the engine wired in first; see the quick start above.

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

## Performance

`docs/perf/baseline.md` is the reference point for demo-load timings, with the raw rows beside
it. Re-measure against it before claiming a change helped:

```sh
CS2DEMOKIT_COLLISION_DIR=/path/to/bakes dotnet run --project tools/CS2DemoKit.Bench -c Release -- sweep --rounds 10 --out mine.csv
```

`--demos`, `--cooldown` and `--label` are the other knobs; `--help` lists them. The label
defaults to the short SHA of the tree being measured, so a CSV stays traceable to a commit.

`CS2DEMOKIT_COLLISION_DIR` is what decides whether the ray path — the `enemy_spotted` transition
scan — gets measured at all. It names a directory of per-map bakes in either layout the locator
accepts. Unset, no rays are cast, every row carries `vis=0` and the tool prints a banner saying so,
so a run without rays cannot be read as one with them. Set but missing the demo's map, the
measurement fails rather than quietly timing the no-ray pipeline under a label that promises rays.
Those columns took the header from 27 fields to 34, which is also why `compare` refuses an arm
published from a checkout older than the ray path instead of filing its shorter rows alongside.

One process per measurement, a discarded warm-up per process, a forced collection before each
timed phase, and the machine's load average stamped on every row. Workstation GC, which is what
a consumer gets unless their app opts into server GC; server GC hides most of the collector cost,
and the collector is roughly a third of this pipeline.

To A/B a change, publish each side and let the tool interleave them:

```sh
dotnet publish tools/CS2DemoKit.Bench -c Release -o /tmp/arm-before   # from the other checkout
dotnet publish tools/CS2DemoKit.Bench -c Release -o /tmp/arm-after
dotnet run --project tools/CS2DemoKit.Bench -c Release -- compare \
  --a /tmp/arm-before --b /tmp/arm-after --rounds 3 --out ab.csv
```

Two things this corpus has already taught, both the expensive way. Spread the demos across sizes:
a set clustered at one size cannot detect a result that only holds at that size, and the first
version of `demos/` was fifteen megabytes wide. And do not read a per-demo median off three runs,
because a collection landing inside a timed window makes the distribution bimodal with the modes
hundreds of milliseconds apart, so three samples can land entirely in the wrong mode. Either take
the best of N, since the noise only ever adds time, or run enough rounds to see both modes.

Two verbs measure the line-of-sight engine on its own, against a bake rather than a demo:

```sh
dotnet run --project tools/CS2DemoKit.Bench -c Release -- rays  assets/de_nuke/collision.tris
dotnet run --project tools/CS2DemoKit.Bench -c Release -- build assets/de_nuke/collision.tris
```

`rays` is per-ray occlusion throughput over one seeded ray corpus, run against the binary tree the
engine used to ship and every tier of the eight-wide one, interleaved and reported as medians. It
prints nodes, box tests and triangles per ray beside the timing, so a change in speed can be traced
to a change in work rather than guessed at.

`build` is the cost and shape of building the tree, plus a structural digest — two builders that
produce the same digest produce the same tree, so a builder change that keeps the digest needs no
differential ray run to prove it changed no answer. Its `--live` flag is the one place the
collection discipline above is inverted: it forces a collection before every heap sample, inside
the build, so the peak becomes a floor on the live set. That costs an order of magnitude, and the
flag reports memory only and prints no wall-clock.

## Regenerating committed artifacts

```sh
# Rules catalog + editor schema (from the engine's own registries, not from the rule files)
dotnet run --project tools/CS2DemoKit.RulesCatalog

# Entity schema-lens registry (needs a sibling CS2OpenDev-SDK checkout for its state file)
dotnet run --project tools/CS2DemoKit.Codegen -- --schemalens --state ../CS2OpenDev-SDK/schema-lens/state.json
```

Both outputs are committed and gated by tests, so a stale regeneration fails the build rather than
shipping quietly.

## Releasing

All three packages publish together from a `v*` tag, to nuget.org and to GitHub Packages.
`docs/releasing.md` has the procedure and the gates the tag build runs.

Prereleases use the same path with a label in both `version.json` and the tag:

```sh
# version.json: "version": "0.11.0-beta0001"
git commit -am "0.11.0-beta0001"
git tag v0.11.0-beta0001
git push origin main v0.11.0-beta0001
```

The tag has to land on the commit that carries the matching `version.json`; the tag build compares
the two and refuses to publish if they disagree.

Consumers take one by naming it, `Version="0.11.0-beta0001"`, and the exact intra-family pins carry
the label so the set installs together. Label rules are in the doc; the short version is no dots
and a zero-padded counter, because both are load-bearing.

## Licence

MIT. Portions of the bit-level decoder are adapted from
[demofile-net](https://github.com/saul/demofile-net) (also MIT) — see `THIRD-PARTY-NOTICES.md`.
Counter-Strike and Counter-Strike 2 are trademarks of Valve Corporation; this project is not
affiliated with or endorsed by Valve.
