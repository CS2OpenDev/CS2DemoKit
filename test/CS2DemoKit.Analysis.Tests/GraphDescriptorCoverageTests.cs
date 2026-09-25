#region

using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Analysis.Building;
using CS2DemoKit.Analysis.Edges;
using CS2DemoKit.Analysis.Graphs;
using CS2DemoKit.Analysis.Nodes;

#endregion

namespace CS2DemoKit.Analysis.Tests;

/// <summary>
///     The descriptor lists are the graph a consumer draws, so they have to be the graph that runs:
///     every <see cref="StateEdge" /> the builder adds has a descriptor carrying it and its real
///     source, every piece of wiring that is not an edge is drawn, and no node the builder makes is
///     left with nothing drawn into it. Runs over the shipped rulesets and the inline matrix on GOTV
///     and HLTV, game scope and a template materialised at two slots.
/// </summary>
[Category("Unit")]
public class GraphDescriptorCoverageTests
{
    // Edge class (generic definition for a generic one) → the kind its rows must carry. An edge class
    // the builder adds that is missing here fails the coverage test, so a new one gets classified.
    private static readonly Dictionary<Type, GraphEdgeKind> _expectedKinds = new()
    {
        [typeof(OnGameEvent<>)] = GraphEdgeKind.Trigger,
        [typeof(OnGameEventSetValue<,>)] = GraphEdgeKind.Trigger,
        [typeof(OnGameEventReduceValue<,>)] = GraphEdgeKind.Trigger,
        [typeof(OnNetMessage<>)] = GraphEdgeKind.Trigger,
        [typeof(OnNetMessageSetValue<,>)] = GraphEdgeKind.Trigger,
        [typeof(OnEntityChange<>)] = GraphEdgeKind.Trigger,
        [typeof(OnEntityChangeSetValue<,>)] = GraphEdgeKind.Trigger,
        [typeof(KeyedCounterEdge<>)] = GraphEdgeKind.Trigger,
        [typeof(WindowedStreakEdge)] = GraphEdgeKind.Trigger,
        [typeof(WindowedHighlightPulseEdge)] = GraphEdgeKind.Trigger,
        [typeof(RoundScopedLogicNodeReset)] = GraphEdgeKind.RoundReset,
        [typeof(KillTeamEnrichmentEdge)] = GraphEdgeKind.Enrichment,
        [typeof(ClutchEnrichmentEdge)] = GraphEdgeKind.Enrichment,
        [typeof(HurtTeamEnrichmentEdge)] = GraphEdgeKind.Enrichment,
        [typeof(BlindEnrichmentEdge)] = GraphEdgeKind.Enrichment,
        [typeof(WeaponFireEnrichmentEdge)] = GraphEdgeKind.Enrichment,
        [typeof(HurtBulletEnrichmentEdge)] = GraphEdgeKind.Enrichment,
        [typeof(ShotEnrichmentEdge)] = GraphEdgeKind.Enrichment,
        [typeof(SprayKillEnrichmentEdge)] = GraphEdgeKind.Enrichment,
        [typeof(SpottedEnrichmentEdge)] = GraphEdgeKind.Enrichment,
        [typeof(AimShotContextEdge)] = GraphEdgeKind.Enrichment,
        [typeof(RoundEndEnrichmentEdge)] = GraphEdgeKind.Enrichment,
        [typeof(ClutchResolutionEnrichmentEdge)] = GraphEdgeKind.Enrichment,
        [typeof(PlayerTeamEdge)] = GraphEdgeKind.PlayerState,
        [typeof(BombPlantedEdge)] = GraphEdgeKind.PlayerState,
        [typeof(BombDefusedEdge)] = GraphEdgeKind.PlayerState,
        [typeof(BombExplodedEdge)] = GraphEdgeKind.PlayerState,
        [typeof(HealthResetEdge)] = GraphEdgeKind.PlayerState,
        [typeof(PlayerDisconnectEdge)] = GraphEdgeKind.PlayerState,
        [typeof(PlayerConnectEdge)] = GraphEdgeKind.PlayerState,
        [typeof(PlayerSpawnConnectivityEdge)] = GraphEdgeKind.PlayerState,
        [typeof(RoundDecidedEdge)] = GraphEdgeKind.PlayerState,
        [typeof(BombPlantSiteEdge)] = GraphEdgeKind.RoundFact,
        [typeof(RecordFirstEventTickEdge)] = GraphEdgeKind.FirstTick,
        [typeof(ComputeOnRoundEndEdge)] = GraphEdgeKind.RoundEndCompute,
        [typeof(ThresholdTallyEdge)] = GraphEdgeKind.Tally,
        [typeof(PlayerEconomyFreezeEndEdge)] = GraphEdgeKind.FreezeEndEconomy,
        [typeof(SideRosterFreezeEndEdge)] = GraphEdgeKind.FreezeEndRoster,
        [typeof(EntityPullNodeSettleEdge)] = GraphEdgeKind.EntitySettle
    };

