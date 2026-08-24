#region

using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Analysis.Catalog;
using CS2DemoKit.Analysis.Config;
using CS2DemoKit.Analysis.Edges;
using CS2DemoKit.Analysis.Graphs;
using CS2DemoKit.Analysis.Nodes;
using CS2DemoKit.Analysis.Plugins;
using CS2DemoKit.Analysis.Registry;
using CS2DemoKit.Analysis.Rules;
using CS2DemoKit.Analysis.Rules.Ast;
using CS2DemoKit.Analysis.Rules.Checking;
using CS2DemoKit.Analysis.Rules.Hashing;
using CS2DemoKit.Analysis.RulesetsV2.Compile;
using CS2DemoKit.Analysis.RulesetsV2.Model;
using CS2DemoKit.Analysis.RulesetsV2.Resolve;
using CS2DemoKit.Parser;
using CS2DemoKit.Parser.GameEvents;
using TriggerDef = CS2DemoKit.Analysis.Config.TriggerDef;

#endregion

namespace CS2DemoKit.Analysis.Building;

/// <summary>
///     The Rulesets v2 planner: the half of <see cref="RuleChainBuilder" /> that
///     lowers a <see cref="CheckedRuleset" /> IR onto the same <see cref="StateGraph" /> primitives
///     the v1 chains use. It reuses the v1 runtime end to end — nodes are the same
///     <c>GenericValueNode</c>/<c>GenericRoundScopedValueNode</c>/<c>ConjunctionNode</c> types, edges
///     are built through <see cref="CreateGameEventEdge" /> with a v1-grammar condition/value string
///     serialized from the checked AST (<see cref="V1ExpressionWriter" />) — so a v2 node evaluates
///     byte-identically to the v1 rule it replaces. Every v2 node is stamped with its ruleset's
///     <see cref="RulesetId.JoinKey" /> surface (highlight timeline chain nodes named
///     <c>_chain_&lt;id&gt;</c>) and registered under a qualified <c>{ruleset}.{stat}</c> key,
///     so the timeline, projector, and fire-count layers resolve
///     v2 rulesets with no evaluator change.
/// </summary>
public sealed partial class RuleChainBuilder
{
    /// <summary>The sentinel subject slot for a game-scoped stat — unreferenced (actor binding suppressed).</summary>
    private const int GameScopeSlot = 0;

    /// <summary>The (absent) player name for a game-scoped node — no player attribution (empty subtitle).</summary>
    private const string GameScopePlayerName = "";

    private static readonly MethodInfo _listAppendEdgeMethod =
        typeof(RuleChainBuilder).GetMethod(nameof(CreateListAppendEdgeGeneric),
            BindingFlags.NonPublic | BindingFlags.Static)!;

    // A2 golden drift-guard seam: the canonical stat/highlight hash map (path → 32-byte hash,
    // all registered spellings) of the most recent per-player template materialization. Written
    // by the template factory (reference assignment only); read by the fingerprint helper's
    // golden test to assert HighlightConfigFingerprint replays the builder's hashing exactly.
    private Dictionary<string, ReadOnlyMemory<byte>>? _lastMaterializedV2StatHashes;

    /// <summary>
    ///     Test seam (A2 drift guard): the path → resolved-identity-hash map accumulated by the
    ///     most recent per-player template materialization — stats under their bare and
    ///     <c>{ruleset}.{stat}</c> spellings, highlights under all four spellings (bare id,
    ///     <c>&lt;id&gt;.count</c>, and both qualified forms). <c>null</c> until a v2 per-player
    ///     template has materialized. Hashes are slot-independent, so the map is identical across
    ///     materializations of one build.
    /// </summary>
    internal IReadOnlyDictionary<string, ReadOnlyMemory<byte>>? LastMaterializedV2StatHashes
        => _lastMaterializedV2StatHashes;

    /// <summary>
    ///     Builds every v2 ruleset into the shared graph: <c>for: each_player</c> rulesets register
    ///     a per-player template; the pilot lives here. Called from
    ///     <see cref="Build(IReadOnlyList{CheckedRuleset}?, RulesetCompilerOptions?)" />
    ///     after the context/enrichment graph is wired, so the game contexts (incl.
    ///     <c>bomb_was_planted</c>) and enrichment nodes the v2 nodes read are already in
    ///     <paramref name="nodeLookup" />.
    /// </summary>
    private void BuildRulesetsV2(
        IReadOnlyList<CheckedRuleset> rulesets,
        RulesetCompilerOptions options,
        StateGraph graph,
        Dictionary<string, StateNode> nodeLookup,
        List<StateNode> allNodes,
        List<GraphEdgeDescriptor> edgeDescriptors,
        Dictionary<string, StateNode> gameNodesByRuleId,
        HashSet<Type> relevantTypes,
        List<OutputDef> v2Outputs,
        IReadOnlyList<RuleDef> perPlayerContextRules)
    {
        List<CheckedRuleset> perPlayer = [];
        List<CheckedRuleset> gameScope = [];
        foreach (CheckedRuleset rs in rulesets)
        {
            if (rs.For == RulesetScope.EachPlayer)
            {
                perPlayer.Add(rs);
            }
            else
            {
                gameScope.Add(rs);
            }

            // show: tables -> export defs. The metric refs are the planner's
            // qualified {ruleset}.{stat} keys, resolved by the projector against the per-player
            // node map (for: each_player) or the game node map (for: match, the PerMatch scope
            // fallback). No v1 ValidateOutputs pass runs on these keys.
            v2Outputs.AddRange(ShowLowering.LowerTables(rs));
        }

        if (perPlayer.Count > 0)
        {
            BuildV2PerPlayerTemplate(perPlayer, options, graph, nodeLookup, relevantTypes,
                perPlayerContextRules);
        }

        if (gameScope.Count > 0)
        {
            BuildV2GameScope(gameScope, options, graph, nodeLookup, allNodes, edgeDescriptors,
                gameNodesByRuleId, relevantTypes);
        }
    }

    /// <summary>
    ///     Builds every <c>for: match</c> (game-scoped) v2 ruleset directly onto the shared graph —
    ///     one node per stat, not a per-player template. A match-scoped stat has <b>no subject</b>: the
    ///     resolver already suppresses the view's per-player actor binding
    ///     (<see cref="BuildActorBinding" /> returns <c>null</c> for a non-<c>each_player</c> ruleset),
    ///     so <c>count: kill</c> counts <em>every</em> kill (the view's baked killer≠victim condition
    ///     still gates it), and <c>player.*</c> reads are rejected at resolve (the <c>player</c> root is
    ///     only in scope for <c>each_player</c>). The subject-relative B6 aggregates
    ///     (<c>round.team.*</c> / <c>round.enemies.*</c> / <c>round.alive.*</c>) DO type-check at match
    ///     scope (they sit under the always-present <c>round</c> root) but have no subject here — they
    ///     are rejected loud in <see cref="RejectSubjectRelativeReads" /> rather than silently binding a
    ///     phantom slot-0 aggregate.
    ///     <para>
    ///         The lowering reuses <see cref="BuildV2Stat" /> with a sentinel slot and a null player
    ///         name (the actor binding suppressed), accumulating nodes/edges/descriptors into local
    ///         lists, then pushes them onto the graph exactly as <see cref="BuildSingletonRule" /> does
    ///         for a v1 game rule (<c>allNodes</c> + <c>graph.AddEdge</c> +
    ///         <c>graph.AddConjunction/AddDisjunction</c> + <c>graph.AddLiveCompute</c>), and registers
    ///         each node in the game <c>{ruleset}.{stat}</c> map so the configured-output projector
    ///         resolves match metrics against it. The per-player
    ///         template path is untouched — this is a pure additive branch.
    ///     </para>
    /// </summary>
    private void BuildV2GameScope(
        IReadOnlyList<CheckedRuleset> rulesets,
        RulesetCompilerOptions options,
        StateGraph graph,
        Dictionary<string, StateNode> nodeLookup,
        List<StateNode> allNodes,
        List<GraphEdgeDescriptor> edgeDescriptors,
        Dictionary<string, StateNode> gameNodesByRuleId,
        HashSet<Type> relevantTypes)
    {
        CatalogRoot catalog = CatalogResource.Load();
        Dictionary<string, CatalogView> views = catalog.Views.ToDictionary(v => v.Name, StringComparer.Ordinal);
        Dictionary<string, string> contextV2ToV1 = new(StringComparer.Ordinal);
        foreach (CatalogContextRule ctx in catalog.Contexts)
        {
            if (ctx.V2Name is { } v2)
            {
                contextV2ToV1[v2] = ctx.RuleId;
            }
        }

        // The B6 subject-relative aggregate v2 paths (round.team.* / round.enemies.* / round.alive.*):
        // legal in an each_player ruleset (subject = the materialized slot) but meaningless at match
        // scope (no subject) — rejected below rather than bound to a phantom slot.
        HashSet<string> subjectRelativePaths =
            new(B6RuleIds.Members.Select(m => m.V2Name), StringComparer.Ordinal);

        // Game scope has no subject: no per-player team, no per-player condition overlay. The stats
        // materialize once (sentinel slot 0, null player name — both unreferenced because the actor
        // binding is suppressed and player.* reads are resolve-rejected).
        _currentPlayerTeam = null;
        _v2ConditionNodeOverlay = null;

        // Shared across every game ruleset in this build (mirrors the per-player template's shared
        // maps): one localLookup seeded from the game node map (so root + game contexts resolve), one
        // resolved-identity dedup + reference-hash space, and one {ruleset}.{stat} map for cross-ruleset
        // (D11a) reads and the configured-output projector.
        Dictionary<string, StateNode> localLookup = new(nodeLookup, StringComparer.OrdinalIgnoreCase);
        Dictionary<string, StateNode> nodesByRuleId = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, ReadOnlyMemory<byte>> statHashesByPath = new(StringComparer.Ordinal);
        MapStatHashSource hashSource = new(statHashesByPath);
        Dictionary<string, StateNode> nodesByHash = new(StringComparer.Ordinal);

        List<StateNode> nodes = [];
        List<StateEdge> edges = [];
        List<GraphEdgeDescriptor> descriptors = [];
        List<LiveComputeRegistration> liveComputes = [];

        foreach (CheckedRuleset rs in rulesets)
        {
            // A match ruleset cannot surface a per-player scoreboard; a highlight is per-round rising-edge
            // attribution the game path does not yet lower. Both fail loud (design gaps, not silent drops).
            if (rs.Show is { Scoreboard.Count: > 0 })
            {
                throw new InvalidOperationException(
                    $"ruleset '{rs.Id.Id}' is for: match but declares show: scoreboard — a scoreboard is "
                    + "per-player; use show: tables (per: match) for a match ruleset.");
            }

            if (rs.Highlights.Count > 0)
            {
                throw new InvalidOperationException(
                    $"ruleset '{rs.Id.Id}' is for: match but declares highlights — game-scoped highlight "
                    + "lowering (match-level timeline attribution) is not yet wired.");
            }

            foreach (CheckedStat stat in rs.Stats)
            {
                RejectSubjectRelativeReads(rs, stat, subjectRelativePaths);
            }

            // Same build order as the per-player template: compute + rate deferred so their sibling/bucket
            // reads are in localLookup + the reference-hash space first.
            foreach (CheckedStat stat in rs.Stats)
            {
                if (stat.Kind is RuleNodeKind.Compute or RuleNodeKind.Rate)
                {
                    continue;
                }

                BuildV2Stat(rs, stat, options, views, contextV2ToV1, GameScopeSlot, GameScopePlayerName,
                    localLookup, nodes, edges, descriptors, nodesByRuleId,
                    statHashesByPath, hashSource, nodesByHash);
            }

            foreach (CheckedStat stat in rs.Stats)
            {
                if (stat.Kind != RuleNodeKind.Rate)
                {
                    continue;
                }

                BuildV2Stat(rs, stat, options, views, contextV2ToV1, GameScopeSlot, GameScopePlayerName,
                    localLookup, nodes, edges, descriptors, nodesByRuleId,
                    statHashesByPath, hashSource, nodesByHash);
            }

            foreach (CheckedStat stat in rs.Stats)
            {
                if (stat.Kind != RuleNodeKind.Compute)
                {
                    continue;
                }

                BuildV2Stat(rs, stat, options, views, contextV2ToV1, GameScopeSlot, GameScopePlayerName,
                    localLookup, nodes, edges, descriptors, nodesByRuleId,
                    statHashesByPath, hashSource, nodesByHash, liveComputes);
            }
        }

        // Push the accumulated nodes/edges onto the graph exactly as a v1 game rule is wired
        // (BuildSingletonRule): every node is snapshot-tracked (allNodes); a logic node also registers
        // with the graph so its rising edge fires; every edge is added and its message type subscribed.
        foreach (StateNode node in nodes)
        {
            allNodes.Add(node);
            switch (node)
            {
                case ConjunctionNode conjunction:
                    graph.AddConjunction(conjunction);
                    break;
                case DisjunctionNode disjunction:
                    graph.AddDisjunction(disjunction);
                    break;
            }
        }

        foreach (StateEdge edge in edges)
        {
            graph.AddEdge(edge);
            relevantTypes.Add(edge.MessageType);
        }

        foreach (LiveComputeRegistration live in liveComputes)
        {
            graph.AddLiveCompute(live.Compute, live.Reads);
        }

        edgeDescriptors.AddRange(descriptors);
        foreach ((string key, StateNode node) in nodesByRuleId)
        {
            gameNodesByRuleId[key] = node;
        }
    }

    /// <summary>
    ///     Loud-fails a <c>for: match</c> stat that reads a subject-relative B6 aggregate
    ///     (<c>round.team.*</c> / <c>round.enemies.*</c> / <c>round.alive.*</c>). These resolve at match
    ///     scope (they sit under the always-present <c>round</c> root) but have no subject — the planner
    ///     would otherwise bind a phantom slot-0 aggregate or drop the read silently. <c>player.*</c>
    ///     reads never reach here (the resolver rejects them with <c>resolve.unknown-root</c> because the
    ///     <c>player</c> root is out of scope for a match ruleset).
    /// </summary>
    private static void RejectSubjectRelativeReads(
        CheckedRuleset rs, CheckedStat stat, HashSet<string> subjectRelativePaths)
    {
        foreach (string read in stat.DeclaredReads)
        {
            if (subjectRelativePaths.Contains(read))
            {
                throw new InvalidOperationException(
                    $"stat '{rs.Id.Id}.{stat.StatId}' reads '{read}', which is subject-relative "
                    + "(the reader's own team / clutch state). A for: match ruleset has no subject — "
                    + "declare for: each_player to read per-player team aggregates.");
            }
        }
    }

