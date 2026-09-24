# Releasing

Three packages ship together, versioned in lockstep, from a `v*` tag. `.github/workflows/nuget.yml`
does the work; this file is the procedure and the constraints it enforces.

## Two things must agree

`version.json` holds the version. Nerdbank.GitVersioning stamps it into the assemblies and the
nupkgs. The tag only selects which commit gets published, and the workflow refuses to publish if
the tag name and the stamped version disagree.

So a release is always two steps in one commit's worth of history: change `version.json`, then tag
that commit.

```sh
# edit version.json: "version": "0.9.2"
git commit -am "0.9.2"
git tag v0.9.2
git push origin main v0.9.2
```

Tagging a commit whose `version.json` still says something else fails the build at the first step,
before anything is packed.

The bump can go through a pull request instead, and then the tag lands on the merge commit. That is
worth the extra step on a prerelease: `ci.yml` packs and smoke-tests with `PublicRelease=true`, so a
bump PR is the only thing that exercises the prerelease packaging path before the tag build runs it
for real. A stable bump gets nothing extra from the detour.

## Prereleases

Same mechanism, prerelease label in both places:

```sh
# edit version.json: "version": "0.9.2-beta0001"
git commit -am "0.9.2-beta0001"
git tag v0.9.2-beta0001
git push origin main v0.9.2-beta0001
```

The packages land on nuget.org and on GitHub Packages exactly as a stable release does, and a
consumer takes one by naming it:

```xml
<PackageReference Include="CS2DemoKit.Analysis" Version="0.9.2-beta0001" />
```

Intra-family pins carry the label through, so `CS2DemoKit.Analysis 0.9.2-beta0001` depends on
`[CS2DemoKit.Parser 0.9.2-beta0001]` and the set installs together.

To go stable afterwards, set `version.json` to `0.9.2` and tag `v0.9.2`. Nothing needs undoing;
`0.9.2` sorts above every `0.9.2-*`.

### Label shape

**No dots in the label.** `nugetPackageVersion.semVer` is at its default of 1, so NBGV rewrites a
dotted label: `0.9.2-beta.1` stamps as `0.9.2-beta-0001`, which then disagrees with the tag and
fails the build.

**Pad the counter to four digits.** Prerelease labels containing letters are compared as ASCII
text, not numerically, so `beta10` sorts *below* `beta2`. `beta0001` through `beta9999` sort
correctly because the padding makes lexical order match numeric order.

`alpha0001`, `beta0001`, `rc0001` all work. So does `0.9.2-beta0001`, `0.9.2-beta0002`, `0.9.2`.

### Never move a prerelease tag, bump the counter

Fixing a beta by force-moving the tag onto a new commit does not republish anything. The version
string is unchanged, and both pushes run `--skip-duplicate`, so nuget.org keeps the content it
already has. The workflow goes green and ships nothing, which is the worst possible combination.

A beta that needs a change becomes `beta0002`. The wasted number costs nothing.

### nuget.org is unlist-only

A published version cannot be deleted, only hidden from search. A typo in a tag is permanent and
public. This is the reason there is no nightly push: a build per commit would accumulate versions
nobody can remove, to save a step that is already one commit and one tag.

## What the workflow checks before it uploads

In order, and any one of them fails the release rather than publishing something broken:

1. **`PublicRelease` is true.** If the ref does not match `publicReleaseRefSpec` in `version.json`,
   NBGV appends `-g<sha>` and the family pins point at versions that were never published. The
   `v\d+\.\d+` pattern is deliberately not anchored at the end, which is what lets a prerelease tag
   match; do not anchor it.
2. **Tag agrees with the stamp.**
3. **Both test suites pass.** Against the committed four-round sample only. CI has no demo corpus,
   so run the local one before tagging (see the README).
4. **`scripts/scan-nuget-artifacts.py`.** No build-machine paths, no loose rule files that should
   have been embedded, exact intra-family pins, unbracketed external pins, SourceLink metadata,
   expected embedded-resource count.
5. **`scripts/nuget-smoke`.** Restores the exact nupkgs about to be uploaded, from a local feed with
   the repo's sources cleared, and parses a demo with them. Packaging breakage that does not show up
   in a project-graph build shows up here.

`ci.yml` runs 3 through 5 on every pull request with `-p:PublicRelease=true`, so a version bump gets
its packaging exercised before the tag exists.

