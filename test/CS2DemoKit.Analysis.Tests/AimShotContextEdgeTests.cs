#region

using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Analysis.Building;
using CS2DemoKit.Analysis.Edges;
using CS2DemoKit.Analysis.Nodes;
using CS2DemoKit.Analysis.Plugins;
using CS2DemoKit.Analysis.Visibility;
using CS2DemoKit.Parser;
using CS2DemoKit.Parser.GameEvents;
using CS2OpenSchema.Events;

#endregion

namespace CS2DemoKit.Analysis.Tests;

/// <summary>
///     Unit pins for <see cref="AimShotContextEdge" />: the counter-strafe admission gate and
///     verdict, spray segmentation by recoil decay, the effective-aim residual, and the dual
///     entity-state / server-value path. Pure in-memory, no demo file: the entity side is driven
///     through <c>EntityChangeScanner</c>'s precomputed-digest lane, which is the same code path a
///     real evaluation takes, so the frame-start-versus-current-frame distinction the edge depends
///     on is exercised rather than mocked away.
///     <para>
///         Each dispatch resets the transient nodes first, mirroring <c>StateGraphEvaluator</c>'s
///         transient-reset loop, so every assertion sees exactly what a rule's <c>where:</c> read
///         would see on that event.
///     </para>
/// </summary>
[Category("Unit")]
public class AimShotContextEdgeTests
{
    // Digest column order, which is also the provider registration order the rig builds. Named
    // because a swapped pair here reads as a plausible wrong number everywhere downstream.
    private const int ColMaxSpeed = 0;
    private const int ColRecoil = 1;
    private const int ColEyePitch = 2;
    private const int ColEyeYaw = 3;
    private const int ColPunchPitch = 4;
    private const int ColPunchYaw = 5;
    private const int ColPosX = 6;
    private const int ColPosY = 7;
    private const int ColPosZ = 8;
    private const int ColDuck = 9;

    private const int Columns = 10;

    /// <summary>An AK's movement cap; 0.34 of it is roughly 73 u/s, the counter-strafe line.</summary>
    private const float AkMaxSpeed = 215f;

    private const int Shooter = 0;

    /// <summary>
    ///     The denominator gate from the population side: a player who stood still all round is not
    ///     in it. See <see cref="AimShotContext.AboveThresholdInLookback" />.
    /// </summary>
    [Test]
    public async Task StandingStill_IsNotAdmittedToTheCounterStrafePopulation()
    {
        Rig rig = new();
        for (int i = 0; i < 40; i++)
        {
            rig.Tick(100 + i, Row(x: 0f, y: 0f));
        }

        await Assert.That(rig.Fire(139)).IsTrue();
        await Assert.That(rig.Admitted.IsActive).IsFalse()
            .Because("the player never exceeded 0.34 * max_speed in the lookback window");
        await Assert.That(rig.Good.IsActive).IsTrue()
            .Because("the shot itself was still, so the verdict is good even though it does not count");
    }

    /// <summary>
    ///     The shape the metric is actually about: moving fast, then stopped by the shot. Admitted
    ///     because of the movement, good because of the stop.
    ///     <para>
    ///         The shot sits five ticks after the last moving one, well inside the window at any
    ///         plausible value of it. That margin is deliberate: this test is about the admitted and
    ///         good verdicts, and it used to park for twelve ticks against a thirteen-tick window,
    ///         which quietly made it a boundary test that a change to
    ///         <see cref="AimShotContextEdge.CounterStrafeSpeedFraction" /> would have flipped for a
    ///         reason unrelated to its name. The boundary itself is
    ///         <see cref="Admission_TurnsOffOneTickPastTheWindow" />'s job.
    ///     </para>
    /// </summary>
    [Test]
    public async Task RunThenStop_IsAdmittedAndGood()
    {
        Rig rig = new();
        // 8 ticks at ~200 u/s (3.125 units per tick at 64/s), then parked through the shot tick.
        float x = 0f;
        for (int i = 0; i < 8; i++)
        {
            x += 200f / 64f;
            rig.Tick(100 + i, Row(x: x, y: 0f));
        }

        for (int i = 8; i < 13; i++)
        {
            rig.Tick(100 + i, Row(x: x, y: 0f));
        }

        await Assert.That(rig.Fire(112)).IsTrue();
        await Assert.That(rig.Admitted.IsActive).IsTrue()
            .Because("200 u/s five ticks earlier clears the ~73 u/s threshold and is inside the window");
        await Assert.That(rig.Good.IsActive).IsTrue()
            .Because("the shot tick itself differenced to zero travel");
    }

