#region

using System.Text.Json;

#endregion

namespace CS2DemoKit.Analysis.Tests.RulesV2;

/// <summary>
///     Shared IO for the Rulesets v2 pilot goldens' pinned expectations. At the production cutover
///     (the v1 rule files were removed) the four pilots stopped comparing v2 against a
///     <b>live</b> v1 oracle and instead assert v2 against captured constants. Those constants are
///     the JSON fixtures under <c>tests/fixtures/rules-v2/&lt;name&gt;.expected.json</c>, captured
///     from the v2==v1-verified run at cutover.
///     <para>
///         Each pilot regenerates its fixture when the <c>PIN_RULES_V2=1</c> environment variable is
///         set (the deliberate, reviewed re-pin path — mirrors AnalysisBench's golden-regen gate),
///         and otherwise reads the committed fixture and asserts the v2 result matches it. The demo,
///         ruleset files, resolver and planner are all deterministic, so the fixture is a stable pin.
///     </para>
/// </summary>
internal static class PilotFixture
{
    private static readonly JsonSerializerOptions _options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    /// <summary>True when the deliberate re-pin path is requested (<c>PIN_RULES_V2=1</c>).</summary>
    internal static bool Regenerate =>
        string.Equals(Environment.GetEnvironmentVariable("PIN_RULES_V2"), "1", StringComparison.Ordinal);

    private static string Dir(string repoRoot) => Path.Combine(repoRoot, "tests", "fixtures", "rules-v2");

    private static string PathFor(string repoRoot, string name) =>
        Path.Combine(Dir(repoRoot), name + ".expected.json");

    internal static void Write<T>(string repoRoot, string name, T value)
    {
        Directory.CreateDirectory(Dir(repoRoot));
        File.WriteAllText(PathFor(repoRoot, name), JsonSerializer.Serialize(value, _options));
    }

    internal static T Read<T>(string repoRoot, string name)
    {
        string path = PathFor(repoRoot, name);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"rules-v2 pilot fixture '{name}' not found at {path}. Regenerate with PIN_RULES_V2=1.", path);
        }

        return JsonSerializer.Deserialize<T>(File.ReadAllText(path), _options)
               ?? throw new InvalidOperationException($"rules-v2 pilot fixture '{name}' deserialized to null");
    }
}
