#region

using CS2DemoKit.Parser.GameEvents;
using CS2DemoKit.TestSupport;
using Google.Protobuf;

#endregion

namespace CS2DemoKit.Parser.Tests;

/// <summary>
///     What a narrowed plan leaves on the frames: nothing decoded under structure-only, only the
///     planned families otherwise, and headers that agree with the frame-free walk. The full parse
///     is the reference in every case.
/// </summary>
[NotInParallel]
[Category("Integration")]
public class DecodePlanFrameTests
{
    private static readonly HashSet<Type> _gameEventsOnlyPayloads =
    [
        typeof(CDemoFileHeader), typeof(CDemoFileInfo), typeof(CSVCMsg_ServerInfo),
        typeof(CDemoStringTables), typeof(CSVCMsg_CreateStringTable), typeof(CSVCMsg_UpdateStringTable),
        typeof(CSVCMsg_ClearAllStringTables), typeof(CMsgSource1LegacyGameEventList), typeof(CMsgSource1LegacyGameEvent)
    ];

    [Test]
    public async Task StructureOnly_DecodesNothing_RecordsEveryHeader()
    {
        string path = DemoTestHelper.RequireDemo();
        byte[] bytes = await File.ReadAllBytesAsync(path);
        ParsedDemo full = DemoTestHelper.GetOrParse(path);
        ParsedDemo structure = DemoParser.Parse(bytes.AsMemory(), new ParseOptions { Plan = DecodePlan.StructureOnly });

        await Assert.That(structure.Frames.Count).IsEqualTo(full.Frames.Count);
        await Assert.That(structure.AllGameEvents.Count).IsEqualTo(0);
        await Assert.That(structure.Players.Count).IsEqualTo(0);
        await Assert.That(structure.Schema).IsNull();
        await Assert.That(structure.Plan).IsSameReferenceAs(DecodePlan.StructureOnly);
        await Assert.That(structure.Provenance.MessagesDecoded).IsEqualTo(0L);
        await Assert.That(structure.Provenance.MessagesSkipped).IsGreaterThan(0L);
        await Assert.That(structure.Provenance.UserCmdsStored).IsEqualTo(0L);
        await Assert.That(structure.Provenance.FramesRead).IsEqualTo((long)full.Frames.Count);

        int packetFrames = 0;
        for (int i = 0; i < full.Frames.Count; i++)
        {
            DemoFrame s = structure.Frames[i];
            DemoFrame f = full.Frames[i];
            await Assert.That(s.CommandKind).IsEqualTo(f.CommandKind);
            await Assert.That(s.ServerTick).IsEqualTo(f.ServerTick);
            await Assert.That(s.GameTick).IsEqualTo(f.GameTick);
            await Assert.That(s.RawStart).IsEqualTo(f.RawStart);
            await Assert.That(s.RawLength).IsEqualTo(f.RawLength);
            await Assert.That(s.HeaderLength).IsEqualTo(f.HeaderLength);
            await Assert.That(s.IsCompressed).IsEqualTo(f.IsCompressed);
            await Assert.That(s.DecodedMessages.Count).IsEqualTo(0);
            await Assert.That(s.UserCmdsPayloadCount).IsEqualTo(0);

            if (!NetMessageCatalog.IsContainer(s.CommandKind))
            {
                await Assert.That(s.InnerMessageHeaders.Length).IsEqualTo(0);
                continue;
            }

            byte[] payload = DownstreamUtilities.GetDecompressedPayload(f, bytes);
            List<InnerMessageHeader> walked = DownstreamUtilities.ReadInnerMessageHeaders(payload, f.CommandKind);
            await Assert.That(s.InnerMessageHeaders.Length).IsEqualTo(walked.Count);
            for (int m = 0; m < walked.Count; m++)
            {
                InnerMessageHeader recorded = s.InnerMessageHeaders.Span[m];
                await Assert.That(recorded.TypeId).IsEqualTo(walked[m].TypeId);
                await Assert.That(recorded.Length).IsEqualTo(walked[m].Length);
                await Assert.That(recorded.DecompressedStart).IsEqualTo(walked[m].DecompressedStart);
                await Assert.That(recorded.Decoded).IsFalse();
            }

            if (walked.Count > 0)
            {
                packetFrames++;
            }
        }

        await Assert.That(packetFrames).IsGreaterThan(0);
    }

