namespace CS2DemoKit.Analysis.Output;

/// <summary>
///     A named, columnar collection of <see cref="MetricRow" />s. The column lists define the
///     canonical ordering of dimensions and values so that formatters (CSV, JSON, DataGrid) emit a
///     stable, predictable schema regardless of per-row dictionary enumeration order.
/// </summary>
/// <param name="Name">
///     The table identity (e.g. <c>player_round_stats</c>). Formatters use it to name output files.
/// </param>
/// <param name="DimensionColumns">Ordered dimension column keys — emitted before the value columns.</param>
/// <param name="ValueColumns">Ordered value column keys — emitted after the dimension columns.</param>
/// <param name="Rows">
///     The rows. Each row's <see cref="MetricRow.Dimensions" /> / <see cref="MetricRow.Values" />
///     are read positionally via these column lists; a key absent from a row is rendered as empty / null.
/// </param>
public sealed record MetricTable(
    string Name,
    IReadOnlyList<string> DimensionColumns,
    IReadOnlyList<string> ValueColumns,
    IReadOnlyList<MetricRow> Rows);
