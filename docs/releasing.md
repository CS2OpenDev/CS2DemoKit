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
rewriting it; each entry names the version the change first ships in. The entries are the
reference; the order a consumer meets them in, with the code to write, is
[`migrating-to-0.12.md`](migrating-to-0.12.md) for 0.12.0 and
[`migrating-to-0.13.md`](migrating-to-0.13.md) for 0.13.0.

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

### `SubTickExtractor` rebuilds `delta_data` commands (0.13.0)

Since build 10896 servers send about 99.8% of user commands as `CMsgServerUserCmd.delta_data`
against the player's previous command. Through 0.12.0 `SubTickExtractor.Extract` parsed `data`
only and skipped the rest without a word, so on a current demo it returned the keyframes' moves:
15 events on a build-10896 matchmaking demo that now gives 60,503. It also treated the command
snapshots that current `DEM_FullPacket` frames carry as new input and counted each of them twice;
those now produce no events. Demos from before the switch (all `data`, no snapshots) give the same
events as before. The event shape and the sort by `When` are unchanged, but anything calibrated on
0.12.0 counts from a current demo sees different numbers.

Decode failures used to be swallowed by a catch-all; they are now counted. New public types:
`UserCmdReconstructor`, `ReconstructedUserCmd`, `UserCmdApplyStatus` and
`UserCmdReconstructionStats` in `CS2DemoKit.Parser.EntityTracking`, and an
`Extract(IEnumerable<DemoFrame>, UserCmdReconstructor)` overload that exposes the stats. Nothing
was removed. `DecodeProvenance`, the `ParseWarning` catalogue and the parse itself are unchanged:
the raw payloads are stored as before, and only callers that ask for input pay for the rebuild
(about 2 to 2.5 s on a full current demo). There is no delta-share counter in the parse
diagnostics; `UserCmdReconstructor.Stats` carries the delta share, and
`docs/parser-architecture.md` says why the parse does not count it.

### `round_won` and `round_lost` filter by team (0.13.0)

`views.yaml` has always said `round_won` fires for the players whose live team won the round and
`round_lost` for the rest. Through 0.12.0 the planner never applied that binding: both views fired
for every player at every round end, so `count: round_won` counted every round. A ruleset that
counted losses as `count: round_won` with a `where:` naming the other team as the winner, as the
shipped `player_stats` did for `CTLosses` / `TLosses` and the `save_rounds` example did, now reads
0; count `round_lost` instead. A stat about the round rather than its result ("rounds survived")
counts the new `round_ended` view, which fires for everyone as the unbound `round_won` did. The
shipped rulesets are migrated and the binding moves none of their values, but the resolved-identity
hashes of the four migrated stats change with their view, so a cache keyed on those hashes (a
highlight fingerprint that reaches them) rebuilds once. Their values do move on a round the server
decides differently from the old derivation, such as a surrender; see the `round_decided` entry
below. At `for: match` both views stay unbound.
A subject on neither side (team 0, a slot whose team was never seen, or team 1, a spectator, coach
or caster) reads neither view: the winner is always 2 or 3, so `round_lost` also requires the
subject to be on 2 or 3.

### `for: match` stats reset per round, and restart with the match (0.13.0)

A `per: round` stat in a `for: match` ruleset never reset through 0.12.0: it read the match total.
It now resets at each freeze end, and every `for: match` stat returns to its build-time value on a
repeated `begin_new_match`, as a per-player stat always has. A match-only build also tracks players
now (it forces the entity scanner, as an each_player build already did), so its enrichments see live
teams: a match-only round-end winner used to be derived from nobody alive and read CT every round.
No shipped ruleset is `for: match`; a user ruleset that is gets different, correct numbers.

### A `show:` table must match its ruleset's scope (0.13.0)

A table whose `per:` does not belong to the ruleset's `for:` (`player_round` / `player_match` in
`for: each_player`, `team_round` / `team_match` in `for: each_team`, `match` in `for: match`)
validated and projected zero rows through 0.12.0. It is now a validation error,
`resolve.show.table-scope-mismatch`, and a `show: scoreboard` outside `for: each_player` is one too,
`resolve.show.scoreboard-scope`, where it used to throw at build. Composition drops the ruleset, as
for any other error.

