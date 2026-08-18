#region

using CS2DemoKit.Analysis.Catalog;
using CS2DemoKit.Analysis.RulesetsV2.Compile;
using CS2DemoKit.Analysis.RulesetsV2.Model;
using CS2DemoKit.Analysis.RulesetsV2.Resolve;
using CS2DemoKit.Analysis.Yaml;

#endregion

namespace CS2DemoKit.Analysis.Tests.RulesV2;

/// <summary>
///     Condition source lists: a <c>flag:</c> / <c>highlight:</c> <c>when:</c> may be
///     authored as a YAML <b>list</b> of predicate strings, meaning their <b>AND-conjunction</b>
///     (<c>when: [a, b]</c> ≡ <c>when: "(a) and (b)"</c>). This battery pins the "pure sugar"
///     contract: the list collapses to the AND-joined string at the model boundary, so
///     <list type="bullet">
///         <item>the mapped model text is exactly the parenthesized-and-joined string;</item>
///         <item>
///             a single-item list <c>[p]</c> collapses to the bare scalar <c>p</c> (no spurious
///             <c>and</c>);
///         </item>
///         <item>
///             the resolved-identity <see cref="V2StatHasher" /> hash of a list-form flag is
///             byte-identical to its string twin — the load-bearing proof that identity and planner
///             lowering are automatically correct with no preimage change;
///         </item>
///         <item>an empty list <c>when: []</c> is a structural error.</item>
///     </list>
///     Demo-free; pure config-layer + resolver.
/// </summary>
[Category("Unit")]
public class WhenListFormTests
{
    // Two sibling counters the flags below read; the list vs. string flags are structurally identical
    // except the `when:` authoring shape, so they must dedup onto one node (equal hash).
    private const string ListVsStringYaml = """
                                            ruleset: when_list_probe
                                            for: each_player
                                            stats:
                                              kills:
                                                count: kill
                                                per: round
                                              assists:
                                                count: assist
                                                per: round
                                              list_flag:
                                                flag:
                                                  when:
                                                    - kills > 0
                                                    - assists > 0
                                                per: round
                                              string_flag:
                                                flag:
                                                  when: "(kills > 0) and (assists > 0)"
                                                per: round
                                              single_list_flag:
                                                flag:
                                                  when:
                                                    - kills > 0
                                                per: round
                                              single_string_flag:
                                                flag:
                                                  when: "kills > 0"
                                                per: round
                                            """;

    private const string HighlightListYaml = """
                                             ruleset: when_list_highlight_probe
                                             for: each_player
                                             stats:
                                               kills:
                                                 count: kill
                                                 per: round
                                             highlights:
                                               list_hl:
                                                 when:
                                                   - kills > 0
                                                   - kills < 5
                                                 title: "{kills}"
                                               single_hl:
                                                 when:
                                                   - kills > 0
                                                 title: "{kills}"
                                             """;

    private const string EmptyListYaml = """
                                         ruleset: when_empty_list_probe
                                         for: each_player
                                         stats:
                                           kills:
                                             count: kill
                                             per: round
                                           bad_flag:
                                             flag:
                                               when: []
                                             per: round
                                         """;

    /// <summary>
    ///     The mapper collapses a multi-item flag <c>when:</c> list to the parenthesized AND-joined
    ///     string, and a single-item list to the bare scalar (no spurious <c>and</c>).
    /// </summary>
    [Test]
    public async Task FlagWhenList_CollapsesTo_AndJoinedString()
    {
        RulesetDoc doc = Load(ListVsStringYaml);

        await Assert.That(FlagArg(doc, "list_flag")).IsEqualTo("(kills > 0) and (assists > 0)")
            .Because("a list when: is the parenthesized AND-conjunction of its items");
        await Assert.That(FlagArg(doc, "string_flag")).IsEqualTo("(kills > 0) and (assists > 0)")
            .Because("the string twin is authored as exactly that AND-joined form");
        await Assert.That(FlagArg(doc, "single_list_flag")).IsEqualTo("kills > 0")
            .Because("a single-item list collapses to the bare scalar — no spurious parens/and");
        await Assert.That(FlagArg(doc, "single_string_flag")).IsEqualTo("kills > 0");
    }

