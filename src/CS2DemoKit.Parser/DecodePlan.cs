#region

using CS2OpenSchema.Protos;

#endregion

namespace CS2DemoKit.Parser;

/// <summary>
///     The families a <see cref="DecodePlan" /> selects. A message belongs to exactly one; see
///     <see cref="NetMessageCatalog.CategoryOf(int)" /> and <see cref="NetMessageCatalog.CategoryOf(EDemoCommands)" />.
/// </summary>
[Flags]
public enum MessageCategories
{
    None = 0,

    /// <summary><c>DEM_FileHeader</c>, <c>DEM_FileInfo</c>, <c>svc_ServerInfo</c>.</summary>
    Header = 1,

    /// <summary><c>DEM_SendTables</c>, <c>DEM_ClassInfo</c>, <c>svc_ClassInfo</c>, <c>svc_FlattenedSerializer</c>.</summary>
    Schema = 2,

    /// <summary>
    ///     <c>DEM_StringTables</c>, the string-table snapshot inside <c>DEM_FullPacket</c>,
    ///     <c>svc_CreateStringTable</c>, <c>svc_UpdateStringTable</c>, <c>svc_ClearAllStringTables</c>.
    /// </summary>
    StringTables = 4,

    /// <summary>The game-event list and every game-event fire.</summary>
    GameEvents = 8,

    /// <summary><c>svc_PacketEntities</c>.</summary>
    Entities = 16,

    /// <summary><c>svc_UserCmds</c> (stored in the arena) and <c>DEM_UserCmd</c>.</summary>
    UserCmds = 32,

    /// <summary>Every other known message and command, and unknown ids.</summary>
    Other = 64,

    All = Header | Schema | StringTables | GameEvents | Entities | UserCmds | Other
}

/// <summary>
///     One inner message's position in a packet frame's bitstream, recorded without decoding it.
///     Answers "how many of which type" on a frame whose payloads were never touched.
/// </summary>
/// <param name="TypeId">The net message type id (UBitVar).</param>
/// <param name="Length">Payload length in bytes.</param>
/// <param name="DecompressedStart">Byte-approximate offset of the payload within the decompressed frame.</param>
/// <param name="Decoded">
///     True when the parse that recorded this header also produced a <see cref="NetMessage" /> for it.
///     Always false from <see cref="DownstreamUtilities.ReadInnerMessageHeaders" />, which decodes nothing.
/// </param>
public readonly record struct InnerMessageHeader(int TypeId, int Length, int DecompressedStart, bool Decoded);

/// <summary>
///     What a parse decodes and keeps. <see cref="Everything" /> is today's behaviour; the other presets
///     leave out whole families so the frames never hold their payloads. Applies to both
///     <see cref="DemoParser.Parse(ReadOnlyMemory{byte},ParseOptions,DemoProfile)" /> and the forward
///     reader through <see cref="ParseOptions.Plan" />.
/// </summary>
public sealed record DecodePlan
{
    /// <summary>Every message and command, exactly as an options-less parse decodes.</summary>
    public static DecodePlan Everything { get; } = new();

    /// <summary>No payload is decoded; every packet frame records its inner-message headers.</summary>
    public static DecodePlan StructureOnly { get; } = new()
    {
        Categories = MessageCategories.None,
        RecordStructure = true
    };

    /// <summary>Header, string tables (the roster) and game events. No schema, no entity data, no input.</summary>
    public static DecodePlan GameEventsOnly { get; } = new()
    {
        Categories = MessageCategories.Header | MessageCategories.StringTables | MessageCategories.GameEvents
    };

    /// <summary>What an entity replay consumes, with the signon prefix retained for checkpoint priming.</summary>
    public static DecodePlan EntityReplay { get; } = new()
    {
        Categories = MessageCategories.Header | MessageCategories.Schema | MessageCategories.StringTables
                     | MessageCategories.Entities,
        RetainSignonPrefix = true
    };

    /// <summary>The families to decode.</summary>
    public MessageCategories Categories { get; init; } = MessageCategories.All;

    /// <summary>Net message type ids decoded regardless of <see cref="Categories" />.</summary>
    public IReadOnlySet<int>? IncludeMessageTypes { get; init; }

    /// <summary>Net message type ids never decoded. Wins over <see cref="IncludeMessageTypes" />.</summary>
    public IReadOnlySet<int>? ExcludeMessageTypes { get; init; }

    /// <summary>
    ///     Game event names to keep; <c>null</c> keeps every event. Other fires are dropped after the
    ///     cheap proto decode that reveals the name, before the typed record is materialised. The
    ///     roster's team column folds from <c>player_team</c>, so a set without it yields no teams.
    /// </summary>
    public IReadOnlySet<string>? GameEventNames { get; init; }

    /// <summary>Record <c>DemoFrame.InnerMessageHeaders</c> on every packet frame.</summary>
    public bool RecordStructure { get; init; }

    /// <summary>The forward reader keeps the frames before the first <c>DEM_Packet</c>.</summary>
    public bool RetainSignonPrefix { get; init; }

    /// <summary>
    ///     Entity classes whose field values a tracker should store. Carried for whoever builds the
    ///     tracker (<c>EntityTracker.StoreClassFilter</c>); the parser and reader never own one.
    /// </summary>
    public IReadOnlySet<string>? EntityClassFilter { get; init; }

    /// <summary>
    ///     True when the plan decodes every message a parse with no plan would. Structure recording
    ///     and prefix retention add to a frame without changing what is decoded.
    /// </summary>
    public bool DecodesEverything =>
        Categories == MessageCategories.All
        && IncludeMessageTypes is null or { Count: 0 }
        && ExcludeMessageTypes is null or { Count: 0 }
        && GameEventNames is null;

    /// <summary>
    ///     True when packet frames are walked at all. A plan with nothing to find inside a packet
    ///     skips the decompression and the bitstream walk entirely.
    /// </summary>
    public bool WalksPackets =>
        Categories != MessageCategories.None || RecordStructure || IncludeMessageTypes is { Count: > 0 };

    /// <summary>Whether a net message with <paramref name="typeId" /> is decoded.</summary>
    public bool Decodes(int typeId)
    {
        if (ExcludeMessageTypes?.Contains(typeId) == true)
        {
            return false;
        }

        if (IncludeMessageTypes?.Contains(typeId) == true)
        {
            return true;
        }

        MessageCategories category = NetMessageCatalog.CategoryOf(typeId);
        return category == MessageCategories.None
            ? (Categories & MessageCategories.Other) != 0
            : (Categories & category) != 0;
    }

    /// <summary>
    ///     Whether a direct-payload command is decoded. For the three containers this is
    ///     <see cref="WalksPackets" />; for <c>DEM_Stop</c> it is always false.
    /// </summary>
    public bool Decodes(EDemoCommands command)
    {
        if (NetMessageCatalog.IsContainer(command))
        {
            return WalksPackets;
        }

        MessageCategories category = NetMessageCatalog.CategoryOf(command);
        return category != MessageCategories.None && (Categories & category) != 0;
    }
}
