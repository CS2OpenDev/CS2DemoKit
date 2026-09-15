#region

using System.Buffers;
using CS2DemoKit.Parser.Entities;
using Google.Protobuf;
using Snappier;

#endregion

namespace CS2DemoKit.Parser;

/// <summary>
///     Parses a CS2 .dem file into a flat list of <see cref="DemoFrame" /> objects by
///     reading the binary frame stream directly and deserializing each protobuf payload.
///     No entity state reconstruction is performed — all message fields including
///     <c>svc_PacketEntities.entity_data</c> are treated as opaque bytes.
/// </summary>
public static class DemoParser
{
    /// <summary>
    ///     Raised whenever <see cref="ParseNetMessage" /> sees a net-message type ID it has
    ///     no parser registered for. Carries the occurrence's frame number, type ID/name, and
    ///     byte-approximate offset + length within the decompressed frame payload (see
    ///     <see cref="UnknownMessageInfo" />). The default type name for an unrecognized ID is
    ///     <c>"unknown(N)"</c> from the name-cache miss path. The message is still dropped from
    ///     <see cref="DemoFrame.InnerMessages" /> — this event is the only trace it leaves, so
    ///     downstream tooling can surface protocol additions Valve has shipped that this parser
    ///     hasn't yet added a case for.
    ///     <para>
    ///         <b>Threading:</b> raised from the decode workers. Handlers MUST be thread-safe:
    ///         <c>System.Collections.Concurrent</c> types, <c>Interlocked</c>, or explicit locks.
    ///     </para>
    ///     <para>
    ///         <b>Process-global.</b> Concurrent parses on a shared queue see each other's
    ///         occurrences interleaved here. <see cref="ParseOptions.OnUnknownMessage" /> (0.8+) is
    ///         scoped to one parse and fires ADDITIONALLY, never instead.
    ///     </para>
    /// </summary>
    public static event Action<UnknownMessageInfo>? OnUnknownMessageType;

    // ── Public entry point ────────────────────────────────────────────────

    /// <summary>
    ///     Parses a CS2 demo file from an in-memory buffer: <see cref="DemoReader.Materialize" />
    ///     over a reader opened on <paramref name="data" />. Every header is scanned, every frame
    ///     decoded in one parallel window, then enriched in order (game events, player info, the
    ///     <see cref="RuntimeSchema" />).
    /// </summary>
    /// <param name="data">
    ///     The raw .dem file bytes.  Call <c>array.AsMemory()</c> to wrap an existing
    ///     <c>byte[]</c> without copying — the parser slices directly into this buffer
    ///     for uncompressed frame payloads, eliminating per-frame allocations.
    /// </param>
    /// <param name="profileOverride">
    ///     Optional explicit <see cref="DemoProfile" /> to assign to the parsed demo,
    ///     bypassing <see cref="DemoSourceClassifier" />'s header heuristics.  Use when
    ///     callers know better than the auto-classifier (testing, mislabeled headers,
    ///     dev tooling).  When <c>null</c> the source is auto-classified.
    /// </param>
    /// <returns>
    ///     A <see cref="ParsedDemo" /> containing all frames plus enriched indexes
    ///     (game events, player info, schema).
    /// </returns>
    /// <exception cref="InvalidDataException">
    ///     Thrown only when the input is not a CS2 demo: missing magic bytes, or too short to hold
    ///     a header. Damage inside a real demo does not throw. It yields the frames that decoded,
    ///     a warning saying what was lost, and a <see cref="ParsedDemo.Health" /> below
    ///     <see cref="ParseHealth.Clean" />.
    /// </exception>
    public static ParsedDemo Parse(ReadOnlyMemory<byte> data, DemoProfile? profileOverride = null) =>
        ParseCore(data, null, profileOverride);

    /// <summary>
    ///     Overload of <see cref="Parse(ReadOnlyMemory{byte},DemoProfile)" /> accepting
    ///     <see cref="ParseOptions" /> (0.8+): cooperative cancellation, a pass-2 parallelism cap,
    ///     progress reporting, a per-parse unknown-message callback, and opt-in net-message
    ///     drop-site counting (surfaced via <see cref="ParsedDemo.Warnings" /> —
    ///     <see cref="ParseWarningCodes.NetMessageDropped" />). Everything documented on the base
    ///     overload applies unchanged; this overload only ADDS what <see cref="ParseOptions" />
    ///     documents.
    /// </summary>
    /// <param name="data">The raw .dem file bytes (see the base overload).</param>
    /// <param name="options">The per-parse knobs; never <c>null</c>.</param>
    /// <param name="profileOverride">Optional explicit profile (see the base overload).</param>
    /// <returns>A <see cref="ParsedDemo" />, exactly as the base overload produces.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="options" /> is null.</exception>
    /// <exception cref="OperationCanceledException">
    ///     Thrown if <see cref="ParseOptions.CancellationToken" /> is canceled before the parse
    ///     completes; no partial <see cref="ParsedDemo" /> is returned.
    /// </exception>
    public static ParsedDemo Parse(ReadOnlyMemory<byte> data, ParseOptions options,
        DemoProfile? profileOverride = null) =>
        ParseCore(data, options ?? throw new ArgumentNullException(nameof(options)), profileOverride);