    /// <summary>
    ///     Folds the v2 rulesets' entity-provider read set into the v1 <paramref name="matched" /> /
    ///     <paramref name="perPlayerList" /> gating — called from
    ///     <c>Build</c> before the <c>EntityChangeScanner</c> is constructed, so a v2 <c>player.*</c> /
    ///     role-handle read forces its provider into the snapshot set instead of silently gating out.
    /// </summary>
    /// <param name="rulesets">The v2 rulesets whose <see cref="CheckedStat.EntityReads" /> to union.</param>
    /// <param name="matched">The singleton-provider gate list to extend.</param>
    /// <param name="perPlayerList">The per-player-provider gate list to extend.</param>
    private void UnionV2EntityReads(
        IReadOnlyList<CheckedRuleset> rulesets,
        List<IEntityValueProvider> matched,
        List<IPerPlayerEntityValueProvider> perPlayerList)
    {
        if (rulesets.Count == 0)
        {
            return;
        }

        HashSet<string> providerNames = new(StringComparer.Ordinal);
        foreach (CheckedRuleset rs in rulesets)
        {
            foreach (CheckedStat stat in rs.Stats)
            {
                foreach (EntityProviderReference read in stat.EntityReads)
                {
                    providerNames.Add(read.ProviderName);
                }
            }

            foreach (CheckedHighlight highlight in rs.Highlights)
            {
                foreach (EntityProviderReference read in highlight.EntityReads)
                {
                    providerNames.Add(read.ProviderName);
                }
            }
        }

        if (providerNames.Count == 0)
        {
            return;
        }

        if (_entityProviders is not null)
        {
            foreach (IEntityValueProvider provider in _entityProviders.All)
            {
                if (providerNames.Contains(provider.ContextName) && !matched.Contains(provider))
                {
                    matched.Add(provider);
                }
            }
        }

        if (_perPlayerEntityProviders is not null)
        {
            foreach (IPerPlayerEntityValueProvider provider in _perPlayerEntityProviders.All)
            {
                if (providerNames.Contains(provider.Name) && !perPlayerList.Contains(provider))
                {
                    perPlayerList.Add(provider);
                }
            }
        }
    }

    /// <summary>
    ///     Registers a per-player <see cref="PerPlayerNodeTemplate" /> that materializes every
    ///     <c>for: each_player</c> v2 ruleset's stats + highlights for one player. Driven by the
    ///     <see cref="CheckedStat" /> IR.
    /// </summary>
    /// <param name="rulesets">The per-player rulesets.</param>
    /// <param name="options">Planner options (C7 env vs constant lowering).</param>
    /// <param name="graph">The shared graph.</param>
    /// <param name="parentNodeLookup">The game-scope lookup (contexts + enrichment) the templates inherit.</param>
    /// <param name="relevantTypes">The message-type subscription set to extend with v2 concrete events.</param>
    /// <param name="perPlayerContextRules">
    ///     The per-player CONTEXT RuleDefs (alive/survived/traded — Scope==PerPlayer) v1 also builds.
    ///     Materialized per slot via the shared <see cref="BuildPerPlayerRuleNode" />
    ///     helper into this template's <c>localLookup</c>, keyed by their v1 rule id — so a v2 <c>when:</c>
    ///     read of <c>player.survived</c> / <c>.traded</c> resolves through <c>contextV2ToV1</c> to the
    ///     same event/enrichment-driven nodes v1 uses. Duplicating these context nodes across the two
    ///     templates is intentional: each slot materializes its own copy, both driven by the same events
    ///     deterministically, so the v2 flag reads a byte-identical value (redundant compute, no
    ///     divergence — contexts aren't columns, so invisible in output).
    /// </param>
    private void BuildV2PerPlayerTemplate(
        IReadOnlyList<CheckedRuleset> rulesets,
        RulesetCompilerOptions options,
        StateGraph graph,
        Dictionary<string, StateNode> parentNodeLookup,
        HashSet<Type> relevantTypes,
        IReadOnlyList<RuleDef> perPlayerContextRules)
    {
        CatalogRoot catalog = CatalogResource.Load();
        Dictionary<string, CatalogView> views = catalog.Views.ToDictionary(v => v.Name, StringComparer.Ordinal);
        Dictionary<string, string> contextV2ToV1 = new(StringComparer.Ordinal);
        foreach (CatalogContextRule ctx in catalog.Contexts)
        {
            if (ctx.V2Name is { } v2)
            {
                contextV2ToV1[v2] = ctx.RuleId;
            }
        }

        graph.AddPerPlayerTemplate(new PerPlayerNodeTemplate((slot, _, playerName, demo) =>
        {
            Dictionary<string, StateNode> localLookup = new(parentNodeLookup, StringComparer.OrdinalIgnoreCase);
            List<StateNode> nodes = [];
            List<StateEdge> edges = [];
            List<GraphEdgeDescriptor> descriptors = [];
            List<(StateNode Trigger, Action Action, StateNode? Writes)> risingEdgeActions = [];
            List<(StateNode Trigger, Action<int, int> Action, StateNode? Writes)> contextRisingEdgeActions = [];
            List<LiveComputeRegistration> liveComputes = [];
            List<PerPlayerColumnAssignment> columns = [];
            Dictionary<string, StateNode> nodesByRuleId = new(StringComparer.OrdinalIgnoreCase);

            // Resolved-identity dedup: hash each stat node and share
            // one StateNode for hash-equal stats (across all rulesets in this template). byPath feeds
            // the hasher's stat-reference row (spec §6 row 6); byHash is the dedup key.
            Dictionary<string, ReadOnlyMemory<byte>> statHashesByPath = new(StringComparer.Ordinal);
            MapStatHashSource hashSource = new(statHashesByPath);
            Dictionary<string, StateNode> nodesByHash = new(StringComparer.Ordinal);

            _currentPlayerTeam = demo?.Players.TryGetValue(slot, out PlayerInfo? info) == true ? info.Team : null;

            // Per-player context bridge: materialize the same per-player CONTEXT
            // nodes (alive/survived/traded) v1 builds into THIS template's localLookup, keyed by v1 rule
            // id, via the shared BuildPerPlayerRuleNode helper. contextV2ToV1 then resolves a v2 when:
            // read of player.survived / .traded to these nodes (ResolveWhenRef). Build ALL contexts (not
            // just the referenced ones) — simplest and future-proof. Fresh defer lists stay empty: the
            // contexts are event/enrichment-driven bools, nothing defers. Runs before the stats loop so
            // survived's parent (alive) and traded's enrichment parent are already in localLookup.
            foreach (RuleDef ctxRule in perPlayerContextRules)
            {
                BuildPerPlayerRuleNode(ctxRule, slot, playerName, graph, parentNodeLookup,
                    localLookup, nodes, edges, descriptors);
            }

            // B6 team aggregates (round.team.* / round.enemies.* / round.alive.*): inject the
            // subject-relative aggregate nodes into THIS slot's localLookup keyed by their v1 rule id,
            // so contextV2ToV1 resolves a v2 when:/while:/compute: read of round.team.alive etc. to
            // them (ResolveWhenRef / ResolveGateSource / the compute remap). Built unconditionally per
            // slot (like the per-player contexts above) — cheap pull-nodes with no edges. Economy
            // (round.team.equipment / round.enemies.equipment) is added by the freeze-end maintenance
            // pass below because it needs a written node + the entity digest.
            InjectB6AliveAggregates(slot, playerName, localLookup, nodes);
            InjectB6EconomyAggregates(slot, playerName, localLookup, nodes, edges);

            // Gap G1 (event-gated per-player aggregate reads): expose THIS slot's per-player context
            // (survived/traded/alive) and B6 aggregate nodes to the condition/value compiler under
            // their v1 rule ids, so a where:-condition read of player.survived / round.enemies.alive —
            // lowered to the bare rule id by V1ExpressionWriter — binds the subject's node. Cleared in
            // the finally so no slot's binding leaks into the next (sequential-materialize contract).
            _v2ConditionNodeOverlay = BuildV2ConditionOverlay(localLookup, perPlayerContextRules);
            try
            {
                foreach (CheckedRuleset rs in rulesets)
                {
                    foreach (CheckedStat stat in rs.Stats)
                    {
                        // compute: builds LAST (mirror v1's deferredExpressions in BuildPerPlayerTemplate):
                        // a compute formula may read a highlight's match-scoped .count (e.g. kast_pct reads
                        // the kast highlight's .count == v1 kast_rounds), which is materialized into
                        // localLookup only by the highlights loop below. Deferring keeps that read resolvable.
                        if (stat.Kind == RuleNodeKind.Compute)
                        {
                            continue;
                        }

                        // rate: (G3) is derived from two sibling buckets — defer it so both bucket nodes are in
                        // localLookup (and their hashes in statHashesByPath, for the row-6 reference embedding)
                        // before the KeyedRatioNode is built, regardless of declaration order.
                        if (stat.Kind == RuleNodeKind.Rate)
                        {
                            continue;
                        }

                        BuildV2Stat(rs, stat, options, views, contextV2ToV1, slot, playerName,
                            localLookup, nodes, edges, descriptors, nodesByRuleId,
                            statHashesByPath, hashSource, nodesByHash);
                    }

                    // rate: pass — every bucket is now built, so each KeyedRatioNode can pull its of:/per:
                    // nodes from localLookup and its of/per hashes resolve in the reference row.
                    foreach (CheckedStat stat in rs.Stats)
                    {
                        if (stat.Kind != RuleNodeKind.Rate)
                        {
                            continue;
                        }

                        BuildV2Stat(rs, stat, options, views, contextV2ToV1, slot, playerName,
                            localLookup, nodes, edges, descriptors, nodesByRuleId,
                            statHashesByPath, hashSource, nodesByHash);
                    }

                    foreach (CheckedHighlight highlight in rs.Highlights)
                    {
                        BuildV2Highlight(rs, highlight, slot, playerName, contextV2ToV1, localLookup, nodes, edges,
                            descriptors, risingEdgeActions, contextRisingEdgeActions, graph.HighlightSink,
                            nodesByRuleId);

                        // Register the highlight's resolved-identity hash so a deferred compute reading its
                        // `<id>.count` (whose ResolvedReference.StatPath is the highlight id) resolves in the
                        // hash source. A highlight's bare reference is its per-round fired bool, so hash it as
                        // a Flag over its when-conjunction + scope; its when: siblings were hashed by the stats
                        // pass above, so the reference row resolves. v2-only — the v1 hashing path never runs.
                        RuleNodeDescriptor highlightDescriptor = new(
                            highlight.HighlightId, RuleNodeKind.Flag, RulesType.Bool, highlight.Scope,
                            [], highlight.When);
                        ReadOnlyMemory<byte> highlightHash =
                            Rules.Hashing.RuleHasher.ComputeHash(highlightDescriptor, hashSource);
                        // The highlight's `.count` member is itself a stat symbol on HighlightScopeSymbol, so a
                        // `<id>.count` read resolves with StatPath == "<id>.count"; register both the bare and
                        // qualified spellings of the highlight id AND its count-node id to cover every form.
                        statHashesByPath[highlight.HighlightId] = highlightHash;
                        statHashesByPath[highlight.CountNodeId] = highlightHash;
                        statHashesByPath[$"{rs.Id.Id}.{highlight.HighlightId}"] = highlightHash;
                        statHashesByPath[$"{rs.Id.Id}.{highlight.CountNodeId}"] = highlightHash;
                    }

                    // Deferred compute: pass overs. Every stat/highlight this ruleset defines now lives in
                    // localLookup, so a compute reading a sibling stat, a highlight .count, or a context
                    // resolves (Compute case remaps v2 paths to localLookup keys).
                    foreach (CheckedStat stat in rs.Stats)
                    {
                        if (stat.Kind != RuleNodeKind.Compute)
                        {
                            continue;
                        }

                        BuildV2Stat(rs, stat, options, views, contextV2ToV1, slot, playerName,
                            localLookup, nodes, edges, descriptors, nodesByRuleId,
                            statHashesByPath, hashSource, nodesByHash, liveComputes);
                    }

                    // show: scoreboard -> per-player column projection. Run after the
                    // ruleset's stats + highlights are registered, resolving each entry against this
                    // ruleset's qualified {ruleset}.{stat} keys (so a bare-id collision between rulesets
                    // in the same template can't cross-bind a column).
                    columns.AddRange(ShowLowering.LowerScoreboard(rs, nodesByRuleId));
                }
            }
            finally
            {
                _v2ConditionNodeOverlay = null;
            }

            // A2 golden drift-guard seam: expose this materialization's canonical stat/highlight
            // hash map (reference only — zero copy). Hashes are slot-independent (the preimage
            // never reads slot/playerName), so any materialization's map is THE map for this
            // (tickRate, profile) build; the fingerprint helper's golden test compares against it.
            _lastMaterializedV2StatHashes = statHashesByPath;

            return new PerPlayerNodeTemplate.MaterializedPlayer(
                slot, playerName, nodes, edges, columns, descriptors,
                risingEdgeActions.Count > 0 ? risingEdgeActions : null,
                NodesByRuleId: nodesByRuleId,
                LiveComputes: liveComputes.Count > 0 ? liveComputes : null,
                ContextRisingEdgeActions: contextRisingEdgeActions.Count > 0 ? contextRisingEdgeActions : null);
        }));

        // Subscribe the graph to every v2 concrete event (the evaluator short-circuits others).
        foreach (CheckedRuleset rs in rulesets)
        {
            foreach (CheckedStat stat in rs.Stats)
            {
                foreach (string ev in stat.ConcreteEvents)
                {
                    if (_registry.TryResolve(ev, out Type type))
                    {
                        relevantTypes.Add(type);
                    }
                }
            }
        }
    }

