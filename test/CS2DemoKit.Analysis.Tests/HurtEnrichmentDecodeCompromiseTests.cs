#region

using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Analysis.Building;
using CS2DemoKit.Analysis.Edges;
using CS2DemoKit.Analysis.Nodes;
using CS2DemoKit.Analysis.Plugins;
using CS2DemoKit.Parser;
using CS2DemoKit.Parser.GameEvents;
using CS2OpenSchema.Events;

#endregion

namespace CS2DemoKit.Analysis.Tests;

/// <summary>
///     Unit pins for the decode-integrity hardening that landed with the EnemyDmg-overcount
///     investigation (the overcount fix itself is the same-frame guard pinned by
///     <c>HurtEnrichmentSameFrameBurstTests</c>):
///     once entity decode is compromised (<see cref="EntityFrameDigest.DecodeCompromised" /> — the
///     producing tracker recorded a decode error, the bit-misalignment shape), the scanner's
///     pre-frame per-pawn snapshot FREEZES for the rest of the run, so
///     <see cref="HurtTeamEnrichmentEdge" />'s damage cap falls back to the event-tracked
///     <see cref="PlayerContextIndex" /> health instead of consuming silently-stale entity HP. The
///     parallel digest path would otherwise keep re-priming fresh trackers at
///     DEM_FullPacket checkpoints, periodically refreshing the snapshot with values that then freeze
///     mid-chunk. All current bench demos decode cleanly, so these pins guard future decode-broken
///     demos. Pure in-memory — no demo file.
/// </summary>
[Category("Unit")]
public class HurtEnrichmentDecodeCompromiseTests
{
    // ── Digest fixtures ───────────────────────────────────────────────────────

    /// <summary>One per-player provider (health) — index 0 in every digest's value array.</summary>
    private static EntityFrameDigest Digest(int? hpForSlot3, bool compromised = false)
    {
        EntityFrameDigest d = new() { DecodeCompromised = compromised };
        if (hpForSlot3 is not null)
        {
            d.PerPawn.Add((3, new object?[] { hpForSlot3.Value }));
        }

        return d;
    }

    /// <summary>
    ///     Scanner over an empty frame list (the precomputed-digest path never drives the layer),
    ///     with one registered health provider and the given hand-built digest stream.
    /// </summary>
    private static (EntityChangeScanner Scanner, PawnHealthProvider Health) BuildScanner(
        params EntityFrameDigest[] digests)
    {
        PawnHealthProvider health = new();
        EntityChangeScanner scanner = new(
            new EntityStateLayer([]),
            providers: [],
            perPlayerProviders: [health]);
        scanner.SetPrecomputedDigests(digests);
        return (scanner, health);
    }

    // ── Scanner-level pins ────────────────────────────────────────────────────

    /// <summary>
    ///     Clean digest stream: the pre-frame snapshot keeps updating every frame (the
    ///     entity path is NOT disabled on healthy demos).
    /// </summary>
    [Test]
    public async Task CleanStream_SnapshotKeepsUpdating()
    {
        (EntityChangeScanner scanner, PawnHealthProvider health) = BuildScanner(
            Digest(100), Digest(64), Digest(37), Digest(null));

        scanner.AdvanceAndPollAt(0, 10);
        await Assert.That(scanner.GetPreFrameValue(health, 3)).IsNull()
            .Because("frame 0 has no previous digest to fold");

        scanner.AdvanceAndPollAt(1, 20);
        await Assert.That(scanner.GetPreFrameValue(health, 3)).IsEqualTo(100);

        scanner.AdvanceAndPollAt(2, 30);
        await Assert.That(scanner.GetPreFrameValue(health, 3)).IsEqualTo(64);

        scanner.AdvanceAndPollAt(3, 40);
        await Assert.That(scanner.GetPreFrameValue(health, 3)).IsEqualTo(37);
    }

