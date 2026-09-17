#region

using CS2DemoKit.Parser.GameEvents;

#endregion

namespace CS2DemoKit.Analysis.Events;

/// <summary>
///     Synthesized when a player controller's networked team changes, read from the entity digest
///     rather than from a <c>player_team</c> event. On a matchmaking demo the only <c>player_team</c>
///     a player fires is the halftime swap, so the events alone leave every first-half kill
///     unclassified; the controller entity carries the team from the signon state on. The evaluator
///     materialises the slot on this event and seeds its team from it; later changes stay with
///     <c>player_team</c>. Frame clock in every tick slot, like every other synthesized event.
/// </summary>
public sealed record PlayerTeamObservedEvent(int FrameNumber, int ServerTick, int GameTick, int PlayerSlot, int OldTeam, int Team)
    : GameEvent("player_team_observed", -1, FrameNumber, ServerTick, GameTick)
{
    public override IReadOnlyList<(string Name, string Value, string WireType)> GetDecodedFields() =>
        [F("PlayerSlot", PlayerSlot), F("OldTeam", OldTeam), F("Team", Team)];
}
