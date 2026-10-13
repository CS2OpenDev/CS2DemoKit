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

    /// <summary>
    ///     Project metric tables from an analysis run. The default needs the run's snapshots; a
    ///     projector that can read a run without them (<see cref="PlayerGameStatsProjector" />)
    ///     overrides it.
    /// </summary>
    /// <param name="run">The analysis run.</param>
    /// <param name="demo">The demo facts to project against.</param>
    /// <returns>Zero or more tables.</returns>
    /// <exception cref="NotSupportedException">The run captured no snapshots and this projector needs them.</exception>
    IReadOnlyList<MetricTable> Project(AnalysisRun run, DemoDescriptor demo)
    {
        ArgumentNullException.ThrowIfNull(run);
        return run.Snapshots is { } result
            ? Project(result, demo)
            : throw new NotSupportedException($"{GetType().Name} needs snapshots.");
    }
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
