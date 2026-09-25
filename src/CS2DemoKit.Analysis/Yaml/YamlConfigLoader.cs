#region

using System.Reflection;
using CS2DemoKit.Analysis.RulesetsV2.Model;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

#endregion

namespace CS2DemoKit.Analysis.Yaml;

/// <summary>
///     Loads Rulesets v2 (<c>ruleset:</c>) documents from YAML — on disk
///     (<see cref="TryLoadDirectory" />), from the assembly's embedded shipped rules
///     (<see cref="LoadShippedEmbedded" />), or from memory such as database rows
///     (<see cref="LoadDocuments" />). All three share one per-document pipeline, so they classify,
///     dedupe, and order errors identically.
///     <para>
///         Loading is <b>strict</b>: every rules file must be a YAML map with a top-level
///         <c>ruleset:</c> key. A file in the retired Rulesets v1 format (<c>chains:</c> /
///         <c>outputs:</c>) is a loud, attributed load error — v1 support was removed, and a
///         v1 file must fail legibly at load time, never be silently skipped. Every error is
///         attributed (file, ruleset id, line when available) and all errors across the
///         directory are collected before reporting.
///     </para>
///     <para>
///         <see cref="TryLoadDirectory" /> and <see cref="LoadDocuments" /> are the tolerant entry
///         points (per-document failure containment — a broken document contributes errors, the rest
///         still load). <see cref="LoadWithOverlay" /> and <see cref="LoadShippedWithOverlay" />
///         hard-fail (throw <see cref="RuleConfigException" />) on shipped-tier errors and contain
///         user-tier errors.
///     </para>
/// </summary>
public static class YamlConfigLoader
{
    /// <summary>
    ///     Loads every <c>.yaml</c> / <c>.yml</c> file in <paramref name="directoryPath" /> (in name
    ///     order), collecting all rulesets and all errors. Never throws for content errors; missing
    ///     directories surface as a single attributed error.
    /// </summary>
    public static RuleConfigLoadResult TryLoadDirectory(string directoryPath)
    {
        if (!Directory.Exists(directoryPath))
        {
            return new RuleConfigLoadResult(
                [new RuleConfigError(directoryPath, "rules directory does not exist")],
                [], []);
        }

        List<string> files = Directory.GetFiles(directoryPath, "*.yaml")
            .Concat(Directory.GetFiles(directoryPath, "*.yml"))
            // *.test.yaml fixtures were the retired v1 `rules check --test` inputs (the Semgrep
            // pairing convention). They were never rule documents, so leftover fixture files are
            // still skipped rather than reported as broken rules files.
            .Where(f => !IsFixtureFile(f))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();

        List<RuleConfigError> errors = new();
        // Select is lazy: each TryReadFile runs when LoadFromSources's foreach pulls it, so read
        // failures interleave with parse/validation errors in file-name order (matching
        // RuleConfigLoadResult.Errors's documented ordering) rather than all landing first.
        return LoadFromSources(files.Select(f => (f, TryReadFile(f, errors))), errors);
    }

    /// <summary>
    ///     Parses the shipped rulesets from the assembly's own embedded resources (the
    ///     <c>CS2DemoKit.Analysis.ShippedRules.*.rules.yaml</c> <c>EmbeddedResource</c> items wired
    ///     in the Analysis csproj, Link-sourced from <c>rules/*.rules.yaml</c>) through the exact
    ///     same per-file pipeline as <see cref="TryLoadDirectory" /> — identical read-error
    ///     handling, v1/syntax classification, duplicate-id detection, and file-name ordering.
    ///     <para>
    ///         This is the flagship "no rules directory on disk" entry point for a NuGet
    ///         consumer: the shipped rulesets travel inside the assembly, version-locked to it,
    ///         so they can never skew against whatever <c>rules/</c> folder (if any) happens to
    ///         sit next to the binary. Compare <see cref="RuleSetLocator.ResolveShippedRulesDirectory" />
    ///         + <see cref="TryLoadDirectory" />, which is the directory-probing alternative used
    ///         by the desktop app (which does ship a <c>rules/</c> folder as content).
    ///     </para>
    ///     <para>
    ///         Errors here (a corrupt embedded resource, a build that embedded a broken rules
    ///         file) indicate a broken package, not a user-fixable problem — callers that need the
    ///         "shipped tier must be perfect" guarantee should check <c>Success</c> and throw a
    ///         <see cref="RuleConfigException" /> themselves, matching <see cref="LoadWithOverlay" />'s
    ///         shipped-tier contract.
    ///     </para>
    ///     <para>
    ///         Unlike <see cref="LoadWithOverlay" />, this does <b>not</b> drop rulesets marked
    ///         <c>enabled: false</c> — there is no overlay tier here to make that filtering
    ///         meaningful, so every shipped ruleset comes back regardless of its <c>enabled:</c>
    ///         value. A caller that wants the overlay's enabled-filtering semantics on top of the
    ///         embedded set should filter <see cref="RuleConfigLoadResult.Rulesets" /> on
    ///         <see cref="RulesetDoc.Enabled" /> itself.
    ///     </para>
    /// </summary>
    /// <returns>The parsed shipped rulesets plus any load errors, in file-name order.</returns>
    /// <exception cref="InvalidOperationException">
    ///     The assembly is missing an expected shipped-rules resource — always a packaging bug,
    ///     never a runtime/user condition.
    /// </exception>
    public static RuleConfigLoadResult LoadShippedEmbedded()
    {
        List<RuleConfigError> errors = new();
        List<(string Label, string? Yaml)> sources = ReadEmbeddedShippedRulesetSources();
        return LoadFromSources(sources, errors);
    }

