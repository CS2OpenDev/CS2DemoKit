#region

using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Analysis.Building;
using CS2DemoKit.Analysis.Nodes;
using CS2DemoKit.Analysis.Plugins;
using CS2DemoKit.Analysis.Visibility;
using CS2DemoKit.Parser.GameEvents;

#endregion

namespace CS2DemoKit.Analysis.Edges;

/// <summary>
///     Everything one shot is known to be, resolved once and read by every shot-anchored metric.
///     Counter-strafing, first-bullet accuracy, spray segmentation and spray control all consume
///     this record rather than each reaching back into entity state for the piece it needs: derive
///     a fact twice and the two derivations drift, and the drift shows up as two metrics that
///     disagree about the same shot with nothing in the code saying which is right.
///     <para>
///         <b>Two provenances, one verdict.</b> <see cref="ServerMovementPenalty" /> is the server's
///         own movement-inaccuracy term for this shot, carried by <c>bullet_damage</c> and therefore
///         available only on demos that emit it and only for shots that landed. Where it exists it
///         is strictly more authoritative than any reconstruction, so
///         <see cref="CounterStrafeGood" /> prefers it and falls back to the speed derivation
///         otherwise. On a demo carrying both, the two must agree; that disagreement is the oracle
///         the derivation is validated against.
///     </para>
/// </summary>
/// <param name="Slot">Player slot that fired.</param>
/// <param name="Tick">
///     The FRAME's tick, which is the clock the entity scan and the vantage sampler are driven on.
///     Deliberately not the event's own <c>ServerTick</c>: that one counts from the server's boot
///     rather than the recording's start, so the two differ by the demo's start tick (measured on
///     the bundled sample: event tick 30,176 against frame tick 9,720). Building a speed window
///     from the event clock leaves it permanently empty, which reads as "this player never moved"
///     and drops every shot out of the counter-strafing population without an error anywhere.
/// </param>
/// <param name="Speed2D">
///     Horizontal speed at the shot, in units per second, differenced from the previous sampled
///     tick's position. Zero and <see cref="HasMovementSample" /> false when no usable sample
///     exists: <c>m_vecVelocity</c> reads uniformly zero on GOTV pawns, so this is a derivative of
///     position and inherits the tick-quantisation error CS2's split-tick simulation introduces.
/// </param>
/// <param name="CounterStrafeThreshold">
///     <c>0.34 * m_flMaxspeed</c> at the shot: the engine's own line below which movement
///     contributes no inaccuracy. Per weapon, not a fixed speed, which is why the raw threshold
///     rides on the record instead of the fraction.
/// </param>
/// <param name="HasMovementSample">
///     Whether <see cref="Speed2D" /> and <see cref="CounterStrafeThreshold" /> are real
///     measurements. False means the columns were missing or the digest was frozen, and a consumer
///     must drop the shot rather than read the zeros as a perfectly still player.
/// </param>
/// <param name="AboveThresholdInLookback">
///     Whether the player exceeded <see cref="CounterStrafeThreshold" /> at any sampled tick in the
///     <see cref="AimShotContextEdge.CounterStrafeLookbackSeconds" /> before the shot. This is the
///     DENOMINATOR gate: a shot from a player who never moved is not a failed counter-strafe, it is
///     not a counter-strafe attempt at all, and counting it dilutes the metric toward "how often did
///     this player stand still".
/// </param>
/// <param name="RecoilIndex">
///     The weapon's recoil index entering the shot, or a negative value when unreadable. Rises per
///     shot while firing and decays once fire stops, so zero means first bullet and monotonicity
///     across shots is the engine's own definition of one spray.
/// </param>
/// <param name="EnemyVisible">
///     Whether any enemy could be seen by this player at the shot, from the visibility scan's level
///     (not its rising edges). False when no map bake was supplied, in which case every
///     visibility-gated metric reading this correctly reports an empty population rather than a
///     wrong one.
/// </param>
/// <param name="EffectiveAimPitch">
///     View pitch plus <see cref="AimShotContextEdge.WeaponRecoilScale" /> times aim-punch pitch, in
///     degrees. See <see cref="AimShotContextEdge" /> on why the scale is not optional.
/// </param>
/// <param name="EffectiveAimYaw">The same for yaw.</param>
/// <param name="ServerMovementPenalty">
///     <c>bullet_damage.InaccuracyMove</c>, the server's own movement penalty at this shot, or null
///     on the reconstructed path.
/// </param>
public sealed record AimShotContext(
    int Slot,
    int Tick,
    float Speed2D,
    float CounterStrafeThreshold,
    bool HasMovementSample,
    bool AboveThresholdInLookback,
    float RecoilIndex,
    bool EnemyVisible,
    double EffectiveAimPitch,
    double EffectiveAimYaw,
    float? ServerMovementPenalty)
{
    /// <summary>
    ///     Was this shot fired slow enough that movement added no inaccuracy? Prefers the server's
    ///     own answer where it exists (any non-zero <see cref="ServerMovementPenalty" /> means the
    ///     engine applied a movement penalty, which is the same fact as "was moving too fast") and
    ///     falls back to comparing the derived speed against the threshold. False without a
    ///     measurement of either kind, which pairs with <see cref="AboveThresholdInLookback" /> also
    ///     being false so the shot leaves the population rather than entering it as a failure.
    ///     <para>
    ///         <b>Which branch runs where.</b> Only the LANDED arm has a penalty to prefer;
    ///         <c>weapon_fire</c> carries no such column, so the fired arm passes null and always
    ///         takes the speed comparison. Every counter-strafe counter in the shipped ruleset counts
    ///         the fired arm, which is what makes the moving-shot count independent of the lookback
    ///         window: see <c>AimShotContextEdgeTests.WideningTheWindow_AddsOnlyGoodShots</c> for the
    ///         argument and the precondition it rests on.
    ///     </para>
    /// </summary>
    public bool CounterStrafeGood =>
        ServerMovementPenalty is { } penalty
            ? penalty <= AimShotContextEdge.ServerMovementPenaltyEpsilon
            : HasMovementSample && Speed2D < CounterStrafeThreshold;
}

