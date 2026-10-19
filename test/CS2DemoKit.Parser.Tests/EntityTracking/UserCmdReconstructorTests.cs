#region

using CS2DemoKit.Parser.EntityTracking;
using Google.Protobuf;

#endregion

namespace CS2DemoKit.Parser.Tests.EntityTracking;

/// <summary>
///     The per-slot state machine in <see cref="UserCmdReconstructor" />: keyframes seed, deltas apply to
///     the latest command, full-packet snapshots prime and self-check without being emitted, and
///     everything that cannot be rebuilt is counted rather than guessed. Synthetic commands, no demo.
/// </summary>
[Category("Unit")]
public class UserCmdReconstructorTests
{
    private const int Slot = 3;

    private static byte[] Varint(ulong v)
    {
        List<byte> b = [];
        while (v >= 0x80)
        {
            b.Add((byte)(v | 0x80));
            v >>= 7;
        }

        b.Add((byte)v);
        return [.. b];
    }

    private static byte[] Field(int number, int wire) => Varint(((ulong)number << 3) | (uint)wire);

    /// <summary>A delta that sets base.client_tick and base.mousedx.</summary>
    private static byte[] TickDelta(int clientTick, int mousedx = 0)
    {
        byte[] inner =
        [
            .. Field(CBaseUserCmdPB.ClientTickFieldNumber, 0), .. Varint((ulong)clientTick),
            .. Field(CBaseUserCmdPB.MousedxFieldNumber, 0), .. Varint((ulong)mousedx)
        ];
        return [.. Field(CSGOUserCmdPB.BaseFieldNumber, 2), .. Varint((ulong)inner.Length), .. inner];
    }

    private static CSGOUserCmdPB Full(int clientTick, int mousedx = 0) => new()
    {
        Base = new CBaseUserCmdPB { ClientTick = clientTick, Mousedx = mousedx, Forwardmove = 250 }
    };

    private static CMsgServerUserCmd Keyframe(int cmdNumber, int clientTick, int mousedx = 0, int slot = Slot) => new()
    {
        CmdNumber = cmdNumber,
        PlayerSlot = slot,
        ClientTick = clientTick,
        ServerTickExecuted = clientTick + 1,
        Data = Full(clientTick, mousedx).ToByteString()
    };

    private static CMsgServerUserCmd Delta(int cmdNumber, int clientTick, int mousedx = 0, int slot = Slot) => new()
    {
        CmdNumber = cmdNumber,
        PlayerSlot = slot,
        ClientTick = clientTick,
        ServerTickExecuted = clientTick + 1,
        DeltaData = ByteString.CopyFrom(TickDelta(clientTick, mousedx))
    };

    [Test]
    public async Task Keyframe_Seeds_AndADeltaBuildsOnIt()
    {
        UserCmdReconstructor r = new();

        await Assert.That(r.Apply(Keyframe(10, 100, 1), false, out CSGOUserCmdPB? first)).IsEqualTo(UserCmdApplyStatus.Full);
        await Assert.That(first!.Base.Mousedx).IsEqualTo(1);

        await Assert.That(r.Apply(Delta(11, 101, 5), false, out CSGOUserCmdPB? second)).IsEqualTo(UserCmdApplyStatus.Delta);
        await Assert.That(second!.Base.ClientTick).IsEqualTo(101);
        await Assert.That(second.Base.Mousedx).IsEqualTo(5);
        await Assert.That(second.Base.Forwardmove).IsEqualTo(250f).Because("inherited from the keyframe");
        await Assert.That(r.Current(Slot)).IsSameReferenceAs(second);

        await Assert.That(r.Stats).IsEqualTo(new UserCmdReconstructionStats { Full = 1, Delta = 1 });
    }