    [Test]
    public async Task EverythingWithStructure_HeadersAccountForEveryDecodedMessage()
    {
        string path = DemoTestHelper.RequireDemo();
        byte[] bytes = await File.ReadAllBytesAsync(path);
        ParsedDemo full = DemoTestHelper.GetOrParse(path);
        DecodePlan plan = new() { RecordStructure = true };
        ParsedDemo recorded = DemoParser.Parse(bytes.AsMemory(), new ParseOptions { Plan = plan, CountDropSites = true });

        await Assert.That(recorded.Frames.Count).IsEqualTo(full.Frames.Count);
        await Assert.That(recorded.AllGameEvents.Count).IsEqualTo(full.AllGameEvents.Count);
        await Assert.That(recorded.Provenance.MessagesSkipped).IsEqualTo(0L);
        await Assert.That(recorded.Provenance.MessagesDecoded).IsGreaterThan(0L);

        // A truncation event abandons the rest of a bitstream, so it counts as a drop without a header.
        long dropped = recorded.Warnings
            .Where(w => w.Code == ParseWarningCodes.NetMessageDropped && !w.Message.StartsWith("<bitstream-truncated>", StringComparison.Ordinal))
            .Sum(w => (long)(w.Count ?? 0));
        long undecodedHeaders = 0;
        for (int i = 0; i < full.Frames.Count; i++)
        {
            DemoFrame r = recorded.Frames[i];
            DemoFrame f = full.Frames[i];
            await Assert.That(r.DecodedMessages.Count).IsEqualTo(f.DecodedMessages.Count);
            await Assert.That(r.UserCmdsPayloadCount).IsEqualTo(f.UserCmdsPayloadCount);
            if (!NetMessageCatalog.IsContainer(r.CommandKind))
            {
                continue;
            }

            int fromBitstream = r.DecodedMessages.Count(m => m.MessageTypeName != "DEM_StringTables");
            int decodedHeaders = 0;
            foreach (InnerMessageHeader h in r.InnerMessageHeaders.Span)
            {
                if (h.Decoded)
                {
                    decodedHeaders++;
                }
                else
                {
                    undecodedHeaders++;
                }
            }

            await Assert.That(decodedHeaders).IsEqualTo(fromBitstream + r.UserCmdsPayloadCount);
        }

        // Every undecoded header under Everything is a message the parse dropped and counted.
        await Assert.That(undecodedHeaders).IsEqualTo(dropped);
    }

    [Test]
    public async Task GameEventsOnly_KeepsEventsAndRoster_DropsTheRest()
    {
        string path = DemoTestHelper.RequireDemo();
        byte[] bytes = await File.ReadAllBytesAsync(path);
        ParsedDemo full = DemoTestHelper.GetOrParse(path);
        ParsedDemo events = DemoParser.Parse(bytes.AsMemory(), new ParseOptions { Plan = DecodePlan.GameEventsOnly });

        await Assert.That(events.Frames.Count).IsEqualTo(full.Frames.Count);
        await Assert.That(events.AllGameEvents.Count).IsEqualTo(full.AllGameEvents.Count);
        await Assert.That(events.Players.Count).IsEqualTo(full.Players.Count);
        await Assert.That(events.Schema).IsNull();
        await Assert.That(events.MapName).IsEqualTo(full.MapName);
        await Assert.That(events.TickInterval).IsEqualTo(full.TickInterval);
        await Assert.That(events.TickCount).IsEqualTo(full.TickCount);
        await Assert.That(events.Provenance.UserCmdsStored).IsEqualTo(0L);
        await Assert.That(events.Provenance.MessagesDecoded).IsLessThan(full.Provenance.MessagesDecoded);

        for (int i = 0; i < full.AllGameEvents.Count; i++)
        {
            await Assert.That(events.AllGameEvents[i].Name).IsEqualTo(full.AllGameEvents[i].Name);
            await Assert.That(events.AllGameEvents[i].GameTick).IsEqualTo(full.AllGameEvents[i].GameTick);
            await Assert.That(events.AllGameEvents[i].FrameNumber).IsEqualTo(full.AllGameEvents[i].FrameNumber);
        }

        foreach ((int slot, PlayerInfo p) in full.Players)
        {
            await Assert.That(events.Players[slot]).IsEqualTo(p);
        }

        foreach (DemoFrame frame in events.Frames)
        {
            await Assert.That(frame.UserCmdsPayloadCount).IsEqualTo(0);
            foreach (NetMessage msg in frame.DecodedMessages)
            {
                IMessage payload = msg.Payload;
                await Assert.That(_gameEventsOnlyPayloads.Contains(payload.GetType())).IsTrue()
                    .Because($"{payload.GetType().Name} is outside the game-events plan");
            }
        }
    }

    [Test]
    public async Task ExcludingUserCmds_LeavesTheArenaEmpty()
    {
        string path = DemoTestHelper.RequireDemo();
        byte[] bytes = await File.ReadAllBytesAsync(path);
        ParsedDemo full = DemoTestHelper.GetOrParse(path);
        DecodePlan plan = new() { ExcludeMessageTypes = new HashSet<int> { NetMessageCatalog.UserCmdsTypeId } };
        ParsedDemo noInput = DemoParser.Parse(bytes.AsMemory(), new ParseOptions { Plan = plan });

        await Assert.That(noInput.Provenance.UserCmdsStored).IsEqualTo(0L);
        await Assert.That(noInput.Provenance.MessagesSkipped).IsEqualTo(full.Provenance.UserCmdsStored);
        await Assert.That(noInput.Provenance.MessagesDecoded).IsEqualTo(full.Provenance.MessagesDecoded);
        await Assert.That(noInput.AllGameEvents.Count).IsEqualTo(full.AllGameEvents.Count);
        for (int i = 0; i < full.Frames.Count; i++)
        {
            await Assert.That(noInput.Frames[i].UserCmdsPayloadCount).IsEqualTo(0);
            await Assert.That(noInput.Frames[i].DecodedMessages.Count).IsEqualTo(full.Frames[i].DecodedMessages.Count);
            await Assert.That(noInput.Frames[i].InnerMessages.Count)
                .IsEqualTo(full.Frames[i].InnerMessages.Count - full.Frames[i].UserCmdsPayloadCount);
        }
    }

