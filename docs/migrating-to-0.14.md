# Migrating to 0.14.0

This page is the order in which a consumer meets the 0.14.0 changes. The per-break reference, one
entry per surface that moved, is the 0.14.0 block under
[Compatibility notes in `releasing.md`](releasing.md#compatibility-notes-worth-carrying-into-a-release).
A consumer still on 0.12.0 goes through [`migrating-to-0.13.md`](migrating-to-0.13.md) first.

## 1. Pin the family together

```xml
<PackageVersion Include="CS2DemoKit.Parser" Version="0.14.0"/>
<PackageVersion Include="CS2DemoKit.Analysis" Version="0.14.0"/>
<PackageVersion Include="CS2DemoKit.Analysis.Rules" Version="0.14.0"/>
```

Intra-family pins are exact, so a mixed set fails to restore rather than half-working.

## 2. What stays put

`DemoAnalysis.Run`, `Build`, `Evaluate` and `ValidateRulesets`, `BuildResult`'s constructor, the
rules syntax and the built-in output projections keep their shape. The shipped rulesets build and
count as before; their per-player `show:` tables gain one dimension (section 4).

## 3. Compile errors, in the order they show up

**`TriggerRefKind` gained `Cadence`.** A `switch` over it with no default arm stops compiling; add
the arm, or a default.

**`OutputScope` gained `PerPlayerPerEvent`, `PerTeamPerEvent` and `PerMatchPerEvent`.** The same
fix: add the arms, or a default.

## 4. Behaviour that moved without a compile error

**A ruleset that cannot be lowered is excluded, not thrown.** Through 0.13.0 a ruleset that resolved
but could not be lowered threw: a `for: each_player` one from the first player's materialization,
which aborted `Evaluate`, and a `for: match` or `for: each_team` one from `Build`. It now lands on
`BuildResult.ExcludedRulesets` with a `build.lowering-failed` diagnostic, and its siblings build and
evaluate. A host that caught the exception to quarantine a ruleset reads the list instead:

```csharp
BuildResult build = DemoAnalysis.Build(demo, docs);
foreach (ExcludedRuleset excluded in build.ExcludedRulesets)
{
    // composition drops first, then lowering drops; each carries its code and position
}
```

`RulesetDiagnostics` still carries only composition diagnostics, so a host that reads only that
list misses lowering failures. A ruleset that reads an excluded one through `use:` is excluded with
it.

**`ValidateRulesets` rejects three shapes it used to pass.** A net trigger on a kind that has no
net path (`resolve.trigger.unsupported-for-kind`), a `while:` gate the planner cannot carry
(`resolve.while.unsupported`), and a non-int `capture keep: list`
(`resolve.keep-list.element-type`). Each failed at build or evaluation before, so a directory that
validated with one of them never produced its numbers. The releasing note lists the exact shapes.

**Shot angles round to nine decimal places.** `enrich.shot.turn_degrees`, `travel_from_spot_deg`
and `spray_residual_deg` come from `ShotEnrichmentEdge.AngleDeltaDegrees`, which now returns a value
rounded to `AngleDecimals` (9) and is exactly 0 for identical views. A value moves by at most about
1e-6 degrees, the noise acos added near zero turn. Before, the last digits differed between Windows
and macOS. `flick_error_deg` moves with `travel_from_spot_deg`, but its chest-angle term is not
rounded and can still differ between them.

**The per-pawn fold runs at the end of the frame.** `EntityChangeScanner` folded frame N's per-pawn
digest into the pre-frame snapshot at the start of frame N+1; it now folds it at the end of frame
N, from the new `CompleteFrame`, which the evaluator calls after each frame's post-frame messages.
Edges read the same values on the same frames, the decode-compromise latch sets at the same digest,
and a caller that walks the scanner through `AdvanceAndPollAt` without calling `CompleteFrame`
still gets the fold at its next poll. The one visible difference: `GetPreFrameValue` read between a
frame's last message and the next poll returns that frame's end state instead of the previous
frame's. The frame observer below reads at exactly that point.

**A new per-player `entity.pawn.is_walking` column moves `pos_x`, `pos_y` and `pos_z` by one in the
per-pawn digest**, the same shape the 0.13.0 `entity.controller.money` column caused for every
column after it: index digest columns by provider name, not position.

**A capture with no `on:` is no longer dropped.** Through 0.13.0 such a stat resolved to nothing
at all: no diagnostic, no node, no column. It now lowers when it reads a bare `team.*` value
`per: match`, and every other shape fails validation with `resolve.trigger.unsupported-for-kind`, so
a directory that validated with one now reports it.

**`EntityValueProviderRegistry.CreateDefault()` registers four more providers** after the
game-rules ones: `entity.team.score@2`, `@3`, `entity.team.clan_name@2`, `@3`. A host that counts
or indexes the registry's list moves with it; the catalog lists the two specs once each with
`scope: "perTeam"`.

**Per-player `show:` tables carry `steam_id`.** `per: player_round` and `per: player_match` rows
gain a `steam_id` dimension between `player_name` and `team`: the SteamID64 as a decimal string, null
when the demo has none for the slot. JSON rows gain a key; a CSV reader that indexes columns by
position shifts every column after `player_name` by one. Join players by `steam_id` rather than slot.
The built-in tables (`player_game_stats`, `player_round_stats` and the rest) are unchanged.

## 5. New things worth adopting

`BuildDiagnosticCodes.LoweringFailed` (`build.lowering-failed`) and the three new
`ResolveDiagnosticCodes` constants are stable codes to key on.

`PlayerGameStatsProjector.Project(AnalysisRun, DemoDescriptor)` projects the end-of-match scoreboard
from a run with or without snapshots. A caller that evaluated a second time with snapshots on only to
get `player_game_stats` can drop that evaluation.

A decode meter replaces a host's own wrappers around every walk seam: `parser.entity_decode.started`
(new Parser `Meter` `CS2DemoKit.Parser.Entities`) plus `analysis.entity_scan.started` (new counter on
the existing Analysis evaluator `Meter`) sum to decodes per visit. See
[`profiling.md`](profiling.md#runtime-sources-always-shipped-near-free-when-off).

`PositionUtil.CellToWorld(entity, rejectZeroCell: true)` is the projectile position read
`ProjectileSampler` makes, now callable on any entity carrying the cell leaves: null when a cell
index is 0, which puts the axis outside every map. `CS2DemoKit.Analysis.Observation.ProjectileRow`
is the per-frame projectile record the entity digest carries for the five grenade classes, read
through the frame observer below.

`AnalysisOptions.FrameObserver`, with `ObservedProviders` and `ObservedProjectileClasses`, is the
per-frame hook: an `IFrameObserver` called once per frame on the evaluation thread with a
`FrameView` over the declared per-player columns, the declared grenade projectiles (`Projectiles`,
`Created`, `Removed`) and each slot's team, as of the end of that frame, at the engine's default
parallelism. A host that ran its own `EntityTracker` beside a run to sample positions, places or
grenade flights can declare those reads instead and drop the second decode. Resolve each declared
name once with `FrameView.Provider` and read through the handle; the view is readable only inside
`OnFrame`. See the README's "Reading state frame by frame".

`projectile_thrown` and `projectile_landed` are new views over two synthesized events,
`projectile_created` and `projectile_removed`, for all five grenades: thrower-bound, with `class`,
`x`, `y` and `z` facets. A host that walked `ProjectileSampler` beside a run to find throw origins or
landing points can subscribe instead, or collect `GameEventEdge<ProjectileRemovedEvent>`. See
[`RULES_AUTHORING.md`](RULES_AUTHORING.md#3-views-and-facets--the-vocabulary).

`team.score` and `team.clan_name` read a side's `CCSTeam` in a `for: each_team` ruleset. A host that
replayed a demo to the last frame for the final score and clan names can capture them with no `on:`
and read a `team_match` table instead:

```yaml
ruleset: library_teams
for: each_team
stats:
  score:     { capture: team.score,     per: match }
  clan_name: { capture: team.clan_name, per: match }
show:
  tables:
    team_final:
      per: team_match
      columns:
        - { stat: score, label: score }
        - { stat: clan_name, label: clan_name }
```

Both are keyed by side, not by team, and a demo cut off at the buzzer can miss the last round's
point, as a replay would. `GenericSideFieldProvider` is the provider shape behind them: a singleton
read narrowed to the entity whose `m_iTeamNum` is the side. See
[`RULES_AUTHORING.md`](RULES_AUTHORING.md#per-side-stats--for-each_team).

A ruleset can sample state once per period instead of on every frame: declare `cadences:` and
trigger on `cadence.<id>`. A host that ran a tick-rate `net.CNETMsg_Tick` stat, or sampled positions
on its own walk at fixed intervals, can use a cadence instead. See
[`RULES_AUTHORING.md`](RULES_AUTHORING.md#cadences).

```yaml
cadences:
  second: { seconds: 1, anchor: round_freeze_end, until: round_ended }
stats:
  pos_x: { capture: player.pos_x, on: cadence.second, per: round }
```

An event table records one row per firing of a trigger, on either run path, so a host that sampled
positions on its own walk can declare a cadence and a `per: player_event` table instead. Player rows
carry `steam_id`, so they join to players by SteamID64 rather than by slot. A ruleset that resolves
an event table also carries a hidden `#seq_<table>` stat; `EventTableSequence.IsSequenceStat` tells
a stat list which ones to skip. See [`RULES_AUTHORING.md`](RULES_AUTHORING.md#8-displaying-results--show).

```yaml
show:
  tables:
    positions:
      per: player_event
      on: cadence.second
      columns: [ { stat: pos_x }, { stat: pos_y } ]
```
