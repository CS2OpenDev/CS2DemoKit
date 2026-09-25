#region

using CS2DemoKit.Analysis.RulesetsV2;
using CS2DemoKit.Analysis.RulesetsV2.Model;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

#endregion

namespace CS2DemoKit.Analysis.Yaml;

/// <summary>
///     The v2 document pipeline entry point: parse a <c>ruleset:</c> YAML string once
///     (via the representation model, so nodes carry positions), map it to a
///     <see cref="RulesetDoc" />, then run stage-1 Expand (<c>for_each:</c>) and structural
///     validation. The directory and in-memory loaders (<see cref="YamlConfigLoader.TryLoadDirectory" />,
///     <see cref="YamlConfigLoader.LoadDocuments" />) split a source with <c>ParseDocuments</c> and
///     run each <c>---</c> document through <c>TryLoadRoot</c>, which returns <c>null</c> for a
///     document that is not a v2 ruleset so the caller can report it (retired-v1 /
///     not-a-rules-document). A YAML syntax error surfaces from <c>ParseDocuments</c> itself.
///     <see cref="Load" /> and <see cref="TryLoad" /> take a single-document source.
/// </summary>
public static class RulesetDocumentLoader
{
    /// <summary>
    ///     Attempts the v2 pipeline over a YAML string. Returns <c>null</c> when the document is not
    ///     a v2 ruleset — its root is not a mapping, it has no top-level <c>ruleset:</c> key, or it
    ///     failed to parse — so the caller can classify and report the file. A stream holding more
    ///     than one <c>---</c> document is not one ruleset: it gets an outcome with no doc and a
    ///     diagnostic pointing at <see cref="YamlConfigLoader.LoadDocuments" />, which loads each
    ///     document as its own ruleset, instead of silently reading only the first.
    /// </summary>
    /// <param name="yaml">The document source.</param>
    /// <param name="file">The absolute source path, or <c>null</c> for in-memory YAML.</param>
    /// <returns>The v2 outcome, or <c>null</c> when the file is not a v2 ruleset.</returns>
    public static Outcome? TryLoad(string yaml, string? file)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        IReadOnlyList<(int Index, YamlNode Root)>? documents = TryParse(yaml);
        if (documents is { Count: > 1 })
        {
            return MultiDocumentOutcome(documents.Count, file);
        }

        return documents is [(_, YamlNode root)] ? TryLoadRoot(root, file) : null;
    }

    /// <summary>
    ///     Runs the v2 pipeline over a YAML string, always attempting the v2 mapping. When the
    ///     document is not a v2 ruleset, returns an outcome carrying a single explanatory
    ///     diagnostic. Intended for tests and tools that already know the input is a ruleset.
    ///     A stream holding more than one <c>---</c> document gets the same refusal as
    ///     <see cref="TryLoad" />.
    /// </summary>
    /// <param name="yaml">The document source.</param>
    /// <param name="file">The absolute source path, or <c>null</c> for in-memory YAML.</param>
    /// <returns>The v2 outcome.</returns>
    public static Outcome Load(string yaml, string? file)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        IReadOnlyList<(int Index, YamlNode Root)>? documents = TryParse(yaml);
        if (documents is { Count: > 1 })
        {
            return MultiDocumentOutcome(documents.Count, file);
        }

        Outcome? outcome = documents is [(_, YamlNode root)] ? TryLoadRoot(root, file) : null;
        return outcome ?? new Outcome(null,
        [
            new RulesetDiagnostic(RulesetDiagnosticCodes.Missing,
                "not a v2 ruleset document — the root must be a map with a 'ruleset:' id",
                new SourcePosition(file, 0, 0))
        ]);
    }

    /// <summary>
    ///     Parses a YAML stream into its documents, skipping empty ones (a trailing <c>---</c>, a
    ///     comment-only document) so they neither load nor fail. Each document keeps its 1-based
    ///     position in the stream, empty ones counted, so a label built from it names the
    ///     document an author sees.
    /// </summary>
    /// <param name="yaml">The stream source.</param>
    /// <returns>The non-empty documents with their 1-based stream positions.</returns>
    /// <exception cref="YamlException">The stream is not well-formed YAML.</exception>
    internal static IReadOnlyList<(int Index, YamlNode Root)> ParseDocuments(string yaml)
    {
        YamlStream stream = [];
        using StringReader reader = new(yaml);
        stream.Load(reader);
        List<(int, YamlNode)> documents = new(stream.Documents.Count);
        for (int i = 0; i < stream.Documents.Count; i++)
        {
            YamlNode root = stream.Documents[i].RootNode;
            if (root is YamlScalarNode { Value: null or "" } scalar && scalar.Tag.IsEmpty)
            {
                continue;
            }

            documents.Add((i + 1, root));
        }

        return documents;
    }

    /// <summary>
    ///     Runs the v2 pipeline over one already-parsed document root. Returns <c>null</c> when the
    ///     root is not a map with a top-level <c>ruleset:</c> key.
    /// </summary>
    /// <param name="root">The document root.</param>
    /// <param name="file">The label diagnostics are attributed to.</param>
    /// <returns>The v2 outcome, or <c>null</c> when the document is not a v2 ruleset.</returns>
    internal static Outcome? TryLoadRoot(YamlNode root, string? file)
    {
        if (root is not YamlMappingNode map)
        {
            return null;
        }

        foreach (YamlNode key in map.Children.Keys)
        {
            if (key is YamlScalarNode { Value: "ruleset" })
            {
                return LoadFromRoot(map, file);
            }
        }

        return null;
    }

    private static IReadOnlyList<(int Index, YamlNode Root)>? TryParse(string yaml)
    {
        try
        {
            return ParseDocuments(yaml);
        }
        catch (YamlException)
        {
            return null;
        }
    }

    private static Outcome MultiDocumentOutcome(int count, string? file) =>
        new(null,
        [
            new RulesetDiagnostic(RulesetDiagnosticCodes.WrongShape,
                $"this YAML holds {count} '---' documents, and this entry point loads one ruleset; "
                + "load it through YamlConfigLoader.LoadDocuments or TryLoadDirectory, which load "
                + "each document as its own ruleset",
                new SourcePosition(file, 0, 0))
        ]);

    private static Outcome LoadFromRoot(YamlMappingNode root, string? file)
    {
        RulesetYamlMapper.MapResult mapped = RulesetYamlMapper.Map(root, file);
        if (mapped.Doc is null)
        {
            return new Outcome(null, mapped.Diagnostics);
        }

        // Stage-1 Expand runs before duplicate-id checking so the validator sees expanded ids.
        RulesetDoc expanded = ForEachExpander.Expand(mapped.Doc);

        IReadOnlyList<RulesetDiagnostic> structural = RulesetStructuralValidator.Validate(expanded);
        IReadOnlyList<RulesetDiagnostic> all = mapped.Diagnostics.Count == 0
            ? structural
            : [.. mapped.Diagnostics, .. structural];
        return new Outcome(expanded, all);
    }

    /// <summary>The outcome of loading one v2 ruleset document.</summary>
    /// <param name="Doc">The mapped, expanded ruleset (best-effort even with diagnostics), or <c>null</c> when unmappable.</param>
    /// <param name="Diagnostics">Every mapping / expansion / validation diagnostic, in document order.</param>
    public sealed record Outcome(RulesetDoc? Doc, IReadOnlyList<RulesetDiagnostic> Diagnostics);
}