    private static ParsedDemo ParseCore(ReadOnlyMemory<byte> data, ParseOptions? options, DemoProfile? profileOverride)
    {
        using DemoReader reader = DemoReader.Open(data, options, profileOverride);
        return reader.Materialize();
    }

    // ── Proto wire helpers ────────────────────────────────────────────────

    /// <summary>
    ///     Scans <paramref name="data" /> for the first occurrence of a length-delimited field
    ///     with <paramref name="fieldNumber" /> and returns the absolute byte offsets of the field's
    ///     payload within <paramref name="data" /> via <paramref name="payloadStart" /> and
    ///     <paramref name="payloadLength" />.
    ///     Returns false and zeros if the field is not found or if the wire format is malformed.
    ///     <para>
    ///         Visibility is <c>internal</c> rather than <c>private</c> so
    ///         <see cref="DownstreamUtilities" /> can reuse it when slicing inner-message
    ///         bytes for the hex view; same-assembly access, not a public API.
    ///     </para>
    /// </summary>
    internal static bool FindBytesField(
        ReadOnlySpan<byte> data, int fieldNumber,
        out int payloadStart, out int payloadLength)
    {
        // Walk the proto wire format with an explicit index so we always know the
        // absolute byte position — required to return payloadStart as an offset into
        // the original span rather than a relative remaining-bytes count.
        int i = 0;
        while (i < data.Length)
        {
            // Read field tag: (fieldNumber << 3) | wireType.
            if (!Leb128Utils.TryReadUInt32(data[i..], out uint tag, out int tagBytes))
            {
                break;
            }

            i += tagBytes;

            int wireType = (int)(tag & 7);
            int fieldNum = (int)(tag >> 3);

            if (wireType == 2 && fieldNum == fieldNumber)
            {
                // Read the length varint; 'i' now points at the payload start.
                if (!Leb128Utils.TryReadUInt32(data[i..], out uint len, out int lenBytes))
                {
                    break;
                }

                i += lenBytes;
                payloadStart = i;
                payloadLength = (int)len;
                return true;
            }

            // Skip this field's value to advance to the next field.
            switch (wireType)
            {
                case 0: // varint — read and discard (continuation bits vary in length)
                    if (!Leb128Utils.TryReadUInt32(data[i..], out _, out int skipVarBytes))
                    {
                        goto done;
                    }

                    i += skipVarBytes;
                    break;
                case 1: i += 8; break; // fixed 64-bit
                case 5: i += 4; break; // fixed 32-bit
                case 2: // length-delimited — skip payload
                    if (!Leb128Utils.TryReadUInt32(data[i..], out uint skipLen, out int skipLenBytes))
                    {
                        goto done;
                    }

                    i += skipLenBytes + (int)skipLen;
                    break;
                default:
                    goto done; // unknown wire type — cannot skip safely
            }

            continue;

            done:
            break;
        }

        payloadStart = 0;
        payloadLength = 0;
        return false;
    }

    // ── Drop warnings ──────────────────────────────────────────────────────

