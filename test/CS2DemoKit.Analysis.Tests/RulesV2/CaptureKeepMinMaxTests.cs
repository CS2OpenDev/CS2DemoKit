#region

using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Analysis.Building;
using CS2DemoKit.Analysis.Catalog;
using CS2DemoKit.Analysis.Config;
using CS2DemoKit.Analysis.Graphs;
using CS2DemoKit.Analysis.Nodes;
using CS2DemoKit.Analysis.Registry;
using CS2DemoKit.Analysis.Rules.Hashing;
using CS2DemoKit.Analysis.RulesetsV2.Compile;
using CS2DemoKit.Analysis.RulesetsV2.Model;
using CS2DemoKit.Analysis.RulesetsV2.Resolve;
using CS2DemoKit.Analysis.Yaml;
using CS2DemoKit.Parser;
using CS2DemoKit.Parser.GameEvents;

#endregion

using CS2DemoKit.TestSupport;

namespace CS2DemoKit.Analysis.Tests.RulesV2;

/// <summary>
///     Pre-freeze gap G2 (scalar min/max aggregation via <c>capture: … , keep: min | max</c>)
///     demo-free battery: the mapper carries <c>keep: min | max</c>, the resolver requires a numeric
///     capture value (a string value is a type error) and threads the keep onto
///     <see cref="CheckedStat.Keep" />, the resolved-identity hash distinguishes <c>keep: max</c> from
///     <c>keep: last</c> (row 8 keep-spec, no preimage change), and the planner lowers to a distinct
///     scalar value node. Runtime reduce semantics (running extremum + unseen→first-value + round
///     reset) are exercised directly on <see cref="OnGameEventReduceValue{TEvent,TValue}" /> over a
///     synthetic value sequence — no demo parse.
/// </summary>
[Category("Unit")]
public class CaptureKeepMinMaxTests
{
    private static readonly CatalogScopeAdapter _adapter = CatalogScopeAdapter.From(CatalogResource.Load());

    private static string CaptureYaml(string keep, string value = "event.tick") => $"""
                                                                                    ruleset: t
                                                                                    for: each_player
                                                                                    stats:
                                                                                      m:
                                                                                        capture: {value}
                                                                                        on: bomb_planted
                                                                                        keep: {keep}
                                                                                        per: round
                                                                                    """;

    // A genuinely String-typed capture value (event.Weapon in a kill view scope, as the bucket tests
    // key on) — used to prove keep: min | max reject a non-numeric value while keep: last accepts it.
    private static string StringCaptureYaml(string keep) => $"""
                                                             ruleset: t
                                                             for: each_player
                                                             stats:
                                                               m:
                                                                 capture: event.Weapon
                                                                 on: kill
                                                                 keep: {keep}
                                                                 per: round
                                                             """;

    // ── Mapping ──────────────────────────────────────────────────────────────

    [Test]
    public async Task Maps_KeepMin_AndMax()
    {
        StatDef min = Load(CaptureYaml("min")).Stats.Single(s => s.Id == "m");
        await Assert.That(min.Keep).IsEqualTo(KeepMode.Min);

        StatDef max = Load(CaptureYaml("max")).Stats.Single(s => s.Id == "m");
        await Assert.That(max.Keep).IsEqualTo(KeepMode.Max);
    }

    [Test]
    public async Task Keep_UnknownValue_IsRejected()
    {
        RulesetDocumentLoader.Outcome outcome =
            RulesetDocumentLoader.Load(CaptureYaml("median"), "t.rules.yaml");
        await Assert.That(outcome.Diagnostics.Any(d => d.Message.Contains("keep"))).IsTrue();
    }

    // ── Resolver ─────────────────────────────────────────────────────────────

    [Test]
    public async Task Resolver_KeepMax_ThreadsKeepKind()
    {
        CheckedStat max = Resolve(CaptureYaml("max")).Stats.Single(s => s.StatId == "m");
        await Assert.That(max.Keep).IsEqualTo(KeepKind.Max);

        CheckedStat min = Resolve(CaptureYaml("min")).Stats.Single(s => s.StatId == "m");
        await Assert.That(min.Keep).IsEqualTo(KeepKind.Min);
    }