    /// <summary>Lowers one checked stat to its node + edges (the construct→graph lowering table).</summary>
    private void BuildV2Stat(
        CheckedRuleset rs, CheckedStat stat, RulesetCompilerOptions options,
        Dictionary<string, CatalogView> views, Dictionary<string, string> contextV2ToV1,
        int slot, string playerName,
        Dictionary<string, StateNode> localLookup, List<StateNode> nodes,
        List<StateEdge> edges, List<GraphEdgeDescriptor> descriptors,
        Dictionary<string, StateNode> nodesByRuleId,
        Dictionary<string, ReadOnlyMemory<byte>> statHashesByPath, MapStatHashSource hashSource,
        Dictionary<string, StateNode> nodesByHash,
        List<LiveComputeRegistration>? liveComputes = null)
    {
        // Resolved-identity dedup (obligation 3): a hash-equal stat shares the existing node.
        byte[] hash = V2StatHasher.Hash(stat, hashSource);
        statHashesByPath[stat.StatId] = hash;
        statHashesByPath[$"{rs.Id.Id}.{stat.StatId}"] = hash;
        string hex = Convert.ToHexString(hash);
        if (nodesByHash.TryGetValue(hex, out StateNode? shared))
        {
            localLookup[stat.StatId] = shared;
            nodesByRuleId[$"{rs.Id.Id}.{stat.StatId}"] = shared;
            return;
        }

        // Entity reads at a SETTLE site — compute: (round-end / live) and flag: when: (flag-eval) — have
        // no event frame, so they cannot reach the fire-time ExpressionCompiler.CompileEventCondition
        // entity seam that where:/value-selector/while: reads use. Materialize the subject's entity value
        // as an always-active EntityValuePullNode (B6-style) registered in localLookup under the read's
        // path, so the compute remap and when-lowering below resolve it as an ordinary graph-node value.
        // Fire-time kinds are untouched — they keep the RewriteEntityReads → CompileEventCondition path.
        EnsureSettleEntityPullNodes(stat, slot, playerName, localLookup, nodes);

        bool roundScoped = stat.Scope is ScopeAxis.PlayerRound or ScopeAxis.Round;
        string? condition = RewriteEntityReads(BuildConditionString(rs, stat, views, contextV2ToV1), stat);

        // While: gate. An entity-bearing single-comparison while: (e.g. `while: player.health > 50`) has
        // NO graph node to gate on — it is a subject-slot pre-frame entity-provider read, not a
        // sibling/context node — so the node-predicate path (ResolveGateSource -> LowerWhenTerm ->
        // CompileNodeBoolExpression) loud-fails ("does not reference a sibling stat/context"): that path
        // resolves references against localLookup NODES and has no entity-provider seam. Fold such a gate
        // into the fire-time event condition instead, the SAME ExpressionCompiler.CompileEventCondition
        // seam (with _entityScanner / _perPlayerEntityProviders) a where: entity read already uses to
        // resolve the subject's entity value — so the while: read binds to the subject slot exactly as
        // where: does. where: and while: both gate the same game-event edge and both must hold, so ANDing
        // them is semantically exact. Only a single entity-bearing comparison is folded, and only for stat
        // kinds that actually thread `condition` into a game-event edge (so the gate is never silently
        // dropped for a condition-ignoring kind such as tally:); a bare-context / sibling while:, a
        // non-entity comparison, and a compound while: all keep the unchanged ResolveGateSource path
        // (parent-as-source / loud-fail). v2-only: the pure-v1 lowering in RuleChainBuilder.cs is
        // untouched, and a stat with no entity-bearing while: takes the byte-identical original path.
        StateNode source;
        IConditionalEdge? sourceGate;
        if (KindThreadsEventCondition(stat)
            && stat.WhileGate is { Root: BinaryNode whileCmp } && IsComparison(whileCmp.Operator)
            && TryFoldEntityWhileGate(whileCmp, stat, out string? foldedWhile))
        {
            condition = condition is null ? foldedWhile : $"({condition}) && ({foldedWhile})";
            source = localLookup["root"];
            sourceGate = null;
        }
        else
        {
            (source, sourceGate) = ResolveGateSource(stat.WhileGate, contextV2ToV1, localLookup);
        }

        // Read-aware ordering (A1): the checked stat's resolved read set,
        // mapped to the sibling/context/enrichment nodes in scope. The v2 game-event edge carries
        // it so the evaluator's read-aware topological sort orders this edge after every declared
        // node's writer within a dispatch slot. Reads that are event fields / literals / player.*
        // (no graph node) drop out; an empty set is v1-identical (null DeclaredReads).
        List<StateNode>? declaredReads =
            ResolveDeclaredReadNodes(stat.DeclaredReads, localLookup, contextV2ToV1);

        switch (stat.Kind)
        {
            case RuleNodeKind.Count when stat.ConcreteEvents.Count == 0:
                // count: <flag> — a counter incremented on the rising edge of a sibling flag
                // (wrap the GenericBoolNode in a single-input
                // conjunction + AddRisingEdgeAction). Not exercised by the pilot (which is
                // count-on-trigger); loud-fail rather than build a dead counter with no increment.
                throw new InvalidOperationException(
                    $"stat '{stat.StatId}' is count-on-flag (no concrete events), which the "
                    + "planner does not yet lower — use count-on-trigger.");

            case RuleNodeKind.Count:
            {
                StateNode node = MakeValueNode(stat.StatId, RulesType.Int, roundScoped, playerName);
                RegisterV2Node(node, rs, stat.StatId, localLookup, nodes, nodesByRuleId);

                // Approach-1 clip-start companion: a value node seeded to RecordFirstEventTickEdge.Sentinel
                // that a parallel write-once edge stamps with the FIRST contributing event's frame-clock
                // tick (same events + same condition as the increment). A count highlight fires at the
                // COMPLETING event's tick, so a multi-kill longer than the reel lead-in loses its early
                // kills; the highlight emission closure reads this node (by stat id) so the clip window can
                // reach back to the first kill. Scoped like the count itself (per: round → round-scoped
                // reset back to the sentinel each round). Registered ONLY under the ruleset's private
                // sibling key `{statId}.__first_tick` in localLookup — deliberately NOT in nodesByRuleId,
                // which flows into the qualified/configured-output node map (a `__first_tick` must never
                // surface as a resolvable stat or an output column). Same-ruleset highlights (the
                // multikill case) read it from localLookup by their when: stat id; a cross-ruleset count
                // read simply falls back to null (the safe lead-in-only behavior).
                ValueNode<int> firstTick = MakeFirstTickNode(
                    $"__first_tick_{rs.Id.Id}_{stat.StatId}", roundScoped, playerName);
                localLookup[$"{stat.StatId}.__first_tick"] = firstTick;
                nodes.Add(firstTick);

                foreach (string ev in stat.ConcreteEvents)
                {
                    edges.Add(CreateV2TriggerEdge(ev, stat.StatId, TriggerAction.Increment, null, condition,
                        source, node, slot, playerName, null, declaredReads, sourceGate));
                    descriptors.Add(new GraphEdgeDescriptor(source, node, ev, EdgeEffect.SetValue, condition));

                    // Game-event triggers alone expose evt.GameTick; a net-triggered count records no
                    // first tick (→ null ClipStartTick, the safe lead-in-only fallback).
                    StateEdge? tickEdge = TryCreateFirstTickRecordEdge(ev, stat.StatId, condition, source,
                        firstTick, slot);
                    if (tickEdge is not null)
                    {
                        edges.Add(tickEdge);
                    }
                }

                break;
            }

            case RuleNodeKind.Sum:
            {
                StateNode node = MakeValueNode(stat.StatId, stat.ValueType, roundScoped, playerName);
                RegisterV2Node(node, rs, stat.StatId, localLookup, nodes, nodesByRuleId);
                string element = RewriteEntityReads(V1ExpressionWriter.Write(stat.ValueSelector!.Root), stat)!;
                foreach (string ev in stat.ConcreteEvents)
                {
                    edges.Add(CreateV2TriggerEdge(ev, stat.StatId, TriggerAction.Set, $"node.value + ({element})",
                        condition, source, node, slot, playerName, null, declaredReads, sourceGate));
                    descriptors.Add(new GraphEdgeDescriptor(source, node, ev, EdgeEffect.SetValue, condition));
                }

                break;
            }

            case RuleNodeKind.Capture when stat.Keep == KeepKind.List:
            {
                RequireNoWhileValueGate(sourceGate, stat.StatId, "capture: keep list");
                ValueNode<IReadOnlyList<int>> node = MakeIntListNode(stat.StatId, roundScoped, playerName);
                RegisterV2Node(node, rs, stat.StatId, localLookup, nodes, nodesByRuleId);
                string element = RewriteEntityReads(V1ExpressionWriter.Write(stat.ValueSelector!.Root), stat)!;
                foreach (string ev in stat.ConcreteEvents)
                {
                    EventRegistration reg = RequireGameEvent(ev, stat.StatId);
                    edges.Add(CreateV2ListAppendEdge(reg, source, node, element, condition, slot));
                    descriptors.Add(new GraphEdgeDescriptor(source, node, ev, EdgeEffect.SetValue, condition));
                }

                break;
            }

            case RuleNodeKind.Capture when stat.Keep is KeepKind.Min or KeepKind.Max:
            {
                // Scalar min/max reduction over the per: window (pre-freeze gap G2). Mirrors the bucket
                // min/max reducer (KeyedCounterNode.Combine): an UNSEEN window takes the first value —
                // never min/max against the value node's phantom 0. ONE shared `__seen_` flag across all
                // concrete events (so a multi-event view reduces across them rather than each event type
                // re-initializing); it is round-scoped iff the node is, so it resets with the extremum
                // each round (per: round) or persists for the match (per: match).
                RequireNoWhileValueGate(sourceGate, stat.StatId, "capture: keep min/max");
                bool keepMax = stat.Keep == KeepKind.Max;
                StateNode node = MakeValueNode(stat.StatId, stat.ValueType, roundScoped, playerName);
                RegisterV2Node(node, rs, stat.StatId, localLookup, nodes, nodesByRuleId);
                string element = RewriteEntityReads(V1ExpressionWriter.Write(stat.ValueSelector!.Root), stat)!;
                BoolNode seen = roundScoped
                    ? new GenericRoundScopedBoolNode($"__seen_{rs.Id.Id}_{stat.StatId}", false, playerName)
                    : new GenericBoolNode($"__seen_{rs.Id.Id}_{stat.StatId}", playerName);
                nodes.Add(seen);
                foreach (string ev in stat.ConcreteEvents)
                {
                    EventRegistration reg = RequireGameEvent(ev, stat.StatId);
                    edges.Add(CreateV2ScalarReduceEdge(reg, source, node, stat.ValueType, element, condition,
                        seen, keepMax, slot, declaredReads));
                    descriptors.Add(new GraphEdgeDescriptor(source, node, ev, EdgeEffect.SetValue, condition));
                }

                break;
            }

            case RuleNodeKind.Capture:
            {
                // First/last scalar capture. `first` = write-once via a round-scoped guard; `last`
                // (v1 `value` semantics, the default) = plain set.
                StateNode node = MakeValueNode(stat.StatId, stat.ValueType, roundScoped, playerName);
                RegisterV2Node(node, rs, stat.StatId, localLookup, nodes, nodesByRuleId);
                string valueExpr = RewriteEntityReads(V1ExpressionWriter.Write(stat.ValueSelector!.Root), stat)!;
                foreach (string ev in stat.ConcreteEvents)
                {
                    BoolNode? guard = null;
                    if (stat.Keep == KeepKind.First)
                    {
                        guard = new GenericRoundScopedBoolNode($"__seen_{rs.Id.Id}_{stat.StatId}", false, playerName);
                        nodes.Add(guard);
                    }

                    edges.Add(CreateV2TriggerEdge(ev, stat.StatId, TriggerAction.Set, valueExpr, condition,
                        source, node, slot, playerName, guard, declaredReads, sourceGate));
                    descriptors.Add(new GraphEdgeDescriptor(source, node, ev, EdgeEffect.SetValue, condition));
                }

                break;
            }

            case RuleNodeKind.Compute:
            {
                Dictionary<string, object> exprLookup =
                    localLookup.ToDictionary(kv => kv.Key, kv => (object)kv.Value, StringComparer.OrdinalIgnoreCase);
                string formula = V1ExpressionWriter.Write(stat.TriggerCondition!.Root);

                // The compute's live dependency set — every graph node its formula actually reads,
                // collected from the SAME resolution that binds exprLookup (so cross-ruleset
                // nodesByRuleId reads are included, which ResolveDeclaredReadNodes alone misses). Only
                // consumed by the live: branch below; for a round-end compute it is inert.
                List<StateNode> liveReadNodes = [];

                void AddLiveRead(StateNode node)
                {
                    if (!liveReadNodes.Contains(node))
                    {
                        liveReadNodes.Add(node);
                    }
                }

                // A v2 compute formula reads siblings/contexts/highlight-counts by their v2 dotted paths
                // (round.number, kast.count), but the node-expression compiler tokenizes on '.' and
                // resolves against v1 localLookup keys (round_number, kast.count). Remap each dotted
                // declared read to a bare alias bound to its resolved node, so the compiler resolves it
                // exactly as v1 resolved kast_rounds/round_number. (v2-only path; the v1 compute lowering
                // in RuleChainBuilder.cs is untouched, keeping the pure-v1 path byte-identical.)
                foreach (string readPath in stat.DeclaredReads)
                {
                    if (!readPath.Contains('.', StringComparison.Ordinal))
                    {
                        // A bare sibling/context id already resolves in exprLookup; capture its node for
                        // the live dependency set.
                        if (localLookup.TryGetValue(readPath, out StateNode? bareNode))
                        {
                            AddLiveRead(bareNode);
                        }

                        continue;
                    }

                    string lookupKey = contextV2ToV1.TryGetValue(readPath, out string? v1Name) ? v1Name : readPath;
                    // A within-ruleset dotted read (round.number, a highlight's <id>.count) resolves in
                    // localLookup; a D11a cross-ruleset {ruleset}.{stat} read resolves in nodesByRuleId,
                    // where every ruleset registers its nodes under the qualified key (the referenced
                    // ruleset built earlier in the shared template, so its node is already present).
                    if (!localLookup.TryGetValue(lookupKey, out StateNode? readNode)
                        && !nodesByRuleId.TryGetValue(readPath, out readNode))
                    {
                        continue; // not a graph-node read (leave verbatim; the compiler reports it)
                    }

                    AddLiveRead(readNode);
                    string alias = lookupKey.Replace(".", "_", StringComparison.Ordinal);
                    exprLookup[alias] = readNode;
                    formula = ReplaceWholeIdentifier(formula, readPath, alias);
                }

                Func<double> computeFunc = ExpressionCompiler.CompileNodeExpression(formula, exprLookup);
                // Per-stat display format: the compute's optional format: (v1 parity), default F1 when
                // unset. Presentation only — outside node identity (the hasher never reads stat.Format).
                ComputedStatNode computed = new(stat.StatId, playerName, computeFunc, stat.Format ?? "F1");
                RegisterV2Node(computed, rs, stat.StatId, localLookup, nodes, nodesByRuleId);

                if (stat.Live)
                {
                    // Opt-in live compute: recompute LIVE as the declared reads go dirty (the evaluator's
                    // dirty-settle interleave), instead of a round-end ComputeOnRoundEndEdge. The read set
                    // is liveReadNodes (the compute's actual graph-node reads). No round-end edge is
                    // emitted — the live recompute subsumes it (it also fires on the round reset that
                    // dirties its round-scoped reads).
                    liveComputes?.Add(new LiveComputeRegistration(computed, liveReadNodes));
                }
                else
                {
                    LogicalEventBinding? roundEnd = _logicalResolver.Resolve("round_end");
                    if (roundEnd is not null)
                    {
                        foreach (string concreteEvent in roundEnd.ConcreteEventNames)
                        {
                            if (_registry.TryResolve(concreteEvent, out Type? eventType))
                            {
                                edges.Add(new ComputeOnRoundEndEdge(localLookup["root"], [computed], eventType));
                            }
                        }
                    }
                }

                break;
            }

            case RuleNodeKind.Flag when stat.ConcreteEvents.Count > 0:
            {
                // flag: true + on: — a triggered bool set on the event (round-scoped variant
                // auto-resets via IRoundScopedNode; no explicit reset edge needed).
                BoolNode boolNode = roundScoped
                    ? new GenericRoundScopedBoolNode(stat.StatId, false, playerName)
                    : new GenericBoolNode(stat.StatId, playerName);
                RegisterV2Node(boolNode, rs, stat.StatId, localLookup, nodes, nodesByRuleId);
                foreach (string ev in stat.ConcreteEvents)
                {
                    edges.Add(CreateV2TriggerEdge(ev, stat.StatId, TriggerAction.Activate, null, condition,
                        source, boolNode, slot, playerName, null, declaredReads, sourceGate));
                    descriptors.Add(new GraphEdgeDescriptor(source, boolNode, ev, EdgeEffect.Activate, condition));
                }

                break;
            }

            case RuleNodeKind.Flag:
            {
                // flag: when: <expr> — an auto-activate logic node over sibling stats/contexts (same
                // mechanism as a highlight's when-node, minus the timeline/count). A pure top-level OR
                // lowers to a DisjunctionNode (the exact shape v1 builds for parents: {mode: any}); an
                // AND / single term lowers to a ConjunctionNode. A term reading >1 sibling in one
                // predicate (a + b > 5) becomes a single A2 multi-source edge.
                (bool isDisjunction, List<IConditionalEdge> inputs) =
                    LowerWhenPredicate(stat.TriggerCondition!.Root, localLookup, contextV2ToV1);
                if (inputs.Count == 0)
                {
                    throw new InvalidOperationException(
                        $"flag '{stat.StatId}' when: read no sibling stat/context the planner could gate on.");
                }

                BoolNode logic = isDisjunction
                    ? new DisjunctionNode(stat.StatId, [.. inputs])
                    : new ConjunctionNode(stat.StatId, [.. inputs]);
                RegisterV2Node(logic, rs, stat.StatId, localLookup, nodes, nodesByRuleId);
                if (roundScoped)
                {
                    edges.Add(new RoundScopedLogicNodeReset(logic));
                }

                // A when: reading a writer-less EntityValuePullNode (settle-site entity read) has no event
                // that writes its source, so the evaluator's logic-recompute index would never bucket this
                // flag under any message — it would evaluate once at init and freeze. Drive a round-end
                // settle recompute (the documented flag-eval settle point) so the flag reflects the
                // subject's round-end entity value. No-op when the when: reads no entity pull-node.
                AddEntityFlagSettleEdges(inputs, localLookup, edges);

                EdgeEffect logicEffect = isDisjunction ? EdgeEffect.Disjunction : EdgeEffect.Conjunction;
                foreach (IConditionalEdge input in inputs)
                {
                    descriptors.Add(new GraphEdgeDescriptor(input.Source, logic, "", logicEffect, input.ConditionLabel));
                }

                break;
            }

            case RuleNodeKind.Streak:
            {
                // streak: <trigger> — a windowed streak counter driven by the trigger's events,
                // reusing the v1 WindowedStreakNode + WindowedStreakEdge (round finalization is the
                // node's own IRoundScopedNode contract; the count accumulates across the match).
                RequireNoWhileValueGate(sourceGate, stat.StatId, "streak:");
                WindowedStreakNode streakNode = new(stat.StatId, playerName,
                    stat.StreakWindow ?? 640, stat.StreakMinStreak ?? 2);
                RegisterV2Node(streakNode, rs, stat.StatId, localLookup, nodes, nodesByRuleId);
                foreach (string ev in stat.ConcreteEvents)
                {
                    EventRegistration reg = RequireGameEvent(ev, stat.StatId);
                    Delegate? cond = condition is not null
                        ? ExpressionCompiler.CompileEventCondition(condition, reg.EventType, reg.Fields, slot,
                            ConditionNodes, _currentPlayerTeam, _playerContextIndex, _entityScanner,
                            _perPlayerEntityProviders, parameterType: typeof(GameEvent))
                        : null;
                    edges.Add(new WindowedStreakEdge(source, streakNode, reg.EventType, cond));
                    descriptors.Add(new GraphEdgeDescriptor(source, streakNode, ev, EdgeEffect.SetValue, condition));
                }

                break;
            }

            case RuleNodeKind.Burst:
            {
                // burst: <trigger> — a windowed multi-kill PULSE (bool). Unlike streak: (an int counter
                // finalized at round end / on a window break), this goes true for ONE dispatch on the
                // event that completes a burst (min_streak matches within a rolling window), so a
                // highlight's when: fires exactly once at the completing kill's tick. The node is
                // transient (auto-clears each dispatch) AND round-scoped (clears the ring at round
                // boundaries) — the two Reset()s are distinct explicit interface impls. Reuses the
                // streak window/min_streak args (folded to ticks in the resolver).
                RequireNoWhileValueGate(sourceGate, stat.StatId, "burst:");
                WindowedHighlightPulseNode pulseNode = new(stat.StatId, playerName,
                    stat.StreakWindow ?? 640, stat.StreakMinStreak ?? 2);
                RegisterV2Node(pulseNode, rs, stat.StatId, localLookup, nodes, nodesByRuleId);
                foreach (string ev in stat.ConcreteEvents)
                {
                    EventRegistration reg = RequireGameEvent(ev, stat.StatId);
                    Delegate? cond = condition is not null
                        ? ExpressionCompiler.CompileEventCondition(condition, reg.EventType, reg.Fields, slot,
                            ConditionNodes, _currentPlayerTeam, _playerContextIndex, _entityScanner,
                            _perPlayerEntityProviders, parameterType: typeof(GameEvent))
                        : null;
                    edges.Add(new WindowedHighlightPulseEdge(source, pulseNode, reg.EventType, cond));
                    descriptors.Add(new GraphEdgeDescriptor(source, pulseNode, ev, EdgeEffect.SetValue, condition));
                }

                break;
            }

            case RuleNodeKind.Bucket:
            {
                // bucket: <trigger> + key: — a per-key counter (basic increment/count), reusing the v1
                // KeyedCounterNode + KeyedCounterEdge. The keyed node is match-scoped and
                // snapshot-excluded (the resolver forces per: match); the actor/match/baked gating rides
                // the condition (edge source is root), mirroring the KeyedCounterEngineTests gating.
                RequireNoWhileValueGate(sourceGate, stat.StatId, "bucket:");
                KeyedCounterNode keyedNode = new(stat.StatId, stat.Label ?? stat.StatId, playerName,
                    MapKeyedReduceMode(stat.BucketReducer));
                RegisterV2Node(keyedNode, rs, stat.StatId, localLookup, nodes, nodesByRuleId);
                IReadOnlyList<string> keyParts = stat.BucketKeyParts!;
                // A summing bucket (value: present) rides the ValueSelector slot; render it to v1
                // grammar exactly like sum:/capture: render their element, so the planner feeds
                // CompileEventValueSelector below. Absent → null → basic count (+1 per event).
                string? bucketValueExpr = stat.ValueSelector is { } vs ? RewriteEntityReads(V1ExpressionWriter.Write(vs.Root), stat) : null;
                foreach (string ev in stat.ConcreteEvents)
                {
                    EventRegistration reg = RequireGameEvent(ev, stat.StatId);
                    edges.Add(CreateV2KeyedCounterEdge(reg, source, keyedNode, keyParts, bucketValueExpr, condition, slot));
                    descriptors.Add(new GraphEdgeDescriptor(source, keyedNode, ev, EdgeEffect.SetValue, condition));
                }

                break;
            }

            case RuleNodeKind.Tally:
            {
                RequireNoWhileValueGate(sourceGate, stat.StatId, "tally:");
                BuildV2Tally(rs, stat, playerName, localLookup, nodes, edges, descriptors, nodesByRuleId);
                break;
            }

            case RuleNodeKind.Rate:
            {
                // rate: of / per (G3) — a per-key ratio over two sibling KeyedCounterNodes. Derived: no
                // trigger edge, no descriptor (the KeyedRatioNode divides on read). Pull both bucket nodes
                // from localLookup by their resolved ids (the rate pass runs after every bucket is built).
                if (!localLookup.TryGetValue(stat.RateOf!, out StateNode? ofNode)
                    || ofNode is not KeyedCounterNode ofBucket)
                {
                    throw new InvalidOperationException(
                        $"rate '{stat.StatId}' numerator '{stat.RateOf}' is not a bucket node in scope.");
                }

                if (!localLookup.TryGetValue(stat.RatePer!, out StateNode? perNode)
                    || perNode is not KeyedCounterNode perBucket)
                {
                    throw new InvalidOperationException(
                        $"rate '{stat.StatId}' denominator '{stat.RatePer}' is not a bucket node in scope.");
                }

                KeyedRatioNode ratioNode = new(stat.StatId, stat.Label ?? stat.StatId, ofBucket, perBucket, playerName);
                RegisterV2Node(ratioNode, rs, stat.StatId, localLookup, nodes, nodesByRuleId);
                break;
            }

            default:
                throw new InvalidOperationException(
                    $"stat '{stat.StatId}' has kind {stat.Kind}, which the planner does not lower.");
        }

        // Record this stat's node under its hash so a later hash-equal stat dedups onto it.
        if (localLookup.TryGetValue(stat.StatId, out StateNode? built))
        {
            nodesByHash[hex] = built;
        }
    }

