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

    /// <summary>
    ///     Max firings observed in any single demo, per view whose <c>views.yaml</c> entry says
    ///     <c>rate: measured</c>: its event's occurrences that pass its <c>baked:</c> filters.
    /// </summary>
    public Dictionary<string, int> ViewMaxPerDemo { get; init; } = new(StringComparer.Ordinal);

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
                StringComparer.OrdinalIgnoreCase),
            ViewMaxPerDemo = new Dictionary<string, int>(
                ViewMaxPerDemo.OrderBy(kv => kv.Key, StringComparer.Ordinal),
                StringComparer.Ordinal)
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
    public string Classify(string name) =>
        MaxPerDemo.TryGetValue(name, out int max) ? Classify(max) : "unmeasured";

    /// <summary>
    ///     Classifies a measured view (<see cref="ViewMaxPerDemo" />) on the same thresholds as
    ///     <see cref="Classify(string)" />. A view the baseline never folded is <c>unmeasured</c>.
    /// </summary>
    public string ClassifyView(string view) =>
        ViewMaxPerDemo.TryGetValue(view, out int max) ? Classify(max) : "unmeasured";

    private static string Classify(int maxPerDemo) =>
        maxPerDemo switch
        {
            <= 50 => "perMatch",
            <= 600 => "perRound",
            <= 20000 => "frequent",
            _ => "perTick"
        };

    /// <summary>A view <see cref="Measure" /> folds: its name, its wire event and its baked filters.</summary>
    /// <param name="Name">The view name, the key in <see cref="ViewMaxPerDemo" />.</param>
    /// <param name="Event">The wire event the filters apply to.</param>
    /// <param name="Baked">The <c>baked:</c> filters, every one of which an occurrence must pass to count.</param>
    public sealed record MeasuredView(string Name, string Event, IReadOnlyList<string> Baked);

    /// <summary>
    ///     Measures the corpus: parses every .dem under <paramref name="benchDir" />, counting
    ///     game events by wire name, net messages by payload type name and each of
    ///     <paramref name="views" /> through its baked filters, keeping the max per demo. ~seconds
    ///     per demo; run explicitly via <c>--measure</c>.
    /// </summary>
    public static FrequencyBaseline Measure(string benchDir, IReadOnlyList<MeasuredView> views)
    {
        ArgumentNullException.ThrowIfNull(views);
        FrequencyBaseline baseline = new();
        List<(MeasuredView View, Func<GameEvent, bool> Passes)> folds =
            [.. views.Select(v => (v, BakedFilter.Compile(v.Baked)))];
        foreach (string demoPath in Directory.EnumerateFiles(benchDir, "*.dem").Order(StringComparer.Ordinal))
        {
            Console.WriteLine($"  measuring {Path.GetFileName(demoPath)}…");
            ParsedDemo parsed = DemoParser.Parse(File.ReadAllBytes(demoPath).AsMemory());

            Dictionary<string, int> counts = new(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, int> viewCounts = new(StringComparer.Ordinal);
            foreach (GameEvent e in parsed.AllGameEvents)
            {
                counts[e.Name] = counts.GetValueOrDefault(e.Name) + 1;
                foreach ((MeasuredView view, Func<GameEvent, bool> passes) in folds)
                {
                    if (string.Equals(view.Event, e.Name, StringComparison.OrdinalIgnoreCase) && passes(e))
                    {
                        viewCounts[view.Name] = viewCounts.GetValueOrDefault(view.Name) + 1;
                    }
                }
            }

            foreach (DemoFrame frame in parsed.Frames)
            {
                foreach (NetMessage message in frame.InnerMessages)
                {
                    // The stored svc_UserCmds payloads compose into the list as block entries.
                    string payloadName = message.Payload.GetType().Name is "BlockMessage"
                        ? nameof(CSVCMsg_UserCommands)
                        : message.Payload.GetType().Name;
                    counts[payloadName] = counts.GetValueOrDefault(payloadName) + 1;
                }
            }

            baseline.MeasuredFrom.Add(Path.GetFileName(demoPath));
            foreach ((string name, int count) in counts)
            {
                baseline.MaxPerDemo[name] = Math.Max(baseline.MaxPerDemo.GetValueOrDefault(name), count);
            }

            // A view its event never fired on a demo still measured there: it fired 0 times.
            foreach ((MeasuredView view, _) in folds)
            {
                baseline.ViewMaxPerDemo[view.Name] = Math.Max(baseline.ViewMaxPerDemo.GetValueOrDefault(view.Name),
                    viewCounts.GetValueOrDefault(view.Name));
            }
        }

        return baseline;
    }
}
