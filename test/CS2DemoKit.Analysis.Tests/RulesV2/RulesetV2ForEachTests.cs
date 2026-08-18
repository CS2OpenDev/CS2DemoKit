#region

using CS2DemoKit.Analysis.RulesetsV2.Model;
using CS2DemoKit.Analysis.Yaml;

#endregion

namespace CS2DemoKit.Analysis.Tests.RulesV2;

/// <summary>
///     Stage-1 Expand battery: <c>for_each:</c> multiplies the carrying stat/highlight,
///     substituting <c>{key}</c> into ids, labels, expression texts, and title templates (the four
///     enumerated surfaces), clears the axis, preserves order, and takes the Cartesian product over
///     multiple axes. Demo-free.
/// </summary>
[Category("Unit")]
public class RulesetV2ForEachTests
{
    private static RulesetDoc LoadDoc(string yaml)
    {
        RulesetDocumentLoader.Outcome outcome = RulesetDocumentLoader.Load(yaml, null);
        return outcome.Doc ?? throw new InvalidOperationException(
            $"expected a mapped ruleset; diagnostics: {string.Join(" | ", outcome.Diagnostics)}");
    }

    /// <summary><c>for_each: {side:[CT,T]}</c> expands one stat into two, substituting id, label, and expression text.</summary>
    [Test]
    public async Task ForEach_SingleAxis_ExpandsWithSubstitution()
    {
        const string Yaml = """
                            ruleset: side_wins
                            for: match
                            stats:
                              "{side}_wins":
                                count: round_won
                                per: match
                                label: "{side} wins"
                                where: "event.winner_side == {side}"
                                for_each: { side: [CT, T] }
                            """;

        RulesetDoc doc = LoadDoc(Yaml);
        await Assert.That(doc.Stats.Count).IsEqualTo(2);

        StatDef ct = doc.Stats[0];
        await Assert.That(ct.Id).IsEqualTo("CT_wins");
        await Assert.That(ct.Label).IsEqualTo("CT wins");
        await Assert.That(ct.Trigger!.Where).IsEqualTo("event.winner_side == CT");
        await Assert.That(ct.ForEach).IsNull();

        StatDef t = doc.Stats[1];
        await Assert.That(t.Id).IsEqualTo("T_wins");
        await Assert.That(t.Label).IsEqualTo("T wins");
        await Assert.That(t.Trigger!.Where).IsEqualTo("event.winner_side == T");
    }

    /// <summary>A non-<c>for_each</c> stat before the carrier keeps its position; expansion happens in place.</summary>
    [Test]
    public async Task ForEach_PreservesOrder_AroundPlainStats()
    {
        const string Yaml = """
                            ruleset: mixed
                            for: match
                            stats:
                              first_plain:
                                count: kill
                                per: match
                              "{side}_wins":
                                count: round_won
                                per: match
                                for_each: { side: [CT, T] }
                              last_plain:
                                count: death
                                per: match
                            """;

        RulesetDoc doc = LoadDoc(Yaml);
        string[] expected = ["first_plain", "CT_wins", "T_wins", "last_plain"];
        await Assert.That(doc.Stats.Select(s => s.Id)).IsEquivalentTo(expected);
    }

    /// <summary>Two axes take the Cartesian product (last axis fastest), with both keys substituted into the id.</summary>
    [Test]
    public async Task ForEach_TwoAxes_TakesCartesianProduct()
    {
        const string Yaml = """
                            ruleset: side_result
                            for: match
                            stats:
                              "{side}_{result}":
                                count: round_end
                                per: match
                                for_each: { side: [CT, T], result: [win, loss] }
                            """;

        RulesetDoc doc = LoadDoc(Yaml);
        string[] expected = ["CT_win", "CT_loss", "T_win", "T_loss"];
        await Assert.That(doc.Stats.Select(s => s.Id)).IsEquivalentTo(expected);
    }

    /// <summary>A highlight <c>for_each</c> substitutes into the id, the when-expression, and the title template.</summary>
    [Test]
    public async Task ForEach_Highlight_SubstitutesWhenAndTitle()
    {
        const string Yaml = """
                            ruleset: side_aces
                            for: each_player
                            highlights:
                              "{side}_ace":
                                when: "kills_{side} >= 5"
                                per: round
                                title: "{player.name} aced on {side}"
                                for_each: { side: [CT, T] }
                            """;

        RulesetDoc doc = LoadDoc(Yaml);
        await Assert.That(doc.Highlights.Count).IsEqualTo(2);

        HighlightDef ct = doc.Highlights[0];
        await Assert.That(ct.Id).IsEqualTo("CT_ace");
        await Assert.That(ct.When).IsEqualTo("kills_CT >= 5");
        // {side} substituted, but the {player.name} ref hole is left intact.
        await Assert.That(ct.Title).IsEqualTo("{player.name} aced on CT");
        await Assert.That(ct.ForEach).IsNull();
    }
}