    /// <summary>
    ///     Lowers a <c>tally:</c> stat: reuses the v1
    ///     <see cref="ThresholdTallyEdge" /> so it evaluates byte-identically to the v1
    ///     <c>threshold_tally</c> rule. The stat produces no node under its own id — like v1, its
    ///     outputs are the threshold <em>target</em> counter nodes (match-scoped
    ///     <see cref="GenericValueNode{Int32}" />, auto-created if not already a sibling), and it
    ///     fires on the profile's round-end events (highest-min bucket wins, first-wins-per-round
    ///     guard for multi-event round ends). The source value is the checked value-selector
    ///     reference — a sibling <see cref="ValueNode{Int32}" /> resolved from the local lookup.
    /// </summary>
    private void BuildV2Tally(
        CheckedRuleset rs, CheckedStat stat, string playerName,
        Dictionary<string, StateNode> localLookup, List<StateNode> nodes,
        List<StateEdge> edges, List<GraphEdgeDescriptor> descriptors,
        Dictionary<string, StateNode> nodesByRuleId)
    {
        string sourceId = V1ExpressionWriter.Write(stat.ValueSelector!.Root);
        if (!localLookup.TryGetValue(sourceId, out StateNode? sourceNode) || sourceNode is not ValueNode<int> intSource)
        {
            throw new InvalidOperationException(
                $"tally '{stat.StatId}' reads source '{sourceId}', which is not an int value node in scope — "
                + "declare the source stat before the tally (the planner builds stats in document order).");
        }

        // Target counters, highest threshold first (a 5-kill round only bumps the 5K bucket). A
        // target that isn't already a sibling node is auto-created match-scoped, exactly as v1 does.
        (int Threshold, ValueNode<int> Target)[] thresholds = stat.TallyThresholds!
            .OrderByDescending(t => t.Min)
            .Select(t =>
            {
                if (!localLookup.TryGetValue(t.Target, out StateNode? targetNode))
                {
                    GenericValueNode<int> created = new(t.Target, playerName);
                    created.SetValue(0);
                    RegisterV2Node(created, rs, t.Target, localLookup, nodes, nodesByRuleId);
                    targetNode = created;
                }

                return targetNode is ValueNode<int> counter
                    ? (t.Min, counter)
                    : throw new InvalidOperationException(
                        $"tally '{stat.StatId}' target '{t.Target}' is not an int counter node.");
            })
            .ToArray();

        LogicalEventBinding? roundEnd = _logicalResolver.Resolve("round_end");
        if (roundEnd is null)
        {
            return;
        }

        // ThresholdTallyEdge is non-idempotent — a first-wins-per-round guard keeps only the first
        // concrete round-end event per round from double-bumping the bucket.
        BoolNode? guard = null;
        if (roundEnd.ConcreteEventNames.Count > 1)
        {
            GenericRoundScopedBoolNode guardNode = new($"__seen_tally_{rs.Id.Id}_{stat.StatId}", false, playerName);
            nodes.Add(guardNode);
            edges.Add(new RoundScopedLogicNodeReset(guardNode));
            guard = guardNode;
        }

        StateNode root = localLookup["root"];
        foreach (string concreteEvent in roundEnd.ConcreteEventNames)
        {
            if (!_registry.TryResolve(concreteEvent, out Type? eventType))
            {
                continue;
            }

            edges.Add(new ThresholdTallyEdge(root, intSource, thresholds, eventType, guard));
            foreach ((int _, ValueNode<int> target) in thresholds)
            {
                descriptors.Add(new GraphEdgeDescriptor(intSource, target, concreteEvent, EdgeEffect.SetValue));
            }
        }
    }

    /// <summary>
    ///     Builds a highlight: an auto-activate <see cref="ConjunctionNode" /> over its <c>when:</c>
    ///     read set (named <c>_chain_&lt;id&gt;</c> so the evaluator's rising edge auto-produces the
    ///     timeline <see cref="Abstractions.RuleChainEvent" /> with player
    ///     attribution) plus a match-scoped <c>&lt;id&gt;.count</c> node incremented by a
    ///     registered rising-edge action.
    ///     <para>
    ///         A1 (rich highlight emission): a SECOND rising-edge action — the context arm, handed
    ///         the firing <c>(frameIdx, tick)</c> by the evaluator — appends a self-contained
    ///         <see cref="HighlightFired" /> to <paramref name="highlightSink" />: the qualified
    ///         <c>{ruleset}.{highlight}</c> identity, the subject's slot and RAW name, the live
    ///         <c>round_number</c> context node's value, and the <c>title:</c> rendered against live
    ///         node values (<see cref="HighlightTitleRenderer" /> — the previously missing
    ///         surfacing layer). Registered after the count bump, so a title hole reading
    ///         <c>&lt;id&gt;.count</c> observes the post-increment value. Works in bare mode — no
    ///         snapshots are read.
    ///     </para>
    /// </summary>
    private static void BuildV2Highlight(
        CheckedRuleset rs, CheckedHighlight highlight, int slot, string playerName,
        Dictionary<string, string> contextV2ToV1,
        Dictionary<string, StateNode> localLookup, List<StateNode> nodes,
        List<StateEdge> edges, List<GraphEdgeDescriptor> descriptors,
        List<(StateNode Trigger, Action Action, StateNode? Writes)> risingEdgeActions,
        List<(StateNode Trigger, Action<int, int> Action, StateNode? Writes)> contextRisingEdgeActions,
        List<HighlightFired> highlightSink,
        Dictionary<string, StateNode> nodesByRuleId)
    {
        (bool isDisjunction, List<IConditionalEdge> inputs) =
            LowerWhenPredicate(highlight.When.Root, localLookup, contextV2ToV1);
        if (inputs.Count == 0)
        {
            throw new InvalidOperationException(
                $"highlight '{highlight.HighlightId}' when: read no sibling stat/context the planner could gate on.");
        }

        string chainName = $"_chain_{highlight.HighlightId}";
        // The evaluator's rising edge auto-produces the timeline RuleChainEvent for any _chain_ logic
        // node (conjunction OR disjunction), so a highlight whose when: is a pure OR fires exactly
        // like a v1 mode: any chain would (obligation 2).
        BoolNode logic = isDisjunction
            ? new DisjunctionNode(chainName, [.. inputs])
            : new ConjunctionNode(chainName, [.. inputs]);
        localLookup[chainName] = logic;
        nodes.Add(logic);
        // A bare highlight ref in a table column binds to its per-round fired
        // state: the round-scoped logic node, registered under the qualified {ruleset}.{highlight}
        // key. (A scoreboard ref means the match-scoped `.count` — a different node, keyed below.)
        nodesByRuleId[$"{rs.Id.Id}.{highlight.HighlightId}"] = logic;
        if (highlight.Scope is ScopeAxis.PlayerRound or ScopeAxis.Round)
        {
            edges.Add(new RoundScopedLogicNodeReset(logic));
        }

        EdgeEffect logicEffect = isDisjunction ? EdgeEffect.Disjunction : EdgeEffect.Conjunction;
        foreach (IConditionalEdge input in inputs)
        {
            descriptors.Add(new GraphEdgeDescriptor(input.Source, logic, "", logicEffect, input.ConditionLabel));
        }

        GenericValueNode<int> countNode = new($"{highlight.HighlightId}.count", playerName);
        countNode.SetValue(0);
        nodes.Add(countNode);
        localLookup[highlight.CountNodeId] = countNode;
        nodesByRuleId[$"{rs.Id.Id}.{highlight.CountNodeId}"] = countNode;

        risingEdgeActions.Add((logic, () => countNode.SetValue(countNode.Value + 1), countNode));
        descriptors.Add(new GraphEdgeDescriptor(logic, countNode, "", EdgeEffect.SetValue, "rising edge"));

        // ── A1 rich emission: the second (context-arm) rising-edge action ──
        // RoundNumber source: the live `round_number` built-in context node (a game-scoped counter,
        // BuiltinContexts.cs — +1 on each $round_freeze_end while match_live), inherited into this
        // template's localLookup from the game node map. Reading it AT the firing instant matches
        // the snapshot projector's attribution (RuleChainEventProjector.BuildRoundByFrame records
        // the live round_number at each message; an unset/inactive node reads 0 = warmup/unknown,
        // the projector's default). Title holes resolve lazily at fire time against the SAME
        // lookups, so later-built siblings (deferred computes, later rulesets in the template) are
        // in scope by the time anything can fire.
        ValueNode<int>? roundNode =
            localLookup.TryGetValue("round_number", out StateNode? rn) ? rn as ValueNode<int> : null;
        string rulesetId = rs.Id.Id;
        string highlightId = highlight.HighlightId;
        string title = highlight.Title;
        int score = highlight.Score;
        HighlightKind kind = highlight.Kind;
        string? group = highlight.Group;
        // The when: reads whose count stats carry a first-tick companion. Read lazily at fire time so
        // a count that only completes here (multikill) has already stamped its first-tick node.
        IReadOnlyList<string> declaredReads = highlight.DeclaredReads;
        contextRisingEdgeActions.Add((logic, (frameIdx, tick) =>
        {
            int round = roundNode?.Value ?? 0;
            highlightSink.Add(new HighlightFired(
                rulesetId, highlightId, frameIdx, tick, slot, playerName, round,
                HighlightTitleRenderer.Render(title, playerName, round, localLookup, contextV2ToV1, nodesByRuleId),
                score, kind, group,
                ResolveClipStartTick(declaredReads, localLookup)));
        }, null));
    }

