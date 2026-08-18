#region

using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Analysis.Building;
using CS2DemoKit.Analysis.Nodes;
using CS2DemoKit.Parser.GameEvents;

#endregion

namespace CS2DemoKit.Analysis.Edges;

/// <summary>
///     Fires on <see cref="PlayerDeathEvent" /> and correlates the kill with the killer's
///     CURRENT spray run (maintained by <see cref="ShotEnrichmentEdge" /> on
///     <c>bullet_damage</c>): when the killer's last damaging shot is at most
///     <see cref="KillAttachMaxGapTicks" /> frame-clock ticks before the death, the kill is
///     attributed to the run and the run's kill counter increments.
///     <list type="bullet">
///         <item>
///             <c>enrich.kill.spray_kills</c> — kills in the killer's current spray run,
///             INCLUDING this one (so <c>&gt;= 2</c> means "second distinct kill inside one
///             uninterrupted spray" — the spray-transfer gate). 0 when the kill is not part of
///             a run.
///         </item>
///         <item>
///             <c>enrich.kill.spray_shots_at_kill</c> — the run's damaging-shot count at the
///             moment of the kill, so authors can require sustained fire (e.g. <c>&gt;= 4</c>)
///             and exclude fast non-spray doubles (deagle taps, auto-shotgun).
///         </item>
///     </list>
///     Approximation documented for honesty: "kill during the run" is proximity-based (the
///     killing bullet's own <c>bullet_damage</c> lands at the same tick as the death, so the
///     gap is 0 or the run's normal shot spacing). The wire carries no bullet↔death identity,
///     so a kill by a DIFFERENT weapon landing within the window of an ongoing spray would be
///     mis-attributed; with the window at 24 ticks this requires near-simultaneous fire from
///     the same player and is not observable in practice.
/// </summary>
public sealed class SprayKillEnrichmentEdge(
    StateNode source,
    PlayerContextIndex playerContext,
    TransientValueNode<int> sprayKills,
    TransientValueNode<int> sprayShotsAtKill) : StateEdge(source)
{
    /// <summary>
    ///     Maximum frame-clock gap between the killer's last damaging shot and the death event
    ///     for the kill to attach to the run. Matches
    ///     <see cref="ShotEnrichmentEdge.SprayContinuationMaxGapTicks" />.
    /// </summary>
    public const int KillAttachMaxGapTicks = ShotEnrichmentEdge.SprayContinuationMaxGapTicks;

    /// <inheritdoc />
    public override IReadOnlyList<StateNode>? AdditionalWrittenNodes => [sprayShotsAtKill];

    /// <inheritdoc />
    public override EdgeEffect? DeclaredEffect => EdgeEffect.SetValue;

    /// <inheritdoc />
    public override Type MessageType => typeof(PlayerDeathEvent);

    /// <inheritdoc />
    public override StateNode? WrittenNode => sprayKills;

    /// <inheritdoc />
    public override bool TryApply(EvaluationContext context) => false;

    /// <inheritdoc />
    public override bool TryApplyDirect(object payload, EvaluationContext context)
    {
        if (payload is not PlayerDeathEvent death)
        {
            return false;
        }

        if (death.Attacker == death.UserId)
        {
            return false; // suicide/world death — no attacker-side spray to credit
        }

        if (!playerContext.TryGet(death.Attacker, out PlayerContextIndex.PlayerContext? ctx))
        {
            return false;
        }

        if (ctx!.SprayShotCount <= 0 || ctx.LastShotGameTick < 0)
        {
            return false;
        }

        int gap = context.Fire!.GameTick - ctx.LastShotGameTick;
        if (gap < 0 || gap > KillAttachMaxGapTicks)
        {
            return false; // the run is stale (or the anchor is from the future) — not this spray
        }

        ctx.SprayKillCount++;
        sprayKills.SetValue(ctx.SprayKillCount);
        sprayShotsAtKill.SetValue(ctx.SprayShotCount);
        return true;
    }
}
