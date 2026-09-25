#region

using System.Globalization;
using System.Linq.Expressions;
using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Analysis.Config;
using CS2DemoKit.Analysis.Edges;
using CS2DemoKit.Analysis.Graphs;
using CS2DemoKit.Analysis.Nodes;
using CS2DemoKit.Analysis.Plugins;
using CS2DemoKit.Analysis.Profiles;
using CS2DemoKit.Analysis.Registry;
using CS2DemoKit.Analysis.RulesetsV2.Compile;
using CS2DemoKit.Analysis.RulesetsV2.Resolve;
using CS2DemoKit.Analysis.Visibility;
using CS2DemoKit.Parser;
using CS2DemoKit.Parser.GameEvents;

#endregion

#pragma warning disable CA2263

namespace CS2DemoKit.Analysis.Building;

/// <summary>
///     Compiles the built-in context rules plus the checked Rulesets v2 documents into a runnable
///     <see cref="StateGraph" /> plus descriptor metadata. Wires up nodes, edges, enrichment, and
///     entity providers.
/// </summary>
/// <remarks>
///     The Rulesets v2 planner lives in the partial <c>RuleChainBuilder.RulesetsV2.cs</c>. The
///     internal recipe types under <c>Analysis.Config</c> (<see cref="RuleChainDef" /> /
///     <see cref="RuleDef" /> / <see cref="TriggerDef" />) are the builder's node-recipe IR — used
///     by <see cref="BuiltinContexts" /> and the v2 lowering; there is no user-facing v1 config
///     format any more.
/// </remarks>
public sealed partial class RuleChainBuilder
{
    // Folds duration literals; from the build target, never from a whole demo.
    private readonly int _tickRate;
    private readonly EntityValueProviderRegistry? _entityProviders;
    private readonly LogicalEventResolver _logicalResolver;
    private readonly PerPlayerEntityValueProviderRegistry? _perPlayerEntityProviders;
    private readonly EventRegistry _registry;

    // The per-player equipment provider, set (and snapshotted by the scanner) only when a v2 ruleset
    // reads round.team.equipment / round.enemies.equipment — the B6 freeze-end economy maintenance
    // edge sums it. Null otherwise, so no economy nodes/edges are built (and GetPreFrameValue, which
    // throws on an un-snapshotted provider, is never reached).
    private IPerPlayerEntityValueProvider? _b6EquipmentProvider;

    // The per-player cash provider, set (and snapshotted) only when a v2 ruleset reads
    // round.team.money / round.enemies.money. The same freeze-end edge sums it.
    private IPerPlayerEntityValueProvider? _b6MoneyProvider;

    private int? _currentPlayerTeam;

    // Combined lookup passed to ExpressionCompiler. Initialised from
    // enrichment.NodeLookup at start of Build, then mutated as each
    // rule node is created so trigger conditions can reference earlier
    // rules' .Value (e.g. condition: "last_round_of_half == true").
    private Dictionary<string, object>? _enrichmentNodes;

    // ContextName → value node, populated only for providers the rule config references
    // (lazy activation). Drives entity-edge construction in CreateEdge().
    private Dictionary<string, StateNode>? _entityContextNodes;

    // The scanner backing `player.entity.*` reads, set once the scanner is built (or left null
    // when no entity providers are registered). Read by per-player compile sites in CreateGameEventEdge.
    private EntityChangeScanner? _entityScanner;
    private PlayerContextIndex? _playerContextIndex;

    // The current build's stand-ins for the state outside the graph. Set at the start of Build; the
    // per-player template factory captures its own reference, so a later Build cannot swap them
    // under an already-registered template.
    private GraphExternals _externals = new();

    // Where the current build made each game-scope node (its team-scope nodes included), stamped
    // around each block that adds nodes. Set at the start of Build and dropped at its end.
    private ProvenanceRecorder _gameProvenance = new();

    // Baked map collision for the visibility rising-edge scan, or null when the caller supplied
    // none. Null is not an error: it means enemy_spotted cannot be produced for this run, which is
    // the same shape as a profile that does not bind an event.
    private readonly VisibilityEngine? _visibilityEngine;

    // Per-slot condition-node overlay for the v2 per-player template (gap G1, event-gated per-player
    // aggregate reads). While a v2 slot's stats/highlights are being built, this holds a SUPERSET of
    // _enrichmentNodes that also exposes the subject slot's per-player context / B6 aggregate nodes
    // (keyed by their v1 rule id), so a where:-condition read of player.survived / round.enemies.alive
    // — lowered by V1ExpressionWriter to the bare rule id — resolves against the SUBJECT's node.
    // Null everywhere else, so the v1 path (and v2 value selectors outside a slot) reads the shared
    // _enrichmentNodes unchanged. Set/cleared per slot in BuildV2PerPlayerTemplate; the sequential
    // materialize contract (same as _currentPlayerTeam) means no two slots race on it.
    private Dictionary<string, object>? _v2ConditionNodeOverlay;

    /// <param name="registry">Event / net-message registry used to resolve trigger names to CLR types.</param>
    /// <param name="target">The tick rate and resolved profile the graph is built for; the defaults are 64 and the fallback profile.</param>
    /// <param name="entityProviders">Optional singleton-entity providers (game-rules etc.).</param>
    /// <param name="perPlayerEntityProviders">Optional per-player entity providers (pawn health, active weapon, etc.).</param>
    /// <param name="visibilityEngine">
    ///     Optional baked map collision. Supplying it is what makes the synthesized
    ///     <c>enemy_spotted</c> event producible: visibility is recomputed from geometry, never read
    ///     off the wire, so with no geometry there is nothing to recompute and any rule subscribing to
    ///     the event simply never fires. The builder does no file I/O and knows nothing about where
    ///     bakes live (see <c>CollisionAssetLocator</c>): the caller loads and injects, exactly as
    ///     <c>VisibilityAnalyzer.Analyze</c> requires.
    /// </param>
    public RuleChainBuilder(
        EventRegistry registry,
        AnalysisTarget? target = null,
        EntityValueProviderRegistry? entityProviders = null,
        PerPlayerEntityValueProviderRegistry? perPlayerEntityProviders = null,
        VisibilityEngine? visibilityEngine = null)
    {
        _registry = registry;
        _entityProviders = entityProviders;
        _perPlayerEntityProviders = perPlayerEntityProviders;
        _visibilityEngine = visibilityEngine;
        _tickRate = target?.TickRate ?? 64;
        _logicalResolver = new LogicalEventResolver(target?.Profile ?? DemoSourceProfileRegistry.DefaultFallback);
    }

    // The node lookup ExpressionCompiler binds condition/value identifiers against: the per-slot v2
    // overlay when one is active, else the shared game-scoped enrichment lookup. v1 never sets the
    // overlay, so v1 compiles are byte-identical.
    private Dictionary<string, object>? ConditionNodes => _v2ConditionNodeOverlay ?? _enrichmentNodes;

    /// <summary>The active engine-side profile resolved for this build.</summary>
    public DemoSourceProfile Profile => _logicalResolver.Profile;

