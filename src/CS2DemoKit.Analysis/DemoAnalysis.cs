#region

using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Analysis.Building;
using CS2DemoKit.Analysis.Catalog;
using CS2DemoKit.Analysis.Config;
using CS2DemoKit.Analysis.Graphs;
using CS2DemoKit.Analysis.Output;
using CS2DemoKit.Analysis.Plugins;
using CS2DemoKit.Analysis.Profiles;
using CS2DemoKit.Analysis.Registry;
using CS2DemoKit.Analysis.Visibility;
using CS2DemoKit.Analysis.RulesetsV2.Model;
using CS2DemoKit.Analysis.RulesetsV2.Resolve;
using CS2DemoKit.Analysis.Yaml;
using CS2DemoKit.Parser;
using CS2DemoKit.Parser.GameEvents;

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
    ///     <c>false</c> runs the cheaper bare evaluation that produces only the
    ///     <see cref="RuleChainTimeline" />. <c>null</c>, the default, captures over a
    ///     <see cref="ParsedDemo" /> or any source with random access and does not over a stream:
    ///     a snapshot run keeps one row and one <see cref="NetMessage" /> per dispatched message,
    ///     so over a forward reader it retains what the reader was chosen to drop. Set it to
    ///     <c>true</c> on a stream deliberately; the choice is visible as
    ///     <see cref="AnalysisProvenance.SnapshotsCaptured" />.
    /// </summary>
    public bool? CaptureSnapshots { get; init; }

    /// <summary>Per-frame evaluation progress in [0, 1]. Only reported in snapshot mode.</summary>
    public IProgress<double>? Progress { get; init; }

    /// <summary>
    ///     Cancels the evaluation (checked once per frame and inside every digest worker).
    ///     A canceled run throws <see cref="OperationCanceledException" /> — partial results are
    ///     discarded, never returned.
    /// </summary>
    public CancellationToken CancellationToken { get; init; }

    /// <summary>
    ///     The entity digest workers an evaluation runs: the one phase that fans out (the per-frame
    ///     evaluation loop itself is sequential). Each worker holds a tracker and a chunk of frames
    ///     decoded ahead of the loop. <c>null</c> (the default) takes the source's default: three
    ///     over a forward reader, where the read bounds the run and each worker is memory the run
    ///     would otherwise not hold, and two fewer than the core count over a retained demo, where
    ///     the frames are already resident and the fold is the only thing left to hide. One selects
    ///     the sequential producer: a single layer advanced in step with the loop. Set it when
    ///     several demos are evaluated concurrently in one process, so they do not each fan out to
    ///     every core.
    ///     <para>
    ///         Values <c>&lt;= 0</c> are ignored (treated as the default) rather than throwing, so a
    ///         misconfigured integer in a service's options cannot cost it an evaluation.
    ///     </para>
    /// </summary>
    public int? MaxDegreeOfParallelism { get; init; }

    /// <summary>
    ///     The source profile to build for. Set, it wins over every resolution step and the run
    ///     reports <see cref="ProfileResolutionKind.Explicit" />. Unset, the profile is resolved from
    ///     the demo's header and, where the source allows, the game-event vocabulary it fires.
    /// </summary>
    public DemoSourceProfile? Profile { get; init; }

    /// <summary>
    ///     Over a <see cref="DemoReader" /> that has not started, run the structure-only vocabulary
    ///     probe so the round-end dialect is exact instead of guessed from the header: a pass that
    ///     decodes event ids and nothing else, and stops as soon as the first round has decided
    ///     the dialect. Off, a stream resolves from
    ///     the header alone and the run reports <see cref="ProfileResolutionKind.HeaderOnly" />.
    ///     Ignored when <see cref="Profile" /> is set.
    /// </summary>
    public bool ProbeDialect { get; init; } = true;

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

    /// <summary>
    ///     Baked map collision geometry, enabling the synthesized <c>enemy_spotted</c> event. Unlike
    ///     every other option here, <c>null</c> means "the capability is unavailable for this run",
    ///     not "use the default": visibility has no wire signal to fall back on, so a rule
    ///     subscribing to <c>enemy_spotted</c> simply never fires without geometry.
    ///     <para>
    ///         The analysis layer does no file I/O and does not know where bakes live. Load one with
    ///         <c>VisibilityEngine.Load</c> against the path <c>CollisionAssetLocator</c> resolves for
    ///         the demo's map, off the calling thread (the BVH build is tenths of a second on the
    ///         large bakes), and hand it in. The engine is immutable after construction and safe to
    ///         share across runs of the same map, which is what makes reusing one across a batch
    ///         worthwhile.
    ///     </para>
    /// </summary>
    public VisibilityEngine? VisibilityEngine { get; init; }
}

