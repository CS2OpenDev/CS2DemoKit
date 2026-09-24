#region

using CS2DemoKit.Parser.EntityTracking;

#endregion

namespace CS2DemoKit.Parser.Tests.EntityTracking;

/// <summary>
///     The <c>codegen_delta_encoder</c> grammar that <see cref="UserCmdDelta" /> reads: ordinary tags,
///     wire type 7 as a reset, and repeated message fields addressed by index. Every blob here is
///     hand-encoded, so each test proves one rule on its own. The first block ports demoinfocs'
///     <c>s2_usercmd_delta_test.go</c>; the rest covers what this port adds or tightens.
/// </summary>
[Category("Unit")]
public class UserCmdDeltaTests
{
    private const int Reset = UserCmdDelta.ResetWireType;

    // ---- encoding helpers ----

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

    private static byte[] Tag(int field, int wire) => Varint(((ulong)field << 3) | (uint)wire);

    private static byte[] VarintField(int field, ulong value) => [.. Tag(field, 0), .. Varint(value)];

    private static byte[] BytesField(int field, byte[] value) =>
        [.. Tag(field, 2), .. Varint((ulong)value.Length), .. value];

    private static byte[] FloatField(int field, float value) =>
        [.. Tag(field, 5), .. BitConverter.GetBytes(value)];

    private static byte[] ResetMarker(int field) => Tag(field, Reset);

    private static byte[] InBase(params byte[][] parts) =>
        BytesField(CSGOUserCmdPB.BaseFieldNumber, [.. parts.SelectMany(p => p)]);

    private static byte[] SubtickOps(params byte[][] ops) =>
        InBase(BytesField(CBaseUserCmdPB.SubtickMovesFieldNumber, [.. ops.SelectMany(p => p)]));

    private static CSGOUserCmdPB Baseline() => new()
    {
        Base = new CBaseUserCmdPB
        {
            ClientTick = 100,
            Forwardmove = 250,
            Mousedx = 7,
            ButtonsPb = new CInButtonStatePB { Buttonstate1 = 512, Buttonstate2 = 3 }
        }
    };

    private static CSubtickMoveStep Step(ulong button) => new() { Button = button, Pressed = true };

    private static CSGOUserCmdPB WithSubticks(params ulong[] buttons)
    {
        CSGOUserCmdPB m = new() { Base = new CBaseUserCmdPB() };
        foreach (ulong b in buttons)
        {
            m.Base.SubtickMoves.Add(Step(b));
        }

        return m;
    }

    // ---- ported from demoinfocs ----

    [Test]
    public async Task ReplaceScalar_KeepsTheRestOfTheBaseline()
    {
        CSGOUserCmdPB m = Baseline();
        UserCmdDelta.Merge(m, InBase(VarintField(CBaseUserCmdPB.ClientTickFieldNumber, 101)));

        await Assert.That(m.Base.ClientTick).IsEqualTo(101);
        await Assert.That(m.Base.ButtonsPb.Buttonstate1).IsEqualTo(512ul);
        await Assert.That(m.Base.Forwardmove).IsEqualTo(250f);
    }

    [Test]
    public async Task MergeNested_ReplacesOnlyTheTouchedField()
    {
        CSGOUserCmdPB m = Baseline();
        UserCmdDelta.Merge(m, InBase(BytesField(CBaseUserCmdPB.ButtonsPbFieldNumber,
            VarintField(CInButtonStatePB.Buttonstate1FieldNumber, 0))));

        await Assert.That(m.Base.ButtonsPb.HasButtonstate1).IsTrue();
        await Assert.That(m.Base.ButtonsPb.Buttonstate1).IsEqualTo(0ul);
        await Assert.That(m.Base.ButtonsPb.Buttonstate2).IsEqualTo(3ul);
        await Assert.That(m.Base.ClientTick).IsEqualTo(100);
    }

    [Test]
    public async Task ResetScalar_ClearsItToUnset()
    {
        CSGOUserCmdPB m = Baseline();
        UserCmdDelta.Merge(m, InBase(BytesField(CBaseUserCmdPB.ButtonsPbFieldNumber,
            ResetMarker(CInButtonStatePB.Buttonstate1FieldNumber))));

        await Assert.That(m.Base.ButtonsPb.HasButtonstate1).IsFalse();
        await Assert.That(m.Base.ButtonsPb.Buttonstate1).IsEqualTo(0ul);
        await Assert.That(m.Base.ButtonsPb.Buttonstate2).IsEqualTo(3ul);
    }

    [Test]
    public async Task ResetNestedMessage_ClearsTheWholeMessage()
    {
        CSGOUserCmdPB m = Baseline();
        UserCmdDelta.Merge(m, InBase(ResetMarker(CBaseUserCmdPB.ButtonsPbFieldNumber)));

        await Assert.That(m.Base.ButtonsPb).IsNull();
        await Assert.That(m.Base.ClientTick).IsEqualTo(100);
    }

