#region

using CS2DemoKit.Parser.Entities;
using CS2DemoKit.Parser.GameEvents;

#endregion

namespace CS2DemoKit.Parser;

/// <summary>
///     The enriched output of <see cref="DemoParser.Parse(ReadOnlyMemory{byte},DemoProfile)" />.
///     Pass 1 and 2 produce raw <see cref="DemoFrame" /> objects; pass 3 enriches them with
///     decoded game events, player info, server metadata, and the entity schema.
/// </summary>
public sealed class ParsedDemo
{
    /// <summary>
    ///     Builds a demo from its parts. The parser is the producer in practice; the constructor is
    ///     public so a consumer's tests can assemble a synthetic demo without reflection.
    /// </summary>
    public ParsedDemo(
        IReadOnlyList<DemoFrame> frames,
        IReadOnlyList<GameEvent> allGameEvents,
        IReadOnlyDictionary<int, PlayerInfo> players,
        RuntimeSchema? schema,
        string mapName,
        int tickCount,
        float tickInterval,
        string serverName,
        string clientName,
        string gameDirectory,
        int buildNumber,
        int serverStartTick,
        int patchVersion,
        string demoVersionName,
        string demoVersionGuid,
        string addons,
        DemoProfile profile,
        DecodePlan? plan = null,
        DecodeProvenance? provenance = null,
        IReadOnlyList<ParseWarning>? warnings = null)
    {
        Warnings = warnings ?? [];
        Plan = plan ?? DecodePlan.Everything;
        Provenance = provenance ?? new DecodeProvenance(DecodeSource.DemoParserParse, DecodeMode.ParallelWholeFile,
            0, null, frames.Count, 0, 0, 0, 0);
        Frames = frames;
        AllGameEvents = allGameEvents;
        Players = players;
        Schema = schema;
        MapName = mapName;
        TickCount = tickCount;
        TickInterval = tickInterval;
        ServerName = serverName;
        ClientName = clientName;
        GameDirectory = gameDirectory;
        BuildNumber = buildNumber;
        ServerStartTick = serverStartTick;
        PatchVersion = patchVersion;
        DemoVersionName = demoVersionName;
        DemoVersionGuid = demoVersionGuid;
        Addons = addons;
        Profile = profile;
    }

    /// <summary>
    ///     Comma-separated addons string from <c>DEM_FileHeader</c>.
    /// </summary>
    public string Addons { get; }

    /// <summary>
    ///     All decoded game events in tick order (pre-built flat index).
    /// </summary>
    public IReadOnlyList<GameEvent> AllGameEvents { get; }

    /// <summary>
    ///     Game build number from <c>DEM_FileHeader</c>.
    /// </summary>
    public int BuildNumber { get; }

    /// <summary>
    ///     Recording client name from <c>DEM_FileHeader</c> (typically the GOTV proxy name).
    /// </summary>
    public string ClientName { get; }

    /// <summary>
    ///     Demo version GUID from <c>DEM_FileHeader</c>.
    /// </summary>
    public string DemoVersionGuid { get; }

    /// <summary>
    ///     Demo version name from <c>DEM_FileHeader</c> (e.g. <c>"valve_demo_2"</c>).
    /// </summary>
    public string DemoVersionName { get; }

    /// <summary>
    ///     Total recording duration as a <see cref="TimeSpan" />,
    ///     computed as <c>TickCount × TickInterval</c>.
    /// </summary>
    public TimeSpan Duration => TimeSpan.FromSeconds(TickCount * TickInterval);

    /// <summary>
    ///     All parsed frames in recording order.
    /// </summary>
    public IReadOnlyList<DemoFrame> Frames { get; }

    /// <summary>
    ///     Game directory from <c>DEM_FileHeader</c> (e.g. <c>"csgo"</c>).
    /// </summary>
    public string GameDirectory { get; }

    /// <summary>
    ///     Map name from <c>DEM_FileHeader</c> (e.g. <c>"de_dust2"</c>).
    /// </summary>
    public string MapName { get; }

    /// <summary>
    ///     Patch version from <c>DEM_FileHeader</c> (typically the live CS2 patch number).
    /// </summary>
    public int PatchVersion { get; }

