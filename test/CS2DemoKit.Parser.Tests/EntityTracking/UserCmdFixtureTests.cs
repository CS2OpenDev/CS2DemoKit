#region

using System.Buffers.Binary;
using System.Collections;
using CS2DemoKit.Parser.EntityTracking;
using CS2DemoKit.Parser.GameEvents;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using TUnit.Core.Exceptions;

#endregion

namespace CS2DemoKit.Parser.Tests.EntityTracking;

/// <summary>
///     Real <c>delta_data</c> commands on a bare clone. The committed sample demo carries no
///     <c>svc_UserCmds</c>, so a short window of a build-10896 matchmaking demo is committed instead:
///     one <c>DEM_FullPacket</c>'s snapshots followed by the next packet frames' commands, as the raw
///     <c>CSVCMsg_UserCommands</c> payloads.
///     <para>
///         Replaying it proves that real deltas rebuild exactly: every rebuilt <c>base.client_tick</c>
///         equals the outer <c>client_tick</c>, nothing fails, and the opening snapshots prime every
///         slot. It also proves the window exercises each rule of the delta grammar, so a regression
///         in any one of them fails here.
///     </para>
///     <para>
///         <b>Fixture format</b> (<c>tests/fixtures/usercmds-delta/&lt;demo-id&gt;.cmds.bin</c>): records of
///         <c>[u8 frame kind: 0 packet, 1 full packet][u32 LE length][CSVCMsg_UserCommands payload]</c>.
///         Re-pin with <c>PIN_USERCMDS=1</c> and <c>DEMO_PATH</c> pointing at a demo on build 10896 or
///         later; the demo is read in place.
///     </para>
/// </summary>
[Category("Unit")]
public class UserCmdFixtureTests
{
    /// <summary>Payloads to take, counting the opening full packet's, before widening for coverage.</summary>
    private const int WindowPayloads = 1500;

    /// <summary>How far the pin may widen the window looking for a missing grammar case.</summary>
    private const int MaxWindowPayloads = 20000;

    private const byte PacketKind = 0;
    private const byte FullPacketKind = 1;

    private static bool Repin =>
        string.Equals(Environment.GetEnvironmentVariable("PIN_USERCMDS"), "1", StringComparison.Ordinal);

