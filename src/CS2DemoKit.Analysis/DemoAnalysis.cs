#region

using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Analysis.Building;
using CS2DemoKit.Analysis.Catalog;
using CS2DemoKit.Analysis.Config;
using CS2DemoKit.Analysis.Graphs;
using CS2DemoKit.Analysis.Output;
using CS2DemoKit.Analysis.Plugins;
using CS2DemoKit.Analysis.Registry;
using CS2DemoKit.Analysis.RulesetsV2.Model;
using CS2DemoKit.Analysis.RulesetsV2.Resolve;
using CS2DemoKit.Analysis.Yaml;
using CS2DemoKit.Parser;

#endregion

namespace CS2DemoKit.Analysis;

/// <summary>
///     Options for a <see cref="DemoAnalysis" /> run. All registries default to their
///     <c>CreateDefault()</c> / <c>Build()</c> sets; override only when embedding custom
///     providers or event registrations.
/// </summary>
public sealed record AnalysisOptions
{
    /// <summary>
    ///     Capture per-message <see cref="NodeSnapshot" /> rows for seek/inspect consumers (the UI path).
    ///     When <c>false</c>, runs the cheaper bare evaluation that produces only the
    ///     <see cref="RuleChainTimeline" /> (the bare, snapshot-free path).
    /// </summary>
    public bool CaptureSnapshots { get; init; } = true;

    /// <summary>Per-frame evaluation progress in [0, 1]. Only reported in snapshot mode.</summary>
    public IProgress<double>? Progress { get; init; }

    /// <summary>
    ///     Cancels the evaluation (checked once per frame and inside the parallel entity decode).
    ///     A canceled run throws <see cref="OperationCanceledException" /> — partial results are
    ///     discarded, never returned.
    /// </summary>
    public CancellationToken CancellationToken { get; init; }

    /// <summary>
    ///     Caps the worker count of the up-front parallel entity decode — the one phase of an
    ///     evaluation that fans out (the per-frame evaluation loop itself is sequential). <c>null</c>
    ///     (the default) leaves it unbounded, which is what the desktop app wants: one demo, all
    ///     cores. Set it when several demos are evaluated concurrently in one process — a batch
    ///     service otherwise multiplies (concurrent demos × ~<see cref="Environment.ProcessorCount" />)
    ///     decode workers onto the same cores, and the resulting oversubscription costs both latency
    ///     and peak memory (each worker owns a full <c>EntityTracker</c> + entity set).
    ///     <para>
    ///         Values <c>&lt;= 0</c> are ignored (treated as unbounded) rather than throwing, matching
    ///         <see cref="ParallelOptions.MaxDegreeOfParallelism" />'s "-1 means unlimited" convention
    ///         without exposing the sentinel. The cap bounds concurrency, not chunking: the decode is
    ///         still partitioned into ~<see cref="Environment.ProcessorCount" /> chunks, so a low cap
    ///         serializes chunks rather than making them bigger.
    ///     </para>
    /// </summary>
    public int? MaxDegreeOfParallelism { get; init; }

    /// <summary>Event registrations; defaults to <see cref="EventRegistry.Build" />.</summary>
    public EventRegistry? Events { get; init; }

    /// <summary>
    ///     Singleton entity-value providers; defaults to
    ///     <see cref="EntityValueProviderRegistry.CreateDefault" />. Supplying the entity-provider
    ///     registries is what lets <see cref="RuleChainBuilder" /> construct the
    ///     <see cref="EntityChangeScanner" /> when rules reference entity contexts — passing
    ///     <c>null</c> here means "use the defaults", not "no providers".
    /// </summary>
    public EntityValueProviderRegistry? EntityProviders { get; init; }

    /// <summary>
    ///     Per-player entity-value providers; defaults to
    ///     <see cref="PerPlayerEntityValueProviderRegistry.CreateDefault" />.
    /// </summary>
    public PerPlayerEntityValueProviderRegistry? PerPlayerEntityProviders { get; init; }
}