    [Test]
    public async Task DeltaForAnUnseenSlot_IsMissingBaseline_AndNotEmitted()
    {
        UserCmdReconstructor r = new();
        await Assert.That(r.Apply(Delta(11, 101), false, out CSGOUserCmdPB? cmd)).IsEqualTo(UserCmdApplyStatus.MissingBaseline);
        await Assert.That(cmd).IsNull();
        await Assert.That(r.Stats.MissingBaseline).IsEqualTo(1L);
        await Assert.That(r.Current(Slot)).IsNull();
    }

    [Test]
    public async Task DeltaBehindTheLatest_IsOutOfOrder()
    {
        UserCmdReconstructor r = new();
        r.Apply(Keyframe(10, 100), false, out _);
        await Assert.That(r.Apply(Delta(9, 99), false, out CSGOUserCmdPB? cmd)).IsEqualTo(UserCmdApplyStatus.OutOfOrder);
        await Assert.That(cmd).IsNull();
        await Assert.That(r.Current(Slot)!.Base.ClientTick).IsEqualTo(100);
    }

    [Test]
    public async Task KeyframeBehindTheLatest_ReseedsTheSlot_AndLaterDeltasBuildOnIt()
    {
        // A reused slot (reconnect, new player) restarts its command numbers with a keyframe.
        UserCmdReconstructor r = new();
        r.Apply(Keyframe(500, 1000, 1), false, out _);
        r.Apply(Delta(501, 1001, 2), false, out _);

        await Assert.That(r.Apply(Keyframe(3, 1200, 7), false, out CSGOUserCmdPB? reseeded)).IsEqualTo(UserCmdApplyStatus.Full);
        await Assert.That(reseeded!.Base.Mousedx).IsEqualTo(7);
        await Assert.That(r.Current(Slot)).IsSameReferenceAs(reseeded);

        await Assert.That(r.Apply(Delta(4, 1201, 8), false, out CSGOUserCmdPB? next)).IsEqualTo(UserCmdApplyStatus.Delta);
        await Assert.That(next!.Base.ClientTick).IsEqualTo(1201);
        await Assert.That(r.Apply(Delta(2, 1199), false, out _)).IsEqualTo(UserCmdApplyStatus.OutOfOrder)
            .Because("the order check now runs against the re-seeded number");
        await Assert.That(r.Stats.OutOfOrder).IsEqualTo(1L);
    }

    [Test]
    public async Task GapLargerThanOne_StillAppliesAgainstTheLatest()
    {
        UserCmdReconstructor r = new();
        r.Apply(Keyframe(10, 100), false, out _);
        await Assert.That(r.Apply(Delta(14, 104, 2), false, out CSGOUserCmdPB? cmd)).IsEqualTo(UserCmdApplyStatus.Delta);
        await Assert.That(cmd!.Base.ClientTick).IsEqualTo(104);
    }

    [Test]
    public async Task MalformedDelta_UnprimesTheSlot_UntilTheNextKeyframe()
    {
        UserCmdReconstructor r = new();
        r.Apply(Keyframe(10, 100), false, out _);

        CMsgServerUserCmd broken = Delta(11, 101);
        broken.DeltaData = ByteString.CopyFrom(0x0A, 0x7F); // base, length 127, nothing follows
        await Assert.That(r.Apply(broken, false, out CSGOUserCmdPB? none)).IsEqualTo(UserCmdApplyStatus.DecodeFailed);
        await Assert.That(none).IsNull();

        await Assert.That(r.Apply(Delta(12, 102), false, out _)).IsEqualTo(UserCmdApplyStatus.MissingBaseline);
        await Assert.That(r.Apply(Keyframe(13, 103), false, out _)).IsEqualTo(UserCmdApplyStatus.Full);
        await Assert.That(r.Apply(Delta(14, 104), false, out _)).IsEqualTo(UserCmdApplyStatus.Delta);

        UserCmdReconstructionStats s = r.Stats;
        await Assert.That(s.DecodeFailed).IsEqualTo(1L);
        await Assert.That(s.MissingBaseline).IsEqualTo(1L);
    }