    [Test]
    public async Task Resolver_KeepMax_OnStringValue_IsTypeError()
    {
        // A string capture value is fine for keep: last but a type error under keep: max — mirrors
        // the bucket min/max reducer requiring a numeric value.
        RulesetResolveResult resolved = ResolveResult(StringCaptureYaml("max"));
        await Assert.That(resolved.Diagnostics.Any(d => d.Code == ResolveDiagnosticCodes.BadSlotType)).IsTrue();
    }

    [Test]
    public async Task Resolver_KeepLast_OnStringValue_IsAccepted()
    {
        // The same string value under keep: last resolves clean — proving the error is keep-specific.
        RulesetResolveResult resolved = ResolveResult(StringCaptureYaml("last"));
        await Assert.That(resolved.Diagnostics.Any(d => d.Code == ResolveDiagnosticCodes.BadSlotType)).IsFalse();
    }

    // ── Resolved-identity (row 8 keep-spec) ──────────────────────────────────

    [Test]
    public async Task KeepMax_And_KeepLast_HashApart()
    {
        // keep is already row 8 of the preimage, so keep: max and keep: last over the SAME value
        // expression must hash apart with no preimage change.
        string maxHex = Hex(CaptureYaml("max"));
        string lastHex = Hex(CaptureYaml("last"));
        string minHex = Hex(CaptureYaml("min"));

        await Assert.That(maxHex).IsNotEqualTo(lastHex)
            .Because("keep: max and keep: last are identity-bearing (row 8 keep-spec)");
        await Assert.That(maxHex).IsNotEqualTo(minHex)
            .Because("keep: max and keep: min must hash apart");
        await Assert.That(Hex(CaptureYaml("max"))).IsEqualTo(maxHex)
            .Because("hashing is deterministic — a keep: max twin dedups");
    }

    // ── Planner ──────────────────────────────────────────────────────────────

    [Test]
    public async Task Planner_KeepMax_MaterializesScalarValueNode_DistinctFromKeepLast()
    {
        const string Yaml = """
                            ruleset: p
                            for: each_player
                            stats:
                              hi:
                                capture: event.tick
                                on: bomb_planted
                                keep: max
                                per: round
                              lo:
                                capture: event.tick
                                on: bomb_planted
                                keep: last
                                per: round
                            """;

        Dictionary<string, StateNode> nodes = Materialize(Resolve(Yaml));
        StateNode hi = nodes["p.hi"];
        StateNode lo = nodes["p.lo"];

        // A scalar reduce node is a plain (round-scoped) value node — NOT a list capture node.
        await Assert.That(hi).IsTypeOf<GenericRoundScopedValueNode<int>>();
        await Assert.That(ReferenceEquals(hi, lo)).IsFalse()
            .Because("keep: max and keep: last hash apart, so they must be distinct nodes");
    }

    // ── Runtime reduce semantics (direct edge, synthetic sequence) ───────────

    [Test]
    public async Task ReduceEdge_Max_KeepsRunningMaximum()
    {
        (GenericRoundScopedValueNode<int> node, OnGameEventReduceValue<RoundMvpEvent, int> edge, GenericRoundScopedBoolNode seen)
            = BuildReduce(true);

        Fire(edge, 3);
        await Assert.That(node.Value).IsEqualTo(3).Because("unseen window initializes to the first value, never max(0, 3)");
        Fire(edge, 7);
        await Assert.That(node.Value).IsEqualTo(7);
        Fire(edge, 2);
        await Assert.That(node.Value).IsEqualTo(7).Because("2 < 7, the maximum is unchanged");
        await Assert.That(seen.IsActive).IsTrue();
    }

    [Test]
    public async Task ReduceEdge_Min_KeepsRunningMinimum()
    {
        (GenericRoundScopedValueNode<int> node, OnGameEventReduceValue<RoundMvpEvent, int> edge, _)
            = BuildReduce(false);

        Fire(edge, 3);
        await Assert.That(node.Value).IsEqualTo(3);
        Fire(edge, 7);
        await Assert.That(node.Value).IsEqualTo(3).Because("7 > 3, the minimum is unchanged");
        Fire(edge, 2);
        await Assert.That(node.Value).IsEqualTo(2);
    }

    [Test]
    public async Task ReduceEdge_SingleValue_IsThatValue()
    {
        (GenericRoundScopedValueNode<int> node, OnGameEventReduceValue<RoundMvpEvent, int> edge, _)
            = BuildReduce(true);

        Fire(edge, 5);
        await Assert.That(node.Value).IsEqualTo(5);
    }

