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
    // "{case}/{sequence}" -> "{line count}:{first 16 hex of the sha256}", taken before every edge was
    // described (#50). The HLTV shipped template did not materialise then (#68), so its player
    // sequence was pinned with that fix. The player sequences gained the two player.team node edges, then the freeze-end team latch.
    // Each game sequence then gained one line, match_live's $round_freeze_end activation. The
    // round_end_terminal close then added a concrete $round_end event to the GOTV family, which is
    // why only its sequences carry the type and the round-end edges for it; HLTV overrides the
    // binding without it, so its game sequence moved for the activation alone. The kast gates moved
    // four player edges from Root to MatchLive on both profiles, which is a reorder at the same
    // count on HLTV.
    private static readonly Dictionary<string, string> _pins = new(StringComparer.Ordinal)
    {
        ["shipped-gotv/game"] = "70:E8396FE962E8104D",
        ["shipped-gotv/player"] = "204:B658ACF6091E175B",
        ["shipped-gotv/types"] = "38:BFFD54C887EBAC52",
        ["shipped-hltv/game"] = "66:4D9CF4CA6FDE3950",
        ["shipped-hltv/player"] = "171:02659CE95D6B30AC",
        ["shipped-hltv/types"] = "35:51EF2DA810C41F6F",
        ["matrix-gotv/game"] = "103:40A9B7AE893B86A3",
        ["matrix-gotv/player"] = "50:0779142BA4CFE2B1",
        ["matrix-gotv/types"] = "32:C176D6CD198773A1",
        ["matrix-hltv/game"] = "94:ECB935D642CC7EF3",
        ["matrix-hltv/player"] = "45:0EFB1AB42FD00667",
        ["matrix-hltv/types"] = "30:66E2445A8A04AE6D"
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
