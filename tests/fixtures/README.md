# tests/fixtures/ — reference data for parity tests

Per-demo subdirectories named after the demo's filename (without `.dem`),
plus a couple of top-level fixture files. Every JSON here is reference data
that one or more tests assert against.

## Layout

```
tests/fixtures/
├── rules-v2/                          pinned outputs for the four baseline rulesets
├── <demo-id>/
│   ├── ours.golden.json               Stat snapshot produced by AnalysisBench
│   ├── expected.golden.json           Committed reference (see "Reliability posture" below)
│   └── entity-fields.ours.golden.json Per-tick entity-field snapshot (FuriaMirage only)
```

## Reliability posture — what each file means

The two stat-side files play different roles. Tests in `StatParityTests`
treat them differently:

| Provider | Source | Role |
|---|---|---|
| `ours` | `AnalysisBench --suite` reads the demo through our parser/analyzer | Reflects what our code currently produces. NOT a reference — it's the thing being measured. |
| `expected` | Committed reference values, seeded from engine output that was verified during the parity-hardening passes | **The reference.** When ours and expected disagree on a stat, the working assumption is that ours regressed until proven otherwise. |

The parity gate is zero-tolerance: `OursVsExpected_StatParity` fails on any
divergence. Never widen or edit the reference to absorb a diff — fix the
engine, or hand-verify the value and re-pin deliberately.

## Where the reference values came from, and where they're going

Today's `expected.golden.json` values are the engine's own verified output:
they were pinned after the parity-hardening passes, during which each stat
was checked against external references and per-event investigation (the
tick citations scattered through the edge tests and view comments are the
residue of that work). What the parity test catches is "our parser drifted
from its verified output" — real regression detection, without any live
external dependency.

The upgrade path for any individual value is hand-verification: a human
confirms the number by watching the demo, and the file's `provider_version`
field moves from `null` to something like
`"hand-verified-2026-XX-XX-by-NAME"`. At that point a failure means "our
parser disagrees with what a human confirmed" — the strongest signal the
suite can give.

## Refresh procedures

| File | Refresh procedure |
|---|---|
| `rules-v2/*.expected.json` | Re-run the pilot tests with `PIN_RULES_V2=1` and the pinning demo available. Deliberate, reviewed re-pin only — the fixtures are the assertion. |
| `ours.golden.json` | Produced by the analysis benchmark suite, which lives in the application repo this library was extracted from; it is not part of this repo. |
| `expected.golden.json` | **Not auto-refreshable.** A re-pin from a verified `ours` snapshot (or a hand-verified edit) is a deliberate, reviewed change — the fixture is the assertion. |
| `entity-fields.ours.golden.json` | Produced by the entity-field diff tool, also in the application repo (it additionally needs a sibling demofile-net checkout as the oracle). |

## Schema versioning

Every JSON file has a `schema_version` field. Today schemas are at v1.
Breaking changes to a schema (new required field, removed field,
renamed key) should bump the version and update the loader. The current
loaders don't enforce version compatibility yet — that's a follow-up
when a v2 actually exists.

## What's not in here

- **The demo files these fixtures were computed from.** Those `.dem` files run
  200–300 MB each and are gitignored, so every test that compares against a
  fixture here skips in a clone that does not have them. The committed
  `tests/assets/sample-de_nuke.dem` is a four-round trim and deliberately does
  **not** satisfy them — see `DemoTestHelper.AllowSampleDemo`.
- **Cross-provider mappings.** Each provider's converter (in
  `src/CS2DemoKit.Analysis/GoldenStats/`) owns its own mapping from raw input
  to the canonical schema.
