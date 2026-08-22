// Smoke consumer for the packed CS2DemoKit packages. Exercises the two things a package can break
// without the in-repo build noticing: that the public API is reachable through the package
// reference, and that the baseline rulesets are actually embedded in the shipped assembly.
//
// Usage: dotnet run -- <path-to-demo.dem>   (the repo's tests/assets sample works)

using CS2DemoKit.Analysis;
using CS2DemoKit.Analysis.Yaml;
using CS2DemoKit.Parser;
using CS2DemoKit.Parser.GameEvents;
using CS2OpenSchema.Events;

if (args.Length < 1)
{
    Console.Error.WriteLine("usage: NuGetSmokeConsumer <demo.dem>");
    return 2;
}

ParsedDemo demo = MemoryMappedDemoSource.ParseFile(args[0]);

int kills = demo.AllGameEvents.Count(e => e.Payload is PlayerDeathEvent);
Console.WriteLine($"parsed {demo.Frames.Count} frames, {demo.AllGameEvents.Count} game events, {kills} kills");

RuleConfigLoadResult loaded = YamlConfigLoader.LoadShippedEmbedded();
if (!loaded.Success)
{
    Console.Error.WriteLine("embedded rulesets failed to load: " + string.Join("; ", loaded.Errors));
    return 1;
}

string[] ids = loaded.Rulesets.Select(r => r.Id).OrderBy(id => id, StringComparer.Ordinal).ToArray();
Console.WriteLine($"embedded rulesets ({ids.Length}): {string.Join(", ", ids)}");

// The baseline set is a contract of the package, not an accident of what happened to be embedded.
string[] expected = ["kast", "player_stats", "post_plant_double", "weapon_stats"];
if (!ids.SequenceEqual(expected))
{
    Console.Error.WriteLine($"expected baseline rulesets [{string.Join(", ", expected)}]");
    return 1;
}

// The analysis entry point, which is the README's quick start and the reason most consumers take
// CS2DemoKit.Analysis at all. Left uncalled, a rename here reaches consumers as a compile error and
// nothing in CI notices, because the in-repo tests use the internals rather than this surface.
AnalysisRun run = DemoAnalysis.Run(demo, loaded.Rulesets);
Console.WriteLine($"analysis ran: {run.Timeline.Events.Count} rule-chain events");

Console.WriteLine("smoke: OK");
return 0;