    [Test]
    public async Task ChainedDeltas_Accumulate()
    {
        CSGOUserCmdPB m = Baseline();
        UserCmdDelta.Merge(m, InBase(BytesField(CBaseUserCmdPB.ButtonsPbFieldNumber,
            VarintField(CInButtonStatePB.Buttonstate1FieldNumber, 0))));
        await Assert.That(m.Base.ButtonsPb.Buttonstate1).IsEqualTo(0ul);

        UserCmdDelta.Merge(m, InBase(BytesField(CBaseUserCmdPB.ButtonsPbFieldNumber,
            VarintField(CInButtonStatePB.Buttonstate1FieldNumber, 512))));
        await Assert.That(m.Base.ButtonsPb.Buttonstate1).IsEqualTo(512ul);
        await Assert.That(m.Base.ButtonsPb.Buttonstate2).IsEqualTo(3ul);
    }

    [Test]
    public async Task RepeatedIndexPatch_MergesIntoThatElementOnly()
    {
        CSGOUserCmdPB m = WithSubticks(1, 2);
        UserCmdDelta.Merge(m, SubtickOps(BytesField(1, VarintField(CSubtickMoveStep.PressedFieldNumber, 0))));

        await Assert.That(m.Base.SubtickMoves.Count).IsEqualTo(2);
        await Assert.That(m.Base.SubtickMoves[0].Pressed).IsTrue();
        await Assert.That(m.Base.SubtickMoves[0].Button).IsEqualTo(1ul);
        await Assert.That(m.Base.SubtickMoves[1].Pressed).IsFalse();
        await Assert.That(m.Base.SubtickMoves[1].Button).IsEqualTo(2ul);
    }

    [Test]
    public async Task RepeatedIndexZero_AndTruncate()
    {
        CSGOUserCmdPB m = WithSubticks(1, 2, 3);

        // Index 0 encodes as "field number 0", which a protobuf tag reader would reject.
        UserCmdDelta.Merge(m, SubtickOps(BytesField(0, VarintField(CSubtickMoveStep.ButtonFieldNumber, 9))));
        await Assert.That(m.Base.SubtickMoves[0].Button).IsEqualTo(9ul);

        UserCmdDelta.Merge(m, SubtickOps(Tag(2, Reset)));
        await Assert.That(m.Base.SubtickMoves.Count).IsEqualTo(2);
        await Assert.That(m.Base.SubtickMoves[1].Button).IsEqualTo(2ul);
    }

    [Test]
    public async Task RepeatedInputHistory_ElementMerge()
    {
        CSGOUserCmdPB m = new();
        m.InputHistory.Add(new CSGOInputHistoryEntryPB { FrameNumber = 10 });

        UserCmdDelta.Merge(m, BytesField(CSGOUserCmdPB.InputHistoryFieldNumber,
            BytesField(0, VarintField(CSGOInputHistoryEntryPB.FrameNumberFieldNumber, 11))));

        await Assert.That(m.InputHistory[0].FrameNumber).IsEqualTo(11);
    }

    // ---- this port ----

    [Test]
    public async Task Fixed32Float_InAnAbsentNestedMessage_CreatesIt()
    {
        CSGOUserCmdPB m = Baseline();
        UserCmdDelta.Merge(m, InBase(BytesField(CBaseUserCmdPB.ViewanglesFieldNumber,
            FloatField(CMsgQAngle.XFieldNumber, 12.5f))));

        await Assert.That(m.Base.Viewangles).IsNotNull();
        await Assert.That(m.Base.Viewangles.X).IsEqualTo(12.5f);
        await Assert.That(m.Base.Viewangles.HasY).IsFalse();
    }

    [Test]
    public async Task ResetOfATopLevelScalar_ClearsIt()
    {
        CSGOUserCmdPB m = Baseline();
        UserCmdDelta.Merge(m, InBase(ResetMarker(CBaseUserCmdPB.MousedxFieldNumber)));

        await Assert.That(m.Base.HasMousedx).IsFalse();
        await Assert.That(m.Base.ClientTick).IsEqualTo(100);
    }

    [Test]
    public async Task ResetOfARepeatedField_ClearsTheList()
    {
        CSGOUserCmdPB m = WithSubticks(1, 2, 3);
        UserCmdDelta.Merge(m, InBase(ResetMarker(CBaseUserCmdPB.SubtickMovesFieldNumber)));

        await Assert.That(m.Base.SubtickMoves.Count).IsEqualTo(0);
    }

    [Test]
    public async Task RepeatedGrow_AddsDefaultElements_ThenMerges()
    {
        CSGOUserCmdPB m = WithSubticks();

        UserCmdDelta.Merge(m, SubtickOps(
            Tag(2, Reset),
            BytesField(1, FloatField(CSubtickMoveStep.WhenFieldNumber, 0.5f))));

        await Assert.That(m.Base.SubtickMoves.Count).IsEqualTo(2);
        await Assert.That(m.Base.SubtickMoves[0].HasWhen).IsFalse();
        await Assert.That(m.Base.SubtickMoves[1].When).IsEqualTo(0.5f);
    }