## Compatibility notes worth carrying into a release

These are the changes a consumer cannot see in a version number. Add to the list rather than
rewriting it; each entry names the version the change first ships in. For 0.12.0 the entries are
the reference; the order a consumer meets them in, with the code to write, is
[`migrating-to-0.12.md`](migrating-to-0.12.md).

### `CatalogEnrichment` gained two positional parameters (0.11.0)

Through 0.10.0 the record was

```csharp
public sealed record CatalogEnrichment(string Name, string ValueType, string Scope);
```

and it now carries `string? Sentinel = null` and `IReadOnlyList<string>? ProvenBy = null` after
those three. Optional parameters read like an additive change and are not one on a public type in a
shipped package, in three separate ways:

- **Binary.** Optional arguments are baked at the *call site*, not the callee: an assembly compiled
  against 0.10.0 emits `call .ctor(string, string, string)`, and that constructor no longer exists.
  A consumer who upgrades the package without rebuilding gets a `MissingMethodException` the first
  time one is constructed, at runtime, with nothing said at load.
- **Source.** The generated `Deconstruct` goes from three `out` parameters to five, so
  `var (name, valueType, scope) = enrichment;` stops compiling. This is the good failure — it shows
  up on the rebuild, and it is a one-line fix.
- **Behavioural.** Record equality and `GetHashCode` are generated over every positional member, so
  two enrichments agreeing on name, type and scope but differing on sentinel or provenance were
  equal under 0.10.0 and are not under 0.11.0. Anything grouping, deduplicating or dictionary-keying
  on the record gets a different answer without any error at all.

Deserialization is not affected: `CatalogResource` goes through the one constructor and
System.Text.Json supplies the defaults, so a catalog written before the parameters existed still
loads.

### `CatalogProvider` gained two positional parameters (0.11.0)

Same shape as the `CatalogEnrichment` break above, same three failure modes, so read that entry for
the mechanics. Through 0.10.0 the record was

```csharp
public sealed record CatalogProvider(string Name, string Scope, string ClrType,
    string? V2Name = null, string? V2Type = null);
```

and it now carries `string? Unit = null` and `string? Note = null` after those five. The reason is
that the provider family stopped being self-describing: `health` and `armor` are ints that need no
gloss, but `duck_amount` is a fraction rather than a flag, `max_speed` is units per second,
`flash_duration` is a latched duration rather than a countdown, and `weapon_recoil_index` and
`weapon_accuracy_penalty` are raw engine accumulators whose scale is per-weapon. Those facts lived
only in C# XML docs, which no rule author reads. They are now catalog data and reach the editor
through the generated schema.

`catalog.json` grew a `unit` and a `note` key on ten of the twenty-one providers. Both are omitted
when null, so a consumer parsing the file positionally or with a closed-shape deserializer is the
one that notices.

### The per-pawn digest narrowed the provider value types (0.11.0)

`DigestColumnLayout` stores int, bool, float and string columns unboxed. Through 0.10.0 the digest
held `object?[]` and compared with `Equals(object, object)`, so a provider declaring **any** value
type worked. A third-party `IPerPlayerEntityValueProvider` declaring `double`, `long`, `uint` or an
enum therefore compiled and ran against 0.10.0 and now throws `NotSupportedException` — from
`EntityChangeScanner`'s constructor, before any frame is read. Both types are public in a packable
assembly, so this is a consumer-visible break with no compile error in front of it.

The exception names the four kinds and the narrowing to apply (`float` for a `double`, `int` for a
`long`/`uint`/enum value). Widening the set is not on the table: the closed set is what lets the
columns be unboxed at all.

Two smaller guards in the same area change behaviour rather than shape:

- `PerPlayerEntityValueProviderRegistry.Register` now throws `ArgumentException` on a name already
  registered (compared case-insensitively) instead of replacing the provider under it. Replacing was
  invisible — the column count is unchanged, so the layout still compares compatible and every rule
  reading that name quietly reads the newcomer. A plugin that re-registered a builtin name to
  override it has to pick its own name.
- `DigestColumnLayout.For` throws `ArgumentException` when two providers claim one name, for the
  same reason: a consumer resolves a column by name, so the second column would be unaddressable.

### `EnrichmentInfrastructure` gained a required positional parameter (0.11.0)

