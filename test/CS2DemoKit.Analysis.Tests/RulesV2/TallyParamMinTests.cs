#region

using CS2DemoKit.Analysis.Catalog;
using CS2DemoKit.Analysis.RulesetsV2.Compile;
using CS2DemoKit.Analysis.RulesetsV2.Resolve;
using CS2DemoKit.Analysis.Yaml;

#endregion

namespace CS2DemoKit.Analysis.Tests.RulesV2;

/// <summary>
///     The <c>tally:</c> param-valued-threshold battery: a threshold's <c>min:</c> may be a
///     <c>params.&lt;name&gt;</c> reference (not only an int literal). The reference binds to its literal
///     int value <b>before</b> the <c>(min, target)</c> pair is built (pre-hash), so a
///     <c>min: params.x</c> tally with <c>x = 3</c> resolves and hashes <b>identically</b> to a literal
///     <c>min: 3</c> tally (spec §6 row 8). An undeclared param, a non-<c>int</c> param, or a malformed
///     reference is an attributed resolver error. Runtime bucket parity lives in
///     the pilot goldens; this battery is demo-free.
/// </summary>
[Category("Unit")]
public class TallyParamMinTests
{
    // A tally over round_kills bumping a 3K bucket at the literal min 3.
    private const string LiteralMin = """
                                      ruleset: t
                                      for: each_player
                                      stats:
                                        round_kills:
                                          count: kill
                                          per: round
                                        multi:
                                          tally: round_kills
                                          thresholds:
                                            - { min: 3, target: rounds_3k }
                                          per: match
                                      """;

    // The same tally, but the min is a params.<name> reference. The param's *default* is deliberately
    // not 3 — the identity test binds it to 3 via paramValues, proving the bound value (not the
    // default) folds into the threshold.
    private const string ParamMin = """
                                    ruleset: t
                                    for: each_player
                                    params:
                                      multi_threshold:
                                        type: int
                                        default: 99
                                    stats:
                                      round_kills:
                                        count: kill
                                        per: round
                                      multi:
                                        tally: round_kills
                                        thresholds:
                                          - { min: params.multi_threshold, target: rounds_3k }
                                        per: match
                                    """;

    private static readonly CatalogScopeAdapter _adapter = CatalogScopeAdapter.From(CatalogResource.Load());

    /// <summary>
    ///     A <c>min: params.x</c> tally with <c>x</c> bound to 3 hashes identically to a literal
    ///     <c>min: 3</c> tally — the param folds to its int before the pair is hashed (dedup-stable).
    /// </summary>
    [Test]
    public async Task ParamMin_BoundToThree_HashesLikeLiteralThree()
    {
        Dictionary<string, object?> bind = new(StringComparer.Ordinal)
        {
            ["multi_threshold"] = 3
        };

        byte[] literalHash = TallyHash(LiteralMin, null);
        byte[] paramHash = TallyHash(ParamMin, bind);

        await Assert.That(Convert.ToHexString(paramHash)).IsEqualTo(Convert.ToHexString(literalHash))
            .Because("min: params.x (x=3) must fold to the literal 3 pre-hash and dedup with min: 3");
    }

    /// <summary>
    ///     Binding the same param to a different value changes the folded min — so it no longer
    ///     matches the literal-3 tally. Proves the bound value is what folds (not a constant).
    /// </summary>
    [Test]
    public async Task ParamMin_BoundToFour_DoesNotMatchLiteralThree()
    {
        Dictionary<string, object?> bind = new(StringComparer.Ordinal)
        {
            ["multi_threshold"] = 4
        };

        byte[] literalHash = TallyHash(LiteralMin, null);
        byte[] paramHash = TallyHash(ParamMin, bind);

        await Assert.That(Convert.ToHexString(paramHash)).IsNotEqualTo(Convert.ToHexString(literalHash))
            .Because("min: params.x (x=4) folds to 4 and must NOT dedup with the literal min: 3");
    }

