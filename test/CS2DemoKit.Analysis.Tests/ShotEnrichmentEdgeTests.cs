#region

using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Analysis.Building;
using CS2DemoKit.Analysis.Edges;
using CS2DemoKit.Analysis.Nodes;
using CS2DemoKit.Parser;
using CS2DemoKit.Parser.GameEvents;

#endregion

using CS2DemoKit.TestSupport;

namespace CS2DemoKit.Analysis.Tests;

/// <summary>
///     Unit pins for the Tier C aim-highlight enrichments: <see cref="ShotEnrichmentEdge" />'s
///     angle-delta math and spray-run tracker, and <see cref="SprayKillEnrichmentEdge" />'s
///     kill↔run correlation. Pure in-memory — no demo file. Each dispatch resets the transient
///     nodes first, mirroring StateGraphEvaluator's transient-reset loop, so every assertion
///     sees exactly what a rule's <c>where:</c> read would see on that event.
/// </summary>
[Category("Unit")]
public class ShotEnrichmentEdgeTests
{
    private sealed record Fixture(
        PlayerContextIndex Index,
        ShotEnrichmentEdge ShotEdge,
        SprayKillEnrichmentEdge KillEdge,
        TransientValueNode<double> TurnDegrees,
        TransientValueNode<int> TicksSinceLast,
        TransientValueNode<int> SprayShots,
        TransientValueNode<int> SprayVictims,
        TransientValueNode<int> SprayKills,
        TransientValueNode<int> SprayShotsAtKill);

    private static Fixture Build()
    {
        PlayerContextIndex index = new();
        index.Register(0, new PlayerContextIndex.PlayerContext(0, 2));
        index.Register(1, new PlayerContextIndex.PlayerContext(1, 2));
        index.Register(5, new PlayerContextIndex.PlayerContext(5, 3));
        index.Register(6, new PlayerContextIndex.PlayerContext(6, 3));

        TransientValueNode<double> turn = new("enrich.shot.turn_degrees");
        TransientValueNode<int> ticks = new(
            "enrich.shot.ticks_since_last_shot", ShotEnrichmentEdge.NoPreviousShotSentinel);
        TransientValueNode<int> sprayShots = new("enrich.shot.spray_shots");
        TransientValueNode<int> sprayVictims = new("enrich.shot.spray_victims");
        TransientValueNode<int> sprayKills = new("enrich.kill.spray_kills");
        TransientValueNode<int> sprayShotsAtKill = new("enrich.kill.spray_shots_at_kill");

        GenericBoolNode root = new("root");
        ShotEnrichmentEdge shotEdge = new(root, index, turn, ticks, sprayShots, sprayVictims);
        SprayKillEnrichmentEdge killEdge = new(root, index, sprayKills, sprayShotsAtKill);
        return new Fixture(index, shotEdge, killEdge, turn, ticks, sprayShots, sprayVictims,
            sprayKills, sprayShotsAtKill);
    }

    private static EvaluationContext Context(NetMessage msg) => new(msg, new DemoFrame
    {
        Command = "DEM_Packet",
        FrameNumber = 0,
        ServerTick = 0,
        RawStart = 0,
        RawLength = 1,
        HeaderLength = 1,
        IsCompressed = false,
        MessageList = [msg]
    });

