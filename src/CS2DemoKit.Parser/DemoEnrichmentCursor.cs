#region

using CS2DemoKit.Parser.Entities;
using CS2DemoKit.Parser.GameEvents;
using CS2OpenSchema.Events;
using CS2OpenSchema.Protos;

#endregion

namespace CS2DemoKit.Parser;

/// <summary>
///     The stateful walk that turns decoded frames into a demo's metadata, roster, schema and typed
///     game events, one frame at a time. <see cref="DemoParser.Parse(ReadOnlyMemory{byte},DemoProfile)" />
///     drives it over every frame and reads the result at the end; the forward reader drives it as it
///     yields and exposes it live. Every property reflects the frames observed so far.
///     <para>
///         A frame's game-event slots are replaced in place with <see cref="GameEventMessage" />. An
///         event whose name is outside the plan's <see cref="DecodePlan.GameEventNames" /> is removed
///         from the frame instead, so <see cref="DemoFrame.DecodedMessages" /> only ever holds what
///         was asked for.
///     </para>
/// </summary>
internal sealed class DemoEnrichmentCursor
{
    private readonly GameEventDecoder _eventDecoder = new();
    private readonly StringTableProcessor _stringTables;
    private readonly DecodeMask _mask;
    private readonly DemoProfile? _profileOverride;

    // Team is not in the userinfo table; it is the last player_team event per slot. Merged into
    // the roster on read, which equals the end-of-parse fold for any prefix of the demo.
    private readonly Dictionary<int, int> _lastTeamBySlot = [];
    private Dictionary<int, PlayerInfo> _players = [];
    private int _rosterVersion;
    private int _playersVersion = -1;
    private int _playbackTicks;

    public DemoEnrichmentCursor(ParseDiagnostics diagnostics, DecodeMask mask, DemoProfile? profileOverride)
    {
        Diagnostics = diagnostics;
        _stringTables = new StringTableProcessor(diagnostics);
        _mask = mask;
        _profileOverride = profileOverride;
    }

    public ParseDiagnostics Diagnostics { get; }

    public string MapName { get; private set; } = string.Empty;

    public string ServerName { get; private set; } = string.Empty;

    public string ClientName { get; private set; } = string.Empty;

    public string GameDirectory { get; private set; } = string.Empty;

    public string DemoVersionName { get; private set; } = string.Empty;

    public string DemoVersionGuid { get; private set; } = string.Empty;

    public string Addons { get; private set; } = string.Empty;

    public int BuildNumber { get; private set; }

    public int ServerStartTick { get; private set; }

    public int PatchVersion { get; private set; }

    /// <summary>The highest frame tick observed so far.</summary>
    public int MaxTickSeen { get; private set; }

    /// <summary>From <c>svc_ServerInfo</c>; the CS2 default until one is observed.</summary>
    public float TickInterval { get; private set; } = 1f / 64f;

    public RuntimeSchema? Schema { get; private set; }

    public bool HasGameEventSchema => _eventDecoder.HasSchema;

    public bool HeaderSeen { get; private set; }

    /// <summary>
    ///     The most recent <c>DEM_FullPacket</c> whose string-table snapshot carried the
    ///     <c>instancebaseline</c> table. The dump is incremental, so a checkpoint primer needs this
    ///     one and not merely the latest full packet.
    /// </summary>
    public DemoFrame? LastInstanceBaselineFullPacket { get; private set; }

    /// <summary><c>CDemoFileInfo.PlaybackTicks</c> when observed, else null.</summary>
    public int? PlaybackTicks => _playbackTicks > 0 ? _playbackTicks : null;

    /// <summary>Playback ticks when known, else the highest tick observed.</summary>
    public int TickCount => _playbackTicks > 0 ? _playbackTicks : MaxTickSeen;

    public DemoProfile Profile =>
        _profileOverride ?? DemoSourceClassifier.Classify(ServerName, ClientName, GameDirectory, BuildNumber);

    /// <summary>
    ///     The roster so far, with each slot's team from its last <c>player_team</c> event. A new
    ///     dictionary per change, so a caller may hold the instance it was handed.
    /// </summary>
    public IReadOnlyDictionary<int, PlayerInfo> Players
    {
        get
        {
            if (_playersVersion != _rosterVersion)
            {
                Dictionary<int, PlayerInfo> merged = new(_stringTables.Players.Count);
                foreach ((int slot, PlayerInfo info) in _stringTables.Players)
                {
                    merged[slot] = _lastTeamBySlot.TryGetValue(slot, out int team) ? info with { Team = team } : info;
                }

                _players = merged;
                _playersVersion = _rosterVersion;
            }

            return _players;
        }
    }

