#region

using CS2DemoKit.Analysis.Edges;
using CS2DemoKit.Analysis.Visibility;

#endregion

namespace CS2DemoKit.Analysis.Tests;

/// <summary>
///     Pins <see cref="AimShotContextEdge.CounterStrafeLookbackSeconds" /> to a derivation instead of
///     a preference.
///     <para>
///         <b>The interval the window governs.</b> <c>ResolveMovement</c> admits a shot when the PEAK
///         speed over <c>[tick - window, tick]</c> cleared the movement-inaccuracy line, so what the
///         window bounds is the gap between the last sample above the line and the shot. It does not
///         bound the deceleration that got the player to the line: that finished before the last
///         above-line sample, outside the window at any width. Sizing the window from a
///         run-down-to-the-line time would compute the wrong quantity correctly, which is exactly the
///         mistake these tests exist to keep from being made again.
///     </para>
///     <para>
///         <b>What that gap has to be.</b> A player who has just dropped below the line is still
///         carrying velocity from the movement, so a shot can still be the end of it; once the
///         velocity is gone the player was standing, which is not a counter-strafe. Friction with no
///         input is the slowest way that residual velocity dies, so the widest gap a genuine stop can
///         produce is the time friction alone needs to carry a player from the line to rest. That is
///         computable from <c>sv_friction</c> and <c>sv_stopspeed</c> and carries no free parameter.
///     </para>
///     <para>
///         <b>These oracles do not import the answer.</b>
///         <see cref="Window_IsTheTimeFrictionNeedsToStopFromTheInaccuracyLine" /> runs the game's own
///         ground-friction update tick by tick from literals written out here and compares the result
///         against what the shipped window converts to; agreeing with a discrete simulation of the
///         model the closed form was derived from is what makes it a derivation rather than an
///         assertion. <see cref="TheInputsAreStillTheGamesOwn" /> is the weaker of the two and says so
///         in its own remark: it guards the three inputs against drift and proves nothing about the
///         formula.
///     </para>
///     <para>
///         <b>Why not fit it to our data instead.</b> The admitted share rises smoothly and
///         monotonically with the window over the whole benchmark corpus, from 24% at zero ticks to
///         81% at one second, with no knee anywhere and a derivative that only decays; and CS% rises
///         with it, from 0% to 70%. There is no stationary point to fit to, so any window chosen from
///         the curve is a choice about how flattering the ratio should be. The physics has an answer
///         and the data does not, which is why this is the one that ships.
///     </para>
/// </summary>
[Category("Unit")]
public class CounterStrafeWindowDerivationTests
{
    /// <summary>
    ///     <c>sv_friction</c>, CS2's shipped default, read from the convar dump (help string "World
    ///     friction."). Duplicated from the engine on purpose: an oracle that imports the value it
    ///     checks is not an oracle.
    /// </summary>
    private const double SvFriction = 5.2;

    /// <summary>
    ///     <c>sv_stopspeed</c>, CS2's shipped default, read from the same dump (help string "Minimum
    ///     stopping speed when on ground.").
    /// </summary>
    private const double SvStopSpeed = 80.0;

    /// <summary>The tick rate every Valve matchmaking demo in the benchmark corpus runs at.</summary>
    private const double TickRate = 64.0;

    /// <summary>
    ///     The tick rate third-party servers run at, which the same seconds constant has to convert
    ///     for.
    /// </summary>
    private const double HighTickRate = 128.0;

    /// <summary>
    ///     Every <c>m_flMaxspeed</c> a player can be carrying, from the Negev at its slowest to the
    ///     knife at the top of the range. The stop time depends on the line, which is a fraction of
    ///     the cap, so the sweep is over caps and the window has to cover the largest.
    /// </summary>
    private static readonly double[] _movementCaps = [150.0, 200.0, 215.0, 225.0, 230.0, 240.0, 250.0];