    /// <summary>
    ///     Loads ruleset documents that already live in memory — rows from a database, an HTTP
    ///     upload body, a test literal — through the exact same per-document pipeline as
    ///     <see cref="TryLoadDirectory" />. Classification (v2 <c>ruleset:</c> vs. YAML syntax error
    ///     vs. retired v1 <c>chains:</c>), duplicate-id dedupe (first wins, the duplicate is an
    ///     error), loaded/failed bucketing, and error ordering are identical; the only difference is
    ///     where the text came from.
    ///     <para>
    ///         This is the entry point for the "rules are not files" consumer (CONS-4): before it,
    ///         a service storing user-authored rules in a database had to either write them to a
    ///         temp directory to get directory-loader semantics, or hand-roll a subset of them and
    ///         drift.
    ///     </para>
    ///     <para>
    ///         <paramref name="documents" /> is enumerated <b>lazily, exactly once</b>, and errors
    ///         interleave in that enumeration order — so a streaming database reader neither gets
    ///         buffered into memory up front nor has its per-row errors reordered. Enumerate in a
    ///         stable order (the directory loader uses ordinal case-insensitive file name) if you
    ///         want reproducible error ordering across runs.
    ///     </para>
    ///     <para>
    ///         To layer these over the shipped rulesets, see
    ///         <see cref="LoadShippedWithOverlay" /> — do not concatenate the two sequences by hand,
    ///         which makes a same-id user ruleset a duplicate-id error instead of an override.
    ///     </para>
    /// </summary>
    /// <param name="documents">
    ///     Each document's <c>Label</c> (how it is named in errors — a file name, a database key, a
    ///     URL; keep it unique, it is what <see cref="RuleConfigError.FilePath" /> carries) paired
    ///     with its YAML <c>Yaml</c> text. A <c>null</c> text is reported as an attributed error
    ///     rather than silently skipped.
    /// </param>
    /// <returns>The parsed rulesets plus any load errors, in enumeration order.</returns>
    public static RuleConfigLoadResult LoadDocuments(IEnumerable<(string Label, string Yaml)> documents)
    {
        ArgumentNullException.ThrowIfNull(documents);

        List<RuleConfigError> errors = new();
        // Select is lazy (the TryLoadDirectory discipline): each document is pulled — and its
        // null-text check runs — exactly when LoadFromSources's foreach reaches it, so a missing-text
        // error interleaves with parse/validation errors in enumeration order.
        return LoadFromSources(documents.Select(d => (d.Label, RequireText(d.Label, d.Yaml, errors))), errors);
    }

    /// <summary>
    ///     Guards a caller-supplied document text; on <c>null</c> (a nullable database column, a
    ///     caller ignoring the non-nullable signature) appends an attributed error and returns
    ///     <c>null</c>, mirroring <see cref="TryReadFile" />'s contract. Without this a null text
    ///     would land in <see cref="RuleConfigLoadResult.FailedFiles" /> with no error at all, and
    ///     the load would report <c>Success</c>.
    /// </summary>
    /// <param name="label">The document label, for attribution.</param>
    /// <param name="yaml">The supplied text.</param>
    /// <param name="errors">The error list to append to.</param>
    /// <returns>The text, or <c>null</c> when it was missing.</returns>
    private static string? RequireText(string label, string? yaml, List<RuleConfigError> errors)
    {
        if (yaml is not null)
        {
            return yaml;
        }

        errors.Add(new RuleConfigError(label, "document text is null — nothing to load"));
        return null;
    }

