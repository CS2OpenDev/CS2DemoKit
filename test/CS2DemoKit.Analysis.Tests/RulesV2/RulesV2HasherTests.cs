#region

using System.Security.Cryptography;
using System.Text;
using CS2DemoKit.Analysis.Rules;
using CS2DemoKit.Analysis.Rules.Checking;
using CS2DemoKit.Analysis.Rules.Hashing;
using CS2DemoKit.Analysis.Rules.Scopes;

#endregion

namespace CS2DemoKit.Analysis.Tests.RulesV2;

/// <summary>
///     Semantic-core hasher battery — the corruption gate. Pins the spec §6
///     resolved-identity preimage: the golden hex hashes (any diff is a deliberate,
///     commit-audited re-baseline), the row-6 recursive identity (the SAME reference text
///     resolving to two DIFFERENT stat nodes must hash apart — the round-2 false-sharing
///     finding; and two different spellings of the SAME node must hash together), and the
///     row-9 id-salt for input-less stats. Pure in-memory; no demo.
/// </summary>
[Category("Unit")]
public class RulesV2HasherTests
{
    // ── Fixture: a deterministic scope + fake stat-hash source ───────────────────

    private static ScopeEnvironment Env() =>
        new("where:",
        [
            ScopeSymbol.Namespace("event",
                ScopeSymbol.Value("weapon", RulesType.String),
                ScopeSymbol.Value("tick", RulesType.Instant)),
            ScopeSymbol.Namespace("player",
                ScopeSymbol.Value("health", RulesType.Int),
                ScopeSymbol.Value("name", RulesType.String)),
            ScopeSymbol.Stat("kills", RulesType.Int),
            ScopeSymbol.Stat("reaction", RulesType.Duration),
            ScopeSymbol.Stat("weapons_seen", RulesType.ListOf(RulesTypeKind.String)),
            // A second ruleset exposing its own stats under a qualified namespace (D11a).
            ScopeSymbol.Namespace("otherrs",
                ScopeSymbol.Stat("kills", RulesType.Int))
        ]);

    private static CheckedExpression Analyze(string source) =>
        ExpressionPipeline.Analyze(source, Env()).Require();

    private static string HashOf(string source, IStatHashSource? statHashes = null) =>
        ExpressionHasher.ComputeHashHex(Analyze(source), statHashes ?? new FakeStatHashes());

    // A per-player, match-scoped counter (the compound-axis successor to the old collapsed
    // ScopeAxis.Player). Its match/round distinction is now explicit (plan decision 5).
    private static RuleNodeDescriptor Counter(string id, string[] events, string? expression = null) =>
        new(id, RuleNodeKind.Count, RulesType.Int, ScopeAxis.PlayerMatch, events,
            expression is null ? null : Analyze(expression));

    // ── Preimage stability goldens (the freeze artifact 3 shape) ─────────────────

    /// <summary>
    ///     The pinned expression-hash goldens. A diff here means the preimage changed — after
    ///     the hash freeze that is a breaking change and must be a deliberate,
    ///     commit-audited re-baseline.
    /// </summary>
    [Test]
    [Arguments("kills > 1", "7f492eebdbe400b24ab7d3eb0ee92403b7d82b997bc8785878654cbc12929ad3")]
    [Arguments("player.health + 1 == 101", "148df3a031e2cde002078b058e706710cd91fcfdba148e85c4fc9bd424b4e263")]
    [Arguments("min(kills, 3) in [1, 2, 3]", "2678b01f11bd4441b9e86e3e10570654733bb8ab93c6cf6dc7b085355a892d7b")]
    [Arguments("reaction > 5s", "0452236ef6afa071726ca4f0b65e244ffff7819d3c60f7874d15f2b4bdd3b8f6")]
    [Arguments("weapons_seen.count > 0 and contains(player.name, \"s\")", "a6a7aa0be9e4f30baf78193aebe3af6dd72eeff968ec8a05ad10c08eba885926")]
    [Arguments("not (event.weapon == \"awp\") or kills - 1 >= 2 * kills", "5361eec6f52a95308de286e559c23dbd064453392093663d9942b70f8cb40938")]
    public async Task Hash_ExpressionGoldens_Stable(string source, string expectedHex)
    {
        await Assert.That(HashOf(source)).IsEqualTo(expectedHex);
    }

