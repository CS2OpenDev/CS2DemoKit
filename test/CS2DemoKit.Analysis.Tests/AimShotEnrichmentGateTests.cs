#region

using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Analysis.Building;
using CS2DemoKit.Analysis.Edges;
using CS2DemoKit.Analysis.Profiles;
using CS2DemoKit.Analysis.Registry;

#endregion

namespace CS2DemoKit.Analysis.Tests;

/// <summary>
///     The shot-anchored aim enrichments are reference-gated on their output names: unless a rule
///     names one, <c>RuleChainBuilder</c> hands <see cref="AimShotContextEdge" /> a null source set
///     and the edge returns before touching a node. The nodes are registered either way, so a name
///     the gate does not know about does not fail the build and does not raise a diagnostic — the
///     stat reading it just reports the node's declared default, a sentinel or a <c>false</c>, on
///     every event of the demo.
///     <para>
///         That makes <see cref="BuiltinContexts.AimShotEnrichments" /> a list which is wrong
///         silently, which is the kind that drifts. These tests hold it to the set the two
///         constructed edges declare they write, so adding a fourteenth enrichment without gating it
///         fails here rather than shipping as a column of sentinels.
///     </para>
/// </summary>
[Category("Unit")]
public class AimShotEnrichmentGateTests
{
    // The enrichment infrastructure built against a throwaway graph, exactly as the catalogue
    // generator builds it: the node set and the edges are profile-independent (only the round-end
    // edge COUNT varies by binding), and the aim edges are constructed whether or not a rule
    // referenced one — inert sources is the gated-out state, not an absent edge.
    private static BuiltinContexts.EnrichmentInfrastructure Infrastructure() =>
        BuiltinContexts.CreateEnrichment(
            new StateGraph().Root,
            new PlayerContextIndex(),
            EventRegistry.Build(),
            new LogicalEventResolver(new Cs2GotvProfile()));

    // Every node an AimShotContextEdge instance declares it writes: the primary WrittenNode plus
    // the AdditionalWrittenNodes the topological sort orders on. Asking the edge is the point —
    // a list that restated the construction site would drift with it rather than against it.
    private static HashSet<string> WrittenByAimShotEdges()
    {
        HashSet<string> written = new(StringComparer.Ordinal);
        foreach (StateEdge edge in Infrastructure().Edges)
        {
            if (edge is not AimShotContextEdge)
            {
                continue;
            }

            if (edge.WrittenNode is { } primary)
            {
                written.Add(primary.Name);
            }

            foreach (StateNode node in edge.AdditionalWrittenNodes ?? [])
            {
                written.Add(node.Name);
            }
        }

        return written;
    }

    [Test]
    public async Task GateList_CoversEveryEnrichmentTheEdgeWrites()
    {
        HashSet<string> written = WrittenByAimShotEdges();
        await Assert.That(written).IsNotEmpty()
            .Because("an empty written set would make the rest of this vacuous");

        string[] ungated = written.Except(BuiltinContexts.AimShotEnrichments, StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        await Assert.That(ungated).IsEmpty()
            .Because("a rule reading one of these would never activate the edge, so the node would "
                     + "keep its declared default on every shot with nothing reporting the miss");
    }

    [Test]
    public async Task GateList_NamesNothingTheEdgeDoesNotWrite()
    {
        // The other direction: a stale or misspelt name would force the six aim columns into the
        // digest — the expensive ones — for a read that cannot happen.
        HashSet<string> written = WrittenByAimShotEdges();
        string[] unwritten = BuiltinContexts.AimShotEnrichments.Except(written, StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        await Assert.That(unwritten).IsEmpty()
            .Because("the gate would then pay for the aim columns on a read the edge never satisfies");
    }

    [Test]
    public async Task GateList_NamesRegisteredEnrichmentNodes()
    {
        // A name is only gateable if a rule can read it, which means it has to resolve through the
        // enrichment lookup the expression compiler and the catalogue both go through.
        BuiltinContexts.EnrichmentInfrastructure infrastructure = Infrastructure();
        foreach (string name in BuiltinContexts.AimShotEnrichments)
        {
            await Assert.That(infrastructure.NodeLookup.ContainsKey(name)).IsTrue()
                .Because($"{name} is gated on but no enrichment node answers to it");
        }
    }
}
