namespace CS2DemoKit.Analysis.Yaml;

/// <summary>
///     Resolves the two rule-set locations consumed by <see cref="YamlConfigLoader.LoadWithOverlay" />:
///     the read-only <b>shipped</b> defaults next to the app binary, and the writable <b>user</b>
///     overlay directory in the platform config location. Packaging-safe by construction — no
///     repo-walking in the primary paths, so a published build resolves identically to a dev build.
/// </summary>
public static class RuleSetLocator
{
    /// <summary>
    ///     Environment override for the shipped-rules directory. Points the analysis at a live
    ///     checkout's <c>rules/</c> during development (edit → re-run without rebuild) or at an
    ///     alternate rule set entirely.
    /// </summary>
    public const string RulesDirEnvVar = "CS2DEMOKIT_RULES_DIR";

    /// <summary>
    ///     Environment override for the user-rules overlay directory. Lets portable installs keep
    ///     their rules beside the app, and lets the release-gate tests inject a temp overlay dir.
    /// </summary>
    public const string UserRulesDirEnvVar = "CS2DEMOKIT_USER_RULES_DIR";

    /// <summary>
    ///     The names these overrides shipped under before the library moved out of the
    ///     DemoViewer.NET repo. Still honoured, second in precedence, so existing scripts and
    ///     launch configurations keep working.
    /// </summary>
    private const string LegacyRulesDirEnvVar = "DEMOVIEWER_RULES_DIR";

    private const string LegacyUserRulesDirEnvVar = "DEMOVIEWER_USER_RULES_DIR";

    /// <summary>Sub-directory of the app base / config dir that holds rule files.</summary>
    private const string RulesDirName = "rules";

    private const string UserRulesReadme = """
                                           # CS2DemoKit — user rules

                                           Any `.yaml` file you put in this directory is loaded on top of the shipped analysis rules.
                                           Rules are written as **Rulesets v2** documents (a `ruleset:` file). Name your files
                                           `<name>.rules.yaml` — that is also what the in-app Rule Workbench lists and edits:

                                           - A ruleset with the **same `ruleset:` id** as a shipped one **replaces it entirely** — to
                                             customize a shipped stat, copy the whole `.rules.yaml` file from the shipped `rules/`
                                             directory (next to the app) into this folder and edit it.
                                           - A ruleset with a **new id** adds brand-new stats. Give it a `show:` section to surface
                                             them in the stats tables and exports.
                                           - To **turn off** a shipped ruleset without redefining it, declare just its id with
                                             `enabled: false`:

                                             ```yaml
                                             ruleset: some_shipped_ruleset
                                             enabled: false
                                             ```

                                           Files load in name order. Ruleset ids must be unique within this directory. Errors in a
                                           file here never break analysis — the file is skipped and the problems are reported in
                                           the app.

                                           Start a file with the line below to get editor validation and autocompletion (the schema
                                           is copied into this folder for you):

                                           ```yaml
                                           # yaml-language-server: $schema=./cs2demokit-rules.schema.json
                                           ```

                                           The old v1 format (a `chains:` document) is retired and no longer loads — a v1 file in
                                           this folder is reported as an error in the app. Write all files as `ruleset:` documents.
                                           """;