    /// <summary>
    ///     What the lookback constant actually governs: the tolerated GAP between the last sample
    ///     above the movement-inaccuracy line and the shot. Admission holds at a gap exactly equal to
    ///     the window and turns off one tick later.
    ///     <para>
    ///         This is the assertion that would have caught the window being sized from the wrong
    ///         interval. <c>CounterStrafeWindowDerivationTests</c> derives the number; this pins what
    ///         the number means to the gate, so a derivation of some other quantity that happened to
    ///         land near the same value could not pass unnoticed. Run at both tick rates because
    ///         <c>_lookbackTicks</c> converts the seconds constant at the demo's own rate, and no demo
    ///         in the corpus is 128.
    ///     </para>
    /// </summary>
    [Test]
    [Arguments(64.0)]
    [Arguments(128.0)]
    public async Task Admission_TurnsOffOneTickPastTheWindow(double tickRate)
    {
        int window = (int)Math.Round(AimShotContextEdge.CounterStrafeLookbackSeconds * tickRate);
        Console.WriteLine($"   {tickRate:F0}-tick: window {window} ticks");

        Rig atTheEdge = await FireAfterGap(window, tickRate);
        Rig pastIt = await FireAfterGap(window + 1, tickRate);

        await Assert.That(atTheEdge.Admitted.IsActive).IsTrue()
            .Because($"a gap of exactly {window} ticks is the last one the window covers");
        await Assert.That(pastIt.Admitted.IsActive).IsFalse()
            .Because($"one tick past {window} the shot is no longer the end of that stop");
    }

    /// <summary>
    ///     Widening the window can only ever add GOOD shots, so the moving-shot count the board shows
    ///     as Linear% does not depend on the window at all. Pinned because the reasoning is short
    ///     enough to look obvious and has one real precondition.
    ///     <para>
    ///         <b>The argument.</b> A shot that a narrower window would NOT admit had no sample above
    ///         the line in that narrower range, and the shot's own tick is in every range, so its
    ///         speed at the shot was at or below the line. On the fired arm that is exactly the
    ///         condition <see cref="AimShotContext.CounterStrafeGood" /> tests, so every shot the
    ///         wider window adds lands in the clean count and none in the moving count. The single
    ///         exception is a speed exactly EQUAL to the line, which is not good and not admitted
    ///         either, and which floating point makes measure-zero.
    ///     </para>
    ///     <para>
    ///         <b>The precondition.</b> That holds only because the fired arm carries no server
    ///         movement penalty, which is what sends <c>CounterStrafeGood</c> down its speed-fallback
    ///         branch; in the penalty branch the verdict is the server's and is not tied to our peak
    ///         at all. The counter-strafe counters all read the fired arm, so the invariant is
    ///         structural today, but it is one rules change away from not being, which is why the
    ///         null is asserted here rather than assumed.
    ///     </para>
    /// </summary>
    [Test]
    public async Task WideningTheWindow_AddsOnlyGoodShots()
    {
        int window = (int)Math.Round(AimShotContextEdge.CounterStrafeLookbackSeconds * 64.0);
        for (int gap = window - 2; gap <= window + 2; gap++)
        {
            Rig rig = await FireAfterGap(gap, 64.0);
            Console.WriteLine($"   gap {gap,3}: admitted={rig.Admitted.IsActive}, good={rig.Good.IsActive}");
            await Assert.That(rig.Good.IsActive).IsTrue()
                .Because($"the shot at a {gap}-tick gap was parked, so it is good whether or not this "
                         + "window admits it, and widening the window therefore adds nothing to the "
                         + "moving count");
            await Assert.That(rig.Run.Last!.ServerMovementPenalty).IsNull()
                .Because("the fired arm has no server penalty to prefer, which is what makes the "
                         + "verdict the speed comparison the argument above depends on");
        }
    }

    /// <summary>Still moving at the shot: admitted, and a failure.</summary>
    [Test]
    public async Task FiringWhileMoving_IsAdmittedAndBad()
    {
        Rig rig = new();
        float x = 0f;
        for (int i = 0; i < 30; i++)
        {
            x += 200f / 64f;
            rig.Tick(100 + i, Row(x: x, y: 0f));
        }

        await Assert.That(rig.Fire(129)).IsTrue();
        await Assert.That(rig.Admitted.IsActive).IsTrue();
        await Assert.That(rig.Good.IsActive).IsFalse();
    }