`BuiltinContexts.EnrichmentInfrastructure` went from three positional parameters to four; the new
`IReadOnlyDictionary<string, EnrichmentSentinel> Sentinels` carries the no-measurement sentinel each
enrichment declares, which the resolver's ungated-aggregate check reads. It has no default, so
constructing or positionally deconstructing the record fails to compile against 0.11.0.

It is deliberately not defaulted. An empty `Sentinels` is not a safe fallback: it is exactly the
state in which every sentinel-aggregate check silently passes, which is the failure the dictionary
exists to prevent. A compile error is the better outcome, and a caller who needs the old shape wants
to look at what it should be passing rather than inherit an empty map.

In practice the record is a return shape — `BuiltinContexts.CreateEnrichment` produces it and the
rule-chain builder consumes it — so a consumer that only receives one is unaffected.

### Two new facets dropped their `is_` prefix before they shipped (0.11.0)

The `shot` and `shot_landed` views expose `first_after_spot` and `first_after_on_target`, not
`is_first_after_spot` / `is_first_after_on_target`. Both names are new in 0.11.0 and neither reached
a release, so nothing to migrate — recorded only because a prerelease consumer may have read the
earlier spelling out of a dev-feed build.

Every other boolean facet is a bare adjective (`enemy`, `silenced`, `bullet`, `no_scope`, `in_air`,
`trade`), and this release's own `first_bullet` strips the prefix off
`enrich.shot.is_first_bullet` — so the two `is_`-prefixed names were the outliers. The underlying
enrichment nodes keep their `is_` names; only the facet spelling changed.

### Two new hard errors reject rulesets that loaded under 0.10.0 (0.11.0)

Both are deliberate — each replaces a silent wrong number — but each turns a working user rules
directory into a failing one, which is not visible in a version number.

- **A scoreboard label collision is now a load error.** A `label:` is the value-column key of the
  per-player metric table, matched across every ruleset in the load unit, so two entries claiming
  one label on one board fought over a single column and the loser read zero. The loader now
  reports `scoreboard label '<L>' on the <board> board is also the column of ruleset '<other>'` as
  an attributed `RuleConfigError`, which means `RuleConfigLoadResult.Success` is false for a
  directory that loaded (wrongly) before. Fix: rename one of the two labels. The round board and the
  match board are separate namespaces, so sharing a label across them is still fine.
- **Aggregating a sentinel-defaulted enrichment without gating it is now a resolve diagnostic**
  (`resolve.ungated-sentinel-aggregate`). A `sum:`, a reducing `bucket: value:`, or a
  `capture: keep: min|max` over an enrichment that carries a no-measurement sentinel used to sum the
  sentinel and produce a plausible number. Fix: add a `match:` or `where:` test on that enrichment
  (or on one the catalog declares proves it measured) to the same stat.
- **`enrich.shot.ticks_since_last_shot` is the sharp edge of that one.** It is a pre-existing public
  enrichment and this release retro-fits it with `"sentinel": "1000000"`, so a consumer ruleset that
  has been summing it ungated since before this release now fails to resolve — and, because the
  resolver returns no ruleset when it has any diagnostic, one offending `sum:` discards every stat
  and highlight in that file, not just the offending one. Fix the stat, or gate it, and the rest of
  the file comes back.

### `DemoFrame.CommandKind` is required and `Command` is computed (0.12.0)

A hand-built frame must set `CommandKind` (an `EDemoCommands`); `Command` is now
`NetMessageCatalog.DemoCommandName(CommandKind)` and has no setter. Compare on `CommandKind`. A
string comparison against `Command` still works and pays a lookup per call.

### `RuleChainBuilder` and `StateGraphEvaluator` build without a demo (0.12.0)

`RuleChainBuilder(registry, demo, profile, ...)` is `RuleChainBuilder(registry, AnalysisTarget?
target, ...)`: pass `AnalysisTarget.From(demo)` where the demo went, or `new AnalysisTarget(tickRate,
profile)` with no demo at all. The `profile` parameter is gone; the target carries the resolved
profile. `RuleChainBuilder.ObservedEvents` and `PlayerContextIndex.InitialTeamBySlot` are gone with
it. `StateGraphEvaluator.Evaluate` and `EvaluateWithSnapshots` take an `IDemoFrameSource`
(`demo.AsFrameSource()`); the frame-list overloads remain. No per-player node exists at
construction any more: they materialise during the run and come back as
`AnalysisRun.MaterializedPlayers` and `FinalNodes`, in both capture modes.

