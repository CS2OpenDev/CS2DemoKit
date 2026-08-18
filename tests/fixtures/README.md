# tests/fixtures/ — reference data for parity tests

Per-demo subdirectories named after the demo's filename (without `.dem`),
plus a couple of top-level fixture files. Every JSON here is reference data
that one or more tests assert against.

## Layout

```
tests/fixtures/
├── cs2-opendocs.expected-sha          submodule pin
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

| File | Refresh command |
|---|---|
| `ours.golden.json` | `dotnet run -c Release --project tools/AnalysisBench -- --suite` |
| `leetify.golden.json` | Same — bench writes both as a side-effect. |
| `expected.golden.json` | **Not auto-refreshable.** Manual edit when hand-verifying. |
| `entity-fields.ours.golden.json` | `dotnet run --project tools/DemoViewer.NET.EntityFieldDiff -- <demo> --write-snapshot` (requires the gitignored EntityFieldDiff tool + sibling demofile-net repo). |
| `cs2-opendocs.expected-sha` | Manual edit when intentionally bumping the schema submodule (see `SchemaSnapshotTests` for the procedure). |

## Schema versioning

Every JSON file has a `schema_version` field. Today schemas are at v1.
Breaking changes to a schema (new required field, removed field,
renamed key) should bump the version and update the loader. The current
loaders don't enforce version compatibility yet — that's a follow-up
when a v2 actually exists.

## What's not in here

- **The demo files themselves.** `.dem` files are 200–300 MB each and
  gitignored. Provisioning them is deferred work; until then, fixture
  refreshes are a maintainer activity (the maintainer has the demos
  locally).
- **Per-stat tolerances.** Lives in `StatParityTests.Tolerances`.
- **Cross-provider mappings.** Each provider's converter (in
  `src/Analysis/.../GoldenStats/`) owns its own mapping from raw input
  to the canonical schema.
