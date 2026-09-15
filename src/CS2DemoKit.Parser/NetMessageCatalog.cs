#region

using System.Collections.Frozen;
using System.Reflection;
using CS2OpenSchema.Protos;
using Google.Protobuf.Reflection;

#endregion

namespace CS2DemoKit.Parser;

/// <summary>
///     The one map from wire type ids to proto names, and from ids and demo commands to the
///     <see cref="MessageCategories" /> a <see cref="DecodePlan" /> selects on. Net message ids are
///     the union of NET, Bidirectional, SVC and EBaseGameEvents with the first writer winning on a
///     collision, which is the same order the parser assigns <see cref="NetMessage.MessageTypeName" />
///     in, so a name resolved here equals the name a decoded message carries.
/// </summary>
public static class NetMessageCatalog
{
    /// <summary>The <c>svc_UserCmds</c> type id, the one message the parser stores rather than decodes.</summary>
    public const int UserCmdsTypeId = (int)SVC_Messages.SvcUserCmds;

    private static readonly FrozenDictionary<int, string> _netNames = BuildNetNames();
    private static readonly FrozenDictionary<int, string> _commandNames = BuildNameCache<EDemoCommands>();
    private static readonly FrozenDictionary<string, int> _netIdsByName = BuildNetIdsByName();

    /// <summary>Every known net message type id and its proto name.</summary>
    public static IReadOnlyDictionary<int, string> Names => _netNames;

    /// <summary>Every known demo command and its proto name.</summary>
    public static IReadOnlyDictionary<int, string> DemoCommandNames => _commandNames;

    /// <summary>The largest known net message type id; sizes a dense per-id table.</summary>
    public static int MaxKnownTypeId { get; } = _netNames.Keys.Max();

    /// <summary>The proto name for <paramref name="typeId" />, or <c>unknown(N)</c> for an id with no decoder.</summary>
    public static string NameOf(int typeId) =>
        _netNames.TryGetValue(typeId, out string? name) ? name : $"unknown({typeId})";

    public static bool TryGetName(int typeId, out string name)
    {
        if (_netNames.TryGetValue(typeId, out string? found))
        {
            name = found;
            return true;
        }

        name = string.Empty;
        return false;
    }

    /// <summary>True when the parser has a decoder for <paramref name="typeId" />.</summary>
    public static bool IsKnown(int typeId) => _netNames.ContainsKey(typeId);

    /// <summary>The type id a proto name resolves to, matched case-insensitively.</summary>
    public static bool TryGetTypeId(string name, out int typeId) => _netIdsByName.TryGetValue(name, out typeId);

    private static FrozenDictionary<string, int> BuildNetIdsByName()
    {
        Dictionary<string, int> result = new(StringComparer.OrdinalIgnoreCase);
        foreach ((int id, string name) in _netNames)
        {
            result.TryAdd(name, id);
        }

        return result.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The proto name for <paramref name="command" />, or <c>DEM_Unknown(N)</c>.</summary>
    public static string DemoCommandName(EDemoCommands command) =>
        _commandNames.TryGetValue((int)command, out string? name) ? name : $"DEM_Unknown({(int)command})";

    /// <summary>
    ///     The category a net message belongs to. <see cref="MessageCategories.Other" /> for a known id
    ///     outside the named categories, <see cref="MessageCategories.None" /> for an unknown id.
    /// </summary>
    public static MessageCategories CategoryOf(int typeId) => typeId switch
    {
        (int)SVC_Messages.SvcServerInfo => MessageCategories.Header,
        (int)SVC_Messages.SvcFlattenedSerializer or (int)SVC_Messages.SvcClassInfo => MessageCategories.Schema,
        (int)SVC_Messages.SvcCreateStringTable or (int)SVC_Messages.SvcUpdateStringTable
            or (int)SVC_Messages.SvcClearAllStringTables => MessageCategories.StringTables,
        (int)EBaseGameEvents.GeSource1LegacyGameEventList or (int)EBaseGameEvents.GeSource1LegacyGameEvent =>
            MessageCategories.GameEvents,
        (int)SVC_Messages.SvcPacketEntities => MessageCategories.Entities,
        (int)SVC_Messages.SvcUserCmds => MessageCategories.UserCmds,
        _ => _netNames.ContainsKey(typeId) ? MessageCategories.Other : MessageCategories.None
    };

    /// <summary>
    ///     The category a direct-payload demo command belongs to. The three packet containers and
    ///     <c>DEM_Stop</c> carry no payload of their own and report <see cref="MessageCategories.None" />;
    ///     what a container holds is gated per inner message.
    /// </summary>
    public static MessageCategories CategoryOf(EDemoCommands command) => command switch
    {
        EDemoCommands.DemFileHeader or EDemoCommands.DemFileInfo => MessageCategories.Header,
        EDemoCommands.DemSendTables or EDemoCommands.DemClassInfo => MessageCategories.Schema,
        EDemoCommands.DemStringTables => MessageCategories.StringTables,
        EDemoCommands.DemUserCmd => MessageCategories.UserCmds,
        EDemoCommands.DemPacket or EDemoCommands.DemSignonPacket or EDemoCommands.DemFullPacket
            or EDemoCommands.DemStop => MessageCategories.None,
        _ => MessageCategories.Other
    };

    /// <summary>True for the three commands whose payload is a multiplexed net message stream.</summary>
    public static bool IsContainer(EDemoCommands command) =>
        command is EDemoCommands.DemPacket or EDemoCommands.DemSignonPacket or EDemoCommands.DemFullPacket;

    private static FrozenDictionary<int, string> BuildNetNames()
    {
        Dictionary<int, string> result = new();
        foreach (KeyValuePair<int, string> kvp in BuildNameCache<NET_Messages>())
        {
            result.TryAdd(kvp.Key, kvp.Value);
        }

        foreach (KeyValuePair<int, string> kvp in BuildNameCache<Bidirectional_Messages>())
        {
            result.TryAdd(kvp.Key, kvp.Value);
        }

        foreach (KeyValuePair<int, string> kvp in BuildNameCache<SVC_Messages>())
        {
            result.TryAdd(kvp.Key, kvp.Value);
        }

        foreach (KeyValuePair<int, string> kvp in BuildNameCache<EBaseGameEvents>())
        {
            result.TryAdd(kvp.Key, kvp.Value);
        }

        return result.ToFrozenDictionary();
    }

    private static FrozenDictionary<int, string> BuildNameCache<TEnum>() where TEnum : struct, Enum
    {
        Dictionary<int, string> result = new();
        foreach (FieldInfo field in typeof(TEnum).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            string? protoName = field.GetCustomAttribute<OriginalNameAttribute>()?.Name;
            if (protoName is null)
            {
                continue;
            }

            result.TryAdd((int)field.GetValue(null)!, protoName);
        }

        return result.ToFrozenDictionary();
    }
}
