# CS2DemoKit.Analysis

A rule-driven analysis engine for CS2 demos: a state-graph evaluator that walks a demo's frames
once and forward, straight off a file or over a retained `ParsedDemo`, four baseline rulesets
embedded in the assembly, rich highlights with frame-clock timestamps, per-player stats, and a 3D
line-of-sight engine for visibility-gated stats. Builds on `CS2DemoKit.Parser`, which does the
decoding.

## Quickstart

```csharp
using CS2DemoKit.Analysis;
using CS2DemoKit.Analysis.Yaml;
using CS2DemoKit.Parser;

// The four baseline rulesets (KAST, per-player stats, weapon stats, post-plant multi-kills) are
// embedded in this assembly, so there are no files to ship or locate alongside your app.
RuleConfigLoadResult loaded = YamlConfigLoader.LoadShippedEmbedded();
if (!loaded.Success)
{
    throw new RuleConfigException(loaded.Errors);
}

// Reads the demo forward once. The reader decodes only what the rules consume and drops each
// frame behind the evaluation loop, so memory stays flat whatever the demo size.
AnalysisRun run = DemoAnalysis.Run(path, loaded.Rulesets);

foreach (HighlightFired hl in run.Highlights)
{
    // hl.Tick is frame clock, the same clock as GameEvent.GameTick and DemoFrame.ServerTick.
    // Never subtract ServerStartTick from it.
    PlayerInfo? player = run.Demo.Players.GetValueOrDefault(hl.PlayerSlot);
    Console.WriteLine($"[{hl.RulesetId}.{hl.HighlightId}] tick {hl.Tick} {player?.SteamId64}: {hl.RenderedTitle}");
}
```

`DemoAnalysis.Run(path, rules)` is the forward path: open a `DemoReader`, resolve the source
profile from the header and the demo's game-event vocabulary, build the graph, narrow the reader's
decode to what the graph consumes (`DemoAnalysis.PlanDecode`), and evaluate. Nothing but the run's
outputs outlives the loop. `AnalysisRun.Provenance` says what actually ran: the source kind, the
profile and how it was resolved, the decode plan, the digest producer, and whether snapshots were
kept. `AnalysisRun.Demo` holds the demo's final facts (map, tick rate, roster) detached from the
reader.

The same call over a retained demo keeps everything:

```csharp
ParsedDemo demo = MemoryMappedDemoSource.ParseFile(path);
AnalysisRun run = DemoAnalysis.Run(demo, loaded.Rulesets);
```

This is the path for a viewer that seeks and inspects after the run; a consumer coming from
0.12.x starts at `docs/migrating-to-0.13.md` in the repository, and one coming from 0.11.0 at
`docs/migrating-to-0.12.md` first. Per-message node snapshots
are on by default over a `ParsedDemo` and off by default over a stream; `AnalysisOptions.CaptureSnapshots`
overrides either way, and on a stream it retains one row per dispatched message, which is what the
stream was chosen to avoid. `AnalysisRun.Highlights`, `MaterializedPlayers` and `FinalNodes` are
populated in both modes. `DemoAnalysis.Build` + `DemoAnalysis.Evaluate` split the two steps for
callers that need the compiled graph before the (multi-second) evaluation runs, e.g. to render a
skeleton UI. Over a stream, a player's name is the one the roster carried when the slot first
materialised; the final names are in `run.Demo.Players`. Over a stream the entity digests are
folded a chunk ahead of the loop by three workers (`AnalysisOptions.MaxDegreeOfParallelism` sets
the count, one means in step with the loop), the file is read and decoded on its own thread, and
a frame's entity and string-table payloads are released once folded, so snapshot rows over a
stream carry no entries for them.
Configured tables (`show: tables` in a ruleset) project with `run.ProjectConfiguredOutputs()` in
both modes. A snapshot run reads them off its rows; a run without snapshots samples the tables'
nodes just before each message that can move the round number (a freeze end, a match start or end)
and at the end, which is the state a snapshot run's round rows hold, so the tables agree row for row
and a forward run no longer has to turn snapshots on to get them. A per-event output, a log of
timeline rising edges, is the one that still needs snapshots.

To customize or fork the shipped rules, extract them to disk with
`YamlConfigLoader.ExtractShippedTo(dir)`, edit the copies, and load your directory back with
`YamlConfigLoader.TryLoadDirectory(dir)` or layer it over the shipped tier with
`YamlConfigLoader.LoadWithOverlay(shippedDir, userDir)`.