    /// <summary>
    ///     The window has to be exactly wide enough for the SLOWEST stop any weapon produces, and no
    ///     wider: too narrow drops shots that really were the end of a stop, too wide admits shots
    ///     taken long after the player had already come to rest, which are not attempts at all and
    ///     which move CS% without anyone counter-strafing better.
    ///     <para>
    ///         Asserted two ways for each cap. The closed form the engine ships
    ///         (<see cref="AimShotContextEdge.SecondsToStopFromTheInaccuracyLine" />) has to agree
    ///         with a tick-by-tick simulation of CS2's friction update, which is the part that makes
    ///         it a derivation; and the shipped window, which is that closed form at the fastest cap,
    ///         has to cover every cap without exceeding the largest.
    ///     </para>
    ///     <para>
    ///         The answer is NOT weapon-independent here, unlike the run-down-to-the-line time: the
    ///         <c>sv_stopspeed</c> floor makes most of this stretch linear rather than proportional,
    ///         so it runs from 7.9 ticks at a 150 cap to 13.1 at 250. A single constant in seconds is
    ///         legitimate anyway, because it only has to cover the worst case and the worst case is
    ///         the highest cap in the game.
    ///     </para>
    /// </summary>
    /// <returns>A task.</returns>
    [Test]
    public async Task Window_IsTheTimeFrictionNeedsToStopFromTheInaccuracyLine()
    {
        double shippedTicks = AimShotContextEdge.CounterStrafeLookbackSeconds * TickRate;
        int shippedWholeTicks = (int)Math.Round(shippedTicks);

        List<string> drift = [];
        double slowest = 0.0;
        foreach (double cap in _movementCaps)
        {
            double simulated = TicksToStopFromTheLine(cap);
            double closedForm = AimShotContextEdge.SecondsToStopFromTheInaccuracyLine(cap) * TickRate;
            slowest = Math.Max(slowest, closedForm);
            Console.WriteLine(
                $"   m_flMaxspeed {cap,6:F0} -> line {cap * AimShotContextEdge.CounterStrafeSpeedFraction,6:F1} u/s"
                + $" stops in {simulated,6:F3} ticks simulated, {closedForm,6:F3} closed form;"
                + $" shipped window {shippedWholeTicks} ticks");

            // One tick of the exponential stretch is all the discretisation error there is at these
            // caps, so a quarter tick of tolerance is loose enough to survive it and tight enough
            // that a formula describing a different quantity could not slip through.
            if (Math.Abs(simulated - closedForm) > 0.25)
            {
                drift.Add(
                    $"at a {cap:F0} cap the friction model stops in {simulated:F3} ticks and the "
                    + $"shipped closed form says {closedForm:F3}: the formula is not this model");
            }

            if (closedForm > shippedTicks)
            {
                drift.Add(
                    $"a player whose line is {cap * AimShotContextEdge.CounterStrafeSpeedFraction:F1} u/s "
                    + $"is still stopping {closedForm:F3} ticks later, past the {shippedTicks:F3}-tick "
                    + "window, so the end of their stop is dropped from the population");
            }
        }

        if (Math.Abs(shippedTicks - slowest) > 1e-9)
        {
            drift.Add(
                $"the slowest stop is {slowest:F3} ticks and the window is {shippedTicks:F3}: the "
                + "difference admits shots taken after the player had already come to rest");
        }

        // The two numbers a reader is likely to quote, pinned so a change to the inputs cannot move
        // them silently. 128-tick is here because _lookbackTicks converts at the demo's own rate and
        // no demo in the corpus is 128, so nothing else would notice the conversion changing.
        if (shippedWholeTicks != 13)
        {
            drift.Add($"the window is {shippedWholeTicks} ticks at 64-tick, it was 13");
        }

        int highRateTicks = (int)Math.Round(AimShotContextEdge.CounterStrafeLookbackSeconds * HighTickRate);
        if (highRateTicks != 26)
        {
            drift.Add($"the window is {highRateTicks} ticks at 128-tick, it was 26");
        }

        drift.ForEach(Console.WriteLine);
        await Assert.That(drift).IsEmpty();
    }

