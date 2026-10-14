#region

using Google.Protobuf;

#endregion

namespace CS2DemoKit.Parser.EntityTracking;

/// <summary>One player command, rebuilt in full whether it arrived as a keyframe or a delta.</summary>
/// <param name="PlayerSlot">The outer <c>CMsgServerUserCmd.player_slot</c>.</param>
/// <param name="CmdNumber">The outer <c>cmd_number</c>, which increases per slot.</param>
/// <param name="ServerTickExecuted">The outer <c>server_tick_executed</c>.</param>
/// <param name="ClientTick">The outer <c>client_tick</c>.</param>
/// <param name="FromDelta">True when the command was rebuilt from <c>delta_data</c>.</param>
/// <param name="Command">
///     The full command. The reconstructor never changes it after returning it, so it can be kept.
///     It is also the slot's baseline for the next delta, so treat it as read-only.
/// </param>
public readonly record struct ReconstructedUserCmd(
    int PlayerSlot,
    int CmdNumber,
    int ServerTickExecuted,
    int ClientTick,
    bool FromDelta,
    CSGOUserCmdPB Command);

/// <summary>What <see cref="UserCmdReconstructor.Apply" /> did with one command.</summary>
public enum UserCmdApplyStatus
{
    /// <summary>
    ///     Parsed from <c>data</c> (with any <c>delta_data</c> applied on top). Emitted, and it becomes
    ///     the slot's baseline whatever its command number, since it needs no baseline of its own.
    /// </summary>
    Full,

    /// <summary>Rebuilt from <c>delta_data</c> against the slot's latest command. Emitted.</summary>
    Delta,

    /// <summary>A <c>DEM_FullPacket</c> snapshot seeded the slot. Not emitted.</summary>
    CheckpointPrimed,

    /// <summary>
    ///     A <c>DEM_FullPacket</c> snapshot repeated a command already rebuilt. Not emitted. A snapshot
    ///     that disagrees with the rebuild is counted in
    ///     <see cref="UserCmdReconstructionStats.CheckpointMismatches" /> and replaces it.
    /// </summary>
    CheckpointDuplicate,

    /// <summary>A delta arrived for a slot with no baseline. Skipped, never decoded against defaults.</summary>
    MissingBaseline,

    /// <summary>
    ///     A delta or full-packet snapshot whose command number is behind the slot's latest command.
    ///     Skipped. A keyframe is never skipped this way: see <see cref="Full" />.
    /// </summary>
    OutOfOrder,

    /// <summary>The payload or delta was malformed. The slot waits for the next keyframe or full packet.</summary>
    DecodeFailed,

    /// <summary>The command carried neither <c>data</c> nor <c>delta_data</c>, or had no valid slot.</summary>
    Empty
}

/// <summary>Running totals of what a <see cref="UserCmdReconstructor" /> has seen since its last reset.</summary>
public readonly record struct UserCmdReconstructionStats
{
    /// <summary>Commands parsed from <c>data</c>.</summary>
    public long Full { get; init; }

    /// <summary>Commands rebuilt from <c>delta_data</c>.</summary>
    public long Delta { get; init; }

    /// <summary>Full-packet snapshots that seeded a slot.</summary>
    public long CheckpointPrimed { get; init; }

    /// <summary>Full-packet snapshots of a command already rebuilt.</summary>
    public long CheckpointDuplicates { get; init; }

    /// <summary>
    ///     Full-packet snapshots that disagree with the command rebuilt at the same number. Zero on
    ///     every demo measured; a nonzero value means the delta format changed.
    /// </summary>
    public long CheckpointMismatches { get; init; }

    /// <summary>Deltas skipped because their slot had no baseline.</summary>
    public long MissingBaseline { get; init; }

    /// <summary>Commands skipped because their number was behind the slot's latest.</summary>
    public long OutOfOrder { get; init; }

    /// <summary>Payloads, keyframes or deltas that failed to decode.</summary>
    public long DecodeFailed { get; init; }

    /// <summary>Commands with no payload at all, or a negative player slot.</summary>
    public long Empty { get; init; }

    /// <summary>
    ///     Delta fields the packaged protos do not describe. They are skipped; a nonzero value means
    ///     Valve added a field that CS2OpenDev.Protos does not have yet.
    /// </summary>
    public long UnknownFieldsSkipped { get; init; }

    /// <summary>
    ///     Emitted commands whose rebuilt <c>base.client_tick</c> differs from the outer
    ///     <c>client_tick</c>. The two agree on every command measured, so this is the quickest sign
    ///     that a rebuild went wrong.
    /// </summary>
    public long ClientTickMismatches { get; init; }
}