/// <summary>The result of a full <see cref="DemoAnalysis" /> run.</summary>
/// <param name="Build">The compiled graph and its metadata (nodes, descriptors, scanner, node maps).</param>
/// <param name="Timeline">Every chain activation/deactivation, in both modes.</param>
/// <param name="Snapshots">
///     The snapshot-mode result (per-message state rows, materialized players, applied-edge maps), or
///     <c>null</c> when snapshots were not captured (see <see cref="AnalysisOptions.CaptureSnapshots" />).
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

    /// <summary>The demo's facts at the end of the run, detached from whichever source produced it.</summary>
    public required DemoDescriptor Demo { get; init; }

    /// <summary>
    ///     What a run without snapshots recorded for its configured tables, sampled at the round
    ///     boundaries; null on a snapshot run, or when the build has no table to project.
    /// </summary>
    internal ProjectionSource? Recorded { get; init; }

    /// <summary>Which source, profile and digest producer ran, and how much was consumed.</summary>
    public required AnalysisProvenance Provenance { get; init; }

    /// <summary>Every player materialised during the run, in order. Populated in both capture modes.</summary>
    public IReadOnlyList<PerPlayerNodeTemplate.MaterializedPlayer> MaterializedPlayers { get; init; } = [];

    /// <summary>Every per-player node materialised during the run. Populated in both capture modes.</summary>
    public IReadOnlyList<StateNode> MaterializedNodes { get; init; } = [];

    /// <summary>
    ///     The graph's static nodes followed by the materialised per-player nodes the snapshot table
    ///     would track. The same list in both capture modes, which is what lets a bare run be
    ///     compared with a snapshot run node for node.
    /// </summary>
    public IReadOnlyList<StateNode> FinalNodes =>
        [.. Build.Nodes, .. MaterializedNodes.Where(n => n is not ISnapshotExcludedNode)];

    /// <summary>Projects the configured outputs against <see cref="Demo" />.</summary>
    public IReadOnlyList<MetricTable> ProjectConfiguredOutputs(string? matchId = null) =>
        ProjectConfiguredOutputs(Demo, matchId);

    /// <summary>Projects the configured outputs against a retained demo's final facts.</summary>
    public IReadOnlyList<MetricTable> ProjectConfiguredOutputs(ParsedDemo demo, string? matchId = null)
    {
        ArgumentNullException.ThrowIfNull(demo);
        return ProjectConfiguredOutputs(DemoDescriptor.From(demo), matchId);
    }

    /// <summary>
    ///     Projects every configured output (the YAML <c>outputs:</c> declarations the build carried
    ///     through <see cref="Graphs.BuildResult.Outputs" />) into its <see cref="MetricTable" />, in
    ///     declared order. Configured outputs are <b>additive</b>: the three built-in
    ///     tables are not included here — callers combine this list with the built-in projectors'
    ///     output. Empty when the config declared no outputs.
    /// </summary>
    /// <param name="demo">The demo facts to project against (dimension context: map, players).</param>
    /// <param name="matchId">
    ///     Optional match identifier for the <c>match_id</c> dimension (typically the demo filename);
    ///     omitted per row when null.
    /// </param>
    /// <remarks>
    ///     A snapshot run projects from its snapshot rows. A run without snapshots projects from what
    ///     it recorded at the round boundaries, which reads the same state, so the tables agree row for
    ///     row; the one exception is an output that logs timeline events (<see cref="OutputScope.PerEvent" />),
    ///     which only a snapshot run can project.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    ///     The run captured no snapshots and an output logs timeline events.
    /// </exception>
    public IReadOnlyList<MetricTable> ProjectConfiguredOutputs(DemoDescriptor demo, string? matchId = null)
    {
        ArgumentNullException.ThrowIfNull(demo);
        if (Build.Outputs is not { Count: > 0 } outputs)
        {
            return [];
        }

        if (Snapshots is null && outputs.Any(o => o.Enabled && o.Scope == OutputScope.PerEvent))
        {
            throw new InvalidOperationException(
                "A configured output that logs timeline events (per-event) requires snapshot mode — run with "
                + "AnalysisOptions.CaptureSnapshots = true.");
        }

        ProjectionSource? recorded = Snapshots is null
            ? Recorded ?? new ProjectionSource([], null, MaterializedPlayers)
            : null;

        List<MetricTable> tables = new(outputs.Count);
        foreach (OutputDef output in outputs)
        {
            if (!output.Enabled)
            {
                continue; // defensive — disabled outputs are normally dropped at load time
            }

            ConfiguredOutputProjector projector = new(output, Build.GameNodesByRuleId)
            {
                MatchId = matchId,
                TeamNodesByRuleId = Build.TeamNodesByRuleId,
                TeamRosterNodes = Build.TeamRosterNodes
            };
            tables.AddRange(Snapshots is not null
                ? projector.Project(Snapshots, demo)
                : projector.Project(recorded!, demo));
        }

        return tables;
    }
}