/// <summary>
///     The per-player entity reads <see cref="AimShotContextEdge" /> pulls out of the scanner's
///     pre-frame snapshot, bundled so the edge's constructor stays readable. Every one of them must
///     also have been gated into the scanner's provider list, or the first read throws: the
///     reference gate and this bundle are two halves of one wiring decision made in
///     <c>RuleChainBuilder</c>.
/// </summary>
/// <param name="Scanner">The scanner whose pre-frame snapshot the reads come from.</param>
/// <param name="MaxSpeed"><c>entity.pawn.max_speed</c>: the counter-strafe threshold's denominator.</param>
/// <param name="RecoilIndex"><c>entity.weapon.recoil_index</c>: spray segmentation and first-bullet.</param>
/// <param name="EyePitch"><c>entity.pawn.eye_pitch</c>.</param>
/// <param name="EyeYaw"><c>entity.pawn.eye_yaw</c>.</param>
/// <param name="PunchPitch"><c>entity.pawn.punch_pitch</c>: the aim-punch spring's base angle, X.</param>
/// <param name="PunchYaw"><c>entity.pawn.punch_yaw</c>: the same, Y.</param>
/// <param name="Vantage">
///     The per-tick vantage sampler, which owns the single derivation of 2D speed and its history.
/// </param>
/// <param name="Visibility">
///     The visibility scan, or null when no map bake was supplied. Only the could-see LEVEL is read
///     from it, never the edges.
/// </param>
/// <param name="TickRate">Ticks per second, used to convert the counter-strafe lookback into ticks.</param>
public sealed record AimShotContextSources(
    EntityChangeScanner Scanner,
    IPerPlayerEntityValueProvider MaxSpeed,
    IPerPlayerEntityValueProvider RecoilIndex,
    IPerPlayerEntityValueProvider EyePitch,
    IPerPlayerEntityValueProvider EyeYaw,
    IPerPlayerEntityValueProvider PunchPitch,
    IPerPlayerEntityValueProvider PunchYaw,
    AimVantageScanner Vantage,
    VisibilityTransitionScanner? Visibility,
    double TickRate);

