#region

using System.Text.Json;
using CS2DemoKit.Parser;
using CS2DemoKit.Parser.GameEvents;

#endregion

namespace CS2DemoKit.RulesCatalog;

/// <summary>
///     Measured trigger frequencies over the bench corpus: every
///     catalog trigger entry carries a <c>frequencyClass</c> the high-frequency lints key on —
///     measured, never hand-tagged. Measurement is an EXPLICIT re-baseline (the generator's
///     <c>--measure</c> verb parses demos/benchmarks and rewrites the committed
///     frequency-baseline.json); ordinary regens merge the committed baseline so catalog output
///     stays deterministic without demos present.
/// </summary>
public sealed class FrequencyBaseline
{
    /// <summary>Committed baseline path, relative to the repo root.</summary>
    public const string RelativePath = "tools/CS2DemoKit.RulesCatalog/frequency-baseline.json";

    private static readonly JsonSerializerOptions _options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>Demo file names the counts were measured from.</summary>
    public List<string> MeasuredFrom { get; init; } = [];

    /// <summary>Max occurrences observed in any single demo, per event / net-message name.</summary>
    public Dictionary<string, int> MaxPerDemo { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Loads the committed baseline, or an empty one (everything "unmeasured") if absent.</summary>
    public static FrequencyBaseline Load(string repoRoot)
    {
        string path = Path.Combine(repoRoot, RelativePath);
        return File.Exists(path)
            ? JsonSerializer.Deserialize<FrequencyBaseline>(File.ReadAllText(path), _options)
              ?? new FrequencyBaseline()
            : new FrequencyBaseline();
    }

    /// <summary>Serializes with the committed-format contract (deterministic, sorted, LF).</summary>
    public string Serialize()
    {
        FrequencyBaseline sorted = new()
        {
            MeasuredFrom = [.. MeasuredFrom.Order(StringComparer.Ordinal)],
            MaxPerDemo = new Dictionary<string, int>(
                MaxPerDemo.OrderBy(kv => kv.Key, StringComparer.Ordinal),
                StringComparer.OrdinalIgnoreCase)
        };
        return JsonSerializer.Serialize(sorted, _options).ReplaceLineEndings("\n") + "\n";
    }

    /// <summary>
    ///     Classifies a trigger name from the measured counts. Thresholds are per-demo maxima
    ///     over a full competitive match (~20–30 rounds, 64 tick/s):
    ///     <c>perMatch</c> ≤ 50, <c>perRound</c> ≤ 600, <c>frequent</c> ≤ 20000,
    ///     <c>perTick</c> above (CNETMsg_Tick measures 100K+). Unknown names are
    ///     <c>unmeasured</c> — lints must treat that as "no evidence", not "safe".
    /// </summary>
    public string Classify(string name)
    {
        if (!MaxPerDemo.TryGetValue(name, out int max))
        {
            return "unmeasured";
        }

        return max switch
        {
            <= 50 => "perMatch",
            <= 600 => "perRound",
            <= 20000 => "frequent",
            _ => "perTick"
        };
    }

    /// <summary>
    ///     Measures the corpus: parses every .dem under <paramref name="benchDir" />, counting
    ///     game events by wire name and net messages by payload type name, keeping the max per
    ///     demo. ~seconds per demo; run explicitly via <c>--measure</c>.
    /// </summary>
    public static FrequencyBaseline Measure(string benchDir)
    {
        FrequencyBaseline baseline = new();
        foreach (string demoPath in Directory.EnumerateFiles(benchDir, "*.dem").Order(StringComparer.Ordinal))
        {
            Console.WriteLine($"  measuring {Path.GetFileName(demoPath)}…");
            ParsedDemo parsed = DemoParser.Parse(File.ReadAllBytes(demoPath).AsMemory());

            Dictionary<string, int> counts = new(StringComparer.OrdinalIgnoreCase);
            foreach (GameEvent e in parsed.AllGameEvents)
            {
                counts[e.Name] = counts.GetValueOrDefault(e.Name) + 1;
            }

            foreach (DemoFrame frame in parsed.Frames)
            {
                foreach (NetMessage message in frame.InnerMessages)
                {
                    string payloadName = message.Payload.GetType().Name;
                    counts[payloadName] = counts.GetValueOrDefault(payloadName) + 1;
                }
            }

            baseline.MeasuredFrom.Add(Path.GetFileName(demoPath));
            foreach ((string name, int count) in counts)
            {
                baseline.MaxPerDemo[name] = Math.Max(baseline.MaxPerDemo.GetValueOrDefault(name), count);
            }
        }

        return baseline;
    }
}
