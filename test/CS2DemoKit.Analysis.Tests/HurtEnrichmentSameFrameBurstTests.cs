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
///     Unit pins for the EnemyDmg +2..+66 overcount fix in
///     <see cref="HurtTeamEnrichmentEdge" />: the pre-frame entity snapshot is FRAME-START health,
///     so within a GOTV frame that coalesces a multi-hit burst, every hit after the victim's first
///     must take its pre-hit HP from the event-tracked cache (which holds the server-reported
///     post-hit health of the previous same-frame hit), not from the snapshot.
///     <para>
///         Root cause chain: the May-2026 goldens were generated while a bitstream
///         misalignment kept entity decode broken on all 5 MM bench demos — the entity HP override
///         never engaged and the event cache did all the capping (golden-verified 9/9). The
///         2026-06-08 AnimGraph2 + instancebaseline decode fixes cured the
///         misalignment, the override came alive, and burst-ending kills started capping at
///         pre-burst frame-start HP. Measured on the nuke bench demo: 4 events, all with a
///         same-frame prior hit on the victim, accounting for jeremyskills +60 and
///         I LOVE TANKS +7 exactly.
///     </para>
///     Pure in-memory — no demo file.
/// </summary>
[Category("Unit")]
public class HurtEnrichmentSameFrameBurstTests
{
    private static EntityFrameDigest Digest(int slot, int hp)
    {
        EntityFrameDigest d = new();
        d.PerPawn.Add((slot, new object?[] { hp }));
        return d;
    }

    /// <summary>Scanner whose pre-frame snapshot reports the given frame-start HP for the slot.</summary>
    private static (EntityChangeScanner Scanner, PawnHealthProvider Health) SnapshotWith(int slot, int hp)
    {
        PawnHealthProvider health = new();
        EntityChangeScanner scanner = new(
            new EntityStateLayer([]),
            providers: [],
            perPlayerProviders: [health]);
        scanner.SetPrecomputedDigests([Digest(slot, hp), Digest(slot, hp), Digest(slot, hp)]);
        scanner.AdvanceAndPollAt(0, 10);
        scanner.AdvanceAndPollAt(1, 20); // folds digest 0 → snapshot: slot → hp
        return (scanner, health);
    }

    private sealed record Fixture(
        HurtTeamEnrichmentEdge Edge,
        PlayerContextIndex Players,
        TransientValueNode<int> CappedDamage,
        TransientValueNode<int> VictimHealthBefore);

    private static Fixture Build(EntityChangeScanner scanner, PawnHealthProvider health)
    {
        PlayerContextIndex players = new();
        players.Register(9, new PlayerContextIndex.PlayerContext(9, 2));
        players.Register(3, new PlayerContextIndex.PlayerContext(3, 3));

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
        return new Fixture(edge, players, capped, healthBefore);
    }

    private static bool ApplyHurt(Fixture f, int victim, int attacker, int dmgHealth, int healthAfter,
        int frameNumber)
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
            Weapon = "ak47",
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
    ///     The measured nuke-demo shape (frame 9800, jeremyskills → slot 9): frame-start HP 66,
    ///     burst of 40 then a lethal 80 in the SAME frame. The lethal hit's pre-hit HP is 26 (the
    ///     event cache, from the first hit's server-reported health), so capped = 26 — not
    ///     min(80, 66) = 66, which was the regression's +40 on this one event.
    /// </summary>
    [Test]
    public async Task BurstLethalHit_SameFrame_CapsWithEventTrackedHealth()
    {
        (EntityChangeScanner scanner, PawnHealthProvider health) = SnapshotWith(slot: 9, hp: 66);
        Fixture f = Build(scanner, health);

        // Hit 1 (same frame 9800): 66 → 26. First hurt of the frame — entity override engages
        // and agrees with the event cache anyway (both 66).
        await Assert.That(ApplyHurt(f, victim: 9, attacker: 3, dmgHealth: 40, healthAfter: 26, frameNumber: 9800)).IsTrue();
        await Assert.That(f.VictimHealthBefore.Value).IsEqualTo(66);
        await Assert.That(f.CappedDamage.Value).IsEqualTo(40);

        // Hit 2 (same frame 9800, lethal): dmg 80 into 26 hp.
        await Assert.That(ApplyHurt(f, victim: 9, attacker: 3, dmgHealth: 80, healthAfter: 0, frameNumber: 9800)).IsTrue();
        await Assert.That(f.VictimHealthBefore.Value).IsEqualTo(26)
            .Because("the victim was already hurt this frame — the frame-start snapshot (66) is stale; "
                     + "the event cache holds the true pre-hit HP");
        await Assert.That(f.CappedDamage.Value).IsEqualTo(26)
            .Because("capped = min(80, 26); capping at the frame-start 66 overcounted enemy_damage by +40");
    }

