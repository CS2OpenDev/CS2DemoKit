# tests/fixtures/ — reference data for parity tests

Per-demo subdirectories named after the demo's filename (without `.dem`), plus
`rules-v2/` for the pinned ruleset outputs. Every JSON here is reference data
that one or more tests assert against.

## Layout

```
tests/fixtures/
├── rules-v2/                          pinned outputs for the four baseline rulesets
├── <demo-id>/
│   ├── expected.golden.json           The reference. See "Posture" below.
│   └── entity-fields.ours.golden.json Per-tick entity-field snapshot (FuriaMirage only)
```

## Posture: only the reference is committed

**`ours` is never stored.** It is what the code currently produces, so it is
derived by running the engine end to end over the demo, every time
(`LiveGoldenStats.Derive`). Only `expected.golden.json` is committed, and it is
the assertion.

This is the whole point. `StatParityTests` used to load *both* sides off disk,
so it compared two committed files that agreed by construction and could not
fail. Because it never opened a `.dem` it did not even skip, so it reported
green in CI while asserting nothing about the engine. That is how an
enemy-damage regression survived about six weeks and shipped with a fully green
suite.

The trade is honest and worth it: a demo that is not on this machine now
**skips** rather than silently passing. Of the demos referenced here, only
`sample-de_nuke` is committed, so a bare clone exercises the gate on that one
and skips the rest.

When `ours` and `expected` disagree, the working assumption is that the engine
regressed until proven otherwise.

## Where the reference values came from

They are the engine's own verified output, pinned after the parity-hardening
passes, during which each stat was checked against external references and
per-event investigation. The tick citations scattered through the edge tests
and view comments are the residue of that work.

So the gate detects **drift**, not incorrectness: it says the engine still
produces what it produced when this was pinned. The upgrade path for any
individual value is hand-verification. A human confirms the number by watching
the demo, and the file's `provider_version` moves from `null` to something like
`"hand-verified-2026-XX-XX-by-NAME"`. At that point a failure means the parser
disagrees with what a human confirmed, which is the strongest signal the suite
can give.

## Refresh procedures

| File | Refresh procedure |
|---|---|
| `<demo-id>/expected.golden.json` | `PIN_EXPECTED=1` with the demo present. **Deliberate, reviewed re-pin only:** the fixture is the assertion. Never re-pin to absorb a diff; fix the engine, or hand-verify and re-pin on purpose. |
| `rules-v2/*.expected.json` | Re-run the pilot tests with `PIN_RULES_V2=1` and the pinning demo available. Same rule: deliberate and reviewed. |
| `entity-fields.ours.golden.json` | Produced by the entity-field diff tool, which lives in the application repo and additionally needs a sibling demofile-net checkout as its oracle. |

To add a demo to the gate: create `tests/fixtures/<demo-filename-without-dem>/`,
put the demo where `DemoTestHelper` can find it, and run with `PIN_EXPECTED=1`.
Review the generated file before committing it.

## A note on `entity-fields.ours.golden.json`

Despite the name it plays the `expected` role: `EntityFieldSnapshotTests`
re-runs the parser at the snapshot's pinned ticks and diffs against it, so it is
a committed reference compared against live output, not a stored `ours`. The
name is kept because the external tool that writes it emits that filename.

## Schema versioning

Every JSON file has a `schema_version` field. Today schemas are at v1. Breaking
changes to a schema (new required field, removed field, renamed key) should bump
the version and update the loader. The current loaders don't enforce version
compatibility yet, which is a follow-up for when a v2 actually exists.

## What's not in here

- **Most of the demo files these fixtures describe.** Those `.dem` files run
  200–300 MB each and are gitignored, so their parity cases skip in a clone that
  does not have them. `tests/assets/sample-de_nuke.dem` is the exception: it is a
  four-round trim, committed, and its fixture is pinned from it.
- **Cross-provider mappings.** Each provider's converter (in
  `src/CS2DemoKit.Analysis/GoldenStats/`) owns its own mapping from raw input to
  the canonical schema.
