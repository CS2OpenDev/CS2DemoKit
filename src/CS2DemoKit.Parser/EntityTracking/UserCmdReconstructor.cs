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
    /// <summary>Parsed from <c>data</c> (with any <c>delta_data</c> applied on top). Emitted.</summary>
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

    /// <summary>The command number is behind the slot's latest command. Skipped.</summary>
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
///     <para>Not thread-safe. One instance follows one demo.</para>
/// </summary>
public sealed class UserCmdReconstructor
{
    private readonly List<ReconstructedUserCmd> _emitted = [];
    private readonly Dictionary<int, SlotState> _slots = [];
    private Counters _c;

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
    ///     objects the caller may keep.
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
        // Parse-time frames never do: their stored payloads are not in this list.
        foreach (NetMessage msg in frame.MessageList)
        {
            CSVCMsg_UserCommands? direct = msg.Payload switch
            {
                CSVCMsg_UserCommands typed => typed,
                DeferredMessage deferred => deferred.TryMaterialize<CSVCMsg_UserCommands>(),
                _ => null
            };

            if (direct is not null)
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
    ///     Applies one command, for callers that parse <c>svc_UserCmds</c> payloads themselves.
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

        bool hasData = cmd.HasData && !cmd.Data.IsEmpty;
        bool hasDelta = cmd.HasDeltaData && !cmd.DeltaData.IsEmpty;
        if ((!hasData && !hasDelta) || cmd.PlayerSlot < 0)
        {
            _c.Empty++;
            return UserCmdApplyStatus.Empty;
        }

        if (!_slots.TryGetValue(cmd.PlayerSlot, out SlotState? slot))
        {
            slot = new SlotState();
            _slots[cmd.PlayerSlot] = slot;
        }

        if (fromFullPacket)
        {
            return ApplyCheckpoint(cmd, slot, hasData, hasDelta);
        }

        CSGOUserCmdPB? built;
        UserCmdApplyStatus status;
        if (hasData)
        {
            built = TryParseKeyframe(cmd, hasDelta);
            status = UserCmdApplyStatus.Full;
        }
        else
        {
            if (slot.Command is null)
            {
                _c.MissingBaseline++;
                return UserCmdApplyStatus.MissingBaseline;
            }

            if (cmd.CmdNumber < slot.CmdNumber)
            {
                _c.OutOfOrder++;
                return UserCmdApplyStatus.OutOfOrder;
            }

            built = TryApplyDelta(slot.Command, cmd.DeltaData);
            status = UserCmdApplyStatus.Delta;
        }

        if (built is null)
        {
            _c.DecodeFailed++;
            slot.Command = null;
            return UserCmdApplyStatus.DecodeFailed;
        }

        slot.Command = built;
        slot.CmdNumber = cmd.CmdNumber;
        if (status == UserCmdApplyStatus.Full)
        {
            _c.Full++;
        }
        else
        {
            _c.Delta++;
        }

        if (cmd.HasClientTick && built.Base?.ClientTick != cmd.ClientTick)
        {
            _c.ClientTickMismatches++;
        }

        command = built;
        return status;
    }

    private UserCmdApplyStatus ApplyCheckpoint(CMsgServerUserCmd cmd, SlotState slot, bool hasData, bool hasDelta)
    {
        bool primed = slot.Command is not null;
        if (primed && cmd.CmdNumber < slot.CmdNumber)
        {
            _c.OutOfOrder++;
            return UserCmdApplyStatus.OutOfOrder;
        }

        bool duplicate = primed && cmd.CmdNumber == slot.CmdNumber;
        CSGOUserCmdPB? snapshot;
        if (hasData)
        {
            snapshot = TryParseKeyframe(cmd, hasDelta);
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
            snapshot = TryApplyDelta(slot.Command!, cmd.DeltaData);
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
        slot.CmdNumber = cmd.CmdNumber;
        _c.CheckpointPrimed++;
        return UserCmdApplyStatus.CheckpointPrimed;
    }

    private CSGOUserCmdPB? TryParseKeyframe(CMsgServerUserCmd cmd, bool hasDelta)
    {
        try
        {
            CSGOUserCmdPB parsed = CSGOUserCmdPB.Parser.ParseFrom(cmd.Data);
            if (hasDelta)
            {
                UserCmdDelta.Merge(parsed, cmd.DeltaData.Span, ref _c.UnknownFieldsSkipped);
            }

            return parsed;
        }
        catch (Exception ex) when (ex is InvalidProtocolBufferException or InvalidDataException)
        {
            return null;
        }
    }

    private CSGOUserCmdPB? TryApplyDelta(CSGOUserCmdPB baseline, ByteString delta)
    {
        CSGOUserCmdPB next = baseline.Clone();
        try
        {
            UserCmdDelta.Merge(next, delta.Span, ref _c.UnknownFieldsSkipped);
            return next;
        }
        catch (Exception ex) when (ex is InvalidProtocolBufferException or InvalidDataException)
        {
            return null;
        }
    }

    /// <summary>
    ///     The latest command rebuilt or primed for <paramref name="playerSlot" />, or null when the
    ///     slot has no baseline. Treat it as read-only: it is the baseline for the slot's next delta.
    /// </summary>
    public CSGOUserCmdPB? Current(int playerSlot) =>
        _slots.TryGetValue(playerSlot, out SlotState? slot) ? slot.Command : null;

    /// <summary>Forgets every slot and zeroes <see cref="Stats" />, before a seek.</summary>
    public void Reset()
    {
        _slots.Clear();
        _emitted.Clear();
        _c = default;
    }

    private sealed class SlotState
    {
        public int CmdNumber;

        /// <summary>The latest command, or null while the slot waits for a keyframe.</summary>
        public CSGOUserCmdPB? Command;
    }

    private struct Counters
    {
        public long Full, Delta, CheckpointPrimed, CheckpointDuplicates, CheckpointMismatches;
        public long MissingBaseline, OutOfOrder, DecodeFailed, Empty, UnknownFieldsSkipped, ClientTickMismatches;
    }
}