    /// <summary>
    ///     The mapper collapses a highlight <c>when:</c> list the same way a flag list does.
    /// </summary>
    [Test]
    public async Task HighlightWhenList_CollapsesTo_AndJoinedString()
    {
        RulesetDoc doc = Load(HighlightListYaml);

        HighlightDef list = doc.Highlights.Single(h => h.Id == "list_hl");
        HighlightDef single = doc.Highlights.Single(h => h.Id == "single_hl");

        await Assert.That(list.When).IsEqualTo("(kills > 0) and (kills < 5)")
            .Because("a highlight list when: is the AND-conjunction of its items");
        await Assert.That(single.When).IsEqualTo("kills > 0")
            .Because("a single-item highlight list collapses to the bare scalar");
    }

    /// <summary>
    ///     The load-bearing "pure sugar" proof: a list-form flag and its AND-joined string twin resolve
    ///     to the SAME resolved-identity hash (so the planner dedups them onto one node), and the
    ///     single-item list matches its scalar twin. If the list produced any different AST, these hashes
    ///     would diverge.
    /// </summary>
    [Test]
    public async Task FlagWhenList_HashEquals_StringTwin()
    {
        Dictionary<string, string> hashes = HashAll(ListVsStringYaml);

        await Assert.That(hashes["list_flag"]).IsEqualTo(hashes["string_flag"])
            .Because("when: [a, b] must be byte-identical in identity to when: \"(a) and (b)\" (pure sugar)");
        await Assert.That(hashes["single_list_flag"]).IsEqualTo(hashes["single_string_flag"])
            .Because("a single-item list when: [p] must be byte-identical in identity to when: \"p\"");
    }

    /// <summary>An empty list <c>when: []</c> is a structural error — a <c>when:</c> must constrain something.</summary>
    [Test]
    public async Task EmptyWhenList_IsStructuralError()
    {
        RulesetDocumentLoader.Outcome outcome = RulesetDocumentLoader.Load(EmptyListYaml, "empty.rules.yaml");

        bool hasEmptyWhenDiag = outcome.Diagnostics.Any(d =>
            d.Code == RulesetDiagnosticCodes.WrongShape
            && d.Message.Contains("empty list", StringComparison.OrdinalIgnoreCase));

        await Assert.That(hasEmptyWhenDiag).IsTrue()
            .Because("when: [] must produce a WrongShape diagnostic (a when: must constrain something)");
    }

    // ── Scaffolding ──────────────────────────────────────────────────────────

    private static RulesetDoc Load(string yaml) =>
        RulesetDocumentLoader.Load(yaml, "probe.rules.yaml").Doc
        ?? throw new InvalidOperationException("probe ruleset failed to map");

    private static string? FlagArg(RulesetDoc doc, string statId) =>
        doc.Stats.Single(s => s.Id == statId).KindArg;

    private static CheckedRuleset Compile(string yaml)
    {
        RulesetDoc doc = Load(yaml);
        CatalogScopeAdapter adapter = CatalogScopeAdapter.From(CatalogResource.Load());
        RulesetResolveResult resolved = CheckedRulesetDraft.Load(doc, adapter).Build(64.0, "Cs2GotvProfile");
        return resolved.Ruleset
               ?? throw new InvalidOperationException(
                   "probe ruleset failed to resolve: " + string.Join("; ", resolved.Diagnostics));
    }

    /// <summary>
    ///     Hashes every stat in dependency order (source order here — no cycles), accumulating each
    ///     node's bytes into the shared path → hash map exactly as the planner does, so a flag that
    ///     reads sibling counters can resolve their hashes. Returns statId → lowercase-hex hash.
    /// </summary>
    private static Dictionary<string, string> HashAll(string yaml)
    {
        CheckedRuleset rs = Compile(yaml);
        Dictionary<string, ReadOnlyMemory<byte>> byPath = new(StringComparer.Ordinal);
        MapStatHashSource source = new(byPath);
        Dictionary<string, string> result = new(StringComparer.Ordinal);

        foreach (CheckedStat stat in rs.Stats)
        {
            byte[] hash = V2StatHasher.Hash(stat, source);
            byPath[stat.StatId] = hash;
            byPath[$"{rs.Id.Id}.{stat.StatId}"] = hash;
            result[stat.StatId] = Convert.ToHexStringLower(hash);
        }

        return result;
    }
}