    /// <summary>The full node preimage text is pinned verbatim (row framing is the contract).</summary>
    [Test]
    public async Task Preimage_NodeText_Pinned()
    {
        RuleNodeDescriptor node = new("double_kill", RuleNodeKind.Count, RulesType.Int, ScopeAxis.Round,
            ["player_death"], Analyze("kills > 1"));

        string preimage = RuleHasher.BuildPreimage(node, new FakeStatHashes());

        await Assert.That(preimage).IsEqualTo("dvr2|1:5:count|2:3:int|3:5:round|4:12:player_death"
                                              + "|5:93:dv2-expr|(gt (stat a047a4dfeb5b168fbce5661bb67646c74d484c685a8a6f3b95577c5e114e6ce7) (int 1))"
                                              + "|7:0:|8:0:|9:0:");
    }

    /// <summary>Pinned node-hash goldens across the descriptor shapes.</summary>
    [Test]
    public async Task Hash_NodeGoldens_Stable()
    {
        FakeStatHashes statHashes = new();

        string triggered = RuleHasher.ComputeHashHex(
            Counter("kills", ["player_death"]), statHashes);
        string computed = RuleHasher.ComputeHashHex(
            new RuleNodeDescriptor("avg", RuleNodeKind.Compute, RulesType.Float, ScopeAxis.Match, [],
                Analyze("kills + 1")), statHashes);
        string inputless = RuleHasher.ComputeHashHex(
            new RuleNodeDescriptor("marker", RuleNodeKind.Flag, RulesType.Bool, ScopeAxis.Round, []), statHashes);

        // Re-baselined: the Counter helper's axis moved from the removed ScopeAxis.Player to
        // the compound ScopeAxis.PlayerMatch, so row 3 is now "player_match" (was "player").
        await Assert.That(triggered).IsEqualTo("59396c258b3b6e516b5d031e5839ac00a72e3cf002614f1f866caf6f6eaf55fb");
        await Assert.That(computed).IsEqualTo("7ea2f6e4ad37add7ef4fc77a0c3fca39c0d853173670ee80dbe22b21d1b6e5bf");
        await Assert.That(inputless).IsEqualTo("b00a5a0c0939c86c3c1a9b9371bd1f423134f360fc9fff8892224ac21370d9fe");
    }

    // ── Row 6: resolved identity (the single most important gate) ────────────────

    /// <summary>
    ///     THE round-2 false-sharing pin: the SAME reference text resolving (via the stat-hash
    ///     source) to two DIFFERENT stat nodes yields two different expression hashes.
    ///     Text-keyed hashing here would be corruption — two rulesets' different 'kills'
    ///     stats would silently share one node.
    /// </summary>
    [Test]
    public async Task Hash_SameTextDifferentNode_HashesApart()
    {
        CheckedExpression expression = Analyze("kills > 1");

        string underNodeA = ExpressionHasher.ComputeHashHex(expression,
            new FakeStatHashes().Map("kills", "ruleset-a/kills"));
        string underNodeB = ExpressionHasher.ComputeHashHex(expression,
            new FakeStatHashes().Map("kills", "ruleset-b/kills"));

        await Assert.That(underNodeA).IsNotEqualTo(underNodeB);
    }

    /// <summary>Stat references hash by node, not by name: bare and qualified spellings of the SAME node hash together.</summary>
    [Test]
    public async Task Hash_DifferentTextSameNode_HashesTogether()
    {
        FakeStatHashes sameNode = new FakeStatHashes()
            .Map("kills", "the-one-node")
            .Map("otherrs.kills", "the-one-node");

        await Assert.That(HashOf("kills > 1", sameNode)).IsEqualTo(HashOf("otherrs.kills > 1", sameNode));

        // ...and the stat's spelled name is nowhere in the preimage.
        string preimage = ExpressionHasher.BuildPreimage(Analyze("kills > 1"), sameNode);
        await Assert.That(preimage.Contains("kills", StringComparison.Ordinal)).IsFalse();
    }

    /// <summary>Non-stat references still hash by their resolved path text.</summary>
    [Test]
    public async Task Hash_NonStatReferences_ByPath()
    {
        string preimage = ExpressionHasher.BuildPreimage(Analyze("player.health > 1"), new FakeStatHashes());

        await Assert.That(preimage).Contains("(ref player.health)");
    }