    /// <summary>
    ///     Two-tier load with the shipped tier coming from the assembly's own embedded resources
    ///     (<see cref="LoadShippedEmbedded" />) rather than a directory, overlaid by in-memory
    ///     documents (<see cref="LoadDocuments" />). The database-backed analogue of
    ///     <see cref="LoadWithOverlay" />, with the same overlay semantics:
    ///     <list type="bullet">
    ///         <item>A user ruleset with the same id as a shipped ruleset <b>replaces it wholesale</b>.</item>
    ///         <item>New user ruleset ids are appended after the shipped rulesets, in enumeration order.</item>
    ///         <item>
    ///             Rulesets with <c>enabled: false</c> (after overlay) are dropped — note this
    ///             differs from bare <see cref="LoadShippedEmbedded" />, which deliberately keeps
    ///             them because it has no overlay tier to make the filtering meaningful.
    ///         </item>
    ///         <item>
    ///             The shipped tier is load-bearing and <b>hard-fails</b> (throws
    ///             <see cref="RuleConfigException" />) on any error — there, an error means a broken
    ///             package, not user input. User-tier errors are contained and reported in
    ///             <see cref="RuleConfigLoadResult.Errors" /> while everything else still loads.
    ///         </item>
    ///     </list>
    ///     The result's <see cref="RuleConfigLoadResult.LoadedFiles" /> mixes both tiers' naming:
    ///     embedded resource file names first, then the caller's labels.
    ///     <para>
    ///         An empty <paramref name="userDocuments" /> sequence is not an error — the result is
    ///         the enabled shipped tier alone.
    ///     </para>
    /// </summary>
    /// <param name="userDocuments">The overlay documents, as for <see cref="LoadDocuments" />.</param>
    /// <returns>The merged, enabled-only rulesets plus any user-tier errors.</returns>
    /// <exception cref="RuleConfigException">A shipped (embedded) ruleset failed to load.</exception>
    public static RuleConfigLoadResult LoadShippedWithOverlay(IEnumerable<(string Label, string Yaml)> userDocuments)
    {
        ArgumentNullException.ThrowIfNull(userDocuments);

        RuleConfigLoadResult shipped = LoadShippedEmbedded();
        if (!shipped.Success)
        {
            throw new RuleConfigException(shipped.Errors);
        }

        return OverlayTiers(shipped, LoadDocuments(userDocuments));
    }

    /// <summary>
    ///     Merges a clean shipped tier with a user tier: shipped order first, user overrides in
    ///     place, new user rulesets appended in user order, disabled rulesets dropped. Each tier was
    ///     column-checked on its own; the merge is a new load unit, so a user column or table name
    ///     colliding with a shipped one surfaces here, attributed to the user file (the shipped tier
    ///     is known clean and the user file is the one that changed). A collision between two user
    ///     rulesets was reported by the user tier's own pass and is not repeated.
    /// </summary>
    private static RuleConfigLoadResult OverlayTiers(RuleConfigLoadResult shipped, RuleConfigLoadResult user)
    {
        IReadOnlyList<RulesetDoc> merged = EnabledRulesets(MergeById(shipped.Rulesets, user.Rulesets, r => r.Id));
        HashSet<string> userIds = new(user.Rulesets.Select(r => r.Id), StringComparer.Ordinal);

        List<RuleConfigError> errors = [.. user.Errors];
        List<string> loadedFiles = [.. shipped.LoadedFiles, .. user.LoadedFiles];
        List<string> failedFiles = [.. user.FailedFiles];
        foreach (RuleConfigError collision in FindShowColumnCollisions(merged, userIds))
        {
            errors.Add(collision);
            if (collision.FilePath is { } file && loadedFiles.Remove(file))
            {
                failedFiles.Add(file);
            }
        }

        return new RuleConfigLoadResult(errors, loadedFiles, failedFiles)
        {
            Rulesets = merged
        };
    }

    /// <summary>
    ///     Writes every embedded shipped rules file — the 14 <c>*.rules.yaml</c> rulesets plus
    ///     <c>cs2demokit-rules.schema.json</c> — into <paramref name="directory" />, byte-identical to the
    ///     repo's <c>rules/</c> files they were embedded from. Creates the directory if needed and
    ///     <b>overwrites</b> any file already there (unconditionally, unlike
    ///     <see cref="RuleSetLocator.ProvisionUserRulesDirectory" />'s deliberate never-overwrite
    ///     idempotence) — do not point this at a directory holding edits you want to keep.
    ///     <para>
    ///         For consumers who want to inspect, fork, or feed the shipped rules into an editor
    ///         with schema validation (the <c># yaml-language-server: $schema=./cs2demokit-rules.schema.json</c>
    ///         modeline points at the extracted <c>cs2demokit-rules.schema.json</c>) rather than read them
    ///         only through <see cref="LoadShippedEmbedded" />.
    ///     </para>
    /// </summary>
    /// <param name="directory">The target directory; created if it does not already exist.</param>
    /// <returns>
    ///     The full paths of every file written, in file-name order (ordinal, case-insensitive) —
    ///     note that puts <c>cs2demokit-rules.schema.json</c> first (<c>d</c> sorts before <c>h</c>/<c>k</c>/
    ///     <c>p</c>/<c>w</c>), not last.
    /// </returns>
    public static IReadOnlyList<string> ExtractShippedTo(string directory)
    {
        ArgumentNullException.ThrowIfNull(directory);
        Directory.CreateDirectory(directory);

        Assembly assembly = typeof(YamlConfigLoader).Assembly;
        List<string> written = new();
        foreach (string resourceName in GetShippedResourceNames(assembly))
        {
            string fileName = resourceName[ShippedResourcePrefix.Length..];
            string targetPath = Path.Combine(directory, fileName);

            using Stream resourceStream = OpenShippedResource(assembly, resourceName);
            using FileStream fileStream = File.Create(targetPath);
            resourceStream.CopyTo(fileStream);

            written.Add(targetPath);
        }

        return written;
    }