/// <summary>
///     The single entry point for running the analysis engine over a demo, whether it is read
///     forward once or held in memory.
///     <para>
///         Wraps the build/evaluate assembly that every consumer otherwise has to repeat — registry
///         creation, builder construction, and (the part that silently produces wrong results when
///         forgotten) threading <see cref="BuildResult.PlayerContextIndex" /> and
///         <see cref="BuildResult.EntityScanner" /> from the build into the evaluator.
///     </para>
///     <para>
///         <see cref="Run(string, IReadOnlyList{RulesetDoc}, AnalysisOptions)" /> is the one-shot
///         forward path: it opens a <see cref="DemoReader" />, resolves the profile, builds, narrows
///         the reader's decode to what the graph consumes, and evaluates while frames are dropped
///         behind the loop. <see cref="Run(ParsedDemo, IReadOnlyList{RulesetDoc}, AnalysisOptions)" />
///         is the same evaluation over a retained demo, with snapshots on by default. Consumers that
///         need the compiled graph before evaluation (e.g. to render a skeleton while the
///         multi-second eval runs) call a <c>Build</c> overload then an <c>Evaluate</c> overload;
///         <c>Evaluate</c> accepts only a <see cref="BuildResult" /> so the scanner/context threading
///         cannot be bypassed.
///     </para>
///     <para>
///         <see cref="ValidateRulesets(IReadOnlyList{RulesetDoc})" /> is the one entry point here
///         that touches no demo at all: it runs the same composition step the <c>Build</c> overloads
///         do and reports what it found, for callers validating rule documents before storing them.
///     </para>
/// </summary>
public static class DemoAnalysis
{
    // Consumed by the evaluator and its built-in edges whether or not a rule subscribes.
    private static readonly string[] _intrinsicEventNames =
    [
        "round_freeze_end", "begin_new_match", "player_death", "player_hurt", "player_connect",
        "player_team", "player_disconnect", "player_spawn", "round_officially_ended", "cs_pre_restart"
    ];

    private static Dictionary<string, List<int>>? _netIdsBySuffix;

    /// <summary>
    ///     The source profile a retained demo builds under: <see cref="AnalysisOptions.Profile" />
    ///     when set, else the header classification refined by every game-event name the demo
    ///     carries. The same answer for the same demo regardless of path.
    /// </summary>
    public static DemoSourceProfile ResolveProfile(ParsedDemo demo, AnalysisOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(demo);
        return ResolveProfileCore(demo, options).Profile;
    }

