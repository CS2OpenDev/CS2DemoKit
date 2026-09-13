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
///     <see cref="KillAttachMaxGapSeconds" /> before the death, the kill is
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
///     mis-attributed; with the window at 375 ms this requires near-simultaneous fire from
///     the same player and is not observable in practice.
/// </summary>
public sealed class SprayKillEnrichmentEdge(
    StateNode source,
    PlayerContextIndex playerContext,
    TransientValueNode<int> sprayKills,
    TransientValueNode<int> sprayShotsAtKill,
    double tickRate = 64.0) : StateEdge(source)
{
    /// <summary>
    ///     Widest gap, in SECONDS, between the killer's last damaging shot and the death event for
    ///     the kill to attach to the run. Defined AS
    ///     <see cref="ShotEnrichmentEdge.SprayContinuationMaxGapSeconds" /> rather than merely
    ///     matching it: a kill attaches to a run exactly when another damaging shot at that instant
    ///     would have continued the run, so the two are one decision, and a drift between them
    ///     would credit a kill to a run the shot stream had already closed.
    /// </summary>
    public const double KillAttachMaxGapSeconds = ShotEnrichmentEdge.SprayContinuationMaxGapSeconds;

    /// <summary>
    ///     <see cref="KillAttachMaxGapSeconds" /> rendered at 64 ticks per second, kept for the same
    ///     reason <see cref="ShotEnrichmentEdge.SprayContinuationMaxGapTicks" /> is: this assembly
    ///     ships as a package and a <c>const</c> a consumer may have inlined cannot be removed. The
    ///     edge converts the seconds at the demo's own rate rather than reading this.
    /// </summary>
    public const int KillAttachMaxGapTicks = ShotEnrichmentEdge.SprayContinuationMaxGapTicks;

    // The attach bound in ticks at this demo's rate: 24 at 64-tick, 48 at 128. Same conversion as
    // ShotEnrichmentEdge's, because it is the same duration measured on the same frame clock.
    private readonly int _attachGapTicks = Math.Max(1, (int)Math.Round(
        KillAttachMaxGapSeconds * (tickRate > 0 ? tickRate : 64.0)));

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
        if (gap < 0 || gap > _attachGapTicks)
        {
            return false; // the run is stale (or the anchor is from the future) — not this spray
        }

        ctx.SprayKillCount++;
        sprayKills.SetValue(ctx.SprayKillCount);
        sprayShotsAtKill.SetValue(ctx.SprayShotCount);
        return true;
    }
}
