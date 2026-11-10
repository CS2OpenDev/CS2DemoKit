#region

using System.Security.Cryptography;
using System.Text;
using CS2DemoKit.Analysis.Catalog;
using CS2DemoKit.Analysis.Rules;
using CS2DemoKit.Analysis.Rules.Hashing;
using CS2DemoKit.Analysis.RulesetsV2.Model;
using CS2DemoKit.Analysis.RulesetsV2.Resolve;

#endregion

namespace CS2DemoKit.Analysis.RulesetsV2.Compile;

/// <summary>
///     Highlights-pipeline work item A2: replays the planner's canonical resolved-identity hashing
///     over a composed config WITHOUT building any graph nodes, producing the per-highlight hash
///     map and the combined config fingerprint that stamp the highlights cache (staleness
///     detection). The replay mirrors <c>RuleChainBuilder.BuildV2PerPlayerTemplate</c>'s exact
///     order — per ruleset (in composition/dependency order, one shared hash space across all
///     per-player rulesets): non-compute/non-rate stats → rate stats → per-highlight Flag-descriptor
///     hashes → deferred computes LAST. The order is load-bearing: a compute may read a highlight's
///     <c>&lt;id&gt;.count</c>, and <see cref="MapStatHashSource" /> throws on any reference to a
///     not-yet-hashed node — hashing computes before highlights would reject valid configs.
///     Highlights register FOUR spellings (bare id, <c>&lt;id&gt;.count</c>,
///     <c>{ruleset}.&lt;id&gt;</c>, <c>{ruleset}.&lt;id&gt;.count</c>) exactly as the builder does;
///     stats register two (bare + qualified).
///     <para>
///         The hashes are (tickRate, profile)-dependent — composition folds durations to ticks
///         (e.g. streak windows), so there is no global fingerprint: compute one per composed
///         (tickRate, profile) input. Titles are deliberately ABSENT from the preimage
///         (<see cref="RuleHasher" />), so <c>title:</c> edits never change the fingerprint.
///         Drift between this replay and the builder's actual hashing is guarded by a golden test
///         against <c>RuleChainBuilder.LastMaterializedV2StatHashes</c>.
///     </para>
/// </summary>
public static class HighlightConfigFingerprint
{
    /// <summary>
    ///     Replays the builder's hashing over an already-composed ruleset set (the
    ///     <c>RulesetComposition.Compose(...).Rulesets</c> for one (tickRate, profile) pair —
    ///     pass them in composition order; it is the builder's iteration order).
    /// </summary>
    /// <param name="rulesets">The checked rulesets, in composition (dependency) order.</param>
    /// <returns>The per-highlight hash map and combined fingerprint.</returns>
    /// <exception cref="InvalidOperationException">
    ///     A <c>for: match</c> ruleset declares highlights (the builder rejects the same config at
    ///     build time — game-scoped highlight lowering is not wired), or a stat/highlight references
    ///     a node outside the per-player hash space (the same config would fail the builder's
    ///     dependency-ordered hashing invariant).
    /// </exception>
    public static Result Compute(IReadOnlyList<CheckedRuleset> rulesets) => Compute(rulesets, null);