    /// <summary>
    ///     Final player state keyed by player slot (0–63).
    ///     Name and SteamID64 are extracted from the <c>userinfo</c> string table;
    ///     <see cref="PlayerInfo.Team" /> reflects the last <c>player_team</c> game event
    ///     for each slot (2=T, 3=CT, 0=unassigned/spectator).
    ///     The slot key is the controller entity index, which matches the <c>userid</c>
    ///     field in game events.
    /// </summary>
    public IReadOnlyDictionary<int, PlayerInfo> Players { get; }

    /// <summary>The plan this parse decoded under. <see cref="DecodePlan.Everything" /> unless one was passed.</summary>
    public DecodePlan Plan { get; }

    /// <summary>Which path produced this result and how much it decoded.</summary>
    public DecodeProvenance Provenance { get; }

    /// <summary>
    ///     Identification of the demo's recording source (GOTV, HLTV, etc.) and
    ///     its expected event capabilities. Auto-classified from the header by
    ///     <see cref="DemoSourceClassifier" />; can be overridden via the
    ///     <c>profileOverride</c> parameter on
    ///     <see cref="DemoParser.Parse(ReadOnlyMemory{byte},DemoProfile)" />.
    /// </summary>
    public DemoProfile Profile { get; }

    /// <summary>
    ///     The flattened entity serializer schema parsed from <c>DEM_SendTables</c>, or
    ///     <c>null</c> if the demo did not contain a send-tables frame.
    /// </summary>
    public RuntimeSchema? Schema { get; }

    /// <summary>
    ///     Server hostname from <c>DEM_FileHeader</c> (e.g. <c>"Valve CS2 Server"</c>).
    /// </summary>
    public string ServerName { get; }

    /// <summary>
    ///     Server tick at which recording began, from <c>DEM_FileHeader</c>.
    ///     Non-zero for mid-match GOTV recordings.
    /// </summary>
    public int ServerStartTick { get; }

    /// <summary>
    ///     Total recorded tick count.
    ///     Sourced from <c>CDemoFileInfo.PlaybackTicks</c> when available (authoritative);
    ///     falls back to the highest tick number observed across all frames.
    /// </summary>
    public int TickCount { get; }

    /// <summary>
    ///     Duration of one server tick in seconds, from <c>svc_ServerInfo.TickInterval</c>.
    ///     Defaults to <c>1/64</c> (CS2 standard rate) if the message was not present.
    /// </summary>
    public float TickInterval { get; }

    /// <summary>
    ///     Server tick rate (ticks per second), derived as <c>Round(1 / TickInterval)</c>.
    ///     Typically, 64 for CS2 matchmaking servers.
    /// </summary>
    public int TickRate => (int)MathF.Round(1f / TickInterval);

    /// <summary>
    ///     Structured parse warnings (the S11 diagnostics channel, v0.6.0): per-structure damage
    ///     the parser recovered from — rejected string tables, unreadable player blobs — that
    ///     previously vanished into <c>Debug.WriteLine</c> (Release builds saw nothing at all).
    ///     Empty for a healthy demo. Prefer <see cref="Health" /> to <c>Warnings.Count</c> for the
    ///     "is this demo damaged" question; the parse itself is still a usable partial result.
    /// </summary>
    public IReadOnlyList<ParseWarning> Warnings { get; }

    /// <summary>
    ///     The one-line answer to "can I trust this demo": the worst
    ///     <see cref="ParseWarningCodes.SeverityOf" /> across <see cref="Warnings" />, or
    ///     <see cref="ParseHealth.Clean" /> when there are none.
    /// </summary>
    /// <remarks>
    ///     Not the same question as <c>Warnings.Count > 0</c>. A demo from a build newer than this
    ///     parser drops net messages it has no case for and reports
    ///     <see cref="ParseHealth.Degraded" /> while being a perfectly good demo. Gating a "this
    ///     demo may be damaged" banner on the count alone would fire on every such demo.
    /// </remarks>
    public ParseHealth Health => ParseWarningCodes.WorstOf(Warnings);
}
