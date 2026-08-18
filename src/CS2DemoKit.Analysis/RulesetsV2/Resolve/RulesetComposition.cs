#region

using CS2DemoKit.Analysis.Rules;
using CS2DemoKit.Analysis.RulesetsV2.Model;

#endregion

namespace CS2DemoKit.Analysis.RulesetsV2.Resolve;

/// <summary>
///     The directory-level composition orchestrator: the step that turns a
///     set of parsed <see cref="RulesetDoc" />s into the planner's ordered
///     <see cref="CheckedRuleset" /> list, wiring the cross-ruleset export graph a qualified
///     <c>ruleset.stat</c> read resolves against.
///     <list type="number">
///         <item>
///             <b>Export graph.</b> Each ruleset's scope + declared-id set + exported-id set come
///             straight from the document; the exported stats' value <em>types</em> come from a
///             fix-point resolve (a ruleset that only reads its dependencies' exports types after those
///             dependencies do), so a used ruleset's exports are in scope by the time a reader resolves.
///         </item>
///         <item>
///             <b>Attributed validation.</b> <see cref="CrossRulesetReferenceValidator" /> classifies
///             every qualified read up front (the four errors + read-scope); a document with an
///             attributed error short-circuits its full resolve, so the checker's noisier generic
///             out-of-scope diagnostics never pile on.
///         </item>
///         <item>
///             <b>Final resolve + cycles.</b> Clean documents resolve against the stable graph (with
///             the within-ruleset cycle pre-pass), then <see cref="CrossRulesetCycleDetector" /> rejects
///             any cycle that spans rulesets. The surviving rulesets return in dependency order
///             (used-before-user) so the planner hashes a referenced node before its reader.
///         </item>
///     </list>
/// </summary>
public static class RulesetComposition
{
    /// <summary>Composes a directory of documents for a specific demo (build context).</summary>
    /// <param name="docs">The expanded, structurally-valid documents.</param>
    /// <param name="adapter">The Catalog scope-environment adapter.</param>
    /// <param name="ticksPerSecond">The demo's <c>ParsedDemo.TickRate</c>.</param>
    /// <param name="profileId">The active demo-source profile id (e.g. <c>Cs2GotvProfile</c>).</param>
    /// <param name="paramValues">The install's param values, or null for declared defaults.</param>
    /// <returns>The composition result.</returns>
    public static Result Compose(IReadOnlyList<RulesetDoc> docs, CatalogScopeAdapter adapter,
        double ticksPerSecond, string profileId, IReadOnlyDictionary<string, object?>? paramValues = null) =>
        Compose(docs, adapter, ResolveContext.Build(ticksPerSecond, profileId, paramValues));

    /// <summary>Composes a directory of documents demo-less (draft context) — for the checker / <c>rules check</c>.</summary>
    /// <param name="docs">The expanded, structurally-valid documents.</param>
    /// <param name="adapter">The Catalog scope-environment adapter.</param>
    /// <returns>The composition result.</returns>
    public static Result ComposeDraft(IReadOnlyList<RulesetDoc> docs, CatalogScopeAdapter adapter) =>
        Compose(docs, adapter, ResolveContext.Draft);

    /// <summary>Composes a directory of documents under an explicit context.</summary>
    /// <param name="docs">The expanded, structurally-valid documents.</param>
    /// <param name="adapter">The Catalog scope-environment adapter.</param>
    /// <param name="context">The load-vs-build context.</param>
    /// <returns>The composition result.</returns>
    public static Result Compose(IReadOnlyList<RulesetDoc> docs, CatalogScopeAdapter adapter, ResolveContext context)
    {
        ArgumentNullException.ThrowIfNull(docs);
        ArgumentNullException.ThrowIfNull(adapter);
        ArgumentNullException.ThrowIfNull(context);

        RulesetExportGraph graph = BuildExportGraph(docs, adapter, context);

        List<RulesetDiagnostic> diagnostics = new();
        // Attributed mirror of `diagnostics`, appended at exactly the same sites in the same order, so
        // the two stay 1:1 (a consumer that counts one and prints the other cannot drift).
        List<RulesetCompositionDiagnostic> attributed = new();
        List<ExcludedRuleset> excluded = new();
        Dictionary<string, CheckedRuleset> checkedByDoc = new(StringComparer.Ordinal);

        // Cross-ruleset cycles are detected structurally (a mutual read prevents type-resolution, so it
        // can only be caught before resolve). Named up front; the per-doc resolves below may add their
        // own unresolved-reference noise, but the cycle is the authoritative diagnostic.
        IReadOnlyList<RulesetDiagnostic> crossCycles = CrossRulesetCycleDetector.Detect(docs);
        diagnostics.AddRange(crossCycles);
        foreach (RulesetDiagnostic cycle in crossCycles)
        {
            // A cycle spans rulesets by construction, so it belongs to no single one; its message
            // names every participating qualified id.
            attributed.Add(RulesetCompositionDiagnostic.From(cycle, null));
        }

        foreach (RulesetDoc doc in docs)
        {
            IReadOnlyList<RulesetDiagnostic> crossErrors = CrossRulesetReferenceValidator.Validate(doc, graph);
            if (crossErrors.Count > 0)
            {
                diagnostics.AddRange(crossErrors);
                excluded.Add(Exclude(doc, crossErrors, attributed));
                continue; // short-circuit: the attributed error is the report, skip the noisier full resolve
            }

            RulesetResolveResult result = RulesetResolver.Resolve(doc, adapter, context, graph);
            if (result.Ruleset is not { } ruleset)
            {
                diagnostics.AddRange(result.Diagnostics);
                excluded.Add(Exclude(doc, result.Diagnostics, attributed));
                continue;
            }

            IReadOnlyList<RulesetDiagnostic> cycles = StatReferenceCycleDetector.Detect(ruleset);
            if (cycles.Count > 0)
            {
                diagnostics.AddRange(cycles);
                excluded.Add(Exclude(doc, cycles, attributed));
                continue;
            }

            checkedByDoc[doc.Id] = ruleset;
        }

        IReadOnlyList<CheckedRuleset> ordered = OrderByDependency(docs, checkedByDoc);
        return new Result(ordered, diagnostics)
        {
            AttributedDiagnostics = attributed,
            Excluded = excluded
        };
    }