    /// <summary>
    ///     <see cref="Compute(IReadOnlyList{CheckedRuleset})" />, with each catalog extension row a node reads
    ///     folded into that node's hash, keyed by provider name.
    /// </summary>
    private static Result Compute(IReadOnlyList<CheckedRuleset> rulesets,
        IReadOnlyDictionary<string, string>? extensionRows)
    {
        ArgumentNullException.ThrowIfNull(rulesets);

        // One shared hash space across every per-player ruleset — the mirror of the single
        // statHashesByPath a per-player template materialization accumulates. Game-scoped rulesets
        // hash into a SEPARATE space in the builder and cannot declare highlights, so they
        // contribute nothing here (a valid config never cross-references their hashes from the
        // per-player template's space — the builder would throw the same way this replay would).
        Dictionary<string, ReadOnlyMemory<byte>> statHashesByPath = new(StringComparer.Ordinal);
        MapStatHashSource hashSource = new(statHashesByPath);
        Dictionary<string, string> highlightHashes = new(StringComparer.Ordinal);

        // Authored score:/kind: are deliberately kept OUT of the per-highlight node hash (like
        // title:, they are not part of resolved node identity, so the RuleHasher drift-guard stays
        // valid). They ARE mixed into the combined config fingerprint below, so a score/kind edit
        // still invalidates the cached scan (the reel's ranking/track would otherwise go stale).
        List<string> configExtras = [];

        foreach (CheckedRuleset rs in rulesets)
        {
            if (rs.For != RulesetScope.EachPlayer)
            {
                if (rs.Highlights.Count > 0)
                {
                    // Mirror the builder's loud rejection (BuildV2GameScope) rather than silently
                    // fingerprinting a config that cannot build.
                    throw new InvalidOperationException(
                        $"ruleset '{rs.Id.Id}' is for: match but declares highlights — game-scoped highlight "
                        + "lowering (match-level timeline attribution) is not yet wired.");
                }

                continue;
            }

            // Builder order 1: every non-compute, non-rate stat (document order).
            foreach (CheckedStat stat in rs.Stats)
            {
                if (stat.Kind is RuleNodeKind.Compute or RuleNodeKind.Rate)
                {
                    continue;
                }

                HashStat(rs, stat, statHashesByPath, hashSource, extensionRows);
            }

            // Builder order 2: rate stats (deferred so both of:/per: bucket hashes resolve).
            foreach (CheckedStat stat in rs.Stats)
            {
                if (stat.Kind != RuleNodeKind.Rate)
                {
                    continue;
                }

                HashStat(rs, stat, statHashesByPath, hashSource, extensionRows);
            }

            // Builder order 3: highlights — hashed as a Flag over the when-conjunction + scope
            // (the exact descriptor BuildV2PerPlayerTemplate constructs), registered under all
            // four spellings so a deferred compute's `<id>.count` read resolves.
            foreach (CheckedHighlight highlight in rs.Highlights)
            {
                RuleNodeDescriptor descriptor = new(
                    highlight.HighlightId, RuleNodeKind.Flag, RulesType.Bool, highlight.Scope,
                    [], highlight.When);
                ReadOnlyMemory<byte> hash = FoldExtensionRows(RuleHasher.ComputeHash(descriptor, hashSource),
                    highlight.EntityReads, extensionRows);
                statHashesByPath[highlight.HighlightId] = hash;
                statHashesByPath[highlight.CountNodeId] = hash;
                statHashesByPath[$"{rs.Id.Id}.{highlight.HighlightId}"] = hash;
                statHashesByPath[$"{rs.Id.Id}.{highlight.CountNodeId}"] = hash;

                highlightHashes[$"{rs.Id.Id}.{highlight.HighlightId}"] = Convert.ToHexStringLower(hash.Span);
                configExtras.Add(
                    $"{rs.Id.Id}.{highlight.HighlightId}#score={highlight.Score}#kind={highlight.Kind}#group={highlight.Group}");
            }

            // Builder order 4: deferred computes LAST (they may read sibling stats, contexts, or a
            // highlight's `<id>.count` — all hashed above by now).
            foreach (CheckedStat stat in rs.Stats)
            {
                if (stat.Kind != RuleNodeKind.Compute)
                {
                    continue;
                }

                HashStat(rs, stat, statHashesByPath, hashSource, extensionRows);
            }
        }

        return new Result(highlightHashes, CombineFingerprint(highlightHashes, configExtras));
    }

    /// <summary>
    ///     Convenience overload for the cache's cheap re-fingerprint path (YAML load → Compose →
    ///     hash; no demo parse, no graph build): composes <paramref name="docs" /> for the given
    ///     (tickRate, profile) and computes the fingerprint.
    /// </summary>
    /// <param name="docs">The loaded ruleset documents (e.g. <c>RuleConfigLoadResult.Rulesets</c>).</param>
    /// <param name="ticksPerSecond">The demo's tick rate (<c>ParsedDemo.TickRate</c>).</param>
    /// <param name="profileId">The active demo-source profile id (e.g. <c>Cs2GotvProfile</c>).</param>
    /// <returns>The per-highlight hash map and combined fingerprint.</returns>
    /// <exception cref="InvalidOperationException">The documents fail composition (diagnostics listed).</exception>
    public static Result Compute(IReadOnlyList<RulesetDoc> docs, double ticksPerSecond, string profileId) =>
        Compute(docs, ticksPerSecond, profileId, null);

