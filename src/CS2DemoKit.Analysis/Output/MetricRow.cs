namespace CS2DemoKit.Analysis.Output;

/// <summary>
///     A single row of dimensioned metric values, following the OpenTelemetry-inspired model in
///     <c>docs/analysis-engine/ANALYSIS_ENGINE_OUTPUT_DESIGN.md</c>: a row is a set of <see cref="Values" /> (the
///     measurements) enriched with <see cref="Dimensions" /> (the context that describes what the
///     values apply to — the "group by" axes).
/// </summary>
/// <param name="Dimensions">
///     The dimension tags for this row (e.g. <c>match_id</c>, <c>round_number</c>, <c>player_slot</c>).
///     Keys correspond to <see cref="MetricTable.DimensionColumns" />.
/// </param>
/// <param name="Values">
///     The measured values for this row (e.g. <c>kills</c>, <c>deaths</c>, <c>damage</c>). Keys
///     correspond to <see cref="MetricTable.ValueColumns" />. A missing key (or a <c>null</c> value)
///     means "not reported" — distinct from a measured zero.
/// </param>
public sealed record MetricRow(
    IReadOnlyDictionary<string, object?> Dimensions,
    IReadOnlyDictionary<string, object?> Values);
