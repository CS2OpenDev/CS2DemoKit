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

- **`for:`** — do you want this *per player* (`each_player`) or for the *whole match* (`match`)?
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
`round_won` · `round_lost`

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

If you need a raw event with no view, use `raw.<event>`; net messages are `net.<Message>`. Views
are almost always what you want.

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
`molotov`, `round_decided`) there is no server stamp, and `event.tick` is the frame clock too.

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
  `round.bomb.was_planted`, `round.clutch.size`. (Winning a round is a *view* — `round_won` /
  `round_lost` — not a context.)
- **Match facts:** `match.map`, `match.phase`, `match.live`, `match.half_state`,
  `match.regulation_status`, `match.freeze_period`.
- **Game rules:** the server's own round state, read straight off the game-rules entity. One value
  for the whole game, so they read the same in every scope.
  - `match.round_win_status` — `0` while the round is undecided, `2` once the terrorists have won
    it, `3` once the counter-terrorists have.
  - `match.round_win_reason` — the engine's round-end reason, set with the status: `7` bomb
    defused, `8` counter-terrorists eliminated the terrorists, `9` terrorists eliminated the
    counter-terrorists, `12` target saved (time ran out). `0` while undecided.
  - `match.total_rounds_played` — rounds decided so far this match; `0` before round 1. It
    increments on the frame a round is decided, not when the next one starts.
  - `match.game_phase` — `2` first half, `4` the halftime break, `3` second half, `5` match over.
  - `match.bomb_planted` — true from the plant; cleared by a defuse as well as at the round's close,
    so "was the bomb planted this round" is `round.bomb.was_planted`, not this.
  - `match.round_time` — the length the round is configured to run, in seconds (`115` in a live
    matchmaking round, `999` in warmup). Not a countdown.

  **Read the status and reason between the decision and the round's close.** They go back to `0`
  at `round_officially_ended`, 448 ticks after the round is decided on a matchmaking demo, and that
  is the event a round-end stat fires on, so a round-end read of either is `0`. At the round's
  close the winner is `enrich.round.winner_side`.
- **Team aggregates (subject-relative):** `round.team.alive` / `round.enemies.alive`,
  `round.team.players` / `round.enemies.players`, `round.team.equipment` /
  `round.enemies.equipment`, `round.alive.in_clutch`.
- **Entity state:** `player.health` / `player.armor` / `player.equipment_value` /
  `player.active_weapon_clip` / `player.active_weapon_class` / `player.place` — the player's live
  pawn state. (`active_weapon_clip` is the magazine count of the currently held weapon — under the
  pre-frame timing below, at a kill event it is the clip BEFORE the killing shot, so "last bullet"
  reads `== 1`; knives, grenades and the C4 read `-1`, and an empty magazine reads `0`. `place` is the human-readable nav-mesh
  place name the pawn last occupied — `"BombsiteA"`, `"TSpawn"`, `"Ramp"`, … — a string; names come
  from the map's nav mesh, so gate on the standard ones
  (`BombsiteA`/`BombsiteB`/`CTSpawn`/`TSpawn`) for map-portable rules.)
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
`while:`), an entity read is the value *at the moment of the event* (e.g. the victim's HP at the
kill). In a node-logic site (`compute:`, `flag: when:`), it's the value at *round end / evaluation
time*. Both are useful — pick the site that matches the question. (Under an event view you can also
read a role's entity state, using the role name in place of `player`: `victim.health` in a
`kill`-view `where:`.)

Contexts are per-player, so they're only available in a `for: each_player` ruleset. A `for: match`
ruleset has no subject and cannot read `player.*` or the team aggregates.

---

## 8. Displaying results — `show:`

- **`scoreboard:`** — per-player columns. `{ stat: <name or highlight.count>, label:, group: }`.
  `group:` is usually `round` (per-round columns) or `game` (match totals).
- **`tables:`** — richer per-round or per-match tables, written as a **named map**
  (`tables: { <table-name>: { per:, columns: [...] } }`). Use `per: match` on a table (in a
  `for: match` ruleset) for a single match-level row.
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
  total_rounds: { count: round_won, per: match }
show:
  tables:
    match_summary:                         # tables: is a named map: <table-name>: { per, columns }
      per: match
      columns:
        - { stat: total_kills, label: TotalK }
        - { stat: total_rounds, label: Rounds }
```

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
- A stat that resolved but cannot measure on *this* run gets a row on `BuildResult.RulesetCoverage`
  instead: a view that does not bind on the demo's source profile, or a spot-derived facet on a run
  with no map bake (above). Those are the two ways a correct ruleset legitimately reports zero, and
  both are the kind you want to read rather than discover from the numbers.

Author small, check often, and grow the file a stat at a time. Every file under
`src/CS2DemoKit.Analysis/Rules/` is a working reference you can copy from.
