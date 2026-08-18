/// <summary>
/// CS2DemoKit.Codegen — run manually when a new CS2OpenDev.Sdk.Entities release lands.
///
/// Usage:
///   dotnet run --project tools/CS2DemoKit.Codegen -- --schemalens --state <path-to-schema-lens/state.json>
///
/// Game events are NOT generated here (CS2OpenDev.Sdk.GameEvents package). Typed entity
/// wrappers are NOT generated here either since the SDK cutover — they ship in the
/// CS2OpenDev.Sdk.Entities package. The lens registry is DERIVED from that package, not
/// authored locally: local migration JSONs retired 2026-08-15 (the SDK is the single
/// curation authority; schema-drift history lives in ITS migration files). Retired flags
/// (--entities, --schemalens-slots, --schemalens-wrappers, --schemalens-hash-genesis,
/// --schemalens-parity) fail loudly rather than resurrect deleted machinery. A bare run
/// prints usage and exits non-zero.
///
/// Flags (explicit only):
///   --schemalens --state <path>  Derive the lens registry from the pinned
///                            CS2OpenDev.Sdk.Entities bindings plus the SDK's
///                            schema-lens/state.json (per-canonical schemaType metadata the
///                            assemblies don't carry — sibling CS2OpenDev-SDK checkout until
///                            the nupkg embeds it, SDK#44), and emit
///                            Generated/SchemaLens.Generated.cs (the GeneratedLensRegistry
///                            that lane-binds EntityState — the SDK wrappers read THROUGH
///                            those lanes).
///                            Writes: Entities/Generated/SchemaLens.Generated.cs
/// </summary>

#region

using CS2DemoKit.Codegen;

#endregion

// Retired flags fail loudly so a stale script cannot silently resurrect deleted machinery:
// --entities / --schemalens-slots / --schemalens-wrappers emitted the local wrapper layer
// (deleted in the SDK cutover); --schemalens-hash-genesis and --schemalens-parity serviced
// the local migration JSONs (deleted when derivation replaced replay, 2026-08-15).
string[] retired =
[
    "--entities", "--schemalens-slots", "--schemalens-wrappers",
    "--schemalens-hash-genesis", "--schemalens-parity",
];
if (retired.Any(args.Contains))
{
    Console.Error.WriteLine(
        "ERROR: retired flag. Typed wrappers ship in CS2OpenDev.Sdk.Entities, and the lens "
        + "registry is DERIVED from that package (--schemalens --state <state.json>); the local "
        + "wrapper emitters and migration-JSON machinery are deleted.");
    return 1;
}

if (!args.Contains("--schemalens"))
{
    Console.Error.WriteLine(
        "No generator flag given. Use --schemalens --state <path-to-schema-lens/state.json>.");
    return 1;
}

string? statePath = null;
int stateIdx = Array.IndexOf(args, "--state");
if (stateIdx >= 0 && stateIdx + 1 < args.Length)
{
    statePath = args[stateIdx + 1];
}

if (statePath is null)
{
    Console.Error.WriteLine(
        "ERROR: --schemalens requires --state <path-to-schema-lens/state.json> (the SDK's "
        + "state file; sibling CS2OpenDev-SDK checkout until the nupkg embeds it).");
    return 1;
}

string? repoRoot = FindRepoRoot();
if (repoRoot is null)
{
    Console.Error.WriteLine("ERROR: Could not locate repo root (CS2DemoKit.slnx not found).");
    return 1;
}

string outputDir = Path.Combine(
    repoRoot, "src", "CS2DemoKit.Parser", "Entities", "Generated");

return SchemaLensGenerator.RunFromSdk(statePath, outputDir);

static string? FindRepoRoot()
{
    string? dir = AppContext.BaseDirectory;
    for (int i = 0; i < 15 && dir is not null; i++)
    {
        if (File.Exists(Path.Combine(dir, "CS2DemoKit.slnx")))
        {
            return dir;
        }

        dir = Path.GetDirectoryName(dir);
    }

    return null;
}