    /// <summary>Logical-name prefix shared by every shipped-rules embedded resource (Analysis csproj wiring).</summary>
    private const string ShippedResourcePrefix = "CS2DemoKit.Analysis.ShippedRules.";

    /// <summary>Reads every embedded <c>*.rules.yaml</c> shipped ruleset, in file-name order, as (label, text) pairs.</summary>
    private static List<(string Label, string? Yaml)> ReadEmbeddedShippedRulesetSources()
    {
        Assembly assembly = typeof(YamlConfigLoader).Assembly;
        List<string> resourceNames = GetShippedResourceNames(assembly)
            .Where(n => n.EndsWith(".rules.yaml", StringComparison.Ordinal))
            .ToList();

        List<(string Label, string? Yaml)> sources = new(resourceNames.Count);
        foreach (string resourceName in resourceNames)
        {
            using Stream stream = OpenShippedResource(assembly, resourceName);
            using StreamReader reader = new(stream);
            sources.Add((resourceName[ShippedResourcePrefix.Length..], reader.ReadToEnd()));
        }

        return sources;
    }

    /// <summary>Every embedded shipped-rules resource name (rulesets + schema), in file-name order.</summary>
    private static List<string> GetShippedResourceNames(Assembly assembly) =>
        assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(ShippedResourcePrefix, StringComparison.Ordinal))
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static Stream OpenShippedResource(Assembly assembly, string resourceName) =>
        assembly.GetManifestResourceStream(resourceName)
        ?? throw new InvalidOperationException(
            $"embedded shipped-rules resource '{resourceName}' missing — rebuild "
            + "CS2DemoKit.Analysis (the rules/*.rules.yaml + cs2demokit-rules.schema.json EmbeddedResource wiring "
            + "in the csproj is out of sync with the assembly).");

    /// <summary>
    ///     The per-document pipeline shared by <see cref="TryLoadDirectory" /> and
    ///     <see cref="LoadShippedEmbedded" />: classify, expand, validate, dedupe-by-id, and bucket
    ///     into loaded/failed — the only difference between the two callers is where
    ///     <paramref name="sources" /> came from (disk vs. embedded resource) and pre-populated
    ///     read errors.
    /// </summary>
    /// <param name="sources">
    ///     Each document's label (a file path for directory loads, a bare file name for embedded
    ///     loads) paired with its YAML text, or <c>null</c> when reading it already failed (the
    ///     failure itself must already be recorded in <paramref name="errors" />). May be lazy
    ///     (e.g. <see cref="Enumerable.Select{TSource,TResult}(IEnumerable{TSource},Func{TSource,TResult})" />)
    ///     so a per-source read runs exactly when this method's iteration reaches it, keeping
    ///     read-error ordering interleaved with parse/validation-error ordering.
    /// </param>
    /// <param name="errors">Errors collected so far (e.g. read failures); appended to in place.</param>
    private static RuleConfigLoadResult LoadFromSources(
        IEnumerable<(string Label, string? Yaml)> sources, List<RuleConfigError> errors)
    {
        List<RulesetDoc> allRulesets = new();
        List<string> loadedFiles = new();
        List<string> failedFiles = new();
        // Ruleset ids must be unique across the whole directory (one tier = one id namespace).
        Dictionary<string, string> rulesetIdToFile = new(StringComparer.Ordinal);

        foreach ((string label, string? yaml) in sources)
        {
            if (yaml is null)
            {
                failedFiles.Add(label);
                continue;
            }

            int errorsBefore = errors.Count;
            IReadOnlyList<(int Index, YamlNode Root)> documents;
            try
            {
                documents = RulesetDocumentLoader.ParseDocuments(yaml);
            }
            catch (YamlException ex)
            {
                errors.Add(new RuleConfigError(label, ex.InnerException?.Message ?? ex.Message,
                    Line: (int)ex.Start.Line, Column: (int)ex.Start.Column));
                failedFiles.Add(label);
                continue;
            }

            if (documents.Count == 0)
            {
                AppendNonRulesetError(null, label, errors);
                failedFiles.Add(label);
                continue;
            }

            // Each '---' document is its own ruleset with its own error containment: a broken
            // document contributes errors and the others still load. Documents after the first
            // are labelled "file#N" so an error names the document it came from.
            foreach ((int index, YamlNode root) in documents)
            {
                string documentLabel = index == 1 ? label : $"{label}#{index}";
                LoadDocument(root, documentLabel, errors, allRulesets, rulesetIdToFile);
            }

            (errors.Count == errorsBefore ? loadedFiles : failedFiles).Add(label);
        }

        // Show columns are checked over the whole load unit, not per file: a label is the
        // value-column key of the table it lands on, matched across every ruleset built together.
        foreach (RuleConfigError collision in FindShowColumnCollisions(allRulesets))
        {
            errors.Add(collision);
            if (collision.FilePath is { } file && loadedFiles.Remove(file))
            {
                failedFiles.Add(file);
            }
        }

        return new RuleConfigLoadResult(errors, loadedFiles, failedFiles)
        {
            Rulesets = allRulesets
        };
    }

    /// <summary>
    ///     Finds every <c>show:</c> column that lands on a surface where an earlier entry (in this or
    ///     any other enabled ruleset) already put the same column key. A surface is one scoreboard
    ///     board or one named <c>tables:</c> entry; the column key is <c>label:</c>, falling back to
    ///     the stat id, exactly as <c>ShowLowering</c> assigns it. A scoreboard entry's board follows
    ///     the lowering's rules too: <c>boards:</c> when written, else the match board for a
    ///     highlight <c>.count</c>, else the stat's own <c>per:</c> (a tally target follows its
    ///     owner). The round and match scoreboards are separate tables, so the same label on both is
    ///     two columns, not a collision, and so is the same label in two differently named tables.
    ///     Table NAMES are checked against each other in the same pass, because two tables under one
    ///     name are two tables a consumer can only address as one.
    ///     <para>
    ///         This is an ERROR, not a warning, deliberately. The projector writes cells into a
    ///         dictionary keyed by column name, so the later ruleset's value silently replaces the
    ///         earlier one and the losing stat reads as zero on every row; the <c>Flick</c> column
    ///         read 0 for weeks that way and was found only by someone doubting the number. A
    ///         <c>tables:</c> column fails the same way (<c>ConfiguredOutputProjector</c> writes
    ///         <c>values[MetricRef.Label]</c>) with the extra twist that the duplicate key is also
    ///         emitted twice in the header, so the CSV carries two identically named columns both
    ///         holding the later stat's value. A load result carries no warnings channel, and one
    ///         nobody reads is how this got here. The fix is a one-word rename, so the cost of
    ///         failing loud is small. A ruleset with <c>enabled: false</c> contributes no columns and
    ///         is skipped; two rulesets that are never loaded together are never checked together,
    ///         because the check runs on the load unit (a directory, the embedded set, an overlay
    ///         merge), which is the set that shares a table.
    ///     </para>
    ///     <para>
    ///         An entry whose ref or <c>boards:</c> value classifies to nothing is reported rather
    ///         than skipped. Nothing else on the LOAD path looks at it: the existence check lives in
    ///         <c>ShowReferenceValidator</c>, which needs a checked ruleset and therefore runs at
    ///         resolve, and an unusable <c>boards:</c> value is not checked even there — it reaches
    ///         <c>ShowLowering.MapBoards</c> and throws mid-build. Reported only for a single load
    ///         unit: the overlay merge re-walks documents whose own tier pass already reported them.
    ///     </para>
    /// </summary>
    /// <param name="rulesets">The rulesets that will be built together, in load order.</param>
    /// <param name="overlayTier">
    ///     For a two-tier merge, the ids of the rulesets that came from the user tier. When given,
    ///     only CROSS-tier collisions are reported (each tier already checked itself), and the error
    ///     is attributed to the user-tier entry whichever side came first in merged order. Null for a
    ///     single load unit, where the later entry is attributed.
    /// </param>
    /// <returns>One attributed error per colliding entry, with the file and line of the entry at fault.</returns>
    internal static List<RuleConfigError> FindShowColumnCollisions(
        IReadOnlyList<RulesetDoc> rulesets, IReadOnlySet<string>? overlayTier = null)
    {
        const string columnConsequence =
            "labels are the table's column keys across every ruleset loaded together, so one value "
            + "silently replaces the other and that stat reads as zero. Rename one of them";

        List<RuleConfigError> errors = new();
        Dictionary<(string Surface, string Column), ShowColumnOwner> owners = new();

        foreach (RulesetDoc doc in rulesets)
        {
            if (!doc.Enabled || doc.Show is not { } show
                || (show.Scoreboard.Count == 0 && show.Tables.Count == 0))
            {
                continue;
            }

            Dictionary<string, PerScope> statScopes = new(StringComparer.Ordinal);
            Dictionary<string, PerScope> tallyTargetScopes = new(StringComparer.Ordinal);
            foreach (StatDef stat in doc.Stats)
            {
                statScopes[stat.Id] = stat.Per;
                if (stat.Thresholds is { } thresholds)
                {
                    foreach (TallyThreshold threshold in thresholds)
                    {
                        tallyTargetScopes[threshold.Target] = stat.Per;
                    }
                }
            }

            HashSet<string> highlights = new(doc.Highlights.Select(h => h.Id), StringComparer.Ordinal);

            foreach (ScoreboardEntry entry in show.Scoreboard)
            {
                if (!TryResolveScoreboardBoards(entry, statScopes, tallyTargetScopes, highlights,
                        out List<bool> boards, out string unclassifiable))
                {
                    if (overlayTier is null)
                    {
                        errors.Add(new RuleConfigError(doc.Position.File,
                            $"show: scoreboard entry '{entry.Stat}' {unclassifiable}.",
                            doc.Id, entry.Stat,
                            entry.Position.Line > 0 ? entry.Position.Line : null,
                            entry.Position.Column > 0 ? entry.Position.Column : null));
                    }

                    continue;
                }

                string column = entry.Label ?? entry.Stat;
                ShowColumnOwner claimant = new(
                    doc.Id, doc.Position.File, entry.Stat, entry.Position.Line, entry.Position.Column);
                foreach (bool roundBoard in boards)
                {
                    string board = roundBoard ? "round" : "match";
                    Claim(errors, owners, overlayTier, ($"scoreboard:{board}", column), claimant,
                        $"scoreboard label '{column}' on the {board} board", "the column", columnConsequence);
                }
            }

            foreach (TableDef table in show.Tables)
            {
                ShowColumnOwner declaration = new(
                    doc.Id, doc.Position.File, table.Name, table.Position.Line, table.Position.Column);
                if (!Claim(errors, owners, overlayTier, (TableNameSurface, table.Name), declaration,
                        $"show: table '{table.Name}'", "a table",
                        "a table name is the emitted table's name, so the two are written under one "
                        + "name and a consumer that addresses tables by name reads only one of them. "
                        + "Rename one of them"))
                {
                    continue; // every column of it would re-report the same clash
                }

                foreach (TableColumn tableColumn in table.Columns)
                {
                    string column = tableColumn.Label ?? tableColumn.Stat;
                    ShowColumnOwner claimant = new(doc.Id, doc.Position.File, tableColumn.Stat,
                        tableColumn.Position.Line, tableColumn.Position.Column);
                    Claim(errors, owners, overlayTier, ($"table:{table.Name}", column), claimant,
                        $"show: table '{table.Name}' column '{column}'", "the column", columnConsequence);
                }
            }
        }

        return errors;
    }

    /// <summary>
    ///     The surface table NAMES are claimed under. Distinct from every <c>table:&lt;name&gt;</c>
    ///     column surface and from the two <c>scoreboard:&lt;board&gt;</c> surfaces, so one dictionary
    ///     carries all three kinds without a table called <c>round</c> shadowing the round board.
    /// </summary>
    private const string TableNameSurface = "table-name";

    /// <summary>
    ///     Claims one (surface, key) pair for <paramref name="claimant" />, or — when something
    ///     already holds it — appends the attributed collision error and refuses the claim.
    /// </summary>
    /// <param name="errors">The error list to append to.</param>
    /// <param name="owners">Who holds each (surface, key) pair so far, in load order.</param>
    /// <param name="overlayTier">The user-tier ruleset ids, as on <see cref="FindShowColumnCollisions" />.</param>
    /// <param name="key">The surface and key being claimed.</param>
    /// <param name="claimant">The entry claiming it.</param>
    /// <param name="subject">How the message names the claimant, e.g. <c>show: table 'x' column 'K'</c>.</param>
    /// <param name="claimedBy">What the incumbent is to the surface — <c>the column</c> / <c>a table</c>.</param>
    /// <param name="consequence">The sentence explaining what breaks, appended after the attribution.</param>
    /// <returns><c>true</c> when the pair was free and is now claimed.</returns>
    private static bool Claim(
        List<RuleConfigError> errors,
        Dictionary<(string Surface, string Column), ShowColumnOwner> owners,
        IReadOnlySet<string>? overlayTier,
        (string Surface, string Column) key,
        ShowColumnOwner claimant,
        string subject,
        string claimedBy,
        string consequence)
    {
        if (!owners.TryGetValue(key, out ShowColumnOwner? first))
        {
            owners[key] = claimant;
            return true;
        }

        ShowColumnOwner atFault = claimant;
        ShowColumnOwner other = first;
        if (overlayTier is not null)
        {
            bool firstIsUser = overlayTier.Contains(first.RulesetId);
            bool claimantIsUser = overlayTier.Contains(claimant.RulesetId);
            if (firstIsUser == claimantIsUser)
            {
                return false; // same tier: that tier's own pass reported it (or threw)
            }

            if (firstIsUser)
            {
                atFault = first;
                other = claimant;
            }
        }

        string otherFile = other.File is null ? "<inline yaml>" : Path.GetFileName(other.File);
        errors.Add(new RuleConfigError(
            atFault.File,
            $"{subject} is also {claimedBy} of ruleset '{other.RulesetId}' ({otherFile}); {consequence}",
            atFault.RulesetId,
            atFault.Stat,
            atFault.Line > 0 ? atFault.Line : null,
            atFault.Column > 0 ? atFault.Column : null));
        return false;
    }

    /// <summary>The show entry that first claimed a surface, with what an error needs to point back at it.</summary>
    private sealed record ShowColumnOwner(string RulesetId, string? File, string Stat, int Line, int Column);

    /// <summary>
    ///     The boards a scoreboard entry lands on, in <c>ShowLowering</c>'s order of precedence:
    ///     an explicit <c>boards:</c> list, else a highlight ref (<c>id</c> or <c>id.count</c>) on the
    ///     match board, else a stat ref on its <c>per:</c> board, else a tally target on its owner's.
    /// </summary>
    /// <param name="entry">The scoreboard entry as written.</param>
    /// <param name="statScopes">Stat id to its <c>per:</c>, over this ruleset's stats.</param>
    /// <param name="tallyTargetScopes">Tally <c>target:</c> id to the owning tally's <c>per:</c>.</param>
    /// <param name="highlights">This ruleset's highlight ids.</param>
    /// <param name="boards">One flag per board the entry lands on, <c>true</c> for the round board.</param>
    /// <param name="reason">
    ///     When the entry classifies to no board, the clause naming why, for the load error. Empty on
    ///     success.
    /// </param>
    /// <returns><c>true</c> when the entry resolves to at least one board.</returns>
    private static bool TryResolveScoreboardBoards(
        ScoreboardEntry entry,
        Dictionary<string, PerScope> statScopes,
        Dictionary<string, PerScope> tallyTargetScopes,
        HashSet<string> highlights,
        out List<bool> boards,
        out string reason)
    {
        boards = new List<bool>(2);
        reason = string.Empty;
        if (entry.Boards is { Count: > 0 } explicitBoards)
        {
            foreach (string board in explicitBoards)
            {
                switch (board)
                {
                    case "round":
                        boards.Add(true);
                        break;
                    case "match":
                        boards.Add(false);
                        break;
                    default:
                        reason = $"lists board '{board}', which is not a board (round | match)";
                        return false;
                }
            }

            return true;
        }

        const string countSuffix = ".count";
        string highlightId = entry.Stat.EndsWith(countSuffix, StringComparison.Ordinal)
            ? entry.Stat[..^countSuffix.Length]
            : entry.Stat;
        if (highlights.Contains(highlightId))
        {
            boards.Add(false);
            return true;
        }

        if (statScopes.TryGetValue(entry.Stat, out PerScope per)
            || tallyTargetScopes.TryGetValue(entry.Stat, out per))
        {
            boards.Add(per == PerScope.Round);
            return true;
        }

        reason = "references neither a stat, a highlight, nor a tally target defined in the ruleset";
        return false;
    }

    /// <summary>True when <paramref name="path" /> names a (retired) rule-test fixture (<c>*.test.yaml</c> / <c>*.test.yml</c>).</summary>
    private static bool IsFixtureFile(string path) =>
        path.EndsWith(".test.yaml", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".test.yml", StringComparison.OrdinalIgnoreCase);

    /// <summary>Reads a file's text; on an I/O error appends an attributed error and returns <c>null</c>.</summary>
    /// <param name="file">The absolute file path.</param>
    /// <param name="errors">The error list to append a read failure to.</param>
    /// <returns>The file contents, or <c>null</c> when the read failed.</returns>
    private static string? TryReadFile(string file, List<RuleConfigError> errors)
    {
        try
        {
            return File.ReadAllText(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            errors.Add(new RuleConfigError(file, $"cannot read file: {ex.Message}"));
            return null;
        }
    }

    /// <summary>
    ///     Loads one parsed document of a source: classifies a non-ruleset document, attributes
    ///     every diagnostic to <paramref name="label" />, and dedupes the ruleset id across the
    ///     whole load unit.
    /// </summary>
    private static void LoadDocument(
        YamlNode root,
        string label,
        List<RuleConfigError> errors,
        List<RulesetDoc> allRulesets,
        Dictionary<string, string> rulesetIdToFile)
    {
        RulesetDocumentLoader.Outcome? v2 = RulesetDocumentLoader.TryLoadRoot(root, label);
        if (v2 is null)
        {
            // Not a `ruleset:` document. The retired v1 format (`chains:`/`outputs:`) gets its
            // own explicit diagnostic so a pre-existing v1 overlay file fails loudly, never
            // silently.
            AppendNonRulesetError(root, label, errors);
            return;
        }

        foreach (RulesetDiagnostic diagnostic in v2.Diagnostics)
        {
            errors.Add(ToRuleConfigError(label, diagnostic, v2.Doc?.Id));
        }

        if (v2.Doc is null)
        {
            return;
        }

        if (rulesetIdToFile.TryGetValue(v2.Doc.Id, out string? firstRulesetFile))
        {
            errors.Add(new RuleConfigError(label,
                $"duplicate ruleset id '{v2.Doc.Id}' (first defined in {Path.GetFileName(firstRulesetFile)})",
                v2.Doc.Id));
        }
        else
        {
            rulesetIdToFile[v2.Doc.Id] = label;
            allRulesets.Add(v2.Doc);
        }
    }

    /// <summary>
    ///     Produces the attributed error for a document that is not a <c>ruleset:</c> document:
    ///     the retired v1 format (<c>chains:</c> /
    ///     <c>outputs:</c> — its own explicit diagnostic), or a generic not-a-ruleset error.
    /// </summary>
    /// <param name="root">The document root, or <c>null</c> when the file holds no document.</param>
    /// <param name="file">The file path or document label.</param>
    /// <param name="errors">The error list to append to.</param>
    private static void AppendNonRulesetError(YamlNode? root, string file, List<RuleConfigError> errors)
    {
        if (root is YamlMappingNode map && map.Children.Keys.OfType<YamlScalarNode>()
                .Any(k => k.Value is "chains" or "outputs"))
        {
            errors.Add(new RuleConfigError(file,
                "this file is the retired Rulesets v1 format ('chains:'/'outputs:') — v1 support "
                + "was removed and the file no longer loads. Rewrite it as a Rulesets v2 'ruleset:' "
                + "document (see docs/RULES_AUTHORING.md)."));
            return;
        }

        errors.Add(new RuleConfigError(file,
            "not a rules document — every rules file must be a YAML map with a top-level "
            + "'ruleset:' key (see docs/RULES_AUTHORING.md)."));
    }

    /// <summary>Converts a v2 <see cref="RulesetDiagnostic" /> into the shared <see cref="RuleConfigError" /> shape.</summary>
    /// <param name="file">The offending file path.</param>
    /// <param name="diagnostic">The v2 diagnostic.</param>
    /// <param name="rulesetId">The owning ruleset id, when known (grouped into <see cref="RuleConfigError.ChainId" />).</param>
    /// <returns>The attributed error.</returns>
    private static RuleConfigError ToRuleConfigError(string file, RulesetDiagnostic diagnostic, string? rulesetId) =>
        new(file, diagnostic.Message, rulesetId, null,
            diagnostic.Position.Line > 0 ? diagnostic.Position.Line : null,
            diagnostic.Position.Column > 0 ? diagnostic.Position.Column : null);

    /// <summary>
    ///     Two-tier load: shipped defaults overlaid by the user's rules directory.
    ///     <list type="bullet">
    ///         <item>A user ruleset with the same id as a shipped ruleset <b>replaces it wholesale</b>.</item>
    ///         <item>New user ruleset ids are appended after the shipped rulesets, in user-file order.</item>
    ///         <item>Rulesets with <c>enabled: false</c> (after overlay) are dropped.</item>
    ///         <item>
    ///             The shipped tier is load-bearing and <b>hard-fails</b> (throws
    ///             <see cref="RuleConfigException" />) on any error; user-tier errors are contained —
    ///             reported in <see cref="RuleConfigLoadResult.Errors" /> while the rest of the
    ///             user tier (and all shipped rulesets) still load.
    ///         </item>
    ///     </list>
    ///     A missing or <c>null</c> user directory is not an error — the result is the shipped tier alone.
    /// </summary>
    public static RuleConfigLoadResult LoadWithOverlay(string shippedDirectory, string? userDirectory)
    {
        RuleConfigLoadResult shipped = TryLoadDirectory(shippedDirectory);
        if (!shipped.Success)
        {
            throw new RuleConfigException(shipped.Errors);
        }

        if (userDirectory is null || !Directory.Exists(userDirectory))
        {
            return shipped with
            {
                Rulesets = EnabledRulesets(shipped.Rulesets)
            };
        }

        return OverlayTiers(shipped, TryLoadDirectory(userDirectory));
    }

    /// <summary>Drops disabled v2 rulesets (<c>enabled: false</c>) after tier overlay.</summary>
    /// <param name="rulesets">The merged rulesets.</param>
    /// <returns>Only the enabled rulesets, in order.</returns>
    private static IReadOnlyList<RulesetDoc> EnabledRulesets(IReadOnlyList<RulesetDoc> rulesets) =>
        rulesets.All(r => r.Enabled) ? rulesets : rulesets.Where(r => r.Enabled).ToList();

    /// <summary>Overlay merge: shipped order first, same-id user items replace in place, new ids append.</summary>
    private static List<T> MergeById<T>(
        IReadOnlyList<T> shipped, IReadOnlyList<T> user, Func<T, string> idOf)
    {
        List<T> merged = new(shipped.Count + user.Count);
        Dictionary<string, int> indexById = new(StringComparer.Ordinal);
        foreach (T item in shipped)
        {
            indexById[idOf(item)] = merged.Count;
            merged.Add(item);
        }

        foreach (T item in user)
        {
            if (indexById.TryGetValue(idOf(item), out int existing))
            {
                merged[existing] = item;
            }
            else
            {
                indexById[idOf(item)] = merged.Count;
                merged.Add(item);
            }
        }

        return merged;
    }
}