    /// <summary>
    ///     The source profile a frame source builds under: <see cref="AnalysisOptions.Profile" />
    ///     when set; else, over a <see cref="DemoReader" /> that has not started and with
    ///     <see cref="AnalysisOptions.ProbeDialect" /> on, the header refined by the vocabulary
    ///     probe; else, over a source with random access, the header refined by the events its
    ///     frames hold; else the header alone.
    /// </summary>
    public static DemoSourceProfile ResolveProfile(IDemoFrameSource source, AnalysisOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        return ResolveProfileCore(source, options).Profile;
    }

    /// <summary>
    ///     Builds the loaded v2 rulesets onto one graph for a known tick rate and profile, with no
    ///     demo in hand. The v2 docs are composed against the target's tick rate and profile so
    ///     D11a cross-ruleset (<c>use:</c>/<c>exports:</c>) reads resolve against the export graph,
    ///     the same seam the coverage path uses. An empty <paramref name="v2Docs" /> list builds the
    ///     bare context/enrichment graph.
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
    /// <param name="target">The tick rate and profile the graph is built for.</param>
    /// <param name="v2Docs">The loaded v2 ruleset documents (<c>RuleConfigLoadResult.Rulesets</c>).</param>
    /// <param name="options">Registry overrides.</param>
    public static BuildResult Build(AnalysisTarget target, IReadOnlyList<RulesetDoc> v2Docs,
        AnalysisOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        return BuildCore(target, ProfileResolutionKind.Explicit, v2Docs, options ?? new AnalysisOptions());
    }

    /// <summary>
    ///     Builds for a retained demo: its tick rate, and its profile as
    ///     <see cref="ResolveProfile(ParsedDemo, AnalysisOptions)" /> resolves it.
    /// </summary>
    public static BuildResult Build(ParsedDemo demo, IReadOnlyList<RulesetDoc> v2Docs,
        AnalysisOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(demo);
        options ??= new AnalysisOptions();
        (DemoSourceProfile profile, ProfileResolutionKind resolution) = ResolveProfileCore(demo, options);
        return BuildCore(new AnalysisTarget(demo.TickRate, profile), resolution, v2Docs, options);
    }

    /// <summary>
    ///     Builds for any frame source: the tick rate its enrichment reports, and its profile as
    ///     <see cref="ResolveProfile(IDemoFrameSource, AnalysisOptions)" /> resolves it. A
    ///     <see cref="DemoReader" /> knows both before its first frame is read.
    /// </summary>
    public static BuildResult Build(IDemoFrameSource source, IReadOnlyList<RulesetDoc> v2Docs,
        AnalysisOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        options ??= new AnalysisOptions();
        (DemoSourceProfile profile, ProfileResolutionKind resolution) = ResolveProfileCore(source, options);
        return BuildCore(new AnalysisTarget(source.Enrichment.TickRate, profile), resolution, v2Docs, options);
    }

