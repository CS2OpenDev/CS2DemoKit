namespace CS2DemoKit.Analysis.Output;

/// <summary>
///     Serializes a <see cref="MetricTable" /> to a textual representation (CSV, JSON, …). Formatters
///     are pure functions of the table — column ordering is driven by
///     <see cref="MetricTable.DimensionColumns" /> / <see cref="MetricTable.ValueColumns" />, not by
///     per-row dictionary enumeration order, so the emitted schema is stable.
/// </summary>
public interface IOutputFormatter
{
    /// <summary>The conventional file extension (without the dot) for this format, e.g. <c>csv</c>.</summary>
    string FileExtension { get; }

    /// <summary>Render the table to a single string.</summary>
    string Format(MetricTable table);

    /// <summary>Render the table and write it to <paramref name="path" /> as UTF-8.</summary>
    void WriteToFile(MetricTable table, string path) => File.WriteAllText(path, Format(table));
}