    /// <summary>Dispatches one bullet_damage through the shot edge (with the evaluator's transient reset).</summary>
    private static bool Shot(Fixture f, int tick, int attacker, int victim,
        float pitch = 0f, float yaw = 0f, float recoil = 1f)
    {
        ((ITransientNode)f.TurnDegrees).Reset();
        ((ITransientNode)f.TicksSinceLast).Reset();
        ((ITransientNode)f.SprayShots).Reset();
        ((ITransientNode)f.SprayVictims).Reset();

        BulletDamageEvent shot = new()
        {
            // 4.1 pawn-handle companions; the shot edge keys on slots, so 0 (absent-key default).
            Victim = victim, VictimPawn = 0, Attacker = attacker, AttackerPawn = 0, Distance = 500f,
            DamageDirX = 0f, DamageDirY = 0f, DamageDirZ = 0f, NumPenetrations = 0,
            NoScope = false, InAir = false,
            ShootAngX = pitch, ShootAngY = yaw, ShootAngZ = 0f,
            AimPunchX = 0f, AimPunchY = 0f, AimPunchZ = 0f,
            AttackTickCount = tick, AttackTickFrac = 0f,
            RenderTickCount = tick, RenderTickFrac = 0f,
            InaccuracyTotal = 0f, InaccuracyMove = 0f, InaccuracyAir = 0f,
            RecoilIndex = recoil, Type = 0
        };
        GameEvent fire = new("bullet_damage", 0, 0, tick, tick, shot);
        GameEventMessage msg = GameEventMessage.ForSynthesizedEvent(fire);
        return f.ShotEdge.TryApplyDirect(shot, Context(msg));
    }

    /// <summary>Dispatches one player_death through the spray-kill edge (with the evaluator's transient reset).</summary>
    private static bool Kill(Fixture f, int tick, int killer, int victim)
    {
        ((ITransientNode)f.SprayKills).Reset();
        ((ITransientNode)f.SprayShotsAtKill).Reset();

        GameEvent death = TestGameEvents.PlayerDeath(victim, killer, 65535, "ak47", dmgHealth: (short)100, gameTick: tick);
        GameEventMessage msg = GameEventMessage.ForSynthesizedEvent(death);
        return f.KillEdge.TryApplyDirect(death.Payload!, Context(msg));
    }

    // ── Angle-delta math ─────────────────────────────────────────────────────

    [Test]
    public async Task AngleDelta_IdenticalAngles_IsZero()
    {
        await Assert.That(ShotEnrichmentEdge.AngleDeltaDegrees(10f, 45f, 10f, 45f))
            .IsEqualTo(0.0).Within(1e-6);
    }

    [Test]
    public async Task AngleDelta_PureYaw_IsTheYawDifference()
    {
        await Assert.That(ShotEnrichmentEdge.AngleDeltaDegrees(0f, 0f, 0f, 90f))
            .IsEqualTo(90.0).Within(1e-6);
    }

    [Test]
    public async Task AngleDelta_YawWraparound_TakesTheShortArc()
    {
        // 170° → −170° crosses the ±180 seam: the real turn is 20°, not 340°.
        await Assert.That(ShotEnrichmentEdge.AngleDeltaDegrees(0f, 170f, 0f, -170f))
            .IsEqualTo(20.0).Within(1e-6);
    }

    [Test]
    public async Task AngleDelta_PurePitch_IsThePitchDifference()
    {
        await Assert.That(ShotEnrichmentEdge.AngleDeltaDegrees(-30f, 0f, 15f, 0f))
            .IsEqualTo(45.0).Within(1e-6);
    }

    [Test]
    public async Task AngleDelta_AtHighPitch_YawDeltaShrinks()
    {
        // Near the poles a yaw swing moves the view direction much less than at the horizon —
        // the vector form must NOT report the raw 90° yaw difference.
        double delta = ShotEnrichmentEdge.AngleDeltaDegrees(80f, 0f, 80f, 90f);
        await Assert.That(delta).IsLessThan(30.0);
        await Assert.That(delta).IsGreaterThan(0.0);
    }

    // ── Flick anchoring (turn_degrees / ticks_since_last_shot) ───────────────

