#region

using System.Security.Cryptography;
using System.Text;
using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Analysis.Graphs;

#endregion

namespace CS2DemoKit.Analysis.Tests;

/// <summary>
///     Pins the order the builder hands edges to the engine, which the evaluator's dispatch slots and
///     topological sort start from. Three sequences per build: the game graph's edges as registered,
///     the per-player template's edges as one player materialises them (the evaluator registers them
///     in list order and hands the same list to its logic-node registration), and the relevant message
///     types. An edge is written as its CLR type, dispatch type, source and written node, so two edges
///     of one type that swap places move the pin too.
///     <para>
///         The rule-graph work routes every edge through a descriptor collector, and this is what
///         proves that routing reordered nothing. The pins were taken from the builder before the
///         collector existed. On a mismatch the failure prints the sequence, so a deliberate change can
///         be reviewed line by line before it is re-pinned.
///     </para>
/// </summary>
[Category("Unit")]
public class GraphWiringOrderPinTests
{
    // "{case}/{sequence}" -> "{line count}:{first 16 hex of the sha256}", taken at dc0c6b6. The HLTV
    // shipped template did not materialise there (#68), so its player sequence was pinned with that fix.
    private static readonly Dictionary<string, string> _pins = new(StringComparer.Ordinal)
    {
        ["shipped-gotv/game"] = "67:7A54066EF8A22166",
        ["shipped-gotv/player"] = "175:89DB8710380C4966",
        ["shipped-gotv/types"] = "37:15C0DE3C4278A0EC",
        ["shipped-hltv/game"] = "67:DDC55B358BE2531E",
        ["shipped-hltv/player"] = "168:9EBA3A53627CE1BB",
        ["shipped-hltv/types"] = "36:8F56BA4EFADEE068",
        ["matrix-gotv/game"] = "95:4FCB5A171704463B",
        ["matrix-gotv/player"] = "42:6CBDE05D43E642B3",
        ["matrix-gotv/types"] = "31:2FCCF886F0F9711A",
        ["matrix-hltv/game"] = "95:C1E1B0A13DEC4ED0",
        ["matrix-hltv/player"] = "42:6D75E9C00D13F614",
        ["matrix-hltv/types"] = "31:2FCCF886F0F9711A"
    };

    public static IEnumerable<string> CaseNames() => RuleGraphFixtures.Cases().Select(c => c.Name);

    [Test]
    [MethodDataSource(nameof(CaseNames))]
    public async Task EdgeAndMessageOrder_MatchesPin(string name)
    {
        (_, Func<BuildResult> make) = RuleGraphFixtures.Cases().Single(c => c.Name == name);
        BuildResult build = make();

        List<string> game = [.. build.Graph.Edges.Select(Describe)];
        List<string> types = [.. build.RelevantMessageTypes.Select(t => $"{t.Namespace}.{TypeName(t)}").Order(StringComparer.Ordinal)];
        List<string> perPlayer = [];
        for (int i = 0; i < build.Graph.PerPlayerTemplates.Count; i++)
        {
            PerPlayerNodeTemplate.MaterializedPlayer p = build.Graph.PerPlayerTemplates[i].Materialize(0, 0, "p0");
            perPlayer.Add($"# template {i}");
            perPlayer.AddRange(p.Edges.Select(Describe));
        }

        List<string> failures = [];
        Check(name + "/game", game, failures);
        Check(name + "/player", perPlayer, failures);
        Check(name + "/types", types, failures);

        await Assert.That(string.Join("\n\n", failures)).IsEmpty();
    }

    private static void Check(string key, List<string> lines, List<string> failures)
    {
        string actual = Pin(lines);
        if (!_pins.TryGetValue(key, out string? expected) || expected != actual)
        {
            failures.Add($"[\"{key}\"] = \"{actual}\" (pinned {expected ?? "nothing"})\n  "
                         + string.Join("\n  ", lines));
        }
    }

    private static string Pin(List<string> lines)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", lines)));
        return $"{lines.Count}:{Convert.ToHexString(hash)[..16]}";
    }

    private static string Describe(StateEdge edge) =>
        $"{TypeName(edge.GetType())}|{TypeName(edge.MessageType)}|{edge.Source.Name}|{edge.WrittenNode?.Name ?? "-"}";

    private static string TypeName(Type type)
    {
        if (!type.IsGenericType)
        {
            return type.Name;
        }

        string name = type.Name[..type.Name.IndexOf('`', StringComparison.Ordinal)];
        return $"{name}<{string.Join(",", type.GetGenericArguments().Select(TypeName))}>";
    }
}
