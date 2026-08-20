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
skipping and starts running against full matches instead of the four-round sample. Five demos of
40-70 MB is plenty; prefer a spread of the build-id suffix in the filename, since that tracks the
protocol variant and is where decode differences show up. `MultiDemoCanaryTests` sweeps the
directory (capped at 25) and is the breadth check.

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
