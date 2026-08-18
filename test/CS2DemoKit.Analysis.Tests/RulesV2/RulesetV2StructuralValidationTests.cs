#region

using CS2DemoKit.Analysis.RulesetsV2.Model;
using CS2DemoKit.Analysis.Yaml;

#endregion

namespace CS2DemoKit.Analysis.Tests.RulesV2;

/// <summary>
///     Structural-validation battery: each of the enumerated structural errors fires
///     with the right diagnostic code, the right source position, and a message naming the problem.
///     Covers the map-time checks (bad kind, bad unary test) and the validator checks
///     (keep-not-on-capture, duplicate id post-expansion, malformed title, actor-not-any, param
///     range). Demo-free.
/// </summary>
[Category("Unit")]
public class RulesetV2StructuralValidationTests
{
    private static IReadOnlyList<RulesetDiagnostic> Diagnose(string yaml) =>
        RulesetDocumentLoader.Load(yaml, "test.rules.yaml").Diagnostics;

    /// <summary>Returns the 1-based line of the first line containing <paramref name="marker" />.</summary>
    private static int LineOf(string yaml, string marker)
    {
        string[] lines = yaml.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].Contains(marker, StringComparison.Ordinal))
            {
                return i + 1;
            }
        }

        throw new InvalidOperationException($"marker '{marker}' not found in the test YAML");
    }

    private static RulesetDiagnostic Single(IReadOnlyList<RulesetDiagnostic> diagnostics, string code)
    {
        RulesetDiagnostic[] matching = diagnostics.Where(d => d.Code == code).ToArray();
        return matching.Length == 1
            ? matching[0]
            : throw new InvalidOperationException(
                $"expected exactly one '{code}' diagnostic, got {matching.Length}; all: {string.Join(" | ", diagnostics)}");
    }

    // ── Happy path ─────────────────────────────────────────────────────────────

    /// <summary>A structurally sound ruleset produces no diagnostics.</summary>
    [Test]
    public async Task ValidRuleset_HasNoDiagnostics()
    {
        const string Yaml = """
                            ruleset: ok
                            for: each_player
                            params:
                              n: { type: int, default: 3, min: 2, max: 5 }
                            stats:
                              kills:
                                count: kill
                                per: match
                              first_kill_tick:
                                capture: event.tick
                                on: kill
                                keep: first
                                per: round
                            highlights:
                              ace:
                                when: "kills >= 5"
                                per: round
                                title: "{player.name} aced round {round.number}"
                            """;

        await Assert.That(Diagnose(Yaml)).IsEmpty();
    }

    // ── Bad kind (map-time) ────────────────────────────────────────────────────

    /// <summary>A stat with no kind key is a BadKind error at the stat's position.</summary>
    [Test]
    public async Task Stat_NoKind_IsBadKind()
    {
        const string Yaml = """
                            ruleset: r
                            for: match
                            stats:
                              nokind:
                                per: match
                            """;

        RulesetDiagnostic diag = Single(Diagnose(Yaml), RulesetDiagnosticCodes.BadKind);
        await Assert.That(diag.Message).Contains("no kind");
        await Assert.That(diag.Position.Line).IsEqualTo(LineOf(Yaml, "nokind:"));
        await Assert.That(diag.Position.Column).IsGreaterThan(0);
    }

    /// <summary>A stat declaring two kinds is a BadKind error.</summary>
    [Test]
    public async Task Stat_TwoKinds_IsBadKind()
    {
        const string Yaml = """
                            ruleset: r
                            for: match
                            stats:
                              twokinds:
                                count: kill
                                sum: event.damage
                                per: match
                            """;

        RulesetDiagnostic diag = Single(Diagnose(Yaml), RulesetDiagnosticCodes.BadKind);
        await Assert.That(diag.Message).Contains("multiple kinds");
    }

    // ── keep: only under capture ───────────────────────────────────────────────

    /// <summary><c>keep:</c> on a non-capture stat is a KeepNotOnCapture error at the stat position.</summary>
    [Test]
    public async Task Keep_OnCount_IsError()
    {
        const string Yaml = """
                            ruleset: r
                            for: match
                            stats:
                              badkeep:
                                count: kill
                                keep: list
                                per: match
                            """;

        RulesetDiagnostic diag = Single(Diagnose(Yaml), RulesetDiagnosticCodes.KeepNotOnCapture);
        await Assert.That(diag.Message).Contains("keep:");
        await Assert.That(diag.Position.Line).IsEqualTo(LineOf(Yaml, "badkeep:"));
    }

    // ── Duplicate id, post-expansion ───────────────────────────────────────────

    /// <summary>
    ///     A plain stat and a <c>for_each</c>-expanded stat that collide only after expansion are a
    ///     DuplicateId error — proving the dup-check sees the expanded document.
    /// </summary>
    [Test]
    public async Task DuplicateId_AfterExpansion_IsError()
    {
        const string Yaml = """
                            ruleset: r
                            for: match
                            stats:
                              CT_x:
                                count: kill
                                per: match
                              "{side}_x":
                                count: death
                                per: match
                                for_each: { side: [CT, T] }
                            """;

        RulesetDiagnostic diag = Single(Diagnose(Yaml), RulesetDiagnosticCodes.DuplicateId);
        await Assert.That(diag.Message).Contains("CT_x");
    }

    /// <summary>A stat and a param sharing an id collide in the shared namespace.</summary>
    [Test]
    public async Task DuplicateId_AcrossNamespaces_IsError()
    {
        const string Yaml = """
                            ruleset: r
                            for: match
                            params:
                              shared: { type: int, default: 1 }
                            stats:
                              shared:
                                count: kill
                                per: match
                            """;

        RulesetDiagnostic diag = Single(Diagnose(Yaml), RulesetDiagnosticCodes.DuplicateId);
        await Assert.That(diag.Message).Contains("shared");
        await Assert.That(diag.Message).Contains("param");
    }

    // ── Title template ─────────────────────────────────────────────────────────

    /// <summary>An unclosed <c>{</c> in a title is a BadTitleTemplate error at the highlight position.</summary>
    [Test]
    public async Task Title_UnclosedHole_IsError()
    {
        const string Yaml = """
                            ruleset: r
                            for: each_player
                            highlights:
                              badtitle:
                                when: "kills >= 1"
                                title: "player {player.name has no close"
                            """;

        RulesetDiagnostic diag = Single(Diagnose(Yaml), RulesetDiagnosticCodes.BadTitleTemplate);
        await Assert.That(diag.Position.Line).IsEqualTo(LineOf(Yaml, "badtitle:"));
    }

    /// <summary>An empty <c>{}</c> hole in a title is a BadTitleTemplate error.</summary>
    [Test]
    public async Task Title_EmptyHole_IsError()
    {
        const string Yaml = """
                            ruleset: r
                            for: each_player
                            highlights:
                              emptyhole:
                                when: "kills >= 1"
                                title: "value is {} here"
                            """;

        RulesetDiagnostic diag = Single(Diagnose(Yaml), RulesetDiagnosticCodes.BadTitleTemplate);
        await Assert.That(diag.Message).Contains("emptyhole");
    }

    // ── Bad unary test (map-time) ──────────────────────────────────────────────

    /// <summary>A match value with two comparison operands is a BadUnaryTest error at the value position.</summary>
    [Test]
    public async Task Match_MalformedComparison_IsBadUnaryTest()
    {
        const string Yaml = """
                            ruleset: r
                            for: match
                            stats:
                              s:
                                count: kill
                                per: match
                                match: { damage: ">= 5 5" }
                            """;

        RulesetDiagnostic diag = Single(Diagnose(Yaml), RulesetDiagnosticCodes.BadUnaryTest);
        await Assert.That(diag.Position.Line).IsEqualTo(LineOf(Yaml, "damage:"));
    }

    /// <summary>A bare bracketed value that is not an integer range is a BadUnaryTest error.</summary>
    [Test]
    public async Task Match_BareList_IsBadUnaryTest()
    {
        const string Yaml = """
                            ruleset: r
                            for: match
                            stats:
                              s:
                                count: kill
                                per: match
                                match: { weapon: [ak47, m4a1] }
                            """;

        RulesetDiagnostic diag = Single(Diagnose(Yaml), RulesetDiagnosticCodes.BadUnaryTest);
        await Assert.That(diag.Message).Contains("range");
    }

    // ── actor: only 'any' ──────────────────────────────────────────────────────

    /// <summary>The reserved <c>actor:</c> key carrying anything but <c>any</c> is a BadActor error.</summary>
    [Test]
    public async Task Actor_NonAny_IsError()
    {
        const string Yaml = """
                            ruleset: r
                            for: each_player
                            stats:
                              s:
                                count: kill
                                per: match
                                match: { actor: killer }
                            """;

        RulesetDiagnostic diag = Single(Diagnose(Yaml), RulesetDiagnosticCodes.BadActor);
        await Assert.That(diag.Message).Contains("actor: killer");
    }

    /// <summary><c>actor: any</c> is accepted — no diagnostic.</summary>
    [Test]
    public async Task Actor_Any_IsAccepted()
    {
        const string Yaml = """
                            ruleset: r
                            for: each_player
                            stats:
                              s:
                                count: kill
                                per: match
                                match: { actor: any }
                            """;

        await Assert.That(Diagnose(Yaml).Where(d => d.Code == RulesetDiagnosticCodes.BadActor)).IsEmpty();
    }

    // ── Param range ────────────────────────────────────────────────────────────

    /// <summary>A param default above its max is a ParamRange error at the param position.</summary>
    [Test]
    public async Task Param_DefaultAboveMax_IsError()
    {
        const string Yaml = """
                            ruleset: r
                            for: match
                            params:
                              n: { type: int, default: 9, min: 2, max: 5 }
                            stats:
                              s:
                                count: kill
                                per: match
                            """;

        RulesetDiagnostic diag = Single(Diagnose(Yaml), RulesetDiagnosticCodes.ParamRange);
        await Assert.That(diag.Message).Contains("above its max");
        await Assert.That(diag.Position.Line).IsEqualTo(LineOf(Yaml, "n: { type: int"));
    }

    /// <summary>A param default below its min is a ParamRange error.</summary>
    [Test]
    public async Task Param_DefaultBelowMin_IsError()
    {
        const string Yaml = """
                            ruleset: r
                            for: match
                            params:
                              n: { type: int, default: 1, min: 2, max: 5 }
                            stats:
                              s:
                                count: kill
                                per: match
                            """;

        RulesetDiagnostic diag = Single(Diagnose(Yaml), RulesetDiagnosticCodes.ParamRange);
        await Assert.That(diag.Message).Contains("below its min");
    }

    /// <summary>min/max on a string param is a ParamRange error (bounds are numeric only).</summary>
    [Test]
    public async Task Param_MinMaxOnString_IsError()
    {
        const string Yaml = """
                            ruleset: r
                            for: match
                            params:
                              s: { type: string, default: hi, min: 1, max: 5 }
                            stats:
                              k:
                                count: kill
                                per: match
                            """;

        RulesetDiagnostic diag = Single(Diagnose(Yaml), RulesetDiagnosticCodes.ParamRange);
        await Assert.That(diag.Message).Contains("numeric or duration");
    }
}
