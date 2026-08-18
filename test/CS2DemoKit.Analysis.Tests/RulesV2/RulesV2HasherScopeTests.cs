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
///     Corruption-class distinctness pins for the spec §6 preimage
///     widening: the compound <c>(For × Per)</c> scope axis
///     (row 3), the <c>tally</c>/<c>streak</c> hashable kinds with their row-8 kind-args, and
///     the <c>sum:</c>/<c>capture:</c> value-selector row-5 packing. Each distinctness pin
///     guards a false-dedup that a narrower preimage would collapse. Also the <c>(ref this)</c>
///     representability confirmation and a verbatim preimage-text snapshot (freeze artifact 3
///     for the new rows). Pure in-memory; no demo.
/// </summary>
/// <remarks>
///     <c>off:</c> is intentionally absent: spec §6's normative row table defines no slot for
///     an <c>off:</c> deactivation twin and no descriptor mechanism was ever specified
///     (only later prose asserts it "contributes"). So no field is invented for it here.
/// </remarks>
[Category("Unit")]
public class RulesV2HasherScopeTests
{
    // ── Fixture ──────────────────────────────────────────────────────────────────

    private static ScopeEnvironment Env() =>
        new("where:",
        [
            ScopeSymbol.Namespace("event",
                ScopeSymbol.Value("tick", RulesType.Instant)),
            ScopeSymbol.Namespace("player",
                ScopeSymbol.Value("health", RulesType.Int),
                ScopeSymbol.Value("armor", RulesType.Int)),
            // `this` — a non-stat Value symbol typed as the enclosing stat's value type
            // (spec §4 / plan decision 7). It resolves as a plain reference, so the hasher
            // emits the fixed marker (ref this), never a row-6 hash embedding.
            ScopeSymbol.Value("this", RulesType.Int)
        ]);

    private static CheckedExpression Analyze(string source) =>
        ExpressionPipeline.Analyze(source, Env()).Require();

    private static string Hash(RuleNodeDescriptor node) =>
        RuleHasher.ComputeHashHex(node, new FakeStatHashes());

    // ── Row 3: compound (For × Per) scope axis (plan decision 5) ─────────────────

    /// <summary>
    ///     THE compound-axis pin: a per-player <c>per: round</c> stat and its <c>per: match</c>
    ///     twin — identical in every other row — must hash apart. A collapsed single per-player
    ///     axis would false-dedup them (they differ only in reset scope).
    /// </summary>
    [Test]
    public async Task Axis_PerPlayerRound_vs_PerPlayerMatch_HashApart()
    {
        RuleNodeDescriptor perMatch = new("kills", RuleNodeKind.Count, RulesType.Int,
            ScopeAxis.PlayerMatch, ["player_death"]);
        RuleNodeDescriptor perRound = perMatch with
        {
            Per = ScopeAxis.PlayerRound
        };

        await Assert.That(Hash(perMatch)).IsNotEqualTo(Hash(perRound));

        // …and each per-player axis stays distinct from its non-player counterpart.
        await Assert.That(Hash(perMatch)).IsNotEqualTo(Hash(perMatch with
        {
            Per = ScopeAxis.Match
        }));
        await Assert.That(Hash(perRound)).IsNotEqualTo(Hash(perRound with
        {
            Per = ScopeAxis.Round
        }));
    }

    // ── Row 5: value-selector packing ────────────────────────────────────────────

    /// <summary>
    ///     The value-selector pin: a <c>sum:</c>/<c>capture:</c> carries a trigger condition
    ///     (row 5) AND a value selector (appended row-5 slot). Two nodes sharing a trigger but
    ///     differing in the value selector must not dedup; presence of a value selector must
    ///     change the row; and a capture ≠ a sum with the identical trigger and value selector.
    /// </summary>
    [Test]
    public async Task ValueSelector_Distinguishes_SameTrigger()
    {
        CheckedExpression trigger = Analyze("player.health > 0");
        RuleNodeDescriptor sumArmor = new("dmg", RuleNodeKind.Sum, RulesType.Int,
            ScopeAxis.PlayerMatch, ["player_hurt"], trigger, Analyze("player.armor"));

        // Same kind, same trigger, DIFFERENT value selector → apart.
        RuleNodeDescriptor sumHealth = sumArmor with
        {
            ValueSelector = Analyze("player.health")
        };
        await Assert.That(Hash(sumArmor)).IsNotEqualTo(Hash(sumHealth));

        // A value selector present ≠ absent (the (cond … | value …) form vs a lone trigger).
        RuleNodeDescriptor sumNoValue = sumArmor with
        {
            ValueSelector = null
        };
        await Assert.That(Hash(sumArmor)).IsNotEqualTo(Hash(sumNoValue));

        // A capture and a sum with the IDENTICAL trigger and value selector still hash apart —
        // the kind (row 1) differs (Keep held equal to isolate the point).
        RuleNodeDescriptor captureArmor = sumArmor with
        {
            Kind = RuleNodeKind.Capture
        };
        await Assert.That(Hash(sumArmor)).IsNotEqualTo(Hash(captureArmor));
    }