    /// <summary>
    ///     The engine's segmentation rule, not the "three or more shots" convention: a run is a
    ///     maximal stretch over which the recoil index never decays. Only the shot that opens one is
    ///     a first bullet.
    /// </summary>
    [Test]
    public async Task RisingRecoil_KeepsOneRun_AndOnlyItsOpeningShotIsAFirstBullet()
    {
        Rig rig = new();
        rig.Tick(94, Row(recoil: 0f)); // state entering the first shot: fresh weapon
        rig.Tick(100, Row(recoil: 1f)); // frame carrying the first shot; recoil already kicked
        await Assert.That(rig.Fire(100)).IsTrue();
        await Assert.That(rig.FirstBullet.IsActive).IsTrue();

        rig.Tick(106, Row(recoil: 2f));
        await Assert.That(rig.Fire(106)).IsTrue();
        await Assert.That(rig.FirstBullet.IsActive).IsFalse();

        rig.Tick(112, Row(recoil: 3f));
        await Assert.That(rig.Fire(112)).IsTrue();
        await Assert.That(rig.FirstBullet.IsActive).IsFalse();
        await Assert.That(rig.Run.ShotsInRun).IsEqualTo(3);
    }

    /// <summary>
    ///     A decayed recoil index is the engine saying the trigger was released, so the next shot
    ///     opens a new run even when the tick gap alone would have kept the old one alive.
    /// </summary>
    [Test]
    public async Task DecayedRecoil_OpensANewRun_EvenInsideTheGapBound()
    {
        Rig rig = new();
        rig.Tick(94, Row(recoil: 0f));
        rig.Tick(100, Row(recoil: 1f));
        rig.Fire(100);
        rig.Tick(106, Row(recoil: 2f));
        rig.Fire(106);

        // Trigger released between the shots: the index decayed back toward zero.
        rig.Tick(110, Row(recoil: 0.1f));

        // Same 6-tick spacing the run measured its bound from, so the gap alone would continue it.
        rig.Tick(112, Row(recoil: 1f));
        await Assert.That(rig.Fire(112)).IsTrue();
        await Assert.That(rig.FirstBullet.IsActive).IsTrue();
        await Assert.That(rig.Run.ShotsInRun).IsEqualTo(1);
    }

    /// <summary>
    ///     THE test this whole edge exists for. Two shots in one spray whose VIEW angles are
    ///     identical: a residual computed on view angle alone reports perfect control, which is
    ///     smooth, believable and wrong. The effective aim carries
    ///     <see cref="AimShotContextEdge.WeaponRecoilScale" /> times the aim punch, so the residual
    ///     is the recoil the player did not compensate for.
    /// </summary>
    [Test]
    public async Task SprayResidual_MeasuresEffectiveAim_NotViewAngleAlone()
    {
        Rig rig = new();
        rig.Tick(94, Row(recoil: 0f, eyePitch: -2f, punchPitch: 0f));
        rig.Tick(100, Row(recoil: 1f, eyePitch: -2f, punchPitch: -1.5f));
        rig.Fire(100);
        await Assert.That(rig.ResidualPitch.Value).IsEqualTo(0.0).Within(1e-6)
            .Because("the opening shot IS the anchor");

        // Identical view angle, 1.5 degrees of aim punch accumulated by the first shot.
        rig.Tick(106, Row(recoil: 2f, eyePitch: -2f, punchPitch: -3f));
        await Assert.That(rig.Fire(106)).IsTrue();
        await Assert.That(rig.ResidualPitch.Value).IsEqualTo(-3.0).Within(1e-6)
            .Because("2.0 * -1.5 is the recoil the view angle never showed; dropping the scale "
                     + "would report a residual of 0 on a shot that climbed three degrees");
    }

    /// <summary>
    ///     Yaw wraps at the half turn, so a spray that crosses it must not report a 359-degree
    ///     residual on a shot the player barely moved for.
    /// </summary>
    [Test]
    public async Task SprayResidualYaw_TakesTheShortWayRound()
    {
        Rig rig = new();
        rig.Tick(94, Row(recoil: 0f, eyeYaw: 179f));
        rig.Tick(100, Row(recoil: 1f, eyeYaw: -179f));
        rig.Fire(100);

        rig.Tick(106, Row(recoil: 2f, eyeYaw: -179f));
        await Assert.That(rig.Fire(106)).IsTrue();
        await Assert.That(rig.ResidualYaw.Value).IsEqualTo(2.0).Within(1e-6);
    }