    /// <summary>An undeclared param min is an attributed resolver error (spec §6 row 8).</summary>
    [Test]
    public async Task UnboundParamMin_IsAttributedError()
    {
        const string Yaml = """
                            ruleset: t
                            for: each_player
                            stats:
                              round_kills:
                                count: kill
                                per: round
                              multi:
                                tally: round_kills
                                thresholds:
                                  - { min: params.nope, target: rounds_3k }
                                per: match
                            """;

        RulesetResolveResult resolved = Resolve(Yaml);
        await Assert.That(resolved.Diagnostics.Any(d =>
                d.Code == ResolveDiagnosticCodes.BadSlotType
                && d.Message.Contains("undeclared param", StringComparison.Ordinal)))
            .IsTrue()
            .Because("a tally min referencing an undeclared param must attribute a resolver error");
    }

    /// <summary>A non-int param min (string here) is an attributed type error (spec §6 row 8).</summary>
    [Test]
    public async Task NonIntParamMin_IsAttributedTypeError()
    {
        const string Yaml = """
                            ruleset: t
                            for: each_player
                            params:
                              label:
                                type: string
                                default: hi
                            stats:
                              round_kills:
                                count: kill
                                per: round
                              multi:
                                tally: round_kills
                                thresholds:
                                  - { min: params.label, target: rounds_3k }
                                per: match
                            """;

        RulesetResolveResult resolved = Resolve(Yaml);
        await Assert.That(resolved.Diagnostics.Any(d =>
                d.Code == ResolveDiagnosticCodes.BadSlotType
                && d.Message.Contains("must be int", StringComparison.Ordinal)))
            .IsTrue()
            .Because("a tally min bound to a string param must attribute a type error");
    }

    /// <summary>
    ///     A duration param min is rejected too — it folds to an int tick literal, but the declared
    ///     type is not <c>int</c>, so it is a type error (guards against literal-node-kind leniency).
    /// </summary>
    [Test]
    public async Task DurationParamMin_IsAttributedTypeError()
    {
        const string Yaml = """
                            ruleset: t
                            for: each_player
                            params:
                              gap:
                                type: duration
                                default: 5s
                            stats:
                              round_kills:
                                count: kill
                                per: round
                              multi:
                                tally: round_kills
                                thresholds:
                                  - { min: params.gap, target: rounds_3k }
                                per: match
                            """;

        RulesetResolveResult resolved = Resolve(Yaml);
        await Assert.That(resolved.Diagnostics.Any(d =>
                d.Code == ResolveDiagnosticCodes.BadSlotType
                && d.Message.Contains("must be int, not duration", StringComparison.Ordinal)))
            .IsTrue()
            .Because("a duration param min must be rejected even though it folds to an int tick literal");
    }

    private static RulesetResolveResult Resolve(string yaml)
    {
        RulesetDocumentLoader.Outcome outcome = RulesetDocumentLoader.Load(yaml, "t.rules.yaml");
        return CheckedRulesetDraft.Load(outcome.Doc!, _adapter).Build(64.0, "Cs2GotvProfile");
    }

    /// <summary>
    ///     Resolves the ruleset (with the given param bindings) and returns the resolved-identity
    ///     hash of its <c>multi</c> tally, replicating the planner's dependency-ordered hashing.
    /// </summary>
    private static byte[] TallyHash(string yaml, IReadOnlyDictionary<string, object?>? paramValues)
    {
        RulesetDocumentLoader.Outcome outcome = RulesetDocumentLoader.Load(yaml, "t.rules.yaml");
        RulesetResolveResult resolved =
            CheckedRulesetDraft.Load(outcome.Doc!, _adapter).Build(64.0, "Cs2GotvProfile", paramValues);
        CheckedRuleset ruleset = resolved.Ruleset
                                 ?? throw new InvalidOperationException(
                                     "tally ruleset failed to resolve: " + string.Join("; ", resolved.Diagnostics));

        Dictionary<string, ReadOnlyMemory<byte>> byPath = new(StringComparer.Ordinal);
        MapStatHashSource source = new(byPath);
        byte[]? tallyHash = null;
        foreach (CheckedStat stat in ruleset.Stats)
        {
            byte[] hash = V2StatHasher.Hash(stat, source);
            byPath[stat.StatId] = hash;
            byPath[$"{ruleset.Id.Id}.{stat.StatId}"] = hash;
            if (stat.StatId == "multi")
            {
                tallyHash = hash;
            }
        }

        return tallyHash ?? throw new InvalidOperationException("tally stat 'multi' not resolved");
    }
}