    /// <summary>
    ///     Folds one frame in. Frames must arrive in recording order: the event schema must precede
    ///     the events it types, and the schema latches on the first <c>CDemoSendTables</c>.
    /// </summary>
    /// <param name="frame">The frame to fold; its game-event slots are rewritten.</param>
    /// <param name="eventSink">Receives every typed event kept, in order, when the caller collects them.</param>
    public void Observe(DemoFrame frame, List<GameEvent>? eventSink)
    {
        if (frame.ServerTick > MaxTickSeen)
        {
            MaxTickSeen = frame.ServerTick;
        }

        List<NetMessage> list = frame.MessageList;
        for (int i = 0; i < list.Count; i++)
        {
            NetMessage msg = list[i];
            switch (msg.Payload)
            {
                case CDemoFileHeader hdr:
                    ApplyHeader(hdr);
                    break;

                case CDemoFileInfo { PlaybackTicks: > 0 } info:
                    _playbackTicks = info.PlaybackTicks;
                    break;

                case CSVCMsg_ServerInfo { TickInterval: > 0 } serverInfo:
                    TickInterval = serverInfo.TickInterval;
                    if (!string.IsNullOrEmpty(serverInfo.MapName) && string.IsNullOrEmpty(MapName))
                    {
                        MapName = serverInfo.MapName;
                    }

                    break;

                case CDemoSendTables sendTables when Schema is null:
                    Schema = DemoParser.TryExtractSchema(sendTables);
                    break;

                case CMsgSource1LegacyGameEventList eventList:
                    _eventDecoder.LoadSchema(eventList);
                    break;

                case CMsgSource1LegacyGameEvent rawEvent:
                    GameEvent evt = _eventDecoder.Decode(rawEvent, frame.ServerTick, frame.FrameNumber);
                    if (_mask.EventNames is { } names && !names.Contains(evt.Name))
                    {
                        list.RemoveAt(i);
                        i--;
                        break;
                    }

                    list[i] = new GameEventMessage(
                        msg.MessageTypeName, msg.Payload,
                        msg.DecompressedStart, msg.DecompressedLength, evt);
                    if (evt.Payload is PlayerTeamEvent teamEvt)
                    {
                        _lastTeamBySlot[teamEvt.UserId] = teamEvt.Team;
                        _rosterVersion++;
                    }

                    eventSink?.Add(evt);
                    break;

                case CDemoStringTables snapshot:
                    _stringTables.ProcessSnapshot(snapshot);
                    _rosterVersion++;
                    if (frame.CommandKind == EDemoCommands.DemFullPacket && CarriesInstanceBaseline(snapshot))
                    {
                        LastInstanceBaselineFullPacket = frame;
                    }

                    break;

                case CSVCMsg_CreateStringTable createTable:
                    _stringTables.ProcessCreate(createTable);
                    _rosterVersion++;
                    break;

                case CSVCMsg_UpdateStringTable updateTable:
                    _stringTables.ProcessUpdate(updateTable);
                    _rosterVersion++;
                    break;
            }
        }
    }

    private void ApplyHeader(CDemoFileHeader hdr)
    {
        HeaderSeen = true;
        if (!string.IsNullOrEmpty(hdr.MapName))
        {
            MapName = hdr.MapName;
        }

        if (!string.IsNullOrEmpty(hdr.ServerName))
        {
            ServerName = hdr.ServerName;
        }

        if (!string.IsNullOrEmpty(hdr.ClientName))
        {
            ClientName = hdr.ClientName;
        }

        if (!string.IsNullOrEmpty(hdr.GameDirectory))
        {
            GameDirectory = hdr.GameDirectory;
        }

        if (hdr.BuildNum > 0)
        {
            BuildNumber = hdr.BuildNum;
        }

        if (hdr.PatchVersion > 0)
        {
            PatchVersion = hdr.PatchVersion;
        }

        if (!string.IsNullOrEmpty(hdr.DemoVersionName))
        {
            DemoVersionName = hdr.DemoVersionName;
        }

        if (!string.IsNullOrEmpty(hdr.DemoVersionGuid))
        {
            DemoVersionGuid = hdr.DemoVersionGuid;
        }

        if (!string.IsNullOrEmpty(hdr.Addons))
        {
            Addons = hdr.Addons;
        }

        ServerStartTick = hdr.ServerStartTick;
        _eventDecoder.ServerStartTick = hdr.ServerStartTick;
    }

    /// <summary>True when a full packet's string-table snapshot carries the <c>instancebaseline</c> table.</summary>
    internal static bool CarriesInstanceBaseline(DemoFrame frame)
    {
        foreach (NetMessage msg in frame.MessageList)
        {
            if (msg.Payload is CDemoStringTables snapshot && CarriesInstanceBaseline(snapshot))
            {
                return true;
            }
        }

        return false;
    }

    private static bool CarriesInstanceBaseline(CDemoStringTables snapshot)
    {
        foreach (CDemoStringTables.Types.table_t table in snapshot.Tables)
        {
            if (table.TableName == "instancebaseline")
            {
                return true;
            }
        }

        return false;
    }
}