    /// <summary>
    ///     Materializes the built-in context rules and every ruleset in <paramref name="rulesets" />
    ///     into nodes, edges, and descriptors; wires them into one <see cref="StateGraph" />; and
    ///     returns the build output ready to evaluate. The v2 entity read sets union with the
    ///     built-in contexts' before the scanner is constructed. Passing no rulesets builds the
    ///     bare context/enrichment graph.
    /// </summary>
    /// <param name="rulesets">The build-time-resolved v2 rulesets (null/empty = contexts only).</param>
    /// <param name="options">Planner options (C7 env vs constant lowering); defaults to constant lowering.</param>
    /// <returns>The composed build output.</returns>
    public BuildResult Build(IReadOnlyList<CheckedRuleset>? rulesets = null,
        RulesetCompilerOptions? options = null)
    {
        rulesets ??= [];
        options ??= RulesetCompilerOptions.Default;
        List<RulesetCoverageDiagnostic> v2Coverage = [];
        foreach (CheckedRuleset ruleset in rulesets)
        {
            v2Coverage.AddRange(ruleset.Coverage);
        }

        StateGraph graph = new();
        GraphExternals externals = new();
        _externals = externals;

        // Every game-scope edge and its descriptors go through here, forwarded to the graph in call
        // order.
        GraphWiring wiring = new(_registry, externals, graph);
        ProvenanceRecorder provenance = new();
        _gameProvenance = provenance;
        _teamNodesByRuleId.Clear();
        _teamRosters.Clear();
        _gameStatHashes.Clear();
        Dictionary<string, StateNode> nodeLookup = new(StringComparer.OrdinalIgnoreCase)
        {
            ["root"] = graph.Root
        };

        List<StateNode> allNodes = new()
        {
            graph.Root
        };
        provenance.Stamp(allNodes, 0, RuleGraphNodeOrigin.Root);
        HashSet<Type> relevantTypes = new();

        // ── Build player-context index (consumed by CreateEnrichment below) ──
        PlayerContextIndex playerContextIndex = new();
        _playerContextIndex = playerContextIndex;

        List<RuleChainDef> builtinContexts = BuiltinContexts.GenerateContextRules();

        // A ruleset that is not per-player has no template to materialize players through, yet its
        // enrichments (enemy facets, the round-end winner) read live teams and alive state.
        graph.TracksPlayers = rulesets.Any(rs => rs.For != RulesetsV2.Model.RulesetScope.EachPlayer);

        // Rule-id → node map exposed for configured-output metric resolution (game scope).
        // Bare rule ids mirror nodeLookup; "chain.rule" qualified aliases are added per chain.
        Dictionary<string, StateNode> gameNodesByRuleId = new(StringComparer.OrdinalIgnoreCase);

        // ── Lazy activation: scan rule references against registered entity providers ──
        // Walk every built-in context rule, substring-match each rule's
        // On/Condition/Value/Parents.When against every registered provider's ContextName.
        // For each matched provider, create its value node, insert into nodeLookup/_enrichmentNodes,
        // and stage it for the scanner. When zero providers are referenced AND no per-player
        // providers are registered, no scanner is built — BuildResult.EntityScanner stays null
        // and the evaluator's per-frame entity hook short-circuits on the null check.
        //
        // Order matters: the scanner must exist BEFORE CreateEnrichment so HurtTeamEnrichmentEdge
        // can be constructed with it. _enrichmentNodes is initialised empty here and gets the
        // singleton provider value nodes; CreateEnrichment's output is merged in afterwards.
        _enrichmentNodes = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

        EntityChangeScanner? entityScanner = null;
        List<IEntityValueProvider> matched = new();
        if (_entityProviders is { All.Count: > 0 } providerReg)
        {
            foreach (IEntityValueProvider provider in providerReg.All)
            {
                if (IsReferencedByBuiltins(provider.ContextName, builtinContexts))
                {
                    matched.Add(provider);
                }
            }
        }

        // The synthesized `enemy_spotted` event needs both halves: a rule that subscribes to it AND
        // baked map geometry to recompute visibility against. Computed here rather than beside the
        // molotov gate below because it also forces its six vantage providers into the digest, and
        // that has to happen before the provider loop closes.
        bool spottedSubscribed = IsReferencedByBuiltins("enemy_spotted", builtinContexts)
                                 || RulesetsSubscribeToEvent("enemy_spotted", rulesets);
        bool emitSpotted = _visibilityEngine is not null && spottedSubscribed;

        // The shot-anchored aim enrichments gate on their OUTPUT names, like the hurt enrichment
        // providers above: a rule reads `counter_strafe_good` off the shot view, never the six
        // entity columns AimShotContextEdge assembles it from. Unlike enemy_spotted this needs no
        // map bake (counter-strafing is a movement fact, not a visibility one), so the vantage
        // sampler it shares with the spot scan gets built for either reason.
        //
        // The names come from BuiltinContexts, which is where the nodes are built: a name the gate
        // does not know about leaves the edge inert while its node stays registered, so the stat
        // reading it reports the node's default on every event and nothing reports the miss.
        bool emitAimShotContext = false;
        foreach (string aimEnrichment in BuiltinContexts.AimShotEnrichments)
        {
            if (IsReferencedByBuiltins(aimEnrichment, builtinContexts)
                || IsReferencedByV2Reads(aimEnrichment, rulesets))
            {
                emitAimShotContext = true;
                break;
            }
        }

        bool needVantage = emitSpotted || emitAimShotContext;

        // Per-player providers are reference-gated by name (catalog width multiplies per-frame
        // capture work, so only referenced providers activate), and the gate has three tiers —
        // none of them the built-in context chains. Those are a fixed set (match_live,
        // round_active, alive, survived, traded) whose only entity reference is the SINGLETON
        // `entity.game.freeze_period`, so no per-player provider name can occur in them and a
        // builtin scan over one is dead weight rather than a safety net. Were such a read ever
        // added, the scanner's un-snapshotted-provider arm throws on the first read of it — the
        // gate omission surfaces loudly instead of quietly zeroing a column.
        //
        // A DIRECT read does not pass through the switch at all: a v2 `player.health` resolves to
        // its provider through the catalog's v2Name, and UnionV2EntityReads (below, before the
        // scanner is built) folds every such provider in. `entity.pawn.*` is not a spelling any
        // ruleset can write — there is no `entity` root — so a v2 read is always in v2 spelling.
        //
        // The switch covers the INDIRECT need: a provider no rule names, read by C# on the rule's
        // behalf. A rule reads `enrich.hurt.capped_damage`; HurtTeamEnrichmentEdge computes it from
        // the health column. The hurt enrichments carry no v2Name in the catalog, so TryEntityRead
        // rejects them and UnionV2EntityReads never sees the provider — without these arms the edge
        // falls back to the event-cache HP and diverges from the entity-snapshot HP, silently.
        // player_stats.rules.yaml's TotalEnemyDmg is the live case.
        //
        // The two `if`s below the switch are that argument one step further removed again: the rule
        // names an aim enrichment, the enrichment is assembled from six columns, and none of the six
        // appears anywhere in the ruleset text. Which is why a name missing from a gate list here
        // costs nothing at build time and everything at read time — the node stays registered and
        // reports its default.
        List<IPerPlayerEntityValueProvider> perPlayerList = [];
        bool healthNeeded = false, weaponNeeded = false, b6EquipmentNeeded = false, b6MoneyNeeded = false;

        // The plant-site round facts read the planter's place: an indirect need like the hurt
        // enrichments', so a ruleset naming any of the three gates the place column in.
        bool roundFactsNeeded = RoundFactIds.Members.Any(m => IsReferencedByV2Reads(m.V2Name, rulesets));
        if (_perPlayerEntityProviders is { All.Count: > 0 })
        {
            // B6 relative economy: a v2 read of round.team.equipment / round.enemies.equipment needs the
            // per-player equipment provider snapshotted so the freeze-end maintenance edge can sum it.
            b6EquipmentNeeded = IsReferencedByV2Reads("round.team.equipment", rulesets)
                                || IsReferencedByV2Reads("round.enemies.equipment", rulesets);
            b6MoneyNeeded = IsReferencedByV2Reads("round.team.money", rulesets)
                            || IsReferencedByV2Reads("round.enemies.money", rulesets);
            healthNeeded = IsReferencedByV2Reads("enrich.hurt.victim_health_before", rulesets)
                           || IsReferencedByV2Reads("enrich.hurt.capped_damage", rulesets);
            weaponNeeded = IsReferencedByV2Reads("enrich.hurt.attacker_active_weapon", rulesets);

            foreach (IPerPlayerEntityValueProvider provider in _perPlayerEntityProviders.All)
            {
                bool referenced = provider.Name switch
                {
                    "entity.pawn.health" => healthNeeded,
                    "entity.pawn.active_weapon_class" => weaponNeeded,
                    "entity.pawn.equipment_value" => b6EquipmentNeeded,
                    "entity.controller.money" => b6MoneyNeeded,
                    "entity.pawn.place" => roundFactsNeeded,
                    _ => false
                };

                // The vantage columns are gated in by the event that consumes them, not by a rule
                // naming them: a ruleset triggering on enemy_spotted references no provider at all,
                // and without this the scan would build from six empty columns and emit nothing.
                if (needVantage && AimVantageScanner.RequiredProviders.Contains(provider.Name))
                {
                    referenced = true;
                }

                // Same argument for the shot-context columns, one step further removed: the rule
                // names an enrichment, the enrichment is assembled from these six, and none of the
                // six appears anywhere in the ruleset text.
                if (emitAimShotContext && AimShotContextEdge.RequiredProviders.Contains(provider.Name))
                {
                    referenced = true;
                }

                if (referenced)
                {
                    perPlayerList.Add(provider);
                }
            }
        }

        // ── Union v2 entity reads BEFORE the scanner is constructed ──
        // A v2 player.*/role-handle read gates its provider exactly like a v1 config read; the
        // scanner must snapshot it, so the v2 provider set folds in here, before the scanner is
        // built — otherwise a v2 `player.health` read silently gates out. No-op for the pilot
        // (its only reads are enrichment/event fields, not entity providers).
        UnionV2EntityReads(rulesets, matched, perPlayerList);

        // The synthesized `molotov_thrown` event is produced by the scanner itself (not a
        // provider), so it also forces scanner construction when referenced — otherwise a build
        // whose only entity dependency is molotov attribution would leave the scanner null.
        // Both sources gate it: a builtin rule triggering on it (substring scan) AND a v2 stat whose
        // view resolves to it (exact ConcreteEvents match, e.g. the `molotov` grenade-usage view).
        bool emitMolotov = IsReferencedByBuiltins("molotov_thrown", builtinContexts)
                           || RulesetsSubscribeToEvent("molotov_thrown", rulesets);

        // Built only when something asks for them, so an ordinary run allocates none and the
        // per-frame consume short-circuits on the null check. The team callback is where liveness
        // enters: the digest columns cannot say whether a slot is a corpse, and PlayerContext already
        // tracks exactly that (alive from the death/spawn edges, connected from the disconnect
        // edges). A slot not yet materialized reads as ineligible, so the scan warms up with the
        // first player-scoped event of the demo, well before the first round's freeze end.
        //
        // The provider-presence re-check is not belt-and-braces: the vantage constructor throws on a
        // missing column (deliberately, so a silent column of nulls cannot masquerade as "nobody had
        // a position"), and a build with no per-player provider registry at all reaches here with the
        // gate set and the columns absent. Leaving both scanners null there degrades to the same
        // inert state as an unreferenced ruleset instead of failing the whole build.
        AimVantageScanner? vantageScanner = null;
        VisibilityTransitionScanner? transitionScanner = null;
        if (needVantage && HasAllProviders(perPlayerList, AimVantageScanner.RequiredProviders))
        {
            vantageScanner = new AimVantageScanner(
                perPlayerList.Select(p => p.Name).ToList(),
                slot => playerContextIndex.TryGet(slot, out PlayerContextIndex.PlayerContext? ctx)
                        && ctx!.Connected && ctx.IsAlive
                    ? ctx.Team
                    : -1,
                (double)_tickRate);

            if (emitSpotted)
            {
                transitionScanner = new VisibilityTransitionScanner(_visibilityEngine!);

                // The scanner's `visible` / on-target sets are match-lived, but a contact is a
                // per-round fact: at round end every pawn is alive and mostly in mutual sight, and a
                // pair already marked visible is skipped, so the next round's first real contact
                // would emit nothing. The index owns the round boundary, so it owns the reset —
                // this is the only place the two meet.
                playerContextIndex.VisibilityTransitions = transitionScanner;
            }
        }

        // Per-player templates seed each slot's team from its controller entity, so an each_player
        // ruleset needs the scanner even when it reads no entity value. A ruleset built onto the
        // graph (for: match) needs the same live teams for its enrichments and the round-end
        // winner, so any ruleset forces it.
        bool trackPlayers = rulesets.Count > 0;
        if (matched.Count > 0 || perPlayerList.Count > 0 || emitMolotov || trackPlayers)
        {
            // The round's winner is the server's verdict: the scanner synthesizes round_decided
            // from these three game-rules singletons, and the round-end enrichment reports the
            // latched winner rather than deriving one. So whenever there is a scanner they are
            // tracked, read or not: three indexed reads per frame on a cached proxy index. One no
            // rule reads is tracked silently: its value node updates, but no change marker is
            // dispatched for it, so a build that does not read them dispatches no extra messages.
            HashSet<IEntityValueProvider> silent = new(ReferenceEqualityComparer.Instance);
            foreach (string contextName in (ReadOnlySpan<string>)
                     [
                         EntityChangeScanner.RoundWinStatusContext,
                         EntityChangeScanner.RoundWinReasonContext,
                         EntityChangeScanner.TotalRoundsPlayedContext
                     ])
            {
                if (_entityProviders?.Get(contextName) is { } provider && !matched.Contains(provider))
                {
                    matched.Add(provider);
                    silent.Add(provider);
                }
            }

            _entityContextNodes = new Dictionary<string, StateNode>(StringComparer.OrdinalIgnoreCase);
            int entityMark = allNodes.Count;
            List<(IEntityValueProvider, StateNode)> trackedForScanner = new(matched.Count);
            foreach (IEntityValueProvider provider in matched)
            {
                StateNode valueNode = CreateEntityValueNode(provider);
                _entityContextNodes[provider.ContextName] = valueNode;
                nodeLookup[provider.ContextName] = valueNode;
                _enrichmentNodes[provider.ContextName] = valueNode;
                allNodes.Add(valueNode);
                wiring.DescribeEntityValue(valueNode, provider.ContextName);
                trackedForScanner.Add((provider, valueNode));
            }

            provenance.Stamp(allNodes, entityMark, RuleGraphNodeOrigin.EntityValue);

            entityScanner = new EntityChangeScanner(
                new EntityStateLayer { StoreUnlensedFields = false },
                trackedForScanner,
                perPlayerList,
                emitMolotov,
                vantageScanner,
                transitionScanner,
                silent);
        }

        // Expose the scanner to per-player compile sites so `player.entity.*` references resolve
        // (see CreateGameEventEdge). Stays null when no entity providers are registered, in which
        // case any such reference is a clean compile-time error.
        _entityScanner = entityScanner;

        ReportUnproducibleSpotReads(rulesets, v2Coverage, spottedSubscribed,
            entityScanner is not null && transitionScanner is not null);

        // The equipment provider was gated into perPlayerList (hence snapshotted) exactly when a v2
        // round.*.equipment read requires it; capture it for the B6 freeze-end economy edge. Null (no
        // economy edge) when unreferenced or when the scanner wasn't built.
        _b6EquipmentProvider = b6EquipmentNeeded && entityScanner is not null
            ? _perPlayerEntityProviders?.Get("entity.pawn.equipment_value")
            : null;
        _b6MoneyProvider = b6MoneyNeeded && entityScanner is not null
            ? _perPlayerEntityProviders?.Get("entity.controller.money")
            : null;

        // ── Create enrichment infrastructure ──────────────────────────────
        // Now that the scanner exists, HurtTeamEnrichmentEdge can be wired with it. The
        // enrichment providers MUST stay in lockstep with the gated scanner list above:
        // GetPreFrameValue now throws on a provider the scanner doesn't snapshot (the B5
        // loud arm), so handing enrichment an ungated provider would fail at eval time.
        IPerPlayerEntityValueProvider? pawnHealthProvider = healthNeeded
            ? _perPlayerEntityProviders?.Get("entity.pawn.health")
            : null;
        IPerPlayerEntityValueProvider? activeWeaponProvider = weaponNeeded
            ? _perPlayerEntityProviders?.Get("entity.pawn.active_weapon_class")
            : null;

        // Same lockstep rule as the two providers above, six times over: every read named here was
        // force-gated into perPlayerList by the emitAimShotContext arm of the provider loop, so it
        // is snapshotted and GetPreFrameValue will resolve it. All four conditions have to hold
        // together (the gate, the scanner, the vantage sampler, and the whole required set present),
        // and when any is missing the edge is handed null and stays inert rather than throwing on
        // its first shot.
        AimShotContextSources? aimShotSources = null;
        if (emitAimShotContext && entityScanner is not null && vantageScanner is not null
            && HasAllProviders(perPlayerList, AimShotContextEdge.RequiredProviders))
        {
            aimShotSources = new AimShotContextSources(
                entityScanner,
                _perPlayerEntityProviders!.Get("entity.pawn.max_speed")!,
                _perPlayerEntityProviders.Get("entity.weapon.recoil_index")!,
                _perPlayerEntityProviders.Get(AimVantageScanner.EyePitchProvider)!,
                _perPlayerEntityProviders.Get(AimVantageScanner.EyeYawProvider)!,
                _perPlayerEntityProviders.Get("entity.pawn.punch_pitch")!,
                _perPlayerEntityProviders.Get("entity.pawn.punch_yaw")!,
                vantageScanner,
                transitionScanner,
                (double)_tickRate);
        }

        BuiltinContexts.EnrichmentInfrastructure enrichment = BuiltinContexts.CreateEnrichment(
            graph.Root, playerContextIndex, _registry, _logicalResolver,
            entityScanner, pawnHealthProvider, activeWeaponProvider, aimShotSources,
            (double)_tickRate);
        foreach ((string key, StateNode node) in enrichment.NodeLookup)
        {
            nodeLookup[key] = node;
            _enrichmentNodes[key] = node;
        }

        int enrichmentMark = allNodes.Count;
        allNodes.AddRange(enrichment.Nodes);
        provenance.Stamp(allNodes, enrichmentMark, RuleGraphNodeOrigin.Enrichment);
        foreach (StateEdge edge in enrichment.Edges)
        {
            // The bookkeeping edges write per-player state only; the rest write enrichment nodes.
            GraphEdgeKind kind = edge.WrittenNode is null && edge.AdditionalWrittenNodes is null
                ? GraphEdgeKind.PlayerState
                : GraphEdgeKind.Enrichment;
            wiring.AddEdge(edge, kind);
        }

        if (roundFactsNeeded)
        {
            int factsMark = allNodes.Count;
            BuildRoundFacts(graph, wiring, nodeLookup, allNodes, gameNodesByRuleId, relevantTypes, entityScanner);
            provenance.Stamp(allNodes, factsMark, RuleGraphNodeOrigin.RoundFact);
        }

        List<RuleChainDef> gameContexts = builtinContexts.Where(c => c.Scope == ChainScope.Game).ToList();
        List<RuleChainDef> perPlayerContexts = builtinContexts.Where(c => c.Scope == ChainScope.PerPlayer).ToList();

        // ── Build game-scoped context rules ────────────────────────────────
        int contextMark = allNodes.Count;
        foreach (RuleChainDef ctx in gameContexts)
        {
            foreach (RuleDef rule in ctx.Rules)
            {
                if (!RequiresSatisfied(rule))
                {
                    continue;
                }

                BuildSingletonRule(rule, graph, wiring, nodeLookup, allNodes, relevantTypes);
                if (nodeLookup.TryGetValue(rule.Id, out StateNode? ctxNode))
                {
                    // Context rules (round_number, gameplay_phase, …) resolve by bare id only.
                    gameNodesByRuleId[rule.Id] = ctxNode;
                }
            }
        }

        provenance.Stamp(allNodes, contextMark, RuleGraphNodeOrigin.Context);

        // ── Rulesets v2: build v2 nodes onto the same graph ──
        // After the context/enrichment graph is wired, so the game contexts (incl. bomb_was_planted)
        // and enrichment nodes the v2 nodes read are already in nodeLookup. The per-player CONTEXT
        // rules (alive/survived/traded) materialize ONLY inside the v2 per-player template — with the
        // v1 chain layer removed there is no separate v1 per-player template any more.
        List<OutputDef> v2Outputs = [];
        if (rulesets.Count > 0)
        {
            // Per-player context bridge: thread the per-player CONTEXT
            // RuleDefs (alive/survived/traded — Scope==PerPlayer) into the v2 template so a v2
            // when: read of player.survived / .traded resolves through the same nodes v1 builds.
            List<RuleDef> perPlayerContextRules = perPlayerContexts
                .SelectMany(c => c.Rules)
                .Where(RequiresSatisfied)
                .ToList();
            BuildRulesetsV2(rulesets, options, graph, wiring, nodeLookup, allNodes,
                gameNodesByRuleId, relevantTypes, v2Outputs, perPlayerContextRules);
        }

        relevantTypes.Add(typeof(PlayerDeathEvent));
        relevantTypes.Add(typeof(PlayerConnectEvent));
        relevantTypes.Add(typeof(PlayerTeamEvent));
        // Connectivity lifecycle events for the disconnect-ghost defect fix (PlayerContext.Connected).
        // Neither is referenced by a context-rule trigger, so they must be marked relevant explicitly
        // or the connectivity edges never see them. Neither participates in player materialization
        // (ExtractPlayerSlots has no case for them), so this only feeds the connectivity edges.
        relevantTypes.Add(typeof(PlayerDisconnectEvent));
        relevantTypes.Add(typeof(PlayerSpawnEvent));

        // Every game-scope row a StateEdge backs, several rows to one edge for a multi-write edge;
        // drives edge graph-breakpoints. By reference, like the descriptors themselves.
        Dictionary<GraphEdgeDescriptor, StateEdge> edgeBacking = new(ReferenceEqualityComparer.Instance);
        foreach (GraphEdgeDescriptor descriptor in wiring.Descriptors)
        {
            if (descriptor.Edge is { } backing)
            {
                edgeBacking[descriptor] = backing;
            }
        }

        // The per-player template keeps this builder alive for as long as the build lives, so the
        // recorder is dropped once the result has its copy of the entries.
        IReadOnlyDictionary<StateNode, NodeProvenance> gameProvenance = provenance.ToDictionary();
        _gameProvenance = new ProvenanceRecorder();
        return new BuildResult(graph, allNodes, wiring.Descriptors,
            relevantTypes, playerContextIndex, entityScanner,
            edgeBacking.Count > 0 ? edgeBacking : null,
            gameNodesByRuleId.Count > 0 ? gameNodesByRuleId : null,
            v2Outputs.Count > 0 ? v2Outputs : null,
            v2Coverage.Count > 0 ? v2Coverage : null)
        {
            Profile = Profile,
            Events = _registry,
            TeamNodesByRuleId = _teamNodesByRuleId.Count > 0 ? new Dictionary<int, IReadOnlyDictionary<string, StateNode>>(_teamNodesByRuleId) : null,
            TeamRosterNodes = _teamRosters.Count > 0 ? new Dictionary<int, StateNode>(_teamRosters) : null,
            RoundBoundaryTypes = RoundBoundaryTypes(),
            ExternalNodes = externals.All,
            Provenance = gameProvenance
        };
    }