/// <summary>
///     Builds ONE <see cref="AimShotContext" /> per shot and emits the shot-anchored enrichments
///     from it: <c>enrich.shot.counter_strafe_good</c>, <c>enrich.shot.counter_strafe_admitted</c>,
///     <c>enrich.shot.is_first_bullet</c>, and the spray-control residual pair
///     <c>enrich.shot.spray_residual_pitch</c> / <c>_yaw</c>. State that has to survive between
///     shots (the spray run, the record itself) lives on the shooter's
///     <see cref="PlayerContextIndex.PlayerContext" /> and is cleared at every round boundary.
///     <para>
///         <b>Two arms, one record.</b> The edge is instantiated twice: on
///         <see cref="WeaponFireEvent" />, which is every shot and therefore the only population a
///         counter-strafe denominator can be built from, and on <see cref="BulletDamageEvent" />,
///         which fires only for shots that landed but carries the server's own
///         <c>InaccuracyMove</c>, <c>RecoilIndex</c>, <c>ShootAng*</c> and <c>AimPunch*</c>. Probed
///         on a Valve matchmaking demo, <c>weapon_fire</c> precedes its <c>bullet_damage</c> in
///         every one of 580 matched pairs, so the reconstructed arm can never consult the server
///         value for its own shot: the arms are ordered, not racing, and the second refines the
///         record the first latched. That ordering is why "prefer the server value" is expressed on
///         the record (<see cref="AimShotContext.CounterStrafeGood" />) rather than as a lookup at
///         build time.
///     </para>
///     <para>
///         <b>Only the fired arm owns the spray run.</b> A landed shot is one of the fired shots, so
///         advancing the run on both would count every landing bullet twice and shorten every
///         measured spray. The landed arm reads the segmentation and never writes it.
///     </para>
///     <para>
///         <b>But both arms emit residuals, from separate anchors.</b> The landed arm anchors on the
///         first BULLET THAT LANDED in the run rather than the first shot fired, and measures the
///         drift of <c>ShootAng*</c> from it. That is a survivorship-biased population and it is
///         emitted anyway, because the alternative is no spray-control measurement at all: the fired
///         arm's anchor is built from the entity punch column, which does not decode to degrees on a
///         GOTV demo (see the known gap below), while <c>ShootAng*</c> is the server's own resolved
///         firing direction. The fired arm still owns the run boundaries, so a new run clears the
///         landed anchor too and the two arms segment the same sprays. Which arm a number came from
///         is the view it was read through: <c>shot</c> is the fired arm, <c>shot_landed</c> the
///         landed one, and a metric must not pool the two.
///     </para>
///     <para>
///         <b>The 2.0 is not optional.</b> Effective aim is the view angle plus
///         <see cref="WeaponRecoilScale" /> times the aim punch, because that scale
///         (<c>weapon_recoil_scale</c>) is what the engine applies when it turns recoil into where
///         the bullet actually goes. Dropping it yields a residual that is smooth, plausible and
///         wrong by the size of the recoil pattern, which is the entire signal spray control is
///         trying to measure.
///     </para>
///     <para>
///         <b>Known gap, measured.</b> On the reconstructed arm the punch terms come from
///         <c>m_predictableBaseAngle</c>, a raw spring sample and not a resolved punch
///         (<see cref="AimPunchState" /> has the why), and on the bundled GOTV sample it does
///         not currently decode to an aim punch at all: the components cluster near -94 and +89
///         degrees. <see cref="PlausiblePunch" /> rejects those, which leaves the residual
///         UNMEASURED rather than several hundred degrees wide, and
///         <c>enrich.shot.spray_residual_measured</c> is how a consumer sees the difference between
///         that and a genuine zero. Spray control is therefore not computable from this arm today;
///         it becomes computable the moment the column decodes to real degrees, with no change
///         here. The landed arm has no such problem: its punch is the server's resolved
///         <c>AimPunch*</c>, which is exactly the oracle the reconstruction will have to agree with.
///     </para>
/// </summary>
public sealed class AimShotContextEdge(
    StateNode source,
    PlayerContextIndex playerContext,
    TransientBoolNode counterStrafeGood,
    TransientBoolNode counterStrafeAdmitted,
    TransientBoolNode isFirstBullet,
    TransientValueNode<double> sprayResidualPitch,
    TransientValueNode<double> sprayResidualYaw,
    TransientBoolNode sprayResidualMeasured,
    TransientValueNode<int> ticksSinceSpot,
    TransientValueNode<double> travelFromSpotDeg,
    TransientValueNode<double> flickErrorDeg,
    TransientBoolNode isFirstAfterSpot,
    TransientValueNode<double> sprayResidualDeg,
    TransientValueNode<int> ticksSinceOnTarget,
    TransientBoolNode isFirstAfterOnTarget,
    Type messageType,
    AimShotContextSources? sources = null) : StateEdge(source)
{
    /// <summary>
    ///     Fraction of <c>m_flMaxspeed</c> at or above which the engine starts charging movement
    ///     inaccuracy. Not a tuning knob: it is the constant in the weapon accuracy code, which is
    ///     why a shot below it is "good" in the same sense the server means it, and why
    ///     <c>bullet_damage.InaccuracyMove == 0</c> is the same statement.
    /// </summary>
    public const float CounterStrafeSpeedFraction = 0.34f;

    /// <summary>
    ///     <c>sv_friction</c>, CS2's shipped default, help string "World friction." A grounded player
    ///     with no movement input loses this share of their speed per second, charged once per tick.
    ///     Named here rather than inlined because <see cref="CounterStrafeLookbackSeconds" /> is
    ///     derived from it, and a derivation whose inputs are literals buried in an expression is not
    ///     one anyone can check.
    /// </summary>
    public const double GroundFrictionPerSecond = 5.2;

    /// <summary>
    ///     <c>sv_stopspeed</c>, CS2's shipped default, help string "Minimum stopping speed when on
    ///     ground." Below this speed the friction drop is charged as though the player were travelling
    ///     at it, which makes the last stretch of a stop linear instead of exponential and is the only
    ///     reason a player reaches rest in finite time at all. The second input to
    ///     <see cref="CounterStrafeLookbackSeconds" />.
    /// </summary>
    public const double GroundStopSpeed = 80.0;

    /// <summary>
    ///     The largest <c>m_flMaxspeed</c> a player carries on foot, which is the knife's. The
    ///     movement-inaccuracy line is a fraction of the HELD weapon's cap, and the time to stop from
    ///     that line grows with it, so the slowest stop in the game is the one that starts from the
    ///     highest line and <see cref="CounterStrafeLookbackSeconds" /> is sized for that one.
    /// </summary>
    public const double FastestMovementCap = 250.0;

    /// <summary>
    ///     <c>enrich.shot.ticks_since_spot</c> when this player has not spotted an enemy yet this
    ///     round. Deliberately a huge number rather than 0 or -1: a bound is written as
    ///     <c>ticks_since_spot &lt;= N</c>, so the sentinel has to FAIL that test. Zero would read as
    ///     "spotted on this very tick" and admit every unspotted shot into the population, which is
    ///     the failure the bound exists to prevent.
    /// </summary>
    public const int NoSpotSentinel = 1_000_000;

    /// <summary>
    ///     <c>enrich.shot.travel_from_spot_deg</c> when there is no contact to measure from, or no
    ///     view angle to measure with. Negative because a travel is an unsigned angle in [0, 180],
    ///     so a consumer gates on <c>&gt;= 0</c> and no real measurement can be mistaken for the
    ///     sentinel. Zero would read as a perfect, already-on-target contact.
    /// </summary>
    public const double NoTravelSentinel = -1.0;

    /// <summary>
    ///     <c>enrich.shot.flick_error_deg</c> when there is nothing to measure. A DIFFERENT sentinel
    ///     from <see cref="NoTravelSentinel" /> because flick error is SIGNED: a real undershoot is
    ///     negative, and -1 would be a perfectly ordinary one-degree undershoot rather than an
    ///     absence. Travel is unsigned in [0, 180] and its error therefore cannot go below -180, so
    ///     this sits clear of every real value.
    /// </summary>
    public const double NoFlickSentinel = -1000.0;

    /// <summary>
    ///     How far back the admission gate looks for movement, in seconds. A shot only enters the
    ///     counter-strafing population if the player exceeded the threshold inside this window, so
    ///     this value sets the denominator and therefore the metric.
    ///     <para>
    ///         <b>What the window actually measures.</b> <see cref="ResolveMovement" /> admits a shot
    ///         when the PEAK speed over <c>[tick - window, tick]</c> cleared the line, so the window
    ///         is the largest tolerated gap between the last sample ABOVE the line and the shot. It is
    ///         not the length of the deceleration that got the player there: that happened before the
    ///         last above-line sample and so falls outside the window entirely, whatever its length.
    ///         Sizing this from a run-down-to-the-line time would be a correct computation of a
    ///         quantity this gate never sees.
    ///     </para>
    ///     <para>
    ///         <b>Derived, not fitted.</b> What has to fit inside the window is the REST of the stop.
    ///         A player who has just dropped below the line is still carrying velocity from that
    ///         movement, and a shot can still be the end of it until the velocity is gone; after that
    ///         the player was standing still, which is a different thing from counter-strafing and
    ///         inflates the ratio without anyone doing it better. Ground friction with no movement
    ///         input is the SLOWEST way that residual velocity can die (a real counter-strafe adds
    ///         counter-input and gets there sooner), so the time friction alone needs to carry a
    ///         player from the line to rest is the widest gap a genuine stop can produce, and that is
    ///         this window. See <see cref="SecondsToStopFromTheInaccuracyLine" />.
    ///     </para>
    ///     <para>
    ///         <b>Which weapon.</b> The line is a fraction of the held cap and the stop time rises
    ///         with the line, so one window sized for <see cref="FastestMovementCap" /> covers every
    ///         weapon: an AWP's 68 u/s line needs 10.5 ticks at 64-tick and a Negev's 51 needs 7.9,
    ///         against the 13 this admits. It also covers the case a starting-speed argument would
    ///         miss, a player who ran with a knife out and fires holding an AWP, because the interval
    ///         depends only on the line at the SHOT tick, which is the same tick
    ///         <see cref="ResolveMovement" /> reads <c>m_flMaxspeed</c> on.
    ///     </para>
    ///     <para>
    ///         <b>The rounding.</b> This is seconds and the gate compares whole ticks, so the knife's
    ///         13.054 ticks at 64-tick is admitted as 13 and the last 0.054 of a tick is lost. Half a
    ///         tick either way is the most that conversion can ever cost, it is below the sampling
    ///         resolution the speed column has in the first place, and rounding rather than taking the
    ///         ceiling keeps the window from drifting wide at every tick rate.
    ///     </para>
    ///     <para>
    ///         <b>Why not fitted to our own data.</b> Swept over the benchmark corpus the admitted
    ///         share and CS% both rise monotonically with the window, with no knee and no stationary
    ///         point anywhere, so every window read off that curve is a choice about how flattering
    ///         the ratio should be rather than a measurement. The sweep lives in
    ///         <c>CounterStrafeWindowDerivationTests</c>. The physics has an answer where the data
    ///         has only a preference.
    ///     </para>
    ///     <para>
    ///         <b>Not a <c>const</c>.</b> It is an expression over the movement constants, so it
    ///         cannot be one; a downstream assembly that had inlined the old literal would keep it,
    ///         and it cannot appear in a <c>const</c> expression, an attribute argument or a
    ///         <c>case</c> label.
    ///     </para>
    /// </summary>
    public static readonly double CounterStrafeLookbackSeconds =
        SecondsToStopFromTheInaccuracyLine(FastestMovementCap);

    /// <summary>Recoil index at or below which a shot counts as the first bullet out of the barrel.</summary>
    public const float FirstBulletRecoilEpsilon = 0.01f;

    /// <summary>
    ///     Largest normalised aim-punch component, in degrees, that is taken as an aim punch at all.
    ///     Generous by design: recoil kick is a couple of degrees and the largest punch in the game
    ///     (taking heavy damage) is well inside this, so nothing real is rejected. It exists to reject
    ///     the UNREAL, see <see cref="PlausiblePunch" /> for what was measured and why that matters.
    /// </summary>
    public const double MaxPlausibleAimPunchDegrees = 45.0;

    /// <summary>
    ///     <c>bullet_damage.InaccuracyMove</c> at or below which the server charged no movement
    ///     penalty. Compared with a tolerance rather than against zero because it arrives as a float
    ///     the server computed, not as a flag.
    /// </summary>
    public const float ServerMovementPenaltyEpsilon = 1e-4f;

    /// <summary>
    ///     Tolerance multiplier on a spray run's measured cycle time. The engine's own rule for
    ///     "the recoil index has not decayed yet" is that the gap since the last shot is within the
    ///     weapon's cycle time; 1.10 absorbs the tick quantisation of a gap measured in whole ticks
    ///     without opening the window wide enough to weld two deliberate taps into one spray.
    /// </summary>
    public const double SprayCycleTolerance = 1.10;

    /// <summary>
    ///     Widest gap, in SECONDS, that may OPEN a spray run, used only until the run's own second
    ///     shot measures its weapon's cycle time. 250 ms covers the slowest repeat-fire cycle in CS2
    ///     (the revolver's), so no weapon's genuine second shot is rejected for being slow. After
    ///     that the measured cycle time is a far tighter bound.
    ///     <para>
    ///         <b>Seconds, not ticks.</b> What it bounds is a weapon's cycle time, which is a
    ///         duration the server owns and not a count the demo's rate can change. Held as 16 ticks
    ///         it was 250 ms on a 64-tick demo and 125 ms on a 128-tick one, and the shot this gate
    ///         decides is the run's SECOND — the one that has not yet measured
    ///         <see cref="AimShotState.RunGapBoundTicks" /> and therefore has nothing tighter to fall
    ///         back on. Every weapon slower than 125 ms per shot (the pistols, the Deagle, the
    ///         autosnipers) would open a fresh run on every trigger pull at 128-tick, which reports
    ///         <c>is_first_bullet</c> on all of them and <c>spray_residual_measured</c> on none.
    ///         <see cref="_lookbackTicks" /> already converts at the demo's own rate; this now does
    ///         the same.
    ///     </para>
    /// </summary>
    public const double SprayOpenGapSeconds = 0.25;

    /// <summary>
    ///     <see cref="SprayOpenGapSeconds" /> rendered at 64 ticks per second, which is what the gate
    ///     comes to on every demo in the benchmark corpus. Kept because this assembly ships as a
    ///     package and a <c>const</c> cannot be removed without breaking a consumer that inlined it;
    ///     the edge itself converts the seconds at the demo's own rate rather than reading this.
    /// </summary>
    public const int SprayOpenGapTicks = (int)(SprayOpenGapSeconds * 64.0);

    /// <summary>
    ///     Float-noise tolerance on the recoil-monotonicity test that decides whether a shot
    ///     continues the current spray run. Matches <see cref="ShotEnrichmentEdge.SprayRecoilEpsilon" />,
    ///     which applies the same rule to the landed-shot stream.
    /// </summary>
    public const float SprayRecoilEpsilon = 0.25f;

    /// <summary>
    ///     <c>weapon_recoil_scale</c>: what the engine multiplies the aim-punch angle by when it
    ///     resolves where a bullet actually goes. Effective aim is view angle plus this times punch.
    /// </summary>
    public const double WeaponRecoilScale = 2.0;

    /// <summary>
    ///     The per-player providers this edge reads, by name, BEYOND the six
    ///     <see cref="AimVantageScanner.RequiredProviders" /> the vantage sampler needs. A caller
    ///     wiring the edge must gate both sets into the digest: they are reference-gated by name and
    ///     a ruleset that triggers on the <c>shot</c> view names none of them, so nothing forces
    ///     them in on its own.
    /// </summary>
    public static IReadOnlyList<string> RequiredProviders { get; } =
    [
        "entity.pawn.max_speed",
        "entity.weapon.recoil_index",
        AimVantageScanner.EyePitchProvider,
        AimVantageScanner.EyeYawProvider,
        "entity.pawn.punch_pitch",
        "entity.pawn.punch_yaw"
    ];

    // The stopping window in ticks at this demo's rate: 13 at 64-tick, 26 at 128. Held rather than
    // recomputed because it is compared against a tick gap on every shot in the demo.
    private readonly int _lookbackTicks = (int)Math.Round(
        CounterStrafeLookbackSeconds * (sources is { TickRate: > 0 } s ? s.TickRate : 64.0));

    // The run-opening gap in ticks at this demo's rate: 16 at 64-tick, 32 at 128. Same conversion as
    // _lookbackTicks above, because it is the same kind of quantity — a duration the server owns,
    // not a count the demo's rate is free to reinterpret. Floored at one tick so a pathologically
    // low rate cannot produce a bound no gap can satisfy.
    private readonly int _openGapTicks = Math.Max(1, (int)Math.Round(
        SprayOpenGapSeconds * (sources is { TickRate: > 0 } g ? g.TickRate : 64.0)));

    // Which of the two arms this instance is. Cached rather than compared per access because the
    // per-arm answer latches below are read on every shot in the demo.
    private readonly bool _isLandedArm = messageType == typeof(BulletDamageEvent);

    /// <inheritdoc />
    // BOTH arms declare the same set, including the residuals: each measures spray control from its
    // own anchor (see the class doc), so both write them and both must register them as transients
    // under their dispatch key. An arm that emitted a node without declaring it would leave that
    // node unreset between fires, and a consumer would read the previous shot's number.
    public override IReadOnlyList<StateNode>? AdditionalWrittenNodes =>
    [
        counterStrafeAdmitted, isFirstBullet, ticksSinceSpot, travelFromSpotDeg, flickErrorDeg,
        isFirstAfterSpot, sprayResidualPitch, sprayResidualYaw, sprayResidualMeasured,
        sprayResidualDeg, ticksSinceOnTarget, isFirstAfterOnTarget
    ];

    /// <inheritdoc />
    public override EdgeEffect? DeclaredEffect => EdgeEffect.SetValue;

    /// <inheritdoc />
    public override Type MessageType => messageType;

    /// <inheritdoc />
    public override StateNode? WrittenNode => counterStrafeGood;

    /// <inheritdoc />
    public override bool TryApply(EvaluationContext context) => false;

    /// <inheritdoc />
    public override bool TryApplyDirect(object payload, EvaluationContext context)
    {
        // Null sources is the ordinary case, not a fault: an analysis whose rules never read a
        // shot-anchored enrichment gates the six aim providers (and the vantage sampler that needs
        // them) out entirely, and this edge stays registered but inert.
        if (sources is null)
        {
            return false;
        }

        return payload switch
        {
            WeaponFireEvent fire => ApplyFired(fire, context),
            BulletDamageEvent landed => ApplyLanded(landed, context),
            _ => false
        };
    }

    /// <summary>
    ///     Normalises an angle in degrees to (-180, 180]. Two callers, same arithmetic: a yaw
    ///     DIFFERENCE across a spray that crosses the half turn, which would otherwise read as a
    ///     catastrophic loss of control on a shot the player never moved for, and a raw
    ///     <c>QAngle</c> COMPONENT off the wire, which arrives over [0, 360) so a real -2 degree
    ///     kick shows up as 358.
    /// </summary>
    /// <param name="degrees">The angle to normalise.</param>
    public static double WrapDegrees(double degrees)
    {
        double wrapped = degrees % 360.0;
        if (wrapped > 180.0)
        {
            wrapped -= 360.0;
        }
        else if (wrapped <= -180.0)
        {
            wrapped += 360.0;
        }

        return wrapped;
    }

    /// <summary>
    ///     How long CS2's ground friction needs to carry a player from the movement-inaccuracy line
    ///     for <paramref name="movementCap" /> down to a standstill, with no movement input at all.
    ///     This is the interval the admission gate tolerates between the last sample above the line
    ///     and the shot, and therefore the derivation behind
    ///     <see cref="CounterStrafeLookbackSeconds" />.
    ///     <para>
    ///         Two stretches, because CS2 charges the friction drop on
    ///         <see cref="GroundStopSpeed" /> rather than the real speed once the player is slower
    ///         than it. Above that speed the drop is a fixed share of the current speed, so speed
    ///         decays as <c>exp(-friction * t)</c> and the stretch down to <c>sv_stopspeed</c> takes
    ///         <c>ln(line / stopspeed) / friction</c>. At and below it the drop is the constant
    ///         <c>stopspeed * friction</c> per second, so the remaining <c>min(line, stopspeed)</c>
    ///         takes <c>min(line, stopspeed) / (stopspeed * friction)</c>. Without the floor the
    ///         second stretch would never end and there would be no window to derive.
    ///     </para>
    ///     <para>
    ///         Public so the derivation can be evaluated at an input other than the one that ships:
    ///         a formula only its own author can run is an assertion, and
    ///         <c>CounterStrafeWindowDerivationTests</c> checks this against a tick-by-tick
    ///         simulation of the same movement model across the whole weapon range.
    ///     </para>
    /// </summary>
    /// <param name="movementCap"><c>m_flMaxspeed</c> for the weapon held, in units per second.</param>
    /// <returns>Seconds, or zero for a non-positive cap.</returns>
    public static double SecondsToStopFromTheInaccuracyLine(double movementCap)
    {
        if (movementCap <= 0.0)
        {
            return 0.0;
        }

        double line = movementCap * CounterStrafeSpeedFraction;
        double exponentialStretch = line > GroundStopSpeed
            ? Math.Log(line / GroundStopSpeed) / GroundFrictionPerSecond
            : 0.0;
        double linearStretch = Math.Min(line, GroundStopSpeed)
                               / (GroundStopSpeed * GroundFrictionPerSecond);
        return exponentialStretch + linearStretch;
    }

    // The server's own account of a shot that landed. Refines the record the fired arm latched for
    // the same (slot, tick) and re-emits the movement and first-bullet verdicts from values that
    // needed no reconstruction: InaccuracyMove is the movement penalty the engine actually charged,
    // and RecoilIndex is the index it actually fired at.
    //
    // The effective aim here is ShootAng* as it arrives, with no punch folded in, which is an
    // assumption worth naming: it holds only if ShootAng is the RESOLVED firing direction rather than
    // the aim before punch. Nothing in this repo pins that down, and the only demos that carry
    // bullet_damage at all are matchmaking ones (the bundled pro GOTV sample emits none), so it stays
    // unverified. It is load-bearing, because the residuals this arm emits are differences of
    // ShootAng*: were ShootAng the pre-punch aim, they would be a view-angle residual under another
    // name and would score a player higher the less they pulled down.
    private bool ApplyLanded(BulletDamageEvent landed, EvaluationContext context)
    {
        int slot = landed.Attacker;
        if (slot < 0 || !playerContext.TryGet(slot, out PlayerContextIndex.PlayerContext? ctx))
        {
            return false;
        }

        int tick = context.Frame.ServerTick;
        AimShotState state = ctx!.AimShot;
        AimShotContext? fired = state.Last;

        // Same shot means same shooter and same tick. A landed shot with no matching fired record
        // (a demo whose weapon_fire stream is absent, or a shot fired before this player's columns
        // materialised) still gets a record: the server half stands on its own, and the derived half
        // is simply marked unmeasured rather than filled with the previous shot's numbers.
        bool sameShot = fired is not null && fired.Slot == slot && fired.Tick == tick;
        Movement movement = ResolveMovement(slot, tick);

        AimShotContext refined = new(
            slot,
            tick,
            sameShot ? fired!.Speed2D : movement.Speed,
            sameShot ? fired!.CounterStrafeThreshold : movement.Threshold,
            sameShot ? fired!.HasMovementSample : movement.HasSample,
            movement.Admitted,
            landed.RecoilIndex,
            sameShot ? fired!.EnemyVisible : EnemyVisible(slot),
            landed.ShootAngX,
            landed.ShootAngY,
            landed.InaccuracyMove);

        state.Last = refined;
        Emit(counterStrafeGood, refined.CounterStrafeGood);
        Emit(counterStrafeAdmitted, refined.AboveThresholdInLookback);
        Emit(isFirstBullet, landed.RecoilIndex <= FirstBulletRecoilEpsilon);

        // Spray control, measured HERE rather than on the fired arm, because this is the only arm
        // whose effective aim is trustworthy. ShootAng is the direction the bullet actually left in,
        // punch already folded in by the server; the entity punch column the fired arm would need
        // does not decode to degrees (probed against this same event: mean |column| 68.4 deg against
        // a true mean of 1.3, with a constant 272.6 elocity\ on every shot).
        //
        // The residual is therefore the drift of the SHOT DIRECTION from the run's first shot, which
        // is what spray control means: perfect compensation holds the bullets on the anchor and the
        // residual stays near zero. Measuring the view angle instead would score a player HIGHER the
        // less they pulled down.
        // This arm does NOT advance the run: the fired arm sees every trigger pull and owns
        // segmentation, while a spray's landed bullets are a subsequence of it. Advancing here too
        // would count every shot that both fired and landed twice.
        //
        // It keeps its own anchor because the fired arm's is built from the entity punch column,
        // which does not decode to degrees (probed against this event: mean |column| 68.4 deg
        // against a true mean of 1.3). The first landed bullet of a run anchors it; the rest measure
        // their drift from that.
        bool landedMeasured = state.HasLandedAnchor;
        if (!landedMeasured)
        {
            state.LandedAnchorPitch = landed.ShootAngX;
            state.LandedAnchorYaw = landed.ShootAngY;
            state.HasLandedAnchor = true;
        }

        Emit(sprayResidualMeasured, landedMeasured);
        sprayResidualPitch.SetValue(
            landedMeasured ? landed.ShootAngX - state.LandedAnchorPitch : 0.0);
        sprayResidualYaw.SetValue(
            landedMeasured ? WrapDegrees(landed.ShootAngY - state.LandedAnchorYaw) : 0.0);

        // The headline number: ONE angular distance between where this bullet went and where the
        // run's first one did. A true 3D angle rather than the components or their Euclidean sum,
        // which only agree with it for small deviations and diverge exactly where a spray goes
        // wrong. Pitch and yaw stay available for the per-shot scatter a visualiser needs; an
        // aggregate wants the distance.
        sprayResidualDeg.SetValue(landedMeasured
            ? ShotEnrichmentEdge.AngleDeltaDegrees(
                (float)state.LandedAnchorPitch, (float)state.LandedAnchorYaw,
                landed.ShootAngX, landed.ShootAngY)
            : 0.0);

        // The landed arm reports its own pairing. It is a SEPARATE method from the fired arm, so an
        // emission added only there leaves every shot_landed consumer sitting on the sentinel, which
        // renders as a confident 0.0 rather than an empty population. ShootAng is the server's own
        // view angle at the shot, which beats the provider read: no pre-frame-snapshot lag.
        EmitSpotPairing(ctx, tick, landed.ShootAngX, landed.ShootAngY);
        return true;
    }

    // Every shot the player took, landed or not. This arm owns the spray run and the record.
    private bool ApplyFired(WeaponFireEvent fire, EvaluationContext context)
    {
        int slot = fire.UserId;
        if (slot < 0 || !playerContext.TryGet(slot, out PlayerContextIndex.PlayerContext? ctx))
        {
            return false;
        }

        // Grenades, knives and the taser fire weapon_fire too. They carry no recoil index and no
        // spray, so letting them through would break a run in half every time a player threw a
        // flash mid-fight. Same classification the `bullet` facet exposes, called directly rather
        // than read off that transient: the two edges sit in one dispatch slot and reading a
        // sibling's output would make this depend on their ordering.
        if (!WeaponClassification.IsBulletWeapon(fire.Weapon))
        {
            return false;
        }

        AimShotContextSources src = sources!;
        int tick = context.Frame.ServerTick;
        AimShotState state = ctx!.AimShot;

        Movement movement = ResolveMovement(slot, tick);

        // Pre-frame, not current: the frame's entity updates have already been applied by the time
        // this event dispatches, so the live recoil index and eye angle already carry THIS shot's
        // kick. Frame-start state is the aim the player was holding when they pulled the trigger.
        float? recoil = ReadFloat(src.RecoilIndex, slot);
        float? eyePitch = ReadFloat(src.EyePitch, slot);
        float? eyeYaw = ReadFloat(src.EyeYaw, slot);
        double? punchPitch = PlausiblePunch(ReadFloat(src.PunchPitch, slot));
        double? punchYaw = PlausiblePunch(ReadFloat(src.PunchYaw, slot));
        // Two gates, not one. The residual needs an effective aim and therefore a readable punch;
        // the spot pairing needs only where the player POINTED. Folding them together made a
        // crosshair-placement measurement that never touches the punch column unreachable whenever
        // that column failed to decode, which is its state on the bundled GOTV sample and is a fact
        // about the punch decode rather than about crosshair placement.
        bool haveView = eyePitch.HasValue && eyeYaw.HasValue;
        bool haveAim = haveView && punchPitch.HasValue && punchYaw.HasValue;

        double effectivePitch = haveAim ? eyePitch!.Value + (WeaponRecoilScale * punchPitch!.Value) : 0.0;
        double effectiveYaw = haveAim ? eyeYaw!.Value + (WeaponRecoilScale * punchYaw!.Value) : 0.0;

        bool opensRun = AdvanceSprayRun(
            state, tick, recoil, effectivePitch, effectiveYaw, haveAim, _openGapTicks);

        AimShotContext shot = new(
            slot, tick, movement.Speed, movement.Threshold, movement.HasSample, movement.Admitted,
            recoil ?? -1f, EnemyVisible(slot), effectivePitch, effectiveYaw, null);
        state.Last = shot;

        Emit(counterStrafeGood, shot.CounterStrafeGood);
        Emit(counterStrafeAdmitted, movement.Admitted);
        Emit(isFirstBullet, opensRun);

        // The residual is a deviation FROM the run's anchor, so the anchor shot has none by
        // construction and a run whose aim never resolved has none at all. Both come out as 0, which
        // is indistinguishable from perfect control, and that is what the companion `measured` bool
        // exists to separate: a spray-control metric gates its denominator on it exactly as a
        // counter-strafe metric gates on `admitted`.
        bool measured = haveAim && state.HasAnchor && !opensRun;
        Emit(sprayResidualMeasured, measured);
        sprayResidualPitch.SetValue(measured ? effectivePitch - state.AnchorPitch : 0.0);
        sprayResidualYaw.SetValue(measured ? WrapDegrees(effectiveYaw - state.AnchorYaw) : 0.0);
        sprayResidualDeg.SetValue(measured
            ? ShotEnrichmentEdge.AngleDeltaDegrees(
                (float)state.AnchorPitch, (float)state.AnchorYaw,
                (float)effectivePitch, (float)effectiveYaw)
            : 0.0);

        // A new run invalidates the landed arm's anchor too, so the two arms segment the same runs
        // even though only this one advances them.
        if (opensRun)
        {
            state.HasLandedAnchor = false;
        }

        // Ticks from this player's most recent enemy-spot to this shot, so a consumer can BOUND a
        // spot-to-shot pairing to one engagement. Without it the only available pairing is "the
        // round's first contact against the round's first landed shot", which on a round where the
        // two belong to different duels measures the angle between unrelated instants: the defect
        // that put 142 degrees in a crosshair-placement column whose real ceiling is about 20.
        //
        // haveView, not haveAim: EmitSpotPairing measures the raw view angle and deliberately leaves
        // recoil out of it, so requiring a readable aim punch would gate it on a column it never
        // reads. EmitSpotPairing applies the view gate itself, which is why the nullables go through
        // unwrapped.
        EmitSpotPairing(ctx, tick, haveView ? eyePitch : null, haveView ? eyeYaw : null);
        return true;
    }

    /// <summary>
    ///     Normalises one networked aim-punch component to (-180, 180] and returns it only when it
    ///     is physically an aim punch. Null means "this column did not give an aim punch", which is
    ///     NOT the same as zero punch and must not be folded into an effective aim.
    ///     <para>
    ///         Normalisation first, because a <c>QAngle</c> is networked over [0, 360): a real -2
    ///         degree kick arrives as 358 and would fail any magnitude test taken on the raw value.
    ///     </para>
    ///     <para>
    ///         <b>Why the test exists at all.</b> <c>m_predictableBaseAngle</c> is a damped-spring
    ///         sample, not a resolved punch, and its decode is not settled (see
    ///         <c>PawnAimPunchProvider</c>). Probed on the bundled GOTV sample it comes back clustered
    ///         near 266 and 89 degrees, i.e. around -94 and +89 after normalisation, with the spring
    ///         velocity near -88 deg/s. No weapon kicks a player's aim 94 degrees. Folding that into
    ///         an effective aim produces residuals of several hundred degrees, and an RMS over them
    ///         is not a wrong spray-control number, it is a number with no relationship to spray
    ///         control at all. Rejecting is the only reading that does not manufacture data.
    ///     </para>
    /// </summary>
    /// <param name="raw">The raw component from the digest, or null when the column read nothing.</param>
    private static double? PlausiblePunch(float? raw)
    {
        if (raw is not { } value)
        {
            return null;
        }

        double normalised = WrapDegrees(value);
        return Math.Abs(normalised) <= MaxPlausibleAimPunchDegrees ? normalised : null;
    }

    /// <summary>
    ///     Writes the three spot-anchored outputs for one shot: the interval back to the contact it
    ///     answered, the crosshair travel from that contact, and the signed error against how far
    ///     off the contact actually was.
    ///     <para>
    ///         <b>The clock is the FRAME tick</b>, which is what both arms already resolve and what
    ///         <c>PlayerContext.LastSpotTick</c> is latched in. Reading the event's own
    ///         <c>GameTick</c> here instead compares two different clocks, and the guard below then
    ///         rejects every shot and leaves all three on their sentinels: no error, just three
    ///         columns of zero.
    ///     </para>
    /// </summary>
    /// <param name="ctx">The shooter's context, carrying the latched contact.</param>
    /// <param name="tick">The shot's frame-clock tick.</param>
    /// <param name="pitch">View pitch at the shot, or null when unavailable.</param>
    /// <param name="yaw">View yaw at the shot, or null when unavailable.</param>
    private void EmitSpotPairing(
        PlayerContextIndex.PlayerContext ctx, int tick, float? pitch, float? yaw)
    {
        bool haveSpot = ctx.LastSpotTick >= 0 && tick >= ctx.LastSpotTick;
        ticksSinceSpot.SetValue(haveSpot ? tick - ctx.LastSpotTick : NoSpotSentinel);

        // Aimed reaction: from the crosshair ARRIVING on an enemy to this shot. Distinct from the
        // spot interval, which also contains the turn onto the target: separating them is what makes
        // one number a reaction and the other a reaction plus aim travel.
        int since = sources?.Visibility?.OnTargetSince(ctx.Slot) ?? -1;
        bool haveOnTarget = since >= 0 && tick >= since;
        ticksSinceOnTarget.SetValue(haveOnTarget ? tick - since : NoSpotSentinel);

        // First shot of THIS acquisition, compared by the acquisition's own tick rather than a flag,
        // so a second burst after the crosshair left and returned counts again. Latched per ARM for
        // the same reason the spot answer below is: one shared latch never survives to the landed
        // arm. See PlayerContextIndex.PlayerContext.AnsweredOnTargetSinceLanded.
        int answeredOnTarget = _isLandedArm
            ? ctx.AnsweredOnTargetSinceLanded
            : ctx.AnsweredOnTargetSinceShot;
        bool firstOnTarget = haveOnTarget && answeredOnTarget != since;
        Emit(isFirstAfterOnTarget, firstOnTarget);
        if (firstOnTarget)
        {
            if (_isLandedArm)
            {
                ctx.AnsweredOnTargetSinceLanded = since;
            }
            else
            {
                ctx.AnsweredOnTargetSinceShot = since;
            }
        }

        // Is this the first shot answering the current contact, on THIS arm? The timing metrics
        // want that one interval; every later shot in the same engagement measures the spray, not
        // the reaction. Latched per arm so a contact answered by a miss is still unanswered for
        // time-to-damage.
        bool firstAnswer = haveSpot
                           && !(_isLandedArm ? ctx.SpotAnsweredByLanded : ctx.SpotAnsweredByShot);
        Emit(isFirstAfterSpot, firstAnswer);
        if (firstAnswer)
        {
            if (_isLandedArm)
            {
                ctx.SpotAnsweredByLanded = true;
            }
            else
            {
                ctx.SpotAnsweredByShot = true;
            }
        }

        // A true 3D angle between the two view directions, not an L1 sum of pitch and yaw deltas,
        // so it needs no yaw-wraparound special case and carries no positive bias. Raw view angle
        // rather than effective aim: this measures where the player POINTED, and folding recoil in
        // would charge them for the weapon's kick.
        bool haveTravel = haveSpot && pitch.HasValue && yaw.HasValue;
        double travel = haveTravel
            ? ShotEnrichmentEdge.AngleDeltaDegrees(
                ctx.LastSpotPitch, ctx.LastSpotYaw, pitch!.Value, yaw!.Value)
            : NoTravelSentinel;
        travelFromSpotDeg.SetValue(travel);

        // Signed: positive is an overshoot walked back, negative an undershoot dragged on (or a
        // target that walked into a held crosshair), zero one clean correction. Both terms are
        // measured from the SAME contact, which is what makes the difference meaningful.
        flickErrorDeg.SetValue(haveTravel ? travel - ctx.LastSpotChestAngle : NoFlickSentinel);
    }

    private static void Emit(TransientBoolNode node, bool value)
    {
        if (value)
        {
            node.Activate();
        }
        else
        {
            node.Deactivate();
        }
    }

    /// <summary>
    ///     Folds this shot into the shooter's spray run and returns whether it OPENED a new one.
    ///     <para>
    ///         The segmentation rule is the engine's, not the "three or more shots" convention the
    ///         analytics sites use: a spray is a maximal run over which the recoil index never
    ///         decays, which is exactly the run over which the recoil pattern is a single continuous
    ///         curve. A shot count is a proxy for that and gets bursts wrong in both directions.
    ///     </para>
    ///     <para>
    ///         Two shots at the same tick (a frame carrying several server ticks, or the second
    ///         barrel of a burst) continue the run on a zero gap. That is deliberate: they share one
    ///         pre-frame snapshot, so their recoil readings are identical and only the gap bound can
    ///         separate them, and separating them would restart the run mid-spray.
    ///     </para>
    /// </summary>
    private static bool AdvanceSprayRun(
        AimShotState state, int tick, float? recoil, double effectivePitch, double effectiveYaw,
        bool haveAim, int openGapTicks)
    {
        int gap = tick - state.LastShotTick;
        int gapBound = state.RunGapBoundTicks > 0 ? state.RunGapBoundTicks : openGapTicks;

        // No recoil column is not evidence of a new run, so the gap decides alone. Reading a
        // missing column as "recoil dropped" would split every spray into single-shot runs and make
        // every shot look like a first bullet.
        bool continues = state.ShotsInRun > 0
                         && state.LastShotTick >= 0
                         && gap >= 0
                         && gap <= gapBound
                         && (recoil is null || recoil.Value >= state.LastRecoil - SprayRecoilEpsilon);

        if (continues)
        {
            state.ShotsInRun++;

            // The run's first non-zero gap IS the weapon's cycle time (continuous fire has no other
            // spacing), so the run measures its own bound instead of carrying a table of per-weapon
            // cycle times that would have to be kept in step with every balance patch.
            if (state.RunGapBoundTicks == 0 && gap > 0)
            {
                state.RunGapBoundTicks = Math.Max(1, (int)Math.Ceiling(gap * SprayCycleTolerance));
            }
        }
        else
        {
            state.ShotsInRun = 1;
            state.RunGapBoundTicks = 0;
            state.AnchorPitch = effectivePitch;
            state.AnchorYaw = effectiveYaw;
            state.HasAnchor = haveAim;
        }

        state.LastShotTick = tick;
        if (recoil is { } value)
        {
            state.LastRecoil = value;
        }

        return !continues;
    }

    private bool EnemyVisible(int slot) => sources!.Visibility?.IsAnyEnemyVisibleTo(slot) ?? false;

    private float? ReadFloat(IPerPlayerEntityValueProvider provider, int slot) =>
        sources!.Scanner.GetPreFrameValue(provider, slot) switch
        {
            float f => f,
            double d => (float)d,
            int i => i,
            _ => null
        };

    /// <summary>
    ///     Resolves the movement half of a shot: the speed at the shot, the per-weapon threshold,
    ///     whether either was actually measured, and whether the shot is ADMITTED into the
    ///     counter-strafing population (the player exceeded the threshold at some sampled tick in the
    ///     lookback window, the shot's own tick included). Every unmeasured case returns
    ///     <c>HasSample</c> and <c>Admitted</c> false, which drops the shot from the denominator
    ///     rather than admitting it on zeros that read as a perfectly still player.
    ///     <para>
    ///         <b>Admission is conditioned on the shot's own sample.</b> The two questions are
    ///         answered from the same speed ring but over different windows, and the wide one can
    ///         hold a sample while the shot's own tick holds none — the frame the shot landed on was
    ///         skipped by the vantage sampler (a compromised decode, or the frozen pre-frame
    ///         snapshot), while the ticks before it were not. Answering them independently admits
    ///         that shot with <c>HasSample</c> false, and
    ///         <see cref="AimShotContext.CounterStrafeGood" /> reads a missing sample as not-good, so
    ///         the shot enters the denominator AND the moving count on the strength of a measurement
    ///         that was never taken. There is no verdict without a sample at the shot, so there is no
    ///         admission either.
    ///     </para>
    /// </summary>
    private Movement ResolveMovement(int slot, int tick)
    {
        AimShotContextSources src = sources!;
        if (ReadFloat(src.MaxSpeed, slot) is not { } maxSpeed || maxSpeed <= 0f)
        {
            return default;
        }

        if (!src.Vantage.TryPeakSpeed(slot, tick, tick, out float speedAtShot))
        {
            return default;
        }

        float threshold = maxSpeed * CounterStrafeSpeedFraction;
        bool admitted = src.Vantage.TryPeakSpeed(slot, tick - _lookbackTicks, tick, out float peak)
                        && peak > threshold;
        return new Movement(speedAtShot, threshold, true, admitted);
    }

    /// <summary>The movement half of one shot, resolved once per shot by <see cref="ResolveMovement" />.</summary>
    /// <param name="Speed">Derived 2D speed at the shot's tick, or 0 when unsampled.</param>
    /// <param name="Threshold"><c>0.34 * m_flMaxspeed</c>, or 0 when the max-speed column was unreadable.</param>
    /// <param name="HasSample">Whether <paramref name="Speed" /> and <paramref name="Threshold" /> are measurements.</param>
    /// <param name="Admitted">Whether the shot enters the counter-strafing denominator.</param>
    private readonly record struct Movement(float Speed, float Threshold, bool HasSample, bool Admitted);
}

