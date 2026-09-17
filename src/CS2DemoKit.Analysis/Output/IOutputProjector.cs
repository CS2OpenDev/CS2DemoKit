#region

using CS2DemoKit.Parser;

#endregion

namespace CS2DemoKit.Analysis.Output;

/// <summary>
///     Extracts one or more <see cref="MetricTable" />s from an <see cref="EvaluationResult" /> at
///     semantically meaningful boundaries (round-end, game-end, per-event). Projectors are pure
///     transforms — they read the evaluation snapshots and produce dimensioned tabular data without
///     mutating the result or touching the wire.
/// </summary>
public interface IOutputProjector
{
    /// <summary>Project metric tables from an evaluation result and its source demo.</summary>
    /// <param name="result">The full evaluation result (snapshots + materialized players + nodes).</param>
    /// <param name="demo">The parsed demo, for dimension context (map, players, filename).</param>
    /// <returns>Zero or more tables. Most built-in projectors return exactly one.</returns>
    IReadOnlyList<MetricTable> Project(EvaluationResult result, DemoDescriptor demo);
}

public static class OutputProjectorExtensions
{
    /// <summary>Projects against a retained demo's final facts.</summary>
    public static IReadOnlyList<MetricTable> Project(this IOutputProjector projector, EvaluationResult result, ParsedDemo demo)
    {
        ArgumentNullException.ThrowIfNull(projector);
        ArgumentNullException.ThrowIfNull(demo);
        return projector.Project(result, DemoDescriptor.From(demo));
    }
}