    [Test]
    public async Task FullPacketDuplicate_IsCompared_AndNotEmitted()
    {
        UserCmdReconstructor r = new();
        r.Apply(Keyframe(10, 100), false, out _);
        r.Apply(Delta(11, 101, 4), false, out _);

        await Assert.That(r.Apply(Keyframe(11, 101, 4), true, out CSGOUserCmdPB? same))
            .IsEqualTo(UserCmdApplyStatus.CheckpointDuplicate);
        await Assert.That(same).IsNull();
        await Assert.That(r.Stats.CheckpointMismatches).IsEqualTo(0L);

        await Assert.That(r.Apply(Keyframe(11, 101, 9), true, out _)).IsEqualTo(UserCmdApplyStatus.CheckpointDuplicate);
        await Assert.That(r.Stats.CheckpointMismatches).IsEqualTo(1L);
        await Assert.That(r.Current(Slot)!.Base.Mousedx).IsEqualTo(9).Because("the server's snapshot wins");
        await Assert.That(r.Stats.CheckpointDuplicates).IsEqualTo(2L);
    }

    [Test]
    public async Task AfterReset_AFullPacketPrimes_AndLaterDeltasDecode()
    {
        UserCmdReconstructor r = new();
        r.Apply(Keyframe(10, 100), false, out _);
        r.Reset();
        await Assert.That(r.Stats).IsEqualTo(default(UserCmdReconstructionStats));
        await Assert.That(r.Current(Slot)).IsNull();

        await Assert.That(r.Apply(Keyframe(50, 500, 3), true, out CSGOUserCmdPB? primed))
            .IsEqualTo(UserCmdApplyStatus.CheckpointPrimed);
        await Assert.That(primed).IsNull();

        await Assert.That(r.Apply(Delta(51, 501), false, out CSGOUserCmdPB? next)).IsEqualTo(UserCmdApplyStatus.Delta);
        await Assert.That(next!.Base.Forwardmove).IsEqualTo(250f);
    }

    [Test]
    public async Task FullPacketAheadOfTheLatest_Reprimes()
    {
        UserCmdReconstructor r = new();
        r.Apply(Keyframe(10, 100), false, out _);
        await Assert.That(r.Apply(Keyframe(20, 200, 6), true, out _)).IsEqualTo(UserCmdApplyStatus.CheckpointPrimed);
        await Assert.That(r.Current(Slot)!.Base.Mousedx).IsEqualTo(6);
    }

    [Test]
    public async Task DataAndDelta_ParsesData_ThenAppliesTheDelta()
    {
        UserCmdReconstructor r = new();
        CMsgServerUserCmd both = Keyframe(10, 100, 1);
        both.DeltaData = ByteString.CopyFrom(TickDelta(100, 8));

        await Assert.That(r.Apply(both, false, out CSGOUserCmdPB? cmd)).IsEqualTo(UserCmdApplyStatus.Full);
        await Assert.That(cmd!.Base.Mousedx).IsEqualTo(8);
    }

    [Test]
    public async Task NeitherPayload_IsEmpty()
    {
        UserCmdReconstructor r = new();
        await Assert.That(r.Apply(new CMsgServerUserCmd { CmdNumber = 1, PlayerSlot = Slot }, false, out _))
            .IsEqualTo(UserCmdApplyStatus.Empty);
        await Assert.That(r.Apply(Keyframe(1, 1, slot: -1), false, out _)).IsEqualTo(UserCmdApplyStatus.Empty);
        await Assert.That(r.Stats.Empty).IsEqualTo(2L);
    }

    [Test]
    public async Task ClientTickMismatch_IsCounted_AndStillEmitted()
    {
        UserCmdReconstructor r = new();
        r.Apply(Keyframe(10, 100), false, out _);
        CMsgServerUserCmd off = Delta(11, 101);
        off.ClientTick = 999;

        await Assert.That(r.Apply(off, false, out CSGOUserCmdPB? cmd)).IsEqualTo(UserCmdApplyStatus.Delta);
        await Assert.That(cmd).IsNotNull();
        await Assert.That(r.Stats.ClientTickMismatches).IsEqualTo(1L);
    }

