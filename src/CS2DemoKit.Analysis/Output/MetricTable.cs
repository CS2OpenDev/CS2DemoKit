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
    IReadOnlyList<MetricRow> Rows)
{
    /// <summary>The value <c>"frame"</c> in <see cref="ColumnClocks" />: the frame clock.</summary>
    public const string FrameClock = "frame";

    /// <summary>The value <c>"server"</c> in <see cref="ColumnClocks" />: the absolute server tick.</summary>
    public const string ServerClock = "server";

    /// <summary>
    ///     The clock each tick-valued value column is on, keyed by column label: <see cref="FrameClock" />
    ///     (the index <c>DemoFrame</c>, timeline events and highlights use) or <see cref="ServerClock" />
    ///     (<c>GameEvent.ServerTick</c>, higher by the demo's <c>ServerStartTick</c>). Only bare tick
    ///     captures are listed; a column absent from the map is not a tick, or is derived from one
    ///     (a difference of two ticks is on no clock). Empty for a table with no tick columns.
    /// </summary>
    public IReadOnlyDictionary<string, string> ColumnClocks { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
}