    /// <summary>
    ///     A <c>QAngle</c> component arrives over [0, 360), so a real -2 degree kick is on the wire
    ///     as 358. Rejecting that as out of range would throw away every downward punch there is.
    /// </summary>
    [Test]
    public async Task PunchNearTheWrap_IsNormalisedNotRejected()
    {
        Rig rig = new();
        rig.Tick(94, Row(recoil: 0f, eyePitch: -2f, punchPitch: 0f));
        rig.Tick(100, Row(recoil: 1f, eyePitch: -2f, punchPitch: 358f)); // -2 degrees on the wire
        rig.Fire(100);

        rig.Tick(106, Row(recoil: 2f, eyePitch: -2f, punchPitch: 356f)); // -4 degrees
        await Assert.That(rig.Fire(106)).IsTrue();
        await Assert.That(rig.ResidualMeasured.IsActive).IsTrue();
        await Assert.That(rig.ResidualPitch.Value).IsEqualTo(-4.0).Within(1e-6)
            .Because("2.0 * (-2 - 0) is the recoil between the anchor's punch and this shot's");
    }

    /// <summary>
    ///     A punch component that is not physically a punch (measured on the bundled sample: about
    ///     -94 degrees) must leave the residual UNMEASURED. Folding it in produces a residual of
    ///     several hundred degrees; suppressing it without saying so produces a 0 that reads as
    ///     flawless recoil control. The companion bool is what separates the two.
    /// </summary>
    [Test]
    public async Task ImplausiblePunch_LeavesTheResidualUnmeasured()
    {
        Rig rig = new();
        rig.Tick(94, Row(recoil: 0f, eyePitch: -2f, punchPitch: 0f));
        rig.Tick(100, Row(recoil: 1f, eyePitch: -2f, punchPitch: 265.99f));
        rig.Fire(100);

        rig.Tick(106, Row(recoil: 2f, eyePitch: -2f, punchPitch: 266.14f));
        await Assert.That(rig.Fire(106)).IsTrue();
        await Assert.That(rig.ResidualMeasured.IsActive).IsFalse();
        await Assert.That(rig.ResidualPitch.Value).IsEqualTo(0.0).Within(1e-6);

        // The shot is still a shot: the movement and segmentation columns are independent of the
        // punch read and must not be lost with it.
        await Assert.That(rig.FirstBullet.IsActive).IsFalse();
        await Assert.That(rig.Run.ShotsInRun).IsEqualTo(2);
    }

    /// <summary>
    ///     A grenade or knife also fires <c>weapon_fire</c>. Letting one through would split every
    ///     spray it landed inside and make the shot after it look like a first bullet.
    /// </summary>
    [Test]
    public async Task NonBulletWeapon_IsIgnoredAndDoesNotBreakARun()
    {
        Rig rig = new();
        rig.Tick(94, Row(recoil: 0f));
        rig.Tick(100, Row(recoil: 1f));
        rig.Fire(100);

        rig.Tick(103, Row(recoil: 1f));
        await Assert.That(rig.Fire(103, "weapon_flashbang")).IsFalse();

        rig.Tick(106, Row(recoil: 2f));
        await Assert.That(rig.Fire(106)).IsTrue();
        await Assert.That(rig.FirstBullet.IsActive).IsFalse();
        await Assert.That(rig.Run.ShotsInRun).IsEqualTo(2);
    }

    /// <summary>
    ///     The dual path. The server's <c>InaccuracyMove</c> is what the engine actually charged for
    ///     movement at this shot, so where it exists it outranks the reconstruction, even when the
    ///     reconstruction says the player was sprinting.
    /// </summary>
    [Test]
    public async Task LandedShot_PrefersTheServersMovementPenalty_OverTheDerivedSpeed()
    {
        Rig rig = new();
        float x = 0f;
        for (int i = 0; i < 30; i++)
        {
            x += 200f / 64f;
            rig.Tick(100 + i, Row(x: x, y: 0f, recoil: 0f));
        }

        await Assert.That(rig.Fire(129)).IsTrue();
        await Assert.That(rig.Good.IsActive).IsFalse()
            .Because("the derived speed says the player was moving");

        await Assert.That(rig.Land(129, inaccuracyMove: 0f, recoilIndex: 0f)).IsTrue();
        await Assert.That(rig.Good.IsActive).IsTrue()
            .Because("the server charged no movement penalty, and that outranks the derivation");
        await Assert.That(rig.Admitted.IsActive).IsTrue()
            .Because("admission still comes from the movement history, which the server does not carry");
    }

    /// <summary>
    ///     A landed shot is one of the fired shots. If the landed arm advanced the run too, every
    ///     bullet that connected would count twice and every measured spray would be short.
    /// </summary>
    [Test]
    public async Task LandedShot_DoesNotAdvanceTheSprayRun()
    {
        Rig rig = new();
        rig.Tick(94, Row(recoil: 0f));
        rig.Tick(100, Row(recoil: 1f));
        rig.Fire(100);
        rig.Land(100, inaccuracyMove: 0f, recoilIndex: 0f);

        rig.Tick(106, Row(recoil: 2f));
        rig.Fire(106);
        rig.Land(106, inaccuracyMove: 0f, recoilIndex: 1f);

        await Assert.That(rig.Run.ShotsInRun).IsEqualTo(2)
            .Because("two triggers were pulled, not four");
    }