    private static string FixtureDir()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "CS2DemoKit.slnx")))
        {
            dir = dir.Parent;
        }

        if (dir is null)
        {
            throw new InvalidOperationException("repo root (CS2DemoKit.slnx) not found above the test assembly");
        }

        return Path.Combine(dir.FullName, "tests", "fixtures", "usercmds-delta");
    }

    public static IEnumerable<string> Fixtures()
    {
        string dir = FixtureDir();
        return Directory.Exists(dir)
            ? Directory.EnumerateFiles(dir, "*.cmds.bin").OrderBy(p => p, StringComparer.Ordinal).Select(p => Path.GetFileName(p))
            : [];
    }

    [Test]
    [MethodDataSource(nameof(Fixtures))]
    public async Task PinnedWindow_RebuildsEveryCommand(string fixture)
    {
        List<(byte Kind, byte[] Payload)> records = Read(Path.Combine(FixtureDir(), fixture));
        await Assert.That(records.Count).IsGreaterThan(1);
        await Assert.That(records[0].Kind).IsEqualTo(FullPacketKind).Because("the window opens on a full packet");

        Replay replay = Replay.Run(records);
        UserCmdReconstructionStats s = replay.Stats;
        Console.WriteLine($"{fixture}: {replay.Describe()}");

        await Assert.That(s.CheckpointPrimed).IsEqualTo((long)replay.OpeningSnapshots)
            .Because("every snapshot in the opening full packet primes its slot");
        await Assert.That(s.Delta).IsGreaterThan(s.Full * 20).Because("the window is from a delta-era demo");
        await Assert.That(s.DecodeFailed).IsEqualTo(0L);
        await Assert.That(s.MissingBaseline).IsEqualTo(0L);
        await Assert.That(s.OutOfOrder).IsEqualTo(0L);
        await Assert.That(s.CheckpointMismatches).IsEqualTo(0L);
        await Assert.That(s.ClientTickMismatches).IsEqualTo(0L);
        await Assert.That(replay.Emitted).IsEqualTo(s.Full + s.Delta);

        foreach (string missing in replay.Coverage.Missing())
        {
            Assert.Fail($"the pinned window never exercises '{missing}'");
        }
    }

    [Test]
    public async Task Pin_WritesTheFixture()
    {
        if (!Repin)
        {
            throw new SkipTestException("deliberate re-pin only: set PIN_USERCMDS=1 and DEMO_PATH");
        }

        string? path = Environment.GetEnvironmentVariable("DEMO_PATH");
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            throw new InvalidOperationException("PIN_USERCMDS=1 needs DEMO_PATH set to a build 10896+ demo");
        }

        List<(byte Kind, byte[] Payload)> all = CaptureAfterFirstFreezeEnd(path, MaxWindowPayloads);
        await Assert.That(all.Count).IsGreaterThan(1);

        // The smallest window of at least WindowPayloads payloads that covers every rule.
        int take = Math.Min(all.Count, WindowPayloads);
        while (Replay.Run(all.GetRange(0, take)).Coverage.Missing().Any() && take < all.Count)
        {
            take = Math.Min(all.Count, take + 250);
        }

        List<(byte Kind, byte[] Payload)> window = all.GetRange(0, take);
        Replay replay = Replay.Run(window);
        await Assert.That(replay.Coverage.Missing().Any()).IsFalse()
            .Because($"no window up to {MaxWindowPayloads} payloads covers the grammar: {replay.Describe()}");

        Directory.CreateDirectory(FixtureDir());
        string target = Path.Combine(FixtureDir(), Path.GetFileNameWithoutExtension(path) + ".cmds.bin");
        using (FileStream file = File.Create(target))
        {
            Span<byte> length = stackalloc byte[4];
            foreach ((byte kind, byte[] payload) in window)
            {
                file.WriteByte(kind);
                BinaryPrimitives.WriteUInt32LittleEndian(length, (uint)payload.Length);
                file.Write(length);
                file.Write(payload);
            }
        }

        Console.WriteLine($"wrote {target}: {window.Count} payloads, {new FileInfo(target).Length} bytes; {replay.Describe()}");
        throw new SkipTestException($"Re-pinned {Path.GetFileName(target)}. Review the diff before committing.");
    }

    /// <summary>
    ///     The payloads of the first full packet after the first <c>round_freeze_end</c>, then those of
    ///     the packet frames that follow, up to <paramref name="limit" /> in all.
    /// </summary>
    private static List<(byte Kind, byte[] Payload)> CaptureAfterFirstFreezeEnd(string path, int limit)
    {
        DecodePlan plan = new()
        {
            Categories = MessageCategories.Header | MessageCategories.UserCmds | MessageCategories.GameEvents
                         | MessageCategories.StringTables
        };

        List<(byte, byte[])> captured = [];
        bool live = false, opened = false;
        using DemoReader reader = DemoReader.OpenFile(path, new ParseOptions { Plan = plan });
        while (captured.Count < limit && reader.TryReadNext(out DemoFrame? frame))
        {
            if (!live)
            {
                live = frame.DecodedMessages.Any(m =>
                    m is GameEventMessage g && g.DecodedEvent.Name == "round_freeze_end");
                continue;
            }

            bool full = frame.CommandKind == EDemoCommands.DemFullPacket;
            if (!opened && !full)
            {
                continue;
            }

            if (frame.UserCmdsPayloadCount == 0)
            {
                continue;
            }

            opened = true;
            for (int i = 0; i < frame.UserCmdsPayloadCount; i++)
            {
                captured.Add((full ? FullPacketKind : PacketKind, frame.GetUserCmdsPayload(i).ToArray()));
            }
        }

        return captured;
    }

    private static List<(byte Kind, byte[] Payload)> Read(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        List<(byte, byte[])> records = [];
        int p = 0;
        while (p < bytes.Length)
        {
            byte kind = bytes[p];
            int length = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(p + 1, 4));
            records.Add((kind, bytes.AsSpan(p + 5, length).ToArray()));
            p += 5 + length;
        }

        return records;
    }

    /// <summary>Replays records through a reconstructor, tallying which grammar rules the deltas use.</summary>
    private sealed class Replay
    {
        public GrammarCoverage Coverage { get; } = new();
        public UserCmdReconstructionStats Stats { get; private set; }
        public long Emitted { get; private set; }
        public int OpeningSnapshots { get; private set; }
        public long Commands { get; private set; }

        public static Replay Run(List<(byte Kind, byte[] Payload)> records)
        {
            Replay replay = new();
            UserCmdReconstructor reconstructor = new();
            bool opening = true;
            foreach ((byte kind, byte[] payload) in records)
            {
                bool full = kind == FullPacketKind;
                opening &= full;
                foreach (CMsgServerUserCmd cmd in CSVCMsg_UserCommands.Parser.ParseFrom(payload).Commands)
                {
                    replay.Commands++;
                    if (opening)
                    {
                        replay.OpeningSnapshots++;
                    }

                    if (!full && cmd.HasDeltaData && reconstructor.Current(cmd.PlayerSlot) is { } baseline)
                    {
                        replay.Coverage.Walk(baseline, CSGOUserCmdPB.Descriptor, cmd.DeltaData.Span);
                    }

                    reconstructor.Apply(cmd, full, out CSGOUserCmdPB? built);
                    if (built is not null)
                    {
                        replay.Emitted++;
                    }
                }
            }

            replay.Stats = reconstructor.Stats;
            return replay;
        }

        public string Describe() =>
            $"commands={Commands} full={Stats.Full} delta={Stats.Delta} primed={Stats.CheckpointPrimed} "
            + $"dup={Stats.CheckpointDuplicates} {Coverage}";
    }

    /// <summary>
    ///     Reads a delta the way <see cref="UserCmdDelta" /> does, against the baseline it will apply
    ///     to, and counts each rule it meets. Changes nothing.
    /// </summary>
    private sealed class GrammarCoverage
    {
        public long ScalarSet, ScalarReset, MessageReset, ListReset, NestedMerge, Grow, Truncate, ElementMerge;

        public IEnumerable<string> Missing()
        {
            (long Count, string Rule)[] rules =
            [
                (ScalarSet, "scalar set"),
                (ScalarReset, "scalar reset (wire 7)"),
                (ListReset, "whole-list reset (wire 7)"),
                (NestedMerge, "nested message merge"),
                (Grow, "repeated grow"),
                (Truncate, "repeated truncate"),
                (ElementMerge, "repeated element merge")
            ];
            return rules.Where(r => r.Count == 0).Select(r => r.Rule);
        }

        public override string ToString() =>
            $"scalarSet={ScalarSet} scalarReset={ScalarReset} messageReset={MessageReset} listReset={ListReset} "
            + $"nested={NestedMerge} grow={Grow} truncate={Truncate} elementMerge={ElementMerge}";

        public void Walk(IMessage? baseline, MessageDescriptor descriptor, ReadOnlySpan<byte> data)
        {
            int p = 0;
            while (p < data.Length)
            {
                ulong tag = Varint(data, ref p);
                int wire = (int)(tag & 7);
                FieldDescriptor? field = descriptor.FindFieldByNumber((int)(tag >> 3));
                switch (wire)
                {
                    case UserCmdDelta.ResetWireType:
                        if (field is { IsRepeated: true })
                        {
                            ListReset++;
                        }
                        else if (field is { FieldType: FieldType.Message })
                        {
                            MessageReset++;
                        }
                        else if (field is not null)
                        {
                            ScalarReset++;
                        }

                        break;
                    case 2:
                    {
                        int length = (int)Varint(data, ref p);
                        ReadOnlySpan<byte> value = data.Slice(p, length);
                        p += length;
                        if (field is { FieldType: FieldType.Message, IsRepeated: true })
                        {
                            WalkRepeated(baseline is null ? null : (IList)field.Accessor.GetValue(baseline), field, value);
                        }
                        else if (field is { FieldType: FieldType.Message })
                        {
                            NestedMerge++;
                            Walk(baseline is null ? null : (IMessage?)field.Accessor.GetValue(baseline), field.MessageType, value);
                        }
                        else if (field is not null)
                        {
                            ScalarSet++;
                        }

                        break;
                    }
                    case 0:
                        Varint(data, ref p);
                        ScalarSet++;
                        break;
                    case 1:
                        p += 8;
                        ScalarSet++;
                        break;
                    case 5:
                        p += 4;
                        ScalarSet++;
                        break;
                }
            }
        }

        private void WalkRepeated(IList? list, FieldDescriptor field, ReadOnlySpan<byte> ops)
        {
            int count = list?.Count ?? 0;
            int p = 0;
            while (p < ops.Length)
            {
                ulong key = Varint(ops, ref p);
                int index = (int)(key >> 3);
                if ((key & 7) == UserCmdDelta.ResetWireType)
                {
                    if (index > count)
                    {
                        Grow++;
                    }
                    else if (index < count)
                    {
                        Truncate++;
                    }

                    count = index;
                    list = null; // Element baselines past a resize are not tracked; counting is enough.
                }
                else
                {
                    int length = (int)Varint(ops, ref p);
                    ElementMerge++;
                    IMessage? element = list is not null && index < list.Count ? (IMessage?)list[index] : null;
                    Walk(element, field.MessageType, ops.Slice(p, length));
                    p += length;
                }
            }
        }

        private static ulong Varint(ReadOnlySpan<byte> data, ref int p)
        {
            ulong result = 0;
            for (int shift = 0; ; shift += 7)
            {
                byte b = data[p++];
                result |= (ulong)(b & 0x7F) << shift;
                if ((b & 0x80) == 0)
                {
                    return result;
                }
            }
        }
    }
}