    /// <summary>
    ///     The three inputs the derivation reads, pinned against the values in CS2's convar dump and
    ///     its movement range.
    ///     <para>
    ///         <b>This is a drift guard and not a validation.</b> It cannot tell a wrong friction from
    ///         a right one, because both sides of the comparison are literals someone typed; all it
    ///         catches is an engine-side edit landing without a matching edit here.
    ///         <see cref="Window_IsTheTimeFrictionNeedsToStopFromTheInaccuracyLine" /> is what checks
    ///         the formula, and it reads these same constants, so the two together say "the formula is
    ///         this movement model, evaluated at these inputs" and nothing stronger.
    ///     </para>
    /// </summary>
    /// <returns>A task.</returns>
    [Test]
    public async Task TheInputsAreStillTheGamesOwn()
    {
        List<string> drift = [];
        if (Math.Abs(AimShotContextEdge.GroundFrictionPerSecond - SvFriction) > 1e-9)
        {
            drift.Add($"sv_friction is {SvFriction}, the engine derives from "
                      + $"{AimShotContextEdge.GroundFrictionPerSecond}");
        }

        if (Math.Abs(AimShotContextEdge.GroundStopSpeed - SvStopSpeed) > 1e-9)
        {
            drift.Add($"sv_stopspeed is {SvStopSpeed}, the engine derives from "
                      + $"{AimShotContextEdge.GroundStopSpeed}");
        }

        // The fastest thing a player carries on foot, and the reason the sweep above stops at 250.
        if (Math.Abs(AimShotContextEdge.FastestMovementCap - _movementCaps[^1]) > 1e-9)
        {
            drift.Add($"the fastest movement cap in the sweep is {_movementCaps[^1]:F0} and the window "
                      + $"is sized for {AimShotContextEdge.FastestMovementCap:F0}");
        }

        drift.ForEach(Console.WriteLine);
        await Assert.That(drift).IsEmpty();
    }

    /// <summary>
    ///     The speed ring <c>TryPeakSpeed</c> answers from has to be at least as wide as the window,
    ///     at every tick rate a demo can carry. A window wider than the ring silently reads the peak
    ///     over only the part that survived the wrap: admission quietly under-reports and nothing
    ///     fails, which is why this is an assertion and not the comment it used to be.
    /// </summary>
    /// <returns>A task.</returns>
    [Test]
    public async Task TheSpeedRingIsAtLeastAsWideAsTheWindow()
    {
        List<string> drift = [];
        double[] rates = [TickRate, HighTickRate, 256.0];
        foreach (double rate in rates)
        {
            int window = (int)Math.Round(AimShotContextEdge.CounterStrafeLookbackSeconds * rate);
            Console.WriteLine($"   {rate,5:F0}-tick -> window {window,3} ticks, ring "
                              + $"{AimVantageScanner.DefaultSpeedHistoryTicks} ticks");
            if (window > AimVantageScanner.DefaultSpeedHistoryTicks)
            {
                drift.Add(
                    $"at {rate:F0}-tick the window is {window} ticks and the speed ring is "
                    + $"{AimVantageScanner.DefaultSpeedHistoryTicks}, so the gate reads only the part "
                    + "of the window that survived the wrap");
            }
        }

        drift.ForEach(Console.WriteLine);
        await Assert.That(drift).IsEmpty();
    }

    // CS2's ground friction update, one tick at a time, starting from the movement-inaccuracy line
    // for this cap and running to rest: the drop is a fixed share of the current speed, with
    // sv_stopspeed acting as a floor under the speed that share is taken of, so the last stretch is
    // linear rather than exponential. The final tick is returned as a fraction rather than rounded
    // up, so the result is comparable to a continuous closed form instead of biased a tick high by
    // the one partial tick at the end.
    private static double TicksToStopFromTheLine(double movementCap)
    {
        double speed = movementCap * AimShotContextEdge.CounterStrafeSpeedFraction;
        double ticks = 0.0;
        while (speed > 0.0 && ticks < 1000.0)
        {
            double control = speed < SvStopSpeed ? SvStopSpeed : speed;
            double drop = control * SvFriction / TickRate;
            if (drop >= speed)
            {
                return ticks + (speed / drop);
            }

            speed -= drop;
            ticks++;
        }

        return ticks;
    }
}