    /// <summary>
    ///     Orders dropped net-message types for warning emission: by count descending, then by
    ///     type name. Total order, so the result does not depend on the enumeration order of
    ///     <paramref name="dropTotals" />.
    /// </summary>
    /// <remarks>
    ///     <c>internal</c> so the ordering can be tested without a demo that drops messages.
    /// </remarks>
    internal static List<KeyValuePair<string, int>> RankDropTypes(IReadOnlyDictionary<string, int> dropTotals) =>
        dropTotals
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => kv.Key, StringComparer.Ordinal)
            .ToList();

    /// <summary>
    ///     Emits the drop-site tallies as warnings: the top eight types by count plus one remainder
    ///     summary. Must run last, after every structural warning, so those claim the budget first.
    /// </summary>
    internal static void EmitDropWarnings(ParseDiagnostics diagnostics, IReadOnlyDictionary<string, int>? dropTotals)
    {
        if (dropTotals is not { Count: > 0 })
        {
            return;
        }

        List<KeyValuePair<string, int>> ordered = RankDropTypes(dropTotals);
        foreach ((string type, int n) in ordered.Take(8))
        {
            diagnostics.Warn(ParseWarningCodes.NetMessageDropped, $"{type} dropped", count: n);
        }

        if (ordered.Count > 8)
        {
            diagnostics.Warn(ParseWarningCodes.NetMessageDropped,
                $"{ordered.Count - 8} more distinct type(s) dropped", count: ordered.Skip(8).Sum(kv => kv.Value));
        }
    }

    /// <summary>
    ///     Reports an unmapped message type ID via <see cref="OnUnknownMessageType" /> and
    ///     returns null so the caller's <c>if (msg is null) continue;</c> drops the message.
    ///     Separate from the switch arm so the event is fired once per occurrence. The
    ///     <paramref name="frameNumber" />, <paramref name="decompressedStart" />, and
    ///     <paramref name="length" /> are forwarded so the UI can locate the dropped bytes.
    /// </summary>
    private static IMessage? HandleUnknown(int typeId, string typeName,
        int frameNumber, int decompressedStart, int length,
        Action<UnknownMessageInfo>? onUnknownMessage)
    {
        UnknownMessageInfo info = new(frameNumber, typeId, typeName, decompressedStart, length);
        OnUnknownMessageType?.Invoke(info);
        onUnknownMessage?.Invoke(info);
        return null;
    }

    // ── Frame parsing ─────────────────────────────────────────────────────

    /// <summary>
    ///     Given a decoded frame header and the (already-decompressed) payload, builds a
    ///     <see cref="DemoFrame" /> with the <see cref="DemoFrame.InnerMessages" /> the plan asked for.
    ///     Packet payloads are sliced with <see cref="FindBytesField" /> rather than parsed as an outer
    ///     proto, so a plan that decodes nothing never copies a payload; the outer parse remains as
    ///     the fallback for a payload the field scan cannot walk.
    /// </summary>
    /// <param name="cmd">The <see cref="EDemoCommands" /> value (compressed flag already stripped).</param>
    /// <param name="tick">Server tick; <c>-1</c> for pre-recording frames.</param>
    /// <param name="framePayload">
    ///     For uncompressed frames this is a zero-copy slice of the original demo buffer.
    ///     For compressed frames this is the Snappy-decompressed heap buffer.
    ///     All proto <c>ParseFrom</c> calls wrap it in a <see cref="ReadOnlySequence{T}" />
    ///     (allocation-free) to avoid copying.
    /// </param>
    /// <param name="rawStart">Byte offset of this frame's first header byte within the raw .dem file.</param>
    /// <param name="headerLength">Byte length of the three ULEB128 header varints.</param>
    /// <param name="rawPayloadSize">Byte length of the payload as stored in the file (compressed or not).</param>
    /// <param name="isCompressed">Whether the payload was Snappy-compressed on disk.</param>
    /// <param name="frameNumber">
    ///     Zero-based index of this frame in the file (set on <see cref="DemoFrame.FrameNumber" />).
    /// </param>
    /// <param name="onUnknownMessage">
    ///     The per-parse unknown-message callback from <see cref="ParseOptions.OnUnknownMessage" />,
    ///     or <c>null</c> when the caller supplied no options. Pure plumbing down to
    ///     <see cref="HandleUnknown" />.
    /// </param>
    /// <param name="dropCounts">
    ///     This worker's drop-count accumulator when
    ///     <see cref="ParseOptions.CountDropSites" /> is on, else <c>null</c>. Thread-owned — never
    ///     shared between workers.
    /// </param>
    /// <param name="state">This partition's user-command store and counters; bracketed per frame here.</param>
    /// <param name="mask">The compiled plan.</param>
    private static DemoFrame ParseFrame(
        EDemoCommands cmd,
        int tick,
        ReadOnlyMemory<byte> framePayload,
        int rawStart,
        int headerLength,
        int rawPayloadSize,
        bool isCompressed,
        int frameNumber,
        Action<UnknownMessageInfo>? onUnknownMessage,
        Dictionary<string, int>? dropCounts,
        PartitionState state,
        DecodeMask mask)
    {
        int rawLength = headerLength + rawPayloadSize;
        UserCmdsWriter userCmds = state.UserCmds;

        // Brackets every ParseInnerMessages call below, so a frame's user-command payloads land in one
        // contiguous block run.
        userCmds.BeginFrame();

        string name = NetMessageCatalog.DemoCommandName(cmd);

        // Wrap the Memory in a single-segment ReadOnlySequence.  This is a pure struct
        // operation — no heap allocation — and satisfies the ParseFrom(ReadOnlySequence<byte>)
        // overload available in Google.Protobuf 3.21+.
        ReadOnlySequence<byte> payloadSeq = new(framePayload);
        List<InnerMessageHeader>? headers = mask.RecordStructure ? [] : null;

        // DEM_Packet / DEM_SignonPacket: outer CDemoPacket is a transport envelope; the actual
        // subcomponents are the net messages multiplexed in CDemoPacket.data (field 3).
        if (cmd is EDemoCommands.DemPacket or EDemoCommands.DemSignonPacket)
        {
            List<NetMessage> packetMessages;
            if (FindBytesField(framePayload.Span, 3, out int dataFieldStart, out int dataLen))
            {
                packetMessages = ParseInnerMessages(framePayload.Span.Slice(dataFieldStart, dataLen), dataFieldStart,
                    frameNumber, onUnknownMessage, dropCounts, state, mask, headers);
            }
            else
            {
                CDemoPacket? outer = Try(CDemoPacket.Parser, payloadSeq, name);
                packetMessages = outer is not null
                    ? ParseInnerMessages(outer.Data.Span, 0, frameNumber, onUnknownMessage, dropCounts, state, mask, headers)
                    : [];
            }

            (byte[]? packetBlock, int packetOffset, int packetCount) = userCmds.EndFrame();

            return new DemoFrame
            {
                ServerTick = tick,
                GameTick = tick,
                FrameNumber = frameNumber,
                CommandKind = cmd,
                RawStart = rawStart,
                RawLength = rawLength,
                HeaderLength = headerLength,
                IsCompressed = isCompressed,
                MessageList = packetMessages,
                UserCmdsBlock = packetBlock,
                UserCmdsOffset = packetOffset,
                UserCmdsCount = packetCount,
                InnerMessageHeaders = headers is { Count: > 0 } ? headers.ToArray() : default
            };
        }

        // DEM_FullPacket is a seek checkpoint bundling:
        //   [0] CDemoStringTables: full string-table snapshot at this tick (field 1)
        //   [1..N] net messages from the nested CDemoPacket (field 2, then its field 3)
        if (cmd == EDemoCommands.DemFullPacket)
        {
            List<NetMessage> messages = [];
            bool foundTables = FindBytesField(framePayload.Span, 1, out int stStart, out int stLen);
            bool foundPacket = FindBytesField(framePayload.Span, 2, out int packetBytesStart, out int packetBytesLen);
            if (foundTables || foundPacket)
            {
                if (foundTables && mask.FullPacketStringTable)
                {
                    ReadOnlySequence<byte> tablesSeq = new(framePayload.Slice(stStart, stLen));
                    CDemoStringTables? st = Try(CDemoStringTables.Parser, tablesSeq, name);
                    if (st is not null)
                    {
                        messages.Add(new NetMessage
                        {
                            MessageTypeName = "DEM_StringTables",
                            Payload = st,
                            DecompressedStart = stLen > 0 ? stStart : null,
                            DecompressedLength = stLen > 0 ? stLen : null
                        });
                    }
                }

                if (foundPacket && packetBytesLen > 0
                    && FindBytesField(framePayload.Span.Slice(packetBytesStart, packetBytesLen), 3,
                        out int dataRelStart, out int innerLen))
                {
                    int absoluteDataFieldStart = packetBytesStart + dataRelStart;
                    messages.AddRange(ParseInnerMessages(framePayload.Span.Slice(absoluteDataFieldStart, innerLen),
                        absoluteDataFieldStart, frameNumber, onUnknownMessage, dropCounts, state, mask, headers));
                }
            }
            else
            {
                CDemoFullPacket? outer = Try(CDemoFullPacket.Parser, payloadSeq, name);
                if (outer?.StringTable is { } st && mask.FullPacketStringTable)
                {
                    messages.Add(new NetMessage
                    {
                        MessageTypeName = "DEM_StringTables",
                        Payload = st,
                        DecompressedStart = null,
                        DecompressedLength = null
                    });
                }

                if (outer?.Packet is { } innerPacket)
                {
                    messages.AddRange(ParseInnerMessages(innerPacket.Data.Span, 0, frameNumber,
                        onUnknownMessage, dropCounts, state, mask, headers));
                }
            }

            (byte[]? fullBlock, int fullOffset, int fullCount) = userCmds.EndFrame();

            return new DemoFrame
            {
                ServerTick = tick,
                GameTick = tick,
                FrameNumber = frameNumber,
                CommandKind = cmd,
                RawStart = rawStart,
                RawLength = rawLength,
                HeaderLength = headerLength,
                IsCompressed = isCompressed,
                MessageList = messages,
                UserCmdsBlock = fullBlock,
                UserCmdsOffset = fullOffset,
                UserCmdsCount = fullCount,
                InnerMessageHeaders = headers is { Count: > 0 } ? headers.ToArray() : default
            };
        }

        // All remaining command types map 1-to-1 to a top-level protobuf message.
        // Notable: DEM_SendTables embeds a size-prefixed CSVCMsg_FlattenedSerializer inside
        // CDemoSendTables.data — that inner decode is handled by RuntimeSchema, not here.
        IMessage? payload = !mask.DecodesCommand(cmd) ? null : cmd switch
        {
            EDemoCommands.DemFileHeader => Try(CDemoFileHeader.Parser, payloadSeq, name),
            EDemoCommands.DemFileInfo => Try(CDemoFileInfo.Parser, payloadSeq, name),
            EDemoCommands.DemSyncTick => Try(CDemoSyncTick.Parser, payloadSeq, name),
            EDemoCommands.DemSendTables => Try(CDemoSendTables.Parser, payloadSeq, name),
            EDemoCommands.DemClassInfo => Try(CDemoClassInfo.Parser, payloadSeq, name),
            EDemoCommands.DemStringTables => Try(CDemoStringTables.Parser, payloadSeq, name),
            EDemoCommands.DemConsoleCmd => Try(CDemoConsoleCmd.Parser, payloadSeq, name),
            EDemoCommands.DemCustomData => Try(CDemoCustomData.Parser, payloadSeq, name),
            EDemoCommands.DemCustomDataCallbacks => Try(CDemoCustomDataCallbacks.Parser, payloadSeq, name),
            EDemoCommands.DemUserCmd => Try(CDemoUserCmd.Parser, payloadSeq, name),
            EDemoCommands.DemSaveGame => Try(CDemoSaveGame.Parser, payloadSeq, name),
            EDemoCommands.DemSpawnGroups => Try(CDemoSpawnGroups.Parser, payloadSeq, name),
            EDemoCommands.DemAnimationData => Try(CDemoAnimationData.Parser, payloadSeq, name),
            EDemoCommands.DemAnimationHeader => Try(CDemoAnimationHeader.Parser, payloadSeq, name),
            EDemoCommands.DemRecovery => Try(CDemoRecovery.Parser, payloadSeq, name),
            _ => null
        };

        // Wrap the single payload as the one subcomponent of this frame.
        // DecompressedStart=0 because the message IS the full decompressed frame payload.
        List<NetMessage> directMessages = payload is not null
            ?
            [
                new NetMessage
                {
                    MessageTypeName = name,
                    Payload = payload,
                    DecompressedStart = 0,
                    DecompressedLength = framePayload.Length
                }
            ]
            : [];
        if (payload is not null)
        {
            state.MessagesDecoded++;
        }

        userCmds.EndFrame();
        return new DemoFrame
        {
            ServerTick = tick,
            GameTick = tick,
            FrameNumber = frameNumber,
            CommandKind = cmd,
            RawStart = rawStart,
            RawLength = rawLength,
            HeaderLength = headerLength,
            IsCompressed = isCompressed,
            MessageList = directMessages
        };
    }

    // ── Inner message multiplexing ────────────────────────────────────────
    // CDemoPacket.data is a BitBuffer-encoded stream of (UBitVar typeId, uvarint size, bytes payload).
    // typeId uses Source engine UBitVar encoding; size uses standard protobuf varint.

    /// <summary>
    ///     Reads the CDemoPacket.data bitstream and returns each embedded net message.
    /// </summary>
    /// <param name="data">The CDemoPacket.data bytes (the bitstream).</param>
    /// <param name="dataFieldStart">
    ///     Absolute byte offset within the decompressed frame payload where <paramref name="data" />[0] is.
    ///     Used to compute <see cref="NetMessage.DecompressedStart" /> for each inner message.
    /// </param>
    /// <param name="frameNumber">
    ///     The owning frame's <see cref="DemoFrame.FrameNumber" />, forwarded to
    ///     <see cref="OnUnknownMessageType" /> so unknown-message occurrences are seekable.
    /// </param>
    /// <param name="onUnknownMessage">
    ///     The per-parse unknown-message callback (<see cref="ParseOptions.OnUnknownMessage" />), or
    ///     <c>null</c>.
    /// </param>
    /// <param name="dropCounts">
    ///     This worker's drop-count accumulator (<see cref="ParseOptions.CountDropSites" />), or
    ///     <c>null</c>. Two of the three drop sites are in this method; the third is
    ///     <see cref="HandleUnknown" />'s caller arm.
    /// </param>
    /// <param name="state">
    ///     This partition's user-command store and counters. <c>svc_UserCmds</c> payloads are appended
    ///     to the store instead of becoming <see cref="NetMessage" /> entries.
    /// </param>
    /// <param name="mask">The compiled plan; an unplanned message is skipped in the bitstream unread.</param>
    /// <param name="headers">Receives one header per message when the plan records structure, else <c>null</c>.</param>
    private static List<NetMessage> ParseInnerMessages(ReadOnlySpan<byte> data, int dataFieldStart, int frameNumber,
        Action<UnknownMessageInfo>? onUnknownMessage, Dictionary<string, int>? dropCounts, PartitionState state,
        DecodeMask mask, List<InnerMessageHeader>? headers)
    {
        List<NetMessage> messages = [];
        int userCmdsSoFar = 0;
        BitBuffer buf = new(data);

        while (buf.RemainingBits > 0)
        {
            // typeId is an unsigned UBitVar; size is an unsigned varint byte count.
            int typeId = (int)buf.ReadUBitVar();
            int size = (int)buf.ReadUVarInt32();

            // Capture bit position immediately after the typeId + size header.
            // This is the start of the raw payload bytes within the CDemoPacket.data bitstream.
            // Note: the bitstream may not be byte-aligned here (UBitVar uses 6/10/14/34 bits,
            // UVarInt32 uses multiples of 8 bits), so DecompressedStart is byte-approximate.
            int bitPayloadStart = buf.TellBits;

            if (size <= 0 || size > buf.RemainingBytes)
            {
                // Third drop site: a corrupted size read abandons every remaining message in this
                // frame's bitstream. Counted once per truncation EVENT (the number of abandoned
                // messages is unknowable from here), not per message.
                if (dropCounts is not null)
                {
                    dropCounts["<bitstream-truncated>"] = dropCounts.GetValueOrDefault("<bitstream-truncated>") + 1;
                }

                break;
            }

            // Byte-approximate position within the decompressed frame payload.
            // bitPayloadStart >> 3 gives the byte-rounded start offset within CDemoPacket.data.
            // Computed before the parse so the unknown-message path can forward it (see HandleUnknown).
            int decompStart = dataFieldStart + (bitPayloadStart >> 3);

            // User commands go to the store and get no NetMessage. It is ~90% of the messages
            // in a demo, so one live object per message is what made parse GC-bound. Skipped
            // before the drop-site accounting below: this is a routing decision, not a decode
            // failure, and counting it would grade every demo Degraded.
            if (typeId == NetMessageCatalog.UserCmdsTypeId)
            {
                if (mask.UserCmds)
                {
                    byte[] cmdBytes = ArrayPool<byte>.Shared.Rent(size);
                    try
                    {
                        buf.ReadBytes(cmdBytes.AsSpan(0, size));
                        // Ordinal is the position this message occupies among the frame's messages, so
                        // DemoFrame.InnerMessages can present it in wire order without storing an object.
                        state.UserCmds.Append(cmdBytes.AsSpan(0, size), messages.Count + userCmdsSoFar);
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(cmdBytes);
                    }

                    userCmdsSoFar++;
                    state.UserCmdsStored++;
                }
                else
                {
                    buf.SkipBytes(size);
                    state.MessagesSkipped++;
                }

                headers?.Add(new InnerMessageHeader(typeId, size, decompStart, mask.UserCmds));
                continue;
            }

            if (!mask.DecodesNet(typeId))
            {
                buf.SkipBytes(size);
                state.MessagesSkipped++;
                headers?.Add(new InnerMessageHeader(typeId, size, decompStart, false));
                continue;
            }

            string typeName = NetMessageCatalog.NameOf(typeId);

            // Rent a pooled buffer, read the bitstream bytes into it, parse, then return.
            // Google.Protobuf copies all data out of the input buffer during ParseFrom,
            // so returning the rented array immediately after the call is safe.
            byte[] rented = ArrayPool<byte>.Shared.Rent(size);
            IMessage? msg;
            try
            {
                buf.ReadBytes(rented.AsSpan(0, size));
                msg = ParseNetMessage(typeId, new ReadOnlyMemory<byte>(rented, 0, size), typeName,
                    frameNumber, decompStart, onUnknownMessage);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented);
            }

            if (msg is null)
            {
                // Drop sites one and two: an unknown type ID (HandleUnknown returned null) or a
                // known type whose protobuf decode failed (Try<T> swallowed and returned null).
                if (dropCounts is not null)
                {
                    dropCounts[typeName] = dropCounts.GetValueOrDefault(typeName) + 1;
                }

                headers?.Add(new InnerMessageHeader(typeId, size, decompStart, false));
                continue;
            }

            state.MessagesDecoded++;
            headers?.Add(new InnerMessageHeader(typeId, size, decompStart, true));
            messages.Add(new NetMessage
            {
                MessageTypeName = typeName,
                Payload = msg,
                DecompressedStart = decompStart,
                DecompressedLength = size
            });
        }

        return messages;
    }

    private static IMessage? ParseNetMessage(int typeId, ReadOnlyMemory<byte> data, string typeName,
        int frameNumber, int decompressedStart, Action<UnknownMessageInfo>? onUnknownMessage)
    {
        // Wrap once here; all Try() calls in the switch share this single struct.
        ReadOnlySequence<byte> seq = new(data);
        return typeId switch
        {
            // NET_ messages
            (int)NET_Messages.NetSplitScreenUser => Try(CNETMsg_SplitScreenUser.Parser, seq, typeName),
            (int)NET_Messages.NetTick => Try(CNETMsg_Tick.Parser, seq, typeName),
            (int)NET_Messages.NetStringCmd => Try(CNETMsg_StringCmd.Parser, seq, typeName),
            (int)NET_Messages.NetSetConVar => Try(CNETMsg_SetConVar.Parser, seq, typeName),
            (int)NET_Messages.NetSignonState => Try(CNETMsg_SignonState.Parser, seq, typeName),
            (int)NET_Messages.NetSpawnGroupLoad => Try(CNETMsg_SpawnGroup_Load.Parser, seq, typeName),
            (int)NET_Messages.NetSpawnGroupManifestUpdate => Try(CNETMsg_SpawnGroup_ManifestUpdate.Parser, seq, typeName),
            (int)NET_Messages.NetSpawnGroupSetCreationTick => Try(CNETMsg_SpawnGroup_SetCreationTick.Parser, seq, typeName),
            (int)NET_Messages.NetSpawnGroupUnload => Try(CNETMsg_SpawnGroup_Unload.Parser, seq, typeName),

            // Bidirectional messages
            (int)Bidirectional_Messages.BiRebroadcastGameEvent => Try(CBidirMsg_RebroadcastGameEvent.Parser, seq, typeName),
            (int)Bidirectional_Messages.BiRebroadcastSource => Try(CBidirMsg_RebroadcastSource.Parser, seq, typeName),

            // Game event messages (EBaseGameEvents range — 200-212)
            (int)EBaseGameEvents.GeSource1LegacyGameEventList => Try(CMsgSource1LegacyGameEventList.Parser, seq, typeName),
            (int)EBaseGameEvents.GeSource1LegacyGameEvent => Try(CMsgSource1LegacyGameEvent.Parser, seq, typeName),

            // SVC_ messages
            (int)SVC_Messages.SvcServerInfo => Try(CSVCMsg_ServerInfo.Parser, seq, typeName),
            (int)SVC_Messages.SvcFlattenedSerializer => Try(CSVCMsg_FlattenedSerializer.Parser, seq, typeName),
            (int)SVC_Messages.SvcClassInfo => Try(CSVCMsg_ClassInfo.Parser, seq, typeName),
            (int)SVC_Messages.SvcSetPause => Try(CSVCMsg_SetPause.Parser, seq, typeName),
            (int)SVC_Messages.SvcCreateStringTable => Try(CSVCMsg_CreateStringTable.Parser, seq, typeName),
            (int)SVC_Messages.SvcUpdateStringTable => Try(CSVCMsg_UpdateStringTable.Parser, seq, typeName),
            (int)SVC_Messages.SvcVoiceInit => Try(CSVCMsg_VoiceInit.Parser, seq, typeName),
            (int)SVC_Messages.SvcVoiceData => Try(CSVCMsg_VoiceData.Parser, seq, typeName),
            (int)SVC_Messages.SvcPrint => Try(CSVCMsg_Print.Parser, seq, typeName),
            (int)SVC_Messages.SvcSounds => Try(CSVCMsg_Sounds.Parser, seq, typeName),
            (int)SVC_Messages.SvcSetView => Try(CSVCMsg_SetView.Parser, seq, typeName),
            (int)SVC_Messages.SvcClearAllStringTables => Try(CSVCMsg_ClearAllStringTables.Parser, seq, typeName),
            (int)SVC_Messages.SvcCmdKeyValues => Try(CSVCMsg_CmdKeyValues.Parser, seq, typeName),
            (int)SVC_Messages.SvcBspdecal => Try(CSVCMsg_BSPDecal.Parser, seq, typeName),
            (int)SVC_Messages.SvcSplitScreen => Try(CSVCMsg_SplitScreen.Parser, seq, typeName),
            (int)SVC_Messages.SvcPacketEntities => Try(CSVCMsg_PacketEntities.Parser, seq, typeName),
            (int)SVC_Messages.SvcPrefetch => Try(CSVCMsg_Prefetch.Parser, seq, typeName),
            (int)SVC_Messages.SvcMenu => Try(CSVCMsg_Menu.Parser, seq, typeName),
            (int)SVC_Messages.SvcGetCvarValue => Try(CSVCMsg_GetCvarValue.Parser, seq, typeName),
            (int)SVC_Messages.SvcStopSound => Try(CSVCMsg_StopSound.Parser, seq, typeName),
            (int)SVC_Messages.SvcPeerList => Try(CSVCMsg_PeerList.Parser, seq, typeName),
            (int)SVC_Messages.SvcPacketReliable => Try(CSVCMsg_PacketReliable.Parser, seq, typeName),
            (int)SVC_Messages.SvcHltvstatus => Try(CSVCMsg_HLTVStatus.Parser, seq, typeName),
            (int)SVC_Messages.SvcServerSteamId => Try(CSVCMsg_ServerSteamID.Parser, seq, typeName),
            (int)SVC_Messages.SvcFullFrameSplit => Try(CSVCMsg_FullFrameSplit.Parser, seq, typeName),
            (int)SVC_Messages.SvcRconServerDetails => Try(CSVCMsg_RconServerDetails.Parser, seq, typeName),
            (int)SVC_Messages.SvcUserMessage => Try(CSVCMsg_UserMessage.Parser, seq, typeName),
            (int)SVC_Messages.SvcBroadcastCommand => Try(CSVCMsg_Broadcast_Command.Parser, seq, typeName),
            (int)SVC_Messages.SvcHltvFixupOperatorStatus => Try(CSVCMsg_HltvFixupOperatorStatus.Parser, seq, typeName),
            // svc_UserCmds never reaches here: ParseInnerMessages routes it into the user-command store
            // before this switch.
            _ => HandleUnknown(typeId, typeName, frameNumber, decompressedStart, data.Length, onUnknownMessage)
        };
    }

    /// <summary>
    ///     Parses <paramref name="data" /> using <paramref name="parser" />, returning
    ///     <c>null</c> instead of throwing on failure so the caller's null-check drops
    ///     the message. Takes the parser and sequence directly — no delegate closure allocation.
    ///     <paramref name="context" /> is reserved for future diagnostic plumbing.
    /// </summary>
    private static T? Try<T>(MessageParser<T> parser, in ReadOnlySequence<byte> data, string context)
        where T : class, IMessage<T>
    {
        _ = context;
        try
        {
            return parser.ParseFrom(data);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    ///     Extracts <see cref="RuntimeSchema" /> from a <c>CDemoSendTables</c> message.
    ///     <c>CDemoSendTables.data</c> = [uvarint size][CSVCMsg_FlattenedSerializer bytes].
    /// </summary>
    internal static RuntimeSchema? TryExtractSchema(CDemoSendTables sendTables)
    {
        if (sendTables.Data.IsEmpty)
        {
            return null;
        }

        try
        {
            ReadOnlySpan<byte> data = sendTables.Data.Span;
            BitBuffer buf = new(data);
            int size = (int)buf.ReadUVarInt32();
            return RuntimeSchema.Parse(CSVCMsg_FlattenedSerializer.Parser.ParseFrom(data.Slice(data.Length - buf.RemainingBytes, size)));
        }
        catch
        {
            return null;
        }
    }

    // ── Frame scan and decode, the reader's per-frame body ───────────────

    /// <summary>What the scan of one frame header found.</summary>
    internal enum FrameScanResult
    {
        Frame,
        Stop,
        EndOfData,
        Truncated,
        Corrupt
    }

    /// <summary>
    ///     Scans the frame header at <paramref name="pos" /> and describes the frame, advancing
    ///     <paramref name="pos" /> past its payload. Every outcome but <see cref="FrameScanResult.Frame" />
    ///     ends the stream; the damaged ones warn first, so frames already scanned stay usable.
    /// </summary>
    internal static FrameScanResult TryScanFrame(ReadOnlyMemory<byte> data, ref int pos, int framesSoFar,
        ParseDiagnostics diagnostics, out FrameDescriptor descriptor)
    {
        descriptor = default;
        if (pos >= data.Length)
        {
            return FrameScanResult.EndOfData;
        }

        int frameStart = pos;
        int headerBytes = Leb128Utils.ParseFrameHeader(data.Span[pos..], out FrameHeader header);
        if (headerBytes < 0)
        {
            diagnostics.Warn(ParseWarningCodes.DemoTruncated,
                $"frame header at byte {frameStart} is incomplete ({data.Length - frameStart} byte(s) left); "
                + $"stopped after {framesSoFar} frame(s).");
            return FrameScanResult.Truncated;
        }

        pos += headerBytes;

        // DEM_Stop marks end of recording; no payload follows.
        if ((EDemoCommands)header.Command == EDemoCommands.DemStop)
        {
            return FrameScanResult.Stop;
        }

        int size = (int)header.Size;
        if (size < 0)
        {
            // Frame offsets chain, so this is unrecoverable for the rest of the stream, but the
            // frames already scanned are intact and a caller can use them.
            diagnostics.Warn(ParseWarningCodes.FrameStreamCorrupt,
                $"frame at byte {frameStart} declares size {header.Size}, which cannot be real; "
                + $"cannot resynchronize, stopped after {framesSoFar} frame(s).", header.Tick);
            return FrameScanResult.Corrupt;
        }

        if (pos + size > data.Length)
        {
            diagnostics.Warn(ParseWarningCodes.DemoTruncated,
                $"frame at byte {frameStart} declares {size} payload byte(s) but only "
                + $"{data.Length - pos} remain; stopped after {framesSoFar} frame(s).", header.Tick);
            return FrameScanResult.Truncated;
        }

        // Zero-copy slice for both cases: a direct view for uncompressed frames, the compressed
        // bytes otherwise (Snappy inflates at decode time).
        descriptor = new FrameDescriptor(
            frameStart, headerBytes,
            (EDemoCommands)header.Command, header.Tick,
            size, header.IsCompressed,
            data.Slice(pos, size));
        pos += size;
        return FrameScanResult.Frame;
    }

    /// <summary>
    ///     Decodes one scanned frame: inflates a compressed payload into the partition's grow-only
    ///     buffer, then parses what the plan asks for. A command the plan does not decode yields a
    ///     frame with offsets and no messages, without touching its payload.
    /// </summary>
    internal static DemoFrame DecodeFrame(FrameDescriptor d, int frameNumber, PartitionState state, DecodeMask mask,
        Action<UnknownMessageInfo>? onUnknownMessage, Dictionary<string, int>? dropCounts)
    {
        if (!mask.DecodesCommand(d.Command))
        {
            return new DemoFrame
            {
                ServerTick = d.Tick,
                GameTick = d.Tick,
                FrameNumber = frameNumber,
                CommandKind = d.Command,
                RawStart = d.RawStart,
                RawLength = d.HeaderLength + d.RawPayloadSize,
                HeaderLength = d.HeaderLength,
                IsCompressed = d.IsCompressed
            };
        }

        ReadOnlyMemory<byte> payload;
        if (d.IsCompressed)
        {
            int decompressedLength = Snappy.GetUncompressedLength(d.RawPayload.Span);
            if (state.DecompressBuffer.Length < decompressedLength)
            {
                state.DecompressBuffer = new byte[decompressedLength]; // grow-only, never shrink
            }

            byte[] decompressBuffer = state.DecompressBuffer;
            int written = Snappy.Decompress(d.RawPayload.Span, decompressBuffer);
            state.BytesDecompressed += written;
            // Slice to the exact written count. The buffer may be larger than this frame from a
            // prior iteration, and the proto parser would read the trailing garbage.
            payload = decompressBuffer.AsMemory(0, written);
        }
        else
        {
            payload = d.RawPayload;
        }

        return ParseFrame(d.Command, d.Tick, payload,
            d.RawStart, d.HeaderLength, d.RawPayloadSize, d.IsCompressed, frameNumber,
            onUnknownMessage, dropCounts, state, mask);
    }

    /// <summary>
    ///     Lightweight record of a single frame's location and metadata, populated by the
    ///     sequential header scan and consumed by the payload decode.
    ///     <see cref="RawPayload" /> is a zero-copy slice of the caller's buffer for uncompressed
    ///     frames, or the compressed bytes when <see cref="IsCompressed" /> is true.
    /// </summary>
    internal readonly record struct FrameDescriptor(
        int RawStart,
        int HeaderLength,
        EDemoCommands Command,
        int Tick,
        int RawPayloadSize,
        bool IsCompressed,
        ReadOnlyMemory<byte> RawPayload);

    /// <summary>
    ///     One decoder's scratch state and counters: a window decode partition, or the sequential
    ///     read. Must stay owned by one decoder: concurrent workers would stomp a shared decompress
    ///     buffer, and a shared store writer would interleave two frames' payload runs.
    /// </summary>
    internal sealed class PartitionState
    {
        public byte[] DecompressBuffer = [];
        public readonly UserCmdsWriter UserCmds = new();
        public long MessagesDecoded;
        public long MessagesSkipped;
        public long UserCmdsStored;
        public long BytesDecompressed;
    }
}