/// <summary>
///     Rebuilds every player's user commands from <c>svc_UserCmds</c>, including the ones the server
///     sends as <c>delta_data</c>.
///     <para>
///         Since build 10896 (mid 2026) about 99.8% of commands arrive as a delta against the same
///         slot's previous command, and only an occasional one carries a full <c>data</c> keyframe.
///         Parsing <c>data</c> alone sees the keyframes only. This class keeps each slot's latest
///         command and applies each delta to a copy of it.
///     </para>
///     <para>
///         <b>Forward use.</b> Feed frames in order through <see cref="AdvanceOneFrame" />, starting
///         at frame 0 or at a <c>DEM_FullPacket</c>. It reads the frames of
///         <see cref="DemoReader.ReadFrames" /> and of <see cref="ParsedDemo.Frames" /> alike. The
///         decode plan must include <see cref="MessageCategories.UserCmds" />.
///     </para>
///     <para>
///         <b>Seek use.</b> Call <see cref="Reset" />, then start at the nearest <c>DEM_FullPacket</c>
///         at or before the target, as <see cref="EntitySeekService" /> does for entities. On current
///         demos every full packet carries a full snapshot of each slot's latest command, which
///         primes the slot, so every later delta decodes.
///     </para>
///     <para>
///         <b>Full packets.</b> Their commands are snapshots, never new input, so they are not
///         emitted. A snapshot seeds a slot that has no baseline or is behind it, and one that
///         repeats the latest command is compared with the rebuild as a free self-check.
///     </para>
///     <para>
///         <b>Failures are counted, not guessed.</b> A delta with no baseline is skipped, not
///         decoded against defaults. A delta that fails to decode leaves the slot without a
///         baseline until the next keyframe or full packet, since the server's next delta is
///         relative to the command that was lost. <see cref="Stats" /> says what was and was not
///         rebuilt.
///     </para>
///     <para>
///         <b>Keyframes always re-seed.</b> A delta or snapshot behind the slot's latest command is
///         skipped as out of order, but a keyframe is complete on its own and is taken as the new
///         baseline even when its number is lower. A player slot that is reused, for a reconnect or
///         a new player, restarts its command numbers, and its first keyframe is what lets the
///         deltas after it decode.
///     </para>
///     <para>
///         <b>On demand.</b> A reader that samples a few slots now and then, such as a rule
///         evaluator, does not need every command rebuilt: set <see cref="OnDemand" /> and a slot
///         is rebuilt when <see cref="Current" /> reads it.
///     </para>
///     <para>Not thread-safe. One instance follows one demo.</para>
/// </summary>
public sealed class UserCmdReconstructor
{
    private const int LengthDelimitedWireType = 2;

    // Player slots are 0..63; the dictionary is for anything else the wire may say.
    private const int DirectSlots = 64;

    private readonly List<ReconstructedUserCmd> _emitted = [];
    private readonly SlotState?[] _direct = new SlotState?[DirectSlots];
    private readonly Dictionary<int, SlotState> _slots = [];
    private Counters _c;

    /// <summary>
    ///     Rebuild a slot's commands only when <see cref="Current" /> reads the slot.
    ///     <para>
    ///         <see cref="AdvanceOneFrame" /> then keeps each command's bytes against its slot, parses
    ///         nothing and returns an empty list; <see cref="Current" /> and <see cref="Apply" /> apply
    ///         the slot's pending commands in wire order first. A read sees the command the eager
    ///         mode would hold at that frame, but the instance is updated in place by the slot's later
    ///         rebuilds, so copy what you keep. A keyframe outside a full packet needs no baseline and
    ///         always re-seeds, so it drops the pending commands before it for its slot, and
    ///         <see cref="Stats" /> then count what was rebuilt rather than every command seen.
    ///     </para>
    ///     <para>
    ///         Rebuilding every command of a demo allocates about ten times the commands' own bytes,
    ///         and a sampling reader looks at almost none of it.
    ///     </para>
    /// </summary>
    public bool OnDemand { get; init; }