    [Test]
    public async Task FirstShot_HasNoAnchor_SentinelGapAndZeroTurn()
    {
        Fixture f = Build();
        bool applied = Shot(f, tick: 1000, attacker: 0, victim: 5, yaw: 10f);

        await Assert.That(applied).IsTrue();
        await Assert.That(f.TurnDegrees.Value).IsEqualTo(0.0);
        await Assert.That(f.TicksSinceLast.Value)
            .IsEqualTo(ShotEnrichmentEdge.NoPreviousShotSentinel);
        await Assert.That(f.SprayShots.Value).IsEqualTo(1);
        await Assert.That(f.SprayVictims.Value).IsEqualTo(1);
    }

    [Test]
    public async Task SecondShot_ReportsTurnAndGapAgainstPreviousShot()
    {
        Fixture f = Build();
        Shot(f, tick: 1000, attacker: 0, victim: 5, yaw: 0f);
        Shot(f, tick: 1010, attacker: 0, victim: 6, yaw: 75f, recoil: 2f);

        await Assert.That(f.TurnDegrees.Value).IsEqualTo(75.0).Within(1e-6);
        await Assert.That(f.TicksSinceLast.Value).IsEqualTo(10);
    }

    [Test]
    public async Task ShotAnchors_ArePerPlayer()
    {
        Fixture f = Build();
        Shot(f, tick: 1000, attacker: 0, victim: 5, yaw: 0f);
        // A DIFFERENT attacker's first shot must not see player 0's anchor.
        Shot(f, tick: 1010, attacker: 1, victim: 5, yaw: 90f);

        await Assert.That(f.TicksSinceLast.Value)
            .IsEqualTo(ShotEnrichmentEdge.NoPreviousShotSentinel);
        await Assert.That(f.TurnDegrees.Value).IsEqualTo(0.0);
    }

    [Test]
    public async Task RoundReset_ClearsTheAnchor()
    {
        Fixture f = Build();
        Shot(f, tick: 1000, attacker: 0, victim: 5, yaw: 0f);
        f.Index.ResetRoundState();
        Shot(f, tick: 1010, attacker: 0, victim: 5, yaw: 90f);

        await Assert.That(f.TicksSinceLast.Value)
            .IsEqualTo(ShotEnrichmentEdge.NoPreviousShotSentinel)
            .Because("a flick must never anchor on a previous round's shot");
    }

    [Test]
    public async Task UnknownAttackerSlot_DoesNotApply()
    {
        Fixture f = Build();
        await Assert.That(Shot(f, tick: 1000, attacker: 42, victim: 5)).IsFalse();
    }

    // ── Spray-run tracking (spray_shots / spray_victims) ─────────────────────

    [Test]
    public async Task SprayRun_CountsShotsAndDistinctVictims()
    {
        Fixture f = Build();
        Shot(f, tick: 1000, attacker: 0, victim: 5, recoil: 1f);
        Shot(f, tick: 1007, attacker: 0, victim: 5, recoil: 2f);
        Shot(f, tick: 1014, attacker: 0, victim: 6, recoil: 3f);
        Shot(f, tick: 1021, attacker: 0, victim: 6, recoil: 4f);

        await Assert.That(f.SprayShots.Value).IsEqualTo(4);
        await Assert.That(f.SprayVictims.Value).IsEqualTo(2)
            .Because("two distinct victims were damaged during the run");
    }

    [Test]
    public async Task SprayRun_BreaksOnLongGap()
    {
        Fixture f = Build();
        Shot(f, tick: 1000, attacker: 0, victim: 5, recoil: 1f);
        Shot(f, tick: 1007, attacker: 0, victim: 5, recoil: 2f);
        // 100 ticks later — a new engagement, even though recoil "rose".
        Shot(f, tick: 1107, attacker: 0, victim: 6, recoil: 3f);

        await Assert.That(f.SprayShots.Value).IsEqualTo(1);
        await Assert.That(f.SprayVictims.Value).IsEqualTo(1)
            .Because("the victim set restarts with the new run");
    }

