using CS2DemoKit.Parser.Models;
using CS2DemoKit.TestSupport;
using TUnit.Core.Exceptions;

namespace CS2DemoKit.Parser.Tests;

/// <summary>
///     The user-command store holds <c>svc_UserCmds</c> payloads outside <see cref="DemoFrame.InnerMessages" />,
///     so nothing in the ordinary message path can catch a mistake in it. These pin it against an
///     independent oracle: the wire bytes, re-derived from the file.
///     <para>
///         Demo-independent. The oracle re-walks each frame's bitstream from the original file and
///         compares byte for byte, so any demo works and no golden is needed.
///     </para>
/// </summary>
[Category("Unit")]
public class UserCmdsStoreTests
{
    /// <summary>
    ///     Re-derives every <c>svc_UserCmds</c> payload for one frame straight from the file: decompress,
    ///     walk the <c>CDemoPacket</c> bitstream, keep the payloads in wire order. Shares no code with
    ///     the store writer, which is what makes it an oracle rather than a restatement.
    /// </summary>
    private static List<byte[]> ReDerive(DemoFrame frame, byte[] fileBytes)
    {
        List<byte[]> payloads = new();
        if (frame.Command is not ("DEM_Packet" or "DEM_SignonPacket"))
        {
            return payloads;
        }

        byte[] decompressed = DownstreamUtilities.GetDecompressedPayload(frame, fileBytes);
        CDemoPacket outer;
        try
        {
            outer = CDemoPacket.Parser.ParseFrom(decompressed);
        }
        catch
        {
            return payloads;
        }

        BitBuffer buf = new(outer.Data.Span);
        while (buf.RemainingBits > 0)
        {
            int typeId = (int)buf.ReadUBitVar();
            int size = (int)buf.ReadUVarInt32();
            if (size <= 0 || size > buf.RemainingBytes)
            {
                break;
            }

            byte[] bytes = new byte[size];
            buf.ReadBytes(bytes);
            if (typeId == (int)SVC_Messages.SvcUserCmds)
            {
                payloads.Add(bytes);
            }
        }

        return payloads;
    }

    /// <summary>Reads one frame's stored run back out, in order.</summary>
    private static List<byte[]> FromStore(DemoFrame frame)
    {
        List<byte[]> payloads = new();
        if (frame.UserCmdsBlock is not { } block)
        {
            return payloads;
        }

        int offset = frame.UserCmdsOffset;
        for (int i = 0; i < frame.UserCmdsCount; i++)
        {
            payloads.Add(UserCmdsStore.Read(block, ref offset).ToArray());
        }

        return payloads;
    }

    /// <summary>
    ///     Total <c>svc_UserCmds</c> payloads the wire carries, counted by the oracle rather than by
    ///     the store, so a store that silently wrote nothing cannot satisfy its own precondition.
    /// </summary>
    private static int WirePayloadCount(ParsedDemo demo, byte[] fileBytes) =>
        demo.Frames.Sum(f => ReDerive(f, fileBytes).Count);

    /// <summary>
    ///     Skips when the demo carries no player input at all. GOTV-side and trimmed demos can omit
    ///     <c>svc_UserCmds</c> entirely (the committed sample does), and a demo without the data
    ///     cannot say anything about how the data is stored.
    /// </summary>
    private static void RequireUserCmds(ParsedDemo demo, byte[] fileBytes)
    {
        if (WirePayloadCount(demo, fileBytes) == 0)
        {
            throw new SkipTestException("demo carries no svc_UserCmds messages");
        }
    }