    /// <summary>Totals since construction or the last <see cref="Reset" />.</summary>
    public UserCmdReconstructionStats Stats => new()
    {
        Full = _c.Full,
        Delta = _c.Delta,
        CheckpointPrimed = _c.CheckpointPrimed,
        CheckpointDuplicates = _c.CheckpointDuplicates,
        CheckpointMismatches = _c.CheckpointMismatches,
        MissingBaseline = _c.MissingBaseline,
        OutOfOrder = _c.OutOfOrder,
        DecodeFailed = _c.DecodeFailed,
        Empty = _c.Empty,
        UnknownFieldsSkipped = _c.UnknownFieldsSkipped,
        ClientTickMismatches = _c.ClientTickMismatches
    };

    /// <summary>
    ///     Rebuilds the commands one frame carries, in wire order. Commands inside a
    ///     <c>DEM_FullPacket</c> prime slots and are not returned.
    /// </summary>
    /// <returns>
    ///     A list owned by the reconstructor, valid until the next call. The commands in it are fresh
    ///     objects the caller may keep. Always empty with <see cref="OnDemand" /> set.
    /// </returns>
    public IReadOnlyList<ReconstructedUserCmd> AdvanceOneFrame(DemoFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        _emitted.Clear();
        bool fromFullPacket = frame.CommandKind == EDemoCommands.DemFullPacket;

        if (frame.UserCmdsBlock is { } block)
        {
            int offset = frame.UserCmdsOffset;
            for (int i = 0; i < frame.UserCmdsCount; i++)
            {
                ReadOnlySpan<byte> payload = UserCmdsStore.Read(block, ref offset);
                if (OnDemand)
                {
                    try
                    {
                        Record(payload, fromFullPacket);
                    }
                    catch (InvalidDataException)
                    {
                        _c.DecodeFailed++;
                    }

                    continue;
                }

                CSVCMsg_UserCommands parsed;
                try
                {
                    parsed = CSVCMsg_UserCommands.Parser.ParseFrom(payload);
                }
                catch (InvalidProtocolBufferException)
                {
                    _c.DecodeFailed++;
                    continue;
                }

                ApplyAll(parsed, fromFullPacket);
            }
        }

        // A frame built by hand (tests, synthetic input) carries its payloads as messages instead.
        // Parse-time frames never do: their stored payloads are not in this list, so a frame with
        // a store is not scanned.
        if (frame.UserCmdsBlock is not null)
        {
            return _emitted;
        }

        foreach (NetMessage msg in frame.MessageList)
        {
            CSVCMsg_UserCommands? direct = msg.Payload switch
            {
                CSVCMsg_UserCommands typed => typed,
                DeferredMessage deferred => deferred.TryMaterialize<CSVCMsg_UserCommands>(),
                _ => null
            };

            if (direct is null)
            {
                continue;
            }

            if (OnDemand)
            {
                foreach (CMsgServerUserCmd cmd in direct.Commands)
                {
                    Record(cmd.PlayerSlot, cmd.CmdNumber, cmd.HasClientTick, cmd.ClientTick, fromFullPacket,
                        cmd.HasData ? cmd.Data.Span : default, cmd.HasDeltaData ? cmd.DeltaData.Span : default);
                }
            }
            else
            {
                ApplyAll(direct, fromFullPacket);
            }
        }

        return _emitted;
    }

    private void ApplyAll(CSVCMsg_UserCommands payload, bool fromFullPacket)
    {
        foreach (CMsgServerUserCmd cmd in payload.Commands)
        {
            UserCmdApplyStatus status = Apply(cmd, fromFullPacket, out CSGOUserCmdPB? command);
            if (command is not null)
            {
                _emitted.Add(new ReconstructedUserCmd(cmd.PlayerSlot, cmd.CmdNumber, cmd.ServerTickExecuted,
                    cmd.ClientTick, status == UserCmdApplyStatus.Delta, command));
            }
        }
    }

