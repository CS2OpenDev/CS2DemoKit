namespace CS2DemoKit.Bench;

/// <summary>
///     The one line every orchestrator prints before its first measurement, saying whether the
///     rows about to be written will contain rays. It exists because the two states produce rows
///     of the same shape under the same label, and a CSV read three weeks later cannot say which
///     pipeline it measured unless the run said so at the time.
/// </summary>
internal static class RayPathBanner
{
    public static string Line()
    {
        string? dir = Environment.GetEnvironmentVariable(Measurement.CollisionDirVariable);
        if (Measurement.RayPathArmed)
        {
            return $"ray path ON: {Measurement.CollisionDirVariable}={dir}; a demo whose map has no bake there FAILS its rows";
        }

        // The library would fall back to the legacy name; this tool does not, and a user who set
        // only that one should read why their rows carry no rays here rather than in the CSV.
        string legacy = string.IsNullOrWhiteSpace(
            Environment.GetEnvironmentVariable(Measurement.LegacyCollisionDirVariable))
            ? ""
            : $" ({Measurement.LegacyCollisionDirVariable} is set, and this tool ignores it)";
        return $"ray path OFF: {Measurement.CollisionDirVariable} is unset{legacy}, no rays are cast, every row carries vis=0";
    }
}