    /// <summary>
    ///     The dispatch types that can move <c>round_number</c> (see
    ///     <see cref="BuildResult.RoundBoundaryTypes" />): the concrete events of the logical events its
    ///     own triggers and its <c>match_live</c> parent's triggers name, plus the two events the
    ///     evaluator itself treats as boundaries.
    /// </summary>
    private HashSet<Type> RoundBoundaryTypes()
    {
        HashSet<Type> types = [typeof(RoundFreezeEndEvent), typeof(BeginNewMatchEvent)];
        foreach (string logical in (string[])["round_freeze_end", "match_start", "match_end"])
        {
            foreach (string concrete in _logicalResolver.Resolve(logical)?.ConcreteEventNames ?? [])
            {
                if (_registry.TryResolve(concrete, out Type? type))
                {
                    types.Add(type);
                }
            }
        }

        return types;
    }

    /// <summary>
    ///     Builds the plant-site round facts (<see cref="RoundFactIds" />): three round-scoped value
    ///     nodes and the <see cref="BombPlantSiteEdge" /> that writes them on <c>bomb_planted</c>.
    ///     Registered as graph rule nodes, so the evaluator resets them at each freeze end, and in the
    ///     lookups under their node ids, so a v2 read of <c>round.bomb.site</c> resolves through the
    ///     catalog context table like any other context. Only built when a ruleset reads one.
    /// </summary>
    private void BuildRoundFacts(StateGraph graph, GraphWiring wiring, Dictionary<string, StateNode> nodeLookup,
        List<StateNode> allNodes, Dictionary<string, StateNode> gameNodesByRuleId, HashSet<Type> relevantTypes,
        EntityChangeScanner? scanner)
    {
        GenericRoundScopedValueNode<string> site = new(RoundFactIds.BombSite, "", null);
        GenericRoundScopedValueNode<string> plantPlace = new(RoundFactIds.BombPlantPlace, "", null);
        GenericRoundScopedValueNode<int> siteEntity = new(RoundFactIds.BombSiteEntity, -1, null);

        foreach (StateNode node in (StateNode[])[site, plantPlace, siteEntity])
        {
            nodeLookup[node.Name] = node;
            _enrichmentNodes![node.Name] = node;
            gameNodesByRuleId[node.Name] = node;
            allNodes.Add(node);
            graph.AddRuleNode(node);
        }

        // The place provider was gated in by roundFactsNeeded, so the scanner snapshots it.
        IPerPlayerEntityValueProvider? place = scanner is not null ? _perPlayerEntityProviders?.Get("entity.pawn.place") : null;
        Func<int, string?>? readPlace = place is not null
            ? slot => scanner!.GetPreFrameValue(place, slot) as string
            : null;

        wiring.AddEdge(new BombPlantSiteEdge(graph.Root, site, plantPlace, siteEntity, readPlace), GraphEdgeKind.RoundFact,
            extraReads: readPlace is not null ? [wiring.Externals.EntityState] : null);
        relevantTypes.Add(typeof(BombPlantedEvent));
    }

