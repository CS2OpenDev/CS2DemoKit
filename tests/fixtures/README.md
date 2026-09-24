# tests/fixtures/ — reference data for parity tests

Per-demo subdirectories named after the demo's filename (without `.dem`), plus
`rules-v2/` for the pinned ruleset outputs and `usercmds-delta/` for a window of
raw player input. Every file here is reference data that one or more tests
assert against.

## Layout

```
tests/fixtures/
├── rules-v2/                          pinned outputs for the four baseline rulesets
├── usercmds-delta/<demo-id>.cmds.bin  raw svc_UserCmds payloads from a build-10896 demo
├── usercmds-delta/<demo-id>.cmds.sha256  digest of the commands rebuilt from that window
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
| `usercmds-delta/<demo-id>.cmds.bin` | `PIN_USERCMDS=1` with `DEMO_PATH` set to a demo on build 10896 or later, running `UserCmdFixtureTests`. **Deliberate re-pin only.** The file is wire input, not engine output: the opening full packet's snapshots plus about 1,500 following payloads, widened until every rule of the delta grammar occurs. The same run writes `<demo-id>.cmds.sha256`, and only after the whole source demo rebuilds with every full-packet snapshot matching the command rebuilt at its number. The demo is read in place and never copied. |
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

## `usercmds-delta/`

The committed sample demo has no `svc_UserCmds`, so this is the only real
`delta_data` a bare clone has. `UserCmdFixtureTests` replays it through
`UserCmdReconstructor` and requires every command to rebuild with a
`base.client_tick` that matches the outer `client_tick`, and hashes every
rebuilt command against the pinned `.cmds.sha256`. The window has no second
full packet (they are further apart than a window worth committing), so the
digest is what checks the rest of each command: subtick moves, input history,
buttons, view angles. It is pinned from a rebuild that the source demo's own
full-packet snapshots confirmed in full (490 of them on the current pin, no
mismatches). Records are
`[u8 kind: 0 packet, 1 full packet][u32 LE length][CSVCMsg_UserCommands payload]`.
The payloads hold player input only (buttons, view angles, movement); there are
no Steam IDs in them. `.gitattributes` marks `*.bin` binary so the LF rule never
touches it.

## What's not in here

- **Most of the demo files these fixtures describe.** Those `.dem` files run
  200–300 MB each and are gitignored, so their parity cases skip in a clone that
  does not have them. `tests/assets/sample-de_nuke.dem` is the exception: it is a
  four-round trim, committed, and its fixture is pinned from it.
- **Cross-provider mappings.** Each provider's converter (in
  `src/CS2DemoKit.Analysis/GoldenStats/`) owns its own mapping from raw input to
  the canonical schema.
