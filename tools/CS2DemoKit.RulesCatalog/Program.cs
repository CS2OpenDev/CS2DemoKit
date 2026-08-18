#region

using System.Text.Json;
using System.Text.Json.Serialization;
using CS2DemoKit.Analysis.Catalog;
using CS2DemoKit.RulesCatalog;

#endregion

// Catalog generator. Codegen-project pattern: the output
// is COMMITTED at rules/catalog.json and a CI drift test asserts regen == committed, so the
// catalog can never silently diverge from the engine's registries.
//
//   dotnet run --project tools/CS2DemoKit.RulesCatalog            → (re)write rules/catalog.json
//   dotnet run --project tools/CS2DemoKit.RulesCatalog -- --check → exit 1 if committed file is stale

string repoRoot = FindRepoRoot();
string rulesDir = Path.Combine(repoRoot, "src", "CS2DemoKit.Analysis", "Rules");
string outputPath = Path.Combine(rulesDir, "catalog.json");
string v2SchemaPath = Path.Combine(rulesDir, "cs2demokit-rules.schema.json");
bool checkOnly = args.Contains("--check");

// --measure: explicit frequency re-baseline — parses every demos/benchmarks/*.dem
// and rewrites the committed frequency-baseline.json before the normal regen. Ordinary runs
// (and --check) merge the committed baseline, keeping output deterministic without demos.
if (args.Contains("--measure"))
{
    string benchDir = Path.Combine(repoRoot, "demos", "benchmarks");
    Console.WriteLine($"Measuring trigger frequencies from {benchDir}…");
    FrequencyBaseline measured = FrequencyBaseline.Measure(benchDir);
    string baselineOut = Path.Combine(repoRoot, FrequencyBaseline.RelativePath);
    File.WriteAllText(baselineOut, measured.Serialize());
    Console.WriteLine($"Wrote {baselineOut} ({measured.MaxPerDemo.Count} names from {measured.MeasuredFrom.Count} demos).");
}

CatalogRoot catalog = CatalogBuilder.Build(FrequencyBaseline.Load(repoRoot));
string json = CatalogJson.Serialize(catalog);
// The v2 ruleset schema (compiler-plan §9) is generated fresh from the catalog's
// views family (per-view match: facet enums, kind if/then, destination enums, defaultSnippets).
string v2Schema = RulesSchemaBuilder.Build(catalog);

if (checkOnly)
{
    string committed = File.Exists(outputPath) ? File.ReadAllText(outputPath) : "";
    string committedV2Schema = File.Exists(v2SchemaPath) ? File.ReadAllText(v2SchemaPath) : "";
    if (committed != json || committedV2Schema != v2Schema)
    {
        Console.Error.WriteLine(
            "DRIFT: rules/catalog.json or rules/cs2demokit-rules.schema.json is stale.");
        Console.Error.WriteLine("Run: dotnet run --project tools/CS2DemoKit.RulesCatalog");
        return 1;
    }

    Console.WriteLine(
        $"catalog.json + schema are in sync ({json.Length} + {v2Schema.Length} bytes).");
    return 0;
}

File.WriteAllText(outputPath, json);
File.WriteAllText(v2SchemaPath, v2Schema);
Console.WriteLine(
    $"Wrote {outputPath} ({json.Length} bytes) + v2 schema ({v2Schema.Length} bytes).");
return 0;

static string FindRepoRoot()
{
    DirectoryInfo? dir = new(AppContext.BaseDirectory);
    while (dir is not null)
    {
        if (Directory.Exists(Path.Combine(dir.FullName, ".git"))
            || File.Exists(Path.Combine(dir.FullName, "CS2DemoKit.slnx")))
        {
            return dir.FullName;
        }

        dir = dir.Parent;
    }

    throw new InvalidOperationException("repo root not found from " + AppContext.BaseDirectory);
}

namespace CS2DemoKit.RulesCatalog
{
    /// <summary>
    ///     Shared serializer settings: deterministic camelCase JSON, LF newlines, trailing
    ///     newline — the drift test byte-compares, so the format is part of the contract.
    /// </summary>
    public static class CatalogJson
    {
        private static readonly JsonSerializerOptions _options = new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        /// <summary>Serializes a catalog with the committed-format contract applied.</summary>
        public static string Serialize(CatalogRoot catalog) =>
            JsonSerializer.Serialize(catalog, _options).ReplaceLineEndings("\n") + "\n";
    }
}
