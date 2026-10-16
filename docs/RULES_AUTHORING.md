# Authoring Rulesets — a hands-on guide (Rulesets v2)

This is the **learning path** for writing your own stats. You describe *what* you want to measure
in a small YAML file; the engine reads every round of a demo and produces the numbers. You never
write code, and you can't crash the parser — the worst that happens is a clear error telling you
what to fix.

This guide teaches you to author, step by step. The authoritative vocabulary — every view, facet,
enrichment, context and provider the engine knows — is `src/CS2DemoKit.Analysis/Rules/catalog.json`,
generated from the engine's own registries. Working examples live under
`src/CS2DemoKit.Analysis/Rules/examples/` — read them; they all validate clean.

**Where your files go.** Rules are `<name>.rules.yaml` documents. `RuleSetLocator` resolves two
directories: a **shipped** tier (`AppContext.BaseDirectory/rules`, overridable with
`CS2DEMOKIT_RULES_DIR`) and a writable **user overlay** under the platform config root, overridable
with `CS2DEMOKIT_USER_RULES_DIR`. The overlay is provisioned with a copy of the v2 JSON schema, so
start every file with this line to get editor validation and autocompletion:

```yaml
# yaml-language-server: $schema=./cs2demokit-rules.schema.json
```

One file may hold several rulesets separated by `---` lines. Each document loads as its own
ruleset, with its own errors: a broken one reports them and the others still load. An error in a
document after the first names it as `file.rules.yaml#N`, counting from 1 at the top of the file,
while `loaded.LoadedFiles` and `loaded.FailedFiles` list files: a file with an error in any of its
documents is a failed file, though its good documents still load. An empty document, such as a
trailing `---`, is skipped.

A ruleset whose `ruleset:` id matches a shipped one replaces it wholesale; a new id adds stats
alongside. To start from a shipped file, extract the shipped tier to disk with
`YamlConfigLoader.ExtractShippedTo(dir)` and edit the copy.

**Check your work as you go.** Validation needs no demo — `DemoAnalysis.ValidateRulesets` composes
your documents and reports everything wrong with them as data:

```csharp
using CS2DemoKit.Analysis;
using CS2DemoKit.Analysis.Yaml;

RuleConfigLoadResult loaded = YamlConfigLoader.TryLoadDirectory("path/to/your-rules-dir");
RulesetValidationResult result = DemoAnalysis.ValidateRulesets(loaded.Rulesets);

bool ok = loaded.Success && result.Success;
// loaded.Errors  — YAML syntax, non-ruleset files, duplicate ids
// result.Diagnostics / result.Excluded — reference, type and cycle errors
```

**Check `loaded.Errors` too.** A file that fails to parse contributes an error there and *no
ruleset* — the surviving documents then compose cleanly, so `result.Success` alone comes back
`true` for a directory containing a broken file. (`ValidateRulesets` also has an overload taking
raw `(label, yaml)` documents, which folds both tiers into one result and populates
`result.LoadErrors`.)

Pass **every** document sharing the id namespace. If you are layering your rules over the shipped
ones, validate `YamlConfigLoader.LoadShippedWithOverlay(userDocs).Rulesets` — otherwise a
cross-ruleset `use:` reference into a shipped ruleset reports a false unknown-ruleset error. Each
diagnostic carries the ruleset it came from, a stable machine-readable code, the offending source
text, an in-expression `(line, column)` span, and ranked "did you mean" candidates for typos.

---

## 1. The mental model

A **ruleset** is a named bundle of **stats**. Each stat is one measurement. You choose:

- **`for:`** — do you want this *per player* (`each_player`), *per side* (`each_team`: one for the
  terrorists, one for the counter-terrorists), *per grenade* (`each_projectile`: one for each
  projectile, from the throw to the moment it is gone) or for the *whole match* (`match`)?
- **the kind** — *how* to measure (count events, sum a value, keep a max, compute a formula, …).
- **the source** — *what* to measure (a "view" like `kill`, or another stat).
- **`per:`** — the window it resets over: `round` or `match`.

That's the whole idea. Everything else is refinement — filtering, formulas, and how the result is
displayed.

---

## 2. Your first ruleset

Count each player's kills.

```yaml
ruleset: my_first
for: each_player
stats:
  kills:
    count: kill
    per: match
show:
  scoreboard:
    - { stat: kills, label: Kills, group: game }
```

- `count: kill` — add 1 every time this player gets a kill. `kill` is a **view** (below).
- `per: match` — accumulate across the whole match (use `per: round` to reset each round).
- `show: scoreboard:` — put a `Kills` column on the per-player scoreboard.

Validate it, and you have a working stat.

---

## 3. Views and facets — the vocabulary

You don't reference raw wire events; you trigger on **views** — author-friendly verbs that already
know the CS2 conventions. Common views:

`kill` · `death` · `assist` · `damage_dealt` · `shot` · `blinded_enemy` ·
`bomb_planted` · `bomb_defused` · `he_grenade` · `flash_grenade` · `smoke_grenade` · `molotov` ·
`decoy_started` · `inferno_started` · `projectile_thrown` · `projectile_landed` · `round_won` ·
`round_lost` · `round_decided` · `round_ended`

A round ends twice. **`round_decided`** fires on the frame the server decides the round, read off
the game rules' round-win status: its facets are `winner_side` (2 = T, 3 = CT), `reason` (the
engine's round-end reason: 1 the bomb exploded, 7 bomb defused, 8 and 9 elimination, 12 time
ran out, 17 and 18 a surrender) and `rounds_played`. It is synthesized from entity state, so its `event.tick` is the frame clock, and
it is dispatched after the frame's own events, so it follows the kill that decided the round.
**`round_ended`** is the round's close (`round_officially_ended` on a matchmaking demo, 448 ticks
later; 544 at the end of a half, when the break is waited out first; either can land a tick early or
late, which is the server's timer, so do not match on the exact gap; the match's last round has no
`round_officially_ended` and closes on `cs_win_panel_match`, 193 ticks after the decision on the
demos measured), bound to nobody: its facets
are `winner_side`, `winner_team`, `has_winner` and `win_reason`. Every round-end stat (survived,
KAST) is timed on the close. The winner at the close is the server's verdict from the decision,
not a guess from who is left alive.