    /// <summary>
    ///     The first compromised digest freezes the snapshot: neither its own per-pawn values nor
    ///     any later digest's — even a clean one from a re-primed parallel chunk — are folded.
    ///     This is the overcount mechanism: chunk workers re-prime at DEM_FullPacket checkpoints, so on a
    ///     decode-broken demo the stream is (clean… compromised… clean… compromised…) and every
    ///     "clean again" burst carries checkpoint state that goes stale within its chunk.
    /// </summary>
    [Test]
    public async Task CompromisedDigest_FreezesSnapshot_Stickily()
    {
        (EntityChangeScanner scanner, PawnHealthProvider health) = BuildScanner(
            Digest(64),
            Digest(100, compromised: true), // stale refresh from a broken tracker
            Digest(90), // next chunk's re-primed tracker reports clean again
            Digest(80));

        scanner.AdvanceAndPollAt(0, 10);
        scanner.AdvanceAndPollAt(1, 20); // folds digest 0 (clean)
        await Assert.That(scanner.GetPreFrameValue(health, 3)).IsEqualTo(64);

        scanner.AdvanceAndPollAt(2, 30); // digest 1 is compromised → freeze, nothing folded
        await Assert.That(scanner.GetPreFrameValue(health, 3)).IsEqualTo(64)
            .Because("a compromised digest's per-pawn values must never reach the snapshot");

        scanner.AdvanceAndPollAt(3, 40); // digest 2 was clean — but the freeze is sticky
        await Assert.That(scanner.GetPreFrameValue(health, 3)).IsEqualTo(64)
            .Because("the freeze is sticky: post-error 'clean' digests come from re-primed chunk "
                     + "trackers whose values go stale within the chunk");
    }

    /// <summary>
    ///     A demo whose decode is compromised before any pawn was captured (the real bit-misalignment
    ///     shape — error at packet ~37, during signon): the snapshot stays EMPTY, so entity-path consumers
    ///     get null and fall back to event-tracked state. This is the May-2026 verified behaviour.
    /// </summary>
    [Test]
    public async Task CompromisedBeforeFirstCapture_SnapshotStaysEmpty()
    {
        (EntityChangeScanner scanner, PawnHealthProvider health) = BuildScanner(
            Digest(null),
            Digest(100, compromised: true),
            Digest(100));

        scanner.AdvanceAndPollAt(0, 10);
        scanner.AdvanceAndPollAt(1, 20);
        scanner.AdvanceAndPollAt(2, 30);
        await Assert.That(scanner.GetPreFrameValue(health, 3)).IsNull();
    }

    // ── Edge-level pins: the damage cap ───────────────────────────────────────

    private sealed record EdgeFixture(
        HurtTeamEnrichmentEdge Edge,
        PlayerContextIndex Players,
        TransientValueNode<int> CappedDamage,
        TransientValueNode<int> VictimHealthBefore);

    /// <summary>Victim slot 3 (T), attacker slot 5 (CT); wires the given scanner + health provider in.</summary>
    private static EdgeFixture BuildEdge(EntityChangeScanner? scanner, PawnHealthProvider? health)
    {
        PlayerContextIndex players = new();
        players.Register(3, new PlayerContextIndex.PlayerContext(3, 2));
        players.Register(5, new PlayerContextIndex.PlayerContext(5, 3));

        TransientBoolNode enemy = new("enrich.hurt.was_enemy_damage");
        TransientBoolNode team = new("enrich.hurt.was_team_damage");
        TransientBoolNode self = new("enrich.hurt.was_self_damage");
        TransientValueNode<int> healthBefore = new("enrich.hurt.victim_health_before", 0);
        TransientValueNode<int> capped = new("enrich.hurt.capped_damage", 0);
        TransientValueNode<string> weapon = new("enrich.hurt.attacker_active_weapon", "");

        GenericBoolNode root = new("root");
        HurtTeamEnrichmentEdge edge = new(
            root, players, enemy, team, self, healthBefore, capped, weapon,
            scanner, health);
        return new EdgeFixture(edge, players, capped, healthBefore);
    }