/// <summary>The result of a full <see cref="DemoAnalysis" /> run.</summary>
/// <param name="Build">The compiled graph and its metadata (skeleton-render inputs, scanner, chain keys).</param>
/// <param name="Timeline">Every chain activation/deactivation, in both modes.</param>
/// <param name="Snapshots">
///     The snapshot-mode result (per-message state rows, materialized players, applied-edge maps), or
///     <c>null</c> when <see cref="AnalysisOptions.CaptureSnapshots" /> was <c>false</c>.
/// </param>
public sealed record AnalysisRun(BuildResult Build, RuleChainTimeline Timeline, EvaluationResult? Snapshots)
{
    /// <summary>
    ///     Every v2 highlight firing of this run as a rich, self-contained record (A1 emission):
    ///     qualified <c>{ruleset}.{highlight}</c> identity, frame/tick (frame clock — the same
    ///     values as the <see cref="Timeline" /> events), subject slot + RAW player name, live
    ///     round attribution, and the rendered <c>title:</c>. Populated in BOTH modes — bare
    ///     (<see cref="AnalysisOptions.CaptureSnapshots" /> = <c>false</c>) included, which is the
    ///     Highlights pipeline's snapshot-free scan mode. Empty when the config declares no v2
    ///     highlights. In firing order.
    /// </summary>
    public IReadOnlyList<HighlightFired> Highlights { get; init; } = [];

    /// <summary>
    ///     Projects every configured output (the YAML <c>outputs:</c> declarations the build carried
    ///     through <see cref="Graphs.BuildResult.Outputs" />) into its <see cref="MetricTable" />, in
    ///     declared order. Configured outputs are <b>additive</b>: the three built-in
    ///     tables are not included here — callers combine this list with the built-in projectors'
    ///     output. Empty when the config declared no outputs.
    /// </summary>
    /// <param name="demo">The parsed demo the run evaluated (dimension context: map, players).</param>
    /// <param name="matchId">
    ///     Optional match identifier for the <c>match_id</c> dimension (typically the demo filename);
    ///     omitted per row when null.
    /// </param>
    /// <exception cref="InvalidOperationException">
    ///     The run was executed with <see cref="AnalysisOptions.CaptureSnapshots" /> disabled —
    ///     projection reads the snapshot vectors.
    /// </exception>
    public IReadOnlyList<MetricTable> ProjectConfiguredOutputs(ParsedDemo demo, string? matchId = null)
    {
        ArgumentNullException.ThrowIfNull(demo);
        if (Build.Outputs is not { Count: > 0 } outputs)
        {
            return [];
        }

        if (Snapshots is null)
        {
            throw new InvalidOperationException(
                "Configured outputs require snapshot mode — run with AnalysisOptions.CaptureSnapshots = true.");
        }

        List<MetricTable> tables = new(outputs.Count);
        foreach (OutputDef output in outputs)
        {
            if (!output.Enabled)
            {
                continue; // defensive — disabled outputs are normally dropped at load time
            }

            ConfiguredOutputProjector projector = new(output, Build.GameNodesByRuleId)
            {
                MatchId = matchId
            };
            tables.AddRange(projector.Project(Snapshots, demo));
        }

        return tables;
    }
}