    [Test]
    public async Task Store_HoldsExactlyTheWirePayloads_InOrder()
    {
        string path = DemoTestHelper.RequireDemo();
        byte[] fileBytes = File.ReadAllBytes(path);
        ParsedDemo demo = DemoParser.Parse(fileBytes.AsMemory());
        RequireUserCmds(demo, fileBytes);

        int framesWithUserCmds = 0, payloadsChecked = 0, mismatches = 0;

        foreach (DemoFrame frame in demo.Frames)
        {
            List<byte[]> expected = ReDerive(frame, fileBytes);
            List<byte[]> actual = FromStore(frame);

            if (expected.Count == 0 && actual.Count == 0)
            {
                continue;
            }

            framesWithUserCmds++;
            if (expected.Count != actual.Count)
            {
                mismatches++;
                continue;
            }

            for (int i = 0; i < expected.Count; i++)
            {
                payloadsChecked++;
                if (!expected[i].AsSpan().SequenceEqual(actual[i]))
                {
                    mismatches++;
                }
            }
        }

        Console.WriteLine($"frames with user commands: {framesWithUserCmds}; payloads: {payloadsChecked}; " +
                          $"mismatches: {mismatches}");

        await Assert.That(framesWithUserCmds).IsGreaterThan(0)
            .Because("a real demo carries player input; zero here means the store never ran");
        await Assert.That(payloadsChecked).IsGreaterThan(0);
        await Assert.That(mismatches).IsEqualTo(0);
    }

    // The store is written by several Parallel.For partitions at once, each with its own blocks. A
    // frame's payloads must stay contiguous in one block and must not interleave with another
    // partition's frame. Running single-threaded and comparing to the parallel result is the check:
    // identical output means partitioning did not corrupt any run.
    [Test]
    public async Task Store_IsUnaffectedByPartitioning()
    {
        string path = DemoTestHelper.RequireDemo();
        byte[] fileBytes = File.ReadAllBytes(path);

        ParsedDemo parallel = DemoParser.Parse(fileBytes.AsMemory());
        RequireUserCmds(parallel, fileBytes);

        ParsedDemo serial = DemoParser.Parse(fileBytes.AsMemory(),
            new ParseOptions { MaxDegreeOfParallelism = 1 });

        await Assert.That(serial.Frames.Count).IsEqualTo(parallel.Frames.Count);

        int compared = 0, mismatches = 0;
        for (int i = 0; i < parallel.Frames.Count; i++)
        {
            List<byte[]> a = FromStore(parallel.Frames[i]);
            List<byte[]> b = FromStore(serial.Frames[i]);

            if (a.Count != b.Count)
            {
                mismatches++;
                continue;
            }

            for (int j = 0; j < a.Count; j++)
            {
                compared++;
                if (!a[j].AsSpan().SequenceEqual(b[j]))
                {
                    mismatches++;
                }
            }
        }

        Console.WriteLine($"payloads compared: {compared}; mismatches: {mismatches}");
        await Assert.That(compared).IsGreaterThan(0);
        await Assert.That(mismatches).IsEqualTo(0);
    }

    // InnerMessages is the complete, ordered message list, so the stored payloads have to appear
    // there too. They are synthesized on access rather than retained, which is what keeps the store
    // worth having; this pins that the synthesis is complete and correctly placed.
    [Test]
    public async Task UserCmdsPayloads_AppearInInnerMessages_InWireOrder()
    {
        string path = DemoTestHelper.RequireDemo();
        byte[] fileBytes = File.ReadAllBytes(path);
        ParsedDemo demo = DemoParser.Parse(fileBytes.AsMemory());
        RequireUserCmds(demo, fileBytes);

        int framesChecked = 0, surfaced = 0, badCount = 0, outOfOrder = 0;
        foreach (DemoFrame frame in demo.Frames)
        {
            if (frame.UserCmdsPayloadCount == 0)
            {
                continue;
            }

            framesChecked++;
            IReadOnlyList<NetMessage> inner = frame.InnerMessages;
            if (inner.Count != frame.MessageList.Count + frame.UserCmdsPayloadCount)
            {
                badCount++;
            }

            // The oracle's payloads, in wire order, must line up with the svc_UserCmds entries in
            // InnerMessages read in list order.
            List<byte[]> expected = ReDerive(frame, fileBytes);
            int seen = 0;
            for (int i = 0; i < inner.Count; i++)
            {
                if (inner[i].MessageTypeName != "svc_UserCmds")
                {
                    continue;
                }

                if (seen >= expected.Count
                    || inner[i].DecompressedLength != expected[seen].Length)
                {
                    outOfOrder++;
                }

                seen++;
                surfaced++;
            }

            if (seen != expected.Count)
            {
                badCount++;
            }
        }

        Console.WriteLine($"frames: {framesChecked}; surfaced via InnerMessages: {surfaced}; "
                          + $"count mismatches: {badCount}; order mismatches: {outOfOrder}");
        await Assert.That(surfaced).IsGreaterThan(0);
        await Assert.That(badCount).IsEqualTo(0);
        await Assert.That(outOfOrder).IsEqualTo(0);
    }