    // Each build's descriptor sets as the builder drew them at dc0c6b6, before this work: sorted
    // "G|P" + source + destination + label + effect + condition, the tally rows left out (they were
    // drawn from the tallied stat and are now drawn from the root). Every one of them must still be
    // drawn the same way. The HLTV shipped template did not materialise at dc0c6b6 (#68), so its
    // player rows were added to that pin with the fix.
    private static readonly Dictionary<string, string> _baselineRows = new(StringComparer.Ordinal)
    {
        ["shipped-gotv"] = "142:2DD1650AAB0812F2",
        ["shipped-hltv"] = "139:076CB20E5C049366",
        ["matrix-gotv"] = "84:8AEA2206E51EAD85",
        ["matrix-hltv"] = "84:8B641E41CF6BFC10"
    };

    public static IEnumerable<string> CaseNames() => RuleGraphFixtures.CoverageCases().Select(c => c.Name);

    [Test]
    [MethodDataSource(nameof(CaseNames))]
    public async Task EveryStateEdge_HasADescriptor_WithItsRealSource(string name)
    {
        List<string> failures = [];
        foreach (Scope scope in Scopes(name))
        {
            HashSet<StateEdge> described = new(ReferenceEqualityComparer.Instance);
            foreach (GraphEdgeDescriptor d in scope.Descriptors)
            {
                if (d.Kind == GraphEdgeKind.Undescribed)
                {
                    failures.Add($"{scope.Name}: a builder row is Undescribed: {Render(d)}");
                }

                if (d.Edge is not { } edge)
                {
                    continue;
                }

                described.Add(edge);
                if (!ReferenceEquals(d.Source, edge.Source))
                {
                    failures.Add($"{scope.Name}: {Render(d)} is drawn from {d.Source.Name}, the edge fires from {edge.Source.Name}");
                }

                if (!IsWriteOf(edge, d))
                {
                    failures.Add($"{scope.Name}: {Render(d)} points at a node its edge neither declares nor is known to write");
                }

                Type key = edge.GetType().IsGenericType ? edge.GetType().GetGenericTypeDefinition() : edge.GetType();
                if (!_expectedKinds.TryGetValue(key, out GraphEdgeKind expected))
                {
                    failures.Add($"{scope.Name}: edge class {key.Name} is not classified in this test");
                }
                else if (d.Kind != expected)
                {
                    failures.Add($"{scope.Name}: {Render(d)} is {d.Kind}, expected {expected}");
                }
            }

            foreach (StateEdge edge in scope.Edges)
            {
                if (!described.Contains(edge))
                {
                    failures.Add($"{scope.Name}: {edge.GetType().Name} from {edge.Source.Name} has no descriptor");
                }
            }

            foreach (StateEdge edge in described)
            {
                if (!scope.Edges.Contains(edge))
                {
                    failures.Add($"{scope.Name}: a descriptor carries a {edge.GetType().Name} the graph never registered");
                }
            }
        }

        await Assert.That(string.Join("\n", failures)).IsEmpty();
    }