    internal static string ResolveContextId(string contextPath)
    {
        return contextPath switch
        {
            "context.round.active" => "round_active",
            "context.round.gameplay_phase" => "gameplay_phase",
            "context.round.bomb_status" => "bomb_status",
            "context.round.number" => "round_number",
            "context.round.no_deaths" => "no_deaths_yet",
            "context.player.alive" => "alive",
            "context.player.survived" => "survived",
            "context.player.traded" => "traded",
            "context.match.map" => "map_name",
            // context.match.tick was removed with the current_game_tick plugin (the alias resolved
            // to a node that no longer exists, so it could only ever produce an unknown-parent
            // error). Rules that need the tick read event fields (e.g. enrich.* tick captures).
            "context.match.live" => "match_live",
            "context.match.regulation_status" => "regulation_status",
            "context.match.half_state" => "half_state",
            _ => contextPath
        };
    }

    // ── Auto-activate rules (conjunction/disjunction from parents) ──────

    private static StateNode BuildAutoActivateRule(RuleDef rule, Dictionary<string, StateNode> nodeLookup)
    {
        ParentsDef parents = rule.Parents!;
        string displayName = rule.Name ?? rule.Id;

        IConditionalEdge[] conditionalEdges = parents.Rules.Select(parentRef =>
        {
            if (!nodeLookup.TryGetValue(ResolveContextId(parentRef.RuleId), out StateNode? sourceNode))
            {
                throw new InvalidOperationException(
                    $"Auto-activate rule '{rule.Id}' references unknown parent '{parentRef.RuleId}'.");
            }

            return CreateConditionalEdge(sourceNode, parentRef.When, parentRef.When);
        }).ToArray();

        return parents.Mode switch
        {
            ParentMode.Any => new DisjunctionNode(displayName, conditionalEdges),
            _ => new ConjunctionNode(displayName, conditionalEdges)
        };
    }

    private static StateNode BuildAutoActivateRuleLocal(RuleDef rule, Dictionary<string, StateNode> localLookup)
    {
        ParentsDef parents = rule.Parents!;
        string displayName = rule.Name ?? rule.Id;

        IConditionalEdge[] conditionalEdges = parents.Rules.Select(parentRef =>
        {
            string resolvedId = ResolveContextId(parentRef.RuleId);
            if (!localLookup.TryGetValue(resolvedId, out StateNode? sourceNode))
            {
                throw new InvalidOperationException(
                    $"Auto-activate rule '{rule.Id}' references unknown parent '{parentRef.RuleId}'.");
            }

            return CreateConditionalEdge(sourceNode, parentRef.When, parentRef.When);
        }).ToArray();

        return parents.Mode switch
        {
            ParentMode.Any => new DisjunctionNode(displayName, conditionalEdges),
            _ => new ConjunctionNode(displayName, conditionalEdges)
        };
    }


    private static Delegate BuildConstantSelector(Type eventType, Type valueType, object value)
    {
        // Compose Func<EntityValueChangedEvent<TMarker>, TValue> _ => value via expression trees.
        ParameterExpression param = Expression.Parameter(eventType, "_");
        ConstantExpression body = Expression.Constant(value, valueType);
        Type funcType = typeof(Func<,>).MakeGenericType(eventType, valueType);
        LambdaExpression lambda = Expression.Lambda(funcType, body, param);
        return lambda.Compile();
    }

    // Builds one per-player rule's node(s) + edges into the caller-supplied slot-local
    // collections. Used by BuildV2PerPlayerTemplate to materialize the per-player CONTEXT nodes
    // (alive/survived/traded) into the v2 template's localLookup keyed by their rule id — the
    // bridge that lets a v2 when: read of player.survived / .traded resolve. The context rules
    // are Bool/Counter/Value rules with parents/triggers only; the caller must set
    // _currentPlayerTeam before invoking.
    private void BuildPerPlayerRuleNode(
        RuleDef rule, int slot, string playerName, StateGraph graph,
        Dictionary<string, StateNode> parentNodeLookup,
        Dictionary<string, StateNode> localLookup, List<StateNode> nodes,
        GraphWiring wiring)
    {
        if (rule.Parents is not null && (rule.Triggers is null || rule.Triggers.Count == 0))
        {
            StateNode logicNode = BuildAutoActivateRuleLocal(rule, localLookup);
            localLookup[rule.Id] = logicNode;
            nodes.Add(logicNode);

            if (rule.ResetOnRound && logicNode is BoolNode boolLogic)
            {
                wiring.AddEdge(new RoundScopedLogicNodeReset(boolLogic), GraphEdgeKind.RoundReset);
            }

            DescribeLogicNode(logicNode, wiring);
            return;
        }

        StateNode node = CreateNode(rule, playerName);
        localLookup[rule.Id] = node;
        nodes.Add(node);

        if (rule.Triggers is not null)
        {
            (StateNode sourceNode, string? sourceWhen) = ResolveParentSource(rule.Parents, localLookup, graph.Root, rule.Id);
            IConditionalEdge? sourceGate = sourceWhen is null
                ? null
                : CreateConditionalEdge(sourceNode, sourceWhen, sourceWhen);

            for (int triggerIdx = 0; triggerIdx < rule.Triggers.Count; triggerIdx++)
            {
                TriggerDef trigger = rule.Triggers[triggerIdx];
                ExpandedTrigger expansion = ExpandTrigger(rule, trigger, triggerIdx,
                    g => nodes.Add(g),
                    e => wiring.AddEdge(e, GraphEdgeKind.RoundReset),
                    playerName);

                foreach (TriggerDef expanded in expansion.Triggers)
                {
                    StateEdge? edge = CreateEdge(expanded, sourceNode, node, slot, playerName, expansion.SuppressionGuard, sourceGate);
                    if (edge is not null)
                    {
                        AddTriggerEdge(wiring, edge, expanded, sourceGate, expansion.SuppressionGuard);
                    }
                }
            }
        }
    }

