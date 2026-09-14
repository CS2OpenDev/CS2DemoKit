#region

using CS2DemoKit.Parser.GameEvents;
using CS2DemoKit.TestSupport;

#endregion

namespace CS2DemoKit.Parser.Tests;

/// <summary>
///     The forward reader must hand out exactly the frames a whole-file parse retains, in the same
///     order, with the same decoded messages, and its enrichment must settle on the same demo facts.
///     The parse is the reference; the reader is what has to agree with it.
/// </summary>
[NotInParallel]
[Category("Integration")]
public class DemoReaderEquivalenceTests
{
    [Test]
    public async Task Reader_YieldsTheSameFramesAsParse()
    {
        string path = DemoTestHelper.RequireDemo();
        byte[] bytes = await File.ReadAllBytesAsync(path);
        ParsedDemo full = DemoTestHelper.GetOrParse(path);

        using DemoReader reader = DemoReader.Open(bytes.AsMemory());
        await Assert.That(reader.FrameCount).IsNull();
        await Assert.That(reader.SupportsRandomAccess).IsFalse();
        await Assert.That(reader.Frames).IsNull();

        List<GameEvent> events = [];
        int index = 0;
        while (reader.TryReadNext(out DemoFrame? frame))
        {
            await Assert.That(index).IsLessThan(full.Frames.Count);
            DemoFrame reference = full.Frames[index];
            await Assert.That(frame.FrameNumber).IsEqualTo(index);
            await Assert.That(frame.CommandKind).IsEqualTo(reference.CommandKind);
            await Assert.That(frame.ServerTick).IsEqualTo(reference.ServerTick);
            await Assert.That(frame.GameTick).IsEqualTo(reference.GameTick);
            await Assert.That(frame.RawStart).IsEqualTo(reference.RawStart);
            await Assert.That(frame.RawLength).IsEqualTo(reference.RawLength);
            await Assert.That(frame.HeaderLength).IsEqualTo(reference.HeaderLength);
            await Assert.That(frame.IsCompressed).IsEqualTo(reference.IsCompressed);
            await Assert.That(frame.UserCmdsPayloadCount).IsEqualTo(reference.UserCmdsPayloadCount);
            await Assert.That(frame.DecodedMessages.Count).IsEqualTo(reference.DecodedMessages.Count);
            for (int m = 0; m < reference.DecodedMessages.Count; m++)
            {
                NetMessage a = frame.DecodedMessages[m];
                NetMessage b = reference.DecodedMessages[m];
                await Assert.That(a.MessageTypeName).IsEqualTo(b.MessageTypeName);
                await Assert.That(a.Payload.GetType()).IsEqualTo(b.Payload.GetType());
                await Assert.That(a.DecompressedStart).IsEqualTo(b.DecompressedStart);
                await Assert.That(a.DecompressedLength).IsEqualTo(b.DecompressedLength);
                await Assert.That(a is GameEventMessage).IsEqualTo(b is GameEventMessage);
                if (a is GameEventMessage gem)
                {
                    events.Add(gem.DecodedEvent);
                }
            }

            index++;
        }

        await Assert.That(index).IsEqualTo(full.Frames.Count);
        await Assert.That(reader.EndReason).IsEqualTo(ReadEndReason.Stop);
        await Assert.That(reader.Progress!.Value).IsEqualTo(1.0).Within(0.001);

        await Assert.That(events.Count).IsEqualTo(full.AllGameEvents.Count);
        for (int i = 0; i < events.Count; i++)
        {
            GameEvent a = events[i];
            GameEvent b = full.AllGameEvents[i];
            await Assert.That(a.Name).IsEqualTo(b.Name);
            await Assert.That(a.EventId).IsEqualTo(b.EventId);
            await Assert.That(a.FrameNumber).IsEqualTo(b.FrameNumber);
            await Assert.That(a.ServerTick).IsEqualTo(b.ServerTick);
            await Assert.That(a.GameTick).IsEqualTo(b.GameTick);
            await Assert.That(a.Payload?.GetType()).IsEqualTo(b.Payload?.GetType());
        }

        IDemoEnrichmentView view = reader.Enrichment;
        await Assert.That(view.MapName).IsEqualTo(full.MapName);
        await Assert.That(view.ServerName).IsEqualTo(full.ServerName);
        await Assert.That(view.ClientName).IsEqualTo(full.ClientName);
        await Assert.That(view.BuildNumber).IsEqualTo(full.BuildNumber);
        await Assert.That(view.ServerStartTick).IsEqualTo(full.ServerStartTick);
        await Assert.That(view.TickInterval).IsEqualTo(full.TickInterval);
        await Assert.That(view.TickRate).IsEqualTo(full.TickRate);
        await Assert.That(view.TickCount).IsEqualTo(full.TickCount);
        await Assert.That(view.TickCountIsFinal).IsTrue();
        await Assert.That(view.Profile).IsEqualTo(full.Profile);
        await Assert.That(view.Schema is null).IsEqualTo(full.Schema is null);
        if (view.Schema is not null && full.Schema is not null)
        {
            await Assert.That(view.Schema.Serializers.Count).IsEqualTo(full.Schema.Serializers.Count);
        }

        await Assert.That(view.Players.Count).IsEqualTo(full.Players.Count);
        foreach ((int slot, PlayerInfo p) in full.Players)
        {
            await Assert.That(view.Players[slot]).IsEqualTo(p);
        }

        await Assert.That(string.Join("\n", view.Warnings.Select(w => $"{w.Code}|{w.Message}|{w.Count}")))
            .IsEqualTo(string.Join("\n", full.Warnings.Select(w => $"{w.Code}|{w.Message}|{w.Count}")));
        await Assert.That(view.Health).IsEqualTo(full.Health);

        DecodeProvenance p1 = reader.Provenance;
        DecodeProvenance p2 = full.Provenance;
        await Assert.That(p1.Source).IsEqualTo(DecodeSource.DemoReader);
        await Assert.That(p1.Mode).IsEqualTo(DecodeMode.Sequential);
        await Assert.That(p1.FramesRead).IsEqualTo(p2.FramesRead);
        await Assert.That(p1.MessagesDecoded).IsEqualTo(p2.MessagesDecoded);
        await Assert.That(p1.MessagesSkipped).IsEqualTo(p2.MessagesSkipped);
        await Assert.That(p1.UserCmdsStored).IsEqualTo(p2.UserCmdsStored);
        await Assert.That(p1.BytesDecompressed).IsEqualTo(p2.BytesDecompressed);

        DemoDescriptor snapshot = view.Snapshot();
        await Assert.That(snapshot).IsEqualTo(DemoDescriptor.From(full) with { Players = snapshot.Players });
    }