### Team is seeded from the controller entity, not the first `player_team` (0.12.0)

A slot's team used to be the `OldTeam` of its first `player_team` event, read in a build-time
pre-scan over the whole demo. It is now read off `CCSPlayerController.m_iTeamNum` as the entity
scanner observes it, delivered as the synthesized `player_team_observed` event. On the corpus the
two agree (matchmaking demos carry one `player_team` per player, the halftime swap, whose
`OldTeam` is the signon team), so no golden moved; a slot with no `player_team` event at all now
gets its real team instead of 0. The consequence for a caller: any `for: each_player` ruleset now
builds the entity scanner, so a build that used to run without one decodes entities.

### `PerPlayerNodeTemplate.Materialize` lost its `ParsedDemo` parameter (0.12.0)

The factory is `Func<int, int, string, MaterializedPlayer>` and the call is
`Materialize(playerSlot, playerIndex, playerName)`. Drop the trailing argument.

### `EvaluationResult.Messages` holds `MessageRef` (0.12.0)

Was a `(DemoFrame, NetMessage)` pair per dispatched message; is `MessageRef(FrameIndex, Tick,
Ordinal, Message)`, so a snapshot no longer pins the frame. Over a retained demo the frame is
`demo.Frames[m.FrameIndex]`. `IOutputProjector.Project` takes a `DemoDescriptor` in place of the
`ParsedDemo`; the `ParsedDemo` form is an extension method and still compiles.

### `EntityStateLayer` no longer pins the demo's frames (0.12.0)

`new EntityStateLayer(frames)` seeks as before; `new EntityStateLayer()` is fed by `Apply` and
throws on `SeekToTick`. `BuildResult.EntityScanner.Layer` is the latter now, so a caller that
seeked the scanner's layer must build its own over `demo.Frames`. `PrimeFromCheckpoint` gained a
frame-based overload for stream priming.

### `DemoAnalyzer`, `DemoContext`, `IDemoContext` and `RoundInfo` are gone (0.12.0)

Nothing in this repository called them. `BuildContext`'s eagerly replayed tracker is
`new EntityStateLayer(demo.Frames).SeekToTick(tick)`; `EventsOfType<T>()` is
`demo.AllGameEvents` filtered on `e.Payload is T`; `EventsInRange` is the same list sliced on
`ServerTick`.

### `AnalysisOptions.CaptureSnapshots` is `bool?` and the default depends on the source (0.12.0)

Null, the default, captures over a `ParsedDemo` and not over a stream. Code that read the
option as a `bool` no longer compiles; `options.CaptureSnapshots ?? true` restores the old
reading. `AnalysisOptions` also gained `Profile` and `ProbeDialect`, and `AnalysisProvenance`
is a nine-parameter record (source, profile, resolution, plan, digest producer, snapshots,
frames, messages, dialect check); `AnalysisRun.Demo` and `Provenance` are required.

### `ParsedDemo`'s constructor is public and carries the plan (0.12.0)

The constructor a test bound by reflection is public, with three optional parameters after the
seventeen positional ones: `DecodePlan? plan`, `DecodeProvenance? provenance`,
`IReadOnlyList<ParseWarning>? warnings`. Bind it directly. `ParseDiagnostics` is an instance
threaded through the parse rather than a thread-static channel, and `StringTableProcessor`
takes one.

### `DemoFrame.GameTick` is set at construction (0.12.0)

It was filled by a post-pass after the header decoded. It is set when the frame is built, so a
frame read forward carries it too. A hand-built frame that relied on the post-pass must set it.

### One digest producer serves both frame sources (0.12.0)