/// <summary>
///     The shooter's spray run and the last <see cref="AimShotContext" /> built for them, held on
///     their <see cref="PlayerContextIndex.PlayerContext" /> for the length of a round. Grouped into
///     one object rather than flattened into seven more properties on <c>PlayerContext</c> because
///     they share one lifetime and one reset: the failure this guards against is a new field being
///     added here and quietly left out of <c>ResetRoundState</c>, which would carry a spray run
///     across a round boundary and merge two unrelated engagements into one measurement.
/// </summary>
public sealed class AimShotState
{
    /// <summary>Effective aim pitch of the run's first shot, the residual's zero point.</summary>
    public double AnchorPitch { get; set; }

    /// <summary>Effective aim yaw of the run's first shot.</summary>
    public double AnchorYaw { get; set; }

    /// <summary>
    ///     Whether the anchor was built from a readable view angle. False means the run opened while
    ///     the eye-angle columns were unavailable, and every residual in it stays zero rather than
    ///     being measured against a fabricated origin.
    /// </summary>
    public bool HasAnchor { get; set; }

    /// <summary>Whether <see cref="LandedAnchorPitch" /> holds a run anchor from a landed bullet.</summary>
    public bool HasLandedAnchor { get; set; }

    /// <summary>
    ///     Shot direction of the run's FIRST landed bullet, pitch in degrees. Separate from
    ///     <see cref="AnchorPitch" /> because the two arms build effective aim from different
    ///     sources, and only this one's is trustworthy on a real demo.
    /// </summary>
    public double LandedAnchorPitch { get; set; }

