namespace CS2DemoKit.Analysis.Output;

/// <summary>
///     The shared format-id → <see cref="IOutputFormatter" /> lookup, so the app's export dialog and
///     export selectors resolve formats through one table (a new format is one
///     formatter file + one line here, and every consumer picks it up). Formatters are pure functions
///     of the table (stateless), so shared singleton instances are safe.
/// </summary>
public static class OutputFormatterRegistry
{
    private static readonly Dictionary<string, IOutputFormatter> _formatters = new(StringComparer.OrdinalIgnoreCase)
    {
        ["csv"] = new CsvOutputFormatter(),
        ["json"] = new JsonOutputFormatter()
    };

    /// <summary>All registered format ids, for UI pickers and CLI usage text.</summary>
    public static IReadOnlyCollection<string> Ids => _formatters.Keys;

    /// <summary>Resolves a format id (case-insensitive), or null when unknown.</summary>
    public static IOutputFormatter? Get(string id) => _formatters.GetValueOrDefault(id.Trim());
}