    /// <summary>
    ///     Records one document's exclusion: attributes its diagnostics to it (appending them to the
    ///     shared attributed list, keeping that list 1:1 with the raw one) and returns the exclusion.
    /// </summary>
    /// <param name="doc">The document being dropped.</param>
    /// <param name="diagnostics">The diagnostics that caused the drop, in production order.</param>
    /// <param name="attributed">The composition-wide attributed list to append to.</param>
    /// <returns>The exclusion record for <paramref name="doc" />.</returns>
    private static ExcludedRuleset Exclude(RulesetDoc doc, IReadOnlyList<RulesetDiagnostic> diagnostics,
        List<RulesetCompositionDiagnostic> attributed)
    {
        List<RulesetCompositionDiagnostic> own = new(diagnostics.Count);
        foreach (RulesetDiagnostic diagnostic in diagnostics)
        {
            own.Add(RulesetCompositionDiagnostic.From(diagnostic, doc.Id));
        }

        attributed.AddRange(own);
        return new ExcludedRuleset(doc.Id, doc.Position.File, own);
    }

    /// <summary>
    ///     Builds the export graph over the directory: structural data (scope + id sets) straight from
    ///     the documents, exported-stat value types from a fix-point resolve (used-before-user).
    /// </summary>
    /// <param name="docs">The documents.</param>
    /// <param name="adapter">The Catalog scope-environment adapter.</param>
    /// <param name="context">The resolve context whose tick rate/profile the type pass uses.</param>
    /// <returns>The export graph.</returns>
    public static RulesetExportGraph BuildExportGraph(IReadOnlyList<RulesetDoc> docs, CatalogScopeAdapter adapter,
        ResolveContext context)
    {
        ArgumentNullException.ThrowIfNull(docs);
        ArgumentNullException.ThrowIfNull(adapter);
        ArgumentNullException.ThrowIfNull(context);

        // Structural surface (scope + declared/exported id sets) needs no resolution.
        Dictionary<string, (RulesetScope For, HashSet<string> Declared, HashSet<string> Exported)> structural = new(
            StringComparer.Ordinal);
        foreach (RulesetDoc doc in docs)
        {
            HashSet<string> declared = new(StringComparer.Ordinal);
            foreach (StatDef stat in doc.Stats)
            {
                declared.Add(stat.Id);
            }

            foreach (HighlightDef highlight in doc.Highlights)
            {
                declared.Add(highlight.Id);
            }

            HashSet<string> exported = doc.Exports is { } list
                ? new HashSet<string>(list, StringComparer.Ordinal)
                : new HashSet<string>(declared, StringComparer.Ordinal);

            structural[doc.Id] = (doc.For, declared, exported); // last-wins on a duplicate id (overlay's concern)
        }

        // Exported-stat value types via fix-point: resolve every not-yet-typed document against the
        // graph built from what is known so far; a dependency types before its reader, so the reader
        // resolves in a later round. Terminates in ≤ docs.Count rounds (a strict DAG); a use:-cycle just
        // leaves the cyclic stats' types unfilled (their qualified reads then attribute unknown-stat/…).
        Dictionary<string, Dictionary<string, RulesType>> exportedTypes = new(StringComparer.Ordinal);
        foreach (string id in structural.Keys)
        {
            exportedTypes[id] = new Dictionary<string, RulesType>(StringComparer.Ordinal);
        }

        HashSet<string> typed = new(StringComparer.Ordinal);
        bool progressed = true;
        while (progressed && typed.Count < docs.Count)
        {
            progressed = false;
            RulesetExportGraph roundGraph = MakeGraph(structural, exportedTypes);
            foreach (RulesetDoc doc in docs)
            {
                if (typed.Contains(doc.Id))
                {
                    continue;
                }

                RulesetResolveResult result = RulesetResolver.Resolve(doc, adapter, context, roundGraph);
                if (result.Ruleset is not { } ruleset)
                {
                    continue; // still missing a dependency's types; retry next round
                }

                Dictionary<string, RulesType> table = exportedTypes[doc.Id];
                (RulesetScope _, HashSet<string> _, HashSet<string> exportedIds) = structural[doc.Id];
                foreach (CheckedStat stat in ruleset.Stats)
                {
                    if (exportedIds.Contains(stat.StatId))
                    {
                        table[stat.StatId] = stat.ValueType;
                    }
                }

                typed.Add(doc.Id);
                progressed = true;
            }
        }

        return MakeGraph(structural, exportedTypes);
    }