    /// <summary>The yaw half of <see cref="LandedAnchorPitch" />.</summary>
    public double LandedAnchorYaw { get; set; }

    /// <summary>The most recent shot's resolved context, or null before this player's first shot of the round.</summary>
    public AimShotContext? Last { get; set; }

    /// <summary>Recoil index of the last shot, the monotonicity anchor for run continuation.</summary>
    public float LastRecoil { get; set; }

    /// <summary>Server tick of the last shot, or -1 when the player has not fired this round.</summary>
    public int LastShotTick { get; set; } = -1;

    /// <summary>
    ///     Tick bound for continuing the current run, measured from its own first gap
    ///     (<see cref="AimShotContextEdge.SprayCycleTolerance" /> times the weapon's cycle time), or
    ///     0 while the run is still one shot long and the open-gap default applies.
    /// </summary>
    public int RunGapBoundTicks { get; set; }

    /// <summary>Shots in the current uninterrupted run; 0 before the player's first shot of the round.</summary>
    public int ShotsInRun { get; set; }

    /// <summary>
    ///     Clears every field back to its start-of-round value, the landed arm's anchor included.
    ///     <para>
    ///         The landed anchor is cleared HERE and not only where the fired arm clears it, because
    ///         the fired arm cannot be relied on to run. It rejects every weapon
    ///         <see cref="WeaponClassification.IsBulletWeapon" /> excludes, shotguns among them, while
    ///         the landed arm has no such gate; and a demo with no <c>weapon_fire</c> stream at all
    ///         never enters that arm. Left out, the anchor from one round's rifle spray is what the
    ///         next round's bullets measure their residual against, which is a spray-control number
    ///         computed across a round boundary and a different engagement.
    ///     </para>
    /// </summary>
    public void Reset()
    {
        AnchorPitch = 0.0;
        AnchorYaw = 0.0;
        HasAnchor = false;
        HasLandedAnchor = false;
        LandedAnchorPitch = 0.0;
        LandedAnchorYaw = 0.0;
        Last = null;
        LastRecoil = 0f;
        LastShotTick = -1;
        RunGapBoundTicks = 0;
        ShotsInRun = 0;
    }
}
