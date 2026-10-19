#region

using CS2DemoKit.Parser.EntityTracking;
using Google.Protobuf;

#endregion

namespace CS2DemoKit.Parser.Tests.EntityTracking;

/// <summary>
///     The input history a <see cref="UserCmdReconstructor" /> keeps with <c>TrackInputs</c>: the
///     frame tick <c>IN_JUMP</c> last rose on, against the slot's previous command or in a sub-tick
///     step, and the share of the last 64 ticks that carried a command. Synthetic frames, no demo.
/// </summary>
[Category("Unit")]
public class UserCmdInputHistoryTests
{
    private const int Slot = 3;
    private const ulong Jump = 1ul << 1;

    private static CMsgServerUserCmd Keyframe(int cmdNumber, ulong buttons = 0, bool subtickJump = false, int slot = Slot)
    {
        CSGOUserCmdPB full = new()
        {
            Base = new CBaseUserCmdPB { ClientTick = cmdNumber, ButtonsPb = new CInButtonStatePB { Buttonstate1 = buttons } }
        };
        if (subtickJump)
        {
            full.Base.SubtickMoves.Add(new CSubtickMoveStep { Button = Jump, Pressed = true, When = 0.5f });
        }

        return new CMsgServerUserCmd { CmdNumber = cmdNumber, PlayerSlot = slot, ClientTick = cmdNumber, Data = full.ToByteString() };
    }

    private static DemoFrame Frame(int tick, EDemoCommands kind = EDemoCommands.DemPacket, params CMsgServerUserCmd[] commands)
    {
        CSVCMsg_UserCommands payload = new();
        payload.Commands.AddRange(commands);
        return new DemoFrame
        {
            CommandKind = kind,
            FrameNumber = tick,
            HeaderLength = 0,
            IsCompressed = false,
            RawLength = 0,
            RawStart = 0,
            ServerTick = tick,
            MessageList = commands.Length == 0 ? [] : [new NetMessage { MessageTypeName = "svc_UserCmds", Payload = payload }]
        };
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task LastJumpTick_IsTheFrameTheButtonRose_OrASubtickPress(bool onDemand)
    {
        UserCmdReconstructor r = new() { OnDemand = onDemand, TrackInputs = true };
        await Assert.That(r.LastJumpTick(Slot)).IsEqualTo(-1);

        r.AdvanceOneFrame(Frame(10, commands: Keyframe(1)));
        await Assert.That(r.LastJumpTick(Slot)).IsEqualTo(-1);

        r.AdvanceOneFrame(Frame(11, commands: Keyframe(2, Jump)));
        await Assert.That(r.LastJumpTick(Slot)).IsEqualTo(11);

        // Held across a keyframe is not a second rise.
        r.AdvanceOneFrame(Frame(12, commands: Keyframe(3, Jump)));
        await Assert.That(r.LastJumpTick(Slot)).IsEqualTo(11);

        r.AdvanceOneFrame(Frame(13, commands: Keyframe(4)));
        r.AdvanceOneFrame(Frame(14, commands: Keyframe(5, subtickJump: true)));
        await Assert.That(r.LastJumpTick(Slot)).IsEqualTo(14);

        // A sub-tick press on a frame where the button state also rose is one rise, on that frame.
        r.AdvanceOneFrame(Frame(15, commands: Keyframe(6, Jump, subtickJump: true)));
        await Assert.That(r.LastJumpTick(Slot)).IsEqualTo(15);
        await Assert.That(r.LastJumpTick(Slot + 1)).IsEqualTo(-1).Because("another slot has its own history");
        await Assert.That(r.Current(Slot)!.Base.ClientTick).IsEqualTo(6).Because("the latest command is served without a rebuild");
    }

    [Test]
    public async Task Coverage_IsTheShareOfTheLast64Ticks_WithACommand()
    {
        UserCmdReconstructor r = new() { OnDemand = true, TrackInputs = true };
        await Assert.That(r.Coverage(Slot)).IsEqualTo(0f);

        for (int tick = 1; tick <= 16; tick++)
        {
            r.AdvanceOneFrame(Frame(tick, commands: Keyframe(tick)));
        }

        await Assert.That(r.Coverage(Slot)).IsEqualTo(16 / 64f);

        // The window ends at the frame last advanced, whether or not it carried a command.
        r.AdvanceOneFrame(Frame(70));
        await Assert.That(r.Coverage(Slot)).IsEqualTo(10 / 64f).Because("ticks 7 to 16 are inside (6, 70]");
        r.AdvanceOneFrame(Frame(81));
        await Assert.That(r.Coverage(Slot)).IsEqualTo(0f);

        // Several commands a frame count each, capped at one.
        for (int tick = 100; tick < 140; tick++)
        {
            r.AdvanceOneFrame(Frame(tick, EDemoCommands.DemPacket, Keyframe(tick * 2), Keyframe(tick * 2 + 1)));
        }

        await Assert.That(r.Coverage(Slot)).IsEqualTo(1f);
    }

    [Test]
    public async Task Snapshots_PrimeTheButtonsButCountForNothing()
    {
        UserCmdReconstructor r = new() { OnDemand = true, TrackInputs = true };
        r.AdvanceOneFrame(Frame(5, EDemoCommands.DemFullPacket, Keyframe(1, Jump)));
        await Assert.That(r.Coverage(Slot)).IsEqualTo(0f);
        await Assert.That(r.LastJumpTick(Slot)).IsEqualTo(-1);

        // The jump was already held in the snapshot, so the first real command does not rise.
        r.AdvanceOneFrame(Frame(6, commands: Keyframe(2, Jump)));
        await Assert.That(r.LastJumpTick(Slot)).IsEqualTo(-1);
        await Assert.That(r.Coverage(Slot)).IsEqualTo(1 / 64f);

        r.Reset();
        await Assert.That(r.Coverage(Slot)).IsEqualTo(0f);
        await Assert.That(r.LastJumpTick(Slot)).IsEqualTo(-1);
    }

    [Test]
    public async Task WithoutTrackInputs_TheHistoryReadsThrow()
    {
        UserCmdReconstructor r = new() { OnDemand = true };
        r.AdvanceOneFrame(Frame(10, commands: Keyframe(1, Jump)));
        Assert.Throws<InvalidOperationException>(() => r.LastJumpTick(Slot));
        Assert.Throws<InvalidOperationException>(() => r.Coverage(Slot));
        await Task.CompletedTask;
    }
}