## Rules from a database or an upload

`YamlConfigLoader.LoadDocuments(...)` gives in-memory `(label, yaml)` documents identical
semantics to a `rules/` directory; `LoadShippedWithOverlay(userDocs)` layers them over the
embedded shipped tier (same-id replaces wholesale, `enabled: false` drops after overlay).
Validate uploads with no demo via `DemoAnalysis.ValidateRulesets(...)` — pass **every** document
sharing the id namespace (shipped + user), or cross-ruleset `use:` references report false
unknown-ruleset errors; the upload path is
`ValidateRulesets(LoadShippedWithOverlay(userDocs).Rulesets)`. At analysis time,
`BuildResult.RulesetDiagnostics` and `.ExcludedRulesets` surface what composition dropped —
check them, or a ruleset that stopped compiling is indistinguishable from feats that never fired.

## Drawing the rule graph

`RuleGraph.FromRun(run)` is the graph a run ran: the build's game scope, its three external nodes
and every materialised player, each node with a stable `Key` (`g/{i}`, `x/{name}`,
`p{slot}/t{template}/{ordinal}`) and each edge already resolved to two of the view's nodes. Key
selections and breakpoints by `Key`; names repeat across players and rulesets.
`.CollapsePlayers()` folds the per-player copies into one node per template position
(`t{template}/{ordinal}`) and one edge per template edge (`t{template}/e{ordinal}`), each with every
player's copy in `Instances`, which is what a skeleton view draws. Game, team and external nodes
and edges pass through unchanged, keys included.

Every `StateEdge` the builder adds has at least one `GraphEdgeDescriptor`, drawn from its real
source, and so does the wiring that is not an edge. `Descriptor.Kind` says which. The per-player
state (team, alive, clutch) and the entity scanner live outside the node graph, so the edges that
write them and the nodes that pull from them are drawn to and from the `player_context` and
`entity_state` external nodes; a highlight's emission goes to `highlights`. An edge that writes
several nodes has a row per node. Fire counts and applied messages belong to
`Descriptor.Edge`: read them there and do not add them up across its rows. On a collapsed edge,
`Descriptor.Edge` is the lowest slot's copy; the template edge's count is the sum over the `Edge` of
each of its `Instances`, one engine edge per player.

```csharp
RuleGraph skeleton = RuleGraph.FromRun(run).CollapsePlayers();
foreach (RuleGraphEdge edge in skeleton.Edges)
{
    int fires = edge.Instances.Sum(d => d.Edge?.FireCount ?? 0);
    Draw(edge.Source.Key, edge.Destination.Key, edge.Descriptor.Label, edge.Descriptor.ConditionLabel, fires);
}
```

`RuleGraphNode.HighlightChains` holds the `_chain_{highlight}` names of the highlights a node feeds
(what `RuleChainEvent.ChainName` carries). `Ruleset` and `Owners` say which ruleset and which
stats made a node, for clustering; a table column's `PerPlayerColumnAssignment.ChainId` is the
`_chain_{ruleset}` key of the ruleset that declared the column, a different space from the
highlight chains. That ruleset is one of the node's `Owners`, not always its `Ruleset`: a stat two
rulesets declare the same way is one node, made by the first.

`RuleGraph.FromBuild(build)` draws a build before any run, previewing each per-player template once.
The preview runs the builder's per-player factory, so call it before a run over the same build
starts, never during one; after a run, use `FromRun`. A template that cannot materialise without a
demo is left out of the preview and named in `Diagnostics`, so check it.

## Clip planning

`CS2DemoKit.Analysis.Clips` turns highlights into clip windows entirely in frame clock:
`ClipRounds.Derive(demo)` (the frame-clock round authority), `HighlightSurfacing.Surface`
(drops hidden firings, collapses group families to their top tier), `ClipWindows`
(per-round window computation with reach-back + coalescing), and `ClipPlanner.Plan(demo, ...)`
→ a renderer-neutral `ClipPlan`. Any tick-space offset for a downstream renderer applies once,
at emission — never inside the plan.

## Version discipline

This family (`CS2DemoKit.Parser`, `CS2DemoKit.Analysis`, `CS2DemoKit.Analysis.Rules`) is
**lockstep exact-pinned** pre-1.0: `CS2DemoKit.Analysis` depends on exact versions of the other
two. Installing `CS2DemoKit.Analysis` alone is the known-good set — there is no metapackage.