    [Test]
    public async Task EmittedCommands_AreDistinct_AndLaterDeltasLeaveThemAlone()
    {
        UserCmdReconstructor r = new();
        r.Apply(Keyframe(10, 100, 1), false, out CSGOUserCmdPB? a);
        r.Apply(Delta(11, 101, 2), false, out CSGOUserCmdPB? b);
        r.Apply(Delta(12, 102, 3), false, out CSGOUserCmdPB? c);

        await Assert.That(b).IsNotSameReferenceAs(a);
        await Assert.That(c).IsNotSameReferenceAs(b);
        await Assert.That(a!.Base.Mousedx).IsEqualTo(1);
        await Assert.That(b!.Base.Mousedx).IsEqualTo(2);
        await Assert.That(c!.Base.Mousedx).IsEqualTo(3);
    }

    [Test]
    public async Task SlotsAreIndependent()
    {
        UserCmdReconstructor r = new();
        r.Apply(Keyframe(10, 100, 1, slot: 0), false, out _);
        r.Apply(Keyframe(70, 700, 7, slot: 1), false, out _);

        await Assert.That(r.Apply(Delta(71, 701, slot: 1), false, out CSGOUserCmdPB? one)).IsEqualTo(UserCmdApplyStatus.Delta);
        await Assert.That(r.Apply(Delta(11, 101, slot: 0), false, out CSGOUserCmdPB? zero)).IsEqualTo(UserCmdApplyStatus.Delta);
        await Assert.That(one!.Base.ClientTick).IsEqualTo(701);
        await Assert.That(zero!.Base.ClientTick).IsEqualTo(101);
    }

    [Test]
    public async Task AdvanceOneFrame_ReadsHandBuiltFrames_AndSkipsFullPacketCommands()
    {
        UserCmdReconstructor r = new();

        CSVCMsg_UserCommands packet = new();
        packet.Commands.Add(Keyframe(10, 100, 1));
        packet.Commands.Add(Delta(11, 101, 2));
        CSVCMsg_UserCommands deferred = new();
        deferred.Commands.Add(Delta(12, 102, 3));

        DemoFrame first = Frame(EDemoCommands.DemPacket,
            new NetMessage { MessageTypeName = "svc_UserCmds", Payload = packet },
            new NetMessage
            {
                MessageTypeName = "svc_UserCmds",
                Payload = DeferredMessage.Defer(CSVCMsg_UserCommands.Parser, deferred.ToByteArray())
            });

        IReadOnlyList<ReconstructedUserCmd> out1 = r.AdvanceOneFrame(first);
        await Assert.That(out1.Select(c => c.CmdNumber).SequenceEqual([10, 11, 12])).IsTrue();
        await Assert.That(out1.Select(c => c.FromDelta).SequenceEqual([false, true, true])).IsTrue();
        await Assert.That(out1[2].Command.Base.Mousedx).IsEqualTo(3);
        await Assert.That(out1[2].ServerTickExecuted).IsEqualTo(103);
        await Assert.That(out1[2].PlayerSlot).IsEqualTo(Slot);

        CSVCMsg_UserCommands snapshot = new();
        snapshot.Commands.Add(Keyframe(12, 102, 3));
        IReadOnlyList<ReconstructedUserCmd> out2 = r.AdvanceOneFrame(Frame(EDemoCommands.DemFullPacket,
            new NetMessage { MessageTypeName = "svc_UserCmds", Payload = snapshot }));

        await Assert.That(out2.Count).IsEqualTo(0);
        await Assert.That(r.Stats.CheckpointDuplicates).IsEqualTo(1L);
        await Assert.That(r.Stats.CheckpointMismatches).IsEqualTo(0L);
    }

