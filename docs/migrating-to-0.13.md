# Migrating to 0.13.0

0.13.0 is mostly additions: a per-side scope (`for: each_team`) and the round facts that feed it,
the server's own round verdict (`round_decided`), a rule graph that describes what runs, grenade
projectiles, and rebuilt player input. The breaks are few and all at compile time, but several
numbers move without one: a weapon's clip, smoke state on current demos, the round-end views, and
anything read from player input.

This page is the order in which a consumer meets the changes. The per-break reference, one entry
per surface that moved, is the 0.13.0 block under
[Compatibility notes in `releasing.md`](releasing.md#compatibility-notes-worth-carrying-into-a-release).
A consumer still on 0.11.0 goes through [`migrating-to-0.12.md`](migrating-to-0.12.md) first.

## 1. Pin the family together

```xml
<PackageVersion Include="CS2DemoKit.Parser" Version="0.13.0"/>
<PackageVersion Include="CS2DemoKit.Analysis" Version="0.13.0"/>
<PackageVersion Include="CS2DemoKit.Analysis.Rules" Version="0.13.0"/>
```

Intra-family pins are exact, so a mixed set fails to restore rather than half-working.

## 2. What stays put

`DemoAnalysis.Run` over a path or a `ParsedDemo`, `DemoAnalysis.Build` and `Evaluate`, the decode
plan, `YamlConfigLoader`'s entry points and `RuleConfigLoadResult`, the rules syntax, and every
output projection keep their shape. The shipped rulesets build and count as before, except on a
round the server decides differently from the old derivation and in one `KASTRounds` cell per
affected player (both in section 4). A retained run and a forward run still produce the same
output.

## 3. Compile errors, in the order they show up

**`BuildResult` lost `Chains`, `GroupHints` and `NodeChains`, and `NodeGroupHint` is gone.** They
were always empty or null. The positional constructor and `Deconstruct` are now

```csharp
BuildResult(Graph, Nodes, Edges, RelevantMessageTypes, PlayerContextIndex, EntityScanner,
            EdgeBacking, GameNodesByRuleId, Outputs, RulesetCoverage)
```

Highlight membership is `RuleGraphNode.HighlightChains` and clustering is `RuleGraphNode.Ruleset`,
`Owners` and `RuleIds`, all on the `RuleGraph` a build or a run draws (section 5). A binary built
against 0.12.x fails with `MissingMethodException` on any of the removed members, a plain property
read included, so rebuild rather than swap the package under a compiled host.

**`PositionSample` has seven members.** `int Team` and `bool IsAlive` follow `Place`, with no
defaults, since any default for `IsAlive` would be wrong for some pawn. Code that constructs or
deconstructs the five-member form fails to compile:

```csharp
// 0.12.0
(int frame, int tick, int slot, Vector3 pos, string? place) = sample;

// 0.13.0
(int frame, int tick, int slot, Vector3 pos, string? place, int team, bool alive) = sample;
```

Record equality includes the new members.

**`RoundEndEnrichmentEdge` takes the win reason.** Its constructor gained a required
`TransientValueNode<int> winReason` between `winnerSide` and `messageType`. Code that builds the edge
itself passes the node `enrich.round.win_reason` writes.

**`MetricRef`, `CheckedStat` and `EntityProviderReference` grew a trailing positional parameter.**
`MetricRef` and `CheckedStat` take `TickClock Clock = TickClock.None`; `EntityProviderReference`
takes `bool IsSingleton = false`. Construction by position still compiles, but a deconstruction must
take the new member, a binary built against 0.12.0 fails with `MissingMethodException` on the
constructor, and record equality includes the new member.

**New public names can collide with yours.** `CS2DemoKit.Analysis.Graphs` gained `RuleGraph`,
`RuleGraphNode`, `RuleGraphEdge`, `RuleGraphScope`, `RuleGraphNodeOrigin`, `NodeTemplateKey`,
`EdgeTemplateKey`, `GraphEdgeKind`, `ExternalStateNode` and `ExternalState`;
`CS2DemoKit.Parser.EntityTracking` gained `ProjectileSample`, `ProjectileSampler`,
`GrenadeProjectileClasses`, `UserCmdReconstructor`, `ReconstructedUserCmd`, `UserCmdApplyStatus` and
`UserCmdReconstructionStats`. A type of your own under one of these names is CS0104 (ambiguous
reference) until you qualify it. The round-facts additions are listed in the `for: each_team` entry
of `releasing.md`.

A `switch` that throws on an unknown member of `RulesetScope`, `OutputScope` or `ScopeAxis` compiles,
and throws on the new members at run time: `EachTeam`, `PerTeamPerRound` / `PerTeamPerGame`,
`TeamRound` / `TeamMatch`. The new `GraphEdgeKind` may gain members in a minor release, so a switch
over it wants a default arm.

## 4. Behaviour that moved without a compile error

**`m_iClip1` and `m_iClip2` read the rounds in the magazine.** Through 0.12.0 both were read as
zigzag varints, which the engine does not send: the clip alternated in sign, came out one high, and
a knife read 0. They now return the count, `-1` for a weapon with no magazine (knives, grenades, the
C4) and `0` for an empty one. That covers `EntityState` reads, `BasePlayerWeapon.Clip1` / `Clip2` and
`player.active_weapon_clip`. A rule written against the old numbers changes meaning (a check for
`> 0` now excludes knives, where `!= 0` includes them as `-1`), and a digest or output cached from
0.12 differs on the column. `m_iClip2` reads `-1` on every CS2 weapon.

**Smokes decode their whole instance baseline.** Through 0.12.0 a smoke's baseline was cut off at
2,048 field paths, so its cell, team, bounces, entity id, thrower and `m_nSmokeEffectTickBegin` kept
garbage until they next changed. On build-10896 demos that made `VisibilityAnalyzer`'s active-smoke
check and the digest's smoke list count flying smokes as clouds near the map origin, so visibility
numbers and smoke digests from 0.12 on current demos differ. The shipped rulesets read no smoke
baseline field and their output does not move. A demo whose entity carries more than 16,384 paths
now reports an entity decode error instead of decoding garbage.

**`round_won` and `round_lost` fire for the team they name.** Through 0.12.0 both fired for every
player at every round end, so `count: round_won` counted every round. A ruleset that counted losses
as `round_won` with a `where:` on the other team winning now reads 0: count `round_lost` instead. A
stat about the round rather than its result ("rounds survived") counts the new `round_ended` view,
which fires for everyone. A player on team 0 or 1 reads neither.

The round's winner is also the server's now, latched from the synthesized `round_decided`, where
0.12.0 derived it from bomb state and alive counts. The two agree on an ordinary round and differ on
a surrender or a draw, and there the counts move: on the fifteen fixture demos, round 13 of one
match ends in a CT surrender, and `CTWins`, `TWins`, `CTLosses` and `TLosses` each move by one for
five players. A round the server decides without leaving freeze time (a surrender vote in freeze
time, a side with nobody left) is opened with a synthesized `round_freeze_end` on its decision's
frame, so its rows no longer land in the round before, and a rule counting `raw.round_freeze_end`
sees it.

The three game-rules values `round_decided` reads are tracked whenever a scanner is built, and
`enrich.round.win_reason` is new, so the shipped rulesets' builds carry four more static nodes: a
consumer that counts `BuildResult.Nodes` or snapshot columns sees them. The new per-player
`entity.controller.money` column moves every later provider's index in the per-pawn digest by one,
so index digest columns by provider name, not position.

**A host that walks the scanner itself reads `PostFrameMessages`.** `round_decided` is dispatched
after the frame's own messages, so it is not in what `EntityChangeScanner.AdvanceAndPollAt`
returns. A host that polls a scanner frame by frame reads `EntityChangeScanner.PostFrameMessages`
after each poll to see it. Reading it consumes nothing; it is valid until the next
`AdvanceAndPollAt`, which refills it, so copy what you keep.

**Other rulesets that validated under 0.12.0 can fail.** A `show:` table whose `per:` does not
belong to the ruleset's `for:` projected zero rows; it is now `resolve.show.table-scope-mismatch`,
and a `show: scoreboard` outside `for: each_player` is `resolve.show.scoreboard-scope`. Composition
drops the ruleset as for any other error, so check `result.Excluded`. In a `for: match` ruleset a
`per: round` stat now resets each round, where it read the match total.

**`tally:` targets are scoped per ruleset and drawn from the root.** Two rulesets whose tallies name
the same target (`kast` and the `multikill` example both use `rounds_2k` to `rounds_5k`) threw at the
first player; each now has its own counters, `kast.rounds_2k` and `multikill.rounds_2k`. In the
graph descriptors a tally's edge is drawn from the root, where it fires, with the tallied stat in
`Reads`, where 0.12.0 drew it from the stat.

**A `---` file loads every ruleset in it.** Through 0.12.0 the first document loaded and the rest
were dropped without a word. Each document is now its own ruleset with its own errors, labelled
`file.rules.yaml#N` after the first; `LoadedFiles` and `FailedFiles` still list files, and a file
with an error in any document is a failed file. A file that loaded cleanly before can now report
errors or duplicate ids from documents that used to be ignored. `RulesetDocumentLoader.Load` and
`TryLoad` return one ruleset, so they now refuse a multi-document stream with a diagnostic and no
document instead of reading the first; load such text with `YamlConfigLoader.LoadDocuments`.

**A stat that reads a coverage-skipped stat is skipped with it.** When a view does not bind on the
demo's profile (`blinded_enemy` on `Cs2HltvProfile`), every stat, `rate:` and highlight in the same
ruleset that reads a skipped stat is skipped too, transitively, and a skipped `tally:` takes its
targets with it. Each is recorded in `BuildResult.RulesetCoverage` and its `show:` column drops. On
HLTV the shipped rulesets now run, without `AvgBlind`; through 0.12.0 the planner threw at the first
player.

**Player input from current demos is rebuilt.** Since build 10896 servers send about 99.8% of user
commands as `delta_data`. `SubTickExtractor.Extract` read `data` only, so on a current demo it
returned the keyframes: 15 events on a build-10896 matchmaking demo that now gives 60,503. It also
counted the command snapshots in a `DEM_FullPacket` twice; those now produce no events. Demos from
before the switch give the same events as before, and the parse itself is unchanged, but anything
calibrated on 0.12.0 counts from a current demo sees different numbers.

**`event.tick` has not moved, and `event.frame_tick` is new.** On a wire event `event.tick` is still
the server clock, higher than the frame clock by the demo's `ServerStartTick`; a rule that
subtracted it to seek a frame keeps working. `event.frame_tick` is the frame clock on every event,
and a table names each bare tick column's clock in `MetricTable.ColumnClocks`.

**Configured tables project without snapshots.** `AnalysisRun.ProjectConfiguredOutputs` no longer
throws on a run without snapshots (a per-event output still does). On a snapshot run, a logic node
switched off by a round reset now marks its column: `kast`'s `KASTRounds` cell reads null at the end
of a match for a player whose last round had no KAST, where it read a stale `true`.

## 5. New things worth adopting

- **The graph a run ran.** `RuleGraph.FromRun(run)` joins the build's game scope with every player
  the run materialised; `RuleGraph.FromBuild(build)` previews it before a run. The Analysis README's
  [Drawing the rule graph](../src/CS2DemoKit.Analysis/README.md#drawing-the-rule-graph) section has
  the rest.
- **Per-side rows.** `for: each_team` builds a ruleset once per side, with `per: team_round` and
  `team_match` tables and a `slots` column to join them to per-player rows.
  `src/CS2DemoKit.Analysis/Rules/examples/round_facts.rules.yaml` is a complete one, and
  [`RULES_AUTHORING.md`](RULES_AUTHORING.md) has the section.
- **Grenades.** `ProjectileSampler.Walk`, over a `ParsedDemo` or a forward `IDemoFrameSource`, yields
  one `ProjectileSample` per grenade projectile per frame, thrower resolved. See the Parser README's
  [Grenade projectiles](../src/CS2DemoKit.Parser/README.md#grenade-projectiles).
- **Who is alive, and on which side.** `PositionSample.Team` and `IsAlive`. `PositionSampler.Walk`
  still yields dead pawns, as it always did, so filter on `IsAlive` for the living.
- **Every player command.** `UserCmdReconstructor` rebuilds each `CSGOUserCmdPB` from `data` and
  `delta_data`, and its `Stats` say how much of the input it could rebuild. The top-level README's
  [Player input](../README.md#player-input-svc_usercmds) section shows the loop.
