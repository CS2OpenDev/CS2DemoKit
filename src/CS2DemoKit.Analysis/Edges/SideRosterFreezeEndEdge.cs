#region

using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Analysis.Building;
using CS2DemoKit.Analysis.Nodes;
using CS2DemoKit.Parser.GameEvents;

#endregion

namespace CS2DemoKit.Analysis.Edges;

/// <summary>
///     Writes a side's roster at <c>round_freeze_end</c>: the slots of the connected players on
///     <paramref name="side" /> at that moment, ascending. It is what a <c>for: each_team</c> table's
///     <c>slots</c> dimension reads, so a consumer can join the round's side membership to players:
///     the membership in THAT round, where a per-player table's <c>team</c> is the side the player
///     finished the match on. The roster holds until the next freeze end overwrites it.
/// </summary>
/// <param name="source">The edge source (the graph root).</param>
/// <param name="playerContext">The shared player-context index, read for team and connectivity.</param>
/// <param name="side">The side (2 = T, 3 = CT).</param>
/// <param name="roster">The node the slots are written to.</param>
public sealed class SideRosterFreezeEndEdge(
    StateNode source,
    PlayerContextIndex playerContext,
    int side,
    IntListCaptureNode roster) : StateEdge(source)
{
    /// <inheritdoc />
    public override EdgeEffect? DeclaredEffect => EdgeEffect.SetValue;

    /// <inheritdoc />
    public override Type MessageType => typeof(RoundFreezeEndEvent);

    /// <inheritdoc />
    public override StateNode? WrittenNode => roster;

    /// <summary>The connected players on <paramref name="side" />, by slot, ascending.</summary>
    /// <param name="playerContext">The shared player-context index.</param>
    /// <param name="side">The side.</param>
    /// <returns>The slots.</returns>
    public static int[] Slots(PlayerContextIndex playerContext, int side)
    {
        ArgumentNullException.ThrowIfNull(playerContext);
        return playerContext.AllPlayers
            .Where(p => p.Connected && p.Team == side)
            .Select(p => p.Slot)
            .Order()
            .ToArray();
    }

    /// <inheritdoc />
    public override bool TryApply(EvaluationContext context)
    {
        if (context.Message is not GameEventMessage { DecodedEvent.Payload: RoundFreezeEndEvent })
        {
            return false;
        }

        roster.SetValue(Slots(playerContext, side));
        return true;
    }
}
