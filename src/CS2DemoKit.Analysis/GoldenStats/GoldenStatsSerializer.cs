#region

using System.Text.Json;
using System.Text.Json.Serialization;

#endregion

namespace CS2DemoKit.Analysis.GoldenStats;

/// <summary>
///     JSON read/write helpers for <see cref="GoldenStatsDocument" />. Centralises
///     serialiser options so every callsite produces the same on-disk format —
///     indented JSON, snake_case property names, nulls skipped on write.
/// </summary>
public static class GoldenStatsSerializer
{
    /// <summary>
    ///     Singleton options used for both read and write. Built once;
    ///     <see cref="JsonSerializerOptions" /> is thread-safe after first use.
    /// </summary>
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // Property names already declared via [JsonPropertyName] on the records,
        // so a naming-policy isn't strictly required — but set it anyway so any
        // future stat-key serialisation falls in line by default.
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DictionaryKeyPolicy = null // Player names + canonical stat keys are
        // emitted verbatim; no transformation.
    };

    /// <summary>Deserialises from a JSON string. Throws on malformed input.</summary>
    public static GoldenStatsDocument Deserialize(string json) =>
        JsonSerializer.Deserialize<GoldenStatsDocument>(json, Options)
        ?? throw new InvalidOperationException("GoldenStatsDocument JSON deserialized to null.");

    /// <summary>Reads from a file. Throws if missing or malformed.</summary>
    public static GoldenStatsDocument ReadFromFile(string path) =>
        Deserialize(File.ReadAllText(path));

    /// <summary>Serialises to indented JSON.</summary>
    public static string Serialize(GoldenStatsDocument stats) =>
        JsonSerializer.Serialize(stats, Options);

    /// <summary>Writes the stats to a file (UTF-8, indented).</summary>
    public static void WriteToFile(GoldenStatsDocument stats, string path) =>
        File.WriteAllText(path, Serialize(stats));
}