The up-front parallel producer that decoded a retained demo's entity digests before the first
frame was evaluated is gone; the pipelined checkpoint-parallel producer that served a forward
reader now serves a `ParsedDemo` too. `DigestProducerKind.ParallelUpFront` is removed, so a
`switch` over the enum that named it no longer compiles and a retained run reports
`Pipelined`. `EntityChangeScanner.AdvanceAndPoll(int)` is removed: the evaluator drives the
scanner by frame index. `AdvanceAndPollAt` called outside an evaluation still seeks a list-backed
layer itself, one tick-gated seek per call, so a host that walks a scanner frame by frame gets what
`AdvanceAndPoll` gave it; over a layer built without frames it throws, since nothing else can
advance one. `PrecomputeParallelDigests` keeps its signature and now runs the pipelined
producer over the frames to completion, holding one digest per frame for the next evaluation over
them, which starts no producer of its own and reports `Pipelined`; a second evaluation folds live
again. It is no longer idempotent: every call folds, so a benchmark that calls it per iteration
measures every iteration. `ScannerProfilingSnapshot.FoldTicks` and `FoldAlloc` are the producer's
fold cost, worker time and allocation summed over its workers, on either path: under the evaluation
or up front in that call; `PrecomputeTicks` and `PrecomputeAlloc` read the same numbers under their
earlier name. `analysis.precompute` is emitted around the up-front call alone. `EntityStateLayer.PrimeFromCheckpoint(int, int)` is removed; the frame-based
overload is the one priming. `SeekToTick` and `SeekBeforeFrame` over a list-backed layer remain for
consumers that seek. `AnalysisOptions.MaxDegreeOfParallelism` is the digest worker count on either
source: unset, three over a reader and two fewer than the core count over a retained demo; one is
the sequential producer.

### Entity values live on five typed lanes (0.12.0)

`LaneKind` gained `Vector` and `Long`, `WireType` gained `VectorLane` and `LongLane`, and the
`ClassShape` constructor took six more optional parameters for those lanes' paths, defaults and
transforms. Vectors, angles, 64-bit scalars and entity handles are decoded typed and stored
unboxed; `EntityState.Fields`, the indexer and `TryGetValue` still hand them back boxed as
`Vector3` and `ulong`, and `Get<T>` and `TryGet<T>` read them without a box. Two projections
changed type: enum-typed fields (`MoveType_t`, `CSPlayerState`, `PlayerConnectedState` and the
rest of the `m_e` and `MixedCase_t` family) and `GameTick_t` fields box an `int` where they boxed
a `ulong`, so the 0xFFFFFFFE tick sentinel reads -2. Handles keep the `HandleIndex` marker and
moved from the object lane to the long lane in the generated lens. `PawnLookup.ResolveHandle`
gained a `uint` overload, so a `cref` to it must name a signature; `PawnLookup.TryReadHandle`
reads a handle without boxing. `EntityTracker.StoreUnlensedFields`, off in the analysis engine,
drops the fallback dictionary for fields no lens rule names, and `AdoptSchemaState` shares one
parsed schema between trackers.

### `m_iClip1` decodes with the minusone serializer (0.13.0)

Through 0.12.0, `m_iClip1` was read as a zigzag varint. The engine sends it with the `minusone`
serializer, an unsigned varint holding clip + 1, so every reader got the zigzag reading of clip + 1:
the value alternated in sign, came out one high in magnitude, and a knife read 0. That covers
`EntityState` reads of the field, the SDK's `BasePlayerWeapon.Clip1` and the
`player.active_weapon_clip` column (`entity.pawn.active_weapon_clip`). They now return the rounds in
the magazine, with -1 for a weapon that has none (knives, grenades, the C4) and 0 for an empty
magazine. There is no compile error to flag it: a rule written against the old numbers changes
meaning, and per-pawn digests or outputs cached from 0.12 differ on this column. Row and cell counts
do not change, because both readings are one-to-one on the same wire value and change on the same
frames.

`m_iClip2` uses the same serializer and gets the same fix. No CS2 weapon has a secondary clip, so
it read 0 on every weapon through 0.12.0 and reads -1 now. Only `EntityState` reads of the field and
the SDK's `BasePlayerWeapon.Clip2` see it; no built-in column or provider reads it.

## Credentials

None to manage. nuget.org auth is a trusted-publishing policy tied to owner `sid2934`, repo
`CS2OpenDev/CS2DemoKit`, workflow `nuget.yml`; the login step trades the run's OIDC token for a
short-lived key. GitHub Packages uses the built-in `GITHUB_TOKEN`. Both pushes use
`--skip-duplicate`, so re-running a tag build on the same commit is safe.

That flag has one sharp edge, and it is aimed at the prerelease line. See below.

Symbol packages ship as a run artifact rather than to GitHub Packages, which rejects `.snupkg`.