    // ── Singleton rule building ────────────────────────────────────────────

    private void BuildSingletonRule(RuleDef rule, StateGraph graph, GraphWiring wiring,
        Dictionary<string, StateNode> nodeLookup, List<StateNode> allNodes, HashSet<Type> relevantTypes)
    {
        if (rule.Parents is not null && (rule.Triggers is null || rule.Triggers.Count == 0))
        {
            StateNode logicNode = BuildAutoActivateRule(rule, nodeLookup);
            nodeLookup[rule.Id] = logicNode;
            if (_enrichmentNodes is not null)
            {
                _enrichmentNodes[rule.Id] = logicNode;
            }

            allNodes.Add(logicNode);

            if (logicNode is ConjunctionNode cj)
            {
                graph.AddConjunction(cj);
            }
            else if (logicNode is DisjunctionNode dj)
            {
                graph.AddDisjunction(dj);
            }

            DescribeLogicNode(logicNode, wiring);
            return;
        }

        StateNode node = CreateNode(rule, null);
        nodeLookup[rule.Id] = node;
        if (_enrichmentNodes is not null)
        {
            _enrichmentNodes[rule.Id] = node;
        }

        allNodes.Add(node);

        if (rule.Triggers is not null)
        {
            (StateNode sourceNode, string? sourceWhen) = ResolveParentSource(rule.Parents, nodeLookup, graph.Root, rule.Id);
            IConditionalEdge? sourceGate = sourceWhen is null
                ? null
                : CreateConditionalEdge(sourceNode, sourceWhen, sourceWhen);

            for (int triggerIdx = 0; triggerIdx < rule.Triggers.Count; triggerIdx++)
            {
                TriggerDef trigger = rule.Triggers[triggerIdx];
                ExpandedTrigger expansion = ExpandTrigger(rule, trigger, triggerIdx,
                    g => allNodes.Add(g),
                    e => wiring.AddEdge(e, GraphEdgeKind.RoundReset));

                foreach (TriggerDef expanded in expansion.Triggers)
                {
                    StateEdge? edge = CreateEdge(expanded, sourceNode, node, null, null, expansion.SuppressionGuard, sourceGate);
                    if (edge is not null)
                    {
                        AddTriggerEdge(wiring, edge, expanded, sourceGate, expansion.SuppressionGuard);
                        relevantTypes.Add(edge.MessageType);
                    }
                }
            }
        }
    }

    /// <summary>
    ///     Adds a context rule's trigger edge through <paramref name="wiring" />: labelled with the
    ///     concrete event, reading its <c>when:</c> gate's sources and its first-wins guard, and drawn
    ///     to the guard it sets as well as to its node.
    /// </summary>
    private static void AddTriggerEdge(GraphWiring wiring, StateEdge edge, TriggerDef expanded,
        IConditionalEdge? sourceGate, BoolNode? guard) =>
        wiring.AddEdge(edge, GraphEdgeKind.Trigger, expanded.On, expanded.Condition,
            GateReads(sourceGate, guard), guard is not null ? [guard] : null, MapAction(expanded.Action));

    /// <summary>
    ///     The nodes an edge reads through its gate and guard rather than its declared reads: every
    ///     source of the <c>while:</c> gate, and the first-wins guard it checks. <c>null</c> for
    ///     neither.
    /// </summary>
    private static IReadOnlyList<StateNode>? GateReads(IConditionalEdge? sourceGate, StateNode? guard)
    {
        if (sourceGate is null)
        {
            return guard is null ? null : [guard];
        }

        return guard is null ? sourceGate.Sources : [.. sourceGate.Sources, guard];
    }

    /// <summary>Draws each input source of a conjunction or disjunction; no-op for any other node.</summary>
    private static void DescribeLogicNode(StateNode logicNode, GraphWiring wiring)
    {
        switch (logicNode)
        {
            case ConjunctionNode cj:
                wiring.DescribeLogicNode(cj, EdgeEffect.Conjunction, cj.Inputs);
                break;
            case DisjunctionNode dj:
                wiring.DescribeLogicNode(dj, EdgeEffect.Disjunction, dj.Inputs);
                break;
        }
    }

    // ── Conditional edge creation ──────────────────────────────────────

    private static IConditionalEdge CreateConditionalEdge(StateNode source,
        string? condition, string? label)
    {
        if (condition is null || condition == "active")
        {
            Type sourceValueType = GetNodeValueType(source);
            Type ceType = typeof(ConditionalEdge<>).MakeGenericType(sourceValueType);

            if (sourceValueType == typeof(bool))
            {
                Delegate pred = ExpressionCompiler.CompileConditionalPredicate("active", typeof(bool));
                return (IConditionalEdge)Activator.CreateInstance(ceType, source, pred, label)!;
            }

            Delegate alwaysTrue = ExpressionCompiler.CompileConditionalPredicate("active", sourceValueType);
            return (IConditionalEdge)Activator.CreateInstance(ceType, source, alwaysTrue, label)!;
        }

        string normalizedCondition = condition.Replace("rule.value", "value", StringComparison.Ordinal);
        Type valType = GetNodeValueType(source);
        Delegate compiled = ExpressionCompiler.CompileConditionalPredicate(normalizedCondition, valType);
        Type condEdgeType = typeof(ConditionalEdge<>).MakeGenericType(valType);
        return (IConditionalEdge)Activator.CreateInstance(condEdgeType, source, compiled, label)!;
    }

    private static StateNode CreateCounterNode(RuleDef rule, string? subtitle)
    {
        int defaultVal = rule.Default is int i ? i : 0;
        if (rule.ResetOnRound)
        {
            return new GenericRoundScopedValueNode<int>(rule.Name ?? rule.Id, defaultVal, subtitle);
        }

        GenericValueNode<int> node = new(rule.Name ?? rule.Id, subtitle);
        if (rule.Default is not null)
        {
            node.SetValue(defaultVal);
        }

        return node;
    }

    // ── Edge creation ──────────────────────────────────────────────────

    private StateEdge? CreateEdge(TriggerDef trigger, StateNode source, StateNode dest,
        int? playerSlot, string? playerName, BoolNode? suppressionGuard = null,
        IConditionalEdge? sourceGate = null)
    {
        // `add` only exists for keyed counters (which build their edges via
        // CreateKeyedCounterEdge, never here). Plain rules accumulate via
        // `set` + "rule.value + …"; a silent set-interpretation would drop that distinction.
        if (trigger.Action == TriggerAction.Add)
        {
            throw new InvalidOperationException(
                $"Rule '{dest.Name}': trigger action 'add' is only valid on keyed_counter rules — "
                + "use action: set with value: \"rule.value + …\" to accumulate on a plain rule.");
        }

        bool isGameEvent = _registry.IsGameEvent(trigger.On);
        bool isNetMessage = _registry.IsNetMessage(trigger.On);

        if (sourceGate is not null && !isGameEvent)
        {
            throw new InvalidOperationException(
                $"Rule with trigger 'on: {trigger.On}': parent 'when:' conditions on triggered rules "
                + "are supported for game-event triggers only (v1). Gate via an auto-activate bool "
                + "parent instead.");
        }

        if (!isGameEvent && !isNetMessage)
        {
            // Third dispatch source: synthesized entity-state change events. Trigger.On
            // matches a registered IEntityValueProvider.ContextName (e.g. "entity.game.freeze_period").
            // Only active when lazy-activation in Build() seeded the provider's value node.
            if (_entityProviders is not null &&
                _entityContextNodes is not null &&
                _entityContextNodes.ContainsKey(trigger.On) &&
                _entityProviders.TryGet(trigger.On, out IEntityValueProvider? provider) &&
                provider is not null)
            {
                return CreateEntityChangeEdge(trigger, provider, source, dest, suppressionGuard);
            }

            // A REGISTERED provider context that wasn't lazily activated is inert by design —
            // the same graceful degradation 'requires:' gives capability gaps.
            if (_entityProviders is not null && _entityProviders.TryGet(trigger.On, out _))
            {
                return null;
            }

            // An entity-context name with no provider registry to resolve it against (builders are
            // legitimately constructed without one; built-in contexts still reference entity.*)
            // degrades to an inert trigger rather than an error.
            if (trigger.On.StartsWith("entity.", StringComparison.Ordinal)
                && (_entityProviders is null || _entityProviders.All.Count == 0))
            {
                return null;
            }

            // Anything else — a name that is not a registered game event, net message, or entity
            // context — must be a build error, not a silently-inert rule (a rule with no edge just
            // reads its default forever). Same loud policy as unguarded $logical resolution
            // failures in ExpandTrigger.
            throw new InvalidOperationException(
                $"Rule '{dest.Name}' has a trigger on unknown event '{trigger.On}' — not a registered "
                + $"game event, net message, or entity context.{SuggestEventName(trigger.On)} "
                + "(Logical event aliases must be written with a '$' prefix, e.g. '$round_end'.)");
        }

        if (isGameEvent)
        {
            EventRegistration reg = _registry.GetEvent(trigger.On)!;
            return CreateGameEventEdge(trigger, reg, source, dest, playerSlot, playerName, suppressionGuard, sourceGate);
        }
        else
        {
            NetMessageRegistration reg = _registry.GetNetMessage(trigger.On)!;
            return CreateNetMessageEdge(trigger, reg, source, dest, playerSlot, playerName, suppressionGuard);
        }
    }

