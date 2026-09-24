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