    // A type test must not decode. This is what lets EntityTracker and the enrichment pass run
    // switch (msg.Payload) over every frame without paying for subtick input.
    [Test]
    public async Task StoredPayload_TypeTest_DoesNotDecode()
    {
        string path = DemoTestHelper.RequireDemo();
        byte[] fileBytes = File.ReadAllBytes(path);
        ParsedDemo demo = DemoParser.Parse(fileBytes.AsMemory());
        RequireUserCmds(demo, fileBytes);

        int checkedCount = 0, leaked = 0;
        foreach (DemoFrame frame in demo.Frames)
        {
            if (frame.UserCmdsPayloadCount == 0)
            {
                continue;
            }

            IReadOnlyList<NetMessage> inner = frame.InnerMessages;
            for (int i = 0; i < inner.Count && checkedCount < 5000; i++)
            {
                if (inner[i].MessageTypeName != "svc_UserCmds")
                {
                    continue;
                }

                checkedCount++;
                if (inner[i].Payload is CSVCMsg_UserCommands or CSVCMsg_PacketEntities)
                {
                    leaked++;
                }
            }

            if (checkedCount >= 5000)
            {
                break;
            }
        }

        await Assert.That(checkedCount).IsGreaterThan(0);
        await Assert.That(leaked).IsEqualTo(0)
            .Because("a payload that answers a type test as the real message would force every "
                     + "switch (msg.Payload) on the replay path to decode subtick input");
    }

    // InnerMessages has two read paths: a foreach walks the records once, the indexer scans to
    // find the record for a position. They must agree, or a consumer's choice of loop changes what
    // it sees.
    [Test]
    public async Task Enumerator_AndIndexer_AgreeExactly()
    {
        string path = DemoTestHelper.RequireDemo();
        byte[] fileBytes = File.ReadAllBytes(path);
        ParsedDemo demo = DemoParser.Parse(fileBytes.AsMemory());
        RequireUserCmds(demo, fileBytes);

        int compared = 0, mismatches = 0, framesChecked = 0;
        foreach (DemoFrame frame in demo.Frames)
        {
            if (frame.UserCmdsPayloadCount == 0)
            {
                continue;
            }

            framesChecked++;
            IReadOnlyList<NetMessage> inner = frame.InnerMessages;

            int i = 0;
            foreach (NetMessage viaForeach in inner)
            {
                NetMessage viaIndexer = inner[i];
                if (viaForeach.MessageTypeName != viaIndexer.MessageTypeName
                    || viaForeach.DecompressedLength != viaIndexer.DecompressedLength)
                {
                    mismatches++;
                }

                compared++;
                i++;
            }

            if (i != inner.Count)
            {
                mismatches++;
            }

            if (framesChecked >= 2000)
            {
                break;
            }
        }

        Console.WriteLine($"frames: {framesChecked}; positions compared: {compared}; mismatches: {mismatches}");
        await Assert.That(compared).IsGreaterThan(0);
        await Assert.That(mismatches).IsEqualTo(0);
    }

    [Test]
    public async Task SubTickExtractor_ReadsFromTheStore()
    {
        string path = DemoTestHelper.RequireDemo();
        byte[] fileBytes = File.ReadAllBytes(path);
        ParsedDemo demo = DemoParser.Parse(fileBytes.AsMemory());
        RequireUserCmds(demo, fileBytes);

        List<SubTickEvent> events = SubTickExtractor.Extract(demo.Frames);

        Console.WriteLine($"subtick events: {events.Count}");
        await Assert.That(events.Count).IsGreaterThan(0)
            .Because("the store is the only source of subtick input now, so zero means it is unreadable");

        // Sorted by When: the extractor's documented output ordering.
        float[] when = events.Select(e => e.When).ToArray();
        await Assert.That(when.SequenceEqual(when.OrderBy(w => w).ToArray())).IsTrue();
    }
}