    /// <summary>
    ///     <see cref="Compute(IReadOnlyList{RulesetDoc}, double, string)" /> against the embedded catalog plus
    ///     <paramref name="catalogExtension" />'s rows, the catalog a run with the same
    ///     <see cref="AnalysisOptions.CatalogExtension" /> composes against. Each extension row a stat or
    ///     highlight reads is folded into that node's hash, so a highlight that reads one no longer matches
    ///     the builder's own node hash: that is the point, since the builder's hash cannot see which
    ///     provider a path lowers to. A highlight that reads none keeps the builder's hash.
    /// </summary>
    /// <param name="docs">The loaded ruleset documents.</param>
    /// <param name="ticksPerSecond">The demo's tick rate.</param>
    /// <param name="profileId">The active demo-source profile id.</param>
    /// <param name="catalogExtension">Extra provider rows, or null.</param>
    /// <returns>The per-highlight hash map and combined fingerprint.</returns>
    /// <exception cref="InvalidOperationException">The documents fail composition (diagnostics listed).</exception>
    public static Result Compute(IReadOnlyList<RulesetDoc> docs, double ticksPerSecond, string profileId,
        CatalogExtension? catalogExtension)
    {
        ArgumentNullException.ThrowIfNull(docs);
        CatalogScopeAdapter adapter = CatalogScopeAdapter.From(CatalogResource.Load(catalogExtension));
        RulesetComposition.Result composed =
            RulesetComposition.Compose(docs, adapter, ticksPerSecond, profileId);
        if (!composed.Success)
        {
            throw new InvalidOperationException(
                "highlight fingerprint: the config failed composition — "
                + string.Join("; ", composed.Diagnostics));
        }

        // The checked rules hash a read by its rules path, not by the provider the path lowers to, so
        // remapping an extension row to another provider would leave every hash unchanged. Each row a
        // node reads is folded into that node's hash; a stat reference carries it on into every
        // highlight that depends on the stat, and from there into the combined fingerprint. A node that
        // reads no extension row, and every node of a run with no extension, keeps its hash.
        Dictionary<string, string>? rows = catalogExtension is { Providers.Count: > 0 }
            ? catalogExtension.Providers.ToDictionary(
                p => p.Name, p => $"ext#{p.Name}#{p.Scope}#{p.ClrType}#{p.V2Name}", StringComparer.OrdinalIgnoreCase)
            : null;
        return Compute(composed.Rulesets, rows);
    }

    /// <summary>
    ///     <paramref name="hash" /> with the extension rows among <paramref name="reads" /> folded in, or
    ///     <paramref name="hash" /> itself when the node reads none.
    /// </summary>
    private static ReadOnlyMemory<byte> FoldExtensionRows(ReadOnlyMemory<byte> hash,
        IReadOnlyList<EntityProviderReference> reads, IReadOnlyDictionary<string, string>? extensionRows)
    {
        if (extensionRows is null)
        {
            return hash;
        }

        List<string> read = reads
            .Select(r => extensionRows.GetValueOrDefault(r.ProviderName))
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();
        if (read.Count == 0)
        {
            return hash;
        }

        byte[] preimage = [.. hash.Span, .. Encoding.UTF8.GetBytes(string.Join('\n', read))];
        return SHA256.HashData(preimage);
    }

    /// <summary>Hashes one stat and registers its bare + qualified spellings (the builder's two).</summary>
    private static void HashStat(CheckedRuleset rs, CheckedStat stat,
        Dictionary<string, ReadOnlyMemory<byte>> statHashesByPath, MapStatHashSource hashSource,
        IReadOnlyDictionary<string, string>? extensionRows)
    {
        ReadOnlyMemory<byte> hash = FoldExtensionRows(V2StatHasher.Hash(stat, hashSource), stat.EntityReads,
            extensionRows);
        statHashesByPath[stat.StatId] = hash;
        statHashesByPath[$"{rs.Id.Id}.{stat.StatId}"] = hash;
    }

    /// <summary>
    ///     SHA-256 hex over the sorted <c>"{key}={hex}"</c> per-highlight hash lines plus the sorted
    ///     authored-config lines (<c>score:</c>/<c>kind:</c>), all LF-joined, UTF-8. The two line sets
    ///     are concatenated (hashes first, then extras) so an edit to either invalidates the cache.
    /// </summary>
    private static string CombineFingerprint(Dictionary<string, string> highlightHashes,
        IReadOnlyList<string> configExtras)
    {
        IEnumerable<string> hashLines = highlightHashes
            .Select(kv => $"{kv.Key}={kv.Value}")
            .OrderBy(line => line, StringComparer.Ordinal);
        IEnumerable<string> extraLines = configExtras.OrderBy(line => line, StringComparer.Ordinal);
        string preimage = string.Join('\n', hashLines.Concat(extraLines));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(preimage)));
    }

    /// <summary>The fingerprint product for one composed (tickRate, profile) config.</summary>
    /// <param name="HighlightHashes">
    ///     Per-highlight resolved-identity hashes, keyed <c>{rulesetId}.{highlightId}</c>, values
    ///     64-char lowercase hex. Enables partial-staleness display ("2 of 3 highlight types stale").
    /// </param>
    /// <param name="Fingerprint">
    ///     The combined config fingerprint: lowercase-hex SHA-256 over the ordinally-sorted
    ///     <c>"{key}={hex}"</c> lines of <paramref name="HighlightHashes" /> joined with <c>\n</c>.
    /// </param>
    public sealed record Result(IReadOnlyDictionary<string, string> HighlightHashes, string Fingerprint);
}