    /// <summary>
    ///     Builds the " Did you mean 'x'?" suffix for unknown-event errors by edit distance over every
    ///     known dispatch name (game events, net messages, registered entity contexts). Empty when
    ///     nothing is plausibly close.
    /// </summary>
    private string SuggestEventName(string unknown)
    {
        IEnumerable<string> candidates = _registry.EventNames.Concat(_registry.NetMessageNames);
        if (_entityProviders is not null)
        {
            candidates = candidates.Concat(_entityProviders.All.Select(p => p.ContextName));
        }

        string? best = null;
        int bestDistance = int.MaxValue;
        foreach (string candidate in candidates)
        {
            int d = LevenshteinDistance(unknown, candidate);
            if (d < bestDistance)
            {
                bestDistance = d;
                best = candidate;
            }
        }

        return best is not null && bestDistance <= Math.Max(2, unknown.Length / 3)
            ? $" Did you mean '{best}'?"
            : "";
    }

    private static int LevenshteinDistance(string a, string b)
    {
        int[] prev = new int[b.Length + 1];
        int[] curr = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++)
        {
            prev[j] = j;
        }

        for (int i = 1; i <= a.Length; i++)
        {
            curr[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = char.ToLowerInvariant(a[i - 1]) == char.ToLowerInvariant(b[j - 1]) ? 0 : 1;
                curr[j] = Math.Min(Math.Min(curr[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }

            (prev, curr) = (curr, prev);
        }

        return prev[b.Length];
    }

    // ── Entity-change edge construction ────────────────────────────────
    //
    // Supported today: Activate/Deactivate on bool destinations, and Set with a
    // literal value expression on Value destinations. Compiled conditions are routed
    // through ExpressionCompiler against the closed-generic EntityValueChangedEvent<TMarker>
    // — fallback identifier resolution (ExpressionCompiler:418-432) handles the
    // entity context reference exactly like any other enrichment node.

    private StateEdge CreateEntityChangeEdge(TriggerDef trigger, IEntityValueProvider provider,
        StateNode source, StateNode dest, BoolNode? suppressionGuard)
    {
        Type eventType = typeof(EntityValueChangedEvent<>).MakeGenericType(provider.MarkerType);

        Delegate? condition = trigger.Condition is not null
            ? ExpressionCompiler.CompileEventCondition(
                trigger.Condition, eventType,
                new Dictionary<string, EventFieldAccessor>(StringComparer.OrdinalIgnoreCase),
                null,
                _enrichmentNodes, _currentPlayerTeam, _playerContextIndex)
            : null;

        if (trigger.Action is TriggerAction.Activate or TriggerAction.Deactivate)
        {
            if (dest is not BoolNode boolDest)
            {
                throw new InvalidOperationException(
                    $"Entity trigger to '{dest.Name}' uses Activate/Deactivate but target is not a BoolNode.");
            }

            EdgeEffect effect = trigger.Action == TriggerAction.Activate
                ? EdgeEffect.Activate
                : EdgeEffect.Deactivate;

            Type edgeType = typeof(OnEntityChange<>).MakeGenericType(provider.MarkerType);
            return (StateEdge)Activator.CreateInstance(
                edgeType, source, boolDest, effect, condition, suppressionGuard)!;
        }

        if (trigger.Action != TriggerAction.Set)
        {
            throw new InvalidOperationException(
                $"Entity trigger to '{dest.Name}' uses {trigger.Action} but only Activate/Deactivate/Set are supported.");
        }

        Type valueType = GetNodeValueType(dest);
        if (trigger.Value is null)
        {
            throw new InvalidOperationException(
                $"Entity trigger to '{dest.Name}' uses Set but has no value expression.");
        }

        // Literal values only (no `event.X` / `node.value` references
        // inside entity-event setters). For "FreezeTime" this is a string literal; we
        // parse it directly and build a constant-returning selector lambda.
        object literalValue = ParseLiteralValue(trigger.Value, valueType);
        Delegate selector = BuildConstantSelector(eventType, valueType, literalValue);

        Type setValueEdgeType = typeof(OnEntityChangeSetValue<,>).MakeGenericType(provider.MarkerType, valueType);
        return (StateEdge)Activator.CreateInstance(
            setValueEdgeType, source, dest, selector, condition, suppressionGuard)!;
    }

    private static StateNode CreateEntityValueNode(IEntityValueProvider provider)
    {
        // Bool fields get GenericBoolNode (Activate/Deactivate semantics). Other value types
        // get GenericValueNode<T>. The scanner writes via reflection on either SetValue or
        // Activate/Deactivate so the runtime type stays loose here.
        if (provider.ValueType == typeof(bool))
        {
            return new GenericBoolNode(provider.ContextName);
        }

        Type nodeType = typeof(GenericValueNode<>).MakeGenericType(provider.ValueType);
        return (StateNode)Activator.CreateInstance(nodeType, provider.ContextName, /*subtitle*/ null)!;
    }

    private StateEdge CreateGameEventEdge(TriggerDef trigger, EventRegistration reg,
        StateNode source, StateNode dest, int? playerSlot, string? playerName,
        BoolNode? suppressionGuard, IConditionalEdge? sourceGate = null,
        IReadOnlyList<StateNode>? declaredReads = null)
    {
        Type eventType = reg.EventType;

        Delegate? condition = trigger.Condition is not null
            ? ExpressionCompiler.CompileEventCondition(trigger.Condition, eventType, reg.Fields, playerSlot, ConditionNodes, _currentPlayerTeam, _playerContextIndex, _entityScanner, _perPlayerEntityProviders, parameterType: typeof(GameEvent))
            : null;

        if (trigger.Action is TriggerAction.Activate or TriggerAction.Deactivate)
        {
            if (dest is not BoolNode boolDest)
            {
                throw new InvalidOperationException(
                    $"Trigger to '{dest.Name}' uses Activate/Deactivate but target is not a BoolNode.");
            }

            EdgeEffect effect = trigger.Action == TriggerAction.Activate
                ? EdgeEffect.Activate
                : EdgeEffect.Deactivate;

            Type edgeType = typeof(OnGameEvent<>).MakeGenericType(eventType);
            return (StateEdge)Activator.CreateInstance(edgeType, source, boolDest, effect, condition, suppressionGuard, sourceGate, declaredReads)!;
        }

        Type valueType = GetNodeValueType(dest);

        Delegate selector;
        if (trigger.Action == TriggerAction.Increment)
        {
            selector = ExpressionCompiler.CompileEventValueSelector(
                "node.value + 1", eventType, valueType, reg.Fields, playerSlot, dest, ConditionNodes,
                _entityScanner, _perPlayerEntityProviders, parameterType: typeof(GameEvent));
        }
        else
        {
            string valueExpr = trigger.Value?.Replace("rule.value", "node.value", StringComparison.Ordinal)
                               ?? throw new InvalidOperationException(
                                   $"Trigger to '{dest.Name}' uses Set but has no value expression.");
            selector = ExpressionCompiler.CompileEventValueSelector(
                valueExpr, eventType, valueType, reg.Fields, playerSlot, dest, ConditionNodes,
                _entityScanner, _perPlayerEntityProviders, parameterType: typeof(GameEvent));
        }

        Type setValueEdgeType = typeof(OnGameEventSetValue<,>).MakeGenericType(eventType, valueType);
        return (StateEdge)Activator.CreateInstance(setValueEdgeType, source, dest, selector, condition, suppressionGuard, sourceGate, declaredReads)!;
    }

    private StateEdge CreateNetMessageEdge(TriggerDef trigger, NetMessageRegistration reg,
        StateNode source, StateNode dest, int? playerSlot, string? playerName,
        BoolNode? suppressionGuard)
    {
        Type payloadType = reg.PayloadType;

        // Net-message conditions were once silently ignored — never compiled, and a
        // literal null sat in OnNetMessage<T>'s condition slot (the graph UI still displayed the
        // condition text via GraphEdgeDescriptor.ConditionLabel, so shown semantics weren't real).
        Delegate? condition = trigger.Condition is not null
            ? ExpressionCompiler.CompileEventCondition(trigger.Condition, payloadType, reg.Fields, playerSlot, ConditionNodes, _currentPlayerTeam, _playerContextIndex, _entityScanner, _perPlayerEntityProviders)
            : null;

        if (trigger.Action is TriggerAction.Activate or TriggerAction.Deactivate)
        {
            if (dest is not BoolNode boolDest)
            {
                throw new InvalidOperationException(
                    $"Trigger to '{dest.Name}' uses Activate/Deactivate but target is not a BoolNode.");
            }

            EdgeEffect effect = trigger.Action == TriggerAction.Activate
                ? EdgeEffect.Activate
                : EdgeEffect.Deactivate;

            Type edgeType = typeof(OnNetMessage<>).MakeGenericType(payloadType);
            return (StateEdge)Activator.CreateInstance(edgeType, source, boolDest, effect, condition, suppressionGuard)!;
        }

        Type valueType = GetNodeValueType(dest);

        string valueExpr = trigger.Value?.Replace("rule.value", "node.value", StringComparison.Ordinal)
                           ?? throw new InvalidOperationException(
                               $"Trigger to '{dest.Name}' uses Set but has no value expression.");

        Delegate selector = ExpressionCompiler.CompileEventValueSelector(
            valueExpr, payloadType, valueType, reg.Fields, playerSlot, dest, ConditionNodes,
            _entityScanner, _perPlayerEntityProviders);

        Type setValueEdgeType = typeof(OnNetMessageSetValue<,>).MakeGenericType(payloadType, valueType);
        return (StateEdge)Activator.CreateInstance(setValueEdgeType, source, dest, selector, condition, suppressionGuard)!;
    }

    // ── Node creation ──────────────────────────────────────────────────

    private static StateNode CreateNode(RuleDef rule, string? subtitle)
    {
        return rule.Type switch
        {
            RuleType.Bool when rule.ResetOnRound =>
                new GenericRoundScopedBoolNode(rule.Name ?? rule.Id,
                    rule.Default is bool and true, subtitle),

            RuleType.Bool =>
                new GenericBoolNode(rule.Name ?? rule.Id, subtitle),

            RuleType.Value => CreateValueNode(rule, subtitle),
            RuleType.Counter => CreateCounterNode(rule, subtitle),
            _ => new GenericBoolNode(rule.Name ?? rule.Id, subtitle)
        };
    }

    private static StateNode CreateStringValueNode(RuleDef rule, string? subtitle)
    {
        string defaultVal = rule.Default as string ?? "";
        if (rule.ResetOnRound)
        {
            return new GenericRoundScopedValueNode<string>(rule.Name ?? rule.Id, defaultVal, subtitle);
        }

        GenericValueNode<string> node = new(rule.Name ?? rule.Id, subtitle);
        if (rule.Default is string)
        {
            node.SetValue(defaultVal);
        }

        return node;
    }

    private static StateNode CreateValueNode(RuleDef rule, string? subtitle)
    {
        return rule.ValueType?.ToLowerInvariant() switch
        {
            "string" => CreateStringValueNode(rule, subtitle),
            "int" => rule.ResetOnRound
                ? new GenericRoundScopedValueNode<int>(rule.Name ?? rule.Id,
                    rule.Default is int i ? i : 0, subtitle)
                : new GenericValueNode<int>(rule.Name ?? rule.Id, subtitle),
            "float" or "double" => new GenericValueNode<double>(rule.Name ?? rule.Id, subtitle),
            "bool" => new GenericValueNode<bool>(rule.Name ?? rule.Id, subtitle),
            _ => CreateStringValueNode(rule, subtitle)
        };
    }

    /// <summary>
    ///     Expands <c>$logical</c> trigger references to their concrete
    ///     bindings under the active profile. Concrete-named triggers pass
    ///     through unchanged. Multi-event bindings yield one
    ///     <see cref="TriggerDef" /> per concrete name; the caller registers
    ///     a separate edge per result. For non-idempotent actions on
    ///     multi-event FirstWins bindings, returns a shared per-round guard
    ///     so the first concrete fire suppresses subsequent ones.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    ///     The trigger references a <c>$logical</c> name that resolves to
    ///     <c>null</c> on the active profile and the rule did not declare
    ///     <c>requires:</c> for it. Strict by design — a silent no-op on
    ///     HLTV is exactly the failure mode this class exists to prevent.
    /// </exception>
    private ExpandedTrigger ExpandTrigger(RuleDef rule, TriggerDef trigger, int triggerIndex,
        Action<StateNode>? registerGuardNode = null,
        Action<StateEdge>? registerGuardResetEdge = null,
        string? subtitle = null)
    {
        if (!LogicalEventResolver.IsLogicalReference(trigger.On))
        {
            return new ExpandedTrigger([trigger], null);
        }

        string logicalName = trigger.On[1..];
        LogicalEventBinding? binding = _logicalResolver.Resolve(logicalName);

        if (binding is null)
        {
            throw new InvalidOperationException(
                $"Rule '{rule.Id}' has trigger on '{trigger.On}', but profile " +
                $"'{_logicalResolver.Profile.DisplayName}' does not bind logical event " +
                $"'{logicalName}'. Add '{logicalName}' to the rule's 'requires:' list to " +
                $"silently skip the rule on profiles that lack it.");
        }

        List<TriggerDef> expanded = new(binding.ConcreteEventNames.Count);
        foreach (string concrete in binding.ConcreteEventNames)
        {
            expanded.Add(trigger with
            {
                On = concrete
            });
        }

        // First-wins-per-round suppression: required when binding has
        // multiple concrete events with FirstWins semantics AND the action
        // is non-idempotent. Activate/Deactivate are idempotent and need
        // no guard. The guard is a per-round bool that flips on first
        // concrete fire and auto-resets at round boundaries.
        bool needsGuard = binding.ConcreteEventNames.Count > 1
                          && binding.Semantics == LogicalEventSemantics.FirstWins
                          && trigger.Action is TriggerAction.Increment or TriggerAction.Set or TriggerAction.Add;

        if (!needsGuard)
        {
            return new ExpandedTrigger(expanded, null);
        }

        GenericRoundScopedBoolNode guard = new(
            $"__seen_{rule.Id}_{triggerIndex}",
            false,
            subtitle);
        registerGuardNode?.Invoke(guard);
        registerGuardResetEdge?.Invoke(new RoundScopedLogicNodeReset(guard));
        return new ExpandedTrigger(expanded, guard);
    }

    private static Type GetNodeValueType(StateNode node)
    {
        Type? type = node.GetType();
        while (type is not null)
        {
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(ValueNode<>))
            {
                return type.GetGenericArguments()[0];
            }

            type = type.BaseType;
        }

        if (node is BoolNode)
        {
            return typeof(bool);
        }

        return typeof(object);
    }

    // ── Lazy-activation helpers ─────────────────────────────────────────

    /// <summary>
    ///     v2 counterpart to <see cref="IsReferencedByBuiltins" />: does any checked v2 stat/highlight
    ///     declare a read of <paramref name="path" />? A v2 <see cref="CheckedStat.DeclaredReads" /> is
    ///     an exact resolved path (e.g. <c>enrich.hurt.capped_damage</c>), so this uses ordinal
    ///     equality rather than the built-in chains' substring scan. Feeds the enrichment-provider
    ///     gate: an enrichment is the one kind of read that <c>UnionV2EntityReads</c> cannot see
    ///     through (it carries no catalog <c>v2Name</c>, so it resolves to no provider), so this is
    ///     what activates the health/weapon provider the enrichment edge computes it from.
    /// </summary>
    private static bool IsReferencedByV2Reads(string path, IReadOnlyList<CheckedRuleset> rulesets)
    {
        foreach (CheckedRuleset ruleset in rulesets)
        {
            foreach (CheckedStat stat in ruleset.Stats)
            {
                if (stat.DeclaredReads.Contains(path, StringComparer.Ordinal))
                {
                    return true;
                }
            }

            foreach (CheckedHighlight highlight in ruleset.Highlights)
            {
                if (highlight.DeclaredReads.Contains(path, StringComparer.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    ///     Did every name in <paramref name="required" /> survive the reference gate into
    ///     <paramref name="gated" />? Asked before constructing anything that resolves digest columns
    ///     by name, because those constructors throw on a missing one rather than reading nulls, and
    ///     a build with no per-player provider registry at all can reach that point with the gate set
    ///     and every column absent.
    /// </summary>
    private static bool HasAllProviders(
        IReadOnlyList<IPerPlayerEntityValueProvider> gated, IReadOnlyList<string> required)
    {
        foreach (string name in required)
        {
            bool found = false;
            foreach (IPerPlayerEntityValueProvider provider in gated)
            {
                if (string.Equals(provider.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    found = true;
                    break;
                }
            }

            if (!found)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    ///     Does any checked v2 stat trigger on <paramref name="eventName" />? A view-based stat's
    ///     trigger expands to concrete wire events in <see cref="CheckedStat.ConcreteEvents" />, so
    ///     this scans that list. Feeds the synthesized-event scanner gate (<c>molotov_thrown</c>):
    ///     the pure-v2 build path has an empty v1 config, so a v2 <c>count: molotov</c> stat would
    ///     otherwise leave molotov synthesis off. Highlights carry no ConcreteEvents (their events
    ///     flow through the flags they reference), so only stats are scanned.
    /// </summary>
    private static bool RulesetsSubscribeToEvent(string eventName, IReadOnlyList<CheckedRuleset> rulesets)
    {
        foreach (CheckedRuleset ruleset in rulesets)
        {
            foreach (CheckedStat stat in ruleset.Stats)
            {
                if (stat.ConcreteEvents.Contains(eventName, StringComparer.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    ///     Records one coverage diagnostic per v2 node that depends on the synthesized
    ///     <c>enemy_spotted</c> contacts on a run that does not produce them — a node reading one of
    ///     <see cref="BuiltinContexts.SpotDerivedEnrichments" />, or a stat triggering on the event
    ///     itself. Left alone the node is not an error and not a miss: the facets read their
    ///     no-measurement sentinel and the subscriber never fires, so the stat reports a number that
    ///     looks exactly like a player who never spotted anyone.
    ///     <para>
    ///         This is a <b>graph-build</b> check, not a resolve-time one, because resolution cannot
    ///         see the answer: <c>ResolveContext</c> carries the tick rate, the source profile, and
    ///         the install's params, and the geometry arrives separately through
    ///         <c>AnalysisOptions.VisibilityEngine</c> at the builder's constructor. The other half of
    ///         the condition is directory-wide (any ruleset subscribing to the event turns the scan on
    ///         for all of them), which a per-document resolve pass would have to guess at — and a
    ///         guess here costs a false positive on a ruleset that is correct.
    ///     </para>
    ///     <para>
    ///         It rides <see cref="RulesetCoverageDiagnostic" /> for the same reason a view that does
    ///         not bind on the active profile does: the node is legitimately unproducible on THIS run
    ///         rather than wrong, so the build stays clean and the consumer surfaces a row. The
    ///         builder's own constructor doc calls a missing engine "the same shape as a profile that
    ///         does not bind an event"; this is that shape, reported.
    ///     </para>
    /// </summary>
    /// <param name="rulesets">The checked rulesets being built.</param>
    /// <param name="coverage">The build's coverage list, appended to in place.</param>
    /// <param name="spottedSubscribed">Whether any rule subscribes to <c>enemy_spotted</c>.</param>
    /// <param name="contactsProduced">Whether the contact scan was actually built and wired into the entity scanner.</param>
    private void ReportUnproducibleSpotReads(
        IReadOnlyList<CheckedRuleset> rulesets,
        List<RulesetCoverageDiagnostic> coverage,
        bool spottedSubscribed,
        bool contactsProduced)
    {
        if (contactsProduced || rulesets.Count == 0)
        {
            return;
        }

        // Which half is missing decides what the author has to do about it, so the message says
        // which rather than naming both and leaving them to find out.
        string cause = _visibilityEngine is null
            ? "this run was given no baked map geometry (AnalysisOptions.VisibilityEngine), and "
              + "visibility is recomputed from geometry rather than read off the wire"
            : spottedSubscribed
                ? "the contact scan could not be built on this run (no demo frames, or the per-player "
                  + "vantage columns it samples are unavailable)"
                : "no stat subscribes to the 'enemy_spotted' view, and the contact scan runs only for "
                  + "a rule that does";

        string profileId = Profile.GetType().Name;
        foreach (CheckedRuleset ruleset in rulesets)
        {
            foreach (CheckedStat stat in ruleset.Stats)
            {
                if (FirstSpotDerivedRead(stat.DeclaredReads) is { } facet)
                {
                    coverage.Add(new RulesetCoverageDiagnostic(ruleset.Id, stat.StatId,
                        stat.ResolvedView ?? "", profileId,
                        $"stat '{stat.StatId}' reads {facet}, which is measured from the synthesized "
                        + $"'enemy_spotted' contacts — {cause}, so it reads its no-measurement value on "
                        + "every event rather than a number",
                        stat.Position));
                    continue;
                }

                if (stat.ConcreteEvents.Contains("enemy_spotted", StringComparer.Ordinal))
                {
                    coverage.Add(new RulesetCoverageDiagnostic(ruleset.Id, stat.StatId,
                        stat.ResolvedView ?? "enemy_spotted", profileId,
                        $"stat '{stat.StatId}' triggers on the synthesized 'enemy_spotted' view — "
                        + $"{cause}, so the event is never produced and the stat never fires",
                        stat.Position));
                }
            }

            foreach (CheckedHighlight highlight in ruleset.Highlights)
            {
                if (FirstSpotDerivedRead(highlight.DeclaredReads) is { } facet)
                {
                    coverage.Add(new RulesetCoverageDiagnostic(ruleset.Id, highlight.HighlightId,
                        "", profileId,
                        $"highlight '{highlight.HighlightId}' reads {facet}, which is measured from the "
                        + $"synthesized 'enemy_spotted' contacts — {cause}, so it reads its "
                        + "no-measurement value on every event rather than a number",
                        highlight.Position));
                }
            }
        }
    }

    /// <summary>
    ///     The first spot-derived enrichment in <paramref name="declaredReads" />, or null when the
    ///     node reads none. One name is enough to report against — the prerequisite is the same for
    ///     all seven, so listing the rest would repeat one fact per facet.
    /// </summary>
    private static string? FirstSpotDerivedRead(IReadOnlyList<string> declaredReads)
    {
        foreach (string read in declaredReads)
        {
            foreach (string spotDerived in BuiltinContexts.SpotDerivedEnrichments)
            {
                if (string.Equals(read, spotDerived, StringComparison.Ordinal))
                {
                    return spotDerived;
                }
            }
        }

        return null;
    }

    private static bool IsReferencedByBuiltins(
        string contextName,
        IReadOnlyList<RuleChainDef> builtinContexts)
    {
        foreach (RuleChainDef ctx in builtinContexts)
        {
            foreach (RuleDef rule in ctx.Rules)
            {
                if (RuleReferencesContext(rule, contextName))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static EdgeEffect MapAction(TriggerAction action) => action switch
    {
        TriggerAction.Activate => EdgeEffect.Activate,
        TriggerAction.Deactivate => EdgeEffect.Deactivate,
        TriggerAction.Increment => EdgeEffect.SetValue,
        TriggerAction.Set => EdgeEffect.SetValue,
        TriggerAction.Add => EdgeEffect.SetValue,
        _ => EdgeEffect.SetValue
    };

    private static object ParseLiteralValue(string expr, Type valueType)
    {
        string trimmed = expr.Trim();
        if (valueType == typeof(string))
        {
            // Source form: "\"FreezeTime\"" — strip the wrapping quotes.
            if (trimmed is ['"', _, ..] && trimmed[^1] == '"')
            {
                return trimmed[1..^1];
            }

            return trimmed;
        }

        if (valueType == typeof(bool))
        {
            return bool.Parse(trimmed);
        }

        if (valueType == typeof(int))
        {
            return int.Parse(trimmed, CultureInfo.InvariantCulture);
        }

        if (valueType == typeof(float))
        {
            return float.Parse(trimmed, CultureInfo.InvariantCulture);
        }

        if (valueType == typeof(double))
        {
            return double.Parse(trimmed, CultureInfo.InvariantCulture);
        }

        throw new InvalidOperationException(
            $"Entity triggers support literal string/bool/int/float/double; got {valueType.Name}.");
    }

    // ── Logical-event expansion ────────────────────────────────────────

    /// <summary>
    ///     Returns false if the rule declares <c>requires:</c> entries that
    ///     the active profile does not bind. Such rules are silently
    ///     skipped — graceful degradation.
    /// </summary>
    private bool RequiresSatisfied(RuleDef rule)
    {
        if (rule.Requires is null || rule.Requires.Count == 0)
        {
            return true;
        }

        foreach (string req in rule.Requires)
        {
            LogicalEventBinding? binding = _logicalResolver.Resolve(req);
            if (binding is null)
            {
                return false;
            }
        }

        return true;
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    private static (StateNode Source, string? When) ResolveParentSource(ParentsDef? parents,
        IReadOnlyDictionary<string, StateNode> lookup, StateNode root, string ruleId)
    {
        if (parents is null || parents.Rules.Count == 0)
        {
            return (root, null);
        }

        if (parents.Rules is [{ When: null }])
        {
            string id = ResolveContextId(parents.Rules[0].RuleId);
            return (lookup.GetValueOrDefault(id) ?? root, null);
        }

        // Single parent WITH a when: condition — the condition gates the triggers at fire time
        // (evaluated against the parent's current value; the topological sort orders the parent's
        // writers first, so same-message count-gated captures work). Discarding it silently — the
        // old behavior — made count-gated rules fire on EVERY event.
        if (parents.Rules.Count == 1)
        {
            string id = ResolveContextId(parents.Rules[0].RuleId);
            return (lookup.GetValueOrDefault(id) ?? root, parents.Rules[0].When);
        }

        // Multiple parents on a TRIGGERED rule are not implemented (conjunction gating only exists
        // for auto-activate rules). The old behavior silently used the first parent and ignored the
        // rest — wrong results with no signal. Author the gate explicitly instead: an auto-activate
        // bool over the parents, then that bool as this rule's single parent.
        throw new InvalidOperationException(
            $"Rule '{ruleId}': triggered rules support a single parent; "
            + $"got {parents.Rules.Count} ({string.Join(", ", parents.Rules.Select(p => $"'{p.RuleId}'"))}). "
            + "Declare an auto-activate bool rule (parents, no triggers) combining them, and parent this rule on it.");
    }

    private static bool RuleReferencesContext(RuleDef rule, string contextName)
    {
        if (rule.Triggers is not null)
        {
            foreach (TriggerDef t in rule.Triggers)
            {
                // 'on:' is a single dispatch name — exact match, never a substring.
                if (string.Equals(t.On, contextName, StringComparison.Ordinal))
                {
                    return true;
                }

                if (ContainsContextToken(t.Condition, contextName))
                {
                    return true;
                }

                if (ContainsContextToken(t.Value, contextName))
                {
                    return true;
                }
            }
        }

        if (rule.Parents?.Rules is not null)
        {
            foreach (ParentRef p in rule.Parents.Rules)
            {
                if (ContainsContextToken(p.When, contextName))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    ///     Token-boundary occurrence check for lazy provider activation. Plain <c>Contains</c> both
    ///     over-activated (a name embedded in a longer identifier, e.g. context
    ///     <c>entity.pawn.health</c> matching an expression using <c>entity.pawn.health_max</c>) and
    ///     was prefix-collision ambiguous between providers. A match here must sit on identifier
    ///     boundaries; a preceding/following <c>.</c> stays legal (the <c>player.entity.*</c>
    ///     composition and sub-path reads).
    /// </summary>
    private static bool ContainsContextToken(string? text, string contextName)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        int idx = 0;
        while ((idx = text.IndexOf(contextName, idx, StringComparison.Ordinal)) >= 0)
        {
            bool startOk = idx == 0 || !IsIdentifierChar(text[idx - 1]);
            int end = idx + contextName.Length;
            bool endOk = end >= text.Length || !IsIdentifierChar(text[end]);
            if (startOk && endOk)
            {
                return true;
            }

            idx++;
        }

        return false;
    }

    private static bool IsIdentifierChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    /// <summary>
    ///     Expansion result: the concrete triggers and an optional shared
    ///     first-wins-per-round guard. The guard is allocated only when the
    ///     binding is multi-event FirstWins and the action is non-idempotent
    ///     (Increment/Set). Activate/Deactivate are idempotent — no guard
    ///     needed.
    /// </summary>
    private readonly record struct ExpandedTrigger(List<TriggerDef> Triggers, BoolNode? SuppressionGuard);
}