    /// <summary>
    ///     The external-state table in the wiring is keyed by edge class. An edge class that is handed
    ///     the per-player context or the entity scanner almost certainly reads or writes that state, so
    ///     one missing from the table would draw neither and nothing would say so.
    /// </summary>
    [Test]
    public async Task EveryEdgeTakingExternalState_IsInTheWiringTable()
    {
        List<string> missing = [];
        foreach (Type type in typeof(StateEdge).Assembly.GetTypes())
        {
            if (type.IsAbstract || !typeof(StateEdge).IsAssignableFrom(type))
            {
                continue;
            }

            bool takesExternal = type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .SelectMany(c => c.GetParameters())
                .Any(p => p.ParameterType == typeof(PlayerContextIndex) || p.ParameterType == typeof(EntityChangeScanner));
            if (takesExternal && GraphWiring.ExternalAccessOf(type) is null)
            {
                missing.Add(type.Name);
            }
        }

        await Assert.That(string.Join(", ", missing)).IsEmpty();
    }

    /// <summary>
    ///     The kill is what moves <c>round.team.alive</c>, through per-player state no node holds. The
    ///     path has to be drawn end to end: the kill enrichment writes the per-player context, and
    ///     every team aggregate and clutch facet pulls from it.
    /// </summary>
    [Test]
    public async Task PlayerContextWriters_AreDrawn()
    {
        BuildResult build = RuleGraphFixtures.Cases().Single(c => c.Name == "shipped-gotv").Build();
        ExternalStateNode context = build.ExternalNodes.Single(n => n.Kind == ExternalState.PlayerContext);

        await Assert.That(build.Edges.Any(d => d.Edge is KillTeamEnrichmentEdge && ReferenceEquals(d.Destination, context)))
            .IsTrue().Because("the kill enrichment marks the victim dead in the per-player context");

        PerPlayerNodeTemplate.MaterializedPlayer p = build.Graph.PerPlayerTemplates[0].Materialize(0, 0, "p0");
        List<StateNode> pulled = [.. p.Nodes.Where(n => n is RoundTeamAggregateNode or RoundClutchFacetNode or RoundClutchSizeNode)];
        await Assert.That(pulled.Count).IsEqualTo(6);
        foreach (StateNode node in pulled)
        {
            await Assert.That(p.EdgeDescriptors.Any(d => d.Kind == GraphEdgeKind.Pull
                                                        && ReferenceEquals(d.Source, context)
                                                        && ReferenceEquals(d.Destination, node)))
                .IsTrue().Because($"{node.Name} reads the per-player context on demand");
        }
    }

    /// <summary>
    ///     A first-wins guard is set by the trigger that checks it, and neither the written node nor the
    ///     additional writes of the edge say so. The row to the guard, and the guard in the reads, are
    ///     what draw it.
    /// </summary>
    [Test]
    public async Task GuardedTrigger_DrawsItsGuardWrite()
    {
        BuildResult build = RuleGraphFixtures.Cases().Single(c => c.Name == "matrix-gotv").Build();
        PerPlayerNodeTemplate.MaterializedPlayer p = build.Graph.PerPlayerTemplates[0].Materialize(0, 0, "p0");

        List<GraphEdgeDescriptor> guardRows = [.. p.EdgeDescriptors.Where(d =>
            d.Kind == GraphEdgeKind.Trigger && d.Destination.Name.StartsWith("__seen_fm_player_first_dmg", StringComparison.Ordinal))];
        await Assert.That(guardRows.Count).IsGreaterThan(0).Because("capture keep: first sets a guard on its first fire");
        foreach (GraphEdgeDescriptor row in guardRows)
        {
            await Assert.That(row.Edge!.WrittenNode!.Name).IsEqualTo("first_dmg");
            await Assert.That(row.Reads.Any(r => ReferenceEquals(r, row.Destination))).IsTrue()
                .Because("the trigger checks the guard before it fires");
            await Assert.That(row.Effect).IsEqualTo(EdgeEffect.Activate);
        }

        List<GraphEdgeDescriptor> seenRows = [.. p.EdgeDescriptors.Where(d =>
            d.Kind == GraphEdgeKind.Trigger && d.Destination.Name == "__seen_fm_player_min_dmg")];
        await Assert.That(seenRows.Count).IsGreaterThan(0).Because("a min reduction marks its window seen");
    }