A round the server decides without it ever leaving freeze time (a surrender vote that passes in the
freeze period, or a side with nobody left to play) has no `round_freeze_end`, and `round.number`
only moves on one. So when a second `round_decided` arrives with no freeze end since the first,
the engine opens the round itself: it dispatches a synthesized `round_freeze_end` on the decision's
frame, just before the `round_decided`, which moves `round.number`, resets the round's stats and
samples the freeze-end economy and rosters like a real one. That round gets its own rows, and
`round.number` stays level with `rounds_played`. A `raw.round_freeze_end` stat sees it too.

**`projectile_thrown`** and **`projectile_landed`** are a grenade projectile's first and last frame,
for all five grenades, bound to the thrower. Their facets are `class` (`smoke`, `molotov`, `he`,
`flash` or `decoy`; an incendiary is a `molotov`), `x`, `y` and `z`, and `projectile_landed` adds
`bounces`. Both are synthesized from entity state, so `event.tick` is the frame clock. A throw fires
on the first frame its thrower resolves, which for a molotov is the frame `molotov` fires, and its
position is where the projectile was first seen; `event.FirstSeenTick` under either view is the
frame-clock tick it was first seen on, the spawn. A projectile lands on the first frame it is gone:
right after the burst for an HE, a flash or a molotov, but only when the cloud or the sound ends for
a smoke or a decoy. A projectile whose thrower never resolves never fires `projectile_thrown`, and
its `projectile_landed` binds to nobody. `x`, `y` and `z` read NaN when the position was not
readable. The events behind them are `projectile_created` and `projectile_removed`; under a view,
`event.*` also reads `EntityIndex`, `Serial` and `ClassName`. A `raw.projectile_created` trigger
reads only the tick, because raw triggers expose no fields, so filter through the views.

**The bursts.** `he_grenade`, `flash_grenade` and `smoke_grenade` are the detonations, bound to the
thrower; under each, `event.EntityId` is the projectile's entity index and `event.X`, `Y`, `Z` where
it burst. **`decoy_started`** is a decoy's sound beginning (`decoy_detonate` is each fake shot after
it) and **`inferno_started`** a molotov's or incendiary's fire lighting. Both bind to nobody: the
wire names the thrower's pawn handle on the one (its `pawn` facet) and no player on the other, so
read them from a `for: match` ruleset and join through their facets: `entity_id` is the decoy
projectile's entity index, or the inferno entity's, and `x`, `y`, `z` the burst. A burst arriving on
the frame the projectile is removed is dispatched before `projectile_removed`, so a `for:
each_projectile` stat on `he_grenade` still sees it.

```yaml
stats:
  smokes_thrown:
    count: projectile_thrown
    match: { class: smoke }
    per: round
```

`enemy_spotted` is a view as well, but it is *synthesized* from recomputed visibility rather than
read off the wire, so it only fires on a run set up for it — read "Facets that need a map bake" in
section 5 before you count it or read anything spot-derived.

A view carries **facets** — typed attributes you filter on with `match:`. The `kill` view has:
`enemy`, `teamkill`, `headshot`, `no_scope`, `through_smoke`, `trade`, `flash_assisted`, `weapon`.

```yaml
stats:
  headshot_kills:
    count: kill
    match: { enemy: true, headshot: true }   # only enemy headshot kills
    per: round
```

