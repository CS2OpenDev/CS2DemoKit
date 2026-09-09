#region

using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Analysis.Building;
using CS2DemoKit.Analysis.Events;
using CS2DemoKit.Analysis.Nodes;

#endregion

namespace CS2DemoKit.Analysis.Edges;

/// <summary>
///     Fires on every synthesized <see cref="EnemySpottedEvent" /> and derives the per-viewer contact
///     history the raw event cannot carry, latched on the viewer's
///     <see cref="PlayerContextIndex.PlayerContext" />:
///     <list type="bullet">
///         <item>
///             <c>enrich.spotted.spot_index</c>: 1 for the viewer's first contact of the round, 2 for
///             the next, and so on. <c>enrich.spotted.is_first_contact</c> is the same fact as a bool,
///             because "first contact" is the population most preaim numbers are actually about: a
///             re-peek of an enemy already fought is a different measurement, and mixing the two
///             makes a player who holds one angle look better than one who rotates.
///         </item>
///         <item>
///             <c>enrich.spotted.ticks_since_last_spot</c>: gap to the viewer's previous spot of ANY
///             enemy this round (<see cref="NoPreviousSpotSentinel" /> when there is none), so an
///             author can separate a fresh engagement from the strafe-peek flicker that makes one
///             duel emit several rising edges.
///         </item>
///     </list>
///     The angle at the moment of contact is NOT enriched here: it is measured by the scanner from
///     geometry the graph never sees, so it rides on the event itself as <c>AngleToChestDeg</c> and
///     the view exposes it as a plain field facet.
///     <para>
///         All gap math is on the event's own tick, which for a synthesized spot is the sampled server
///         tick (the event carries the same value on all three clocks). Round boundaries clear the
///         latched state via <see cref="PlayerContextIndex.ResetRoundState" />.
///     </para>
/// </summary>
public sealed class SpottedEnrichmentEdge(
    StateNode source,
    PlayerContextIndex playerContext,
    TransientValueNode<int> ticksSinceLastSpot,
    TransientValueNode<int> spotIndex,
    TransientBoolNode isFirstContact) : StateEdge(source)
{
    /// <summary>
    ///     Value of <c>enrich.spotted.ticks_since_last_spot</c> when the viewer has spotted nobody
    ///     else this round. Large enough that any <c>&lt;=</c> gap gate fails naturally, matching
    ///     <see cref="ShotEnrichmentEdge.NoPreviousShotSentinel" />'s convention.
    /// </summary>
    public const int NoPreviousSpotSentinel = 1_000_000;

    /// <inheritdoc />
    public override IReadOnlyList<StateNode>? AdditionalWrittenNodes => [spotIndex, isFirstContact];

    /// <inheritdoc />
    public override EdgeEffect? DeclaredEffect => EdgeEffect.SetValue;

    /// <inheritdoc />
    public override Type MessageType => typeof(EnemySpottedEvent);

    /// <inheritdoc />
    public override StateNode? WrittenNode => ticksSinceLastSpot;

    /// <inheritdoc />
    public override bool TryApply(EvaluationContext context) => false;

    /// <inheritdoc />
    public override bool TryApplyDirect(object payload, EvaluationContext context)
    {
        if (payload is not EnemySpottedEvent spotted)
        {
            return false;
        }

        if (!playerContext.TryGet(spotted.ViewerSlot, out PlayerContextIndex.PlayerContext? ctx))
        {
            return false;
        }

        int tick = spotted.GameTick;

        // A negative gap means the latched anchor is from a later tick (a re-evaluation or seek
        // artifact); treat it as "no previous spot" rather than emitting a nonsense gap.
        bool hasPrevious = ctx!.LastSpotTick >= 0 && tick >= ctx.LastSpotTick;
        ticksSinceLastSpot.SetValue(hasPrevious ? tick - ctx.LastSpotTick : NoPreviousSpotSentinel);

        ctx.SpotCount++;
        spotIndex.SetValue(ctx.SpotCount);
        if (ctx.SpotCount == 1)
        {
            isFirstContact.Activate();
        }
        else
        {
            isFirstContact.Deactivate();
        }

        ctx.LastSpotTick = tick;
        ctx.LastSpotPitch = spotted.ViewerPitchDeg;
        ctx.LastSpotYaw = spotted.ViewerYawDeg;
        ctx.LastSpotChestAngle = spotted.AngleToChestDeg;
        // A new contact re-arms both first-answer latches: the interval every timing metric wants
        // is to THIS contact, not to whichever one the player last shot at.
        ctx.SpotAnsweredByShot = false;
        ctx.SpotAnsweredByLanded = false;
        return true;
    }
}