    // ── Row 1 + row 8: tally kind + thresholds (plan decision 4) ─────────────────

    /// <summary>
    ///     The tally pins: a <c>tally</c> ≠ a <c>count</c> with the same trigger (kind row 1 —
    ///     mapping tally onto count would false-dedup them); two tallies differing only in a
    ///     threshold's min OR its target hash apart (row 8 <c>(min, target)</c> kind-args — the
    ///     target is the emit-node id each boundary writes to, behaviorally load-bearing); the
    ///     threshold set is order-insensitive.
    /// </summary>
    [Test]
    public async Task Tally_DistinctFromCount_AndThresholdSplits()
    {
        RuleNodeDescriptor count = new("multi", RuleNodeKind.Count, RulesType.Int,
            ScopeAxis.PlayerRound, ["player_death"]);
        RuleNodeDescriptor tally345 = count with
        {
            Kind = RuleNodeKind.Tally,
            TallyThresholds = [(3, "triple"), (4, "quad"), (5, "ace")]
        };
        RuleNodeDescriptor tally34 = tally345 with
        {
            TallyThresholds = [(3, "triple"), (4, "quad")]
        };

        // tally ≠ count with the same trigger (row 1 kind).
        await Assert.That(Hash(count)).IsNotEqualTo(Hash(tally345));

        // Differ in a threshold's MIN → apart.
        await Assert.That(Hash(tally345)).IsNotEqualTo(Hash(tally34));

        // Differ ONLY in a threshold's TARGET (same min set) → apart. Different targets write to
        // different counter nodes, so they must not dedup (v1's hasher hashes both).
        RuleNodeDescriptor tally345OtherTarget = tally345 with
        {
            TallyThresholds = [(3, "triple"), (4, "quad"), (5, "monster")]
        };
        await Assert.That(Hash(tally345)).IsNotEqualTo(Hash(tally345OtherTarget));

        // The (min, target) pair set is order-insensitive.
        await Assert.That(Hash(tally345 with
            {
                TallyThresholds = [(5, "ace"), (4, "quad"), (3, "triple")]
            }))
            .IsEqualTo(Hash(tally345));
    }

    // ── Row 1 + row 8: streak kind + window/min-streak (plan decision 4) ─────────

    /// <summary>The streak pins: window and min-streak each split; a streak ≠ a count with the same trigger.</summary>
    [Test]
    public async Task Streak_WindowAndMinStreak_Split()
    {
        RuleNodeDescriptor streak = new("spree", RuleNodeKind.Streak, RulesType.Int,
            ScopeAxis.PlayerRound, ["player_death"], StreakWindow: 640, StreakMinStreak: 2);

        await Assert.That(Hash(streak)).IsNotEqualTo(Hash(streak with
        {
            StreakWindow = 320
        }));
        await Assert.That(Hash(streak)).IsNotEqualTo(Hash(streak with
        {
            StreakMinStreak = 3
        }));

        RuleNodeDescriptor count = new("spree", RuleNodeKind.Count, RulesType.Int,
            ScopeAxis.PlayerRound, ["player_death"]);
        await Assert.That(Hash(streak)).IsNotEqualTo(Hash(count));
    }

    // ── (ref this) self-reference (plan decision 7) ──────────────────────────────

    /// <summary>
    ///     Confirms <c>(ref this)</c> is representable (it already is — a non-stat Value symbol
    ///     serializes to the fixed marker, never a row-6 hash embedding of a not-yet-computed
    ///     hash) and that two stats referencing <c>this</c> in structurally identical ASTs hash
    ///     together by their OTHER rows — the marker carries no node identity of its own. This
    ///     passes both before and after the change (a representability confirmation, not a
    ///     corruption pin).
    /// </summary>
    [Test]
    public async Task RefThis_SerializesAsMarker_AndCarriesNoNodeIdentity()
    {
        CheckedExpression thisPlusOne = Analyze("this + 1");

        string preimage = ExpressionHasher.BuildPreimage(thisPlusOne, new FakeStatHashes());
        await Assert.That(preimage).Contains("(ref this)");
        await Assert.That(preimage.Contains("(stat ", StringComparison.Ordinal)).IsFalse();

        // Two compute stats with structurally identical `this`-referencing ASTs, differing only
        // in id: inputs are present, so the id leaves the preimage and they dedup.
        RuleNodeDescriptor a = new("bonus_a", RuleNodeKind.Compute, RulesType.Int,
            ScopeAxis.PlayerMatch, [], thisPlusOne);
        RuleNodeDescriptor b = new("bonus_b", RuleNodeKind.Compute, RulesType.Int,
            ScopeAxis.PlayerMatch, [], Analyze("this + 1"));
        await Assert.That(Hash(a)).IsEqualTo(Hash(b));
    }