    [Test]
    public async Task ReduceEdge_UnseenInit_DoesNotMaxAgainstPhantomZero()
    {
        // A min over an all-positive series would collapse to 0 if the unseen window reduced against
        // the value node's phantom 0. The first value must initialize instead.
        (GenericRoundScopedValueNode<int> node, OnGameEventReduceValue<RoundMvpEvent, int> edge, _)
            = BuildReduce(false);

        Fire(edge, 5);
        await Assert.That(node.Value).IsEqualTo(5).Because("min(unseen, 5) = 5, not min(0, 5) = 0");
    }

    [Test]
    public async Task ReduceEdge_RoundReset_RestartsExtremum()
    {
        (GenericRoundScopedValueNode<int> node, OnGameEventReduceValue<RoundMvpEvent, int> edge, GenericRoundScopedBoolNode seen)
            = BuildReduce(true);

        Fire(edge, 9);
        Fire(edge, 4);
        await Assert.That(node.Value).IsEqualTo(9);

        // Round boundary: both the value node and the seen flag reset (they are round-scoped).
        ((IRoundScopedNode)node).Reset();
        seen.Reset();

        Fire(edge, 5);
        await Assert.That(node.Value).IsEqualTo(5)
            .Because("the new round re-initializes to the first value, not max(9, 5) from the prior window");
        Fire(edge, 8);
        await Assert.That(node.Value).IsEqualTo(8);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static (GenericRoundScopedValueNode<int>, OnGameEventReduceValue<RoundMvpEvent, int>, GenericRoundScopedBoolNode)
        BuildReduce(bool keepMax)
    {
        GenericBoolNode source = new("root");
        GenericRoundScopedValueNode<int> node = new("m", 0);
        GenericRoundScopedBoolNode seen = new("__seen");
        OnGameEventReduceValue<RoundMvpEvent, int> edge =
            new(source, node, evt => evt.Of<RoundMvpEvent>().Value, null, seen, keepMax);
        return (node, edge, seen);
    }

    private static void Fire(OnGameEventReduceValue<RoundMvpEvent, int> edge, int value)
    {
        GameEvent fire = TestGameEvents.RoundMvp(userId: 0, value: value, eventId: 0);
        GameEventMessage msg = GameEventMessage.ForSynthesizedEvent(fire);
        DemoFrame frame = new()
        {
            Command = "DEM_Packet",
            FrameNumber = 0,
            ServerTick = 0,
            RawStart = 0,
            RawLength = 1,
            HeaderLength = 1,
            IsCompressed = false,
            MessageList = [msg]
        };

        // TryApplyDirect takes the PAYLOAD — it casts straight to TEvent — while the element and
        // condition selectors take the fire, which they reach through the context. A default context
        // would leave them nothing to read.
        edge.TryApplyDirect(fire.Payload!, new EvaluationContext(msg, frame));
    }

    private static RulesetDoc Load(string yaml) =>
        RulesetDocumentLoader.Load(yaml, "t.rules.yaml").Doc
        ?? throw new InvalidOperationException("ruleset failed to map");

    private static RulesetResolveResult ResolveResult(string yaml) =>
        CheckedRulesetDraft.Load(Load(yaml), _adapter).Build(64.0, "Cs2GotvProfile");

    private static CheckedRuleset Resolve(string yaml) =>
        ResolveResult(yaml).Ruleset
        ?? throw new InvalidOperationException("ruleset failed to resolve");

    private static string Hex(string yaml)
    {
        CheckedRuleset rs = Resolve(yaml);
        MapStatHashSource source = new(new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.Ordinal));
        return Convert.ToHexStringLower(V2StatHasher.Hash(rs.Stats.Single(s => s.StatId == "m"), source));
    }

    private static Dictionary<string, StateNode> Materialize(CheckedRuleset rs)
    {
        RuleChainBuilder builder = new(EventRegistry.Build());
        BuildResult build = builder.Build([rs]);

        Dictionary<string, StateNode> merged = new(StringComparer.OrdinalIgnoreCase);
        foreach (PerPlayerNodeTemplate template in build.Graph.PerPlayerTemplates)
        {
            PerPlayerNodeTemplate.MaterializedPlayer player = template.Materialize(0, 0, "test", null);
            if (player.NodesByRuleId is { } byId)
            {
                foreach ((string key, StateNode node) in byId)
                {
                    merged[key] = node;
                }
            }
        }

        return merged;
    }
}