    /// <summary>
    ///     Sub-snapshot-HP burst finisher (nuke frame 10039 shape): even when the lethal hit's raw
    ///     damage (11) is BELOW the frame-start snapshot HP (14), the cap must still use the event
    ///     cache (3) — min(11, 3) = 3, not the raw 11 the regression counted (+8 on this event).
    /// </summary>
    [Test]
    public async Task BurstLethalHit_DamageBelowSnapshotHp_StillCapsWithEventCache()
    {
        (EntityChangeScanner scanner, PawnHealthProvider health) = SnapshotWith(slot: 9, hp: 14);
        Fixture f = Build(scanner, health);

        await Assert.That(ApplyHurt(f, victim: 9, attacker: 3, dmgHealth: 11, healthAfter: 3, frameNumber: 10039)).IsTrue();
        await Assert.That(ApplyHurt(f, victim: 9, attacker: 3, dmgHealth: 11, healthAfter: 0, frameNumber: 10039)).IsTrue();
        await Assert.That(f.CappedDamage.Value).IsEqualTo(3)
            .Because("min(11, eventCache 3) — the regression counted min(11, snapshot 14) = 11");
    }

    /// <summary>
    ///     Control: hits in DIFFERENT frames re-engage the entity override — the same-frame guard
    ///     must not disable the entity-snapshot path across frames (where the snapshot legitimately catches
    ///     non-damage HP changes the event cache misses).
    /// </summary>
    [Test]
    public async Task HitsInDifferentFrames_EntityOverrideReEngages()
    {
        (EntityChangeScanner scanner, PawnHealthProvider health) = SnapshotWith(slot: 9, hp: 66);
        Fixture f = Build(scanner, health);

        await Assert.That(ApplyHurt(f, victim: 9, attacker: 3, dmgHealth: 40, healthAfter: 26, frameNumber: 100)).IsTrue();

        // Next frame: event cache says 26, but the (test-pinned) snapshot claims 66 — e.g. a heal
        // the event path missed. Different frame → snapshot wins again.
        await Assert.That(ApplyHurt(f, victim: 9, attacker: 3, dmgHealth: 80, healthAfter: 0, frameNumber: 101)).IsTrue();
        await Assert.That(f.VictimHealthBefore.Value).IsEqualTo(66)
            .Because("the same-frame guard is scoped to one frame; cross-frame the entity value is authoritative");
        await Assert.That(f.CappedDamage.Value).IsEqualTo(66);
    }

    /// <summary>
    ///     Independence: a same-frame hurt on a DIFFERENT victim must not suppress the entity
    ///     override for this victim's first hurt of the frame.
    /// </summary>
    [Test]
    public async Task OtherVictimHurtSameFrame_DoesNotSuppressOverride()
    {
        PawnHealthProvider health = new();
        EntityChangeScanner scanner = new(
            new EntityStateLayer([]),
            providers: [],
            perPlayerProviders: [health]);
        EntityFrameDigest d = new();
        d.PerPawn.Add((9, new object?[] { 66 }));
        d.PerPawn.Add((3, new object?[] { 80 }));
        scanner.SetPrecomputedDigests([d, d, d]);
        scanner.AdvanceAndPollAt(0, 10);
        scanner.AdvanceAndPollAt(1, 20);

        Fixture f = Build(scanner, health);

        // Slot 3 hurt first in frame 500; then slot 9's first hurt of the same frame.
        await Assert.That(ApplyHurt(f, victim: 3, attacker: 9, dmgHealth: 10, healthAfter: 70, frameNumber: 500)).IsTrue();
        await Assert.That(ApplyHurt(f, victim: 9, attacker: 3, dmgHealth: 100, healthAfter: 0, frameNumber: 500)).IsTrue();
        await Assert.That(f.VictimHealthBefore.Value).IsEqualTo(66)
            .Because("the guard is per-victim: slot 9's first hurt of the frame still reads the snapshot");
        await Assert.That(f.CappedDamage.Value).IsEqualTo(66);
    }
}
