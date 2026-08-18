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
    internal ParsedDemo(
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
        DemoProfile profile)
    {
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
        // S11 diagnostics channel (v0.6.0): drain the parse-thread accumulator INSIDE the ctor
        // body, so the call site in the (protected) DemoParser.cs needs no signature change.
        // Drain-on-construct is also the per-parse reset — see ParseDiagnostics.
        Warnings = ParseDiagnostics.Drain();
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
    ///     Empty for a healthy demo. The UI surfaces a "this demo may be damaged" banner when
    ///     non-empty; the parse itself is still a usable partial result.
    /// </summary>
    public IReadOnlyList<ParseWarning> Warnings { get; }
}
