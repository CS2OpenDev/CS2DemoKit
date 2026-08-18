# CS2DemoKit.Analysis

A rule-driven analysis engine for parsed CS2 demos: a state-graph evaluator that walks a
`ParsedDemo`'s frames once, four baseline rulesets embedded in the assembly, rich highlights with
frame-clock timestamps, per-player stats, and a 3D line-of-sight engine for visibility-gated
stats. Builds on `CS2DemoKit.Parser` — parse first, then hand the result here.

## Quickstart

```csharp
using CS2DemoKit.Analysis;
using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Analysis.Yaml;
using CS2DemoKit.Parser;

ParsedDemo demo = MemoryMappedDemoSource.ParseFile(path);

// The four baseline rulesets — KAST, per-player stats, weapon stats, post-plant multi-kills —
// embedded in this assembly, so there are no files to ship or locate alongside your app.
RuleConfigLoadResult loaded = YamlConfigLoader.LoadShippedEmbedded();
if (!loaded.Success)
{
    throw new RuleConfigException(loaded.Errors);
}

AnalysisRun run = DemoAnalysis.Run(demo, loaded.Rulesets);

foreach (HighlightFired hl in run.Highlights)
{
    // hl.Tick is frame clock — the same clock as GameEvent.GameTick and DemoFrame.ServerTick.
    // Never subtract ParsedDemo.ServerStartTick from it.
    PlayerInfo? player = demo.Players.GetValueOrDefault(hl.PlayerSlot);
    Console.WriteLine($"[{hl.RulesetId}.{hl.HighlightId}] tick {hl.Tick} {player?.SteamId64}: {hl.RenderedTitle}");
}
```

`DemoAnalysis.Run` builds the graph and evaluates it in one call; `DemoAnalysis.Build` +
`DemoAnalysis.Evaluate` split the two steps for callers that need the compiled graph before the
(multi-second) evaluation runs, e.g. to render a skeleton UI. `AnalysisRun.Highlights` is populated
in **both** capture modes — including the cheaper bare scan (`new AnalysisOptions { CaptureSnapshots
= false }`), which is the mode to reach for if you only need highlights, not per-frame snapshots.

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

Set `AnalysisOptions.MaxDegreeOfParallelism` when evaluating several demos in one process —
otherwise each demo's entity-decode precompute fans out to every core, and each worker holds a
full `EntityTracker`. `null`/≤0 means unbounded (the default). Still gate the number of
*concurrent demos* with your own `SemaphoreSlim`, sized with the parse-side memory multiplier
in mind.

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
position resolver, pass `CS2DemoKit.Parser.EntityTracking.PositionUtil.CellToWorldVector`. Without
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
