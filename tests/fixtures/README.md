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
│   ├── leetify.golden.json            Stat snapshot converted from Leetify API JSON
│   ├── expected.golden.json           Curated reference (see "Reliability posture" below)
│   └── entity-fields.ours.golden.json Per-tick entity-field snapshot (FuriaMirage only)
```

## Reliability posture — what each file means

The three stat-side providers (`ours`, `leetify`, `expected`) are NOT
equally trustworthy. Tests in `StatParityTests` treat them differently:

| Provider | Source | Trust level today |
|---|---|---|
| `ours` | `AnalysisBench --suite` reads the demo through our parser/analyzer | Reflects what our code currently produces. NOT a reference — it's the thing being measured. |
| `leetify` | Leetify's public `?include=playerStats` API response, converted via `LeetifyGoldenStatsConverter` | **The current gold standard.** When ours and Leetify disagree on a stat, the working assumption is that ours is wrong until proven otherwise. |
| `expected` | Hand-curated values | **Not yet reliable.** Today's files were seeded from ours+leetify agreement, NOT from a human watching the demo. Function: parser-regression tripwire only. |

## Why `expected` exists if it's not yet hand-verified

The intent is for `expected.golden.json` to become the load-bearing ground
truth that unblocks the oracle sunset (dropping the live Leetify API
dependency from CI). That requires actual hand-verification.

Today's seed files were written from values where `ours` and `leetify`
agreed exactly on a chosen demo. They serve two interim purposes:

1. **Parser regression detection** — if ours produces a different value
   for a stat the seed has, the test fails. That catches our parser
   drifting from its own past output, even without a human in the loop.
2. **Infrastructure proof** — the schema, the loader, the parity-test
   shape all exist and work. Replacing seeded values with hand-verified
   values is a content swap, no code change required.

When hand-verification work happens, the file's `provider_version` field
will move from `null` to something like `"hand-verified-2026-XX-XX-by-NAME"`,
and the oracle-sunset clock starts.

## Refresh procedures

| File | Refresh procedure |
|---|---|
| `rules-v2/*.expected.json` | Re-run the pilot tests with `PIN_RULES_V2=1` and the pinning demo available. Deliberate, reviewed re-pin only — the fixtures are the assertion. |
| `ours.golden.json` | Produced by the analysis benchmark suite, which lives in the application repo this library was extracted from; it is not part of this repo. |
| `leetify.golden.json` | Same source — the bench writes both as a side-effect. |
| `expected.golden.json` | **Not auto-refreshable.** Manual edit when hand-verifying. |
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
- **Per-stat tolerances.** Lives in `StatParityTests.Tolerances`.
- **Cross-provider mappings.** Each provider's converter (in
  `src/CS2DemoKit.Analysis/GoldenStats/`) owns its own mapping from raw input
  to the canonical schema.