    [Test]
    public async Task SprayRun_BreaksOnRecoilReset()
    {
        Fixture f = Build();
        Shot(f, tick: 1000, attacker: 0, victim: 5, recoil: 3f);
        Shot(f, tick: 1007, attacker: 0, victim: 5, recoil: 4f);
        // Re-tapped: recoil dropped back to ~1 despite the small gap.
        Shot(f, tick: 1020, attacker: 0, victim: 6, recoil: 1f);

        await Assert.That(f.SprayShots.Value).IsEqualTo(1);
        await Assert.That(f.SprayVictims.Value).IsEqualTo(1);
    }

    [Test]
    public async Task SameTickMultiVictim_ContinuesTheRun()
    {
        // A penetration collateral: two bullet_damage at the SAME tick, same recoil.
        Fixture f = Build();
        Shot(f, tick: 1000, attacker: 0, victim: 5, recoil: 2f);
        Shot(f, tick: 1000, attacker: 0, victim: 6, recoil: 2f);

        await Assert.That(f.SprayShots.Value).IsEqualTo(2);
        await Assert.That(f.SprayVictims.Value).IsEqualTo(2);
    }

    // ── Kill ↔ spray-run correlation (spray_kills / spray_shots_at_kill) ─────

    [Test]
    public async Task SprayTransfer_SecondKillInOneRun_CountsTwo()
    {
        Fixture f = Build();
        Shot(f, tick: 1000, attacker: 0, victim: 5, recoil: 1f);
        Shot(f, tick: 1007, attacker: 0, victim: 5, recoil: 2f);
        Kill(f, tick: 1007, killer: 0, victim: 5);
        await Assert.That(f.SprayKills.Value).IsEqualTo(1);

        Shot(f, tick: 1014, attacker: 0, victim: 6, recoil: 3f);
        Shot(f, tick: 1021, attacker: 0, victim: 6, recoil: 4f);
        bool applied = Kill(f, tick: 1021, killer: 0, victim: 6);

        await Assert.That(applied).IsTrue();
        await Assert.That(f.SprayKills.Value).IsEqualTo(2)
            .Because("both kills happened inside one uninterrupted spray run");
        await Assert.That(f.SprayShotsAtKill.Value).IsEqualTo(4);
    }

    [Test]
    public async Task KillAfterRunWentStale_DoesNotAttach()
    {
        Fixture f = Build();
        Shot(f, tick: 1000, attacker: 0, victim: 5, recoil: 2f);
        bool applied = Kill(f, tick: 1000 + SprayKillEnrichmentEdge.KillAttachMaxGapTicks + 1,
            killer: 0, victim: 5);

        await Assert.That(applied).IsFalse();
        await Assert.That(f.SprayKills.Value).IsEqualTo(0);
    }

    [Test]
    public async Task NewRun_ResetsTheKillCounter()
    {
        Fixture f = Build();
        Shot(f, tick: 1000, attacker: 0, victim: 5, recoil: 2f);
        Kill(f, tick: 1000, killer: 0, victim: 5);
        await Assert.That(f.SprayKills.Value).IsEqualTo(1);

        // Long gap → new run → a kill in it is that run's FIRST kill, not the second.
        Shot(f, tick: 2000, attacker: 0, victim: 6, recoil: 1f);
        Kill(f, tick: 2000, killer: 0, victim: 6);

        await Assert.That(f.SprayKills.Value).IsEqualTo(1);
    }

    [Test]
    public async Task Suicide_DoesNotAttachToAnyRun()
    {
        Fixture f = Build();
        Shot(f, tick: 1000, attacker: 0, victim: 5, recoil: 2f);
        bool applied = Kill(f, tick: 1000, killer: 0, victim: 0);

        await Assert.That(applied).IsFalse();
        await Assert.That(f.SprayKills.Value).IsEqualTo(0);
    }

    [Test]
    public async Task KillWithNoShotHistory_DoesNotApply()
    {
        Fixture f = Build();
        await Assert.That(Kill(f, tick: 1000, killer: 1, victim: 5)).IsFalse();
    }
}