    /// <summary>
    ///     A round boundary is a hard reset of everyone's engagement: a run that survived one would
    ///     weld the last shot of one round to the first of the next and report a residual measured
    ///     against an anchor from a different fight.
    /// </summary>
    [Test]
    public async Task RoundReset_ClearsTheSprayRun()
    {
        Rig rig = new();
        rig.Tick(94, Row(recoil: 0f));
        rig.Tick(100, Row(recoil: 1f));
        rig.Fire(100);
        rig.Tick(106, Row(recoil: 2f));
        rig.Fire(106);
        await Assert.That(rig.Run.ShotsInRun).IsEqualTo(2);

        rig.Players.ResetRoundState();
        await Assert.That(rig.Run.ShotsInRun).IsEqualTo(0);
        await Assert.That(rig.Run.LastShotTick).IsEqualTo(-1);
        await Assert.That(rig.Run.Last).IsNull();

        rig.Tick(112, Row(recoil: 3f));
        await Assert.That(rig.Fire(112)).IsTrue();
        await Assert.That(rig.FirstBullet.IsActive).IsTrue()
            .Because("the first shot after a reset opens a run whatever the recoil index says");
    }

    /// <summary>
    ///     A window with no sample is not a window of zeros. Reading it as "stood perfectly still"
    ///     would silently admit or acquit every shot taken while a player's columns were missing.
    /// </summary>
    [Test]
    public async Task PeakSpeed_ReportsNoSampleRatherThanZero()
    {
        Rig rig = new();
        rig.Tick(100, Row(x: 0f));
        rig.Tick(101, Row(x: 1f));

        await Assert.That(rig.Vantage.TryPeakSpeed(Shooter, 100, 101, out float measured)).IsTrue();
        await Assert.That(measured).IsGreaterThan(0f);
        await Assert.That(rig.Vantage.TryPeakSpeed(Shooter, 500, 600, out float _)).IsFalse();
        await Assert.That(rig.Vantage.TryPeakSpeed(99, 100, 101, out float _)).IsFalse();
    }

    /// <summary>
    ///     One full digest row. Defaults are a standing player with a fresh weapon at the origin, so
    ///     each test names only the columns it is about.
    /// </summary>
    // Runs the shooter for eight sampled ticks at 200 u/s, parks them, and fires exactly gapTicks
    // after the last moving tick. Returns the rig so a caller can read both verdicts off it.
    private static async Task<Rig> FireAfterGap(int gapTicks, double tickRate)
    {
        Rig rig = new(tickRate: tickRate);
        float perTick = (float)(200.0 / tickRate);
        float x = 0f;
        int tick = 100;
        for (int i = 0; i < 8; i++)
        {
            x += perTick;
            rig.Tick(tick++, Row(x: x, y: 0f));
        }

        int lastMovingTick = tick - 1;
        int shotTick = lastMovingTick + gapTicks;
        while (tick <= shotTick)
        {
            rig.Tick(tick++, Row(x: x, y: 0f));
        }

        await Assert.That(rig.Fire(shotTick)).IsTrue();
        return rig;
    }

    private static object?[] Row(
        float x = 0f, float y = 0f, float recoil = 0f,
        float eyePitch = 0f, float eyeYaw = 0f,
        float punchPitch = 0f, float punchYaw = 0f)
    {
        object?[] row = new object?[Columns];
        row[ColMaxSpeed] = AkMaxSpeed;
        row[ColRecoil] = recoil;
        row[ColEyePitch] = eyePitch;
        row[ColEyeYaw] = eyeYaw;
        row[ColPunchPitch] = punchPitch;
        row[ColPunchYaw] = punchYaw;
        row[ColPosX] = x;
        row[ColPosY] = y;
        row[ColPosZ] = 0f;
        row[ColDuck] = 0f;
        return row;
    }

