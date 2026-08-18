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
    private readonly ParsedDemo? _demo;
    private readonly EntityValueProviderRegistry? _entityProviders;
    private readonly LogicalEventResolver _logicalResolver;
    private readonly PerPlayerEntityValueProviderRegistry? _perPlayerEntityProviders;
    private readonly EventRegistry _registry;

    // The per-player equipment provider, set (and snapshotted by the scanner) only when a v2 ruleset
    // reads round.team.equipment / round.enemies.equipment — the B6 freeze-end economy maintenance
    // edge sums it. Null otherwise, so no economy nodes/edges are built (and GetPreFrameValue, which
    // throws on an un-snapshotted provider, is never reached).
    private IPerPlayerEntityValueProvider? _b6EquipmentProvider;

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
    /// <param name="demo">Optional parsed demo — supplies the source profile and player roster.</param>
    /// <param name="profile">Explicit source profile override; falls back to the demo's profile or the default.</param>
    /// <param name="entityProviders">Optional singleton-entity providers (game-rules etc.).</param>
    /// <param name="perPlayerEntityProviders">Optional per-player entity providers (pawn health, active weapon, etc.).</param>
    public RuleChainBuilder(
        EventRegistry registry,
        ParsedDemo? demo = null,
        DemoSourceProfile? profile = null,
        EntityValueProviderRegistry? entityProviders = null,
        PerPlayerEntityValueProviderRegistry? perPlayerEntityProviders = null)
    {
        _registry = registry;
        _entityProviders = entityProviders;
        _perPlayerEntityProviders = perPlayerEntityProviders;
        _demo = demo;

        DemoSourceProfile resolved = profile
                                     ?? (demo?.Profile is not null
                                         ? DemoSourceProfileRegistry.Resolve(demo.Profile, ObservedEvents(demo))
                                         : DemoSourceProfileRegistry.DefaultFallback);

        _logicalResolver = new LogicalEventResolver(resolved);
    }

    /// <summary>
    ///     The distinct game-event names this demo actually fires. The profile says what the SOURCE
    ///     might emit; this says what THIS recording did — the only way to tell a Valve GOTV demo
    ///     from a tournament-server one, which are identical in the header.
    /// </summary>
    private static HashSet<string> ObservedEvents(ParsedDemo demo)
    {
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
        foreach (GameEvent evt in demo.AllGameEvents)
        {
            names.Add(evt.Name);
        }

        return names;
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
        Dictionary<string, StateNode> nodeLookup = new(StringComparer.OrdinalIgnoreCase)
        {
            ["root"] = graph.Root
        };

        List<StateNode> allNodes = new()
        {
            graph.Root
        };
        List<GraphEdgeDescriptor> edgeDescriptors = new();
        // descriptor → backing StateEdge, by reference identity. Populated only where a real
        // trigger edge is created alongside its descriptor (BuildSingletonRule); drives edge
        // graph-breakpoints. Logic/per-player descriptors stay absent.
        Dictionary<GraphEdgeDescriptor, StateEdge> edgeBacking = new(ReferenceEqualityComparer.Instance);
        Dictionary<string, List<StateNode>> groupMembers = new(StringComparer.OrdinalIgnoreCase);
        List<ConjunctionNode> conjunctions = new();
        HashSet<Type> relevantTypes = new();

        // ── Build player-context index (consumed by CreateEnrichment below) ──
        PlayerContextIndex playerContextIndex = new();
        PopulateInitialTeams(playerContextIndex);
        _playerContextIndex = playerContextIndex;

        List<RuleChainDef> builtinContexts = BuiltinContexts.GenerateContextRules();

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

        // Per-player providers are reference-gated like singletons (catalog width
        // multiplies per-frame capture work, so only referenced providers activate). A YAML
        // reference always CONTAINS the provider name (`player.entity.pawn.health` ⊃
        // `entity.pawn.health`; same for subject reads), so the substring scan covers the
        // config side. The two providers consumed by C# enrichment (HurtTeamEnrichmentEdge)
        // gate on their enrichment OUTPUT names instead — the C# read exists to feed those
        // transient nodes, so a config that reads neither needs neither provider.
        List<IPerPlayerEntityValueProvider> perPlayerList = [];
        bool healthNeeded = false, weaponNeeded = false, b6EquipmentNeeded = false;
        if (_perPlayerEntityProviders is { All.Count: > 0 })
        {
            // B6 relative economy: a v2 read of round.team.equipment / round.enemies.equipment needs the
            // per-player equipment provider snapshotted so the freeze-end maintenance edge can sum it.
            b6EquipmentNeeded = IsReferencedByV2Reads("round.team.equipment", rulesets)
                                || IsReferencedByV2Reads("round.enemies.equipment", rulesets);
            // The enrichment-provider gate spans BOTH rule surfaces: a builtin-context read
            // (substring scan) AND a v2 ruleset read (exact DeclaredReads path). A summing bucket like
            // weapon-stats' damage_by_weapon reads enrich.hurt.capped_damage only from the v2 side, so
            // without the v2 union its health provider would gate out and capped-damage would fall back
            // to the event-cache HP — diverging from the entity-snapshot HP (silent mismatch).
            healthNeeded = IsReferencedByBuiltins("entity.pawn.health", builtinContexts)
                           || IsReferencedByBuiltins("enrich.hurt.victim_health_before", builtinContexts)
                           || IsReferencedByBuiltins("enrich.hurt.capped_damage", builtinContexts)
                           || IsReferencedByV2Reads("entity.pawn.health", rulesets)
                           || IsReferencedByV2Reads("enrich.hurt.victim_health_before", rulesets)
                           || IsReferencedByV2Reads("enrich.hurt.capped_damage", rulesets);
            weaponNeeded = IsReferencedByBuiltins("entity.pawn.active_weapon_class", builtinContexts)
                           || IsReferencedByBuiltins("enrich.hurt.attacker_active_weapon", builtinContexts)
                           || IsReferencedByV2Reads("entity.pawn.active_weapon_class", rulesets)
                           || IsReferencedByV2Reads("enrich.hurt.attacker_active_weapon", rulesets);

            foreach (IPerPlayerEntityValueProvider provider in _perPlayerEntityProviders.All)
            {
                bool referenced = provider.Name switch
                {
                    "entity.pawn.health" => healthNeeded,
                    "entity.pawn.active_weapon_class" => weaponNeeded,
                    "entity.pawn.equipment_value" => b6EquipmentNeeded
                                                     || IsReferencedByBuiltins(provider.Name, builtinContexts),
                    _ => IsReferencedByBuiltins(provider.Name, builtinContexts)
                };
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

        if ((matched.Count > 0 || perPlayerList.Count > 0 || emitMolotov) && _demo is not null)
        {
            _entityContextNodes = new Dictionary<string, StateNode>(StringComparer.OrdinalIgnoreCase);
            List<(IEntityValueProvider, StateNode)> trackedForScanner = new(matched.Count);
            foreach (IEntityValueProvider provider in matched)
            {
                StateNode valueNode = CreateEntityValueNode(provider);
                _entityContextNodes[provider.ContextName] = valueNode;
                nodeLookup[provider.ContextName] = valueNode;
                _enrichmentNodes[provider.ContextName] = valueNode;
                allNodes.Add(valueNode);
                trackedForScanner.Add((provider, valueNode));
            }

            entityScanner = new EntityChangeScanner(
                new EntityStateLayer(_demo.Frames),
                trackedForScanner,
                perPlayerList,
                emitMolotov);
        }

        // Expose the scanner to per-player compile sites so `player.entity.*` references resolve
        // (see CreateGameEventEdge). Stays null when no entity providers are registered, in which
        // case any such reference is a clean compile-time error.
        _entityScanner = entityScanner;

        // The equipment provider was gated into perPlayerList (hence snapshotted) exactly when a v2
        // round.*.equipment read requires it; capture it for the B6 freeze-end economy edge. Null (no
        // economy edge) when unreferenced or when the scanner wasn't built.
        _b6EquipmentProvider = b6EquipmentNeeded && entityScanner is not null
            ? _perPlayerEntityProviders?.Get("entity.pawn.equipment_value")
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
        BuiltinContexts.EnrichmentInfrastructure enrichment = BuiltinContexts.CreateEnrichment(
            graph.Root, playerContextIndex, _registry, _logicalResolver,
            entityScanner, pawnHealthProvider, activeWeaponProvider);
        foreach ((string key, StateNode node) in enrichment.NodeLookup)
        {
            nodeLookup[key] = node;
            _enrichmentNodes[key] = node;
        }

        allNodes.AddRange(enrichment.Nodes);
        foreach (StateEdge edge in enrichment.Edges)
        {
            graph.AddEdge(edge);
        }

        List<RuleChainDef> gameContexts = builtinContexts.Where(c => c.Scope == ChainScope.Game).ToList();
        List<RuleChainDef> perPlayerContexts = builtinContexts.Where(c => c.Scope == ChainScope.PerPlayer).ToList();

        // ── Build game-scoped context rules ────────────────────────────────
        foreach (RuleChainDef ctx in gameContexts)
        {
            foreach (RuleDef rule in ctx.Rules)
            {
                if (!RequiresSatisfied(rule))
                {
                    continue;
                }

                BuildSingletonRule(rule, graph, nodeLookup, allNodes, edgeDescriptors, edgeBacking, groupMembers, relevantTypes);
                if (nodeLookup.TryGetValue(rule.Id, out StateNode? ctxNode))
                {
                    // Context rules (round_number, gameplay_phase, …) resolve by bare id only.
                    gameNodesByRuleId[rule.Id] = ctxNode;
                }
            }
        }

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
            BuildRulesetsV2(rulesets, options, graph, nodeLookup, allNodes, edgeDescriptors,
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

        List<NodeGroupHint> groupHints = groupMembers
            .Select(kv => new NodeGroupHint(kv.Key, kv.Value))
            .ToList();

        return new BuildResult(graph, allNodes, edgeDescriptors, conjunctions,
            relevantTypes, groupHints, playerContextIndex, entityScanner, null,
            edgeBacking.Count > 0 ? edgeBacking : null,
            gameNodesByRuleId.Count > 0 ? gameNodesByRuleId : null,
            v2Outputs.Count > 0 ? v2Outputs : null,
            v2Coverage.Count > 0 ? v2Coverage : null);
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
        List<StateEdge> edges, List<GraphEdgeDescriptor> descriptors)
    {
        if (rule.Parents is not null && (rule.Triggers is null || rule.Triggers.Count == 0))
        {
            StateNode logicNode = BuildAutoActivateRuleLocal(rule, localLookup);
            localLookup[rule.Id] = logicNode;
            nodes.Add(logicNode);

            if (rule.ResetOnRound && logicNode is BoolNode boolLogic)
            {
                edges.Add(new RoundScopedLogicNodeReset(boolLogic));
            }

            if (logicNode is ConjunctionNode cj)
            {
                foreach (IConditionalEdge input in cj.Inputs)
                {
                    descriptors.Add(new GraphEdgeDescriptor(
                        input.Source, cj, "", EdgeEffect.Conjunction, input.ConditionLabel));
                }
            }
            else if (logicNode is DisjunctionNode dj)
            {
                foreach (IConditionalEdge input in dj.Inputs)
                {
                    descriptors.Add(new GraphEdgeDescriptor(
                        input.Source, dj, "", EdgeEffect.Disjunction, input.ConditionLabel));
                }
            }

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
                    edges.Add,
                    playerName);

                foreach (TriggerDef expanded in expansion.Triggers)
                {
                    StateEdge? edge = CreateEdge(expanded, sourceNode, node, slot, playerName, expansion.SuppressionGuard, sourceGate);
                    if (edge is not null)
                    {
                        edges.Add(edge);
                        descriptors.Add(new GraphEdgeDescriptor(
                            sourceNode, node, expanded.On,
                            MapAction(expanded.Action), expanded.Condition));
                    }
                }
            }
        }
    }

    // ── Singleton rule building ────────────────────────────────────────────

    private void BuildSingletonRule(RuleDef rule, StateGraph graph,
        Dictionary<string, StateNode> nodeLookup,
        List<StateNode> allNodes, List<GraphEdgeDescriptor> edgeDescriptors,
        Dictionary<GraphEdgeDescriptor, StateEdge> edgeBacking,
        Dictionary<string, List<StateNode>> groupMembers, HashSet<Type> relevantTypes)
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
                foreach (IConditionalEdge input in cj.Inputs)
                {
                    edgeDescriptors.Add(new GraphEdgeDescriptor(
                        input.Source, cj, "", EdgeEffect.Conjunction, input.ConditionLabel));
                }
            }
            else if (logicNode is DisjunctionNode dj)
            {
                graph.AddDisjunction(dj);
                foreach (IConditionalEdge input in dj.Inputs)
                {
                    edgeDescriptors.Add(new GraphEdgeDescriptor(
                        input.Source, dj, "", EdgeEffect.Disjunction, input.ConditionLabel));
                }
            }

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
                    e => graph.AddEdge(e));

                foreach (TriggerDef expanded in expansion.Triggers)
                {
                    StateEdge? edge = CreateEdge(expanded, sourceNode, node, null, null, expansion.SuppressionGuard, sourceGate);
                    if (edge is not null)
                    {
                        graph.AddEdge(edge);
                        relevantTypes.Add(edge.MessageType);
                        GraphEdgeDescriptor descriptor = new(
                            sourceNode, node, expanded.On,
                            MapAction(expanded.Action), expanded.Condition);
                        edgeDescriptors.Add(descriptor);
                        // Pair descriptor ↔ backing edge for graph-breakpoint resolution.
                        edgeBacking[descriptor] = edge;
                    }
                }
            }
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
    ///     equality rather than the v1 substring scan. Feeds the enrichment-provider gate so a
    ///     health/weapon-dependent enrichment referenced only from the v2 side still activates its
    ///     per-player provider (the same union the enrichment edge relies on).
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

    /// <summary>
    ///     Pre-scans the demo's <c>player_team</c> events to determine each slot's
    ///     starting team_num. The first such event for a slot has <c>OldTeam</c>
    ///     equal to the team the player was on prior to the swap; if no event ever
    ///     fires for a slot, fall back to the final team in <c>demo.Players</c>.
    /// </summary>
    private void PopulateInitialTeams(PlayerContextIndex index)
    {
        if (_demo is null)
        {
            return;
        }

        HashSet<int> firstSeen = new();
        foreach (GameEvent ev in _demo.AllGameEvents)
        {
            if (ev.Payload is not PlayerTeamEvent pt)
            {
                continue;
            }

            if (!firstSeen.Add(pt.UserId))
            {
                continue;
            }

            index.InitialTeamBySlot[pt.UserId] = pt.OldTeam;
        }

        foreach ((int slot, PlayerInfo info) in _demo.Players)
        {
            if (!index.InitialTeamBySlot.ContainsKey(slot))
            {
                index.InitialTeamBySlot[slot] = info.Team;
            }
        }
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