    // ── Freeze artifact: verbatim preimage snapshots for the new rows ────────────

    /// <summary>
    ///     Pins the exact preimage text for a fixed descriptor set exercising the widened rows:
    ///     compound axis (row 3 <c>player_match</c>/<c>player_round</c>), tally + streak kind
    ///     args (rows 1/8), the value-selector two-slot form (row 5), and the <c>(ref this)</c>
    ///     marker. Any diff here after the hash freeze is a breaking change and must be a
    ///     deliberate, commit-audited re-baseline. The length-framed <c>(cond … | value …)</c>
    ///     form is a deliberate framing choice (the spec's illustrative <c>…</c> left it open;
    ///     §6's normative wire-framing is length-prefixed, so both slots carry a UTF-8 byte
    ///     length to keep a user-authored string literal from masquerading as the delimiter).
    /// </summary>
    [Test]
    public async Task Preimage_NewRows_Pinned()
    {
        FakeStatHashes statHashes = new();

        string Pm(RuleNodeDescriptor node)
        {
            return RuleHasher.BuildPreimage(node, statHashes);
        }

        await Assert.That(Pm(new RuleNodeDescriptor("kills", RuleNodeKind.Count, RulesType.Int,
                ScopeAxis.PlayerMatch, ["player_death"])))
            .IsEqualTo("dvr2|1:5:count|2:3:int|3:12:player_match|4:12:player_death|5:0:|7:0:|8:0:|9:0:");

        await Assert.That(Pm(new RuleNodeDescriptor("kills", RuleNodeKind.Count, RulesType.Int,
                ScopeAxis.PlayerRound, ["player_death"])))
            .IsEqualTo("dvr2|1:5:count|2:3:int|3:12:player_round|4:12:player_death|5:0:|7:0:|8:0:|9:0:");

        await Assert.That(Pm(new RuleNodeDescriptor("multi", RuleNodeKind.Tally, RulesType.Int,
                ScopeAxis.PlayerRound, ["player_death"], TallyThresholds: [(3, "triple"), (4, "quad"), (5, "ace")])))
            .IsEqualTo("dvr2|1:5:tally|2:3:int|3:12:player_round|4:12:player_death|5:0:"
                       + "|7:0:|8:28:;tally=3:triple,4:quad,5:ace|9:0:");

        await Assert.That(Pm(new RuleNodeDescriptor("spree", RuleNodeKind.Streak, RulesType.Int,
                ScopeAxis.PlayerRound, ["player_death"], StreakWindow: 640, StreakMinStreak: 2)))
            .IsEqualTo("dvr2|1:6:streak|2:3:int|3:12:player_round|4:12:player_death|5:0:"
                       + "|7:0:|8:31:;streak.window=640;streak.min=2|9:0:");

        await Assert.That(Pm(new RuleNodeDescriptor("dmg", RuleNodeKind.Sum, RulesType.Int,
                ScopeAxis.PlayerMatch, ["player_hurt"],
                Analyze("player.health > 0"), Analyze("player.armor"))))
            .IsEqualTo("dvr2|1:3:sum|2:3:int|3:12:player_match|4:11:player_hurt"
                       + "|5:90:(cond 41:dv2-expr|(gt (ref player.health) (int 0)) | value 27:dv2-expr|(ref player.armor))"
                       + "|7:0:|8:0:|9:0:");

        await Assert.That(Pm(new RuleNodeDescriptor("bonus", RuleNodeKind.Compute, RulesType.Int,
                ScopeAxis.PlayerMatch, [], Analyze("this + 1"))))
            .IsEqualTo("dvr2|1:7:compute|2:3:int|3:12:player_match|4:0:"
                       + "|5:33:dv2-expr|(add (ref this) (int 1))|7:0:|8:0:|9:0:");
    }

    /// <summary>A trivial stat-hash source; none of these descriptors reference stats, so it is never invoked.</summary>
    private sealed class FakeStatHashes : IStatHashSource
    {
        public ReadOnlyMemory<byte> GetStatHash(ResolvedReference reference) =>
            SHA256.HashData(Encoding.UTF8.GetBytes($"fake-node:{reference.StatPath}"));
    }
}