    [Test]
    public async Task OnDemand_RebuildsOnRead_AndEmitsNothing()
    {
        UserCmdReconstructor r = new() { OnDemand = true };

        IReadOnlyList<ReconstructedUserCmd> emitted =
            r.AdvanceOneFrame(Packet(Keyframe(10, 100, 1), Delta(11, 101, 2), Delta(12, 102, 3)));
        await Assert.That(emitted.Count).IsEqualTo(0);
        await Assert.That(r.Stats).IsEqualTo(default(UserCmdReconstructionStats)).Because("nothing is parsed before a read");

        CSGOUserCmdPB? read = r.Current(Slot);
        await Assert.That(read!.Base.Mousedx).IsEqualTo(3);
        await Assert.That(read.Base.ClientTick).IsEqualTo(102);
        await Assert.That(r.Stats.Full).IsEqualTo(1L);
        await Assert.That(r.Stats.Delta).IsEqualTo(2L);

        r.AdvanceOneFrame(Packet(Delta(13, 103, 4)));
        await Assert.That(read.Base.Mousedx).IsEqualTo(3).Because("a queued delta waits for the next read");
        await Assert.That(r.Current(Slot)).IsSameReferenceAs(read).Because("a later rebuild updates the instance in place");
        await Assert.That(read.Base.Mousedx).IsEqualTo(4);
    }

    [Test]
    public async Task OnDemand_KeyframeDropsTheQueueBeforeIt_AndReadsLikeEager()
    {
        DemoFrame[] frames = [Packet(Keyframe(10, 100, 1), Delta(11, 101, 2)), Packet(Keyframe(20, 200, 9), Delta(21, 201, 8))];
        UserCmdReconstructor eager = new(), lazy = new() { OnDemand = true };
        foreach (DemoFrame frame in frames)
        {
            eager.AdvanceOneFrame(frame);
            lazy.AdvanceOneFrame(frame);
        }

        await Assert.That(lazy.Current(Slot)).IsEqualTo(eager.Current(Slot));
        await Assert.That(lazy.Current(Slot)!.Base.Mousedx).IsEqualTo(8);
        await Assert.That(eager.Stats.Full).IsEqualTo(2L);
        await Assert.That(lazy.Stats.Full).IsEqualTo(1L);
        await Assert.That(lazy.Stats.Delta).IsEqualTo(1L);
    }

    [Test]
    public async Task OnDemand_MatchesEager_AcrossSnapshots_OutOfOrder_BadDeltas_AndARestart()
    {
        CMsgServerUserCmd broken = Delta(13, 103);
        broken.DeltaData = ByteString.CopyFrom(0x0A, 0x7F);
        DemoFrame[] frames =
        [
            Packet(Keyframe(10, 100, 1), Delta(11, 101, 2)),
            Packet(Delta(12, 102, 3)),
            Snapshot(Keyframe(12, 102, 3)),
            Packet(Delta(9, 99, 7)),
            Packet(broken),
            Packet(Delta(14, 104, 5)),
            Snapshot(Keyframe(14, 104, 6)),
            Packet(Delta(15, 105, 4)),
            Packet(Keyframe(2, 2, 1), Delta(3, 3, 2))
        ];

        UserCmdReconstructor eager = new(), everyFrame = new() { OnDemand = true }, atTheEnd = new() { OnDemand = true };
        foreach (DemoFrame frame in frames)
        {
            eager.AdvanceOneFrame(frame);
            everyFrame.AdvanceOneFrame(frame);
            atTheEnd.AdvanceOneFrame(frame);
            await Assert.That(everyFrame.Current(Slot)).IsEqualTo(eager.Current(Slot));
        }

        await Assert.That(everyFrame.Stats).IsEqualTo(eager.Stats);
        await Assert.That(atTheEnd.Current(Slot)).IsEqualTo(eager.Current(Slot));
        await Assert.That(atTheEnd.Current(Slot)!.Base.Mousedx).IsEqualTo(2);
        await Assert.That(atTheEnd.Stats.Full).IsEqualTo(1L).Because("the restart keyframe dropped everything queued before it");
        await Assert.That(atTheEnd.Stats.Delta).IsEqualTo(1L);
    }