For `for: each_player`, a view automatically binds to *this* player (the `kill` view counts *this
player's* kills). At `for: match`, there's no subject, so `count: kill` counts *everyone's* kills
(a match total).

`round_won` and `round_lost` bind by team: for `for: each_player` they fire on the round's close
only for the players whose team (their live team, so the halftime swap is followed) won or lost
it, so `count: round_won` is this player's round wins and `count: round_won` + `count: round_lost`
is one per decided round for a player on a side. A player on neither side (a spectator, a coach, a
slot whose team has not been seen) reads neither. A stat about the round rather than its result, such as "rounds survived",
counts `round_ended` instead, which fires for everyone. (Before 0.13.0 the binding was not applied
and both views fired for every player, so a ruleset that counted losses as `round_won` with a
`where:` on the winner now reads 0: count `round_lost`.) At `for: match` both views fire on every
close, like any view without a subject.

If you need a raw event with no view, use `raw.<event>`; net messages are `net.<Message>`. Views
are almost always what you want.

### Cadences

A stat that samples state at a fixed rate triggers on a **cadence**, not on `net.CNETMsg_Tick`. A
tick trigger evaluates every edge on every frame; a cadence fires once per period. Declare it at the
top of the ruleset and trigger on `cadence.<id>`:

```yaml
ruleset: round_positions
for: each_player
cadences:
  second: { seconds: 1, anchor: round_freeze_end, until: round_ended }   # or ticks: 64
stats:
  pos_x: { capture: player.pos_x, on: cadence.second, per: round }
```

Give the period as `ticks:` or `seconds:`, not both; seconds are rounded to ticks at the demo's
tick rate. `anchor: round_freeze_end` is required and starts step 0 on the freeze end's frame.
`until:` stops it: `round_ended` on the round's close, `next_freeze_end` on the next freeze end, and
with neither it runs until the next anchor restarts it. Both follow the active profile's bindings:
the anchor is any concrete event of `$round_freeze_end`, plus the freeze end the evaluator
synthesizes for a round decided in freeze time, and `round_ended` is any concrete event of
`$round_end`. A custom profile binds `cadence_tick` through `DemoSourceProfile.CadenceTick`. A match restart stops every cadence. Each
step fires on the first frame at or after its due tick, after everything else that frame
dispatches, so a frame gap longer than a period fires the missed steps together on that frame, and
a step due on the stop's own frame does not fire. A step reads the frame's own state: `player.pos_x`
on a step is the position at the end of the frame it fired on, the value a frame observer reads
there. The ticks are the frame clock: `event.tick` reads
the frame the step fired on, and `event.Step` counts from 0 at the anchor. A cadence binds to
nobody, so an each_player stat fires for every player on every step. Trigger on `cadence.<id>`
only: a bare `cadence_tick` reference would fire on every cadence in the build and is rejected.

---

## 4. The stat kinds

Pick exactly one kind per stat.

### `count:` — +1 per event
```yaml
deaths: { count: death, per: round }
```

### `sum:` — add up a value per event
```yaml
damage: { sum: event.DmgHealth, on: damage_dealt, match: { enemy: true }, per: round }
```
`sum:` takes the value to add; `on:` names the view whose events drive it.

### `capture:` — remember value(s)
`keep:` chooses what to keep: `first`, `last`, `list`, or the extremes `min` / `max`.
```yaml
best_multi:                              # the most kills this player got in any single round
  capture: round_kills                   # a numeric value…
  keep: max                              # …keep the maximum over the match
  per: match
```

### `compute:` — a formula over your other stats
Evaluated at round end. Reads your sibling stats and contexts. Add `live: true` to recompute
continuously instead of only at round end.
```yaml
adr: { compute: "damage / round.number" }        # average damage per round
kd:  { compute: "kills / deaths" }
```
Expressions support `+ - * /`, comparisons, `and`/`or`/`not`, the functions
`min max abs floor contains startswith`, and duration literals `10s` / `500ms` / `"1:30"`.

### `tally:` — bucket a value into thresholds
The 2K/3K/4K/5K idiom. Each threshold's `target` is a counter it feeds.
```yaml
multi_kills:
  tally: round_kills
  thresholds:
    - { min: 5, target: rounds_5k }
    - { min: 4, target: rounds_4k }
    - { min: 3, target: rounds_3k }
    - { min: 2, target: rounds_2k }
```
`min:` can also be a `params.<name>` reference if you parameterize your ruleset.

### `streak:` — a windowed streak of events
```yaml
rapid_kills: { streak: kill, window: "10s", min_streak: 2 }
```

### `bucket:` — one sub-count per key
Breaks a stat down by a key (per weapon, per site, …). `key:` may be a **list** for a composite
(tuple) key. Add `value:` + `reduce:` to reduce a value per key instead of counting.
```yaml
kills_by_weapon:
  bucket: kill
  key: event.Weapon
  match: { enemy: true }
damage_by_weapon:
  bucket: damage_dealt                   # the view whose events carry the hurt enrichments
  key: event.Weapon
  match: { enemy: true }
  value: enrich.hurt.capped_damage
  reduce: sum                            # sum | count | min | max | last | first
```
Keep `value:` in the same scope as the driving view — a `hurt`-scoped
enrichment belongs under a `damage_dealt` bucket, not a `kill` one.

### `rate:` — a per-key ratio
Divides two same-keyed buckets into a per-key ratio (e.g. per-weapon headshot %). Both buckets must
use the same `key:`. Iterates the denominator's keys; a key with 0 denominator is skipped.
```yaml
hs_by_weapon:  { bucket: kill, key: event.Weapon, match: { enemy: true, headshot: true } }
weapon_hs_rate: { rate: { of: hs_by_weapon, per: kills_by_weapon } }
```

### `flag:` — a per-round boolean
True/false for the round, driven either by an event (`on:` + `activate`) or by a condition over
your other stats (`when:`). Its most common use is inside a **highlight** (next section).

---

## 5. Gating — filtering when a stat measures

Four ways to narrow what a stat counts, from coarsest to finest:

- **`match:`** — filter by a view's typed facets: `match: { enemy: true, headshot: true }`.
- **`where:`** — a free-form condition over the event's fields, enrichments, contexts, and
  entity state: `where: 'event.Weapon == "awp"'`.
- **`while:`** — only fire while a per-player condition holds: `while: player.alive`.
- **`when:`** — (on `flag:`/`highlight:`) a condition over your *sibling stats*: `when: kills >= 2`.

`match:` and `where:` filter each event; `while:` gates on the player's live state; `when:` composes
your stats. You can combine them.

```yaml
eco_kills:
  count: kill
  match: { enemy: true }
  where: "round.team.equipment < round.enemies.equipment"   # your team was out-bought
  per: round
```

`when:` may be a single expression or a **list**, which reads as "all of these" (AND):
```yaml
when: [enemy_kills > 0, player.survived]     # same as "enemy_kills > 0 and player.survived"
```

**Two clocks: `event.tick` and `event.frame_tick`.** A demo carries two clocks. `event.frame_tick`
is the frame clock on every event: the index `DemoFrame`, timeline events and highlights use, and
the one a video or clip consumer seeks by. `event.tick` is the absolute server tick on a wire event
(`kill`, `shot`, `bomb_planted`, ...), higher by the demo's `ServerStartTick` (about 20,000 ticks on
a typical GOTV demo). On the views the engine synthesizes from entity state (`enemy_spotted`,
`molotov`, `projectile_thrown`, `projectile_landed`, `round_decided`) there is no server stamp, and
`event.tick` is the frame clock too.

Capture `event.frame_tick` when the tick is going to a consumer, and whenever you compare ticks
across views: it is one clock everywhere, so a `where:` that differences a molotov tick against a
kill tick is only right on the frame clock. A table says which clock each tick column is on
(`MetricTable.ColumnClocks`: `frame` or `server`), for a bare `event.tick` or `event.frame_tick`
capture; a column computed from a tick has no entry. The `ticks_since_*` facets are already
durations and need neither.

### Facets that carry a sentinel

A few timing and angle facets read a **sentinel** when there was nothing to measure, rather than
zero: `ticks_since_spot`, `ticks_since_on_target`, `ticks_since_last_shot` and
`ticks_since_last_spot` read `1000000` on an event with no contact behind it,
`travel_from_spot_deg` reads `-1`, and `flick_error_deg` reads `-1000`. Zero would mean "spotted
this very tick" or "a perfect correction", which is the opposite of the truth. The catalogue
marks these (`sentinel:` on the enrichment; the schema hover on the facet says so too).

The consequence: a `sum:`, a reducing `bucket: value:`, or a `capture: … keep: min | max` over
one of them **must gate on it** in `match:` or `where:`, or the checker refuses the stat
(`resolve.ungated-sentinel-aggregate`). Ungated, every unmeasured event would add a million to the
total and the result would look plausible. A bound is the natural gate:

```yaml
burst_gaps:
  sum: enrich.shot.ticks_since_last_shot
  on: shot
  where: "enrich.shot.ticks_since_last_shot <= 64"   # the gate: only shots inside one burst
  per: round
```

A `capture:` with `keep: first | last | list` and a `count:` are not aggregates and need no gate.

### Facets that need a map bake

Seven facets are measured from **recomputed visibility** rather than from anything on the wire, and
they measure nothing unless the run is set up for it:

