# CS2DemoKit.Codegen

Emits `src/CS2DemoKit.Parser/Entities/Generated/SchemaLens.Generated.cs` — the
`GeneratedLensRegistry` that lane-binds `EntityState` (which curated engine fields land on
the typed int/float/object lanes). The SDK-emitted wrappers read THROUGH those lanes via the
`SdkAbstractions` seam, so the emit is load-bearing even though no wrapper code is generated
here.

The registry is **derived from the pinned `CS2OpenDev.Sdk.Entities` package** — the SDK is
the single curation authority for CS2 object definitions, and schema-drift history lives in
its migration files. The local Schema Lens migration JSONs (and their replay/hash machinery)
were retired 2026-08-15, parity-proven first: all 162 legacy rules reproduced exactly, then
extended to the full SDK curation (735 rules / 61 concrete classes).

Two derivation inputs:

1. `EntityWrapperRegistry.Bindings` (compile-time package reference) — classes, the
   prefix-flattened canonical path list per class, and the alias tables.
2. The SDK's `schema-lens/state.json` — the per-canonical `schemaType` the assemblies
   don't carry (the one fact that decides the lane). Passed via `--state` (sibling
   checkout) until the nupkg embeds it —
   [SDK#44](https://github.com/CS2OpenDev/CS2OpenDev-SDK/issues/44) (embed + cell-alias asks).

What stays DVN-owned is code, not data: `SchemaLensSdkDeriver.MapType` — the
`schemaType → (lane, transform)` storage-policy mapping. Lanes are HONEST (they state the
lane the decoder's honour-the-wire routing actually uses), the only transform is the
`HandleIndex` marker, and an unmapped schemaType fails loudly, never guesses. Plus an
interim wire-flattening alias for the origin cell/vec leaves (see SDK#44; becomes a no-op
when upstream ships the aliases).

## Regen recipe (on every `Sdk.Entities` pin bump)

```sh
# 1. Bump the pin (Directory.Packages.props), audit the tag diff.
# 2. Regenerate — the emit is deterministic; run twice → identical file:
dotnet run --project tools/CS2DemoKit.Codegen -- --schemalens --state ../CS2OpenDev-SDK/schema-lens/state.json
# 3. REBUILD consumers (stale binaries are the classic trap), then verify:
#    - Parser suite (SchemaLensGeneratedTests census pins + the SDK battery)
#    - golden A/B (StatParityTests against tests/fixtures/, which runs the engine live)
```

A changed `LensHash` after a pin bump is expected (new curation); a changed hash WITHOUT a
pin bump means the emit is stale or hand-edited — regenerate.

Retired flags fail loudly rather than resurrect deleted machinery: `--entities`,
`--schemalens-slots`, `--schemalens-wrappers` (the local wrapper layer, deleted in the SDK
cutover), `--schemalens-hash-genesis`, `--schemalens-parity` (the migration-JSON era). Game
events are not generated here either — records ship in `CS2OpenDev.Sdk.GameEvents`.