    [Test]
    public async Task OnDemand_ReadsStoredPayloads_LikeEager()
    {
        // Parse-time frames carry their payloads in the store, which on demand reads without protobuf.
        CSVCMsg_UserCommands one = new();
        one.Commands.Add(Keyframe(10, 100, 1));
        one.Commands.Add(Delta(11, 101, 2));
        CSVCMsg_UserCommands two = new();
        two.Commands.Add(Delta(12, 102, 3, slot: 0));
        two.Commands.Add(new CMsgServerUserCmd { CmdNumber = 1, PlayerSlot = Slot });

        UserCmdsWriter writer = new();
        writer.BeginFrame();
        writer.Append(one.ToByteArray(), 0);
        writer.Append(two.ToByteArray(), 1);
        (byte[]? block, int offset, int count) = writer.EndFrame();
        DemoFrame frame = new()
        {
            CommandKind = EDemoCommands.DemPacket,
            FrameNumber = 0,
            HeaderLength = 0,
            IsCompressed = false,
            RawLength = 0,
            RawStart = 0,
            ServerTick = 0,
            MessageList = [],
            UserCmdsBlock = block,
            UserCmdsOffset = offset,
            UserCmdsCount = count
        };

        UserCmdReconstructor eager = new(), lazy = new() { OnDemand = true };
        eager.AdvanceOneFrame(frame);
        await Assert.That(lazy.AdvanceOneFrame(frame).Count).IsEqualTo(0);
        await Assert.That(lazy.Current(Slot)).IsEqualTo(eager.Current(Slot));
        await Assert.That(lazy.Current(Slot)!.Base.Mousedx).IsEqualTo(2);
        await Assert.That(lazy.Current(0)).IsNull();
        await Assert.That(lazy.Stats).IsEqualTo(eager.Stats);
        await Assert.That(lazy.Stats.MissingBaseline).IsEqualTo(1L);
        await Assert.That(lazy.Stats.Empty).IsEqualTo(1L);
    }

    [Test]
    public async Task OnDemand_Apply_RebuildsTheQueueFirst()
    {
        UserCmdReconstructor r = new() { OnDemand = true };
        r.AdvanceOneFrame(Packet(Keyframe(10, 100, 1), Delta(11, 101, 2)));

        await Assert.That(r.Apply(Delta(12, 102, 3), false, out CSGOUserCmdPB? cmd)).IsEqualTo(UserCmdApplyStatus.Delta);
        await Assert.That(cmd!.Base.Mousedx).IsEqualTo(3);
        await Assert.That(r.Stats.Full).IsEqualTo(1L);
        await Assert.That(r.Stats.Delta).IsEqualTo(2L);
    }

    [Test]
    public async Task OnDemand_LongUnreadQueue_IsCaughtUpFromTheOldestFrames()
    {
        // Past the queue bound the oldest frames are applied to every slot, so a late read still
        // sees every delta and nothing was dropped.
        UserCmdReconstructor eager = new(), lazy = new() { OnDemand = true };
        DemoFrame first = Packet(Keyframe(10, 100, 1));
        eager.AdvanceOneFrame(first);
        lazy.AdvanceOneFrame(first);
        for (int i = 1; i < 5000; i++)
        {
            DemoFrame frame = Packet(Delta(10 + i, 100 + i, i % 7));
            eager.AdvanceOneFrame(frame);
            lazy.AdvanceOneFrame(frame);
        }

        await Assert.That(lazy.Current(Slot)).IsEqualTo(eager.Current(Slot));
        await Assert.That(lazy.Current(Slot)!.Base.ClientTick).IsEqualTo(5099);
        await Assert.That(lazy.Stats).IsEqualTo(eager.Stats);
        await Assert.That(lazy.Stats.Delta).IsEqualTo(4999L);
    }