    [Test]
    public async Task RepeatedResize_AtTheLimit_Grows_AndPastItThrows()
    {
        CSGOUserCmdPB m = WithSubticks();
        UserCmdDelta.Merge(m, SubtickOps(Tag(UserCmdDelta.RepeatedLimit, Reset)));
        await Assert.That(m.Base.SubtickMoves.Count).IsEqualTo(UserCmdDelta.RepeatedLimit);

        Assert.Throws<InvalidDataException>(() => UserCmdDelta.Merge(WithSubticks(), SubtickOps(Tag(UserCmdDelta.RepeatedLimit + 1, Reset))));
    }

    [Test]
    public async Task ElementMergePastTheEnd_Throws()
    {
        Assert.Throws<InvalidDataException>(() => UserCmdDelta.Merge(WithSubticks(1),
                SubtickOps(BytesField(1, VarintField(CSubtickMoveStep.ButtonFieldNumber, 4)))));
    }

    [Test]
    public async Task RepeatedOperation_WithAnotherWireType_Throws()
    {
        Assert.Throws<InvalidDataException>(() => UserCmdDelta.Merge(WithSubticks(1), SubtickOps(VarintField(0, 1))));
    }

    [Test]
    public async Task UnknownFields_AreSkippedForEveryWireType_AndCounted()
    {
        CSGOUserCmdPB m = Baseline();
        // Field 13 is unassigned in CBaseUserCmdPB and 100 in CSGOUserCmdPB.
        byte[] delta =
        [
            .. VarintField(100, 5),
            .. BytesField(100, [1, 2, 3]),
            .. ResetMarker(100),
            .. FloatField(100, 1f),
            .. InBase(ResetMarker(13), VarintField(CBaseUserCmdPB.ClientTickFieldNumber, 102))
        ];

        long skipped = 0;
        UserCmdDelta.Merge(m, delta, ref skipped);

        await Assert.That(skipped).IsEqualTo(5L);
        await Assert.That(m.Base.ClientTick).IsEqualTo(102);
        await Assert.That(m.Base.Mousedx).IsEqualTo(7);
    }

    [Test]
    public async Task StringScalar_Merges()
    {
        CSGOUserCmdPB m = Baseline();
        UserCmdDelta.Merge(m, InBase(BytesField(CBaseUserCmdPB.ExecutionNotesFieldNumber,
            BytesField(CBaseUserCmdExecutionNotes.IgnoredReasonFieldNumber, "late"u8.ToArray()))));

        await Assert.That(m.Base.ExecutionNotes.IgnoredReason).IsEqualTo("late");
    }

    [Test]
    public async Task TruncatedInput_Throws_AndACloneKeepsTheBaselineIntact()
    {
        CSGOUserCmdPB baseline = Baseline();
        byte[] whole = InBase(VarintField(CBaseUserCmdPB.ClientTickFieldNumber, 300));

        // Cut inside the varint value, inside the nested length, and inside the tag of a fixed32.
        byte[][] broken =
        [
            whole[..^1],
            [.. Tag(CSGOUserCmdPB.BaseFieldNumber, 2), 0x20, 0x10],
            [.. Tag(CSGOUserCmdPB.BaseFieldNumber, 2), 0x02, .. Tag(CBaseUserCmdPB.ForwardmoveFieldNumber, 5), 0x00],
            [0x80]
        ];

        foreach (byte[] bad in broken)
        {
            CSGOUserCmdPB copy = baseline.Clone();
            Assert.Throws<InvalidDataException>(() => UserCmdDelta.Merge(copy, bad));
        }

        await Assert.That(baseline).IsEqualTo(Baseline());
    }

    [Test]
    public async Task SetThenResetOfOneField_EndsCleared()
    {
        CSGOUserCmdPB m = Baseline();
        UserCmdDelta.Merge(m, InBase(
            VarintField(CBaseUserCmdPB.MousedxFieldNumber, 9),
            VarintField(CBaseUserCmdPB.ClientTickFieldNumber, 101),
            ResetMarker(CBaseUserCmdPB.MousedxFieldNumber)));

        await Assert.That(m.Base.HasMousedx).IsFalse();
        await Assert.That(m.Base.ClientTick).IsEqualTo(101);
    }

    [Test]
    public async Task ResetThenSetOfOneField_EndsSet()
    {
        CSGOUserCmdPB m = Baseline();
        UserCmdDelta.Merge(m, InBase(
            ResetMarker(CBaseUserCmdPB.MousedxFieldNumber),
            VarintField(CBaseUserCmdPB.MousedxFieldNumber, 9)));

        await Assert.That(m.Base.Mousedx).IsEqualTo(9);
    }

    [Test]
    public async Task FieldNumberZero_Throws()
    {
        Assert.Throws<InvalidDataException>(() => UserCmdDelta.Merge(Baseline(), VarintField(0, 1)));
    }

    [Test]
    public async Task GroupWireTypes_Throw()
    {
        Assert.Throws<InvalidDataException>(() => UserCmdDelta.Merge(Baseline(), Tag(100, 3)));
    }
}