/// <summary>
///     The single entry point for running the analysis engine over a parsed demo.
///     <para>
///         Wraps the build/evaluate assembly that every consumer otherwise has to repeat — registry
///         creation, builder construction, and (the part that silently produces wrong results when
///         forgotten) threading <see cref="BuildResult.PlayerContextIndex" /> and
///         <see cref="BuildResult.EntityScanner" /> from the build into the evaluator.
///     </para>
///     <para>
///         <see cref="Run" /> is the one-shot path. Consumers that need the compiled graph before
///         evaluation (e.g. to render a skeleton while the multi-second eval runs) call
///         <see cref="Build(ParsedDemo, IReadOnlyList{RulesetDoc}, AnalysisOptions?)" /> then
///         <see cref="Evaluate" /> — <see cref="Evaluate" /> accepts only a
///         <see cref="BuildResult" /> so the scanner/context threading cannot be bypassed.
///     </para>
///     <para>
///         <see cref="ValidateRulesets(IReadOnlyList{RulesetDoc})" /> is the one entry point here
///         that touches no demo at all: it runs the same composition step <see cref="Build" /> does
///         and reports what it found, for callers validating rule documents before storing them.
///     </para>
/// </summary>
public static class DemoAnalysis
{
    /// <summary>
    ///     Builds the loaded v2 rulesets onto one graph. The v2 docs are composed against the
    ///     demo's real tick rate and active source profile so D11a cross-ruleset
    ///     (<c>use:</c>/<c>exports:</c>) reads resolve against the export graph — the same seam the
    ///     the coverage path uses. An empty
    ///     <paramref name="v2Docs" /> list builds the bare context/enrichment graph.
    ///     <para>
    ///         Composition is <b>tolerant</b>: a document that fails cross-reference validation,
    ///         resolution, or a cycle check is dropped and the remaining rulesets still build. What
    ///         was dropped, and why, rides back on <see cref="BuildResult.RulesetDiagnostics" /> and
    ///         <see cref="BuildResult.ExcludedRulesets" /> — read them, or a broken ruleset is
    ///         indistinguishable from one whose feats simply never fired. To reject a broken set
    ///         <em>before</em> paying for a demo parse, call
    ///         <see cref="ValidateRulesets(IReadOnlyList{RulesetDoc})" />.
    ///     </para>
    /// </summary>
    /// <param name="demo">The parsed demo (its tick rate + profile drive v2 composition).</param>
    /// <param name="v2Docs">The loaded v2 ruleset documents (<c>RuleConfigLoadResult.Rulesets</c>).</param>
    /// <param name="options">Registry overrides.</param>
    public static BuildResult Build(ParsedDemo demo,
        IReadOnlyList<RulesetDoc> v2Docs, AnalysisOptions? options = null)
    {
        options ??= new AnalysisOptions();
        RuleChainBuilder builder = CreateBuilder(demo, options);
        if (v2Docs.Count == 0)
        {
            return builder.Build();
        }

        CatalogScopeAdapter adapter = CatalogScopeAdapter.From(CatalogResource.Load());
        RulesetComposition.Result composed =
            RulesetComposition.Compose(v2Docs, adapter, demo.TickRate, builder.Profile.GetType().Name);
        BuildResult build = builder.Build([.. composed.Rulesets]);
        return build with
        {
            RulesetDiagnostics = composed.AttributedDiagnostics,
            ExcludedRulesets = composed.Excluded
        };
    }

    /// <summary>
    ///     Validates a whole set of ruleset documents — against the embedded Catalog and against
    ///     each other — <b>without parsing or evaluating any demo</b>. This is the composition
    ///     pipeline <c>Build</c> runs, stopped one step short of building a graph: identifier
    ///     resolution and type checking per ruleset, plus the D11a cross-ruleset layer (the export
    ///     graph, the four qualified-reference errors, the read-scope rule, and within- and
    ///     cross-ruleset reference cycles).
    ///     <para>
    ///         The intended consumer is an upload-time validation endpoint for user-authored rules:
    ///         it answers "is this set safe to store and run" in milliseconds, and every diagnostic
    ///         carries a stable code, a message, and a <c>file(line,col)</c> position to hand back to
    ///         the author.
    ///     </para>
    ///     <para>
    ///         <b>Pass every document that shares the id namespace, not just the ones being
    ///         validated.</b> The export graph is built only from <paramref name="docs" />, so a user
    ///         ruleset with <c>use: [kast]</c> validated on its own reports a false
    ///         <c>resolve.cross-ref.unknown-ruleset</c>. A service layering database rules over the
    ///         shipped ones should validate
    ///         <c>YamlConfigLoader.LoadShippedWithOverlay(userDocs).Rulesets</c>.
    ///     </para>
    ///     <para>
    ///         Validation runs in the <b>draft</b> resolve context (<c>ResolveContext.Draft</c>),
    ///         which is deliberate: every reference and type error is rate- and profile-independent,
    ///         so they all surface here. What does NOT surface is anything downstream of duration
    ///         folding and param binding — including canonical rule hashing, which is
    ///         (tickRate, profile)-dependent and therefore has no demo-less answer. A consumer that
    ///         needs canonical hashes (cache keys, dedupe) computes them per demo context via
    ///         <c>HighlightConfigFingerprint.Compute(docs, ticksPerSecond, profileId)</c>.
    ///     </para>
    /// </summary>
    /// <param name="docs">
    ///     Every document in the id namespace being validated (e.g.
    ///     <c>RuleConfigLoadResult.Rulesets</c>). An empty list validates trivially.
    /// </param>
    /// <returns>The diagnostics, the excluded rulesets, and the ids that composed cleanly.</returns>
    public static RulesetValidationResult ValidateRulesets(IReadOnlyList<RulesetDoc> docs)
    {
        ArgumentNullException.ThrowIfNull(docs);
        if (docs.Count == 0)
        {
            return new RulesetValidationResult([], [], [], []);
        }

        CatalogScopeAdapter adapter = CatalogScopeAdapter.From(CatalogResource.Load());
        RulesetComposition.Result composed = RulesetComposition.ComposeDraft(docs, adapter);
        return new RulesetValidationResult(
            [],
            composed.AttributedDiagnostics,
            composed.Excluded,
            [.. composed.Rulesets.Select(rs => rs.Id.Id)]);
    }

