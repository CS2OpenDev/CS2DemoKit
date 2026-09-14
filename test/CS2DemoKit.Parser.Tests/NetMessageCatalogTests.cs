#region

using CS2DemoKit.TestSupport;

#endregion

namespace CS2DemoKit.Parser.Tests;

/// <summary>
///     The catalog is the one name and category map. Every decoded message must carry the name the
///     catalog gives its id, every known id must land in a category, and the frame-free header walk
///     must see the same messages the slice walk does.
/// </summary>
[NotInParallel]
public class NetMessageCatalogTests
{
    [Test]
    public async Task EveryKnownId_HasACategory()
    {
        await Assert.That(NetMessageCatalog.Names.Count).IsGreaterThan(40);
        foreach (int id in NetMessageCatalog.Names.Keys)
        {
            await Assert.That(NetMessageCatalog.CategoryOf(id)).IsNotEqualTo(MessageCategories.None);
            await Assert.That(NetMessageCatalog.IsKnown(id)).IsTrue();
        }

        await Assert.That(NetMessageCatalog.CategoryOf(NetMessageCatalog.UserCmdsTypeId)).IsEqualTo(MessageCategories.UserCmds);
        await Assert.That(NetMessageCatalog.CategoryOf((int)SVC_Messages.SvcPacketEntities)).IsEqualTo(MessageCategories.Entities);
        await Assert.That(NetMessageCatalog.CategoryOf((int)EBaseGameEvents.GeSource1LegacyGameEvent)).IsEqualTo(MessageCategories.GameEvents);
        await Assert.That(NetMessageCatalog.CategoryOf(400)).IsEqualTo(MessageCategories.None);
        await Assert.That(NetMessageCatalog.NameOf(400)).IsEqualTo("unknown(400)");
        await Assert.That(NetMessageCatalog.MaxKnownTypeId).IsGreaterThanOrEqualTo((int)EBaseGameEvents.GeSource1LegacyGameEvent);
    }

    [Test]
    public async Task DemoCommands_RoundTrip()
    {
        await Assert.That(NetMessageCatalog.DemoCommandName(EDemoCommands.DemPacket)).IsEqualTo("DEM_Packet");
        await Assert.That(NetMessageCatalog.DemoCommandName(EDemoCommands.DemFullPacket)).IsEqualTo("DEM_FullPacket");
        await Assert.That(NetMessageCatalog.DemoCommandName((EDemoCommands)250)).IsEqualTo("DEM_Unknown(250)");
        await Assert.That(NetMessageCatalog.IsContainer(EDemoCommands.DemSignonPacket)).IsTrue();
        await Assert.That(NetMessageCatalog.IsContainer(EDemoCommands.DemFileHeader)).IsFalse();
        await Assert.That(NetMessageCatalog.CategoryOf(EDemoCommands.DemFileHeader)).IsEqualTo(MessageCategories.Header);
        await Assert.That(NetMessageCatalog.CategoryOf(EDemoCommands.DemSendTables)).IsEqualTo(MessageCategories.Schema);
        await Assert.That(NetMessageCatalog.CategoryOf(EDemoCommands.DemPacket)).IsEqualTo(MessageCategories.None);
        await Assert.That(NetMessageCatalog.CategoryOf(EDemoCommands.DemSyncTick)).IsEqualTo(MessageCategories.Other);
    }

    [Test]
    [Category("Integration")]
    public async Task DecodedMessages_CarryCatalogNames()
    {
        string path = DemoTestHelper.RequireDemo();
        ParsedDemo demo = DemoTestHelper.GetOrParse(path);
        int checkedMessages = 0;
        foreach (DemoFrame frame in demo.Frames.Take(2000))
        {
            foreach (NetMessage msg in frame.DecodedMessages)
            {
                if (msg.MessageTypeName.StartsWith("DEM_", StringComparison.Ordinal))
                {
                    continue;
                }

                await Assert.That(NetMessageCatalog.Names.Values.Contains(msg.MessageTypeName)).IsTrue();
                checkedMessages++;
            }
        }

        await Assert.That(checkedMessages).IsGreaterThan(0);
    }

