#region

using System.Numerics;
using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Analysis.Building;
using CS2DemoKit.Analysis.Nodes;
using CS2DemoKit.Parser.GameEvents;

#endregion

namespace CS2DemoKit.Analysis.Edges;

/// <summary>
///     Fires on every <see cref="BulletDamageEvent" /> (a bullet that damaged a player) and
///     derives per-attacker "shot history" enrichments from state latched on the attacker's
///     <see cref="PlayerContextIndex.PlayerContext" />:
///     <list type="bullet">
///         <item>
///             <c>enrich.shot.turn_degrees</c> — 3D angular distance between this shot's absolute
///             aim angle (<c>ShootAngX/Y</c>, pitch/yaw) and the attacker's PREVIOUS damaging
///             shot's aim angle. <c>enrich.shot.ticks_since_last_shot</c> carries the frame-clock
///             tick gap to that previous shot (<see cref="NoPreviousShotSentinel" /> when there is
///             none this round), so authors gate "flick" as a large delta over a small gap.
///             Honesty note: the anchor is the previous DAMAGING shot — bullet_damage does not
///             fire for misses — so this captures fast re-aims between landed shots (spray
///             transfers, pistol/rifle target switches), NOT slow-refire AWP flicks, whose
///             previous shot is always outside any meaningful gap window.
///         </item>
///         <item>
///             <c>enrich.shot.spray_shots</c> / <c>enrich.shot.spray_victims</c> — the current
///             "spray run": consecutive damaging shots whose tick gap is at most
///             <see cref="SprayContinuationMaxGapTicks" /> and whose <c>RecoilIndex</c> never
///             drops (within <see cref="SprayRecoilEpsilon" /> — RecoilIndex rises per shot while
///             firing continuously and decays once fire stops). A drop or a long gap starts a new
///             run. spray_victims counts DISTINCT victim slots damaged during the run.
///         </item>
///     </list>
///     All gap math uses the event's frame-clock <c>GameTick</c> (same clock as
///     <see cref="PlayerDeathEvent" />, which <see cref="SprayKillEnrichmentEdge" /> compares
///     against). Round boundaries clear the latched state via
///     <see cref="PlayerContextIndex.ResetRoundState" />.
/// </summary>
public sealed class ShotEnrichmentEdge(
    StateNode source,
    PlayerContextIndex playerContext,
    TransientValueNode<double> turnDegrees,
    TransientValueNode<int> ticksSinceLastShot,
    TransientValueNode<int> sprayShots,
    TransientValueNode<int> sprayVictims) : StateEdge(source)
{
    /// <summary>
    ///     Value of <c>enrich.shot.ticks_since_last_shot</c> when the attacker has no previous
    ///     damaging shot this round. Large enough that any <c>&lt;=</c> gap gate fails naturally.
    /// </summary>
    public const int NoPreviousShotSentinel = 1_000_000;

    /// <summary>
    ///     Maximum frame-clock tick gap between consecutive damaging shots for a spray run to
    ///     continue (24 ticks = 375 ms @ 64/s — covers every automatic's refire with margin).
    /// </summary>
    public const int SprayContinuationMaxGapTicks = 24;

    /// <summary>Float-noise tolerance on the RecoilIndex monotonicity check.</summary>
    public const float SprayRecoilEpsilon = 0.25f;

    /// <inheritdoc />
    public override IReadOnlyList<StateNode>? AdditionalWrittenNodes =>
        [ticksSinceLastShot, sprayShots, sprayVictims];

    /// <inheritdoc />
    public override EdgeEffect? DeclaredEffect => EdgeEffect.SetValue;

    /// <inheritdoc />
    public override Type MessageType => typeof(BulletDamageEvent);

    /// <inheritdoc />
    public override StateNode? WrittenNode => turnDegrees;

    /// <inheritdoc />
    public override bool TryApply(EvaluationContext context) => false;

    /// <inheritdoc />
    public override bool TryApplyDirect(object payload, EvaluationContext context)
    {
        if (payload is not BulletDamageEvent shot)
        {
            return false;
        }

        if (!playerContext.TryGet(shot.Attacker, out PlayerContextIndex.PlayerContext? ctx))
        {
            return false;
        }

        int tick = context.Fire!.GameTick;
        // A negative gap means the latched anchor is from a later tick (out-of-order replay /
        // seek artifact) — treat as "no previous shot" rather than emitting nonsense.
        bool hasPrev = ctx!.LastShotGameTick >= 0 && tick >= ctx.LastShotGameTick;
        int gap = hasPrev ? tick - ctx.LastShotGameTick : NoPreviousShotSentinel;

        if (hasPrev)
        {
            turnDegrees.SetValue(AngleDeltaDegrees(
                ctx.LastShotPitch, ctx.LastShotYaw, shot.ShootAngX, shot.ShootAngY));
            ticksSinceLastShot.SetValue(gap);
        }
        else
        {
            turnDegrees.SetValue(0.0);
            ticksSinceLastShot.SetValue(NoPreviousShotSentinel);
        }

        // Spray-run continuation: recent enough AND RecoilIndex did not drop. Same-tick events
        // (multi-victim penetration, shotgun pellets) continue the run with gap 0.
        bool continues = ctx.SprayShotCount > 0
                         && hasPrev
                         && gap <= SprayContinuationMaxGapTicks
                         && shot.RecoilIndex >= ctx.SprayLastRecoil - SprayRecoilEpsilon;
        if (continues)
        {
            ctx.SprayShotCount++;
        }
        else
        {
            ctx.SprayShotCount = 1;
            ctx.SprayVictimsMask = 0;
            ctx.SprayKillCount = 0;
        }

        if (shot.Victim is >= 0 and < 64)
        {
            ctx.SprayVictimsMask |= 1UL << shot.Victim;
        }

        sprayShots.SetValue(ctx.SprayShotCount);
        sprayVictims.SetValue(BitOperations.PopCount(ctx.SprayVictimsMask));

        ctx.LastShotGameTick = tick;
        ctx.LastShotPitch = shot.ShootAngX;
        ctx.LastShotYaw = shot.ShootAngY;
        ctx.SprayLastRecoil = shot.RecoilIndex;
        return true;
    }

    /// <summary>
    ///     3D angular distance in degrees between two view directions given as Source QAngle
    ///     pitch/yaw pairs (degrees). Both angles are converted to unit view vectors and the
    ///     delta is <c>acos(dot)</c>, so yaw wraparound (−180/+180) and pitch are handled
    ///     without special cases. Roll is irrelevant to a view direction and ignored.
    /// </summary>
    public static double AngleDeltaDegrees(float pitchA, float yawA, float pitchB, float yawB)
    {
        const double toRad = Math.PI / 180.0;
        double pa = pitchA * toRad, ya = yawA * toRad;
        double pb = pitchB * toRad, yb = yawB * toRad;

        double xa = Math.Cos(pa) * Math.Cos(ya), ya2 = Math.Cos(pa) * Math.Sin(ya), za = -Math.Sin(pa);
        double xb = Math.Cos(pb) * Math.Cos(yb), yb2 = Math.Cos(pb) * Math.Sin(yb), zb = -Math.Sin(pb);

        double dot = Math.Clamp(xa * xb + ya2 * yb2 + za * zb, -1.0, 1.0);
        return Math.Acos(dot) / toRad;
    }
}
