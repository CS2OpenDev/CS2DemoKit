#region

using CS2DemoKit.Analysis.Catalog;
using CS2DemoKit.RulesCatalog;

#endregion

namespace CS2DemoKit.Analysis.Tests;

/// <summary>
///     Gates for the generated rules catalog: the
///     committed <c>rules/catalog.json</c> must match a regen from the live registries (the
///     Codegen-project drift convention — the catalog can never silently diverge from the
///     engine), and the embedded runtime copy must load and agree with it. Pure in-memory —
///     no demo file.
/// </summary>
[Category("Unit")]
public class CatalogDriftTests
{
    /// <summary>
    ///     Regen == committed. A red run here means someone changed a registry (events,
    ///     enrichments, contexts, providers, profiles) without regenerating:
    ///     <c>dotnet run --project tools/CS2DemoKit.RulesCatalog</c>.
    /// </summary>
    [Test]
    public async Task CommittedCatalog_MatchesRegenFromLiveRegistries()
    {
        string committedPath = Path.Combine(FindRepoRoot(), "src", "CS2DemoKit.Analysis", "Rules", "catalog.json");
        await Assert.That(File.Exists(committedPath)).IsTrue()
            .Because("rules/catalog.json is a committed artifact");

        string committed = await File.ReadAllTextAsync(committedPath);
        string regen = CatalogJson.Serialize(
            CatalogBuilder.Build(FrequencyBaseline.Load(FindRepoRoot())));

        await AssertNotACrlfCheckout(committed, "catalog.json");
        await Assert.That(committed).IsEqualTo(regen)
            .Because("the committed catalog drifted from the engine's registries — "
                     + "run: dotnet run --project tools/CS2DemoKit.RulesCatalog");
    }

    /// <summary>
    ///     Schema generation: the generated v2 ruleset schema
    ///     <c>rules/cs2demokit-rules.schema.json</c> is drift-gated too — its per-view <c>match:</c> facet
    ///     enums, kind if/then, destination enums, and reserved-shape annotations must match a regen
    ///     from the same catalog's <c>views</c> family.
    /// </summary>
    [Test]
    public async Task CommittedV2Schema_MatchesRegenFromLiveCatalog()
    {
        string v2SchemaPath = Path.Combine(FindRepoRoot(), "src", "CS2DemoKit.Analysis", "Rules", "cs2demokit-rules.schema.json");
        await Assert.That(File.Exists(v2SchemaPath)).IsTrue()
            .Because("rules/cs2demokit-rules.schema.json is a committed artifact (the pilot modeline references it)");

        string committed = await File.ReadAllTextAsync(v2SchemaPath);
        string regen = RulesSchemaBuilder.Build(
            CatalogBuilder.Build(FrequencyBaseline.Load(FindRepoRoot())));

        await AssertNotACrlfCheckout(committed, "cs2demokit-rules.schema.json");
        await Assert.That(committed).IsEqualTo(regen)
            .Because("the v2 schema drifted from the catalog's views family — "
                     + "run: dotnet run --project tools/CS2DemoKit.RulesCatalog");
    }

    /// <summary>
    ///     Both comparisons above are exact against a string built in memory with LF, so a CRLF
    ///     checkout fails them without anything having drifted. Separating the two says which one
    ///     happened instead of sending a contributor to regenerate an unchanged artifact.
    /// </summary>
    private static async Task AssertNotACrlfCheckout(string committed, string what) =>
        await Assert.That(committed).DoesNotContain("\r")
            .Because($"{what} is stored eol=lf (see .gitattributes). A CRLF checkout is not drift: "
                     + "re-clone, or run git add --renormalize .");

    /// <summary>
    ///     The embedded resource loads and carries every family. Pins the resource wiring in
    ///     the Analysis csproj (LogicalName) and the loader's deserialization options.
    /// </summary>
    [Test]
    public async Task EmbeddedCatalog_LoadsAndAgreesWithRegen()
    {
        CatalogRoot embedded = CatalogResource.Load();
        CatalogRoot regen = CatalogBuilder.Build(FrequencyBaseline.Load(FindRepoRoot()));

        await Assert.That(embedded.CatalogVersion).IsEqualTo(regen.CatalogVersion);
        await Assert.That(embedded.Events.Count).IsEqualTo(regen.Events.Count);
        await Assert.That(embedded.NetMessages.Count).IsEqualTo(regen.NetMessages.Count);
        await Assert.That(embedded.Enrichments.Count).IsEqualTo(regen.Enrichments.Count);
        await Assert.That(embedded.Contexts.Count).IsEqualTo(regen.Contexts.Count);
        await Assert.That(embedded.Providers.Count).IsEqualTo(regen.Providers.Count);
        await Assert.That(embedded.Profiles.Count).IsEqualTo(regen.Profiles.Count);

        // Spot-pin load-bearing entries the authoring surface depends on.
        await Assert.That(embedded.Events.Any(e => e.Name == "player_death")).IsTrue();
        await Assert.That(embedded.Events.Single(e => e.Name == "molotov_thrown").Synthesized).IsTrue();
        await Assert.That(embedded.Enrichments.Any(e => e.Name == "enrich.kill.was_enemy_kill")).IsTrue();
        // S7 (totalAssists mixed sign) fix: the assist view's `enemy` facet reads the
        // assister-vs-victim enrichment, NOT the killer-vs-victim was_enemy_kill.
        await Assert.That(embedded.Enrichments.Any(e => e.Name == "enrich.kill.was_enemy_assist")).IsTrue();
        await Assert.That(embedded.Views.Single(v => v.Name == "assist")
                .Facets.Single(f => f.Name == "enemy").Enrichment)
            .IsEqualTo("enrich.kill.was_enemy_assist");
        await Assert.That(embedded.Providers.Any(p => p.Name == "entity.pawn.health")).IsTrue();
        await Assert.That(embedded.Profiles.Any(p => p.Id == "Cs2GotvProfile")).IsTrue();
        await Assert.That(embedded.Profiles.SelectMany(p => p.Bindings)
            .Any(b => b.LogicalName == "$round_end")).IsTrue();

        // Frequency plumbing end to end (plan D12): the measured baseline classifies the tick
        // message per-tick (138K+/demo measured) and the one-shot header per-match; the lints
        // key on these fields, never on names.
        await Assert.That(embedded.NetMessages.Single(m => m.Name == "CNETMsg_Tick").FrequencyClass)
            .IsEqualTo("perTick");
        await Assert.That(embedded.NetMessages.Single(m => m.Name == "CDemoFileHeader").FrequencyClass)
            .IsEqualTo("perMatch");
    }

    private static string FindRepoRoot()
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
}