- on the `shot` view — `ticks_since_spot`, `travel_from_spot_deg`, `flick_error_deg`,
  `first_after_spot`, `ticks_since_on_target`, `first_after_on_target`
- on the `kill` view — `ticks_since_spot`

Two things have to be true, and neither of them is something you can write inside the stat that
reads the facet:

1. **The run was given baked map collision** — `AnalysisOptions.VisibilityEngine`. There is no
   spotted flag on the wire to fall back on: visibility is recomputed from geometry or not at all.
2. **Some rule subscribes to the `enemy_spotted` view.** That scan is expensive, so it runs only
   when a rule asks for the event. `on: shot` is not asking for it — a stat that merely *reads* the
   spot facets does not switch the scan on.

Miss either and the facets read their sentinel on every event, which sums to a plausible number
instead of failing. The build says so rather than leaving you to guess: every stat that reads one
gets a coverage row naming the stat, the facet, and which of the two prerequisites is missing. The
same goes for the `enemy_spotted` view itself — `count: enemy_spotted` resolves and type-checks on
any profile, and with no bake it reports 0 for every player.

So the working shape is a subscribing stat next to the readers:

```yaml
stats:
  contacts:                 # the subscription — this is what turns the contact scan on
    count: enemy_spotted
    per: round
  reaction_ticks:
    sum: enrich.shot.ticks_since_spot
    on: shot
    match: { first_after_spot: true, ticks_since_spot: "<= 320" }   # the sentinel gate
    per: round
  flick_error_sum:
    sum: enrich.shot.flick_error_deg
    on: shot
    match: { travel_from_spot_deg: ">= 0" }   # travel proves the flick error measured too
    per: round
```

The rest of the aim family — `counter_strafe_good`, `counter_strafe_admitted`, `first_bullet`,
`spray_residual_deg` and the three `spray_residual_*` columns — is computed from movement and
recoil state alone and needs neither prerequisite.

---

## 6. Highlights — per-round achievements and their totals

A **highlight** is a per-round "did it happen" flag. Its match-scoped **`.count`** is how many
rounds it fired — the idiomatic way to turn "this round I did X" into a match total.

```yaml
stats:
  round_kills: { count: kill, match: { enemy: true }, per: round }
highlights:
  multi_kill_round:
    when: round_kills >= 2
    per: round
    title: "Multi-kill round"
show:
  scoreboard:
    - { stat: multi_kill_round.count, label: MultiRounds, group: game }
```

Each firing also comes back as a `HighlightFired` on `AnalysisRun.Highlights`, stamped with the
frame-clock tick it happened on — which is what `CS2DemoKit.Analysis.Clips` turns into clip windows.

---

## 7. Contexts — reading the game state

Inside `when:` / `where:` / `compute:` you can read live game state:

- **Per-player (this player):** `player.survived`, `player.traded`, `player.alive`.
- **Round facts:** `round.number`, `round.active`, `round.no_deaths_yet`, `round.bomb_status`,
  `round.bomb.was_planted`, `round.clutch.size`.
- **Where the bomb went down:** set at the plant, held until the next round's freeze end, so they
  read the same at `round_ended` as at the plant.
  - `round.bomb.site` — `"A"` or `"B"`, from the planter's nav place at the plant (`BombsiteA` /
    `BombsiteB`); `""` before a plant, or on a map whose nav mesh names its sites differently.
  - `round.bomb.plant_place` — the planter's place as read (`"BombsiteA"`, or whatever the map
    calls it); `""` before a plant.
  - `round.bomb.site_entity` — `bomb_planted.Site`, the bomb target's entity index (e.g. `173` and
    `236` on nuke), not a letter; `-1` before a plant. (Winning a round is a *view* — `round_won` /
  `round_lost` — not a context.)
- **Match facts:** `match.map`, `match.phase`, `match.live`, `match.half_state`,
  `match.regulation_status`, `match.freeze_period`.
- **Game rules:** the server's own round state, read straight off the game-rules entity. One value
  for the whole game, so they read the same in every scope.
  - `match.round_win_status` — `0` while the round is undecided, `2` once the terrorists have won
    it, `3` once the counter-terrorists have.
  - `match.round_win_reason` — the engine's round-end reason, set with the status: `1` target
    bombed (the bomb exploded), `7` bomb defused, `8` counter-terrorists eliminated the terrorists,
    `9` terrorists eliminated the counter-terrorists, `12` target saved (time ran out), `17` the
    terrorists surrendered, `18` the counter-terrorists surrendered. `0` while undecided. Those are
    the reasons seen on the matchmaking demos measured, not the engine's whole list, so a filter
    meant to cover every outcome should not enumerate reasons.
  - `match.total_rounds_played` — rounds decided so far this match; `0` before round 1. It
    increments on the frame a round is decided, not when the next one starts.
  - `match.game_phase` — `2` first half, `4` the halftime break, `3` second half, `5` match over.
  - `match.bomb_planted` — true from the plant; cleared by a defuse as well as at the round's close,
    so "was the bomb planted this round" is `round.bomb.was_planted`, not this.
  - `match.round_time` — the length the round is configured to run, in seconds (`115` in a live
    matchmaking round, `999` in warmup). Not a countdown.

  **Read the status and reason on `round_decided`, not at the round's close.** They go back to
  `0` at `round_officially_ended`, 448 ticks after the round is decided on a matchmaking demo (544
  at the end of a half), and that is the event `round_ended` (and every round-end stat) fires on,
  so a read of either there is `0`, except on the match's last round: its close is
  `cs_win_panel_match`, 193 ticks after the decision on the demos measured, before the status
  resets, so there the status still reads the winner. At the close the winner and reason are
  `enrich.round.winner_side` and `enrich.round.win_reason` (the `winner_side` and `win_reason` facets
  of `round_ended`).
