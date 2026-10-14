#region

using System.Runtime.CompilerServices;
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
    private readonly List<QueuedFrame> _queue = [];
    private int _firstSeq;
    private Counters _c;

    /// <summary>
    ///     Rebuild a slot's commands only when <see cref="Current" /> reads the slot.
    ///     <para>
    ///         <see cref="AdvanceOneFrame" /> then only remembers where the frame's payloads are and
    ///         returns an empty list; <see cref="Current" /> and <see cref="Apply" /> first apply the
    ///         slot's commands from the frames it has not seen, in wire order. A read sees the command
    ///         the eager mode would hold at that frame, but the instance is updated in place by the
    ///         slot's later rebuilds, so copy what you keep. A keyframe outside a full packet needs no
    ///         baseline and always re-seeds, so a rebuild spanning several frames starts at the slot's
    ///         newest keyframe, and <see cref="Stats" /> count what was rebuilt rather than every
    ///         command seen.
    ///     </para>
    ///     <para>
    ///         Rebuilding every command of a demo allocates about ten times the commands' own bytes,
    ///         and a sampling reader looks at almost none of it. Frames stay queued until every slot
    ///         with commands in them has been read, up to a bound of a few thousand frames; past it
    ///         the oldest are applied to every slot.
    ///     </para>
    /// </summary>
    public bool OnDemand { get; init; }

    /// <summary>The default <see cref="OnDemandFrameBound" />.</summary>
    public const int DefaultOnDemandFrameBound = 4096;

    /// <summary>
    ///     How many frames an on-demand reconstructor holds before it applies the oldest half of them
    ///     to every slot. A queued frame pins its payload block, about 1 MB per 600 frames of a
    ///     matchmaking demo, which is what the bound limits; a caller whose frames stay alive anyway,
    ///     such as one walking <see cref="ParsedDemo.Frames" />, can lift it to
    ///     <see cref="int.MaxValue" /> and never pay for a slot it does not read.
    /// </summary>
    public int OnDemandFrameBound { get; set; } = DefaultOnDemandFrameBound;

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
            if (OnDemand)
            {
                if (frame.UserCmdsCount > 0)
                {
                    Enqueue(new QueuedFrame(block, frame.UserCmdsOffset, frame.UserCmdsCount, fromFullPacket, null));
                }

                return _emitted;
            }

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

            // Parse-time frames carry no typed payloads: their stored payloads are not in this list.
            return _emitted;
        }

        // A frame built by hand (tests, synthetic input) carries its payloads as messages instead.
        List<CSVCMsg_UserCommands>? typed = null;
        foreach (NetMessage msg in frame.MessageList)
        {
            CSVCMsg_UserCommands? direct = msg.Payload switch
            {
                CSVCMsg_UserCommands t => t,
                DeferredMessage deferred => deferred.TryMaterialize<CSVCMsg_UserCommands>(),
                _ => null
            };

            if (direct is null)
            {
                continue;
            }

            if (OnDemand)
            {
                (typed ??= []).Add(direct);
            }
            else
            {
                ApplyAll(direct, fromFullPacket);
            }
        }

        if (typed is not null)
        {
            Enqueue(new QueuedFrame(null, 0, 0, fromFullPacket, [.. typed]));
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
    ///     <see cref="OnDemand" /> set, the slot is first caught up with the queued frames.
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
        if (OnDemand)
        {
            CatchUp(slot, cmd.PlayerSlot);
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

    private void Enqueue(QueuedFrame frame)
    {
        _queue.Add(frame);
        if (_queue.Count > OnDemandFrameBound)
        {
            int n = _queue.Count / 2;
            Rebuild(_firstSeq, _firstSeq + n - 1, null, -1);
            _queue.RemoveRange(0, n);
            _firstSeq += n;
        }
    }

    /// <summary>Applies the queued frames <paramref name="playerSlot" /> has not seen yet.</summary>
    private void CatchUp(SlotState slot, int playerSlot)
    {
        int newest = _firstSeq + _queue.Count - 1;
        if (slot.ConsumedSeq >= newest)
        {
            return;
        }

        if (slot.ConsumedSeq == newest - 1)
        {
            // One frame behind, as a reader that polls every frame is for every slot: parse the
            // frame once and move every slot in that position along with it.
            AdvanceCohort(newest);
            return;
        }

        Rebuild(Math.Max(slot.ConsumedSeq + 1, _firstSeq), newest, slot, playerSlot);
    }

    private void AdvanceCohort(int seq)
    {
        QueuedFrame frame = _queue[seq - _firstSeq];
        FrameCommands commands = new(frame);
        while (commands.MoveNext(out QueuedCmd cmd))
        {
            if (cmd.Slot < 0 || (cmd.Data.IsEmpty && cmd.Delta.IsEmpty))
            {
                _c.Empty++;
                continue;
            }

            SlotState slot = GetSlot(cmd.Slot);
            if (slot.ConsumedSeq == seq - 1)
            {
                ApplyCore(slot, cmd.CmdNumber, cmd.HasClientTick, cmd.ClientTick, frame.FromFullPacket, cmd.Data,
                    cmd.Delta, true, out _);
            }
        }

        _c.DecodeFailed += commands.DecodeFailed;
        foreach (SlotState slot in AllSlots())
        {
            if (slot.ConsumedSeq == seq - 1)
            {
                slot.ConsumedSeq = seq;
            }
        }
    }

    /// <summary>
    ///     Applies frames <paramref name="fromSeq" /> to <paramref name="toSeq" /> to one slot, or to
    ///     every slot when <paramref name="only" /> is null. A slot's commands before its newest
    ///     keyframe in the range are skipped, since the keyframe re-seeds it.
    /// </summary>
    private void Rebuild(int fromSeq, int toSeq, SlotState? only, int onlySlot)
    {
        if (only is not null)
        {
            only.StartSeq = -1;
        }
        else
        {
            foreach (SlotState slot in AllSlots())
            {
                slot.StartSeq = -1;
                slot.SeenInRange = false;
            }
        }

        for (int seq = toSeq; seq >= fromSeq; seq--)
        {
            QueuedFrame frame = _queue[seq - _firstSeq];
            if (frame.FromFullPacket)
            {
                continue;
            }

            FrameCommands commands = new(frame);
            int index = 0;
            bool found = false;
            while (commands.MoveNext(out QueuedCmd cmd))
            {
                int i = index++;
                if (cmd.Slot < 0 || (only is not null && cmd.Slot != onlySlot))
                {
                    continue;
                }

                SlotState slot = only ?? GetSlot(cmd.Slot);
                if (slot.ConsumedSeq < seq)
                {
                    slot.SeenInRange = true;
                }

                if (cmd.Data.IsEmpty)
                {
                    continue;
                }

                if (slot.ConsumedSeq < seq && (slot.StartSeq < 0 || slot.StartSeq == seq))
                {
                    // The last keyframe of the frame wins, so a later index overrides.
                    slot.StartSeq = seq;
                    slot.StartIndex = i;
                    found = true;
                }
            }

            if (found && only is not null)
            {
                break;
            }
        }

        int startSeq = ForwardStart(fromSeq, toSeq, only);
        for (int seq = startSeq; seq <= toSeq; seq++)
        {
            QueuedFrame frame = _queue[seq - _firstSeq];
            FrameCommands commands = new(frame);
            int index = 0;
            while (commands.MoveNext(out QueuedCmd cmd))
            {
                int i = index++;
                if (only is not null && cmd.Slot != onlySlot)
                {
                    continue;
                }

                if (cmd.Slot < 0 || (cmd.Data.IsEmpty && cmd.Delta.IsEmpty))
                {
                    _c.Empty++;
                    continue;
                }

                SlotState slot = only ?? GetSlot(cmd.Slot);
                if (slot.ConsumedSeq >= seq
                    || (slot.StartSeq >= 0 && (seq < slot.StartSeq || (seq == slot.StartSeq && i < slot.StartIndex))))
                {
                    continue;
                }

                ApplyCore(slot, cmd.CmdNumber, cmd.HasClientTick, cmd.ClientTick, frame.FromFullPacket, cmd.Data,
                    cmd.Delta, true, out _);
            }

            _c.DecodeFailed += commands.DecodeFailed;
        }

        if (only is not null)
        {
            only.ConsumedSeq = toSeq;
            return;
        }

        foreach (SlotState slot in AllSlots())
        {
            if (slot.ConsumedSeq < toSeq)
            {
                slot.ConsumedSeq = toSeq;
            }
        }
    }

    /// <summary>
    ///     The first frame the forward pass needs: every slot with commands in the range has a
    ///     keyframe at or after it. A slot with commands but no keyframe needs the whole range.
    /// </summary>
    private int ForwardStart(int fromSeq, int toSeq, SlotState? only)
    {
        if (only is not null)
        {
            return only.StartSeq >= 0 ? only.StartSeq : fromSeq;
        }

        int start = toSeq + 1;
        foreach (SlotState slot in AllSlots())
        {
            if (!slot.SeenInRange)
            {
                continue;
            }

            if (slot.StartSeq < 0)
            {
                return fromSeq;
            }

            start = Math.Min(start, slot.StartSeq);
        }

        return start;
    }

    private IEnumerable<SlotState> AllSlots()
    {
        foreach (SlotState? slot in _direct)
        {
            if (slot is not null)
            {
                yield return slot;
            }
        }

        foreach (SlotState slot in _slots.Values)
        {
            yield return slot;
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
    ///     With <see cref="OnDemand" /> set, the slot is first caught up with the queued frames.
    /// </summary>
    public CSGOUserCmdPB? Current(int playerSlot)
    {
        if (OnDemand)
        {
            if (_queue.Count == 0)
            {
                return FindSlot(playerSlot)?.Command;
            }

            SlotState slot = GetSlot(playerSlot);
            CatchUp(slot, playerSlot);
            return slot.Command;
        }

        return FindSlot(playerSlot)?.Command;
    }

    /// <summary>Forgets every slot and zeroes <see cref="Stats" />, before a seek.</summary>
    public void Reset()
    {
        Array.Clear(_direct);
        _slots.Clear();
        _emitted.Clear();
        _queue.Clear();
        _firstSeq = 0;
        _c = default;
    }

    /// <summary>A frame's command payloads, either in the parser's store or as typed messages.</summary>
    private readonly record struct QueuedFrame(
        byte[]? Block,
        int Offset,
        int Count,
        bool FromFullPacket,
        CSVCMsg_UserCommands[]? Typed);

    /// <summary>The outer fields of one queued command, its payloads as views into the frame.</summary>
    private ref struct QueuedCmd
    {
        public int Slot;
        public int CmdNumber;
        public int ClientTick;
        public bool HasClientTick;
        public ReadOnlySpan<byte> Data;
        public ReadOnlySpan<byte> Delta;
    }

    /// <summary>
    ///     Walks a queued frame's commands in wire order, reading the outer message by hand so that
    ///     nothing is allocated. A malformed stored payload is counted and the rest of it skipped.
    /// </summary>
    private ref struct FrameCommands
    {
        private readonly byte[]? _block;
        private readonly CSVCMsg_UserCommands[]? _typed;
        private int _offset;
        private int _remaining;
        private ReadOnlySpan<byte> _payload;
        private int _p;
        private int _typedMessage;
        private int _typedCommand;

        public long DecodeFailed;

        public FrameCommands(in QueuedFrame frame)
        {
            _block = frame.Block;
            _offset = frame.Offset;
            _remaining = frame.Count;
            _typed = frame.Typed;
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public bool MoveNext(out QueuedCmd cmd)
        {
            while (true)
            {
                if (_p < _payload.Length)
                {
                    try
                    {
                        if (TryReadStored(out cmd))
                        {
                            return true;
                        }
                    }
                    catch (InvalidDataException)
                    {
                        DecodeFailed++;
                        _p = _payload.Length;
                    }

                    continue;
                }

                if (_remaining > 0)
                {
                    _payload = UserCmdsStore.Read(_block!, ref _offset);
                    _remaining--;
                    _p = 0;
                    continue;
                }

                if (_typed is not null && _typedMessage < _typed.Length)
                {
                    CSVCMsg_UserCommands message = _typed[_typedMessage];
                    if (_typedCommand < message.Commands.Count)
                    {
                        CMsgServerUserCmd c = message.Commands[_typedCommand++];
                        cmd = new QueuedCmd
                        {
                            Slot = c.PlayerSlot,
                            CmdNumber = c.CmdNumber,
                            ClientTick = c.ClientTick,
                            HasClientTick = c.HasClientTick,
                            Data = c.HasData ? c.Data.Span : default,
                            Delta = c.HasDeltaData ? c.DeltaData.Span : default
                        };
                        return true;
                    }

                    _typedMessage++;
                    _typedCommand = 0;
                    continue;
                }

                cmd = default;
                return false;
            }
        }

        /// <summary>Reads the next <c>commands</c> entry of the current payload; false when the payload is exhausted.</summary>
        private bool TryReadStored(out QueuedCmd cmd)
        {
            while (_p < _payload.Length)
            {
                ulong tag = UserCmdDelta.ReadVarint(_payload, ref _p);
                int wire = (int)(tag & 7);
                if (tag >> 3 == CSVCMsg_UserCommands.CommandsFieldNumber && wire == LengthDelimitedWireType)
                {
                    ReadCommand(UserCmdDelta.ReadLengthDelimited(_payload, ref _p), out cmd);
                    return true;
                }

                Skip(_payload, ref _p, wire);
            }

            cmd = default;
            return false;
        }

        private static void ReadCommand(ReadOnlySpan<byte> entry, out QueuedCmd cmd)
        {
            cmd = default;
            int p = 0;
            while (p < entry.Length)
            {
                ulong tag = UserCmdDelta.ReadVarint(entry, ref p);
                int wire = (int)(tag & 7);
                switch ((int)(tag >> 3), wire)
                {
                    case (CMsgServerUserCmd.DataFieldNumber, LengthDelimitedWireType):
                        cmd.Data = UserCmdDelta.ReadLengthDelimited(entry, ref p);
                        break;
                    case (CMsgServerUserCmd.DeltaDataFieldNumber, LengthDelimitedWireType):
                        cmd.Delta = UserCmdDelta.ReadLengthDelimited(entry, ref p);
                        break;
                    case (CMsgServerUserCmd.CmdNumberFieldNumber, 0):
                        cmd.CmdNumber = (int)UserCmdDelta.ReadVarint(entry, ref p);
                        break;
                    case (CMsgServerUserCmd.PlayerSlotFieldNumber, 0):
                        cmd.Slot = (int)UserCmdDelta.ReadVarint(entry, ref p);
                        break;
                    case (CMsgServerUserCmd.ClientTickFieldNumber, 0):
                        cmd.ClientTick = (int)UserCmdDelta.ReadVarint(entry, ref p);
                        cmd.HasClientTick = true;
                        break;
                    default:
                        Skip(entry, ref p, wire);
                        break;
                }
            }
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
    }

    private sealed class SlotState
    {
        public int CmdNumber;

        /// <summary>The latest command, or null while the slot waits for a keyframe.</summary>
        public CSGOUserCmdPB? Command;

        /// <summary>On demand: the newest queued frame whose commands this slot has applied.</summary>
        public int ConsumedSeq = -1;

        /// <summary>Scratch for a rebuild: the slot's newest keyframe in the range, or -1.</summary>
        public int StartSeq = -1;

        public int StartIndex;

        /// <summary>Scratch for a rebuild of every slot: the slot has commands in the range.</summary>
        public bool SeenInRange;
    }

    private struct Counters
    {
        public long Full, Delta, CheckpointPrimed, CheckpointDuplicates, CheckpointMismatches;
        public long MissingBaseline, OutOfOrder, DecodeFailed, Empty, UnknownFieldsSkipped, ClientTickMismatches;
    }
}