    /// <summary>Pseudo-member tails stay in the preimage after the node hash (.count reads differ from raw reads).</summary>
    [Test]
    public async Task Hash_StatTailSegments_Distinguish()
    {
        FakeStatHashes statHashes = new();

        string preimage = ExpressionHasher.BuildPreimage(Analyze("weapons_seen.count > 0"), statHashes);
        await Assert.That(preimage).Contains(" count)");
        await Assert.That(HashOf("weapons_seen.count > 0", statHashes))
            .IsNotEqualTo(HashOf("weapons_seen.set", statHashes));
    }

    // ── Normalization ties in: §5 equivalences through real SHA-256 ──────────────

    /// <summary>The §5 hash-equal pairs hold through the full pipeline and real hashes.</summary>
    [Test]
    public async Task Hash_NormalizedSpellings_Equal()
    {
        await Assert.That(HashOf("kills>1")).IsEqualTo(HashOf("kills > 1"));
        await Assert.That(HashOf("kills > 1 && player.health > 0"))
            .IsEqualTo(HashOf("kills > 1 and player.health > 0"));
        await Assert.That(HashOf("reaction > 5s")).IsEqualTo(HashOf("reaction > 320"));
        await Assert.That(HashOf("reaction > 0.5s")).IsEqualTo(HashOf("reaction > 500ms"));
    }

    /// <summary>The §5 hash-distinct pairs stay distinct: no constant folding, structure is identity.</summary>
    [Test]
    public async Task Hash_DistinctStructures_Distinct()
    {
        await Assert.That(HashOf("kills + (2 * 3)")).IsNotEqualTo(HashOf("(kills + 2) * 3"));
        await Assert.That(HashOf("kills > 1 + 2")).IsNotEqualTo(HashOf("kills > 3"));
        await Assert.That(HashOf("kills > 1")).IsNotEqualTo(HashOf("player.health > 1"));
    }

    // ── Node preimage rows ───────────────────────────────────────────────────────

    /// <summary>Row 4 is order-insensitive: the concrete event set sorts before hashing.</summary>
    [Test]
    public async Task Hash_EventSet_SortedAndDeduped()
    {
        FakeStatHashes statHashes = new();

        string ab = RuleHasher.ComputeHashHex(Counter("c", ["a_event", "b_event"]), statHashes);
        string ba = RuleHasher.ComputeHashHex(Counter("c", ["b_event", "a_event"]), statHashes);
        string abDup = RuleHasher.ComputeHashHex(Counter("c", ["b_event", "a_event", "b_event"]), statHashes);
        string aOnly = RuleHasher.ComputeHashHex(Counter("c", ["a_event"]), statHashes);

        await Assert.That(ab).IsEqualTo(ba);
        await Assert.That(ab).IsEqualTo(abDup);
        await Assert.That(ab).IsNotEqualTo(aOnly);
    }

    /// <summary>Rows 1–3 split hashes: kind, value type, and scope axis are all identity.</summary>
    [Test]
    public async Task Hash_KindTypeAxis_Split()
    {
        FakeStatHashes statHashes = new();
        RuleNodeDescriptor count = Counter("x", ["player_death"]);

        await Assert.That(RuleHasher.ComputeHashHex(count, statHashes))
            .IsNotEqualTo(RuleHasher.ComputeHashHex(count with
            {
                Kind = RuleNodeKind.Sum
            }, statHashes));
        await Assert.That(RuleHasher.ComputeHashHex(count, statHashes))
            .IsNotEqualTo(RuleHasher.ComputeHashHex(count with
            {
                ValueType = RulesType.Float
            }, statHashes));
        await Assert.That(RuleHasher.ComputeHashHex(count, statHashes))
            .IsNotEqualTo(RuleHasher.ComputeHashHex(count with
            {
                Per = ScopeAxis.Match
            }, statHashes));
    }

    /// <summary>Row 7: gating (and the gate's identity) is part of the node hash.</summary>
    [Test]
    public async Task Hash_GateHash_Splits()
    {
        FakeStatHashes statHashes = new();
        RuleNodeDescriptor ungated = Counter("x", ["player_death"]);
        RuleNodeDescriptor gatedA = ungated with
        {
            GateHash = SHA256.HashData("gate-a"u8.ToArray())
        };
        RuleNodeDescriptor gatedB = ungated with
        {
            GateHash = SHA256.HashData("gate-b"u8.ToArray())
        };

        await Assert.That(RuleHasher.ComputeHashHex(ungated, statHashes))
            .IsNotEqualTo(RuleHasher.ComputeHashHex(gatedA, statHashes));
        await Assert.That(RuleHasher.ComputeHashHex(gatedA, statHashes))
            .IsNotEqualTo(RuleHasher.ComputeHashHex(gatedB, statHashes));
    }