- **Team aggregates (subject-relative):** `round.team.alive` / `round.enemies.alive`,
  `round.team.players` / `round.enemies.players`, `round.team.equipment` /
  `round.enemies.equipment`, `round.team.money` / `round.enemies.money`, `round.alive.in_clutch`.
  The economy sums are sampled once per round, at `round_freeze_end`: the equipment the side
  carries and the cash it has left after the freeze-time buys. A purchase later in buy time is in
  neither. Bound-check a money sum before a rule leans on it (`round.team.money <= 5 * 16000`);
  it is a sum of wire values and a decode fault would show there first. (Issue #54 reported
  $3,400 to $4,300 per player in round 1 of one build-10231 demo; read through the engine at this
  sample point the sides of that round hold $2,600 and $750, the pistol round's leftovers.)
- **Entity state:** `player.health` / `player.armor` / `player.equipment_value` /
  `player.active_weapon_clip` / `player.active_weapon_class` / `player.place` / `player.money` —
  the player's live pawn state (`money` is the controller's cash, the one read here not on the
  pawn). (`active_weapon_clip` is the magazine count of the currently held weapon — under the
  pre-frame timing below, at a kill event it is the clip BEFORE the killing shot, so "last bullet"
  reads `== 1`; knives, grenades and the C4 read `-1`, and an empty magazine reads `0`. `place` is the human-readable nav-mesh
  place name the pawn last occupied — `"BombsiteA"`, `"TSpawn"`, `"Ramp"`, … — a string; names come
  from the map's nav mesh, so gate on the standard ones
  (`BombsiteA`/`BombsiteB`/`CTSpawn`/`TSpawn`) for map-portable rules. Outside any named area it
  reads `""`, not null.)
- **Position:** `player.pos_x` / `player.pos_y` / `player.pos_z` — the pawn's world origin in map
  units. That is its FEET, not its eyes; eye height is origin plus a stance-dependent offset.
- **Movement and aim state:** the pawn and active-weapon reads the shot-anchored aim metrics are
  built from. Each of these changes far more often than the economy reads above, so each costs the
  digest a full column — but they are gated by name, and a ruleset that reads none of them pays
  nothing.
  - `player.duck_amount` — the crouch ramp: a **fraction** from `0` (standing) to `1` (fully
    crouched), not a flag. It interpolates across the transition, so a threshold on it asks "how
    far into the crouch", and `> 0` is "started to crouch" rather than "is crouching".
  - `player.max_speed` — the pawn's movement cap in units/second. **Weapon-dependent** (an AWP out
    caps far below a knife) and it changes again when scoped or walking, which is why it is the
    denominator of a counter-strafe test rather than a fixed speed: the engine's own
    "moving enough to spoil the shot" line is `0.34 * max_speed`.
  - `player.shots_fired` — position within the CURRENT burst, not a round or match total: it
    resets on trigger release and on weapon change.
  - `player.is_scoped` — whether the pawn is looking down a scope.
  - `player.flash_duration` — remaining blind time in **seconds**; `0` is not flashed. Gating an
    aim metric on `== 0` keeps blinded engagements out of a population where they read as
    catastrophic crosshair placement that says nothing about aim.
  - `player.eye_pitch` / `player.eye_yaw` — where the crosshair points. See the wrap note below.
  - `player.punch_pitch` / `player.punch_yaw` — the aim punch (recoil kick). **Do not build a
    recoil metric on these without reading the warning below.**
  - `player.weapon_recoil_index` — the active weapon's fractional index into its recoil pattern,
    rising per shot and decaying between sprays. A raw engine accumulator with per-weapon scaling:
    the property worth using is that it climbs monotonically within one spray (which is how a
    spray is segmented at all), not the size of the number.
  - `player.weapon_accuracy_penalty` — the active weapon's accumulated inaccuracy term. Also a raw
    per-weapon accumulator, so a Deagle's `0.3` and a Negev's `0.3` are not the same statement and
    thresholding across weapons compares nothing. It is the weapon's own decaying penalty and does
    NOT include the movement contribution, so reading it alone as "how inaccurate was this shot"
    understates a running player.
  - `player.on_ground` / `player.ducking`: the ground and crouch bits of the pawn's flags, as
    bools. `player.ducking` is the flag, not the ramp `player.duck_amount` gives. `player.flags` is
    the raw bitfield; the language has no bitwise operators, so test the two named bits instead.
  - `player.weapon_throw_strength`: the held grenade's throw strength: `1` for a full throw, `0`
    for an underhand lob, about `0.5` with both buttons. Only a grenade carries it, and a read that
    stops resolving does not clear the column, so after the grenade leaves the hand it keeps its
    last value. Test it together with `player.active_weapon_class`.
  - `player.coaching_team`: the side this player coaches, `2` or `3`, and `0` for everyone else.
    It is read off the slot's controller, not through a pawn, so a slot with no player pawn bound
    has a value too.

**The four angle columns wrap at 360.** `eye_pitch`, `eye_yaw`, `punch_pitch` and `punch_yaw` are
raw `QAngle` components as the engine networks them, over **[0, 360)** — a -2 degree kick arrives
as `358`. So `abs(a - b)` is wrong across the wrap: two angles one degree apart read as 359 degrees
apart when one of them is just below zero, and nothing reports it. Difference them through the
modulo instead, which folds the result back onto (-180, 180]:

```yaml
# Killed from behind: the victim was facing the way the killer was, so the two view yaws
# are close — which only reads as close if the difference goes through the wrap.
back_kills:
  count: kill
  match: { enemy: true }
  where: "abs(((player.eye_yaw - victim.eye_yaw + 180.0) % 360.0 + 360.0) % 360.0 - 180.0) <= 60.0"
  per: round
```

**`punch_pitch` / `punch_yaw` do not currently decode to an aim punch.** On the bundled GOTV
sample the column comes back clustered near -94 and +89 degrees, which is not a recoil kick by any
reading — a real one is a couple of degrees. The framework's own consumer treats anything past 45
degrees as unreal and **rejects every value this column produces**, which is why
`enrich.shot.spray_residual_measured` reports `false` on that demo rather than reporting a
300-degree residual as data. Until the column decodes, a rule that averages or thresholds these
two is measuring the decode, not the player.

**A timing note on entity reads.** In an event-gated site (`where:`, a `sum:`/`capture:` value,
`while:`), an entity read is the value *at the moment of the event*. Which state that is depends on
what raised the trigger:

- A game event from the demo (`player_hurt`, `kill`, `weapon_fire`, `round_freeze_end`, ...) reads
  the state from before its own frame's entity update. A frame's game events come before its
  entity update on the wire, so this is the state the event arrived in: the victim's HP before the
  hit, the clip before the shot.
