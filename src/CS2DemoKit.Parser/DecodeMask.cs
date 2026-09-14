#region

using CS2OpenSchema.Protos;

#endregion

namespace CS2DemoKit.Parser;

/// <summary>
///     A <see cref="DecodePlan" /> compiled to dense tables so the per-message check on the decode
///     hot path is one array index. Built once per parse or reader.
/// </summary>
internal sealed class DecodeMask
{
    public static readonly DecodeMask Everything = Compile(DecodePlan.Everything);

    private readonly bool[] _net;
    private readonly bool[] _command;

    private DecodeMask(DecodePlan plan, bool[] net, bool[] command)
    {
        Plan = plan;
        _net = net;
        _command = command;
        DecodeUnknown = (plan.Categories & MessageCategories.Other) != 0;
        WalkPackets = plan.WalksPackets;
        RecordStructure = plan.RecordStructure;
        UserCmds = plan.Decodes(NetMessageCatalog.UserCmdsTypeId);
        FullPacketStringTable = plan.Decodes(EDemoCommands.DemStringTables);
        EventNames = plan.GameEventNames;
    }

    public DecodePlan Plan { get; }

    /// <summary>Ids past the table are unknown types; they follow <see cref="MessageCategories.Other" />.</summary>
    public bool DecodeUnknown { get; }

    public bool WalkPackets { get; }

    public bool RecordStructure { get; }

    /// <summary>Whether <c>svc_UserCmds</c> payloads go to the arena or are skipped in the bitstream.</summary>
    public bool UserCmds { get; }

    /// <summary>Whether the string-table snapshot inside <c>DEM_FullPacket</c> is decoded.</summary>
    public bool FullPacketStringTable { get; }

    public IReadOnlySet<string>? EventNames { get; }

    public bool DecodesNet(int typeId) => (uint)typeId < (uint)_net.Length ? _net[typeId] : DecodeUnknown;

    public bool DecodesCommand(EDemoCommands command)
    {
        int i = (int)command;
        return (uint)i < (uint)_command.Length ? _command[i] : (Plan.Categories & MessageCategories.Other) != 0;
    }

    public static DecodeMask Compile(DecodePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        int maxNet = NetMessageCatalog.MaxKnownTypeId;
        if (plan.IncludeMessageTypes is { Count: > 0 } include)
        {
            maxNet = Math.Max(maxNet, include.Max());
        }

        if (plan.ExcludeMessageTypes is { Count: > 0 } exclude)
        {
            maxNet = Math.Max(maxNet, exclude.Max());
        }

        bool[] net = new bool[maxNet + 1];
        for (int i = 0; i < net.Length; i++)
        {
            net[i] = plan.Decodes(i);
        }

        int maxCommand = 0;
        foreach (int id in NetMessageCatalog.DemoCommandNames.Keys)
        {
            maxCommand = Math.Max(maxCommand, id);
        }

        bool[] command = new bool[maxCommand + 1];
        for (int i = 0; i < command.Length; i++)
        {
            command[i] = plan.Decodes((EDemoCommands)i);
        }

        return new DecodeMask(plan, net, command);
    }
}