    /// <summary>Row 8: keep-spec (and bucket parts) split hashes.</summary>
    [Test]
    public async Task Hash_KeepSpec_Splits()
    {
        FakeStatHashes statHashes = new();
        RuleNodeDescriptor first = new("cap", RuleNodeKind.Capture, RulesType.Int, ScopeAxis.Round,
            ["player_death"], Keep: KeepKind.First);

        IReadOnlyList<string> weaponKeyParts = ["weapon"];
        await Assert.That(RuleHasher.ComputeHashHex(first, statHashes))
            .IsNotEqualTo(RuleHasher.ComputeHashHex(first with
            {
                Keep = KeepKind.List
            }, statHashes));
        await Assert.That(RuleHasher.ComputeHashHex(first, statHashes))
            .IsNotEqualTo(RuleHasher.ComputeHashHex(
                first with
                {
                    BucketKeyParts = weaponKeyParts
                }, statHashes));
    }

    /// <summary>
    ///     Row 5 + row 8 (C8 single-value SUM bucket): a summing bucket's <c>value:</c> selector is
    ///     part of identity. A count bucket and a sum bucket over the same trigger + key hash apart
    ///     (the value slot + reducer join the preimage), and two sum buckets summing DIFFERENT amounts
    ///     hash apart — collapsing them onto one node would be silent corruption (the Min-only tally
    ///     bug's bucket analogue). The value selector must be hashed unconditionally, not gated to sum:.
    /// </summary>
    [Test]
    public async Task Hash_BucketValueSelector_Splits()
    {
        FakeStatHashes statHashes = new();
        IReadOnlyList<string> weaponKeyParts = ["event.weapon"];
        RuleNodeDescriptor countBucket = new("dmg", RuleNodeKind.Bucket, RulesType.Int, ScopeAxis.PlayerMatch,
            ["player_hurt"], Analyze("event.weapon == \"ak47\""), BucketKeyParts: weaponKeyParts);
        RuleNodeDescriptor sumHealth = countBucket with
        {
            ValueSelector = Analyze("player.health"),
            BucketReducer = "sum"
        };
        RuleNodeDescriptor sumKills = countBucket with
        {
            ValueSelector = Analyze("kills"),
            BucketReducer = "sum"
        };

        await Assert.That(RuleHasher.ComputeHashHex(countBucket, statHashes))
            .IsNotEqualTo(RuleHasher.ComputeHashHex(sumHealth, statHashes))
            .Because("a count bucket and a sum bucket over the same trigger + key must hash apart");
        await Assert.That(RuleHasher.ComputeHashHex(sumHealth, statHashes))
            .IsNotEqualTo(RuleHasher.ComputeHashHex(sumKills, statHashes))
            .Because("two sum buckets summing different amounts must NOT dedup (false-dedup = corruption)");
    }

    /// <summary>
    ///     Row 8 (C8 composite bucket keys): a composite key-part list is ordered — <c>[a, b]</c> and
    ///     <c>[b, a]</c> select different tuples and must hash apart (order is identity-bearing). A
    ///     two-part key must also differ from either of its single parts alone.
    /// </summary>
    [Test]
    public async Task Hash_CompositeBucketKey_OrderBearing()
    {
        FakeStatHashes statHashes = new();
        RuleNodeDescriptor baseBucket = new("byWeaponHs", RuleNodeKind.Bucket, RulesType.Int,
            ScopeAxis.PlayerMatch, ["player_death"]);

        RuleNodeDescriptor ab = baseBucket with
        {
            BucketKeyParts = ["event.Weapon", "event.Headshot"]
        };
        RuleNodeDescriptor ba = baseBucket with
        {
            BucketKeyParts = ["event.Headshot", "event.Weapon"]
        };
        RuleNodeDescriptor a = baseBucket with
        {
            BucketKeyParts = ["event.Weapon"]
        };

        await Assert.That(RuleHasher.ComputeHashHex(ab, statHashes))
            .IsNotEqualTo(RuleHasher.ComputeHashHex(ba, statHashes))
            .Because("[a, b] and [b, a] are different tuples — order must be identity-bearing");
        await Assert.That(RuleHasher.ComputeHashHex(ab, statHashes))
            .IsNotEqualTo(RuleHasher.ComputeHashHex(a, statHashes))
            .Because("a two-part composite key must not dedup with either part alone");
    }

