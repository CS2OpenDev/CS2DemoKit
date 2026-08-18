#region

using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Analysis.Building;
using CS2DemoKit.Analysis.Graphs;
using CS2DemoKit.Analysis.Registry;
using CS2DemoKit.Parser;
using CS2DemoKit.Parser.GameEvents;
using CS2DemoKit.TestSupport;

#endregion

namespace CS2DemoKit.Analysis.Tests;

/// <summary>
///     Phase-0 foundation tests for the Analysis-graph breakpoint feature. They pin the two
///     load-bearing pieces the rest of the feature builds on:
///     <list type="number">
///         <item>
///             <b>The descriptor → <see cref="StateEdge" /> bridge</b> (<see cref="BuildResult.EdgeBacking" />).
///             The visualization layer hands back a <see cref="GraphEdgeDescriptor" /> when an edge is
///             clicked; an edge breakpoint must resolve that to the runtime <see cref="StateEdge" /> whose
///             fires were recorded. The descriptor never holds the edge — the two are born in the same
///             build-loop iteration sharing <c>Source</c>/<c>Destination</c> node refs — so this asserts
///             the map is built and is coverage-correct.
///         </item>
///         <item>
///             <b>The applied-index recording</b> (<see cref="EvaluationResult.AppliedMessagesByEdge" />).
///             For each fired edge, the sorted global message indices at which it applied — recorded
///             side-effect-free during the single eval pass. The index alignment is the subtle part:
///             a recorded index must point at the exact message that fired the edge, proven here by
///             checking the decoded-event type at that index matches the edge's dispatch key.
///         </item>
///     </list>
///     Re-hosted on the built-in context edges (the game-scoped trigger edges every build wires)
///     after the Rulesets v1 chain layer was removed.
/// </summary>
[Category("Unit")]
public class GraphBreakpointRecordingTests
{
    // The built-in no_deaths_yet rule (display name "NoDeathsYet") deactivates on $player_death —
    // a real trigger-backed game-scoped edge at the BuildSingletonRule site, present in every build.
    private static (GraphEdgeDescriptor Descriptor, StateEdge Edge) ResolvePlayerDeathEdge(BuildResult build)
    {
        StateNode node = build.Nodes.First(n => n.Name == "NoDeathsYet");
        GraphEdgeDescriptor desc = build.Edges.First(e =>
            ReferenceEquals(e.Destination, node) && e.Label == "player_death");
        return (desc, build.EdgeBacking![desc]);
    }

    /// <summary>
    ///     EdgeBacking maps the trigger-backed graph-edge descriptor to a <see cref="StateEdge" />
    ///     whose <c>Source</c>/<c>WrittenNode</c> are the very nodes named on the descriptor (by
    ///     reference). Logic/conjunction descriptors carry no backing edge and stay out of the map.
    ///     Pure structural build — no demo required.
    /// </summary>
    [Test]
    public async Task EdgeBacking_MapsTriggerDescriptorToEdge_AndExcludesLogicEdges()
    {
        EventRegistry registry = EventRegistry.Build();
        RuleChainBuilder builder = new(registry);
        BuildResult build = builder.Build();

        await Assert.That(build.EdgeBacking).IsNotNull()
            .Because("the built-in context rules produce trigger-backed game edges");

        (GraphEdgeDescriptor triggerDesc, StateEdge edge) = ResolvePlayerDeathEdge(build);

        // Reference identity — the same node instances appear on both descriptor and edge.
        await Assert.That(ReferenceEquals(edge.Source, triggerDesc.Source)).IsTrue();
        await Assert.That(ReferenceEquals(edge.WrittenNode, triggerDesc.Destination)).IsTrue();

        // Global coverage invariant: EVERY entry in the map shares node refs with its descriptor.
        foreach ((GraphEdgeDescriptor desc, StateEdge backing) in build.EdgeBacking!)
        {
            await Assert.That(ReferenceEquals(backing.Source, desc.Source)).IsTrue();
            await Assert.That(ReferenceEquals(backing.WrittenNode, desc.Destination)).IsTrue();
        }

        // Exclusion: logic-input descriptors (conjunction/disjunction) have no backing StateEdge,
        // so they must NOT be keys in EdgeBacking.
        GraphEdgeDescriptor? logicDesc = build.Edges
            .FirstOrDefault(e => e.Effect is EdgeEffect.Conjunction or EdgeEffect.Disjunction);
        if (logicDesc is not null)
        {
            await Assert.That(build.EdgeBacking!.ContainsKey(logicDesc)).IsFalse()
                .Because("logic-input descriptors have no StateEdge and are out of breakpoint scope");
        }
    }

    /// <summary>
    ///     <see cref="EvaluationResult.AppliedMessagesByEdge" /> records, for the player_death edge,
    ///     the message indices at which it fired — sorted, in range, and (the alignment proof) each
    ///     index points at a <see cref="GameEventMessage" /> whose decoded-event type equals the
    ///     edge's dispatch key. A misaligned index (off-by-one in <c>snapshots.Count</c>) would point
    ///     at a different message type and fail. DEMO_PATH-gated.
    /// </summary>
    [Test]
    public async Task AppliedMessagesByEdge_RecordsPlayerDeathEdge_AtAlignedIndices()
    {
        string path = DemoTestHelper.RequireDemo();

        ParsedDemo parsed = DemoTestHelper.GetOrParse(path);

        EventRegistry registry = EventRegistry.Build();
        RuleChainBuilder builder = new(registry, parsed);
        BuildResult build = builder.Build();

        StateGraphEvaluator evaluator = new(build.Graph, parsed, build.PlayerContextIndex, build.EntityScanner);
        EvaluationResult result = evaluator.EvaluateWithSnapshots(parsed.Frames, build.Nodes);

        await Assert.That(result.AppliedMessagesByEdge).IsNotNull();

        // Resolve the player_death edge via the same descriptor → edge bridge the UI uses.
        (_, StateEdge backingEdge) = ResolvePlayerDeathEdge(build);

        bool recorded = result.AppliedMessagesByEdge!.TryGetValue(backingEdge, out List<int>? hits);
        await Assert.That(recorded).IsTrue()
            .Because("a typical match has player_death events, so the edge fired at least once");
        await Assert.That(hits!.Count).IsGreaterThan(0);

        int prev = -1;
        foreach (int idx in hits!)
        {
            await Assert.That(idx).IsGreaterThan(prev).Because("indices are appended in message order → strictly sorted");
            await Assert.That(idx).IsGreaterThanOrEqualTo(0);
            await Assert.That(idx).IsLessThan(result.Messages.Count);

            NetMessage m = result.Messages[idx].Message;
            await Assert.That(m is GameEventMessage).IsTrue()
                .Because("the recorded index must point at the event message that fired the edge");
            GameEvent recordedFire = ((GameEventMessage)m).DecodedEvent;
            await Assert.That(recordedFire.Payload?.GetType() ?? recordedFire.GetType())
                    .IsEqualTo(backingEdge.MessageType)
                .Because("alignment proof: the message at this index dispatches under the edge's key");

            prev = idx;
        }
    }
}