### `for: each_team`, and the public types that grew for it (0.13.0)

A third scope builds a ruleset once per side. Additions a consumer compiled against 0.12.0 meets:

- `RulesetScope.EachTeam`; `OutputScope.PerTeamPerRound` and `PerTeamPerGame`; in
  `CS2DemoKit.Analysis.Rules`, `ScopeAxis.TeamRound` and `TeamMatch`. A `switch` over any of these
  enums that throws on an unknown member throws on the new ones. The hasher names the axes, so every
  existing resolved-identity hash is unchanged.
- `MetricRef` gained an optional positional parameter, `TickClock Clock = TickClock.None`: the
  binary, source and equality consequences described under `CatalogEnrichment` above apply.
- `MetricTable.ColumnClocks`, `BuildResult.TeamNodesByRuleId`, `TeamRosterNodes` and
  `RoundBoundaryTypes`, `ConfiguredOutputProjector.TeamNodesByRuleId` and `TeamRosterNodes`, and
  `CheckedStat.Clock` are init-only or trailing optional members; `CheckedStat` is a positional
  record, so its constructor changed shape too.
- `EntityProviderReference` gained a trailing positional parameter, `bool IsSingleton = false`, for
  the singleton reads described below: its constructor and `Deconstruct` changed shape, and the
  binary, source and equality consequences described under `CatalogEnrichment` above apply.
- New resolve codes: `resolve.show.table-scope-mismatch`, `resolve.show.scoreboard-scope`,
  `resolve.team-scope.unsupported`. The last covers the clutch reads (`round.alive.in_clutch`,
  `round.clutch.size`) in any stat, a `tally:` source included, and a read of another ruleset's
  stat anywhere but `compute:`. A team ruleset's `compute:` reads a `for: match` or another
  `for: each_team` ruleset's stat; in a `where:`, `while:`, `capture:`, `sum:`, `tally:` or bucket
  key the read used to validate clean and throw at build.
