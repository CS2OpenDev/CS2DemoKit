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
///     validation. The loader dispatch (<see cref="YamlConfigLoader.TryLoadDirectory" />) uses
///     <see cref="TryLoad" />, which returns <c>null</c> for any file that is not a v2 ruleset so
///     the caller can report it (retired-v1 / not-a-rules-document / YAML syntax error).
/// </summary>
public static class RulesetDocumentLoader
{
    /// <summary>
    ///     Attempts the v2 pipeline over a YAML string. Returns <c>null</c> when the document is not
    ///     a v2 ruleset — its root is not a mapping, it has no top-level <c>ruleset:</c> key, or it
    ///     failed to parse — so the caller can classify and report the file.
    /// </summary>
    /// <param name="yaml">The document source.</param>
    /// <param name="file">The absolute source path, or <c>null</c> for in-memory YAML.</param>
    /// <returns>The v2 outcome, or <c>null</c> when the file is not a v2 ruleset.</returns>
    public static Outcome? TryLoad(string yaml, string? file)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        YamlMappingNode? root = TryGetRulesetRoot(yaml);
        return root is null ? null : LoadFromRoot(root, file);
    }

    /// <summary>
    ///     Runs the v2 pipeline over a YAML string, always attempting the v2 mapping. When the
    ///     document is not a v2 ruleset, returns an outcome carrying a single explanatory
    ///     diagnostic. Intended for tests and tools that already know the input is a ruleset.
    /// </summary>
    /// <param name="yaml">The document source.</param>
    /// <param name="file">The absolute source path, or <c>null</c> for in-memory YAML.</param>
    /// <returns>The v2 outcome.</returns>
    public static Outcome Load(string yaml, string? file)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        YamlMappingNode? root = TryGetRulesetRoot(yaml);
        return root is null
            ? new Outcome(null,
            [
                new RulesetDiagnostic(RulesetDiagnosticCodes.Missing,
                    "not a v2 ruleset document — the root must be a map with a 'ruleset:' id",
                    new SourcePosition(file, 0, 0))
            ])
            : LoadFromRoot(root, file);
    }

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

    private static YamlMappingNode? TryGetRulesetRoot(string yaml)
    {
        try
        {
            YamlStream stream = [];
            using StringReader reader = new(yaml);
            stream.Load(reader);
            if (stream.Documents.Count == 0 || stream.Documents[0].RootNode is not YamlMappingNode root)
            {
                return null;
            }

            foreach (YamlNode key in root.Children.Keys)
            {
                if (key is YamlScalarNode { Value: "ruleset" })
                {
                    return root;
                }
            }

            return null;
        }
        catch (YamlException)
        {
            return null;
        }
    }

    /// <summary>The outcome of loading one v2 ruleset document.</summary>
    /// <param name="Doc">The mapped, expanded ruleset (best-effort even with diagnostics), or <c>null</c> when unmappable.</param>
    /// <param name="Diagnostics">Every mapping / expansion / validation diagnostic, in document order.</param>
    public sealed record Outcome(RulesetDoc? Doc, IReadOnlyList<RulesetDiagnostic> Diagnostics);
}