- A trigger the engine raises from the frame itself (a cadence step, `round_decided`, the freeze
  end synthesized for a round decided in freeze time, and the entity events synthesized from the
  frame's state: `projectile_created`, `projectile_removed`, `molotov_thrown`, `enemy_spotted`)
  reads the frame's state after its entity update, as a frame observer and a capture with no
  `on:` do. So `thrower.pos_x` and `projectile.pos_x` on `projectile_created` are one frame, and
  `thrower.pos_x@-8` counts back from it. These events dispatch after the frame's wire events, so
  a `hegrenade_detonate` that arrives on the frame the projectile is removed reaches the instance
  before `projectile_removed` does.
- The one exception is `player.cmd.*`, below.

In a node-logic site (`compute:`, `flag: when:`), it's the value at *round end / evaluation time*.
Both are useful; pick the site that matches the question. (Under an event view you can also read a
role's entity state, using the role name in place of `player`: `victim.health` in a `kill`-view
`where:`.)

**Player input.** `player.cmd.*` reads the player's latest user command, rebuilt from the demo's
`svc_UserCmds` (matchmaking demos carry them; a demo without them reads `0`):

- `player.cmd.buttons`: the held-button mask (`base.buttons_pb.buttonstate1`), a 64-bit int.
  `!= 0` is "pressing something"; the language has no bitwise operators yet.
- `player.cmd.forward` / `player.cmd.left`: the movement inputs, `forwardmove` and `leftmove`.
- `player.cmd.view_pitch` / `player.cmd.view_yaw`: the view angles the client sent, in degrees.
- `player.cmd.tick`: the client tick the command was built on.

A command read includes the commands of the frame the event fires on, at a game event too, where
the entity reads above are the state from before the frame's update. Commands are served at event sites only (`where:`, a `sum:` /
`capture:` value); a `compute:` or `flag: when:` that reads one fails to lower. Reading any of them
makes the run decode user commands, which costs a run that reads none nothing.

**Looking back.** `player.pos_x@-8` reads what `player.pos_x` read 8 ticks earlier on the frame
clock. N is a whole number from 1 to 64. It works on the subject's per-player entity reads: the
`player.*` names above, and `thrower.*` in a `for: each_projectile` ruleset (not `player.cmd.*`, a
role handle, `projectile.*`, `match.*` or an event field), in a `where:` or the value of an
event-triggered stat; anywhere else is `resolve.look-back.unsupported`. Before the player has a value that far back it reads `0`. The
ticks count back from the state the site reads, so on a cadence step `@-8` is 8 ticks before the
step's own frame.

```yaml
cadences:
  step: { ticks: 8, anchor: round_freeze_end }
stats:
  moved_x:
    sum: abs(player.pos_x - player.pos_x@-8)
    on: cadence.step
    per: round
```

The `round.*` and `match.*` contexts read the same in every scope. `player.*` needs a player, so
only a `for: each_player` ruleset has it. The team aggregates (`round.team.*` / `round.enemies.*`)
are relative to a subject: the player's team in `for: each_player`, the ruleset's side in
`for: each_team`; `round.alive.in_clutch` and `round.clutch.size` need a player. A `for: match`
ruleset has no subject and cannot read any of them.

---

## 8. Displaying results — `show:`

- **`scoreboard:`** — per-player columns. `{ stat: <name or highlight.count>, label:, group: }`.
  `group:` is usually `round` (per-round columns) or `game` (match totals).
- **`tables:`** — richer per-round or per-match tables, written as a **named map**
  (`tables: { <table-name>: { per:, columns: [...] } }`). The `per:` has to match the ruleset's
  `for:`, or validation reports `resolve.show.table-scope-mismatch`: `player_round` /
  `player_match` in `for: each_player`, `team_round` / `team_match` in `for: each_team`,
  `projectile` in `for: each_projectile` and `match` (a single match-level row) in `for: match`.
  Player rows carry `player_slot`,
  `player_name`, `steam_id` (the SteamID64 as a string, null when the demo has none) and `team`;
  join players by `steam_id` rather than by slot. A `player_round` row's `team` is the player's
  team at that round's freeze end, so the halftime and overtime swaps show; a `player_match` row's
  is the team they finished on.
- **Event tables: one row per firing.** `per: player_event`, `team_event` or `match_event` (one
  per `for:`, in the same order) takes an `on:` trigger, written as a stat's `on:` is, and emits a
  row each time it fires for an instance, with the columns read right after that message. Rows
  carry `round_number` and `tick` (the frame clock, listed in `ColumnClocks`); player rows also
  carry `player_slot`, `player_name`, `steam_id` (a string) and `team` (the player's team at the
  firing), side rows `side`. A
  cadence is the usual trigger. The checker refuses a trigger that fires too often to keep a row
  per firing (`resolve.show.event-table-frequency`): a `perTick` or `frequent` catalog event, or a
  wire event whose frequency was never measured. Synthesized events pass. A missing `on:`, or an
  `on:` on any other grain, is `resolve.show.event-table-trigger`. Event tables project with or
  without snapshots.

  ```yaml
  cadences:
    second: { seconds: 1, anchor: round_freeze_end, until: round_ended }
  stats:
    pos_x: { capture: player.pos_x, on: cadence.second, per: match }
    pos_y: { capture: player.pos_y, on: cadence.second, per: match }
  show:
    tables:
      positions:
        per: player_event
        on: cadence.second
        columns: [ { stat: pos_x }, { stat: pos_y } ]
  ```
- **Which clock a tick column is on.** A table lists its bare tick columns in
  `MetricTable.ColumnClocks` (`frame` or `server`), so a consumer knows whether to subtract the
  demo's `ServerStartTick` without knowing which views are synthesized.
- **`as: ticks | seconds | time`** on a column reformats a tick-valued stat (raw ticks, seconds, or
  `m:ss`).

`scoreboard:` is inherently per-player; in a `for: match` ruleset use `tables:` instead.

**A scoreboard `label:` is the column key**, and it is matched across every ruleset loaded
together, not just yours. Two entries landing the same label on the same board (round or match)
would fight over one column and the loser would silently read as zero, so the loader rejects the
directory with an attributed error naming both rulesets. The board follows the stat's `per:`
(a highlight `.count` is always match-scoped; `boards:` overrides), and the round and match
scoreboards are separate tables, so the same label on a per-round stat and a match total is fine.

---

## 9. Bringing it together — a worked example

"Multi-kill rounds," end to end (this is
`src/CS2DemoKit.Analysis/Rules/examples/paper-test/multikill.rules.yaml`):