**Bump all `CS2DemoKit.*` package references together, in one commit.** A direct reference to one
family member at a version that conflicts with another member's transitive exact pin doesn't fail
the build — NuGet's nearest-wins rule lets it through with only **NU1608**, a warning. Restore
succeeds and the skew surfaces later as a runtime `MissingMethodException`, not a build error. Add
this to your project so that class of skew fails the build instead:

```xml
<PropertyGroup>
  <WarningsAsErrors>$(WarningsAsErrors);NU1608;NU1605</WarningsAsErrors>
</PropertyGroup>
```

## Parallelism

`AnalysisOptions.MaxDegreeOfParallelism` is the number of entity digest workers, each holding a
tracker and a chunk of frames folded ahead of the evaluation loop. Unset, a forward reader gets
three (the read bounds the run, and each worker is memory the run would otherwise not hold) and
a retained demo gets two fewer than the core count (the frames are already resident; the fold is
the only thing left to hide). One selects the sequential producer. Set it when evaluating several
demos in one process, and still gate the number of *concurrent demos* with your own
`SemaphoreSlim`, sized with the parse-side memory multiplier in mind.

## Garbage collection

The forward path allocates 100 to 500 MB of short-lived frames per demo, and under the default
concurrent workstation collector that is a gen0 collection every few frames, each one suspending
the reader thread and the digest workers. Two startup settings on the host process are worth a
quarter of the wall-clock over the corpus (measured in `docs/perf/baseline.md`, "GC
configuration"): `DOTNET_gcConcurrent=0`, or `<ConcurrentGarbageCollection>false</ConcurrentGarbageCollection>`
in the host's project file, and `DOTNET_GCgen0size=4000000` (hex bytes: a 64 MB gen0 budget).
Both are read when the process starts. Nothing the engine can set at run time reproduces them;
`GCSettings.LatencyMode` was measured and is a wash, so the engine leaves the collector alone.
A host with a UI thread should weigh the first one, since it trades background collections for
blocking ones.

## Pawn position

Rules read a pawn's world position as `player.pos_x`, `player.pos_y`, `player.pos_z` (floats).
There is no `m_vecOrigin` leaf on a pawn: position is a cell index plus an in-cell offset on
`CBodyComponent`, reconstructed as `(cell - 32) * 512 + offset`.

**These three cost more than every other provider combined, and only when you read them.**
Providers are gated in by name, so a ruleset that reads no axis pays nothing. A ruleset that does
read one defeats the digest's delta encoding for that column, because a moving player changes it
every frame. Measured on a 123,283-frame demo, counting per-pawn cells the digest actually emits:

| provider set | cells emitted |
|---|---|
| the six shipped providers | 14,455 |
| plus `pos_z` | 379,576 |
| plus all three axes | 1,442,280 |

One axis is 26x, three are 100x. **Read only the axes you need**: that is where the split into three
providers pays, since a height check gates in one column rather than three.

Three providers rather than one vector-valued provider is forced, not preferred. The rules type
vocabulary is `bool`/`int`/`float`/`string`/`duration`/`instant` plus list and map of a *scalar*
element, so there is no type a `Vector3` could be declared as and no member access to read `.z` off
one. Where that costs: a single vector column would emit 548,787 cells against the split's
1,442,280 on the same demo, because it boxes once per pawn-frame instead of about 2.7 times. A
ruleset that genuinely reads all three axes is paying 2.6x for the language's scalar type system.
Worth revisiting only if positional rules become common enough to justify a new type kind, which
would reach the checker, the normalizer and canonical ruleset hashing.

### Zones and bombsites

The library does not resolve map zones. Bombsite membership needs trigger volumes or baked zone
geometry, neither of which is in a demo file.

The one exception is the site a bomb was planted at. `round.bomb.site` reads `"A"` or `"B"` from
the planter's nav-mesh place at the plant (`BombsiteA` / `BombsiteB`), `round.bomb.plant_place`
holds that place as read, and `round.bomb.site_entity` holds `bomb_planted.Site`, which is the bomb
target's entity index and not a letter. All three hold until the next freeze end. The letter agreed
with the planted C4's own `m_nBombSite` on every plant measured (21 across two matchmaking demos),
but it depends on the map naming its sites the standard way; on a map that does not, `site` reads
`""` and `plant_place` still carries the name.

For anything else, register your own provider, and rules address it by name like any built-in:

```csharp
public sealed class SiteProvider : IPerPlayerEntityValueProvider, IPawnStateReader
{
    public string Name => "entity.pawn.site";
    public Type ValueType => typeof(string);
    public string EntityClass => "CCSPlayerPawn";

    // The scanner validates this leaf against the demo's schema and THROWS if its wire type is
    // not compatible with ValueType, so it cannot be an arbitrary field: a string provider needs
    // a string-ish leaf. A computed provider declares whichever of its inputs matches.
    public string FieldName => SchemaNames.CCSPlayerPawn.LastPlaceName;

    public object? ReadForPawnState(EntityTracker tracker, EntityState pawn) =>
        PositionUtil.CellToWorld(pawn) is { } p ? MyZones.Resolve(p) : null;
    // ... CaptureAllSlots / Read / ReadForPawn as in PawnPositionProvider
}

var providers = PerPlayerEntityValueProviderRegistry.CreateDefault();
providers.Register(new SiteProvider());
var run = DemoAnalysis.Run(demo, rulesets, new AnalysisOptions { PerPlayerEntityProviders = providers });
```

A zone name is coarse and changes rarely, so it stays cheap in the digest in a way raw coordinates
cannot. Prefer it over three axis reads plus arithmetic in the rule.

Implement `IPawnStateReader` when your provider needs the raw `EntityState`. The SDK wrapper's
typed accessors resolve through the Lens lane only, and the `CBodyComponent` pair is not on it, so
`CSPlayerPawn.Origin` returns null on the decode path this engine uses.

## Line-of-sight / visibility

The LOS engine ships in this package under `CS2DemoKit.Analysis.Visibility`
(`VisibilityEngine`, `VisibilityAnalyzer`, `TriangleBvh`) — `VisibilityEngine.Load(trisPath)` loads
a per-map baked triangle mesh and answers ray/occlusion queries against it.

The baked collision geometry itself (`collision.tris` per map) does **not** ship in this package —
it's Valve-derived geometry distributed out-of-band as its own asset bundle. The resolution
convention now ships in the package: `CollisionAssetLocator` finds the blob via the
`CS2DEMOKIT_COLLISION_DIR` environment variable (`<map>.tris` / `<map>/collision.tris`) with an
`assets/<map>/collision.tris` walk-up fallback, null-on-miss; `MapAssetBundleReader` reads the
`bundle.json` manifest beside it. Thread the manifest's identity into
`VisibilityAnalyzer.Options.Bundle` and **persist `Report.Bundle` with any stored result** —
bundles are selected by map name only, so bake identity is the only way to tell a stale bake from
a current one after a CS2 map update. `Analyze` accepts a `CancellationToken`. For the analyzer's
position resolver, pass `CS2DemoKit.Parser.EntityTracking.PositionUtil.CellToWorld`. Without
a bundle for a given map, LOS-dependent stats are simply unavailable; the rest of analysis is
unaffected.

## On-disk rule locations

`RuleSetLocator` (used internally by the shipped-tier resolution helpers) resolves two directories
for applications that deploy rules as files: a per-user overlay under the platform config root
(`~/Library/Application Support/<app>` on macOS, `%APPDATA%\<app>` on Windows,
`$XDG_CONFIG_HOME/<app>` on Linux), and `AppContext.BaseDirectory/rules` — with a directory
walk-up fallback — for the shipped tier. Set `RuleSetLocator.AppConfigDirName` once at startup to
your application's name so user rules land beside its other settings; it defaults to `CS2DemoKit`.
Both locations are overridable with `CS2DEMOKIT_RULES_DIR` and `CS2DEMOKIT_USER_RULES_DIR`.

Server-side consumers that just want the embedded defaults should prefer
`YamlConfigLoader.LoadShippedEmbedded()` over these directory probes — they exist for on-disk
deployment models, not as the primary API.

## Dependencies

`CS2DemoKit.Parser` and `CS2DemoKit.Analysis.Rules` (exact-pinned, see above), `CS2OpenDev.Sdk`
(schema field-name constants), and `YamlDotNet` (the primary rule format — shipped and user
rulesets are both YAML).

## License

MIT. See `THIRD-PARTY-NOTICES.md` in the repo for third-party attributions carried by the family.
