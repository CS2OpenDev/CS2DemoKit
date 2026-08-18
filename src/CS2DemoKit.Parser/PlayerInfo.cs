namespace CS2DemoKit.Parser;

/// <summary>
///     Player info extracted from the "userinfo" string table.
/// </summary>
public sealed record PlayerInfo(
    int Slot,
    string Name,
    ulong SteamId64,
    int UserId, // matches userid fields in game events
    int Team, // 2 = T, 3 = CT
    bool IsBot)
{
    /// <summary>
    ///     True for the GOTV proxy / demo recorder occupying a <c>userinfo</c> slot — an infrastructure
    ///     entry, not a person who played. It carries a name (e.g. "DemoRecorder") and no SteamID, so
    ///     without this flag it is indistinguishable from a player and inflates every "how many players"
    ///     surface by one.
    ///     <para>
    ///         Deliberately flagged rather than dropped from <c>ParsedDemo.Players</c>: the map is keyed
    ///         by slot and consumers resolve event actors through it, so removing an occupied slot would
    ///         change lookups. Presentation surfaces filter on this instead.
    ///     </para>
    /// </summary>
    public bool IsHltv { get; init; }
}
