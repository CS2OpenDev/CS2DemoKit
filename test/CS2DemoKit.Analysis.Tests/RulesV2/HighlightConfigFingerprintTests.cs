#region

using CS2DemoKit.Analysis.Building;
using CS2DemoKit.Analysis.Catalog;
using CS2DemoKit.Analysis.Config;
using CS2DemoKit.Analysis.Graphs;
using CS2DemoKit.Analysis.Plugins;
using CS2DemoKit.Analysis.Registry;
using CS2DemoKit.Analysis.RulesetsV2.Compile;
using CS2DemoKit.Analysis.RulesetsV2.Model;
using CS2DemoKit.Analysis.RulesetsV2.Resolve;
using CS2DemoKit.Analysis.Yaml;

#endregion

namespace CS2DemoKit.Analysis.Tests.RulesV2;

/// <summary>
///     A2 (<see cref="HighlightConfigFingerprint" />) battery — demo-free.
///     <list type="bullet">
///         <item>
///             <b>Golden drift guard:</b> over the SHIPPED highlight-bearing rules (kast +
///             post_plant_double), the helper's replayed hashes must be byte-identical to the
///             hashes the builder actually computes during per-player template materialization
///             (seam: <c>RuleChainBuilder.LastMaterializedV2StatHashes</c>). Any divergence means
///             the replay drifted from <c>BuildV2PerPlayerTemplate</c>'s order/descriptors.
///         </item>
///         <item><b>Sensitivity:</b> a <c>when:</c> change changes the fingerprint.</item>
///         <item><b>Insensitivity:</b> a <c>title:</c>-only change does NOT (titles are outside the preimage).</item>
///         <item>
///             <b>Tick-rate dependence:</b> a streak window authored in seconds folds to ticks at
///             composition, so the same document fingerprints differently per tick rate.
///         </item>
///     </list>
/// </summary>
[Category("Unit")]
public class HighlightConfigFingerprintTests
{
    private const string ProfileId = "Cs2GotvProfile";

    // ── Tick-rate dependence ─────────────────────────────────────────────────

    private const string StreakProbeYaml = """
                                           ruleset: fp_streak
                                           for: each_player
                                           stats:
                                             quick:
                                               streak: kill
                                               window: 10s
                                               min_streak: 2
                                               per: match
                                           highlights:
                                             h:
                                               when: quick >= 1
                                               per: round
                                               title: "T"
                                           """;

    // ── Golden drift guard (helper ≡ builder) ────────────────────────────────

    /// <summary>
    ///     The helper's per-highlight hashes equal the builder's actual materialization-time hashes
    ///     for the shipped kast + post_plant_double config (same composition inputs on both sides).
    /// </summary>
    [Test]
    public async Task ShippedRules_HelperHashes_MatchBuilderHashes()
    {
        IReadOnlyList<RulesetDoc> docs = LoadShippedDocs();
        CatalogScopeAdapter adapter = CatalogScopeAdapter.From(CatalogResource.Load());
        RulesetComposition.Result composed = RulesetComposition.Compose(docs, adapter, 64.0, ProfileId);
        await Assert.That(composed.Success).IsTrue()
            .Because("the shipped rules must compose cleanly: "
                     + string.Join("; ", composed.Diagnostics));

        // Helper side: the standalone replay (no graph build).
        HighlightConfigFingerprint.Result helper = HighlightConfigFingerprint.Compute(composed.Rulesets);
        await Assert.That(helper.HighlightHashes.Keys.ToHashSet())
            .IsEquivalentTo(new HashSet<string>
            {
                "kast.kast",
                "post_plant_double.post_plant_double"
            })
            .Because("the shipped config declares exactly these two per-player highlights");

        // Builder side: really build + materialize, then read the drift-guard seam.
        RuleChainBuilder builder = new(
            EventRegistry.Build(),
            entityProviders: EntityValueProviderRegistry.CreateDefault(),
            perPlayerEntityProviders: PerPlayerEntityValueProviderRegistry.CreateDefault());
        BuildResult build = builder.Build(composed.Rulesets);
        foreach (PerPlayerNodeTemplate template in build.Graph.PerPlayerTemplates)
        {
            template.Materialize(0, 0, "golden", null);
        }

        IReadOnlyDictionary<string, ReadOnlyMemory<byte>>? builderHashes = builder.LastMaterializedV2StatHashes;
        await Assert.That(builderHashes).IsNotNull()
            .Because("materializing the v2 per-player template must populate the A2 seam");

        foreach ((string key, string helperHex) in helper.HighlightHashes)
        {
            await Assert.That(builderHashes!.ContainsKey(key)).IsTrue()
                .Because($"the builder must have hashed '{key}' under its qualified spelling");
            string builderHex = Convert.ToHexStringLower(builderHashes[key].Span);
            await Assert.That(helperHex).IsEqualTo(builderHex)
                .Because($"helper and builder hashes for '{key}' must be byte-identical — a "
                         + "mismatch means HighlightConfigFingerprint drifted from the builder's "
                         + "hashing order/descriptors");
        }

        // The four highlight spellings the builder registers must all be present and equal —
        // proving the replay's spelling registration mirrors RuleChainBuilder (lines ~528-531).
        foreach (string bare in new[]
                 {
                     "kast", "post_plant_double"
                 })
        {
            string qualified = $"{bare}.{bare}";
            string hex = Convert.ToHexStringLower(builderHashes![qualified].Span);
            await Assert.That(Convert.ToHexStringLower(builderHashes[bare].Span)).IsEqualTo(hex);
            await Assert.That(Convert.ToHexStringLower(builderHashes[$"{bare}.count"].Span)).IsEqualTo(hex);
            await Assert.That(Convert.ToHexStringLower(builderHashes[$"{qualified}.count"].Span)).IsEqualTo(hex);
        }
    }