    [Test]
    [MethodDataSource(nameof(CaseNames))]
    public async Task NonEdgeWiring_IsDescribed(string name)
    {
        List<string> failures = [];
        foreach (Scope scope in Scopes(name))
        {
            bool Has(GraphEdgeKind kind, StateNode from, StateNode to) =>
                scope.Descriptors.Any(d => d.Kind == kind && ReferenceEquals(d.Source, from) && ReferenceEquals(d.Destination, to));

            foreach (StateNode node in scope.OwnNodes)
            {
                IEnumerable<IConditionalEdge> inputs = node switch
                {
                    ConjunctionNode cj => cj.Inputs,
                    DisjunctionNode dj => dj.Inputs,
                    _ => []
                };
                foreach (StateNode source in inputs.SelectMany(i => i.Sources))
                {
                    if (!Has(GraphEdgeKind.LogicInput, source, node))
                    {
                        failures.Add($"{scope.Name}: logic input {source.Name} -> {node.Name} is not drawn");
                    }
                }

                switch (node)
                {
                    case RoundTeamAggregateNode or RoundClutchFacetNode or RoundClutchSizeNode:
                        if (!Has(GraphEdgeKind.Pull, scope.External(ExternalState.PlayerContext), node))
                        {
                            failures.Add($"{scope.Name}: {node.Name} has no pull from the player context");
                        }

                        break;
                    case EntityValuePullNode:
                        if (!Has(GraphEdgeKind.Pull, scope.External(ExternalState.EntityState), node))
                        {
                            failures.Add($"{scope.Name}: {node.Name} has no pull from the entity state");
                        }

                        break;
                    case KeyedRatioNode ratio:
                        List<GraphEdgeDescriptor> pulls = [.. scope.Descriptors.Where(d => d.Kind == GraphEdgeKind.Pull && ReferenceEquals(d.Destination, ratio))];
                        if (pulls.Count != 2 || !pulls.All(d => d.Source is KeyedCounterNode))
                        {
                            failures.Add($"{scope.Name}: rate {node.Name} is not drawn from its two buckets");
                        }

                        break;
                }
            }

            foreach ((StateNode trigger, Action _, StateNode? writes) in scope.Player?.RisingEdgeActions ?? [])
            {
                if (writes is null || !Has(GraphEdgeKind.RisingEdgeAction, trigger, writes))
                {
                    failures.Add($"{scope.Name}: rising-edge action on {trigger.Name} is not drawn");
                }
            }

            foreach ((StateNode trigger, Action<int, int> _, StateNode? _) in scope.Player?.ContextRisingEdgeActions ?? [])
            {
                if (!Has(GraphEdgeKind.RisingEdgeAction, trigger, scope.External(ExternalState.Highlights)))
                {
                    failures.Add($"{scope.Name}: the emission on {trigger.Name} is not drawn");
                }
            }

            IEnumerable<LiveComputeRegistration> live = scope.Player is { } player ? player.LiveComputes ?? [] : scope.Graph.LiveComputes;
            foreach (LiveComputeRegistration reg in live)
            {
                foreach (StateNode read in reg.Reads)
                {
                    if (!Has(GraphEdgeKind.LiveComputeRead, read, reg.Compute))
                    {
                        failures.Add($"{scope.Name}: live compute {reg.Compute.Name} does not draw its read of {read.Name}");
                    }
                }
            }

            if (scope.Player is null)
            {
                foreach (StateNode node in scope.Nodes.Where(n => n.Name.StartsWith("entity.", StringComparison.Ordinal)))
                {
                    if (!Has(GraphEdgeKind.EntityValue, scope.External(ExternalState.EntityState), node))
                    {
                        failures.Add($"{scope.Name}: entity value {node.Name} is not drawn from the entity state");
                    }
                }
            }
        }

        await Assert.That(string.Join("\n", failures)).IsEmpty();
    }

    /// <summary>
    ///     Every node the builder makes has something drawn into it, the root aside. The one exception
    ///     is by design: a count on a net message records no first tick (a net message carries no game
    ///     tick), so its <c>__first_tick_</c> companion is never written.
    /// </summary>
    [Test]
    [MethodDataSource(nameof(CaseNames))]
    public async Task NoOrphanNodes(string name)
    {
        HashSet<string> allowed = new(StringComparer.Ordinal)
        {
            "__first_tick_fm_player_headers"
        };

        List<string> orphans = [];
        foreach (Scope scope in Scopes(name))
        {
            HashSet<StateNode> inbound = new(scope.Descriptors.Select(d => d.Destination), ReferenceEqualityComparer.Instance);
            foreach (StateNode node in scope.OwnNodes)
            {
                if (node is not RootNode && !inbound.Contains(node) && !allowed.Contains(node.Name))
                {
                    orphans.Add($"{scope.Name}: {node.Name} ({node.GetType().Name})");
                }
            }
        }

        await Assert.That(string.Join("\n", orphans)).IsEmpty();
    }