    private static RulesetExportGraph MakeGraph(
        Dictionary<string, (RulesetScope For, HashSet<string> Declared, HashSet<string> Exported)> structural,
        Dictionary<string, Dictionary<string, RulesType>> exportedTypes)
    {
        Dictionary<string, RulesetExportGraph.Entry> entries = new(StringComparer.Ordinal);
        foreach ((string id, (RulesetScope forScope, HashSet<string> declared, HashSet<string> exported)) in structural)
        {
            entries[id] = new RulesetExportGraph.Entry(forScope, declared, exported,
                exportedTypes.TryGetValue(id, out Dictionary<string, RulesType>? t)
                    ? t
                    : new Dictionary<string, RulesType>(StringComparer.Ordinal));
        }

        return new RulesetExportGraph(entries);
    }

    /// <summary>Orders the checked rulesets used-before-user (topological on <c>use:</c>); input order on a cycle.</summary>
    private static List<CheckedRuleset> OrderByDependency(IReadOnlyList<RulesetDoc> docs,
        Dictionary<string, CheckedRuleset> checkedByDoc)
    {
        Dictionary<string, IReadOnlyList<string>> useByDoc = docs.ToDictionary(d => d.Id, d => d.Use, StringComparer.Ordinal);
        List<CheckedRuleset> ordered = new();
        HashSet<string> placed = new(StringComparer.Ordinal);
        HashSet<string> visiting = new(StringComparer.Ordinal);

        void Visit(string id)
        {
            if (placed.Contains(id) || !checkedByDoc.TryGetValue(id, out CheckedRuleset? ruleset) || !visiting.Add(id))
            {
                return;
            }

            if (useByDoc.TryGetValue(id, out IReadOnlyList<string>? uses))
            {
                foreach (string used in uses)
                {
                    Visit(used);
                }
            }

            visiting.Remove(id);
            if (placed.Add(id))
            {
                ordered.Add(ruleset);
            }
        }

        foreach (RulesetDoc doc in docs)
        {
            Visit(doc.Id);
        }

        return ordered;
    }

    /// <summary>The composition product: the checked rulesets (dependency-ordered) and every diagnostic.</summary>
    /// <param name="Rulesets">The successfully checked rulesets, used-before-user; empty on any hard failure path.</param>
    /// <param name="Diagnostics">
    ///     Every composition diagnostic (attributed cross-ref errors, within/cross cycles, resolve
    ///     errors).
    /// </param>
    public sealed record Result(IReadOnlyList<CheckedRuleset> Rulesets, IReadOnlyList<RulesetDiagnostic> Diagnostics)
    {
        /// <summary>True when the whole directory composed with no diagnostics.</summary>
        public bool Success => Diagnostics.Count == 0;

        /// <summary>
        ///     <see cref="Diagnostics" /> attributed to the ruleset each one came from — same count,
        ///     same order, same rendering. The form <c>BuildResult.RulesetDiagnostics</c> and
        ///     <c>DemoAnalysis.ValidateRulesets</c> surface to consumers that key on ruleset id.
        /// </summary>
        public IReadOnlyList<RulesetCompositionDiagnostic> AttributedDiagnostics { get; init; } = [];

        /// <summary>
        ///     Every document that failed to compose and was therefore dropped from
        ///     <see cref="Rulesets" />, with the diagnostics explaining each drop. In document order.
        /// </summary>
        public IReadOnlyList<ExcludedRuleset> Excluded { get; init; } = [];
    }
}
