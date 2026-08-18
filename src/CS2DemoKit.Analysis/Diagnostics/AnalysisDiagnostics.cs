#region

using System.Diagnostics;

#endregion

namespace CS2DemoKit.Analysis.Diagnostics;

/// <summary>
///     Runtime <see cref="ActivitySource" /> for the analysis / load-pipeline phase timeline
///     (<c>analysis.eval</c> ⊃ <c>analysis.precompute</c>, plus any phases a host chooses to span).
///     <see cref="System.Diagnostics.ActivitySource.StartActivity(string, ActivityKind)" /> returns <c>null</c> when no
///     <see cref="ActivityListener" /> is sampling, so these spans are near-free in the default build:
///     they ship in the binary but cost ~one predicted branch when idle (no allocation, no listener).
///     Capture them with <c>dotnet-trace</c>, an OpenTelemetry exporter, or any custom
///     <see cref="ActivityListener" />.
/// </summary>
public static class AnalysisDiagnostics
{
    /// <summary>Source name that listeners filter on: <c>CS2DemoKit.Analysis</c>.</summary>
    public const string SourceName = "CS2DemoKit.Analysis";

    /// <summary>The shared analysis-pipeline activity source.</summary>
    public static readonly ActivitySource ActivitySource = new(SourceName);
}