    [Test]
    [MethodDataSource(nameof(CaseNames))]
    public async Task EveryEndpointAndRead_Joins(string name)
    {
        List<string> failures = [];
        foreach (Scope scope in Scopes(name))
        {
            HashSet<StateNode> known = new(scope.Nodes, ReferenceEqualityComparer.Instance);
            foreach (GraphEdgeDescriptor d in scope.Descriptors)
            {
                foreach (StateNode node in (StateNode[])[d.Source, d.Destination, .. d.Reads])
                {
                    if (!known.Contains(node))
                    {
                        failures.Add($"{scope.Name}: {Render(d)} names {node.Name}, which is not a node of the build");
                    }
                }
            }
        }

        await Assert.That(string.Join("\n", failures)).IsEmpty();
    }

    /// <summary>
    ///     The descriptor counts for the shipped rulesets on GOTV, game scope and one player. Before this
    ///     work they were 43 and 107; the 67 game edges and 174 player edges they now all describe did
    ///     not move.
    /// </summary>
    [Test]
    public async Task ShippedGotv_DescriptorCounts()
    {
        BuildResult build = RuleGraphFixtures.Cases().Single(c => c.Name == "shipped-gotv").Build();
        PerPlayerNodeTemplate.MaterializedPlayer p = build.Graph.PerPlayerTemplates[0].Materialize(0, 0, "p0");

        await Assert.That(build.Graph.Edges.Count).IsEqualTo(67);
        await Assert.That(p.Edges.Count).IsEqualTo(174);
        await Assert.That(build.Edges.Count).IsEqualTo(ShippedGameRows);
        await Assert.That(p.EdgeDescriptors.Count).IsEqualTo(ShippedPlayerRows);
        await Assert.That(build.EdgeBacking!.Count).IsEqualTo(build.Edges.Count(d => d.Edge is not null));
    }

    private const int ShippedGameRows = 134;
    private const int ShippedPlayerRows = 211;

    /// <summary>A tally fires from the root at round end and reads its stat: drawn that way, not from the stat.</summary>
    [Test]
    public async Task Tally_IsDrawnFromRoot_AndReadsItsSource()
    {
        BuildResult build = RuleGraphFixtures.Cases().Single(c => c.Name == "matrix-gotv").Build();
        PerPlayerNodeTemplate.MaterializedPlayer p = build.Graph.PerPlayerTemplates[0].Materialize(0, 0, "p0");

        List<GraphEdgeDescriptor> tally = [.. p.EdgeDescriptors.Where(d => d.Kind == GraphEdgeKind.Tally)];
        await Assert.That(tally.Count).IsGreaterThan(0);
        foreach (GraphEdgeDescriptor row in tally)
        {
            await Assert.That(row.Source).IsTypeOf<RootNode>();
            await Assert.That(row.Reads.Any(r => r.Name == "kills")).IsTrue();
        }

        // Both buckets and the guard, per concrete round-end event.
        await Assert.That(tally.Select(d => d.Destination.Name).Distinct().Order())
            .IsEquivalentTo(["__seen_tally_fm_player_multi_tally", "rounds_2k", "rounds_3k"]);
    }