    // ── Sensitivity / insensitivity ──────────────────────────────────────────

    private static string ProbeYaml(string when, string title) => $"""
                                                                   ruleset: fp_probe
                                                                   for: each_player
                                                                   stats:
                                                                     kills_r:
                                                                       count: kill
                                                                       per: round
                                                                   highlights:
                                                                     h:
                                                                       when: {when}
                                                                       per: round
                                                                       title: "{title}"
                                                                   """;

    /// <summary>A <c>when:</c> change changes the highlight hash AND the combined fingerprint.</summary>
    [Test]
    public async Task WhenChange_ChangesFingerprint()
    {
        HighlightConfigFingerprint.Result two = Compute(ProbeYaml("kills_r >= 2", "T"), 64.0);
        HighlightConfigFingerprint.Result three = Compute(ProbeYaml("kills_r >= 3", "T"), 64.0);

        await Assert.That(two.HighlightHashes["fp_probe.h"])
            .IsNotEqualTo(three.HighlightHashes["fp_probe.h"])
            .Because("the when: conjunction is in the hash preimage (row 5)");
        await Assert.That(two.Fingerprint).IsNotEqualTo(three.Fingerprint)
            .Because("a changed highlight hash must change the combined fingerprint");
    }

    /// <summary>
    ///     A <c>title:</c>-only change changes NOTHING — titles are deliberately absent from the
    ///     canonical preimage ("positions, display names, and output destinations"), which is what
    ///     lets the cache survive title edits.
    /// </summary>
    [Test]
    public async Task TitleOnlyChange_DoesNotChangeFingerprint()
    {
        HighlightConfigFingerprint.Result a = Compute(ProbeYaml("kills_r >= 2", "Old title"), 64.0);
        HighlightConfigFingerprint.Result b = Compute(ProbeYaml("kills_r >= 2", "Completely new title"), 64.0);

        await Assert.That(a.HighlightHashes["fp_probe.h"]).IsEqualTo(b.HighlightHashes["fp_probe.h"])
            .Because("title: is outside the resolved-identity preimage");
        await Assert.That(a.Fingerprint).IsEqualTo(b.Fingerprint)
            .Because("a title-only edit must not invalidate cached highlights");
    }

    private static string ScoredProbeYaml(string scoreLine, string kindLine) => $"""
                                                                                 ruleset: fp_probe
                                                                                 for: each_player
                                                                                 stats:
                                                                                   kills_r:
                                                                                     count: kill
                                                                                     per: round
                                                                                 highlights:
                                                                                   h:
                                                                                     when: kills_r >= 2
                                                                                     per: round
                                                                                     {scoreLine}
                                                                                     {kindLine}
                                                                                     title: "T"
                                                                                 """;

    /// <summary>
    ///     Authored <c>score:</c>/<c>kind:</c> change the combined config
    ///     fingerprint (so a ranking/track edit invalidates the cached scan), but they are kept OUT of
    ///     the per-highlight node hash — like <c>title:</c>, they are not resolved-node identity, which
    ///     keeps the builder drift-guard (<see cref="ShippedRules_HelperHashes_MatchBuilderHashes" />)
    ///     valid. This pins Option B.
    /// </summary>
    [Test]
    public async Task ScoreAndKind_ChangeFingerprint_ButNotNodeHash()
    {
        HighlightConfigFingerprint.Result baseline = Compute(ScoredProbeYaml("score: 50", "kind: highlight"), 64.0);
        HighlightConfigFingerprint.Result rescored = Compute(ScoredProbeYaml("score: 90", "kind: highlight"), 64.0);
        HighlightConfigFingerprint.Result rekinded = Compute(ScoredProbeYaml("score: 50", "kind: lowlight"), 64.0);

        // Node hash is identity-only: score/kind are outside the preimage, so it is unchanged.
        await Assert.That(rescored.HighlightHashes["fp_probe.h"]).IsEqualTo(baseline.HighlightHashes["fp_probe.h"])
            .Because("score: is outside the resolved-identity preimage (Option B keeps the drift-guard valid)");
        await Assert.That(rekinded.HighlightHashes["fp_probe.h"]).IsEqualTo(baseline.HighlightHashes["fp_probe.h"])
            .Because("kind: is outside the resolved-identity preimage");

        // Combined fingerprint IS score/kind-sensitive, so an edit invalidates the cached scan.
        await Assert.That(rescored.Fingerprint).IsNotEqualTo(baseline.Fingerprint)
            .Because("a score: edit must invalidate the cache (the reel's ranking would otherwise go stale)");
        await Assert.That(rekinded.Fingerprint).IsNotEqualTo(baseline.Fingerprint)
            .Because("a kind: edit must invalidate the cache (the firing's track would otherwise go stale)");
    }