    /// <summary>
    ///     Applies one command, for callers that parse <c>svc_UserCmds</c> payloads themselves. With
    ///     <see cref="OnDemand" /> set, the slot's pending commands are applied first.
    /// </summary>
    /// <param name="cmd">One entry of <c>CSVCMsg_UserCommands.commands</c>.</param>
    /// <param name="fromFullPacket">True when it came from a <c>DEM_FullPacket</c> frame.</param>
    /// <param name="command">
    ///     The rebuilt command when the status is <see cref="UserCmdApplyStatus.Full" /> or
    ///     <see cref="UserCmdApplyStatus.Delta" />, otherwise null.
    /// </param>
    public UserCmdApplyStatus Apply(CMsgServerUserCmd cmd, bool fromFullPacket, out CSGOUserCmdPB? command)
    {
        ArgumentNullException.ThrowIfNull(cmd);
        command = null;

        ReadOnlySpan<byte> data = cmd.HasData ? cmd.Data.Span : default;
        ReadOnlySpan<byte> delta = cmd.HasDeltaData ? cmd.DeltaData.Span : default;
        if ((data.IsEmpty && delta.IsEmpty) || cmd.PlayerSlot < 0)
        {
            _c.Empty++;
            return UserCmdApplyStatus.Empty;
        }

        SlotState slot = GetSlot(cmd.PlayerSlot);
        if (slot.Pending is { Count: > 0 })
        {
            Rebuild(slot);
        }

        return ApplyCore(slot, cmd.CmdNumber, cmd.HasClientTick, cmd.ClientTick, fromFullPacket, data, delta, false,
            out command);
    }

    private UserCmdApplyStatus ApplyCore(SlotState slot, int cmdNumber, bool hasClientTick, int clientTick,
        bool fromFullPacket, ReadOnlySpan<byte> data, ReadOnlySpan<byte> delta, bool inPlace,
        out CSGOUserCmdPB? command)
    {
        command = null;
        if (fromFullPacket)
        {
            return ApplyCheckpoint(slot, cmdNumber, data, delta);
        }

        CSGOUserCmdPB? built;
        UserCmdApplyStatus status;
        if (!data.IsEmpty)
        {
            // No order check: a keyframe needs no baseline, and a lower number means the slot restarted.
            built = TryParseKeyframe(data, delta);
            status = UserCmdApplyStatus.Full;
        }
        else
        {
            if (slot.Command is null)
            {
                _c.MissingBaseline++;
                return UserCmdApplyStatus.MissingBaseline;
            }

            if (cmdNumber < slot.CmdNumber)
            {
                _c.OutOfOrder++;
                return UserCmdApplyStatus.OutOfOrder;
            }

            // In place is safe here only: a failed merge drops the baseline anyway.
            built = TryApplyDelta(slot.Command, delta, inPlace);
            status = UserCmdApplyStatus.Delta;
        }

        if (built is null)
        {
            _c.DecodeFailed++;
            slot.Command = null;
            return UserCmdApplyStatus.DecodeFailed;
        }

        slot.Command = built;
        slot.CmdNumber = cmdNumber;
        if (status == UserCmdApplyStatus.Full)
        {
            _c.Full++;
        }
        else
        {
            _c.Delta++;
        }

        if (hasClientTick && built.Base?.ClientTick != clientTick)
        {
            _c.ClientTickMismatches++;
        }

        command = built;
        return status;
    }

    private UserCmdApplyStatus ApplyCheckpoint(SlotState slot, int cmdNumber, ReadOnlySpan<byte> data,
        ReadOnlySpan<byte> delta)
    {
        bool primed = slot.Command is not null;
        if (primed && cmdNumber < slot.CmdNumber)
        {
            _c.OutOfOrder++;
            return UserCmdApplyStatus.OutOfOrder;
        }

        bool duplicate = primed && cmdNumber == slot.CmdNumber;
        CSGOUserCmdPB? snapshot;
        if (!data.IsEmpty)
        {
            snapshot = TryParseKeyframe(data, delta);
        }
        else if (duplicate)
        {
            // A delta-only snapshot of the command already held says nothing new.
            _c.CheckpointDuplicates++;
            return UserCmdApplyStatus.CheckpointDuplicate;
        }
        else if (!primed)
        {
            _c.MissingBaseline++;
            return UserCmdApplyStatus.MissingBaseline;
        }
        else
        {
            snapshot = TryApplyDelta(slot.Command!, delta, false);
        }

        if (snapshot is null)
        {
            // A bad snapshot does not unseat a good baseline.
            _c.DecodeFailed++;
            return UserCmdApplyStatus.DecodeFailed;
        }

        if (duplicate)
        {
            _c.CheckpointDuplicates++;
            if (!snapshot.Equals(slot.Command))
            {
                // The server's next delta is relative to its own command, so the snapshot wins.
                _c.CheckpointMismatches++;
                slot.Command = snapshot;
            }

            return UserCmdApplyStatus.CheckpointDuplicate;
        }

        slot.Command = snapshot;
        slot.CmdNumber = cmdNumber;
        _c.CheckpointPrimed++;
        return UserCmdApplyStatus.CheckpointPrimed;
    }