    /// <summary>
    ///     Row 8 (C8 named reducers): the reducer name is identity-bearing on its own. Two buckets with
    ///     the same key AND the same value selector but a different reducer (<c>max</c> vs <c>sum</c>)
    ///     must hash apart — collapsing a max onto a sum would be silent corruption.
    /// </summary>
    [Test]
    public async Task Hash_BucketReducer_Splits()
    {
        FakeStatHashes statHashes = new();
        RuleNodeDescriptor sumBucket = new("hp", RuleNodeKind.Bucket, RulesType.Int, ScopeAxis.PlayerMatch,
            ["player_hurt"], Analyze("event.weapon == \"ak47\""), Analyze("player.health"),
            BucketKeyParts: ["event.weapon"], BucketReducer: "sum");
        RuleNodeDescriptor maxBucket = sumBucket with
        {
            BucketReducer = "max"
        };

        await Assert.That(RuleHasher.ComputeHashHex(sumBucket, statHashes))
            .IsNotEqualTo(RuleHasher.ComputeHashHex(maxBucket, statHashes))
            .Because("a sum bucket and a max bucket over the same key + value must NOT dedup");
    }

    // ── Row 9: the id-salt for input-less stats ──────────────────────────────────

    /// <summary>Two input-less stats stay distinct through their own ids (v1's id-salt, retained).</summary>
    [Test]
    public async Task Hash_InputlessStats_SaltedById()
    {
        FakeStatHashes statHashes = new();
        RuleNodeDescriptor a = new("counter_a", RuleNodeKind.Count, RulesType.Int, ScopeAxis.Round, []);
        RuleNodeDescriptor b = new("counter_b", RuleNodeKind.Count, RulesType.Int, ScopeAxis.Round, []);

        await Assert.That(RuleHasher.ComputeHashHex(a, statHashes))
            .IsNotEqualTo(RuleHasher.ComputeHashHex(b, statHashes));
        await Assert.That(RuleHasher.ComputeHashHex(a, statHashes))
            .IsEqualTo(RuleHasher.ComputeHashHex(a with
            {
            }, statHashes));
    }

    /// <summary>With inputs present the id leaves the preimage: same-shaped nodes dedup across different ids.</summary>
    [Test]
    public async Task Hash_WithInputs_IdIsNotIdentity()
    {
        FakeStatHashes statHashes = new();

        await Assert.That(RuleHasher.ComputeHashHex(Counter("kills_a", ["player_death"]), statHashes))
            .IsEqualTo(RuleHasher.ComputeHashHex(Counter("kills_b", ["player_death"]), statHashes));

        // The v1 regression stays pinned: same id, different formula → different hashes.
        await Assert.That(RuleHasher.ComputeHashHex(
                Counter("score", ["player_death"], "kills + 1"), statHashes))
            .IsNotEqualTo(RuleHasher.ComputeHashHex(
                Counter("score", ["player_death"], "kills + 2"), statHashes));
    }

    /// <summary>
    ///     Deterministic fake: a stat's node hash is SHA-256 of an assigned tag (default: the
    ///     stat path itself). Mapping two paths to one tag models two spellings of the SAME
    ///     node; remapping one path models the same text resolving to a DIFFERENT node.
    /// </summary>
    private sealed class FakeStatHashes : IStatHashSource
    {
        private readonly Dictionary<string, string> _tags = new(StringComparer.Ordinal);

        public ReadOnlyMemory<byte> GetStatHash(ResolvedReference reference)
        {
            string statPath = reference.StatPath ?? throw new InvalidOperationException("not a stat reference");
            string tag = _tags.GetValueOrDefault(statPath, statPath);
            return SHA256.HashData(Encoding.UTF8.GetBytes($"fake-node:{tag}"));
        }

        public FakeStatHashes Map(string statPath, string nodeTag)
        {
            _tags[statPath] = nodeTag;
            return this;
        }
    }
}
