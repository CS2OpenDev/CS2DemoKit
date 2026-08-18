#region

using CS2DemoKit.Analysis.Building;
using CS2DemoKit.Analysis.Catalog;
using CS2DemoKit.Analysis.Config;
using CS2DemoKit.Analysis.Graphs;
using CS2DemoKit.Analysis.Plugins;
using CS2DemoKit.Analysis.Registry;
using CS2DemoKit.Analysis.RulesetsV2.Model;
using CS2DemoKit.Analysis.RulesetsV2.Resolve;
using CS2DemoKit.Analysis.Yaml;
using CS2DemoKit.Parser;

#endregion

namespace CS2DemoKit.Analysis.Tests.RulesV2;

/// <summary>
///     Shared scaffolding for the v2 kind golden tests: compiles an in-memory v2 ruleset through
///     the full resolve + planner pipeline against a demo's real tick rate and source profile, and
///     reads per-player runtime node values back out of the materialized graphs (the
///     <c>MaterializedPlayers</c> surface), so a v2 node can be compared against an independent
///     oracle per player.
/// </summary>
internal static class V2KindGoldenSupport
{
    /// <summary>Compiles an in-memory v2 ruleset string into a ready-to-evaluate build.</summary>
    /// <param name="demo">The parsed demo (its tick rate + source profile drive the build re-pass).</param>
    /// <param name="yaml">The v2 ruleset YAML.</param>
    /// <returns>The composed build.</returns>
    internal static BuildResult CompileV2(ParsedDemo demo, string yaml)
    {
        RulesetDocumentLoader.Outcome outcome = RulesetDocumentLoader.Load(yaml, "golden.rules.yaml");
        RulesetDoc doc = outcome.Doc
                         ?? throw new InvalidOperationException(
                             "v2 ruleset failed to load: " + string.Join("; ", outcome.Diagnostics));
        if (outcome.Diagnostics.Count > 0)
        {
            throw new InvalidOperationException(
                "v2 ruleset has mapping/structural diagnostics: " + string.Join("; ", outcome.Diagnostics));
        }

        RuleChainBuilder builder = new(
            EventRegistry.Build(),
            demo,
            entityProviders: EntityValueProviderRegistry.CreateDefault(),
            perPlayerEntityProviders: PerPlayerEntityValueProviderRegistry.CreateDefault());

        string profileId = builder.Profile.GetType().Name;
        CatalogScopeAdapter adapter = CatalogScopeAdapter.From(CatalogResource.Load());
        RulesetResolveResult resolved = CheckedRulesetDraft.Load(doc, adapter).Build(demo.TickRate, profileId);
        CheckedRuleset ruleset = resolved.Ruleset
                                 ?? throw new InvalidOperationException(
                                     "v2 ruleset failed to resolve: " + string.Join("; ", resolved.Diagnostics));

        return builder.Build([ruleset]);
    }

    /// <summary>Locates the repo root by walking up from the test assembly until the solution file is found.</summary>
    /// <returns>The repo root directory.</returns>
    internal static string FindRepoRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "CS2DemoKit.slnx"))
                || File.Exists(Path.Combine(dir.FullName, "CS2DemoKit.sln")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("repo root not found");
    }
}