    private CSGOUserCmdPB? TryParseKeyframe(ReadOnlySpan<byte> data, ReadOnlySpan<byte> delta)
    {
        try
        {
            CSGOUserCmdPB parsed = CSGOUserCmdPB.Parser.ParseFrom(data);
            if (!delta.IsEmpty)
            {
                UserCmdDelta.Merge(parsed, delta, ref _c.UnknownFieldsSkipped);
            }

            return parsed;
        }
        catch (Exception ex) when (ex is InvalidProtocolBufferException or InvalidDataException)
        {
            return null;
        }
    }

    private CSGOUserCmdPB? TryApplyDelta(CSGOUserCmdPB baseline, ReadOnlySpan<byte> delta, bool inPlace)
    {
        CSGOUserCmdPB next = inPlace ? baseline : baseline.Clone();
        try
        {
            UserCmdDelta.Merge(next, delta, ref _c.UnknownFieldsSkipped);
            return next;
        }
        catch (Exception ex) when (ex is InvalidProtocolBufferException or InvalidDataException)
        {
            return null;
        }
    }

    /// <summary>Queues the commands of one stored <c>CSVCMsg_UserCommands</c> payload without parsing them.</summary>
    private void Record(ReadOnlySpan<byte> payload, bool fromFullPacket)
    {
        int p = 0;
        while (p < payload.Length)
        {
            ulong tag = UserCmdDelta.ReadVarint(payload, ref p);
            int wire = (int)(tag & 7);
            if (tag >> 3 == CSVCMsg_UserCommands.CommandsFieldNumber && wire == LengthDelimitedWireType)
            {
                RecordCommand(UserCmdDelta.ReadLengthDelimited(payload, ref p), fromFullPacket);
            }
            else
            {
                Skip(payload, ref p, wire);
            }
        }
    }

    private void RecordCommand(ReadOnlySpan<byte> cmd, bool fromFullPacket)
    {
        int playerSlot = 0, cmdNumber = 0, clientTick = 0;
        bool hasClientTick = false;
        ReadOnlySpan<byte> data = default, delta = default;
        int p = 0;
        while (p < cmd.Length)
        {
            ulong tag = UserCmdDelta.ReadVarint(cmd, ref p);
            int wire = (int)(tag & 7);
            switch ((int)(tag >> 3), wire)
            {
                case (CMsgServerUserCmd.DataFieldNumber, LengthDelimitedWireType):
                    data = UserCmdDelta.ReadLengthDelimited(cmd, ref p);
                    break;
                case (CMsgServerUserCmd.DeltaDataFieldNumber, LengthDelimitedWireType):
                    delta = UserCmdDelta.ReadLengthDelimited(cmd, ref p);
                    break;
                case (CMsgServerUserCmd.CmdNumberFieldNumber, 0):
                    cmdNumber = (int)UserCmdDelta.ReadVarint(cmd, ref p);
                    break;
                case (CMsgServerUserCmd.PlayerSlotFieldNumber, 0):
                    playerSlot = (int)UserCmdDelta.ReadVarint(cmd, ref p);
                    break;
                case (CMsgServerUserCmd.ClientTickFieldNumber, 0):
                    clientTick = (int)UserCmdDelta.ReadVarint(cmd, ref p);
                    hasClientTick = true;
                    break;
                default:
                    Skip(cmd, ref p, wire);
                    break;
            }
        }

        Record(playerSlot, cmdNumber, hasClientTick, clientTick, fromFullPacket, data, delta);
    }