    /// <summary>
    ///     The shipped (baseline) rules directory. Resolution order:
    ///     <list type="number">
    ///         <item><see cref="RulesDirEnvVar" /> when set — the dev/power-user override.</item>
    ///         <item><c>AppContext.BaseDirectory/rules</c> — the packaged location (csproj copies <c>rules/</c> to output).</item>
    ///         <item>Repo-walk from the base directory — dev fallback for hosts that don't copy content (e.g. test runners).</item>
    ///     </list>
    ///     Returns the packaged location even when absent so the caller gets one deterministic
    ///     answer; <see cref="YamlConfigLoader.TryLoadDirectory" /> reports a missing directory as an
    ///     attributed error rather than throwing raw.
    /// </summary>
    public static string ResolveShippedRulesDirectory()
    {
        string? overrideDir = Environment.GetEnvironmentVariable(RulesDirEnvVar)
                              ?? Environment.GetEnvironmentVariable(LegacyRulesDirEnvVar);
        if (!string.IsNullOrEmpty(overrideDir))
        {
            return overrideDir;
        }

        string packaged = Path.Combine(AppContext.BaseDirectory, RulesDirName);
        if (Directory.Exists(packaged))
        {
            return packaged;
        }

        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, RulesDirName);
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        return packaged;
    }

    /// <summary>
    ///     The application name used for the per-user config directory. Defaults to
    ///     <c>CS2DemoKit</c>; a host application should set it once at startup to its own name so
    ///     user rules land beside that application's other settings rather than under the
    ///     library's name. DemoViewer.NET sets <c>"DemoViewer.NET"</c> here, which is also the
    ///     location its users' existing rules already live in.
    /// </summary>
    public static string AppConfigDirName { get; set; } = "CS2DemoKit";

    /// <summary>
    ///     The cross-platform application-data root, named by <see cref="AppConfigDirName" /> —
    ///     <c>~/Library/Application Support/&lt;name&gt;</c> on macOS,
    ///     <c>%APPDATA%\&lt;name&gt;</c> on Windows,
    ///     <c>$XDG_CONFIG_HOME/&lt;name&gt;</c> (default <c>~/.config/&lt;name&gt;</c>) on Linux.
    ///     <para>
    ///         The single owner of the per-OS location logic. It lives in this low layer so a host
    ///         application can build its own app-data file paths on top of it. Deliberately does
    ///         <b>not</b> honour <see cref="UserRulesDirEnvVar" />: that override is rules-specific
    ///         and stays in <see cref="GetUserRulesDirectory" />.
    ///     </para>
    /// </summary>
    public static string GetConfigRoot()
    {
        string configRoot;
        if (OperatingSystem.IsMacOS())
        {
            configRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Library", "Application Support");
        }
        else if (OperatingSystem.IsWindows())
        {
            configRoot = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        }
        else
        {
            string? xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            configRoot = !string.IsNullOrEmpty(xdg)
                ? xdg
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        }

        return Path.Combine(configRoot, AppConfigDirName);
    }

    /// <summary>
    ///     The per-user rules overlay directory (never auto-created here — see
    ///     <see cref="EnsureUserRulesDirectory" />): the <see cref="UserRulesDirEnvVar" /> override
    ///     when set, else the <c>rules</c> sub-directory of <see cref="GetConfigRoot" /> —
    ///     <c>~/Library/Application Support/&lt;name&gt;/rules</c> on macOS,
    ///     <c>%APPDATA%\&lt;name&gt;\rules</c> on Windows,
    ///     <c>$XDG_CONFIG_HOME/&lt;name&gt;/rules</c> (default <c>~/.config/...</c>) on Linux.
    /// </summary>
    public static string GetUserRulesDirectory()
    {
        string? overrideDir = Environment.GetEnvironmentVariable(UserRulesDirEnvVar)
                              ?? Environment.GetEnvironmentVariable(LegacyUserRulesDirEnvVar);
        if (!string.IsNullOrEmpty(overrideDir))
        {
            return overrideDir;
        }

        return Path.Combine(GetConfigRoot(), RulesDirName);
    }

    /// <summary>
    ///     Creates the user rules directory on first use and provisions it for editing: a README
    ///     explaining the overlay semantics, plus a copy of the Rulesets v2 JSON schema
    ///     (<c>cs2demokit-rules.schema.json</c>) from the shipped directory so <c># yaml-language-server</c>
    ///     editor validation lights up immediately. Idempotent — existing files are never
    ///     overwritten (a dir provisioned pre-cutover keeps its v1 README/schema untouched; it just
    ///     gains the v2 schema). Returns the directory path.
    /// </summary>
    public static string EnsureUserRulesDirectory(string? shippedDirectory = null) =>
        ProvisionUserRulesDirectory(GetUserRulesDirectory(), shippedDirectory);

    /// <summary>
    ///     <see cref="EnsureUserRulesDirectory" /> against an explicit target directory — the
    ///     provisioning logic without the platform-location resolution (also what tests exercise).
    /// </summary>
    public static string ProvisionUserRulesDirectory(string userDir, string? shippedDirectory = null)
    {
        Directory.CreateDirectory(userDir);

        string readmePath = Path.Combine(userDir, "README.md");
        if (!File.Exists(readmePath))
        {
            File.WriteAllText(readmePath, UserRulesReadme);
        }

        if (shippedDirectory is not null)
        {
            // Rulesets v2 schema (GAP-AE-5 cutover): user dirs are provisioned v2-only. The v1
            // format is retired entirely — a leftover v1 schema copy in an old user dir is inert.
            string schemaSource = Path.Combine(shippedDirectory, "cs2demokit-rules.schema.json");
            string schemaTarget = Path.Combine(userDir, "cs2demokit-rules.schema.json");
            if (File.Exists(schemaSource) && !File.Exists(schemaTarget))
            {
                File.Copy(schemaSource, schemaTarget);
            }
        }

        return userDir;
    }
}