    /// <summary>
    ///     Lowers a <c>flag: when:</c> / <c>highlight: when:</c> predicate AST onto the engine's logic
    ///     primitives:
    ///     <c>
    ///         a or
    ///         b
    ///     </c>
    ///     legal via a disjunction, <c>a + b &gt; 5</c> via an N-source edge). Returns whether the
    ///     top-level operator is OR (→ <see cref="DisjunctionNode" />, the exact shape v1 builds for
    ///     <c>parents: {mode: any}</c>) or AND / a single term (→ <see cref="ConjunctionNode" />), plus
    ///     one <see cref="IConditionalEdge" /> per flattened operand. A term is a comparison of a
    ///     sibling against a constant (single-source <see cref="ConditionalEdge{T}" />, the v1
    ///     <c>
    ///         value
    ///         &lt;op&gt; &lt;rhs&gt;
    ///     </c>
    ///     form), a bare bool-flag/context reference (single-source
    ///     <c>active</c> edge), or a comparison whose operands read more than one sibling — the A2
    ///     multi-source case, lowered to one <see cref="Abstractions.ConditionalEdge.FromAll" /> edge.
    /// </summary>
    private static (bool IsDisjunction, List<IConditionalEdge> Inputs) LowerWhenPredicate(
        ExpressionNode when, Dictionary<string, StateNode> localLookup,
        Dictionary<string, string> contextV2ToV1)
    {
        List<IConditionalEdge> inputs = [];
        if (when is BinaryNode { Operator: BinaryOperator.Or })
        {
            FlattenLogic(when, BinaryOperator.Or, localLookup, contextV2ToV1, inputs);
            return (true, inputs);
        }

        FlattenLogic(when, BinaryOperator.And, localLookup, contextV2ToV1, inputs);
        return (false, inputs);
    }

    /// <summary>
    ///     Flattens a chain of the given associative operator (<c>and</c>/<c>or</c>) into one
    ///     <see cref="IConditionalEdge" /> per non-<paramref name="op" /> operand (each lowered by
    ///     <see cref="LowerWhenTerm" />). Mixing the other operator inside an operand is not flattened
    ///     — it falls through to <see cref="LowerWhenTerm" />, which loud-throws on a shape it cannot
    ///     express as a single edge (the same fail-loud contract the pilot planner uses).
    /// </summary>
    private static void FlattenLogic(ExpressionNode node, BinaryOperator op,
        Dictionary<string, StateNode> localLookup, Dictionary<string, string> contextV2ToV1,
        List<IConditionalEdge> inputs)
    {
        if (node is BinaryNode binary && binary.Operator == op)
        {
            FlattenLogic(binary.Left, op, localLookup, contextV2ToV1, inputs);
            FlattenLogic(binary.Right, op, localLookup, contextV2ToV1, inputs);
            return;
        }

        inputs.Add(LowerWhenTerm(node, localLookup, contextV2ToV1));
    }

    /// <summary>
    ///     Lowers one <c>when:</c> operand to a single <see cref="IConditionalEdge" />: a comparison of
    ///     a lone sibling against a constant (single-source), a bare bool reference (single-source
    ///     <c>active</c>), or a comparison reading multiple siblings (A2 multi-source
    ///     <see cref="Abstractions.ConditionalEdge.FromAll" />). A context read (<c>player.survived</c>)
    ///     resolves through the catalog v2Name→ruleId table. Any other shape — a negation
    ///     (<c>not X</c>), a mixed nested boolean (the <c>(b or c)</c> operand of <c>a and (b or c)</c>),
    ///     a function-bearing predicate — routes to the general whole-predicate fallback
    ///     (<see cref="LowerWhenTermGeneral" />).
    /// </summary>
    private static IConditionalEdge LowerWhenTerm(ExpressionNode term,
        Dictionary<string, StateNode> localLookup, Dictionary<string, string> contextV2ToV1)
    {
        switch (term)
        {
            case BinaryNode cmp when IsComparison(cmp.Operator):
            {
                List<(string Path, StateNode Node)> refs = [];
                CollectSiblingRefs(cmp, localLookup, contextV2ToV1, refs);

                if (refs.Count > 1)
                {
                    // a + b > 5 — one N-source edge: satisfied iff every referenced source is active
                    // AND the compiled comparison over their current values holds.
                    string formula = V1ExpressionWriter.Write(cmp);
                    Dictionary<string, object> exprLookup = new(StringComparer.OrdinalIgnoreCase);
                    List<StateNode> sources = [];
                    foreach ((string path, StateNode node) in refs)
                    {
                        exprLookup[path] = node;
                        if (!sources.Contains(node))
                        {
                            sources.Add(node);
                        }
                    }

                    Func<bool> predicate = ExpressionCompiler.CompileNodeBoolExpression(formula, exprLookup);
                    return ConditionalEdge.FromAll(sources, predicate, formula);
                }

                if (cmp.Left is ReferenceNode leftRef
                    && ResolveWhenRef(leftRef.Path, localLookup, contextV2ToV1) is { } leftNode)
                {
                    string label = $"value {OpText(cmp.Operator)} {V1ExpressionWriter.Write(cmp.Right)}";
                    return LowerConditionalEdge(leftNode, label, label);
                }

                if (cmp.Right is ReferenceNode rightRef
                    && ResolveWhenRef(rightRef.Path, localLookup, contextV2ToV1) is { } rightNode)
                {
                    string label = $"value {Flip(cmp.Operator)} {V1ExpressionWriter.Write(cmp.Left)}";
                    return LowerConditionalEdge(rightNode, label, label);
                }

                throw new InvalidOperationException(
                    "when: comparison does not reference a sibling stat/context the planner can gate on.");
            }
            case ReferenceNode flagRef when ResolveWhenRef(flagRef.Path, localLookup, contextV2ToV1) is { } flagNode:
                return LowerConditionalEdge(flagNode, "active", "active");
            default:
                // General whole-predicate fallback (pre-freeze planner-completeness gap): any when:
                // operand the structural single-/multi-source comparison decomposition above cannot
                // reduce — a negation (`not X`, a UnaryNode), a mixed nested boolean whose operator
                // differs from the enclosing flatten operator (the `(b or c)` operand of
                // `a and (b or c)`, a non-comparison BinaryNode), a function-bearing predicate, etc.
                // Reuses the G4/A2 machinery: collect every gate-able sibling/context source the term
                // references and compile the WHOLE term into one ConditionalEdge.FromAll predicate.
                return LowerWhenTermGeneral(term, localLookup, contextV2ToV1);
        }
    }

    /// <summary>
    ///     The general whole-predicate fallback for a <c>when:</c> operand the structural
    ///     OR/AND-of-comparisons decomposition (<see cref="LowerWhenPredicate" /> /
    ///     <see cref="FlattenLogic" /> / the <see cref="LowerWhenTerm" /> fast cases) cannot express as
    ///     a single-source or A2 multi-source comparison edge: a negation (<c>not X</c>), a mixed
    ///     nested boolean (<c>a and (b or c)</c>'s <c>(b or c)</c> operand), a function-bearing
    ///     predicate, or any other boolean combination over sibling/context node values. It collects
    ///     every distinct gate-able source the term references (the same <see cref="ResolveWhenRef" />
    ///     resolution the fast paths use — siblings, per-player contexts, B6 aggregates) and compiles
    ///     the whole term via <see cref="ExpressionCompiler.CompileNodeBoolExpression" /> — the G4 path
    ///     that evaluates <c>not</c> (<c>!</c>), <c>and</c>/<c>or</c> (<c>&amp;&amp;</c>/<c>||</c>),
    ///     comparisons and functions over each node's live <c>.Value</c> — feeding one
    ///     <see cref="Abstractions.ConditionalEdge.FromAll" /> edge (a
    ///     <see cref="MultiSourceConditionalEdge" />) over those sources. Because the compiled formula
    ///     is written through <see cref="V1ExpressionWriter" /> with the <paramref name="contextV2ToV1" />
    ///     table and the exprLookup is keyed by the SAME identifiers that writer emits, a context read
    ///     (<c>player.survived</c> → bare <c>survived</c>) resolves exactly as the fast single-source
    ///     path resolves it. A term referencing no gate-able sibling/context is still a loud error
    ///     (unchanged from the previous throw).
    /// </summary>
    private static IConditionalEdge LowerWhenTermGeneral(ExpressionNode term,
        Dictionary<string, StateNode> localLookup, Dictionary<string, string> contextV2ToV1)
    {
        List<(string Key, StateNode Node)> refs = [];
        CollectWhenSources(term, localLookup, contextV2ToV1, refs);
        if (refs.Count == 0)
        {
            throw new InvalidOperationException(
                $"when: expression '{term.CanonicalText}' read no sibling stat/context the planner could gate on.");
        }

        // Write the formula through the same context-lowering table the exprLookup keys use, so a
        // context path (player.survived) becomes the bare v1 id (survived) in BOTH the compiled
        // formula and the lookup — the seam CompileNodeBoolExpression resolves against node values.
        string formula = V1ExpressionWriter.Write(term, contextV2ToV1);
        Dictionary<string, object> exprLookup = new(StringComparer.OrdinalIgnoreCase);
        List<StateNode> sources = [];
        foreach ((string key, StateNode node) in refs)
        {
            exprLookup[key] = node;
            if (!sources.Contains(node))
            {
                sources.Add(node);
            }
        }

        Func<bool> predicate = ExpressionCompiler.CompileNodeBoolExpression(formula, exprLookup);
        return ConditionalEdge.FromAll(sources, predicate, formula);
    }

    /// <summary>
    ///     Walks a <c>when:</c> operand collecting the distinct sibling/context nodes it references
    ///     (recursing through negations, boolean/comparison/arithmetic <see cref="BinaryNode" />s and
    ///     function-call arguments), keyed by the identifier
    ///     <see cref="V1ExpressionWriter.Write(ExpressionNode, IReadOnlyDictionary{string, string}?)" />
    ///     emits for each — a context path lowered to its bare v1 id, every other path verbatim — so
    ///     the compiled fallback formula's identifiers and its expression-lookup keys agree.
    /// </summary>
    private static void CollectWhenSources(ExpressionNode node,
        Dictionary<string, StateNode> localLookup, Dictionary<string, string> contextV2ToV1,
        List<(string Key, StateNode Node)> refs)
    {
        switch (node)
        {
            case ReferenceNode reference:
                if (ResolveWhenRef(reference.Path, localLookup, contextV2ToV1) is { } resolved
                    && refs.All(r => !ReferenceEquals(r.Node, resolved)))
                {
                    string key = V1ExpressionWriter.MapReferencePath(
                        contextV2ToV1.TryGetValue(reference.Path, out string? v1Id) ? v1Id : reference.Path);
                    refs.Add((key, resolved));
                }

                return;
            case UnaryNode unary:
                CollectWhenSources(unary.Operand, localLookup, contextV2ToV1, refs);
                return;
            case BinaryNode binary:
                CollectWhenSources(binary.Left, localLookup, contextV2ToV1, refs);
                CollectWhenSources(binary.Right, localLookup, contextV2ToV1, refs);
                return;
            case CallNode call:
                foreach (ExpressionNode arg in call.Arguments)
                {
                    CollectWhenSources(arg, localLookup, contextV2ToV1, refs);
                }

                return;
        }
    }

    /// <summary>
    ///     Lowers one <c>when:</c>/<c>while:</c> operand to a single <see cref="IConditionalEdge" />,
    ///     routing B6 live pull-nodes (<see cref="RoundTeamAggregateNode" /> /
    ///     <see cref="RoundClutchFacetNode" />) — pre-freeze gap G4 — through a reflective fire-time
    ///     value predicate rather than the v1 <see cref="RuleChainBuilder.CreateConditionalEdge" />
    ///     path. Those pull-nodes are neither a typed <see cref="ValueNode{T}" /> nor a
    ///     <see cref="BoolNode" />, so the <c>ConditionalEdge&lt;T&gt;</c> ctor (which binds a
    ///     <c>ValueNode&lt;T&gt;</c>) cannot construct over them (it throws
    ///     <see cref="MissingMethodException" />); and because they are always
    ///     <see cref="StateNode.IsActive" />, a plain source-activation gate over one never restricts.
    ///     The reflective edge reads <c>node.Value</c> exactly as the A2 multi-source path and the
    ///     v2 <c>where:</c>/<c>compute:</c> reads already do. Any other node (a sibling stat, a
    ///     per-player context <see cref="BoolNode" /> such as <c>player.alive</c>/<c>player.survived</c>,
    ///     a written B6 equipment <see cref="ValueNode{T}" />) keeps the unchanged v1 path — so the
    ///     v1 builder's conditional-edge behaviour is byte-identical.
    /// </summary>
    private static IConditionalEdge LowerConditionalEdge(StateNode node, string? condition, string label) =>
        IsReflectiveValueNode(node)
            ? BuildReflectiveConditionalEdge(node, condition, label)
            : CreateConditionalEdge(node, condition, label);

    /// <summary>
    ///     True when <paramref name="node" /> surfaces its value only through a reflectively-read
    ///     <c>Value</c> property — a B6 live pull-node (<see cref="RoundTeamAggregateNode" /> /
    ///     <see cref="RoundClutchFacetNode" />): neither a typed <see cref="ValueNode{T}" /> (any
    ///     <c>T</c>) nor a <see cref="BoolNode" />, yet exposing a public instance <c>Value</c>. These
    ///     are the only nodes the <c>ConditionalEdge&lt;T&gt;</c> ctor cannot bind, so this predicate
    ///     is exactly the divert condition for the reflective gate path (gap G4).
    /// </summary>
    private static bool IsReflectiveValueNode(StateNode node)
    {
        if (node is BoolNode)
        {
            return false;
        }

        for (Type? t = node.GetType(); t is not null; t = t.BaseType)
        {
            if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(ValueNode<>))
            {
                return false;
            }
        }