    [Test]
    [Category("Integration")]
    public async Task HeaderWalk_MatchesSliceWalk()
    {
        string path = DemoTestHelper.RequireDemo();
        ParsedDemo demo = DemoTestHelper.GetOrParse(path);
        byte[] bytes = await File.ReadAllBytesAsync(path);
        int framesChecked = 0;
        foreach (DemoFrame frame in demo.Frames)
        {
            EDemoCommands command = frame.Command switch
            {
                "DEM_Packet" => EDemoCommands.DemPacket,
                "DEM_SignonPacket" => EDemoCommands.DemSignonPacket,
                "DEM_FullPacket" => EDemoCommands.DemFullPacket,
                _ => EDemoCommands.DemSyncTick
            };
            if (command == EDemoCommands.DemSyncTick)
            {
                continue;
            }

            byte[] payload = DownstreamUtilities.GetDecompressedPayload(frame, bytes);
            List<DownstreamUtilities.InnerMessageSlice> slices = DownstreamUtilities.ExtractInnerMessageSlices(frame, payload);
            List<InnerMessageHeader> headers = DownstreamUtilities.ReadInnerMessageHeaders(payload, command);

            await Assert.That(headers.Count).IsEqualTo(slices.Count);
            for (int i = 0; i < slices.Count; i++)
            {
                await Assert.That(headers[i].TypeId).IsEqualTo(slices[i].TypeId);
                await Assert.That(headers[i].Length).IsEqualTo(slices[i].Bytes.Length);
                await Assert.That(headers[i].Decoded).IsFalse();
            }

            if (++framesChecked >= 500)
            {
                break;
            }
        }

        await Assert.That(framesChecked).IsGreaterThan(0);
    }

    [Test]
    public async Task SkipBytes_AdvancesLikeReadBytes()
    {
        SkipProbe probe = CompareSkipToRead();
        await Assert.That(probe.TellSkip).IsEqualTo(probe.TellRead);
        await Assert.That(probe.RemainingSkip).IsEqualTo(probe.RemainingRead);
        await Assert.That(probe.BitsMatch).IsTrue();
        await Assert.That(probe.ByteMatch).IsTrue();
        await Assert.That(probe.TellAfterZeroSkip).IsEqualTo(probe.TellAfterByte);
        await Assert.That(probe.RemainingAfterFullSkip).IsLessThan(8);
        await Assert.That(SkipPastEndThrows()).IsTrue();
    }

    private readonly record struct SkipProbe(
        int TellRead, int TellSkip, int RemainingRead, int RemainingSkip, bool BitsMatch, bool ByteMatch,
        int TellAfterByte, int TellAfterZeroSkip, int RemainingAfterFullSkip);

    // BitBuffer is a ref struct, so the comparison runs synchronously and the assertions read a record.
    private static SkipProbe CompareSkipToRead()
    {
        byte[] data = new byte[64];
        for (int i = 0; i < data.Length; i++)
        {
            data[i] = (byte)(i * 37 + 11);
        }

        BitBuffer read = new(data);
        BitBuffer skip = new(data);
        read.ReadUBits(5);
        skip.ReadUBits(5);
        read.ReadBytes(21);
        skip.SkipBytes(21);
        int tellRead = read.TellBits, tellSkip = skip.TellBits;
        int remainingRead = read.RemainingBits, remainingSkip = skip.RemainingBits;
        bool bitsMatch = skip.ReadUBits(13) == read.ReadUBits(13);
        bool byteMatch = skip.ReadByte() == read.ReadByte();
        int tellAfterByte = read.TellBits;
        skip.SkipBytes(0);
        int tellAfterZeroSkip = skip.TellBits;
        skip.SkipBytes(skip.RemainingBytes);
        return new SkipProbe(tellRead, tellSkip, remainingRead, remainingSkip, bitsMatch, byteMatch,
            tellAfterByte, tellAfterZeroSkip, skip.RemainingBits);
    }

    private static bool SkipPastEndThrows()
    {
        BitBuffer buffer = new(new byte[3]);
        buffer.ReadUBits(5);
        try
        {
            buffer.SkipBytes(3);
            return false;
        }
        catch (ArgumentOutOfRangeException)
        {
            return true;
        }
    }
}
