#region

using SdkDecoder = CS2OpenDev.Sdk.GameEvents.GameEventDecoder;

#endregion

namespace CS2DemoKit.Parser.GameEvents;

/// <summary>
///     Turns a <c>CMsgSource1LegacyGameEvent</c> fire into a <see cref="GameEvent" />: the SDK's
///     typed payload record, plus the per-fire transport context the SDK deliberately omits.
/// </summary>
/// <remarks>
///     <para>
///         The decoding itself is <c>CS2OpenDev.Sdk.GameEvents</c>'s. What used to be here — a
///         272-entry factory table, a hand-rolled KV1 coercion chain, and a name→descriptor map —
///         is gone. Two things that cost us to get right are now upstream's:
///     </para>
///     <list type="bullet">
///         <item>
///             KV1 coercion, including the CS2-only tags the CS:GO proto spec does not document:
///             type 8 (entity/pawn handle, in <c>val_long</c>) and type 9 (controller slot, in
///             <c>val_short</c>).
///         </item>
///         <item>
///             Duplicate-name resolution. <c>gameevents</c> declares 289 records under 273 names,
///             and <c>player_death</c> exists as both a 2-field <c>core</c> record and the 22-field
///             <c>mod</c> record CS2 actually fires. The SDK's registry resolves
///             <c>mod &gt; game &gt; core</c>; picking the wrong one compiles and parses, and
///             silently drops weapon, headshot, assister and distance from every kill.
///         </item>
///     </list>
///     <para>
///         The descriptor map below is kept, but only to name the keys of an event the SDK has no
///         record for — a demo may fire something an SDK build predates, and
///         <see cref="UnknownGameEvent" /> still wants field names rather than <c>key_0</c>.
///     </para>
/// </remarks>
internal sealed class GameEventDecoder
{
    private readonly Dictionary<int, EventDescriptor> _byId = new();
    private readonly SdkDecoder _sdk = new();

    /// <summary>Whether a <c>CMsgSource1LegacyGameEventList</c> has been seen yet.</summary>
    public bool HasSchema => _sdk.DescriptorCount > 0;

    /// <summary>
    ///     Server start tick from <c>CDemoFileHeader</c>, used to derive
    ///     <c>GameTick = ServerTick - ServerStartTick</c>. Set by the parser once the file header
    ///     is decoded.
    /// </summary>
    public int ServerStartTick { get; set; }

    /// <summary>Decode one fire.</summary>
    public GameEvent Decode(CMsgSource1LegacyGameEvent msg, int frameTick, int frameNumber)
    {
        // msg.ServerTick is the true server tick; the frame header tick is the game tick in CS2.
        int serverTick = msg.HasServerTick ? msg.ServerTick : frameTick + ServerStartTick;
        int gameTick = msg.HasServerTick ? msg.ServerTick - ServerStartTick : frameTick;
        int eventId = msg.Eventid;

        string name = _sdk.ResolveName(msg) is { Length: > 0 } resolved ? resolved : msg.EventName;

        // Misses are expected, not exceptional: an event with no loaded descriptor, or one this
        // SDK build has no record for. Both fall through to the untyped form rather than throwing.
        if (_sdk.TryDecode(msg, out object? payload) && payload is not null)
        {
            return new GameEvent(name, eventId, frameNumber, serverTick, gameTick, payload);
        }

        _byId.TryGetValue(eventId, out EventDescriptor? desc);
        Dictionary<string, object> fields = BuildFields(msg, desc);

        // Since Sdk.GameEvents 4.0 the events the schema extractor cannot see (item_drop,
        // halftime, game_restart) come from the SDK's curated supplement, so a miss here really
        // is an event nobody has a record for.
        return new UnknownGameEvent(name, eventId, frameNumber, serverTick, gameTick, fields);
    }

    /// <summary>Load the per-demo event schema. Safe to call more than once.</summary>
    public void LoadSchema(CMsgSource1LegacyGameEventList msg)
    {
        _sdk.LoadDescriptors(msg);

        foreach (CMsgSource1LegacyGameEventList.Types.descriptor_t d in msg.Descriptors)
        {
            KeyInfo[] keys = d.Keys.Select(k => new KeyInfo(k.Name, k.Type)).ToArray();
            _byId[d.Eventid] = new EventDescriptor(d.Eventid, d.Name, keys);
        }
    }

    /// <summary>Name the positional keys of a fire the SDK could not type.</summary>
    private static Dictionary<string, object> BuildFields(
        CMsgSource1LegacyGameEvent msg, EventDescriptor? desc)
    {
        Dictionary<string, object> result = new(msg.Keys.Count, StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < msg.Keys.Count; i++)
        {
            CMsgSource1LegacyGameEvent.Types.key_t k = msg.Keys[i];
            string fieldName = desc is not null && i < desc.Keys.Length
                ? desc.Keys[i].Name
                : $"key_{i}";
            result[fieldName] = ExtractValue(k);
        }

        return result;
    }

    private static object ExtractValue(CMsgSource1LegacyGameEvent.Types.key_t k) => k.Type switch
    {
        1 => k.ValString,
        2 => k.ValFloat,
        // Types 3 (LONG), 4 (SHORT), 5 (BYTE): prefer val_long, with a fallback chain for
        // older-format demos that may carry the value in val_short or val_byte instead.
        3 or 4 or 5 => k.ValLong != 0 ? k.ValLong : k.ValShort != 0 ? k.ValShort : k.ValByte,
        6 => k.ValBool,
        7 => k.ValUint64,
        // CS2-only, absent from the CS:GO proto spec:
        //   8 = entity/pawn handle (32-bit), in val_long
        //   9 = player controller slot index (16-bit), in val_short
        8 => k.ValLong,
        9 => k.ValShort,
        _ => k.ValLong
    };

    private sealed class EventDescriptor(int id, string name, KeyInfo[] keys)
    {
        /// <summary>Wire event id.</summary>
        public int Id { get; } = id;

        /// <summary>Ordered key descriptors — the positional layout of every fire of this event.</summary>
        public KeyInfo[] Keys { get; } = keys;

        /// <summary>Native event name.</summary>
        public string Name { get; } = name;
    }

    private sealed record KeyInfo(string Name, int Type);
}