        return node.GetType().GetProperty("Value",
            BindingFlags.Public | BindingFlags.Instance) is not null;
    }

    /// <summary>
    ///     Builds a single-source <see cref="MultiSourceConditionalEdge" /> whose predicate reads a B6
    ///     pull-node's live <c>Value</c> at fire time (gap G4). The <paramref name="condition" /> uses
    ///     <c>value</c> for the node's own value (e.g. <c>value &gt; 0</c>); a bare-reference gate
    ///     (<c>null</c>/<c>"active"</c>) becomes a truthiness test — <c>value == true</c> for a boolean
    ///     facet (<c>round.alive.in_clutch</c>), <c>value != 0</c> otherwise. The node is the edge's
    ///     lone source; it is always active, so the wrapper's activation check passes and the compiled
    ///     value predicate does the gating.
    /// </summary>
    private static MultiSourceConditionalEdge BuildReflectiveConditionalEdge(StateNode node, string? condition, string label)
    {
        PropertyInfo valueProp = node.GetType().GetProperty("Value",
                                     BindingFlags.Public | BindingFlags.Instance)
                                 ?? throw new InvalidOperationException(
                                     $"gate over '{node.Name}' expected a reflectively-read 'Value' property.");

        string expr = condition is null or "active"
            ? valueProp.PropertyType == typeof(bool) ? "value == true" : "value != 0"
            : condition;

        Dictionary<string, object> lookup = new(StringComparer.OrdinalIgnoreCase)
        {
            ["value"] = node
        };
        Func<bool> predicate = ExpressionCompiler.CompileNodeBoolExpression(expr, lookup);
        return new MultiSourceConditionalEdge([node], predicate, label);
    }

    /// <summary>
    ///     Resolves a <c>when:</c> reference path to its graph node: a sibling stat / enrichment / game
    ///     context keyed directly, or a context read (<c>player.survived</c>) via the catalog
    ///     v2Name→ruleId table. Returns <c>null</c> when the path names no node in scope.
    /// </summary>
    private static StateNode? ResolveWhenRef(string path,
        Dictionary<string, StateNode> localLookup, Dictionary<string, string> contextV2ToV1)
    {
        if (localLookup.TryGetValue(path, out StateNode? direct))
        {
            return direct;
        }

        return contextV2ToV1.TryGetValue(path, out string? v1Id)
               && localLookup.TryGetValue(v1Id, out StateNode? context)
            ? context
            : null;
    }

    /// <summary>
    ///     Collects the distinct sibling/context nodes a comparison AST references (both operands),
    ///     in first-occurrence order, keyed by the reference path <see cref="V1ExpressionWriter" />
    ///     emits — so a multi-source predicate's expression-lookup and its declared sources agree.
    /// </summary>
    private static void CollectSiblingRefs(ExpressionNode node,
        Dictionary<string, StateNode> localLookup, Dictionary<string, string> contextV2ToV1,
        List<(string Path, StateNode Node)> refs)
    {
        switch (node)
        {
            case ReferenceNode reference:
                if (ResolveWhenRef(reference.Path, localLookup, contextV2ToV1) is { } resolved
                    && refs.All(r => !ReferenceEquals(r.Node, resolved)))
                {
                    refs.Add((V1ExpressionWriter.MapReferencePath(reference.Path), resolved));
                }

                return;
            case BinaryNode binary:
                CollectSiblingRefs(binary.Left, localLookup, contextV2ToV1, refs);
                CollectSiblingRefs(binary.Right, localLookup, contextV2ToV1, refs);
                return;
            case UnaryNode unary:
                CollectSiblingRefs(unary.Operand, localLookup, contextV2ToV1, refs);
                return;
        }
    }

    /// <summary>Builds the fire-time condition string: the actor binding (if any) ∧ the trigger condition.</summary>
    private static string? BuildConditionString(CheckedRuleset rs, CheckedStat stat,
        Dictionary<string, CatalogView> views, Dictionary<string, string> contextV2ToV1)
    {
        // where: (trigger) reads are lowered through contextV2ToV1 so a per-player context
        // (player.survived) / B6 aggregate (round.enemies.alive) becomes its bare v1 rule id — the
        // form ExpressionCompiler resolves against the subject slot's per-player node (gap G1,
        // event-gated per-player aggregate reads). The actor binding below is a raw player.slot
        // string the compiler already handles, so it is built without the remap.
        string? trigger = stat.TriggerCondition is null
            ? null
            : V1ExpressionWriter.Write(stat.TriggerCondition.Root, contextV2ToV1);
        string? actor = BuildActorBinding(rs, stat, views);
        if (actor is null)
        {
            return trigger;
        }

        return trigger is null ? actor : $"{actor} && ({trigger})";
    }

    /// <summary>
    ///     The per-player actor binding an <c>actor_slot</c> view lowers to
    ///     (<c>event.&lt;ActorSlotField&gt; == player.slot</c>) — the same slot-equality v1 hand-wrote.
    ///     Without a subject to bind to (a non-per-player ruleset, or <c>match: {actor: any}</c>
    ///     suppression) it lowers to the actor-slot validity test instead. Null for
    ///     <c>binding: none</c>/<c>team</c> views, which have no actor field.
    /// </summary>
    private static string? BuildActorBinding(CheckedRuleset rs, CheckedStat stat,
        Dictionary<string, CatalogView> views)
    {
        if (stat.ResolvedView is null)
        {
            return null;
        }

        if (!views.TryGetValue(stat.ResolvedView, out CatalogView? view)
            || !string.Equals(view.Binding, "actor_slot", StringComparison.Ordinal))
        {
            return null;
        }

        CatalogViewRole? role = view.Roles.FirstOrDefault(r => string.Equals(r.Role, view.Actor, StringComparison.Ordinal));
        if (role is null)
        {
            return null;
        }

        if (rs.For == RulesetScope.EachPlayer && !stat.SuppressActorBinding)
        {
            return $"event.{role.Field} == player.slot";
        }

        // No subject, so slot equality cannot do the filtering it does above, and the view's baked
        // predicate only excludes suicides. A player_death whose Attacker is the 0xFFFF no-player
        // sentinel is a world kill; counting it made `for: match` disagree with the per-player twin,
        // which drops it for want of a subject. The bound matches MaterializeNewPlayers: any slot in
        // range does get materialized off the event, so this holds for any demo, not just the one
        // that surfaced it.
        return $"event.{role.Field} >= 0 && event.{role.Field} < {PlayerSlotExclusiveUpperBound}";
    }

    /// <summary>
    ///     One past the last valid player slot. Mirrors the range guard in
    ///     <c>StateGraphEvaluator.MaterializeNewPlayers</c>, which is what decides whether an
    ///     event-discovered slot becomes a subject at all.
    /// </summary>
    private const int PlayerSlotExclusiveUpperBound = 64;

    /// <summary>
    ///     Resolves a <c>while:</c> gate to its parent-as-edge-source node: a single context reference maps (via the catalog v2Name→ruleId table) to the
    ///     game context node; a single sibling flag reference maps to that flag; no gate → the graph
    ///     root (always active).
    /// </summary>
    private static (StateNode Source, IConditionalEdge? Gate) ResolveGateSource(CheckedExpression? whileGate,
        Dictionary<string, string> contextV2ToV1, Dictionary<string, StateNode> localLookup)
    {
        if (whileGate is null)
        {
            return (localLookup["root"], null);
        }

        if (whileGate.Root is ReferenceNode reference)
        {
            StateNode? node = null;
            if (contextV2ToV1.TryGetValue(reference.Path, out string? v1Id)
                && localLookup.TryGetValue(v1Id, out StateNode? contextNode))
            {
                node = contextNode;
            }
            else if (localLookup.TryGetValue(reference.Path, out StateNode? siblingNode))
            {
                node = siblingNode;
            }

            if (node is not null)
            {
                // A B6 live pull-node (round.alive.in_clutch) is always IsActive, so the parent-as-edge
                // source gate never restricts (gap G4) — gate it on a fire-time value predicate instead.
                // Every other node (a per-player context BoolNode such as player.alive, a sibling flag)
                // keeps the unchanged parent-as-edge-source path: its activeness IS the gate.
                return IsReflectiveValueNode(node)
                    ? (node, LowerConditionalEdge(node, "active", reference.CanonicalText))
                    : (node, null);
            }
        }

        // A single comparison gate (round.enemies.alive > 0, round.team.equipment > 2000) lowers to a
        // fire-time value predicate over its referenced node(s) — the same reflective/compiled edge the
        // when: single- and multi-source paths build. The edge stays rooted (always active); the gate does
        // the restricting. Compound and/or compositions are not lowered yet.
        if (whileGate.Root is BinaryNode cmp && IsComparison(cmp.Operator))
        {
            return (localLookup["root"], LowerWhenTerm(cmp, localLookup, contextV2ToV1));
        }

        throw new InvalidOperationException(
            $"while: gate '{whileGate.Root.CanonicalText}' did not resolve to a single context/flag node "
            + "the planner can use as an edge source (compound while gates are not yet supported).");
    }

    /// <summary>
    ///     Loud-fails a stat kind that cannot thread a <c>while:</c> value-gate (gap G4) into its edge:
    ///     the edge creators for <c>capture: keep list/min/max</c>, <c>streak:</c>, <c>bucket:</c> and
    ///     <c>tally:</c> take no <see cref="IConditionalEdge" /> source-gate slot, so a value predicate
    ///     over a B6 pull-node would be silently dropped — the exact no-op class G4 closes. Count / sum
    ///     / capture-first-last / flag-triggered stats do carry it (game-event <c>ShouldApply</c>).
    /// </summary>
    private static void RequireNoWhileValueGate(IConditionalEdge? gate, string statId, string kind)
    {
        if (gate is not null)
        {
            throw new InvalidOperationException(
                $"stat '{statId}': a while: value-gate over a B6 pull-node is not yet lowered for {kind} "
                + "stats (gap G4 covers count / sum / capture-first-last / flag).");
        }
    }

    private EventRegistration RequireGameEvent(string eventName, string statId) =>
        _registry.IsGameEvent(eventName)
            ? _registry.GetEvent(eventName)!
            : throw new InvalidOperationException(
                $"v2 stat '{statId}' triggers on '{eventName}', which is not a registered game event "
                + "(entity-change v2 triggers are not yet lowered).");

    /// <summary>
    ///     Builds the write edge for one v2 trigger event, dispatching on whether the event is a
    ///     registered net message (a <c>net.&lt;Message&gt;</c> trigger, D12 payload matching) or a
    ///     game event. Net edges reuse the v1 <see cref="RuleChainBuilder.CreateNetMessageEdge" />,
    ///     which compiles the same <c>event.*</c>-grammar condition/value against the message's
    ///     payload fields — so a bare net trigger is value-identical to the v1 net path and a
    ///     <c>where:</c>-conditioned one threads the compiled condition through the very same
    ///     <see cref="ExpressionCompiler.CompileEventCondition" /> the v1 net rule uses. Net edges
    ///     carry no declared-read set (<c>OnNetMessage&lt;T&gt;</c> has no read-aware topo slot); a
    ///     net <c>where:</c> over payload fields resolves to an empty read set anyway (event fields
    ///     drop out of DeclaredReads), so this is not a lost ordering constraint in practice.
    /// </summary>
    private StateEdge CreateV2TriggerEdge(
        string ev, string statId, TriggerAction action, string? value, string? condition,
        StateNode source, StateNode dest, int slot, string playerName,
        BoolNode? guard, IReadOnlyList<StateNode>? declaredReads, IConditionalEdge? sourceGate = null)
    {
        if (_registry.IsNetMessage(ev))
        {
            if (sourceGate is not null)
            {
                // A while: value-gate over a B6 pull-node (gap G4) rides the game-event ShouldApply
                // sourceGate slot; OnNetMessage<T> has no such slot, so a net-triggered stat cannot
                // carry one. Loud-fail rather than silently drop the gate (the very no-op G4 fixes).
                throw new InvalidOperationException(
                    $"stat '{statId}': a while: value-gate over a B6 pull-node is only supported on "
                    + "game-event triggers, not net-message triggers.");
            }

            NetMessageRegistration netReg = _registry.GetNetMessage(ev)!;
            // Net edges have no Increment shortcut; express +1 as an explicit Set selector so the
            // compiled selector is value-identical to the game-event Increment path.
            TriggerAction netAction = action == TriggerAction.Increment ? TriggerAction.Set : action;
            string? netValue = action == TriggerAction.Increment ? "node.value + 1" : value;
            TriggerDef netTrigger = new(ev, netAction, Value: netValue, Condition: condition);
            return CreateNetMessageEdge(netTrigger, netReg, source, dest, slot, playerName, guard);
        }

        EventRegistration reg = RequireGameEvent(ev, statId);
        TriggerDef trigger = new(ev, action, Value: value, Condition: condition);
        return CreateGameEventEdge(trigger, reg, source, dest, slot, playerName, guard, sourceGate, declaredReads);
    }

    /// <summary>
    ///     Maps a checked stat's <see cref="CheckedStat.DeclaredReads" /> paths to the graph nodes in
    ///     scope (A1 read-aware ordering). A path resolves either directly (a
    ///     sibling stat id, an enrichment name like <c>enrich.kill.was_enemy_kill</c>, or a game
    ///     context already keyed by its v1 id) or via the catalog v2Name→ruleId table (a context read
    ///     like <c>round.bomb.was_planted</c> → <c>bomb_was_planted</c>). Paths with no graph node —
    ///     event fields (<c>event.Attacker</c>), the subject (<c>player.slot</c>), literals — drop
    ///     out. Returns <c>null</c> for an empty result so the edge stays v1-identical (no declared
    ///     reads → the pre-A1 topological order).
    /// </summary>
    private static List<StateNode>? ResolveDeclaredReadNodes(
        IReadOnlyList<string> declaredReadPaths,
        Dictionary<string, StateNode> localLookup,
        Dictionary<string, string> contextV2ToV1)
    {
        List<StateNode> nodes = [];
        foreach (string path in declaredReadPaths)
        {
            if (localLookup.TryGetValue(path, out StateNode? direct))
            {
                // A settle-site EntityValuePullNode (materialized under a player.* read path for a sibling
                // compute:/flag: when:) must NOT become a fire-time edge's declared read: a player.* read
                // has no graph node in the DeclaredReads model (it drops out — the read is resolved at fire
                // time by the event-condition seam, not ordered against a writer). Skipping it keeps a
                // where:/while: edge's declared-read set byte-identical whether or not a sibling settle-site
                // stat happened to materialize a pull-node under the same path.
                if (direct is EntityValuePullNode)
                {
                    continue;
                }

                if (!nodes.Contains(direct))
                {
                    nodes.Add(direct);
                }
            }
            else if (contextV2ToV1.TryGetValue(path, out string? v1Id)
                     && localLookup.TryGetValue(v1Id, out StateNode? context) && !nodes.Contains(context))
            {
                nodes.Add(context);
            }
        }

        return nodes.Count > 0 ? nodes : null;
    }

    /// <summary>
    ///     Builds the per-slot condition-node overlay for gap G1 (event-gated per-player aggregate
    ///     reads): a SUPERSET of the shared game-scoped enrichment lookup that additionally exposes
    ///     THIS slot's per-player context nodes (alive/survived/traded) and B6 aggregate nodes
    ///     (round.team.* / round.enemies.* / round.alive.*) under their v1 rule ids — the exact keys a
    ///     where:-condition read resolves against once <see cref="V1ExpressionWriter" /> has lowered
    ///     the v2 dotted path to its rule id. Only rule ids actually materialized into
    ///     <paramref name="localLookup" /> are exposed (economy nodes appear only when the equipment
    ///     provider was gated in), so a demo-less build simply exposes fewer keys. The base enrichment
    ///     entries are copied so mutating the overlay never disturbs the shared lookup.
    /// </summary>
    private Dictionary<string, object>? BuildV2ConditionOverlay(
        Dictionary<string, StateNode> localLookup,
        IReadOnlyList<RuleDef> perPlayerContextRules)
    {
        Dictionary<string, object> overlay = _enrichmentNodes is { } enrich
            ? new Dictionary<string, object>(enrich, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

        void Expose(string ruleId)
        {
            if (localLookup.TryGetValue(ruleId, out StateNode? node))
            {
                overlay[ruleId] = node;
            }
        }

        foreach (RuleDef ctxRule in perPlayerContextRules)
        {
            Expose(ctxRule.Id);
        }

        foreach (B6RuleIds.B6Member member in B6RuleIds.Members)
        {
            Expose(member.RuleId);
        }

        Expose(B6RuleIds.TeamEquipment);
        Expose(B6RuleIds.EnemiesEquipment);

        return overlay;
    }

    /// <summary>
    ///     Materializes the B6 alive/count/clutch aggregate nodes for one subject slot and registers
    ///     them in <paramref name="localLookup" /> under their v1 rule ids (the keys the catalog
    ///     v2Name→ruleId table maps to). These are pure read-derived nodes over the shared
    ///     <see cref="PlayerContextIndex" /> — no edges, no second store (decision a). No-op if the
    ///     index is not available (e.g. a demo-less build).
    /// </summary>
    private void InjectB6AliveAggregates(int slot, string? playerName,
        Dictionary<string, StateNode> localLookup, List<StateNode> nodes)
    {
        if (_playerContextIndex is not { } index)
        {
            return;
        }

        void Add(string ruleId, StateNode node)
        {
            localLookup[ruleId] = node;
            nodes.Add(node);
        }

        Add(B6RuleIds.TeamAlive,
            new RoundTeamAggregateNode(B6RuleIds.TeamAlive, index, slot,
                RoundTeamAggregateNode.AggregateKind.TeamAlive, playerName));
        Add(B6RuleIds.TeamPlayers,
            new RoundTeamAggregateNode(B6RuleIds.TeamPlayers, index, slot,
                RoundTeamAggregateNode.AggregateKind.TeamPlayers, playerName));
        Add(B6RuleIds.EnemiesAlive,
            new RoundTeamAggregateNode(B6RuleIds.EnemiesAlive, index, slot,
                RoundTeamAggregateNode.AggregateKind.EnemyAlive, playerName));
        Add(B6RuleIds.EnemiesPlayers,
            new RoundTeamAggregateNode(B6RuleIds.EnemiesPlayers, index, slot,
                RoundTeamAggregateNode.AggregateKind.EnemyPlayers, playerName));
        Add(B6RuleIds.AliveInClutch,
            new RoundClutchFacetNode(B6RuleIds.AliveInClutch, index, slot, playerName));
        Add(B6RuleIds.ClutchSize,
            new RoundClutchSizeNode(B6RuleIds.ClutchSize, index, slot, playerName));
    }

    /// <summary>
    ///     Materializes the B6 freeze-end economy nodes (decision c) for one subject slot: two written
    ///     <see cref="GenericValueNode{T}" /> (<c>round_team_equipment</c> / <c>round_enemies_equipment</c>)
    ///     plus a <see cref="Edges.PlayerEconomyFreezeEndEdge" /> that writes them once per player at
    ///     <c>round_freeze_end</c> from the digest-sampled absolute team economy sums. No-op unless the
    ///     equipment provider was gated in (a ruleset actually reads <c>round.*.equipment</c>), so the
    ///     scanner is guaranteed to snapshot it and <c>GetPreFrameValue</c> never throws.
    /// </summary>
    private void InjectB6EconomyAggregates(int slot, string? playerName,
        Dictionary<string, StateNode> localLookup, List<StateNode> nodes, List<StateEdge> edges)
    {
        if (_playerContextIndex is not { } index || _entityScanner is not { } scanner
                                                 || _b6EquipmentProvider is not { } equipment)
        {
            return;
        }

        GenericValueNode<int> teamEquipment = new(B6RuleIds.TeamEquipment, playerName);
        teamEquipment.SetValue(0);
        GenericValueNode<int> enemiesEquipment = new(B6RuleIds.EnemiesEquipment, playerName);
        enemiesEquipment.SetValue(0);

        localLookup[B6RuleIds.TeamEquipment] = teamEquipment;
        localLookup[B6RuleIds.EnemiesEquipment] = enemiesEquipment;
        nodes.Add(teamEquipment);
        nodes.Add(enemiesEquipment);

        edges.Add(new PlayerEconomyFreezeEndEdge(
            localLookup["root"], index,
            s => scanner.GetPreFrameValue(equipment, s) is int v ? v : 0,
            slot, teamEquipment, enemiesEquipment));
    }

    /// <summary>
    ///     Materializes an <see cref="EntityValuePullNode" /> for each SUBJECT-player entity read a
    ///     SETTLE-site stat performs (compute: / flag: when:), registered in <paramref name="localLookup" />
    ///     under the read's path (e.g. <c>player.health</c>) so the compute remap and when-lowering resolve
    ///     it as an ordinary graph-node value. Only the two eventless kinds are handled — every other kind
    ///     reads <c>player.*</c> at FIRE time through the event-condition seam (<see cref="RewriteEntityReads" />),
    ///     so it must NOT be diverted here. Role-handle reads (a non-player <see cref="EntityProviderReference.Subject" />)
    ///     are event-fire only (the role's slot is read from the event field per fire); they are skipped.
    ///     Dedup is by read path within the slot's localLookup (a second stat reading the same path reuses
    ///     the node). Mirrors <see cref="InjectB6EconomyAggregates" />, but keyed by the read path rather
    ///     than a fixed rule id. No-op for a stat with no settle-site entity reads (v1-identical).
    /// </summary>
    private void EnsureSettleEntityPullNodes(CheckedStat stat, int slot, string? playerName,
        Dictionary<string, StateNode> localLookup, List<StateNode> nodes)
    {
        bool settleSite = stat.Kind == RuleNodeKind.Compute
                          || stat.Kind == RuleNodeKind.Flag && stat.ConcreteEvents.Count == 0;
        if (!settleSite || stat.EntityReads.Count == 0)
        {
            return;
        }

        foreach (EntityProviderReference read in stat.EntityReads)
        {
            if (read.Subject != EntityProviderReference.PlayerSubject)
            {
                continue; // role-handle reads resolve the role's slot from the event — fire-time only
            }

            if (localLookup.ContainsKey(read.Path))
            {
                continue; // already materialized (another settle-site stat read the same path this slot)
            }

            EntityValuePullNode pull = CreateEntityPullNode(read.ProviderName, slot, playerName);
            localLookup[read.Path] = pull;
            nodes.Add(pull);
        }
    }

    /// <summary>
    ///     Builds the <see cref="EntityValuePullNode" /> backing a subject-player settle-site entity read.
    ///     Requires the entity scanner (built only when a demo is present) and the per-player provider
    ///     registry — a missing scanner is the SAME compile-time "requires per-player entity providers and
    ///     a player slot" error the fire-time <c>where:</c> entity seam raises
    ///     (<c>ExpressionCompiler.ResolvePlayerEntity</c>), so a no-demo build surfaces the identical
    ///     marker. The provider was gated into the scanner's snapshot set by
    ///     <see cref="UnionV2EntityReads" /> (its <see cref="CheckedStat.EntityReads" /> lists it regardless
    ///     of site), so the node's read is guaranteed registered.
    /// </summary>
    /// <summary>
    ///     For a <c>flag: when:</c> whose lowered <paramref name="inputs" /> gate on a writer-less
    ///     <see cref="EntityValuePullNode" />, adds one <see cref="EntityPullNodeSettleEdge" /> per
    ///     profile <c>$round_end</c> concrete event so the evaluator recomputes the flag at round end
    ///     (the flag-eval settle point), reading the pull-node's live round-end value. A pull-node has no
    ///     writer, so without this the flag's logic node is never bucketed into the recompute index and
    ///     would freeze at its init state. No-op when the when: reads no pull-node (byte-identical to the
    ///     sibling/context flag path).
    /// </summary>
    private void AddEntityFlagSettleEdges(
        List<IConditionalEdge> inputs, Dictionary<string, StateNode> localLookup, List<StateEdge> edges)
    {
        List<StateNode> pullNodes = [];
        foreach (IConditionalEdge input in inputs)
        {
            foreach (StateNode src in input.Sources)
            {
                if (src is EntityValuePullNode && !pullNodes.Contains(src))
                {
                    pullNodes.Add(src);
                }
            }
        }

        if (pullNodes.Count == 0)
        {
            return;
        }

        LogicalEventBinding? roundEnd = _logicalResolver.Resolve("round_end");
        if (roundEnd is null)
        {
            return;
        }

        StateNode[] pulls = [.. pullNodes];
        StateNode root = localLookup["root"];
        foreach (string concreteEvent in roundEnd.ConcreteEventNames)
        {
            if (_registry.TryResolve(concreteEvent, out Type? eventType))
            {
                edges.Add(new EntityPullNodeSettleEdge(root, pulls, eventType));
            }
        }
    }

    private EntityValuePullNode CreateEntityPullNode(string providerName, int slot, string? playerName)
    {
        if (_entityScanner is not { } scanner || _perPlayerEntityProviders is null)
        {
            throw new InvalidOperationException(
                $"'player.{providerName}' requires per-player entity providers and a player slot, but none "
                + "are bound. Build the rule chain with a PerPlayerEntityValueProviderRegistry (so the entity "
                + "scanner is created) for per-player chains.");
        }

        IPerPlayerEntityValueProvider provider = _perPlayerEntityProviders.Get(providerName)
                                                 ?? throw new InvalidOperationException(
                                                     $"Unknown per-player entity provider: '{providerName}' (from a settle-site entity read).");

        return new EntityValuePullNode(
            $"__entity_{providerName.Replace('.', '_')}", scanner, provider, slot, playerName);
    }

    private static void RegisterV2Node(StateNode node, CheckedRuleset rs, string statId,
        Dictionary<string, StateNode> localLookup, List<StateNode> nodes,
        Dictionary<string, StateNode> nodesByRuleId)
    {
        localLookup[statId] = node; // bare id: the ruleset's own private sibling namespace
        nodes.Add(node);
        // Qualified-only in the configured-output map: no bare-id
        // fallback, so the v1 bare-id collision hazard cannot arise for v2.
        nodesByRuleId[$"{rs.Id.Id}.{statId}"] = node;
    }

    /// <summary>
    ///     Builds the clip-start companion node for a <c>count:</c> stat (Approach 1): a
    ///     <see cref="ValueNode{Int32}" /> seeded to <see cref="RecordFirstEventTickEdge.Sentinel" />
    ///     (the "unset" marker a reader distinguishes from a real tick 0). Scoped like the count itself,
    ///     so a per-round count resets its first-tick back to the sentinel each round. Named with the
    ///     internal <c>__first_tick_</c> prefix like the capture <c>__seen_</c> guards — never a
    ///     configured output.
    /// </summary>
    private static ValueNode<int> MakeFirstTickNode(string name, bool roundScoped, string? subtitle)
    {
        if (roundScoped)
        {
            return new GenericRoundScopedValueNode<int>(name, RecordFirstEventTickEdge.Sentinel, subtitle);
        }

        GenericValueNode<int> node = new(name, subtitle);
        node.SetValue(RecordFirstEventTickEdge.Sentinel);
        return node;
    }

    /// <summary>
    ///     Builds the write-once first-tick recording edge for one concrete event of a <c>count:</c>
    ///     stat — a companion of the increment edge gated by the SAME <paramref name="condition" />, so
    ///     it stamps <paramref name="firstTick" /> with the frame-clock tick of the first contributing
    ///     event of the round. Returns <c>null</c> for a net-message trigger (no <c>evt.GameTick</c> to
    ///     record → the count carries no clip-start, the safe lead-in-only fallback).
    /// </summary>
    private RecordFirstEventTickEdge? TryCreateFirstTickRecordEdge(
        string ev, string statId, string? condition, StateNode source, ValueNode<int> firstTick, int slot)
    {
        if (_registry.IsNetMessage(ev))
        {
            return null;
        }

        EventRegistration reg = RequireGameEvent(ev, statId);
        Delegate? cond = condition is not null
            ? ExpressionCompiler.CompileEventCondition(condition, reg.EventType, reg.Fields, slot, ConditionNodes,
                _currentPlayerTeam, _playerContextIndex, _entityScanner, _perPlayerEntityProviders,
                parameterType: typeof(GameEvent))
            : null;
        return new RecordFirstEventTickEdge(source, firstTick, reg.EventType, cond);
    }

    /// <summary>
    ///     The clip-start tick for a firing highlight: the earliest first-contributing-event tick among
    ///     the count stats its <paramref name="declaredReads" /> reference (Approach 1). Each count stat
    ///     registers a <c>{stat}.__first_tick</c> companion in its ruleset-private
    ///     <paramref name="localLookup" /> (see <see cref="MakeFirstTickNode" />); this reads them by
    ///     that key at fire time. Returns <c>null</c> when no read has a set companion — the safe
    ///     lead-in-only fallback (net-triggered counts, non-count / cross-ruleset reads, ambiguous
    ///     shapes). When several reads carry a set first-tick, the MIN is taken (the earliest kill).
    /// </summary>
    private static int? ResolveClipStartTick(
        IReadOnlyList<string> declaredReads,
        Dictionary<string, StateNode> localLookup)
    {
        int? best = null;
        foreach (string path in declaredReads)
        {
            if (!localLookup.TryGetValue($"{path}.__first_tick", out StateNode? candidate)
                || candidate is not ValueNode<int> tickNode)
            {
                continue;
            }

            int recorded = tickNode.Value;
            if (recorded != RecordFirstEventTickEdge.Sentinel && (best is null || recorded < best))
            {
                best = recorded;
            }
        }

        return best;
    }

    private static StateNode MakeValueNode(string name, RulesType valueType, bool roundScoped, string? subtitle) =>
        valueType.Kind switch
        {
            RulesTypeKind.Float => roundScoped
                ? new GenericRoundScopedValueNode<double>(name, 0.0, subtitle)
                : NewValueNode(name, subtitle, 0.0),
            RulesTypeKind.String => roundScoped
                ? new GenericRoundScopedValueNode<string>(name, "", subtitle)
                : NewValueNode<string>(name, subtitle, ""),
            // Int / Instant / Duration all hold an int tick/count at runtime.
            _ => roundScoped
                ? new GenericRoundScopedValueNode<int>(name, 0, subtitle)
                : NewValueNode(name, subtitle, 0)
        };

    private static GenericValueNode<T> NewValueNode<T>(string name, string? subtitle, T initial)
    {
        GenericValueNode<T> node = new(name, subtitle);
        node.SetValue(initial);
        return node;
    }

    private static ValueNode<IReadOnlyList<int>> MakeIntListNode(string name, bool roundScoped, string? subtitle) =>
        roundScoped
            ? new RoundScopedIntListCaptureNode(name, subtitle)
            : new IntListCaptureNode(name, subtitle);

    /// <summary>
    ///     Builds a copy-on-append list capture edge on a game event: on each qualifying fire the
    ///     target list becomes <c>old + element(evt)</c> (never mutated in place). The element and
    ///     condition compile through the same <see cref="ExpressionCompiler" /> path a scalar capture
    ///     uses; only the append is hand-written.
    /// </summary>
    private StateEdge CreateV2ListAppendEdge(EventRegistration reg, StateNode source,
        ValueNode<IReadOnlyList<int>> target, string elementExpr, string? condition, int slot)
    {
        Delegate element = ExpressionCompiler.CompileEventValueSelector(
            elementExpr, reg.EventType, typeof(int), reg.Fields, slot, null, ConditionNodes,
            _entityScanner, _perPlayerEntityProviders, parameterType: typeof(GameEvent));
        Delegate? cond = condition is not null
            ? ExpressionCompiler.CompileEventCondition(condition, reg.EventType, reg.Fields, slot, ConditionNodes,
                _currentPlayerTeam, _playerContextIndex, _entityScanner, _perPlayerEntityProviders,
                parameterType: typeof(GameEvent))
            : null;

        return (StateEdge)_listAppendEdgeMethod.MakeGenericMethod(reg.EventType)
            .Invoke(null, [source, target, element, cond])!;
    }

    /// <summary>
    ///     Builds a scalar min/max reduce edge on a game event (<c>capture: … , keep: min | max</c>,
    ///     pre-freeze gap G2) via <see cref="OnGameEventReduceValue{TEvent,TValue}" />. The element and
    ///     condition compile through the same <see cref="ExpressionCompiler" /> path a scalar capture
    ///     uses (so entity/enrichment reads resolve identically); the shared <paramref name="seen" />
    ///     flag drives the unseen→first-value initialization the runtime edge applies. The value slot is
    ///     <see cref="double" /> for a float-typed selector, else <see cref="int" /> — mirroring
    ///     <see cref="MakeValueNode" />'s node type, so the node and edge agree on <c>TValue</c>.
    /// </summary>
    private StateEdge CreateV2ScalarReduceEdge(EventRegistration reg, StateNode source, StateNode target,
        RulesType valueType, string elementExpr, string? condition, BoolNode seen, bool keepMax, int slot,
        IReadOnlyList<StateNode>? declaredReads)
    {
        Type tValue = valueType.Kind == RulesTypeKind.Float ? typeof(double) : typeof(int);
        Delegate element = ExpressionCompiler.CompileEventValueSelector(
            elementExpr, reg.EventType, tValue, reg.Fields, slot, null, ConditionNodes,
            _entityScanner, _perPlayerEntityProviders, parameterType: typeof(GameEvent));
        Delegate? cond = condition is not null
            ? ExpressionCompiler.CompileEventCondition(condition, reg.EventType, reg.Fields, slot, ConditionNodes,
                _currentPlayerTeam, _playerContextIndex, _entityScanner, _perPlayerEntityProviders,
                parameterType: typeof(GameEvent))
            : null;

        Type edgeType = typeof(OnGameEventReduceValue<,>).MakeGenericType(reg.EventType, tValue);
        return (StateEdge)Activator.CreateInstance(
            edgeType, source, target, element, cond, seen, keepMax, declaredReads)!;
    }

    /// <summary>
    ///     Builds a <c>bucket:</c> keyed-counter edge on a game event, reusing the v1
    ///     <see cref="KeyedCounterEdge{TEvent}" /> runtime. The key selector compiles through the same
    ///     <c>CompileEventKeySelector</c> path v1 uses (a string-valued event expression), and the
    ///     condition through the full v2 condition seam (enrichment + entity reads). When
    ///     <paramref name="valueExpr" /> is present (a <c>value:</c> summing bucket, C8's single-value
    ///     SUM reducer) it compiles a numeric delta selector through the exact
    ///     <c>CompileEventValueSelector</c> path v1's <c>keyed_counter</c> <c>add</c> uses, so an
    ///     enrichment amount like <c>enrich.hurt.capped_damage</c> resolves via the same enrichment
    ///     nodes; absent, the edge is a basic count (+1 per event).
    ///     <para>
    ///         A composite (multi-part) <paramref name="keyParts" /> compiles one key selector per part
    ///         and joins their per-event strings length-framed (each part prefixed by its char length +
    ///         a Unit Separator <c>U+001F</c>), a collision-proof tuple key no visible delimiter could
    ///         forge (weapon names etc. may contain any printable char). A single part is fed through
    ///         unchanged — byte-identical to the original single-key bucket (its runtime bucket keys, and
    ///         therefore its output rows, do not gain any framing).
    ///     </para>
    /// </summary>
    private StateEdge CreateV2KeyedCounterEdge(EventRegistration reg, StateNode source,
        KeyedCounterNode target, IReadOnlyList<string> keyParts, string? valueExpr, string? condition, int slot)
    {
        Delegate keySelector;
        if (keyParts.Count == 1)
        {
            keySelector = ExpressionCompiler.CompileEventKeySelector(
                keyParts[0], reg.EventType, reg.Fields, slot, ConditionNodes, parameterType: typeof(GameEvent));
        }
        else
        {
            Delegate[] partSelectors = new Delegate[keyParts.Count];
            for (int i = 0; i < keyParts.Count; i++)
            {
                partSelectors[i] = ExpressionCompiler.CompileEventKeySelector(
                    keyParts[i], reg.EventType, reg.Fields, slot, ConditionNodes, parameterType: typeof(GameEvent));
            }

            keySelector = CombineKeySelectors(partSelectors);
        }

        Delegate? deltaSelector = valueExpr is not null
            ? ExpressionCompiler.CompileEventValueSelector(
                valueExpr, reg.EventType, typeof(double), reg.Fields, slot, null,
                ConditionNodes, _entityScanner, _perPlayerEntityProviders, parameterType: typeof(GameEvent))
            : null;
        Delegate? cond = condition is not null
            ? ExpressionCompiler.CompileEventCondition(condition, reg.EventType, reg.Fields, slot, ConditionNodes,
                _currentPlayerTeam, _playerContextIndex, _entityScanner, _perPlayerEntityProviders,
                parameterType: typeof(GameEvent))
            : null;

        Type edgeType = typeof(KeyedCounterEdge<>).MakeGenericType(reg.EventType);
        return (StateEdge)Activator.CreateInstance(
            edgeType, source, target, keySelector, deltaSelector, cond, /*suppressionGuard*/ null)!;
    }

    /// <summary>
    ///     Maps a resolved bucket reducer name (<see cref="CheckedStat.BucketReducer" />) to the engine's
    ///     <see cref="KeyedReduceMode" /> (C8 named reducers). <c>null</c> (an implicit/explicit count) and
    ///     <c>sum</c> both accumulate (<see cref="KeyedReduceMode.Add" />) — the value the edge supplies
    ///     differs (+1 vs. the value: amount), not the fold — so they and the v1 path stay byte-identical.
    /// </summary>
    private static KeyedReduceMode MapKeyedReduceMode(string? reducer) =>
        reducer switch
        {
            null or "sum" or "count" => KeyedReduceMode.Add,
            "min" => KeyedReduceMode.Min,
            "max" => KeyedReduceMode.Max,
            "last" => KeyedReduceMode.Last,
            "first" => KeyedReduceMode.First,
            _ => throw new InvalidOperationException(
                $"v2 planner: unknown bucket reducer '{reducer}' (structural validation should have rejected it).")
        };

    /// <summary>
    ///     Combines per-part key selectors into a single composite-key selector (C8 composite bucket
    ///     keys). Each part's per-event string is length-framed — <c>&lt;len&gt;U+001F&lt;part&gt;</c> —
    ///     and concatenated in author order, so the resulting key is a collision-proof, order-bearing
    ///     tuple: two different tuples (or the same parts in a different order) never render to the same
    ///     string, whatever printable content the parts contain.
    /// </summary>
    private static Func<GameEvent, string> CombineKeySelectors(Delegate[] parts)
    {
        Func<GameEvent, string>[] typed = new Func<GameEvent, string>[parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            typed[i] = (Func<GameEvent, string>)parts[i];
        }

        return evt =>
        {
            StringBuilder sb = new();
            foreach (Func<GameEvent, string> sel in typed)
            {
                string part = sel(evt) ?? string.Empty;
                sb.Append(part.Length).Append('\u001f').Append(part);
            }

            return sb.ToString();
        };
    }

    private static OnGameEventSetValue<TEvent, IReadOnlyList<int>> CreateListAppendEdgeGeneric<TEvent>(
        StateNode source, ValueNode<IReadOnlyList<int>> target,
        Delegate elementSelector, Delegate? condition) where TEvent : class
    {
        Func<GameEvent, int> element = (Func<GameEvent, int>)elementSelector;
        Func<GameEvent, bool>? cond = (Func<GameEvent, bool>?)condition;
        return new OnGameEventSetValue<TEvent, IReadOnlyList<int>>(
            source, target, evt => AppendCopy(target.Value, element(evt)), cond);
    }

    private static int[] AppendCopy(IReadOnlyList<int>? old, int item)
    {
        if (old is null || old.Count == 0)
        {
            return [item];
        }

        int[] next = new int[old.Count + 1];
        for (int i = 0; i < old.Count; i++)
        {
            next[i] = old[i];
        }

        next[old.Count] = item;
        return next;
    }

    private static bool IsComparison(BinaryOperator op) =>
        op is BinaryOperator.Equal or BinaryOperator.NotEqual or BinaryOperator.Greater
            or BinaryOperator.GreaterOrEqual or BinaryOperator.Less or BinaryOperator.LessOrEqual;

    private static string OpText(BinaryOperator op) =>
        op switch
        {
            BinaryOperator.Equal => "==",
            BinaryOperator.NotEqual => "!=",
            BinaryOperator.Greater => ">",
            BinaryOperator.GreaterOrEqual => ">=",
            BinaryOperator.Less => "<",
            BinaryOperator.LessOrEqual => "<=",
            _ => throw new InvalidOperationException($"not a comparison operator: {op}")
        };

    private static string Flip(BinaryOperator op) =>
        op switch
        {
            BinaryOperator.Equal => "==",
            BinaryOperator.NotEqual => "!=",
            BinaryOperator.Greater => "<",
            BinaryOperator.GreaterOrEqual => "<=",
            BinaryOperator.Less => ">",
            BinaryOperator.LessOrEqual => ">=",
            _ => throw new InvalidOperationException($"not a comparison operator: {op}")
        };

    /// <summary>
    ///     Replaces every whole-token occurrence of a (possibly dotted) identifier path in a v1-grammar
    ///     expression string with an alias, not matching when the path is a sub-path of a longer
    ///     identifier chain (guarded by identifier/dot lookaround). Used by the v2 <c>compute:</c>
    ///     lowering to swap a v2 dotted reference path for a bare alias the node-expression compiler
    ///     can tokenize.
    /// </summary>
    private static string ReplaceWholeIdentifier(string expression, string path, string alias) =>
        Regex.Replace(expression, $@"(?<![\w.]){Regex.Escape(path)}(?![\w.])", alias);

    /// <summary>
    ///     Rewrites each v2 entity-provider read path in a rendered v1-grammar condition/value string
    ///     to the v1 provider spelling the runtime <see cref="ExpressionCompiler" /> resolves (the
    ///     additive v2-only counterpart to the v1 provider threading). The
    ///     resolver records the read as its v2 dotted path (<c>player.health</c>) plus the catalog
    ///     provider name (<c>entity.pawn.health</c>); the v1 grammar spells a per-player entity read as
    ///     <c>player.&lt;provider-name&gt;</c> (<c>player.entity.pawn.health</c>, stripped back to the
    ///     provider name by <c>ExpressionCompiler</c>). Only the paths in
    ///     <see cref="CheckedStat.EntityReads" /> are rewritten — <c>player.slot</c>/<c>.team</c>/
    ///     <c>.name</c> are loader-injected members, never entity reads, so they pass through. This
    ///     touches only the v2 planner path; the v1 builder is untouched, so pure-v1 stays byte-identical.
    /// </summary>
    private static string? RewriteEntityReads(string? expression, CheckedStat stat)
    {
        if (expression is null || stat.EntityReads.Count == 0)
        {
            return expression;
        }

        string result = expression;
        foreach (EntityProviderReference read in stat.EntityReads)
        {
            // A player.* read is keyed by the per-player chain's compile-time slot: `player.<provider>`
            // (ExpressionCompiler's `player` branch -> ResolvePlayerEntity, fixed slot). A role-handle read
            // is keyed by the role's event slot-field read PER FIRE: `<SlotField>.<provider>` (e.g.
            // `VictimSlot.entity.pawn.health`) — the event-subject entity grammar the compiler resolves via
            // GetPreFrameValue at the slot read from the event. RoleSlotField is always populated for a
            // non-player subject (resolve-time), so fall back loudly if it is somehow absent.
            string v1Spelling = read.Subject == EntityProviderReference.PlayerSubject
                ? $"player.{read.ProviderName}" // player.entity.pawn.health
                : $"{read.RoleSlotField // VictimSlot.entity.pawn.health
                     ?? throw new InvalidOperationException(
                         $"role-handle entity read '{read.Path}' has no resolved event slot-field")}."
                  + read.ProviderName;
            result = ReplaceWholeIdentifier(result, read.Path, v1Spelling);
        }

        return result;
    }

    /// <summary>
    ///     True when the stat's kind lowers to a game-event edge whose <c>condition</c> is compiled via
    ///     <see cref="ExpressionCompiler.CompileEventCondition" /> (with the entity-provider seam) — the
    ///     kinds a folded entity-bearing <c>while:</c> predicate can safely ride. Excludes
    ///     condition-ignoring kinds (<c>tally:</c>, <c>compute:</c>, <c>rate:</c>) and the eventless
    ///     forms (<c>flag: when:</c>, count-on-flag) so an entity <c>while:</c> is never silently dropped
    ///     there — those keep the loud ResolveGateSource fallback.
    /// </summary>
    private static bool KindThreadsEventCondition(CheckedStat stat) =>
        stat.ConcreteEvents.Count > 0 && stat.Kind is
            RuleNodeKind.Count or RuleNodeKind.Sum or RuleNodeKind.Capture
            or RuleNodeKind.Flag or RuleNodeKind.Streak or RuleNodeKind.Bucket or RuleNodeKind.Burst;

    /// <summary>
    ///     Detects an entity-bearing <c>while:</c> comparison and renders it to the v1 event-condition
    ///     grammar (subject entity reads rewritten to their <c>player.entity.pawn.*</c> spelling). Returns
    ///     <c>true</c> only when the comparison actually references a resolved entity read — i.e.
    ///     <see cref="RewriteEntityReads" /> rewrote at least one identifier — so a non-entity comparison
    ///     (a B6 aggregate / sibling gate) leaves this false and takes the unchanged node-predicate path.
    /// </summary>
    private static bool TryFoldEntityWhileGate(BinaryNode cmp, CheckedStat stat, out string? folded)
    {
        string v1 = V1ExpressionWriter.Write(cmp);
        string rewritten = RewriteEntityReads(v1, stat)!;
        if (!string.Equals(v1, rewritten, StringComparison.Ordinal))
        {
            folded = rewritten;
            return true;
        }

        folded = null;
        return false;
    }
}