- New with the round facts (#61), all additions: `RoundDecidedEvent` in `CS2DemoKit.Analysis.Events`;
  `RoundDecidedEdge`, `BombPlantSiteEdge` and `SideRosterFreezeEndEdge` in
  `CS2DemoKit.Analysis.Edges`; `RoundFactIds` in `CS2DemoKit.Analysis.Building`;
  `CCSGameRulesRoundWinStatusMarker`, `CCSGameRulesRoundWinReasonMarker`,
  `CCSGameRulesTotalRoundsPlayedMarker`, `CCSGameRulesGamePhaseMarker`,
  `CCSGameRulesBombPlantedMarker` and `CCSGameRulesRoundTimeMarker` in
  `CS2DemoKit.Analysis.Plugins.Markers`. A consumer type with one of these names hits CS0104 when
  it imports the namespace. Members: `BuiltinProviderSpecs.ControllerMoney`, `GameRoundWinStatus`,
  `GameRoundWinReason`, `GameTotalRoundsPlayed`, `GameGamePhase`, `GameBombPlanted`,
  `GameRoundTime` and `CreateGameRulesProviders()`; `PlayerContextIndex.DecidedWinnerSide` and
  `DecidedReason`; and `EntityChangeScanner.PostFrameMessages`, the messages to dispatch after a
  polled frame's own, so a host that walks the scanner itself with `AdvanceAndPollAt` must read it
  after each poll to see `round_decided`. `DemoSourceProfile.RoundDecided` is a new virtual that
  returns null; the built-in profiles bind it through `Cs2GotvProfile`, and a profile that derives
  `DemoSourceProfile` directly leaves the `round_decided` view unbound until it overrides it (the
  round-end winner still latches from the synthesized event, which does not go through the
  binding). In the rules language: the `match.*` game-rules singletons (`round_win_status`,
  `round_win_reason`, `total_rounds_played`, `game_phase`, `bomb_planted`, `round_time`),
  `round.bomb.site`, `plant_place` and `site_entity`, `round.team.money` and `round.enemies.money`,
  and `player.money`.

### Configured tables project without snapshots (0.13.0)

`AnalysisRun.ProjectConfiguredOutputs` threw on a run without snapshots through 0.12.0. It now
projects from what the run recorded at each round boundary, which is the state a snapshot run's
round rows hold, so the tables agree row for row. Only a per-event output (a timeline log) still
throws without snapshots. A snapshot run projects as before, with one correction: a logic node
switched off by a round reset now marks its snapshot column, where it used to keep its last `true`
in every later row. kast's `KASTRounds` table cell is one such node, and at the end of a match it
now reads null for a player whose last round had no KAST, rather than a stale `true`: 45 cells over
the fifteen fixture demos, 12 on the five first measured and 33 on the other ten (#65).

### The round's winner is the server's, and `round_decided` is new (0.13.0)

The round-end enrichment (`enrich.round.winner_side` / `winner_team` / `has_winner`) reports the
winner the game rules declared, latched from the new synthesized `round_decided` event, and derives
one from bomb state and alive counts only when there is no entity scanner or no win-status provider.
The two differ on a round the derivation cannot see, such as a surrender or a draw, and there stat
values move. On the fifteen fixture demos that is one round (#65): round 13 of
`..._0665775997_405` ends in a CT surrender (reason 18, win status 2). The derivation read a CT win
from alive counts, where the server's verdict is a T win, so `CTWins`, `TWins`, `CTLosses` and
`TLosses` each move by one for five players, and the final `enrich.round.winner_side` and
`winner_team` go from 3 to 2. No table row moves, because none of the four is projected. On every
other round of those demos the two agreed. `round_decided` is dispatched after the frame's own
messages, a new ordering special case in the evaluator: it follows the kill that decided the round
when both arrive in one frame. `$round_end` is unchanged and still the round's close.

The three game-rules providers it reads (`entity.game.round_win_status`, `round_win_reason`,
`total_rounds_played`) are tracked whenever a scanner is built, and `enrich.round.win_reason` is new,
so every build carries four more static nodes: the rules-output fixtures moved by `nodeCount + 4` and
their hash, and a consumer that counts `BuildResult.Nodes` or snapshot columns sees them. One that no
rule reads is tracked silently: its node updates, but no change marker is dispatched for it, so the
only new messages are the `round_decided` events themselves (three on the sample, one per decided
round), each of which adds one to `MessagesConsumed` and, on a snapshot run, one snapshot row.

A round the server decides without a freeze end (a surrender vote passing in freeze time, or a side
with nobody left to play) is opened at its decision: when a second `round_decided` arrives with no
`round_freeze_end` since the first, the evaluator dispatches a synthesized `round_freeze_end` on the
decision's frame just before it. `round.number` moves only on a freeze end, so without that the
round's decision and close landed in the previous round's rows (a side's `round_won` read 2 and the
other's `round_lost` 2), and on a demo where it happens mid-match every later round was numbered one
short of the server's `m_totalRoundsPlayed`. It happens on 8 of the 282 matchmaking demos measured
(reasons 17 and 18, the surrenders, and 8 on the mid-match one). A rule that counts
`raw.round_freeze_end` sees the synthesized one, and the round-scoped reset, the freeze-end economy
and the side rosters run on it as on a real one. On a demo with no such round nothing changes.

`RoundEndEnrichmentEdge` writes the reason too, so its constructor gained a required parameter,
`TransientValueNode<int> winReason`, between `winnerSide` and `messageType`. Code that constructs the
edge itself no longer compiles against 0.13.0, and a binary built against 0.12.0 fails with
`MissingMethodException`; pass the node `enrich.round.win_reason` writes.

### Singleton reads build, and a new per-player column shifts the digest (0.13.0)

A `match.*` read of a singleton provider (`capture: match.freeze_period`) threw at build through
0.12.0; it now reads the provider's value. The new per-player provider `entity.controller.money`
(`player.money`) sits before the angle and position columns in both registries, so the column index
of every provider after it moved by one in the per-pawn digest layout. A consumer that indexes
digest columns by position rather than by provider name needs to rebuild its index; the per-pawn
fold fixtures moved for the new column.

### `CSmokeGrenadeProjectile` decodes its whole instance baseline (0.13.0)

Through 0.12.0 entity decode stopped collecting field paths at 2,048 per update without a word. A
smoke's instancebaseline carries 3,214 to 3,482 of them (nearly all `m_VoxelFrameData`), so every
smoke's baseline was cut off and its values decoded from misaligned bits. Any field the creation
packet did not re-send kept a garbage value until it next changed: the cell (so `CellToWorld` was
thousands of units off, sometimes for the smoke's whole life), `m_iTeamNum` (usually 0),
`m_nBounces`, `m_nEntityId`, `m_hThrower` (unresolved on 11 to 28% of smokes) and
`m_nSmokeEffectTickBegin`. On build-10896 demos that last one made `VisibilityAnalyzer`'s active
smoke check and the digest's smoke list count flying smokes as clouds near the map origin, so
visibility numbers and smoke digests from 0.12 on current demos differ. Other projectile classes
were never affected. The shipped rulesets read no smoke baseline field, and the rules-output digests
for all fifteen fixture demos are byte-identical (#65).

The cap is now 16,384, and a demo whose entity carries more paths than that reports an entity decode
error (`LastEntityError`, `DecodeErrorRaised`) instead of decoding garbage. As with any entity decode
error, the rest of that packet is skipped. No API changes.

### `PositionSample` carries `Team` and `IsAlive` (0.13.0)

`PositionSample` gained two trailing positional members, `int Team` (the pawn's `m_iTeamNum`, 0 when
unseen) and `bool IsAlive` (`m_lifeState` alive and `m_iHealth` above zero). Its constructor and
`Deconstruct` changed shape: code that constructs or deconstructs the five-member form no longer
compiles against 0.13.0, and a binary built against 0.12.0 that constructs one fails with
`MissingMethodException`. Record equality now includes the new members. There are no defaults on
purpose, since any default for `IsAlive` would be wrong for some pawn.

`PositionSampler.Walk` yields the same samples as through 0.12.0. That includes dead pawns, which it
always yielded although its docs and `PawnLookup.ForEachLivePawn`'s said "live": a dead player's
pawn stays bound to its controller for the rest of the round (385 of 2,069 one-second rows on a
build-10231 de_nuke carry one). Filter on `IsAlive` for the living only. `ForEachLivePawn`'s
behaviour is unchanged and its doc is corrected; `PawnLookup.IsAlive(EntityState)` is new and holds
the rule. The docs also now say that `PositionSample.Tick` is the frame clock (`GameEvent.GameTick`,
not `GameEvent.ServerTick`) and that `Place` is the empty string, not null, outside a named nav area.

### Grenade projectiles have a sampler (0.13.0)

New in `CS2DemoKit.Parser.EntityTracking`: `ProjectileSampler.Walk`, over a `ParsedDemo` or a forward
`IDemoFrameSource`, yields a `ProjectileSample` per grenade projectile per frame with its thrower
slot, position, initial position and velocity, bounces and Created/Removed flags.
`GrenadeProjectileClasses` names the five projectile classes, and `PawnLookup.ResolveThrowerSlot`,
previously internal to Analysis, is public with the same behaviour. The additions are source- and
binary-compatible except for one case: a consumer that declares its own `ProjectileSample`,
`ProjectileSampler` or `GrenadeProjectileClasses` and imports `CS2DemoKit.Parser.EntityTracking`
gets CS0104 (ambiguous reference) until it qualifies the name. The digest and rules output do not
move. Analysis's internal `ProjectileSlotIndex` still follows only smoke and molotov slots, the two
classes the digest reads; `ProjectileSampler` follows all five with its own slot tracking.

### The rule graph a consumer reads is the graph that runs (0.13.0)

Through 0.12.x a consumer drawing the rule graph got descriptors only for rule trigger edges, and
three always-empty members (#50). What changed:

- **Breaking.** `BuildResult` lost `Chains`, `GroupHints` and `NodeChains`, which were always empty
  or null, and `NodeGroupHint` is gone. The positional constructor and `Deconstruct` are now
  `(Graph, Nodes, Edges, RelevantMessageTypes, PlayerContextIndex, EntityScanner, EdgeBacking,
  GameNodesByRuleId, Outputs, RulesetCoverage)`. Source that reads, constructs or deconstructs the
  removed members no longer compiles, and a binary built against 0.12.x fails with
  `MissingMethodException` on any of those accesses, a plain property read included. Highlight
  membership is now `RuleGraphNode.HighlightChains` (the `_chain_{highlight}` names a
  `RuleChainEvent.ChainName` carries); clustering is `RuleGraphNode.Ruleset`, `Owners` and
  `RuleIds`.
- **The descriptor lists grew.** `BuildResult.Edges` describes every game-scope edge, one row per
  written node: the enrichments, the per-player bookkeeping, first-wins guard writes and the entity
  value dispatch included. `MaterializedPlayer.EdgeDescriptors` and
  `EvaluationResult.MaterializedEdgeDescriptors` gained the first-tick, round-end compute, round
  reset, tally, economy, settle, pull (the rate buckets included) and live-compute rows, every
  source of a multi-source `when:` input, and the highlight emission. On the shipped rulesets on
  GOTV the game scope went from 43 to 134 rows and one player from 107 to 211. A write to
  per-player state is drawn to `ExternalStateNode` `player_context`, one of the three
  `BuildResult.ExternalNodes`, which are not in `Nodes`: a consumer that joins `build.Edges` to
  `build.Nodes` alone drops those rows. Join to `Nodes` and `ExternalNodes`, or use `RuleGraph`.
  Several rows can share one edge, so a fire count must not be summed across them.
- `GraphEdgeDescriptor` gained `Kind`, `Edge` and `Reads` as init-only members. Source- and
  binary-compatible, but record equality and `ToString` now include them, so a value-equality set
  of descriptors sees the rows of a multi-write edge as distinct. `GraphEdgeKind` may gain members
  in a minor release; handle unknown values.
- `EdgeBacking` maps every game-scope row a `StateEdge` backs, several rows to one edge for a
  multi-write edge. `GraphEdgeDescriptor.Edge` carries the same edge on the row itself and covers
  the per-player rows too.
- A `tally:` is drawn from the root, which is where it fires from, with the tallied stat in
  `Reads`. It was drawn from the stat.
- `AuthoringGraph` follows reads (`AuthoringGraphEdge.IsRead`) and anchors a highlight's `.count`
  and a tally's targets. `AuthoringGraphNode.ChainIds` is now filled, with highlight chains, not the
  `_chain_{ruleset}` join key a table column's `PerPlayerColumnAssignment.ChainId` carries.
- New in `CS2DemoKit.Analysis.Graphs`: `RuleGraph`, `RuleGraphNode`, `RuleGraphEdge`,
  `RuleGraphScope`, `RuleGraphNodeOrigin`, `NodeTemplateKey`, `EdgeTemplateKey`, `GraphEdgeKind`,
  `ExternalStateNode` and `ExternalState`. A consumer type with one of these names hits CS0104 when
  it imports the namespace. `RuleGraph.FromBuild` with templates runs the builder's per-player
  factory, which keeps state on the builder while it runs: never call it while a run over the same
  build is going. A template that cannot materialise without a demo is left out of that preview
  and named in `RuleGraph.Diagnostics`.
- `RuleGraph.CollapsePlayers` folds only per-player copies, by template position: game, team and
  external edges pass through with their keys. A collapsed edge's `Descriptor` is the lowest slot's
  copy and `RuleGraphEdge.Instances` holds every player's, so a template edge's fire count is the
  sum over their `Edge`s, not `Descriptor.Edge.FireCount`.
- A hand-built `GraphEdgeDescriptor` with no `Edge`, as a 0.12 consumer builds one, is matched to
  the graph edge from its source that writes its destination, and `RuleGraph` draws that edge once
  with a copy of the descriptor that carries it. Without a match the edge is drawn as
  `Undescribed` beside the descriptor's row, and reported.
- Rules output, node counts, snapshot columns, the decode plan, the order edges are registered in
  (game and per player) and resolved-identity hashes do not move. The fifteen rules-output fixtures
  are byte-identical.

### `tally:` targets belong to their ruleset (0.13.0)

Two `for: each_player` rulesets whose `tally:` thresholds named the same target (the shipped `kast`
and the `multikill` example both use `rounds_2k` to `rounds_5k`) built and then
threw at the first player: the second ruleset bound its tally to the first one's counters and never
registered its own, so its `show:` scoreboard found nothing. Each ruleset now gets its own target
counters, so `kast.rounds_2k` and `multikill.rounds_2k` are separate nodes with their own values.
Every example under `Rules/examples/` now runs beside the shipped rulesets. The shipped rulesets on
their own build and count the same as before.

### A stat that reads a coverage-skipped stat is skipped with it (0.13.0)

A stat whose view does not bind on the demo's profile was skipped and recorded in
`RulesetCoverage`, but a stat, `rate:` or highlight that read it was still built, and the planner
threw `stat reference '...' was hashed before the node it points at` at the first player (#68). The
shipped rulesets hit this on `Cs2HltvProfile`, where `blinded_enemy` does not bind and the
`AvgBlind` compute reads two stats on it. Such a reader is now skipped too, transitively, with its
own `RulesetCoverageDiagnostic` naming the stat it reads and the view that did not bind, and its
`show:` column drops as for any other skip. A skipped `tally:` takes its threshold targets with it:
each target is recorded in coverage too, so a `show:` entry naming one drops its column rather than
failing validation as an unknown reference. A `rate:` over a skipped bucket was already dropped,
silently; it is now recorded as well. The skip runs within one ruleset: a stat in another ruleset
that reads a skipped stat is not skipped with it. On HLTV the shipped rulesets now run, with
`AvgBlind` absent. Output on the other profiles does not move.

### A `---` rules file loads every ruleset in it (0.13.0)

Through 0.12.0 a rules YAML holding several rulesets separated by `---` loaded only the first, and
dropped the rest without an error. `YamlConfigLoader.TryLoadDirectory`, `LoadDocuments` and the
overlay loaders built on them now load each document as its own ruleset, with the same error
containment a file gets: a broken document reports its errors and the others still load. Errors in
a document after the first carry the label `file.rules.yaml#N` (N counts from 1 at the top of the
file). A document with no `ruleset:` key is the same "not a rules document" error a single such file
gets; an empty document, such as a trailing `---`, is skipped. `LoadedFiles` and `FailedFiles` still
list files, not documents: an error in any document of a file, a `show:` column collision found
across the load included, lists the file as failed. A file that loaded cleanly before can now
report errors or duplicate ids from the documents that used to be ignored.
`RulesetDocumentLoader.Load` and `TryLoad`, which return one ruleset, now refuse a multi-document
stream with a diagnostic instead of reading its first document. No shipped ruleset uses `---`.

## Credentials

None to manage. nuget.org auth is a trusted-publishing policy tied to owner `sid2934`, repo
`CS2OpenDev/CS2DemoKit`, workflow `nuget.yml`; the login step trades the run's OIDC token for a
short-lived key. GitHub Packages uses the built-in `GITHUB_TOKEN`. Both pushes use
`--skip-duplicate`, so re-running a tag build on the same commit is safe.

That flag has one sharp edge, and it is aimed at the prerelease line: see
[Never move a prerelease tag, bump the counter](#never-move-a-prerelease-tag-bump-the-counter) above.

Symbol packages ship as a run artifact rather than to GitHub Packages, which rejects `.snupkg`.