    /// <summary>
    ///     The narrowest decode plan that still feeds <paramref name="build" /> everything its graph
    ///     dispatches: the header and string tables always, every game event an edge or the
    ///     evaluator consumes, and the schema, entities and retained signon prefix only when the
    ///     build has an entity scanner. A net message an edge subscribes to is included by wire
    ///     id; one the catalog cannot place widens the plan to every
    ///     <see cref="MessageCategories.Other" /> message rather than dropping it.
    /// </summary>
    public static DecodePlan PlanDecode(BuildResult build)
    {
        ArgumentNullException.ThrowIfNull(build);
        EventRegistry registry = build.Events ?? EventRegistry.Build();
        HashSet<string> eventNames = new(_intrinsicEventNames, StringComparer.OrdinalIgnoreCase);
        HashSet<int> netIds = [];
        MessageCategories categories =
            MessageCategories.Header | MessageCategories.StringTables | MessageCategories.GameEvents;

        foreach (Type type in build.RelevantMessageTypes.Concat(build.Graph.Edges.Select(e => e.MessageType)))
        {
            if (!registry.TryGetName(type, out string name, out bool isNetMessage))
            {
                continue; // synthesized, or never on the wire
            }

            if (!isNetMessage)
            {
                eventNames.Add(name);
            }
            else if (!TryPlaceNetPayload(type, netIds, ref categories))
            {
                categories |= MessageCategories.Other;
            }
        }

        bool scanner = build.EntityScanner is not null;
        if (scanner)
        {
            categories |= MessageCategories.Schema | MessageCategories.Entities;
        }

        return new DecodePlan
        {
            Categories = categories,
            IncludeMessageTypes = netIds.Count > 0 ? netIds : null,
            GameEventNames = eventNames,
            RetainSignonPrefix = scanner
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

    /// <summary>Evaluates a compiled graph over a retained demo. Snapshots are captured unless turned off.</summary>
    public static AnalysisRun Evaluate(ParsedDemo demo, BuildResult build, AnalysisOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(demo);
        return EvaluateCore(demo.AsFrameSource(), build, options ?? new AnalysisOptions(), demo);
    }

    /// <summary>
    ///     Evaluates a compiled graph over any frame source. Over a forward reader the frames are
    ///     consumed and dropped, entity state is decoded in step with the loop, and snapshots are
    ///     off unless asked for; over a retained demo this is the same evaluation the
    ///     <see cref="ParsedDemo" /> overload runs. The source must decode at least what
    ///     <see cref="PlanDecode" /> asks for; a narrower reader silently starves the graph.
    /// </summary>
    public static AnalysisRun Evaluate(IDemoFrameSource source, BuildResult build, AnalysisOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        return EvaluateCore(source, build, options ?? new AnalysisOptions(), null);
    }

    /// <summary>Builds and evaluates a retained demo in one call.</summary>
    public static AnalysisRun Run(ParsedDemo demo, IReadOnlyList<RulesetDoc> v2Docs,
        AnalysisOptions? options = null)
        => Evaluate(demo, Build(demo, v2Docs, options), options);

    /// <summary>
    ///     Builds and evaluates over any frame source. A <see cref="DemoReader" /> that has not
    ///     started and still decodes everything is narrowed to <see cref="PlanDecode" /> first; a
    ///     reader the caller already configured is left on its own plan.
    /// </summary>
    public static AnalysisRun Run(IDemoFrameSource source, IReadOnlyList<RulesetDoc> v2Docs,
        AnalysisOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        BuildResult build = Build(source, v2Docs, options);
        if (source is DemoReader { Started: false } reader && reader.Plan.DecodesEverything)
        {
            reader.Configure(PlanDecode(build));
        }

        return Evaluate(source, build, options);
    }

    /// <summary>
    ///     The forward path over a file: open, resolve the profile, build, narrow the decode to
    ///     what the graph consumes, evaluate, close. Nothing but the run's outputs and the
    ///     signon prefix outlives the loop.
    /// </summary>
    public static AnalysisRun Run(string path, IReadOnlyList<RulesetDoc> v2Docs, AnalysisOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        using DemoReader reader = DemoReader.OpenFile(path, ReaderOptions(options));
        return Run(reader, v2Docs, options);
    }

    /// <summary>The forward path over a demo already in memory; see <see cref="Run(string, IReadOnlyList{RulesetDoc}, AnalysisOptions)" />.</summary>
    public static AnalysisRun Run(ReadOnlyMemory<byte> bytes, IReadOnlyList<RulesetDoc> v2Docs,
        AnalysisOptions? options = null)
    {
        using DemoReader reader = DemoReader.Open(bytes, ReaderOptions(options));
        return Run(reader, v2Docs, options);
    }

    // The decode window on the reader thread. With the fold parallelised and the read on its
    // own thread, the decode is the thread that bounds a run, and one window of frames in
    // flight (about 40 MB live with its decode partitions on a full match) is what lets it
    // keep up. Off when the caller asked for one thread, which selects the sequential
    // producer.
    private const int DefaultReadAheadFrames = 1024;

    private static ParseOptions ReaderOptions(AnalysisOptions? options) => new()
    {
        MaxDegreeOfParallelism = options?.MaxDegreeOfParallelism,
        CancellationToken = options?.CancellationToken ?? default,
        ReadAheadFrames = options?.MaxDegreeOfParallelism == 1 ? 0 : DefaultReadAheadFrames
    };

    private static BuildResult BuildCore(AnalysisTarget target, ProfileResolutionKind resolution,
        IReadOnlyList<RulesetDoc> v2Docs, AnalysisOptions options)
    {
        ArgumentNullException.ThrowIfNull(v2Docs);
        RuleChainBuilder builder = CreateBuilder(target, options);
        if (v2Docs.Count == 0)
        {
            return builder.Build() with
            {
                ProfileResolution = resolution
            };
        }

        CatalogScopeAdapter adapter = CatalogScopeAdapter.From(CatalogResource.Load());
        RulesetComposition.Result composed =
            RulesetComposition.Compose(v2Docs, adapter, target.TickRate, builder.Profile.GetType().Name);
        BuildResult build = builder.Build([.. composed.Rulesets]);
        return build with
        {
            RulesetDiagnostics = composed.AttributedDiagnostics,
            ExcludedRulesets = composed.Excluded,
            ProfileResolution = resolution
        };
    }

    private static RuleChainBuilder CreateBuilder(AnalysisTarget target, AnalysisOptions options) => new(
        options.Events ?? EventRegistry.Build(),
        target,
        entityProviders: options.EntityProviders ?? EntityValueProviderRegistry.CreateDefault(),
        perPlayerEntityProviders: options.PerPlayerEntityProviders ?? PerPlayerEntityValueProviderRegistry.CreateDefault(),
        visibilityEngine: options.VisibilityEngine);

    private static (DemoSourceProfile Profile, ProfileResolutionKind Resolution) ResolveProfileCore(
        ParsedDemo demo, AnalysisOptions? options)
    {
        if (options?.Profile is { } explicitProfile)
        {
            return (explicitProfile, ProfileResolutionKind.Explicit);
        }

        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
        foreach (GameEvent evt in demo.AllGameEvents)
        {
            names.Add(evt.Name);
        }

        return (DemoSourceProfileRegistry.Resolve(demo.Profile, names), ProfileResolutionKind.HeaderAndVocabulary);
    }

    private static (DemoSourceProfile Profile, ProfileResolutionKind Resolution) ResolveProfileCore(
        IDemoFrameSource source, AnalysisOptions? options)
    {
        if (options?.Profile is { } explicitProfile)
        {
            return (explicitProfile, ProfileResolutionKind.Explicit);
        }

        DemoProfile header = source.Enrichment.Profile;
        if (source is DemoReader { Started: false } reader && (options?.ProbeDialect ?? true))
        {
            IReadOnlySet<string> probed = reader.ProbeGameEventNames(DialectProbe.Stop(), options?.CancellationToken ?? default);
            return (DemoSourceProfileRegistry.Resolve(header, probed), ProfileResolutionKind.HeaderAndVocabulary);
        }

        if (source.Frames is { } frames)
        {
            HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
            foreach (DemoFrame frame in frames)
            {
                foreach (NetMessage msg in frame.DecodedMessages)
                {
                    if (msg is GameEventMessage gem)
                    {
                        names.Add(gem.DecodedEvent.Name);
                    }
                }
            }

            return (DemoSourceProfileRegistry.Resolve(header, names), ProfileResolutionKind.HeaderAndVocabulary);
        }

        return (DemoSourceProfileRegistry.Resolve(header), ProfileResolutionKind.HeaderOnly);
    }

    private static AnalysisRun EvaluateCore(IDemoFrameSource source, BuildResult build, AnalysisOptions options,
        ParsedDemo? demo)
    {
        ArgumentNullException.ThrowIfNull(build);
        StateGraphEvaluator evaluator = new(build.Graph, demo, build.PlayerContextIndex, build.EntityScanner);
        bool capture = options.CaptureSnapshots ?? source.SupportsRandomAccess;

        RuleChainTimeline timeline;
        EvaluationResult? result = null;
        ConfiguredOutputRecorder? recorder = null;
        if (!capture)
        {
            // Without snapshots the configured tables are sampled at the round boundaries instead.
            if (ConfiguredOutputRecorder.Serves(build))
            {
                recorder = new ConfiguredOutputRecorder(build);
                evaluator.Observer = recorder;
            }

            timeline = evaluator.Evaluate(source, options.MaxDegreeOfParallelism, options.CancellationToken);
        }
        else
        {
            result = evaluator.EvaluateWithSnapshots(
                source, build.Nodes, options.Progress, options.MaxDegreeOfParallelism,
                options.CancellationToken);
            timeline = result.Timeline;
        }

        return new AnalysisRun(build, timeline, result)
        {
            Recorded = recorder?.ToSource(),
            Highlights = evaluator.HighlightsFired,
            Demo = source.Enrichment.Snapshot(),
            MaterializedPlayers = evaluator.MaterializedPlayers,
            MaterializedNodes = evaluator.MaterializedNodes,
            Provenance = new AnalysisProvenance(
                source.SupportsRandomAccess ? AnalysisSourceKind.Materialised : AnalysisSourceKind.Stream,
                build.Profile,
                build.ProfileResolution,
                source is DemoReader reader ? reader.Plan : demo?.Plan,
                build.EntityScanner?.ProducerKind ?? DigestProducerKind.None,
                result is not null,
                evaluator.FramesConsumed,
                evaluator.MessagesConsumed,
                CheckDialect(build.Profile, evaluator.RoundOfficiallyEndedSeen, evaluator.CsPreRestartSeen))
        };
    }

    private static DialectCheck CheckDialect(DemoSourceProfile profile, int officiallyEnded, int preRestart)
    {
        IReadOnlyList<string> bound = profile.RoundEnd?.ConcreteEventNames ?? [];
        bool bindsOfficial = bound.Contains("round_officially_ended", StringComparer.OrdinalIgnoreCase);
        bool bindsPreRestart = bound.Contains("cs_pre_restart", StringComparer.OrdinalIgnoreCase);
        bool neverSeen = (bindsOfficial && officiallyEnded == 0 && preRestart > 0)
                         || (bindsPreRestart && preRestart == 0 && officiallyEnded > 0);
        return new DialectCheck(officiallyEnded, preRestart, neverSeen);
    }

    // Registrations name a payload class (CNETMsg_Tick, CDemoFileHeader); the catalog names the
    // wire enum member (net_Tick, DEM_FileHeader). The part after the first underscore is shared.
    private static bool TryPlaceNetPayload(Type payloadType, HashSet<int> netIds, ref MessageCategories categories)
    {
        string className = payloadType.Name;
        if (className.StartsWith("CDemo", StringComparison.Ordinal))
        {
            string commandName = "DEM_" + className["CDemo".Length..];
            foreach ((int id, string name) in NetMessageCatalog.DemoCommandNames)
            {
                if (string.Equals(name, commandName, StringComparison.OrdinalIgnoreCase))
                {
                    categories |= NetMessageCatalog.CategoryOf((EDemoCommands)id);
                    return true;
                }
            }

            return false;
        }

        int underscore = className.IndexOf('_');
        if (underscore < 0)
        {
            return false;
        }

        _netIdsBySuffix ??= BuildNetIdsBySuffix();
        if (!_netIdsBySuffix.TryGetValue(className[(underscore + 1)..], out List<int>? ids))
        {
            return false;
        }

        foreach (int id in ids)
        {
            netIds.Add(id);
        }

        return true;
    }

    private static Dictionary<string, List<int>> BuildNetIdsBySuffix()
    {
        Dictionary<string, List<int>> map = new(StringComparer.OrdinalIgnoreCase);
        foreach ((int id, string name) in NetMessageCatalog.Names)
        {
            int underscore = name.IndexOf('_');
            if (underscore < 0)
            {
                continue;
            }

            string suffix = name[(underscore + 1)..];
            if (!map.TryGetValue(suffix, out List<int>? ids))
            {
                map[suffix] = ids = [];
            }

            ids.Add(id);
        }

        return map;
    }
}