    [Test]
    public async Task OnDemand_BoundedQueue_AppliesTheOldestFramesToEverySlot()
    {
        // Two slots on keyframes every frame, a third that joins and leaves inside one flushed
        // range, read only at the end: each holds what the eager mode holds.
        UserCmdReconstructor eager = new(), lazy = new() { OnDemand = true, OnDemandFrameBound = 8 };
        for (int i = 0; i < 100; i++)
        {
            List<CMsgServerUserCmd> commands = [Keyframe(10 + i, 100 + i, i, slot: 1), Delta(20 + i, 200 + i, i + 1, slot: 2)];
            if (i % 10 == 0)
            {
                commands.Add(Keyframe(20 + i, 200 + i, i, slot: 2));
            }

            if (i is >= 40 and < 44)
            {
                commands.Add(i == 40 ? Keyframe(i, i, 7, slot: 5) : Delta(i, i, 7, slot: 5));
            }

            DemoFrame frame = i % 25 == 24 ? Snapshot(Keyframe(10 + i, 100 + i, i, slot: 1)) : Packet([.. commands]);
            eager.AdvanceOneFrame(frame);
            lazy.AdvanceOneFrame(frame);
        }

        foreach (int slot in new[] { 1, 2, 5, 9 })
        {
            await Assert.That(lazy.Current(slot)).IsEqualTo(eager.Current(slot));
        }

        await Assert.That(lazy.Current(5)!.Base.ClientTick).IsEqualTo(43);
        await Assert.That(lazy.Stats.Full).IsLessThan(eager.Stats.Full).Because("commands before a keyframe are skipped");
        await Assert.That(lazy.Stats.DecodeFailed + lazy.Stats.MissingBaseline + lazy.Stats.OutOfOrder).IsEqualTo(0L);
    }

    [Test]
    public async Task OnDemand_BoundedQueue_AppliesAFlushedRangeOfOnlySnapshots()
    {
        // The second flush covers frames 2 and 3, which hold only full-packet commands: one
        // overwrites slot 3's stale baseline, the other primes slot 9 for the first time.
        UserCmdReconstructor eager = new(), lazy = new() { OnDemand = true, OnDemandFrameBound = 4 };
        DemoFrame[] frames =
        [
            Packet(Keyframe(1, 1, 0, slot: 3)),
            Packet(),
            Snapshot(Keyframe(1, 1, 9, slot: 3), Keyframe(5, 5, 7, slot: 9)),
            Snapshot(),
            Packet(),
            Packet(),
            Packet(),
        ];
        foreach (DemoFrame frame in frames)
        {
            eager.AdvanceOneFrame(frame);
            lazy.AdvanceOneFrame(frame);
        }

        await Assert.That(eager.Current(3)!.Base.Mousedx).IsEqualTo(9);
        await Assert.That(eager.Current(9)).IsNotNull();
        await Assert.That(lazy.Current(3)).IsEqualTo(eager.Current(3));
        await Assert.That(lazy.Current(9)).IsEqualTo(eager.Current(9));
    }

    private static DemoFrame Packet(params CMsgServerUserCmd[] commands) => Frame(EDemoCommands.DemPacket, Message(commands));

    private static DemoFrame Snapshot(params CMsgServerUserCmd[] commands) =>
        Frame(EDemoCommands.DemFullPacket, Message(commands));

    private static NetMessage Message(CMsgServerUserCmd[] commands)
    {
        CSVCMsg_UserCommands payload = new();
        payload.Commands.AddRange(commands);
        return new NetMessage { MessageTypeName = "svc_UserCmds", Payload = payload };
    }

    private static DemoFrame Frame(EDemoCommands kind, params NetMessage[] messages) => new()
    {
        CommandKind = kind,
        FrameNumber = 0,
        HeaderLength = 0,
        IsCompressed = false,
        RawLength = 0,
        RawStart = 0,
        ServerTick = 0,
        MessageList = [.. messages]
    };
}