    /// <summary>
    ///     One shooter, both edge arms, and the entity pipeline behind them. Frames are fed through
    ///     the scanner's precomputed-digest lane so the pre-frame snapshot (what the edge reads for
    ///     recoil and aim) and the current-frame vantage sample (what it reads for speed) come apart
    ///     exactly as they do in a real evaluation.
    /// </summary>
    /// <summary>
    ///     A landed shot that follows a contact reports the interval to it and the crosshair travel
    ///     from it, on the LANDED arm specifically.
    ///     <para>
    ///         The fired arm and the landed arm are two instances of this edge over two different
    ///         events. Every shot-anchored aim column that pairs a shot against a contact reads these
    ///         through the <c>shot_landed</c> view, so an inert landed arm leaves them sitting on
    ///         their sentinels and every dependent column reads a confident 0.0 with no error to say
    ///         the population was empty.
    ///     </para>
    /// </summary>
    [Test]
    public async Task LandedShot_AfterAContact_ReportsTheIntervalAndTheTravel()
    {
        Rig rig = new();
        rig.Tick(100, Row(recoil: 0f, eyePitch: 0f, eyeYaw: 0f));

        bool found = rig.Players.TryGet(Shooter, out PlayerContextIndex.PlayerContext? ctx);
        await Assert.That(found).IsTrue();
        ctx!.LastSpotTick = 100;
        ctx.LastSpotPitch = 0f;
        ctx.LastSpotYaw = 0f;
        ctx.LastSpotChestAngle = 2f;

        await Assert.That(rig.Land(110, 0f, 0f)).IsTrue();

        await Assert.That(rig.TicksSinceSpot.Value).IsEqualTo(10)
            .Because("the landed arm must measure from the latched contact, not fall to the sentinel");
        await Assert.That(rig.TravelFromSpot.Value).IsGreaterThanOrEqualTo(0.0);
    }

    /// <summary>
    ///     Whether a shot is the first to answer an acquisition is decided per ARM, exactly as
    ///     <c>is_first_after_spot</c> is.
    ///     <para>
    ///         One shared latch never survives to the landed arm; see
    ///         <see cref="PlayerContextIndex.PlayerContext.AnsweredOnTargetSinceLanded" />. What the
    ///         regression costs is an aimed-reaction metric on the landed arm reporting an empty
    ///         population, with nothing to say the gate emptied it rather than the data.
    ///     </para>
    /// </summary>
    [Test]
    public async Task FirstShotAfterAcquisition_IsAnsweredPerArm()
    {
        Rig rig = new(OnTargetSince(100));
        rig.Tick(100, Row(recoil: 0f, eyePitch: 0f, eyeYaw: 0f));

        await Assert.That(rig.Fire(105)).IsTrue();
        await Assert.That(rig.TicksSinceOnTarget.Value).IsEqualTo(5);
        await Assert.That(rig.FirstAfterOnTarget.IsActive).IsTrue();

        await Assert.That(rig.Land(105, 0f, 0f)).IsTrue();
        await Assert.That(rig.TicksSinceOnTarget.Value).IsEqualTo(5);
        await Assert.That(rig.FirstAfterOnTarget.IsActive).IsTrue()
            .Because("time-to-shoot and time-to-damage are different metrics over different events: "
                     + "the fired arm answering an acquisition does not answer it for the landed arm");

        // Neither arm answers the same acquisition twice.
        await Assert.That(rig.Fire(112)).IsTrue();
        await Assert.That(rig.FirstAfterOnTarget.IsActive).IsFalse();
        await Assert.That(rig.Land(112, 0f, 0f)).IsTrue();
        await Assert.That(rig.FirstAfterOnTarget.IsActive).IsFalse();
    }

    /// <summary>
    ///     A shot with no acquisition behind it reports the sentinel rather than a zero interval, and
    ///     is not the first shot of anything.
    /// </summary>
    [Test]
    public async Task ShotWithNoAcquisition_ReportsTheSentinel()
    {
        Rig rig = new();
        rig.Tick(100, Row(recoil: 0f, eyePitch: 0f, eyeYaw: 0f));

        await Assert.That(rig.Fire(105)).IsTrue();
        await Assert.That(rig.TicksSinceOnTarget.Value).IsEqualTo(AimShotContextEdge.NoSpotSentinel);
        await Assert.That(rig.FirstAfterOnTarget.IsActive).IsFalse();
    }

    /// <summary>
    ///     A visibility scanner whose only viewer is <see cref="Shooter" />, crosshair on an enemy
    ///     since <paramref name="tick" />. Real geometry rather than a stub: <c>OnTargetSince</c> is
    ///     the scanner's own derivation off its vantage set and there is no seam to inject at.
    /// </summary>
    /// <param name="tick">The tick the crosshair arrives on the enemy.</param>
    private static VisibilityTransitionScanner OnTargetSince(int tick)
    {
        string[] columns =
        [
            AimVantageScanner.DuckAmountProvider,
            AimVantageScanner.EyePitchProvider,
            AimVantageScanner.EyeYawProvider,
            AimVantageScanner.PosXProvider,
            AimVantageScanner.PosYProvider,
            AimVantageScanner.PosZProvider
        ];

        // Shooter's feet 16 below the enemy's puts its eye level with the enemy's chest anchor, so a
        // level crosshair down +X is a zero-degree error and unambiguously on target.
        AimVantageScanner sight = new(columns, slot => slot == Shooter ? 2 : 3);
        sight.Observe(Shooter, [0f, 0f, 0f, 0f, 0f, -16f]);
        sight.Observe(1, [0f, 0f, 0f, 500f, 0f, 0f]);

        VisibilityTransitionScanner visibility = new(VisibilityEngine.FromTriangles([], 0));
        visibility.Sample(tick, tick, sight.Sample(tick));
        return visibility;
    }