    private static bool ApplyHurt(EdgeFixture f, int victim, int attacker, int dmgHealth, int healthAfter,
        int frameNumber = 0)
    {
        GameEvent hurt = new("player_hurt", -1, frameNumber, 0, 0, new PlayerHurtEvent
        {
            UserId = victim,
            UserIdPawn = 0,
            Attacker = attacker,
            AttackerPawn = 0,
            Health = (byte)healthAfter,
            Armor = 0,
            DmgHealth = (short)dmgHealth,
            DmgArmor = 0,
            Weapon = "awp",
            HitGroup = 0
        });
        GameEventMessage msg = GameEventMessage.ForSynthesizedEvent(hurt);
        DemoFrame frame = new()
        {
            Command = "DEM_Packet",
            FrameNumber = frameNumber,
            ServerTick = 0,
            RawStart = 0,
            RawLength = 1,
            HeaderLength = 1,
            IsCompressed = false,
            MessageList = [msg]
        };
        return f.Edge.TryApply(new EvaluationContext(msg, frame));
    }

    /// <summary>
    ///     Decode-broken demo: victim already whittled to 37 hp (event-tracked), while the
    ///     frozen-broken entity stream still claims 100. The lethal 108-damage hit — in a LATER
    ///     frame, so the same-frame guard is not what decides — must cap at 37 (event cache), not
    ///     100 (stale entity HP), because the compromised snapshot froze and returns null.
    /// </summary>
    [Test]
    public async Task LethalHit_OnCompromisedDemo_CapsWithEventTrackedHealth()
    {
        (EntityChangeScanner scanner, PawnHealthProvider health) = BuildScanner(
            Digest(null),
            Digest(100, compromised: true), // broken tracker still reports full HP
            Digest(100),
            Digest(100));
        scanner.AdvanceAndPollAt(0, 10);
        scanner.AdvanceAndPollAt(1, 20);
        scanner.AdvanceAndPollAt(2, 30);

        EdgeFixture f = BuildEdge(scanner, health);

        // First hit, frame 10: 100 → 37 (non-lethal, uncapped by definition: DmgHealth = lost HP).
        await Assert.That(ApplyHurt(f, victim: 3, attacker: 5, dmgHealth: 63, healthAfter: 37, frameNumber: 10)).IsTrue();
        await Assert.That(f.CappedDamage.Value).IsEqualTo(63);

        // Lethal overkill hit in a DIFFERENT frame: awp for 108 into a 37-hp victim.
        await Assert.That(ApplyHurt(f, victim: 3, attacker: 5, dmgHealth: 108, healthAfter: 0, frameNumber: 11)).IsTrue();
        await Assert.That(f.VictimHealthBefore.Value).IsEqualTo(37)
            .Because("the frozen snapshot returns null, so pre-hit HP comes from the event cache");
        await Assert.That(f.CappedDamage.Value).IsEqualTo(37)
            .Because("capped damage = min(108, 37); a silently-stale entity 100 would overcount");
    }

    /// <summary>
    ///     Control: on a clean demo the entity snapshot still OVERRIDES the event cache (the
    ///     point of the entity path — it sees non-damage HP changes the event path misses). The fix
    ///     must not disable the entity path where decode is trustworthy.
    /// </summary>
    [Test]
    public async Task LethalHit_OnCleanDemo_EntitySnapshotStillOverrides()
    {
        (EntityChangeScanner scanner, PawnHealthProvider health) = BuildScanner(
            Digest(64), Digest(64), Digest(64));
        scanner.AdvanceAndPollAt(0, 10);
        scanner.AdvanceAndPollAt(1, 20); // snapshot: slot 3 → 64

        EdgeFixture f = BuildEdge(scanner, health);
        f.Players.SetHealth(3, 37); // event cache disagrees (e.g. missed a medshot)

        await Assert.That(ApplyHurt(f, victim: 3, attacker: 5, dmgHealth: 108, healthAfter: 0)).IsTrue();
        await Assert.That(f.VictimHealthBefore.Value).IsEqualTo(64)
            .Because("clean entity state wins over the event cache — the entity-override semantics are preserved");
        await Assert.That(f.CappedDamage.Value).IsEqualTo(64);
    }
}