    /// <summary>
    ///     Loads YAML documents from memory and validates them in one call — the shape an HTTP
    ///     upload endpoint wants, where the input is text rather than parsed documents. Equivalent
    ///     to <c>YamlConfigLoader.LoadDocuments(documents)</c> followed by
    ///     <see cref="ValidateRulesets(IReadOnlyList{RulesetDoc})" />, with the YAML-tier errors
    ///     preserved separately in <see cref="RulesetValidationResult.LoadErrors" />.
    ///     <para>
    ///         Documents that fail to load contribute their errors and no ruleset; the rest are
    ///         still composed, so one unparseable upload does not mask the composition errors in its
    ///         siblings. The same "pass the whole id namespace" rule as
    ///         <see cref="ValidateRulesets(IReadOnlyList{RulesetDoc})" /> applies — to validate
    ///         against the shipped tier, load with
    ///         <c>YamlConfigLoader.LoadShippedWithOverlay</c> and use the document overload.
    ///     </para>
    /// </summary>
    /// <param name="documents">Each document's label (used to attribute errors) and its YAML text.</param>
    /// <returns>The load errors, composition diagnostics, exclusions, and cleanly-composed ids.</returns>
    public static RulesetValidationResult ValidateRulesets(IEnumerable<(string Label, string Yaml)> documents)
    {
        ArgumentNullException.ThrowIfNull(documents);

        RuleConfigLoadResult loaded = YamlConfigLoader.LoadDocuments(documents);
        RulesetValidationResult composed = ValidateRulesets(loaded.Rulesets);
        return composed with
        {
            LoadErrors = loaded.Errors
        };
    }

    private static RuleChainBuilder CreateBuilder(ParsedDemo demo, AnalysisOptions options) => new(
        options.Events ?? EventRegistry.Build(),
        demo,
        entityProviders: options.EntityProviders ?? EntityValueProviderRegistry.CreateDefault(),
        perPlayerEntityProviders: options.PerPlayerEntityProviders ?? PerPlayerEntityValueProviderRegistry.CreateDefault());

    /// <summary>Evaluates a compiled graph over the demo's frames.</summary>
    public static AnalysisRun Evaluate(ParsedDemo demo, BuildResult build, AnalysisOptions? options = null)
    {
        options ??= new AnalysisOptions();
        StateGraphEvaluator evaluator = new(build.Graph, demo, build.PlayerContextIndex, build.EntityScanner);

        if (!options.CaptureSnapshots)
        {
            RuleChainTimeline timeline = evaluator.Evaluate(
                demo.Frames, options.MaxDegreeOfParallelism, options.CancellationToken);
            return new AnalysisRun(build, timeline, null)
            {
                Highlights = evaluator.HighlightsFired
            };
        }

        EvaluationResult result = evaluator.EvaluateWithSnapshots(
            demo.Frames, build.Nodes, options.Progress, options.MaxDegreeOfParallelism,
            options.CancellationToken);
        return new AnalysisRun(build, result.Timeline, result)
        {
            Highlights = evaluator.HighlightsFired
        };
    }

    /// <summary>Builds and evaluates in one call.</summary>
    public static AnalysisRun Run(ParsedDemo demo, IReadOnlyList<RulesetDoc> v2Docs,
        AnalysisOptions? options = null)
        => Evaluate(demo, Build(demo, v2Docs, options), options);
}