    private void Record(int playerSlot, int cmdNumber, bool hasClientTick, int clientTick, bool fromFullPacket,
        ReadOnlySpan<byte> data, ReadOnlySpan<byte> delta)
    {
        if ((data.IsEmpty && delta.IsEmpty) || playerSlot < 0)
        {
            _c.Empty++;
            return;
        }

        SlotState slot = GetSlot(playerSlot);
        slot.Pending ??= [];
        if (!fromFullPacket && !data.IsEmpty)
        {
            // Nothing queued before a keyframe can reach Current: the keyframe re-seeds the slot.
            slot.Pending.Clear();
            slot.PendingBytes = 0;
        }

        int need = data.Length + delta.Length;
        if (slot.Bytes is null || slot.Bytes.Length - slot.PendingBytes < need)
        {
            byte[] grown = new byte[Math.Max(Math.Max(4096, slot.PendingBytes + need), 2 * (slot.Bytes?.Length ?? 0))];
            slot.Bytes?.AsSpan(0, slot.PendingBytes).CopyTo(grown);
            slot.Bytes = grown;
        }

        int at = slot.PendingBytes;
        data.CopyTo(slot.Bytes.AsSpan(at));
        delta.CopyTo(slot.Bytes.AsSpan(at + data.Length));
        slot.PendingBytes = at + need;
        slot.Pending.Add(new PendingCmd(cmdNumber, clientTick, hasClientTick, fromFullPacket, at, data.Length,
            delta.Length));
    }

    /// <summary>Applies the slot's pending commands in wire order and empties the queue.</summary>
    private void Rebuild(SlotState slot)
    {
        List<PendingCmd> pending = slot.Pending!;
        byte[] bytes = slot.Bytes!;
        foreach (PendingCmd cmd in pending)
        {
            ApplyCore(slot, cmd.CmdNumber, cmd.HasClientTick, cmd.ClientTick, cmd.FromFullPacket,
                bytes.AsSpan(cmd.Offset, cmd.DataLength), bytes.AsSpan(cmd.Offset + cmd.DataLength, cmd.DeltaLength),
                true, out _);
        }

        pending.Clear();
        slot.PendingBytes = 0;
    }

    private static void Skip(ReadOnlySpan<byte> data, ref int p, int wire)
    {
        if (wire == LengthDelimitedWireType)
        {
            UserCmdDelta.ReadLengthDelimited(data, ref p);
        }
        else
        {
            UserCmdDelta.SkipValue(data, ref p, wire);
        }
    }

    private SlotState GetSlot(int playerSlot)
    {
        if ((uint)playerSlot < DirectSlots)
        {
            return _direct[playerSlot] ??= new SlotState();
        }

        if (!_slots.TryGetValue(playerSlot, out SlotState? slot))
        {
            slot = new SlotState();
            _slots[playerSlot] = slot;
        }

        return slot;
    }

    private SlotState? FindSlot(int playerSlot) =>
        (uint)playerSlot < DirectSlots
            ? _direct[playerSlot]
            : _slots.TryGetValue(playerSlot, out SlotState? slot) ? slot : null;

    /// <summary>
    ///     The latest command rebuilt or primed for <paramref name="playerSlot" />, or null when the
    ///     slot has no baseline. Treat it as read-only: it is the baseline for the slot's next delta.
    ///     With <see cref="OnDemand" /> set, the slot's pending commands are applied first.
    /// </summary>
    public CSGOUserCmdPB? Current(int playerSlot)
    {
        if (FindSlot(playerSlot) is not { } slot)
        {
            return null;
        }

        if (slot.Pending is { Count: > 0 })
        {
            Rebuild(slot);
        }

        return slot.Command;
    }

    /// <summary>Forgets every slot and zeroes <see cref="Stats" />, before a seek.</summary>
    public void Reset()
    {
        Array.Clear(_direct);
        _slots.Clear();
        _emitted.Clear();
        _c = default;
    }

    /// <summary>A queued command; its bytes are the slot buffer's <see cref="DataLength" /> then <see cref="DeltaLength" /> bytes at <see cref="Offset" />.</summary>
    private readonly record struct PendingCmd(
        int CmdNumber,
        int ClientTick,
        bool HasClientTick,
        bool FromFullPacket,
        int Offset,
        int DataLength,
        int DeltaLength);

    private sealed class SlotState
    {
        public int CmdNumber;

        /// <summary>The latest command, or null while the slot waits for a keyframe.</summary>
        public CSGOUserCmdPB? Command;

        /// <summary>Queued on-demand commands, in wire order.</summary>
        public List<PendingCmd>? Pending;

        /// <summary>The queued commands' bytes, the first <see cref="PendingBytes" /> in use.</summary>
        public byte[]? Bytes;

        public int PendingBytes;
    }

    private struct Counters
    {
        public long Full, Delta, CheckpointPrimed, CheckpointDuplicates, CheckpointMismatches;
        public long MissingBaseline, OutOfOrder, DecodeFailed, Empty, UnknownFieldsSkipped, ClientTickMismatches;
    }
}