    /// <summary>
    ///     Every row an edge backs has a label, and every row drawn before this work is still drawn
    ///     with the same source, destination, label, effect and condition, which is what the fire
    ///     counters and breakpoints of a consumer look rows up by.
    /// </summary>
    [Test]
    [MethodDataSource(nameof(CaseNames))]
    public async Task Labels_AreNeverEmpty_AndBaselineRowsAreUnchanged(string name)
    {
        List<string> failures = [];
        List<string> rows = [];
        foreach (Scope scope in Scopes(name).Where(s => s.Slot is null or 0))
        {
            string prefix = scope.Player is null ? "G" : "P";
            foreach (GraphEdgeDescriptor d in scope.Descriptors)
            {
                if (d.Edge is not null && string.IsNullOrEmpty(d.Label))
                {
                    failures.Add($"{scope.Name}: {Render(d)} has no label");
                }

                if (IsBaselineShaped(d))
                {
                    rows.Add($"{prefix}|{d.Source.Name}|{d.Destination.Name}|{d.Label}|{d.Effect}|{d.ConditionLabel}");
                }
            }
        }

        rows.Sort(StringComparer.Ordinal);
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", rows)));
        string actual = $"{rows.Count}:{Convert.ToHexString(hash)[..16]}";
        if (_baselineRows.TryGetValue(name, out string? pinned) && actual != pinned)
        {
            failures.Add($"baseline rows moved: {actual}, pinned {pinned}\n  " + string.Join("\n  ", rows));
        }

        await Assert.That(string.Join("\n", failures)).IsEmpty();
    }

    // The shape a row had before this work: a trigger drawn to the node it writes, a logic input from
    // the primary source of an input, or a rising-edge action to a node.
    private static bool IsBaselineShaped(GraphEdgeDescriptor d) => d.Kind switch
    {
        GraphEdgeKind.Trigger => ReferenceEquals(d.Destination, d.Edge?.WrittenNode),
        GraphEdgeKind.LogicInput => (d.Destination switch
        {
            ConjunctionNode cj => cj.Inputs,
            DisjunctionNode dj => dj.Inputs,
            _ => []
        }).Any(i => ReferenceEquals(i.Source, d.Source) && i.ConditionLabel == d.ConditionLabel),
        GraphEdgeKind.RisingEdgeAction => d.Destination is not ExternalStateNode,
        _ => false
    };

    private static bool IsWriteOf(StateEdge edge, GraphEdgeDescriptor d)
    {
        if (edge is RoundScopedLogicNodeReset reset)
        {
            return ReferenceEquals(d.Destination, reset.WrappedNode);
        }

        return ReferenceEquals(d.Destination, edge.WrittenNode)
               || (edge.AdditionalWrittenNodes?.Any(n => ReferenceEquals(n, d.Destination)) ?? false)
               || d.Destination is ExternalStateNode
               // A guard the trigger sets is also one it checks.
               || (d.Destination is BoolNode && d.Reads.Any(r => ReferenceEquals(r, d.Destination)));
    }

    private static string Render(GraphEdgeDescriptor d) =>
        $"[{d.Kind}] {d.Source.Name} -> {d.Destination.Name} '{d.Label}' ({d.Edge?.GetType().Name ?? "no edge"})";

    private static IEnumerable<Scope> Scopes(string name)
    {
        (_, Func<BuildResult> make) = RuleGraphFixtures.CoverageCases().Single(c => c.Name == name);
        BuildResult build = make();
        yield return new Scope($"{name}/game", build, null, null);

        for (int t = 0; t < build.Graph.PerPlayerTemplates.Count; t++)
        {
            foreach (int slot in (int[])[0, 7])
            {
                PerPlayerNodeTemplate.MaterializedPlayer p = build.Graph.PerPlayerTemplates[t].Materialize(slot, slot, $"p{slot}");
                yield return new Scope($"{name}/t{t}/slot{slot}", build, p, slot);
            }
        }
    }

    private sealed record Scope(string Name, BuildResult Build, PerPlayerNodeTemplate.MaterializedPlayer? Player, int? Slot)
    {
        internal StateGraph Graph => Build.Graph;

        internal IReadOnlyList<StateNode> OwnNodes => Player is { } p ? p.Nodes : Build.Nodes;

        internal IReadOnlyList<StateNode> Nodes =>
            Player is { } p ? [.. Build.Nodes, .. Build.ExternalNodes, .. p.Nodes] : [.. Build.Nodes, .. Build.ExternalNodes];

        internal IReadOnlyList<StateEdge> Edges => Player is { } p ? p.Edges : Build.Graph.Edges;

        internal IReadOnlyList<GraphEdgeDescriptor> Descriptors => Player is { } p ? p.EdgeDescriptors : Build.Edges;

        internal ExternalStateNode External(ExternalState kind) => Build.ExternalNodes.Single(n => n.Kind == kind);
    }
}