```yaml
ruleset: multikill
for: each_player
stats:
  round_kills:
    count: kill
    match: { enemy: true }
    per: round
  multi_kill_tally:
    tally: round_kills
    thresholds:
      - { min: 5, target: rounds_5k }
      - { min: 4, target: rounds_4k }
      - { min: 3, target: rounds_3k }
      - { min: 2, target: rounds_2k }
show:
  scoreboard:
    - { stat: rounds_2k, label: "2K", group: game }
    - { stat: rounds_3k, label: "3K", group: game }
    - { stat: rounds_4k, label: "4K", group: game }
    - { stat: rounds_5k, label: "5K", group: game }
```

For more complete, real examples read the four baseline rulesets in
`src/CS2DemoKit.Analysis/Rules/`: `weapon_stats` (buckets), `kast` (counters + tally + a highlight
+ a compute, and the one ruleset that `exports:`), `post_plant_double` (a narrow, gated highlight),
and `player_stats` (the big one — computes, entity reads, and the cross-ruleset `use: [kast]`).
Smaller single-purpose files live under `src/CS2DemoKit.Analysis/Rules/examples/`.

---

## 10. Two bigger tools

### Match-wide stats — `for: match`
When a stat isn't per-player (total kills, total rounds, a match-level table), use `for: match`.
Views count everyone; `player.*`/team contexts aren't available (there's no subject). Display with
`tables: (per: match)`, not `scoreboard:`.
```yaml
ruleset: match_totals
for: match
stats:
  total_kills:  { count: kill, per: match }
  total_rounds: { count: round_ended, per: match }
show:
  tables:
    match_summary:                         # tables: is a named map: <table-name>: { per, columns }
      per: match
      columns:
        - { stat: total_kills, label: TotalK }
        - { stat: total_rounds, label: Rounds }
```

### Per-side stats — `for: each_team`
When the question is about a side rather than a player (what a side bought, whether it won, how its
kills fell across the round), use `for: each_team`. The ruleset runs twice, once for the terrorists
(`team.side == 2`) and once for the counter-terrorists (`team.side == 3`), and its tables have one
row per side: `per: team_round` gives one row per round per side, `per: team_match` one per side.

- An actor view binds to the side: `count: kill` counts the kills by players on the side *in that
  round* (their live team, so the halftime swap is followed). `match: { actor: any }` counts
  everyone's, as in `for: match`.
- `round_won` / `round_lost` fire for the side that won / lost the round.
- `round.team.*` / `round.enemies.*` read relative to the side: `round.team.equipment`,
  `round.team.money`, `round.team.alive`, and the enemies' twins.
- `team.side` is the side, `2` or `3`. `player.*` is not in scope, and neither are
  `round.alive.in_clutch` / `round.clutch.size`, whose subject is a player.
- `team.score` (rounds won) and `team.clan_name` read the side's own team entity, so they are keyed
  by side, not by team: after halftime the team that started T is the CT row. Read them anywhere an
  expression goes, or capture them with no `on:` to get the value the side ends on:
  `score: { capture: team.score, per: match }`. A capture with no `on:` takes only a bare `team.*`
  read, `per: match`, kept last; anything else is `resolve.trigger.unsupported-for-kind`. On a demo
  cut off at the buzzer the last round's point can be missing.
- A `team_round` table carries two dimensions besides the round: `side`, and `slots`, the
  comma-joined slots of the side's connected players at that round's freeze end. `slots` is the join
  key to a per-player table: it says which side a player was on *in that round*, as a
  `player_round` table's `team` does.
- No `highlights:` and no `scoreboard:` (both are attributed to players). It may `use:` a
  `for: match` or another `for: each_team` ruleset, but not a per-player one, and it reads the used
  ruleset's stats in `compute:` only: a read in `where:`, `while:`, `capture:`, `sum:`, `tally:` or
  a bucket key is a validation error (`resolve.team-scope.unsupported`).

```yaml
ruleset: side_economy
for: each_team
stats:
  equipment: { capture: round.team.equipment, on: raw.round_freeze_end, per: round }
  money:     { capture: round.team.money,     on: raw.round_freeze_end, per: round }
  kills:     { count: kill, match: { enemy: true }, per: round }
  won:       { count: round_won, per: round }
show:
  tables:
    side_rounds:
      per: team_round
      columns:
        - { stat: equipment, label: Equipment }
        - { stat: money, label: Money }
        - { stat: kills, label: Kills }
        - { stat: won, label: Won }
```

`src/CS2DemoKit.Analysis/Rules/examples/round_facts.rules.yaml` is a complete one: the side's buy
(with its thresholds as `params:`), the decision, the plant, and the kill timeline with the
man-count after each kill, per round per side, every tick on the frame clock, and a `team_final`
table with each side's final score and clan name. A tick column of a
thing that did not happen that round reads `0`. A string column does not read `""`: a capture of
`round.bomb.site` or `round.bomb.plant_place` on a round with no plant projects as null (the
expression reads `""`, and an empty string capture comes out null), so test a table cell for null,
or use the int twin `round.bomb.site_entity`, which projects `-1`.

### Per-grenade stats: `for: each_projectile`
When the question is about a grenade (where it was thrown from, where it landed, how many times it
bounced, what the thrower was doing when it left the hand), use `for: each_projectile`. The ruleset
runs once per projectile: an instance materializes on `projectile_created`, lives while the grenade
flies, and on `projectile_removed` its stats are sampled into one row of a `per: projectile` table
and the instance is released. A grenade still in the air when the demo ends gets its row then.

- `projectile.*` reads the instance's row as of the frame the read happens on: `class` (the short
  name the views match on: `smoke`, `molotov`, `he`, `flash`, `decoy`), `class_name` (the server
  class), `thrower` (the slot, held from its first resolution), `team`, `entity_index`, `serial`,
  `pos_x` / `pos_y` / `pos_z` (the live position, `NaN` while unreadable), `initial_x` / `_y` /
  `_z` (where it left the hand), `velocity_x` / `_y` / `_z` (the release velocity), `bounces`,
  `first_seen_tick` (the frame-clock tick the grenade appeared on, the spawn: the created tick
  unless `projectile_created` waited for the thrower), `smoke_effect_tick` and
  `smoke_detonation_x` / `_y` / `_z` (a smoke's cloud: the server tick it began, 0 while the
  grenade flies, and where it popped, `NaN` until then and on every other class), `is_incendiary`
  and `explode_effect_tick` (the server tick the explosion effect began, 0 until it detonates). A
  capture of one of the tick members is labelled with its clock in `ColumnClocks`.
