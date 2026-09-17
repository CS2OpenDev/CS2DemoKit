#region

using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Parser;

#endregion

namespace CS2DemoKit.Analysis;

/// <summary>
///     What a graph build needs from the demo it will run over: the tick rate that folds duration
///     literals, and the source profile that binds logical events. Both are known from a demo's
///     signon prefix, so a build never needs the whole demo.
/// </summary>
/// <param name="TickRate">Server ticks per second.</param>
/// <param name="Profile">The resolved source profile, dialect included.</param>
public sealed record AnalysisTarget(int TickRate, DemoSourceProfile Profile)
{
    /// <summary>A target for a retained demo, resolving its profile from the events it carries.</summary>
    public static AnalysisTarget From(ParsedDemo demo, DemoSourceProfile? profile = null)
    {
        ArgumentNullException.ThrowIfNull(demo);
        return new AnalysisTarget(demo.TickRate, profile ?? DemoAnalysis.ResolveProfile(demo));
    }
}