    [Test]
    public async Task Reader_SignonPrefixAndBaselineCheckpoint_MatchTheList()
    {
        string path = DemoTestHelper.RequireDemo();
        byte[] bytes = await File.ReadAllBytesAsync(path);
        ParsedDemo full = DemoTestHelper.GetOrParse(path);
        IDemoFrameSource list = full.AsFrameSource();

        using DemoReader reader = DemoReader.Open(bytes.AsMemory(), new ParseOptions { Plan = DecodePlan.EntityReplay });
        await Assert.That(reader.SignonPrefix.Count).IsEqualTo(0);
        int read = 0;
        while (reader.TryReadNext(out _))
        {
            read++;
        }

        await Assert.That(read).IsEqualTo(full.Frames.Count);
        await Assert.That(reader.SignonPrefix.Select(f => f.FrameNumber).ToArray())
            .IsEquivalentTo(list.SignonPrefix.Select(f => f.FrameNumber).ToArray());
        await Assert.That(reader.SignonPrefix.Count).IsGreaterThan(0);
        await Assert.That(reader.SignonPrefix.All(f => f.CommandKind != CS2OpenSchema.Protos.EDemoCommands.DemPacket)).IsTrue();

        while (list.TryReadNext(out _))
        {
        }

        await Assert.That(reader.LastInstanceBaselineFullPacket?.FrameNumber)
            .IsEqualTo(list.LastInstanceBaselineFullPacket?.FrameNumber);
        await Assert.That(reader.LastInstanceBaselineFullPacket).IsNotNull();
    }