    [Test]
    public async Task HeaderOnly_WalksPacketsForServerInfoAlone()
    {
        string path = DemoTestHelper.RequireDemo();
        byte[] bytes = await File.ReadAllBytesAsync(path);
        ParsedDemo full = DemoTestHelper.GetOrParse(path);
        DecodePlan plan = new() { Categories = MessageCategories.Header };
        ParsedDemo header = DemoParser.Parse(bytes.AsMemory(), new ParseOptions { Plan = plan });

        await Assert.That(header.Frames.Count).IsEqualTo(full.Frames.Count);
        await Assert.That(header.MapName).IsEqualTo(full.MapName);
        await Assert.That(header.ServerName).IsEqualTo(full.ServerName);
        await Assert.That(header.TickCount).IsEqualTo(full.TickCount);
        await Assert.That(header.TickInterval).IsEqualTo(full.TickInterval);
        await Assert.That(header.Provenance.MessagesSkipped).IsGreaterThan(0L);
        await Assert.That(header.Provenance.BytesDecompressed).IsLessThanOrEqualTo(full.Provenance.BytesDecompressed);
        foreach (DemoFrame frame in header.Frames)
        {
            if (!NetMessageCatalog.IsContainer(frame.CommandKind))
            {
                continue;
            }

            await Assert.That(frame.UserCmdsPayloadCount).IsEqualTo(0);
            foreach (NetMessage msg in frame.DecodedMessages)
            {
                await Assert.That(msg.Payload.GetType()).IsEqualTo(typeof(CSVCMsg_ServerInfo));
            }
        }
    }

    [Test]
    public async Task GameEventNames_KeepOnlyTheNamedEvents()
    {
        string path = DemoTestHelper.RequireDemo();
        byte[] bytes = await File.ReadAllBytesAsync(path);
        ParsedDemo full = DemoTestHelper.GetOrParse(path);
        HashSet<string> wanted = new(StringComparer.Ordinal) { "player_death", "round_freeze_end" };
        DecodePlan plan = DecodePlan.GameEventsOnly with { GameEventNames = wanted };
        ParsedDemo narrowed = DemoParser.Parse(bytes.AsMemory(), new ParseOptions { Plan = plan });

        int expected = full.AllGameEvents.Count(e => wanted.Contains(e.Name));
        await Assert.That(expected).IsGreaterThan(0);
        await Assert.That(narrowed.AllGameEvents.Count).IsEqualTo(expected);
        foreach (GameEvent evt in narrowed.AllGameEvents)
        {
            await Assert.That(wanted.Contains(evt.Name)).IsTrue();
        }

        // The dropped fires leave the frame entirely, so the decoded list is shorter by exactly them.
        int decodedEvents = 0;
        foreach (DemoFrame frame in narrowed.Frames)
        {
            foreach (NetMessage msg in frame.DecodedMessages)
            {
                if (msg.Payload is CMsgSource1LegacyGameEvent)
                {
                    await Assert.That(msg).IsTypeOf<GameEventMessage>();
                    decodedEvents++;
                }
            }
        }

        await Assert.That(decodedEvents).IsEqualTo(expected);
        // Team folds from the kept events only, so a plan that drops player_team has no teams.
        await Assert.That(narrowed.Players.Values.All(p => p.Team == 0)).IsTrue();
    }

    [Test]
    public async Task NothingPlanned_NeverInflatesAPacket()
    {
        string path = DemoTestHelper.RequireDemo();
        byte[] bytes = await File.ReadAllBytesAsync(path);
        ParsedDemo full = DemoTestHelper.GetOrParse(path);
        DecodePlan plan = new() { Categories = MessageCategories.None };
        await Assert.That(plan.WalksPackets).IsFalse();
        ParsedDemo nothing = DemoParser.Parse(bytes.AsMemory(), new ParseOptions { Plan = plan });

        await Assert.That(nothing.Frames.Count).IsEqualTo(full.Frames.Count);
        await Assert.That(nothing.Provenance.MessagesDecoded).IsEqualTo(0L);
        await Assert.That(nothing.Provenance.MessagesSkipped).IsEqualTo(0L);
        await Assert.That(nothing.Provenance.BytesDecompressed).IsLessThan(full.Provenance.BytesDecompressed);
        foreach (DemoFrame frame in nothing.Frames)
        {
            await Assert.That(frame.DecodedMessages.Count).IsEqualTo(0);
            await Assert.That(frame.InnerMessageHeaders.Length).IsEqualTo(0);
        }
    }
}
