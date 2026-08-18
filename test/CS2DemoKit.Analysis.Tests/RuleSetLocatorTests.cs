#region

using CS2DemoKit.Analysis.Yaml;

#endregion

namespace CS2DemoKit.Analysis.Tests;

/// <summary>
///     Rule-directory resolution and first-run provisioning
///     (<see cref="RuleSetLocator" />), plus the shipped-tier hard-fail contract of
///     <see cref="YamlConfigLoader.LoadWithOverlay" />.
///     <para>
///         Re-homed from the retired <c>RuleOverlayTests</c>, which was deleted with the Rulesets v1
///         removal: its overlay cases had v2 successors in <c>RulesetV2DocumentModelTests</c>, but
///         these three behaviours are format-independent and would otherwise have gone uncovered.
///         Provisioning in particular is first-run, user-facing behaviour — it is what a new user's
///         rules folder is made of.
///     </para>
/// </summary>
[Category("Unit")]
public class RuleSetLocatorTests
{
    private sealed class TempDir : IDisposable
    {
        public TempDir(params (string Name, string Content)[] files)
        {
            Path = Directory.CreateTempSubdirectory("demoviewer-locator-test-").FullName;
            foreach ((string name, string content) in files)
            {
                File.WriteAllText(System.IO.Path.Combine(Path, name), content);
            }
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, true);
            }
            catch (IOException)
            {
                // Best-effort cleanup.
            }
        }
    }

    // A minimal well-formed v2 ruleset document (same shape as RulesetV2DocumentModelTests.MiniRuleset).
    private static string Ruleset(string id) =>
        $"ruleset: {id}\nfor: match\nstats:\n  k:\n    count: kill\n    per: match\n";

    /// <summary>
    ///     First-run provisioning creates the directory with a README and a copy of the v2 schema, so
    ///     <c># yaml-language-server</c> validation works in the user's editor immediately. Idempotent:
    ///     re-provisioning never overwrites what the user has edited.
    /// </summary>
    [Test]
    public async Task ProvisionUserRulesDirectory_CreatesReadmeAndV2Schema_Idempotently()
    {
        using TempDir shipped = new(("dv-rules.schema.json", "{}"));
        string target = Path.Combine(shipped.Path, "user-rules");

        string provisioned = RuleSetLocator.ProvisionUserRulesDirectory(target, shipped.Path);

        await Assert.That(provisioned).IsEqualTo(target);
        await Assert.That(File.Exists(Path.Combine(target, "README.md"))).IsTrue();
        await Assert.That(File.Exists(Path.Combine(target, "dv-rules.schema.json"))).IsTrue();
        await Assert.That(File.ReadAllText(Path.Combine(target, "README.md")))
            .Contains("$schema=./dv-rules.schema.json")
            .Because("the README's modeline must name the schema that was actually provisioned");

        // Idempotence: a user's edits survive re-provisioning (this runs on every app start).
        File.WriteAllText(Path.Combine(target, "README.md"), "user-modified");
        RuleSetLocator.ProvisionUserRulesDirectory(target, shipped.Path);
        await Assert.That(File.ReadAllText(Path.Combine(target, "README.md"))).IsEqualTo("user-modified");
    }

    /// <summary>Provisioning a directory whose shipped source has no schema still yields a usable folder.</summary>
    [Test]
    public async Task ProvisionUserRulesDirectory_WithoutAShippedSchema_StillCreatesTheDirectory()
    {
        using TempDir shipped = new();
        string target = Path.Combine(shipped.Path, "user-rules");

        RuleSetLocator.ProvisionUserRulesDirectory(target, shipped.Path);

        await Assert.That(Directory.Exists(target)).IsTrue();
        await Assert.That(File.Exists(Path.Combine(target, "README.md"))).IsTrue()
            .Because("a missing schema is not fatal — the folder is still the place user rules go");
    }

    /// <summary>
    ///     <c>DEMOVIEWER_USER_RULES_DIR</c> overrides the platform user-rules location (the seam the
    ///     Workbench tests and a developer with a scratch config rely on), and clearing it restores the
    ///     platform default.
    /// </summary>
    [Test]
    [NotInParallel] // mutates process-wide environment
    public async Task UserRulesDirectory_EnvOverride_Wins()
    {
        string custom = Path.Combine(Path.GetTempPath(), "demoviewer-user-override-test");
        string? saved = Environment.GetEnvironmentVariable(RuleSetLocator.UserRulesDirEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(RuleSetLocator.UserRulesDirEnvVar, custom);
            await Assert.That(RuleSetLocator.GetUserRulesDirectory()).IsEqualTo(custom);
        }
        finally
        {
            Environment.SetEnvironmentVariable(RuleSetLocator.UserRulesDirEnvVar, saved);
        }

        await Assert.That(RuleSetLocator.GetUserRulesDirectory()).IsNotEqualTo(custom);
    }

    /// <summary>
    ///     The shipped tier is load-bearing: an error there throws rather than degrading
    ///     (shipped-tier errors hard-fail). A user-tier file is contained instead — that half is covered by
    ///     <c>RulesetV2DocumentModelTests.Directory_RetiredV1File_FailsLoud_SiblingV2StillLoads</c>.
    /// </summary>
    [Test]
    public async Task BrokenShippedTier_Throws()
    {
        using TempDir shipped = new(("base.rules.yaml", "ruleset: c1\nversion: 1\nstats:\n  - bogus_key: true\n"));
        using TempDir user = new(("mine.rules.yaml", Ruleset("my_ruleset")));

        RuleConfigException ex = Assert.Throws<RuleConfigException>(
            () => YamlConfigLoader.LoadWithOverlay(shipped.Path, user.Path));

        await Assert.That(ex.Errors.Count).IsGreaterThan(0)
            .Because("the throw must carry what was wrong, not just that something was");
    }

    /// <summary>A missing user directory is not an error — the shipped tier loads alone.</summary>
    [Test]
    public async Task MissingUserDirectory_LoadsShippedOnly()
    {
        using TempDir shipped = new(("base.rules.yaml", Ruleset("shipped_ruleset")));

        RuleConfigLoadResult result = YamlConfigLoader.LoadWithOverlay(
            shipped.Path,
            Path.Combine(Path.GetTempPath(), "demoviewer-nonexistent-" + Guid.NewGuid().ToString("N")));

        await Assert.That(result.Success).IsTrue();
        await Assert.That(result.Rulesets.Count).IsEqualTo(1);
    }
}