    private sealed class Rig
    {
        // Comfortably more frames than any test here advances, and never reaching the last index:
        // consuming it hands the array back as null, which is the eval loop's release-after-consume
        // contract and not something a test should trip over.
        private const int FrameCapacity = 256;

        private readonly EntityFrameDigest?[] _digests = new EntityFrameDigest?[FrameCapacity];
        private readonly AimShotContextEdge _fired;
        private readonly AimShotContextEdge _landed;
        private readonly DigestColumnLayout _layout;
        private readonly EntityChangeScanner _scanner;
        private int _frame;

        internal Rig(VisibilityTransitionScanner? visibility = null, double tickRate = 64.0)
        {
            Players.Register(Shooter, new PlayerContextIndex.PlayerContext(Shooter, 2));

            IPerPlayerEntityValueProvider[] providers =
            [
                new GenericPerPlayerFieldProvider(BuiltinProviderSpecs.PawnMaxSpeed),
                new GenericPerPlayerFieldProvider(BuiltinProviderSpecs.WeaponRecoilIndex),
                new PawnEyeAngleProvider(PawnAngleAxis.Pitch),
                new PawnEyeAngleProvider(PawnAngleAxis.Yaw),
                new PawnAimPunchProvider(PawnAngleAxis.Pitch),
                new PawnAimPunchProvider(PawnAngleAxis.Yaw),
                new PawnPositionProvider(PawnPositionAxis.X),
                new PawnPositionProvider(PawnPositionAxis.Y),
                new PawnPositionProvider(PawnPositionAxis.Z),
                new GenericPerPlayerFieldProvider(BuiltinProviderSpecs.PawnDuckAmount)
            ];
            _layout = DigestColumnLayout.For(providers);

            Vantage = new AimVantageScanner(
                providers.Select(p => p.Name).ToList(), _ => 2, tickRate);
            _scanner = new EntityChangeScanner(
                new EntityStateLayer([]),
                providers: [],
                perPlayerProviders: providers,
                emitMolotovThrows: false,
                vantageScanner: Vantage);
            _scanner.SetPrecomputedDigests(_digests);

            AimShotContextSources sources = new(
                _scanner, providers[ColMaxSpeed], providers[ColRecoil],
                providers[ColEyePitch], providers[ColEyeYaw],
                providers[ColPunchPitch], providers[ColPunchYaw],
                Vantage, visibility, tickRate);

            GenericBoolNode root = new("root");
            _fired = new AimShotContextEdge(
                root, Players, Good, Admitted, FirstBullet, ResidualPitch, ResidualYaw, ResidualMeasured,
                TicksSinceSpot, TravelFromSpot, FlickError, FirstAfterSpot, ResidualDeg,
                TicksSinceOnTarget, FirstAfterOnTarget, typeof(WeaponFireEvent), sources);
            _landed = new AimShotContextEdge(
                root, Players, Good, Admitted, FirstBullet, ResidualPitch, ResidualYaw, ResidualMeasured,
                TicksSinceSpot, TravelFromSpot, FlickError, FirstAfterSpot, ResidualDeg,
                TicksSinceOnTarget, FirstAfterOnTarget, typeof(BulletDamageEvent), sources);
        }

        internal TransientBoolNode FirstAfterSpot { get; } = new("enrich.shot.is_first_after_spot");

        internal TransientValueNode<int> TicksSinceOnTarget { get; } =
            new("enrich.shot.ticks_since_on_target", AimShotContextEdge.NoSpotSentinel);

        internal TransientBoolNode FirstAfterOnTarget { get; } =
            new("enrich.shot.is_first_after_on_target");

        internal TransientValueNode<double> ResidualDeg { get; } = new("enrich.shot.spray_residual_deg");

        internal TransientValueNode<double> FlickError { get; } =
            new("enrich.shot.flick_error_deg", AimShotContextEdge.NoFlickSentinel);

