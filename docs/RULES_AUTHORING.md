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
- **Team aggregates (subject-relative):** `round.team.alive` / `round.enemies.alive`,
  `round.team.players` / `round.enemies.players`, `round.team.equipment` /
  `round.enemies.equipment`, `round.alive.in_clutch`.
- **Entity state:** `player.health` / `player.armor` / `player.equipment_value` /
  `player.active_weapon_clip` / `player.active_weapon_class` / `player.place` — the player's live
  pawn state. (`active_weapon_clip` is the magazine count of the currently held weapon — under the
  pre-frame timing below, at a kill event it is the clip BEFORE the killing shot, so "last bullet"
  reads `== 1`; no-magazine weapons like knives read `-1`. `place` is the human-readable nav-mesh
  place name the pawn last occupied — `"BombsiteA"`, `"TSpawn"`, `"Ramp"`, … — a string; names come
  from the map's nav mesh, so gate on the standard ones
  (`BombsiteA`/`BombsiteB`/`CTSpawn`/`TSpawn`) for map-portable rules.)

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

Author small, check often, and grow the file a stat at a time. Every file under
`src/CS2DemoKit.Analysis/Rules/` is a working reference you can copy from.