- A capture with no `on:` takes a bare `projectile.*` read: `keep: first` samples it on the frame
  the instance materializes, `keep: last` reads it live, so the table sees the value at removal.
  Anything else with no `on:` is `resolve.trigger.unsupported-for-kind`.
- `thrower.*` is the thrower's per-player read set (`thrower.is_walking`, `thrower.pos_x`, ...),
  read at the thrower's slot on the frame of the event that triggers the stat. It is in scope on
  every trigger, not only the `projectile_thrown` view that names the role, and it looks back like
  `player.*` does: `thrower.pos_x@-8` on `raw.projectile_created` is where the thrower stood 8 ticks
  before the grenade appeared, and `thrower.on_ground@-40` reaches the release of a long throw.
- `projectile_thrown` / `projectile_landed`, and the raw `projectile_created` /
  `projectile_removed`, bind to the instance: a stat on them fires for this grenade only. Any other
  event fires for every live instance, as it does in `for: match`.
- The kinds are `count`, `sum`, `capture`, a triggered `flag` and a round-end `compute`, which
  settles when the instance is sampled. `tally`, `streak`, `burst`, `bucket`, `rate`, a `flag:`
  with no trigger and a live `compute:` keep state the instance could not release, and are
  `resolve.projectile-scope.unsupported`, as are highlights. `per:` is accepted and ignored: the
  instance's whole life is its window. `player.*` is not in scope.
- It may `use:` a `for: match` ruleset, read in `compute:` only; nothing reads a projectile ruleset.
- The `projectile` table row carries `round_number` (the round the grenade was removed in),
  `first_seen_tick`, `created_tick` and `removed_tick` (frame clock, listed in `ColumnClocks`),
  `removed` (false for a grenade still in the air when the run ended, whose `removed_tick` is then
  the run's last tick), `entity_index`, `serial`, the thrower's `player_slot`, `player_name` and
  `steam_id`, and `team` (the grenade's).

```yaml
ruleset: grenade_rows
for: each_projectile
stats:
  class:        { capture: projectile.class,     keep: first }
  thrower:      { capture: projectile.thrower,   keep: first }
  spawn_x:      { capture: projectile.pos_x,     keep: first }
  end_x:        { capture: projectile.pos_x,     keep: last }
  bounces:      { capture: projectile.bounces,   keep: last }
  thrower_walk: { capture: thrower.is_walking,   on: raw.projectile_created }
show:
  tables:
    grenades:
      per: projectile
      columns: [ { stat: class }, { stat: thrower }, { stat: spawn_x }, { stat: end_x }, { stat: bounces }, { stat: thrower_walk } ]
```

The `projectile_created` an instance starts on waits for the thrower to resolve, so a
`keep: first` position can be a tick into flight; `initial_x` / `_y` / `_z` is the release point.

### Reusing another ruleset — `use:` / `exports:`
A stat can read `otherRuleset.stat` if your file declares `use: [otherRuleset]` and that ruleset
`exports:` it. This is how one file builds on another without copy-pasting.
```yaml
ruleset: kast
for: each_player
exports: [kast_pct]
stats: { ... }
---
ruleset: ratings
for: each_player
use: [kast]
stats:
  hltv: { compute: "0.73 * (kast.kast_pct / 100) + ..." }   # reads kast's exported stat
```
The engine catches every mistake: unknown ruleset, unknown stat, not-exported, not-in-`use:`, and
reference cycles — each with a clear message.

---

## 11. Reference maps of parameters & provenance

- **`params:`** let a ruleset take values (e.g. a threshold) that a reader can adjust; read them as
  `params.<name>` (currently in `tally:` thresholds and expressions).
- **`define:`** names a reusable list, trigger, or a **lookup map** read as `ref[key]`.
- **`catalog_version:` / `min_app_version:`** stamp which catalog/app a ruleset was written against.

---

## 12. When something's wrong

- `ValidateRulesets` attributes each diagnostic to its ruleset and gives you the offending text plus
  its `(line, column)` inside the expression, along with ranked "did you mean" candidates for typos.
- "unknown name 'X' in the … slot — available roots: …" means you referenced something not in scope
  (a common one: reading `player.*` in a `for: match` ruleset — there's no subject there).
- A stat can declare only **one** kind; two kinds on one stat is an error.
- Not sure a facet/view exists? The error lists the valid options, and
  `src/CS2DemoKit.Analysis/Rules/catalog.json` is the full list.
- A ruleset that fails to compose lands in `result.Excluded` with the diagnostics that dropped it —
  at analysis time the same information is on `BuildResult.RulesetDiagnostics` / `.ExcludedRulesets`.
  Check them, or a ruleset that stopped compiling looks identical to stats that never fired.
- A ruleset is dropped on one of two channels. Validation drops what the source shows is wrong,
  and `ValidateRulesets` reports it with no demo. The build drops what only lowering can find, with
  a `build.lowering-failed` diagnostic on `BuildResult.ExcludedRulesets` only (not on
  `RulesetDiagnostics`); a ruleset that `use:`s it goes with it. Either way the rest still run.
- Three shapes that validate in YAML but cannot be built are validation errors:
  `resolve.trigger.unsupported-for-kind` is a `net.<Message>` trigger on `capture keep: list|min|max`,
  `streak`, `burst` or `bucket`. `resolve.while.unsupported` is a compound `while:` (two gates, from a
  define and the site, count as one compound), or a value gate (a comparison, or
  `round.alive.in_clutch`) on a net trigger or on those same kinds; a comparison over a
  `player.*` entity read is fine wherever the kind has an event trigger.
  `resolve.keep-list.element-type` is a `keep: list` whose element is not an int, instant or duration.
- A stat that resolved but cannot measure on *this* run gets a row on `BuildResult.RulesetCoverage`
  instead: a view that does not bind on the demo's source profile, or a spot-derived facet on a run
  with no map bake (above). Those are the two ways a correct ruleset legitimately reports zero, and
  both are the kind you want to read rather than discover from the numbers.

Author small, check often, and grow the file a stat at a time. Every file under
`src/CS2DemoKit.Analysis/Rules/` is a working reference you can copy from.