    /// <summary>
    ///     The same document fingerprints differently per tick rate: the streak's <c>window: 10s</c>
    ///     folds to 640 vs 1280 ticks at composition, the referenced stat's hash embeds in the
    ///     highlight's when-reference row — so there is no global fingerprint; compute per
    ///     (tickRate, profile).
    /// </summary>
    [Test]
    public async Task TickRateChange_ChangesFingerprint_WhenDurationsFoldDifferently()
    {
        HighlightConfigFingerprint.Result at64 = Compute(StreakProbeYaml, 64.0);
        HighlightConfigFingerprint.Result at128 = Compute(StreakProbeYaml, 128.0);

        await Assert.That(at64.HighlightHashes["fp_streak.h"])
            .IsNotEqualTo(at128.HighlightHashes["fp_streak.h"])
            .Because("the streak window hashes in ticks (row 8), and the highlight's when: embeds "
                     + "the referenced stat's hash (row 6) — tick rate is identity-bearing");
        await Assert.That(at64.Fingerprint).IsNotEqualTo(at128.Fingerprint);
    }

    // ── Loud rejection parity ────────────────────────────────────────────────

    /// <summary>
    ///     A <c>for: match</c> ruleset declaring highlights is rejected loudly — mirroring the
    ///     builder's build-time throw, so the fingerprint helper can never stamp a config the
    ///     builder would refuse to build.
    /// </summary>
    [Test]
    public async Task MatchScopedHighlights_ThrowLikeTheBuilder()
    {
        const string Yaml = """
                            ruleset: fp_match
                            for: match
                            stats:
                              total_kills:
                                count: kill
                                per: match
                            highlights:
                              h:
                                when: total_kills >= 1
                                per: round
                                title: "T"
                            """;

        IReadOnlyList<CheckedRuleset> rulesets = ComposeProbe(Yaml, 64.0);
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => HighlightConfigFingerprint.Compute(rulesets));
        await Assert.That(ex.Message).Contains("highlights")
            .Because("game-scoped highlight lowering is not wired — the builder throws, so must the replay");
    }

    // ── Scaffolding ──────────────────────────────────────────────────────────

    private static HighlightConfigFingerprint.Result Compute(string yaml, double tickRate) =>
        HighlightConfigFingerprint.Compute([LoadDoc(yaml)], tickRate, ProfileId);

    private static IReadOnlyList<CheckedRuleset> ComposeProbe(string yaml, double tickRate)
    {
        CatalogScopeAdapter adapter = CatalogScopeAdapter.From(CatalogResource.Load());
        RulesetComposition.Result composed =
            RulesetComposition.Compose([LoadDoc(yaml)], adapter, tickRate, ProfileId);
        return composed.Success
            ? composed.Rulesets
            : throw new InvalidOperationException(
                "probe failed composition: " + string.Join("; ", composed.Diagnostics));
    }

    private static RulesetDoc LoadDoc(string yaml) =>
        RulesetDocumentLoader.Load(yaml, "probe.rules.yaml").Doc
        ?? throw new InvalidOperationException("probe ruleset failed to map");

    private static IReadOnlyList<RulesetDoc> LoadShippedDocs()
    {
        string rulesDir = Path.Combine(FindRepoRoot(), "src", "CS2DemoKit.Analysis", "Rules");
        return
        [
            LoadFile(Path.Combine(rulesDir, "kast.rules.yaml")),
            LoadFile(Path.Combine(rulesDir, "post_plant_double.rules.yaml"))
        ];

        static RulesetDoc LoadFile(string path)
        {
            return RulesetDocumentLoader.Load(File.ReadAllText(path), Path.GetFileName(path)).Doc
                   ?? throw new InvalidOperationException($"shipped ruleset failed to load: {path}");
        }
    }

    private static string FindRepoRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "CS2DemoKit.slnx")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("repo root not found");
    }
}