        internal TransientValueNode<double> TravelFromSpot { get; } =
            new("enrich.shot.travel_from_spot_deg", AimShotContextEdge.NoTravelSentinel);

        internal TransientValueNode<int> TicksSinceSpot { get; } =
            new("enrich.shot.ticks_since_spot", AimShotContextEdge.NoSpotSentinel);

        internal TransientBoolNode Admitted { get; } = new("enrich.shot.counter_strafe_admitted");

        internal TransientBoolNode FirstBullet { get; } = new("enrich.shot.is_first_bullet");

        internal TransientBoolNode Good { get; } = new("enrich.shot.counter_strafe_good");

        internal PlayerContextIndex Players { get; } = new();

        internal TransientValueNode<double> ResidualPitch { get; } = new("enrich.shot.spray_residual_pitch");

        internal TransientValueNode<double> ResidualYaw { get; } = new("enrich.shot.spray_residual_yaw");

        internal TransientBoolNode ResidualMeasured { get; } = new("enrich.shot.spray_residual_measured");

        /// <summary>The shooter's live spray-run state, which several assertions read directly.</summary>
        internal AimShotState Run
        {
            get
            {
                Players.TryGet(Shooter, out PlayerContextIndex.PlayerContext? ctx);
                return ctx!.AimShot;
            }
        }

        internal AimVantageScanner Vantage { get; }

        /// <summary>Dispatches a <c>weapon_fire</c> for the shooter at <paramref name="tick" />.</summary>
        internal bool Fire(int tick, string weapon = "weapon_ak47")
        {
            GameEvent evt = new("weapon_fire", -1, _frame, tick, tick,
                new WeaponFireEvent { UserId = Shooter, UserIdPawn = 0, Weapon = weapon, Silenced = false });
            return Dispatch(_fired, evt);
        }

        /// <summary>Dispatches a <c>bullet_damage</c> for the shooter at <paramref name="tick" />.</summary>
        internal bool Land(int tick, float inaccuracyMove, float recoilIndex)
        {
            BulletDamageEvent landed = new()
            {
                Victim = 5, VictimPawn = 0, Attacker = Shooter, AttackerPawn = 0, Distance = 500f,
                DamageDirX = 0f, DamageDirY = 0f, DamageDirZ = 0f, NumPenetrations = 0,
                NoScope = false, InAir = false,
                ShootAngX = 0f, ShootAngY = 0f, ShootAngZ = 0f,
                AimPunchX = 0f, AimPunchY = 0f, AimPunchZ = 0f,
                AttackTickCount = tick, AttackTickFrac = 0f,
                RenderTickCount = tick, RenderTickFrac = 0f,
                InaccuracyTotal = 0f, InaccuracyMove = inaccuracyMove, InaccuracyAir = 0f,
                RecoilIndex = recoilIndex, Type = 0
            };
            return Dispatch(_landed, new GameEvent("bullet_damage", -1, _frame, tick, tick, landed));
        }

        /// <summary>Feeds one frame of per-pawn digest values at <paramref name="tick" />.</summary>
        internal void Tick(int tick, object?[] values)
        {
            EntityFrameDigest digest = new();
            digest.PerPawn = PerPawnColumns.FromBoxedRows(_layout, [(Shooter, values)]);
            _digests[_frame] = digest;
            _ = _scanner.AdvanceAndPollAt(_frame, tick);
            _frame++;
        }

        private bool Dispatch(AimShotContextEdge edge, GameEvent evt)
        {
            // Mirror the evaluator's per-dispatch transient reset, so an assertion cannot pass on a
            // value the PREVIOUS shot left behind.
            Good.Reset();
            Admitted.Reset();
            FirstBullet.Reset();
            ResidualPitch.Reset();
            ResidualMeasured.Reset();
            ResidualYaw.Reset();
            ((ITransientNode)TicksSinceSpot).Reset();
            ((ITransientNode)TravelFromSpot).Reset();
            ((ITransientNode)FlickError).Reset();
            FirstAfterSpot.Reset();
            ((ITransientNode)TicksSinceOnTarget).Reset();
            FirstAfterOnTarget.Reset();
            ((ITransientNode)ResidualDeg).Reset();

            GameEventMessage msg = GameEventMessage.ForSynthesizedEvent(evt);
            DemoFrame frame = new()
            {
                Command = "DEM_Packet",
                FrameNumber = evt.FrameNumber,
                ServerTick = evt.ServerTick,
                RawStart = 0,
                RawLength = 1,
                HeaderLength = 1,
                IsCompressed = false,
                MessageList = [msg]
            };
            return edge.TryApplyDirect(evt.Payload!, new EvaluationContext(msg, frame));
        }
    }
}