    [Test]
    public async Task ReadFrames_Materialize_AndPeek_HonourTheirContracts()
    {
        string path = DemoTestHelper.RequireDemo();
        byte[] bytes = await File.ReadAllBytesAsync(path);
        ParsedDemo full = DemoTestHelper.GetOrParse(path);

        using (DemoReader materializing = DemoReader.Open(bytes.AsMemory(), new ParseOptions { Plan = DecodePlan.GameEventsOnly }))
        {
            ParsedDemo demo = materializing.Materialize();
            await Assert.That(demo.Frames.Count).IsEqualTo(full.Frames.Count);
            await Assert.That(demo.AllGameEvents.Count).IsEqualTo(full.AllGameEvents.Count);
            await Assert.That(demo.Plan).IsSameReferenceAs(DecodePlan.GameEventsOnly);
            await Assert.That(demo.Provenance.Source).IsEqualTo(DecodeSource.DemoParserParse);
            Assert.Throws<InvalidOperationException>(() => materializing.Materialize());
            Assert.Throws<InvalidOperationException>(() => materializing.Configure(DecodePlan.Everything));
        }

        using DemoReader reader = DemoReader.Open(bytes.AsMemory());
        await Assert.That(reader.TryPeekNext(out DemoFrame? peeked)).IsTrue();
        await Assert.That(reader.TryPeekNext(out DemoFrame? peekedAgain)).IsTrue();
        await Assert.That(peekedAgain).IsSameReferenceAs(peeked);
        await Assert.That(reader.TryReadNext(out DemoFrame? first)).IsTrue();
        await Assert.That(first).IsSameReferenceAs(peeked);
        Assert.Throws<InvalidOperationException>(() => reader.Configure(DecodePlan.StructureOnly));
        Assert.Throws<InvalidOperationException>(() => reader.ProbeGameEventNames());

        int rest = reader.ReadFrames().Count();
        await Assert.That(rest + 1).IsEqualTo(full.Frames.Count);
        await Assert.That(reader.TryReadNext(out _)).IsFalse();
        await Assert.That(reader.TryPeekNext(out _)).IsFalse();
    }

    [Test]
    public async Task Probe_ReportsTheVocabularyTheParseSaw()
    {
        string path = DemoTestHelper.RequireDemo();
        byte[] bytes = await File.ReadAllBytesAsync(path);
        ParsedDemo full = DemoTestHelper.GetOrParse(path);

        using DemoReader reader = DemoReader.Open(bytes.AsMemory());
        IReadOnlySet<string> probed = reader.ProbeGameEventNames();
        HashSet<string> expected = new(full.AllGameEvents.Select(e => e.Name), StringComparer.OrdinalIgnoreCase);
        await Assert.That(probed.Count).IsEqualTo(expected.Count);
        foreach (string name in expected)
        {
            await Assert.That(probed.Contains(name)).IsTrue().Because($"{name} fired in the demo");
        }

        // The probe is a rewind, not a read: the reader is still at the start.
        await Assert.That(reader.Position).IsEqualTo(16L);
        await Assert.That(reader.TryReadNext(out DemoFrame? first)).IsTrue();
        await Assert.That(first!.FrameNumber).IsEqualTo(0);
    }

    [Test]
    public async Task AsFrameSource_WalksTheListAndKeepsRandomAccess()
    {
        string path = DemoTestHelper.RequireDemo();
        ParsedDemo full = DemoTestHelper.GetOrParse(path);
        IDemoFrameSource source = full.AsFrameSource();

        await Assert.That(source.SupportsRandomAccess).IsTrue();
        await Assert.That(source.Frames).IsSameReferenceAs(full.Frames);
        await Assert.That(source.FrameCount).IsEqualTo(full.Frames.Count);
        await Assert.That(source.Progress).IsNull();
        await Assert.That(source.Enrichment.TickCountIsFinal).IsTrue();
        await Assert.That(source.Enrichment.Players).IsSameReferenceAs(full.Players);
        await Assert.That(source.Enrichment.Snapshot()).IsEqualTo(DemoDescriptor.From(full));

        await Assert.That(source.TryPeekNext(out DemoFrame? peeked)).IsTrue();
        await Assert.That(peeked).IsSameReferenceAs(full.Frames[0]);
        int index = 0;
        while (source.TryReadNext(out DemoFrame? frame))
        {
            await Assert.That(frame).IsSameReferenceAs(full.Frames[index++]);
        }

        await Assert.That(index).IsEqualTo(full.Frames.Count);
        await Assert.That(source.TryPeekNext(out _)).IsFalse();
        await Assert.That(source.LastInstanceBaselineFullPacket).IsNotNull();
    }
}
