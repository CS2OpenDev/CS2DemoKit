#region

using System.Collections.Frozen;
using System.Reflection;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Snappier;

#endregion

namespace CS2DemoKit.Parser;

/// <summary>
///     Stable convenience API for consumers (UI, probes, hex-view) that need to extract,
///     display, or scan the parser's intermediate bytes. These helpers are NOT used
///     internally by the parse pipeline — they exist so external code doesn't have to
///     re-implement frame-payload decompression, inner-message slicing, or proto-wire
///     scanning.
///     <para>
///         <b>Stability:</b> additions are safe in any release; signatures are safe to
///         evolve in minor versions. This file is intentionally NOT one of the protected
///         core-parser files — changes here don't require the explicit sign-off that
///         <see cref="DemoParser" /> / <see cref="BitBuffer" /> do.
///     </para>
/// </summary>
public static class DownstreamUtilities
{
    // ── Cheap header-only metadata (demo-library indexer, "instant" tier) ──────
    //
    // The demo-library browser scans directories of .dem files; a full DemoParser.Parse per file is far too
    // expensive to do eagerly (seconds each, holds the whole file in RAM). These read ONLY the first frame —
    // the DemFileHeader (a few hundred bytes) — so the map / server / version show instantly. Players, score
    // and reliable duration are NOT in the header (verified: CDemoFileInfo is often empty and the .dem.info
    // companion is a foreign GC blob), so those stay a background full-parse concern for the indexer's cache.

    private const uint DemIsCompressedFlag = 64; // EDemoCommands.DEM_IsCompressed bit on the frame command varint

    // Net-message type id → proto name, mirroring DemoParser's combined cache (NET → Bidirectional → SVC
    // → EBaseGameEvents, first writer wins on collision). This is the SAME mapping the parser uses to
    // assign NetMessage.MessageTypeName, so resolving a slice's type id here yields the exact string a
    // known message carries — the bridge ExtractInnerMessageBytesAligned matches on.
    private static readonly FrozenDictionary<int, string> _innerMsgNameByTypeId = BuildInnerMsgNameCache();

    /// <summary>
    ///     Re-extracts the exact raw proto bytes for each inner message, in the same order as
    ///     <see cref="DemoFrame.InnerMessages" />, without storing them long-term.
    ///     Call once per frame-selection with the cached decompressed payload to obtain bytes
    ///     suitable for <see cref="Scan" />-based node annotation.
    ///     Returns a <c>byte[]?[]</c> of length <c>frame.InnerMessages.Count</c>;
    ///     a null slot means bytes could not be recovered for that message.
    /// </summary>
    public static byte[]?[] ExtractInnerMessageBytes(DemoFrame frame, ReadOnlySpan<byte> decompressedPayload)
    {
        int count = frame.InnerMessages.Count;
        if (count == 0 || decompressedPayload.IsEmpty)
        {
            return [];
        }

        // Direct-payload frames: the single message IS the full decompressed payload.
        // DecompressedStart == 0 distinguishes them from inner messages.
        if (frame.Command is not ("DEM_Packet" or "DEM_SignonPacket" or "DEM_FullPacket"))
        {
            return [decompressedPayload.ToArray()];
        }

        // DEM_Packet / DEM_SignonPacket: CDemoPacket.data is field 3.
        if (frame.Command is "DEM_Packet" or "DEM_SignonPacket")
        {
            if (!DemoParser.FindBytesField(decompressedPayload, 3, out int dataStart, out int dataLen) || dataLen == 0)
            {
                return new byte[]?[count];
            }

            return ExtractBitBufferMessages(decompressedPayload.Slice(dataStart, dataLen), count);
        }

        // DEM_FullPacket: message 0 = CDemoStringTables (field 1); messages 1..N = inner messages
        // from CDemoPacket.data found at field 2 of CDemoFullPacket, then field 3 within that.
        {
            byte[]?[] result = new byte[]?[count];
            if (count > 0 && DemoParser.FindBytesField(decompressedPayload, 1, out int stStart, out int stLen) && stLen > 0)
            {
                result[0] = decompressedPayload.Slice(stStart, stLen).ToArray();
            }

            if (count > 1
                && DemoParser.FindBytesField(decompressedPayload, 2, out int pktStart, out int pktLen) && pktLen > 0
                && DemoParser.FindBytesField(decompressedPayload.Slice(pktStart, pktLen), 3, out int dataRel, out int dataLen) && dataLen > 0)
            {
                byte[]?[] inner = ExtractBitBufferMessages(
                    decompressedPayload.Slice(pktStart + dataRel, dataLen), count - 1);
                for (int i = 0; i < inner.Length && i + 1 < result.Length; i++)
                {
                    result[i + 1] = inner[i];
                }
            }

            return result;
        }
    }

    /// <summary>
    ///     Re-walks <paramref name="frame" />'s CDemoPacket bitstream and returns every embedded
    ///     net message — known AND unknown — in bitstream order, each with its type ID and exact
    ///     bytes. This is the exact-byte counterpart used to surface UNKNOWN net-messages (which
    ///     never become a <see cref="NetMessage" />) for reverse-engineering. The byte-rounded
    ///     <see cref="UnknownMessageInfo.DecompressedStart" /> can't be used to slice the
    ///     bit-interleaved buffer precisely — these bytes are read bit-exact, so they can.
    ///     Returns an empty list for non-packet frames (which never contain unknown messages).
    /// </summary>
    public static List<InnerMessageSlice> ExtractInnerMessageSlices(DemoFrame frame, ReadOnlySpan<byte> decompressedPayload)
    {
        List<InnerMessageSlice> slices = [];
        if (decompressedPayload.IsEmpty)
        {
            return slices;
        }

        // DEM_Packet / DEM_SignonPacket: CDemoPacket.data is field 3.
        if (frame.Command is "DEM_Packet" or "DEM_SignonPacket")
        {
            if (DemoParser.FindBytesField(decompressedPayload, 3, out int dataStart, out int dataLen) && dataLen > 0)
            {
                WalkInnerSlices(decompressedPayload.Slice(dataStart, dataLen), slices);
            }

            return slices;
        }

        // DEM_FullPacket: inner messages live in CDemoPacket.data found at field 2 → field 3.
        if (frame.Command == "DEM_FullPacket"
            && DemoParser.FindBytesField(decompressedPayload, 2, out int pktStart, out int pktLen) && pktLen > 0
            && DemoParser.FindBytesField(decompressedPayload.Slice(pktStart, pktLen), 3, out int dataRel, out int innerLen) && innerLen > 0)
        {
            WalkInnerSlices(decompressedPayload.Slice(pktStart + dataRel, innerLen), slices);
        }

        return slices;
    }

    // Mirrors DemoParser.ParseInnerMessages' bitstream walk (UBitVar typeId, UVarInt32 size,
    // size payload bytes) but keeps the type ID and reads every message including unknowns.
    private static void WalkInnerSlices(ReadOnlySpan<byte> data, List<InnerMessageSlice> outSlices)
    {
        BitBuffer buf = new(data);
        while (buf.RemainingBits > 0)
        {
            int typeId = (int)buf.ReadUBitVar();
            int size = (int)buf.ReadUVarInt32();
            if (size <= 0 || size > buf.RemainingBytes)
            {
                break;
            }

            outSlices.Add(new InnerMessageSlice(typeId, buf.ReadBytes(size)));
        }
    }

    /// <summary>
    ///     Like <see cref="ExtractInnerMessageBytes" />, but aligns the recovered bytes to
    ///     <see cref="DemoFrame.InnerMessages" /> BY NET-MESSAGE TYPE ID rather than positionally.
    ///     <para>
    ///         <see cref="ExtractInnerMessageBytes" /> assumes the i-th bitstream message is the i-th
    ///         <see cref="NetMessage" /> — wrong for any frame containing an UNKNOWN message (a type id
    ///         with no decoder, dropped from <see cref="DemoFrame.InnerMessages" /> but still present in
    ///         the bitstream). After the first unknown, every later known card got the wrong bytes.
    ///         <see cref="DemoFrame.InnerMessages" /> is an ordered SUBSEQUENCE of the full bitstream, so
    ///         we walk every slice (<see cref="ExtractInnerMessageSlices" />, which keeps the type id) and
    ///         match each known message to the next slice of the same type id.
    ///     </para>
    ///     <para>
    ///         The type-id↔name bridge is exact: the parser assigns
    ///         <see cref="NetMessage.MessageTypeName" /> from the same <c>OriginalNameAttribute</c> proto
    ///         cache this method rebuilds, so a slice's resolved name equals a known message's
    ///         <see cref="NetMessage.MessageTypeName" /> exactly when the type ids match.
    ///     </para>
    ///     <para>
    ///         Residual: a KNOWN type whose proto parse failed is dropped from
    ///         <see cref="DemoFrame.InnerMessages" /> yet stays in the bitstream with its known type id;
    ///         given two same-type messages in one frame where the first failed, the match can take the
    ///         wrong one. Strictly better than positional (wrong after ANY unknown) and needs nothing
    ///         from the protected parser.
    ///     </para>
    /// </summary>
    public static byte[]?[] ExtractInnerMessageBytesAligned(DemoFrame frame, ReadOnlySpan<byte> decompressedPayload)
    {
        int count = frame.InnerMessages.Count;
        if (count == 0 || decompressedPayload.IsEmpty)
        {
            return [];
        }

        // Direct-payload (non-packet) frames carry a single message that IS the whole payload — no inner
        // bitstream, so no unknowns and nothing to align.
        if (frame.Command is not ("DEM_Packet" or "DEM_SignonPacket" or "DEM_FullPacket"))
        {
            return [decompressedPayload.ToArray()];
        }

        byte[]?[] result = new byte[]?[count];

        // DEM_FullPacket message 0 is the CDemoStringTables blob (field 1), not part of the inner
        // bitstream; the aligned inner messages start at index 1.
        int innerStart = 0;
        if (frame.Command == "DEM_FullPacket")
        {
            if (DemoParser.FindBytesField(decompressedPayload, 1, out int stStart, out int stLen) && stLen > 0)
            {
                result[0] = decompressedPayload.Slice(stStart, stLen).ToArray();
            }

            innerStart = 1;
        }

        List<InnerMessageSlice> slices = ExtractInnerMessageSlices(frame, decompressedPayload);
        AlignSlicesToInnerMessages(slices, frame.InnerMessages, innerStart, result);
        return result;
    }

    // Match each known InnerMessage (an ordered subsequence of the bitstream) to the next slice of the
    // same net-message type id, advancing the cursor past each match. A failed lookup leaves that slot
    // null WITHOUT advancing the cursor, so one miss (e.g. an unresolved name) can't desync the rest.
    private static void AlignSlicesToInnerMessages(
        List<InnerMessageSlice> slices, IReadOnlyList<NetMessage> innerMessages, int innerStart, byte[]?[] result)
    {
        int sliceIdx = 0;
        for (int i = innerStart; i < innerMessages.Count && i < result.Length; i++)
        {
            string typeName = innerMessages[i].MessageTypeName;
            int found = -1;
            for (int j = sliceIdx; j < slices.Count; j++)
            {
                if (_innerMsgNameByTypeId.TryGetValue(slices[j].TypeId, out string? name) && name == typeName)
                {
                    found = j;
                    break;
                }
            }

            if (found < 0)
            {
                continue;
            }

            result[i] = slices[found].Bytes;
            sliceIdx = found + 1;
        }
    }

    private static FrozenDictionary<int, string> BuildInnerMsgNameCache()
    {
        Dictionary<int, string> result = new();
        AddProtoEnumNames<NET_Messages>(result);
        AddProtoEnumNames<Bidirectional_Messages>(result);
        AddProtoEnumNames<SVC_Messages>(result);
        AddProtoEnumNames<EBaseGameEvents>(result);
        return result.ToFrozenDictionary();
    }

    private static void AddProtoEnumNames<TEnum>(Dictionary<int, string> into) where TEnum : struct, Enum
    {
        foreach (FieldInfo field in typeof(TEnum).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            string? protoName = field.GetCustomAttribute<OriginalNameAttribute>()?.Name;
            if (protoName is null)
            {
                continue;
            }

            into.TryAdd((int)field.GetValue(null)!, protoName);
        }
    }

    // ── On-demand byte extraction ──────────────────────────────────────────

    /// <summary>
    ///     Returns the decompressed payload bytes for <paramref name="frame" /> by slicing
    ///     <paramref name="demoBytes" /> (the raw .dem file loaded separately by the caller).
    ///     If the frame was not compressed, a copy of the uncompressed slice is returned.
    /// </summary>
    /// <param name="frame">The frame whose payload to decompress.</param>
    /// <param name="demoBytes">The full raw .dem file bytes, loaded independently of the parser.</param>
    public static byte[] GetDecompressedPayload(DemoFrame frame, byte[] demoBytes) =>
        GetDecompressedPayload(frame, (ReadOnlySpan<byte>)demoBytes);

    /// <summary>
    ///     Span overload of <see cref="GetDecompressedPayload(DemoFrame,byte[])" />, for callers whose
    ///     raw .dem bytes are not a managed array — notably a
    ///     <see cref="MemoryMappedDemoSource" /> view (<c>src.Memory.Span</c>).
    ///     <para>
    ///         <b>Lifetime:</b> when <paramref name="demoBytes" /> comes from a mapped source, the source
    ///         must still be alive for the duration of this call. The returned array is always a fresh
    ///         managed copy, so it is safe to keep after the source is disposed.
    ///     </para>
    /// </summary>
    /// <param name="frame">The frame whose payload to decompress.</param>
    /// <param name="demoBytes">The full raw .dem file bytes.</param>
    public static byte[] GetDecompressedPayload(DemoFrame frame, ReadOnlySpan<byte> demoBytes)
    {
        ReadOnlySpan<byte> span = demoBytes.Slice(frame.PayloadStart, frame.PayloadLength);
        return frame.IsCompressed ? Snappy.DecompressToArray(span) : span.ToArray();
    }

    /// <summary>
    ///     Reads the cheap first-frame metadata (map name, server/client name, demo version) from a CS2 demo
    ///     file <b>without</b> a full parse: opens the file and reads only a small prefix (the DemFileHeader
    ///     frame is a few hundred bytes). For the demo-library indexer's instant tier. Returns false if the
    ///     file isn't a readable CS2 demo or the header can't be decoded.
    /// </summary>
    public static bool TryReadQuickInfo(string path, out DemoQuickInfo info)
    {
        info = default;
        try
        {
            using FileStream fs = File.OpenRead(path);
            // 256 KB covers the 16-byte file header + the small DemFileHeader frame with a huge margin.
            int want = (int)Math.Min(fs.Length, 256 * 1024);
            if (want < 16)
            {
                return false;
            }

            byte[] buf = new byte[want];
            int read = fs.Read(buf, 0, want);
            return TryReadQuickInfo(buf.AsSpan(0, read), out info);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    ///     Span overload of <see cref="TryReadQuickInfo(string,out DemoQuickInfo)" />: <paramref name="prefix" />
    ///     must contain at least the 16-byte file header plus the first (DemFileHeader) frame.
    /// </summary>
    public static bool TryReadQuickInfo(ReadOnlySpan<byte> prefix, out DemoQuickInfo info)
    {
        info = default;
        if (prefix.Length < 16 || !"PBDEMS2"u8.SequenceEqual(prefix[..7]))
        {
            return false;
        }

        int off = 16; // frames start after the 16-byte file header (magic + two int32LE fields)
        if (!TryReadPrefixVarint(prefix, ref off, out uint raw) ||
            !TryReadPrefixVarint(prefix, ref off, out _) || // tick
            !TryReadPrefixVarint(prefix, ref off, out uint size)) // payload size
        {
            return false;
        }

        bool compressed = (raw & DemIsCompressedFlag) != 0;
        uint cmd = raw & ~DemIsCompressedFlag;
        if (cmd != (uint)EDemoCommands.DemFileHeader || off + (int)size > prefix.Length)
        {
            return false; // not the expected header frame, or the prefix was too short to hold it
        }

        try
        {
            ReadOnlySpan<byte> payload = prefix.Slice(off, (int)size);
            byte[] data = compressed ? Snappy.DecompressToArray(payload) : payload.ToArray();
            CDemoFileHeader hdr = CDemoFileHeader.Parser.ParseFrom(data);
            info = new DemoQuickInfo(
                hdr.MapName ?? string.Empty, hdr.ServerName ?? string.Empty,
                hdr.ClientName ?? string.Empty, hdr.DemoVersionName ?? string.Empty);
            return !string.IsNullOrEmpty(hdr.MapName);
        }
        catch (InvalidProtocolBufferException)
        {
            return false;
        }
    }

    // Minimal bounds-checked uvarint reader for the header prefix (independent of the hot-path LEB128 decoder,
    // which assumes a valid full buffer). Returns false on truncation rather than throwing.
    private static bool TryReadPrefixVarint(ReadOnlySpan<byte> b, ref int off, out uint value)
    {
        value = 0;
        for (int shift = 0; shift < 35; shift += 7)
        {
            if (off >= b.Length)
            {
                return false;
            }

            byte x = b[off++];
            value |= (uint)(x & 0x7F) << shift;
            if ((x & 0x80) == 0)
            {
                return true;
            }
        }

        return false;
    }

    // ── Proto wire scanner (single-level, no recursion) ────────────────────
    //
    // Deliberately does NOT recurse into nested messages — the hex view re-invokes
    // Scan on sub-message bytes when the user expands a TreeView row. See P5 of the
    // parser architecture review (docs/parser-architecture-backlog.md) for rationale.

    /// <summary>
    ///     Returns one <see cref="FieldSpan" /> per top-level proto field occurrence in
    ///     <paramref name="data" />, in byte order. Repeated fields produce multiple spans
    ///     with the same <see cref="FieldSpan.FieldNumber" />.
    ///     Does NOT recurse into nested messages — call Scan on the sub-message's bytes separately.
    /// </summary>
    public static List<FieldSpan> Scan(byte[] data)
    {
        List<FieldSpan> spans = new();
        int pos = 0;

        while (pos < data.Length)
        {
            int fieldStart = pos;

            if (!Leb128Utils.TryReadUInt32(data, ref pos, out uint tag))
            {
                break;
            }

            if (tag == 0)
            {
                break;
            }

            int fieldNumber = (int)(tag >> 3);
            int wireType = (int)(tag & 0x7);
            if (fieldNumber == 0)
            {
                break;
            }

            switch (wireType)
            {
                case 0: // varint
                    if (!Leb128Utils.TrySkip(data, ref pos))
                    {
                        goto done;
                    }

                    break;
                case 1: // 64-bit fixed
                    if (pos + 8 > data.Length)
                    {
                        goto done;
                    }

                    pos += 8;
                    break;
                case 2: // length-delimited
                    if (!Leb128Utils.TryReadUInt32(data, ref pos, out uint len))
                    {
                        goto done;
                    }

                    // Compare as uint to avoid signed-int overflow when len is large.
                    // data.Length - pos is always >= 0 here (pos <= data.Length from the while guard).
                    if (len > (uint)(data.Length - pos))
                    {
                        goto done;
                    }

                    pos += (int)len;
                    break;
                case 5: // 32-bit fixed
                    if (pos + 4 > data.Length)
                    {
                        goto done;
                    }

                    pos += 4;
                    break;
                default:
                    goto done; // unknown wire type — stop scanning
            }

            spans.Add(new FieldSpan(fieldNumber, wireType, fieldStart, pos));
            continue;

            done:
            break;
        }

        return spans;
    }

    /// <summary>
    ///     Given a length-delimited <see cref="FieldSpan" /> (wireType == 2), returns the byte
    ///     range of the value payload (after the tag + length varints).
    ///     Returns false if the span is not length-delimited.
    /// </summary>
    public static bool TryGetPayloadRange(byte[] data, FieldSpan span,
        out int payloadStart, out int payloadLength)
    {
        payloadStart = -1;
        payloadLength = -1;
        if (span.WireType != 2)
        {
            return false;
        }

        int pos = span.Start;
        if (!Leb128Utils.TryReadUInt32(data, ref pos, out _))
        {
            return false; // skip tag
        }

        if (!Leb128Utils.TryReadUInt32(data, ref pos, out uint len))
        {
            return false; // read length
        }

        payloadStart = pos;
        payloadLength = (int)len;
        return true;
    }

    /// <summary>Reads the 4-byte little-endian payload from a fixed32-wire-type span.</summary>
    public static bool TryReadFixed32Value(byte[] data, FieldSpan span, out uint value)
    {
        value = 0;
        int pos = span.Start;
        if (!Leb128Utils.TryReadUInt32(data, ref pos, out _))
        {
            return false; // skip tag
        }

        if (pos + 4 > data.Length)
        {
            return false;
        }

        value = BitConverter.ToUInt32(data, pos);
        return true;
    }

    /// <summary>Reads the 8-byte little-endian payload from a fixed64-wire-type span.</summary>
    public static bool TryReadFixed64Value(byte[] data, FieldSpan span, out ulong value)
    {
        value = 0;
        int pos = span.Start;
        if (!Leb128Utils.TryReadUInt32(data, ref pos, out _))
        {
            return false; // skip tag
        }

        if (pos + 8 > data.Length)
        {
            return false;
        }

        value = BitConverter.ToUInt64(data, pos);
        return true;
    }

    /// <summary>Reads the varint payload from a varint-wire-type span.</summary>
    public static bool TryReadVarintValue(byte[] data, FieldSpan span, out ulong value)
    {
        value = 0;
        int pos = span.Start;
        if (!Leb128Utils.TryReadUInt32(data, ref pos, out _))
        {
            return false; // skip tag
        }

        return Leb128Utils.TryReadUInt64(data, ref pos, out value);
    }

    private static byte[]?[] ExtractBitBufferMessages(ReadOnlySpan<byte> data, int expectedCount)
    {
        List<byte[]?> results = new(expectedCount);
        BitBuffer buf = new(data);
        while (buf.RemainingBits > 0)
        {
            _ = buf.ReadUBitVar();
            int size = (int)buf.ReadUVarInt32();
            if (size <= 0 || size > buf.RemainingBytes)
            {
                break;
            }

            results.Add(buf.ReadBytes(size));
        }

        while (results.Count < expectedCount)
        {
            results.Add(null);
        }

        return results.ToArray();
    }

    /// <summary>
    ///     One inner message recovered from a frame's CDemoPacket bitstream: its net-message
    ///     type ID and exact payload bytes. Unlike <see cref="ExtractInnerMessageBytes" /> this
    ///     keeps EVERY message (known and unknown) in bitstream order and preserves the type ID,
    ///     so callers can locate the exact bytes of messages the parser dropped (unknown types).
    /// </summary>
    /// <param name="TypeId">Net-message type ID (UBitVar).</param>
    /// <param name="Bytes">Exact payload bytes for this message.</param>
    public readonly record struct InnerMessageSlice(int TypeId, byte[] Bytes);

    /// <summary>
    ///     Cheap first-frame demo metadata — no full parse. See <see cref="TryReadQuickInfo(string,out DemoQuickInfo)" />
    ///     .
    /// </summary>
    public readonly record struct DemoQuickInfo(string MapName, string ServerName, string ClientName, string DemoVersion);

    /// <summary>One field range located by the proto-wire scanner: the field number, wire type, and byte span.</summary>
    /// <param name="FieldNumber">Protobuf field number.</param>
    /// <param name="WireType">Protobuf wire-type discriminator (0=varint, 1=fixed64, 2=length-delim, 5=fixed32).</param>
    /// <param name="Start">Inclusive start offset within the payload.</param>
    /// <param name="End">Exclusive end offset within the payload.</param>
    public readonly record struct FieldSpan(int FieldNumber, int WireType, int Start, int End)
    {
        /// <summary>Length in bytes of the field's value span.</summary>
        public int Length => End - Start;

        /// <summary>Human-readable name of the wire type (e.g. <c>varint</c>, <c>length-delimited</c>).</summary>
        public string WireTypeName => WireType switch
        {
            0 => "varint",
            1 => "fixed64",
            2 => "length-delimited",
            5 => "fixed32",
            _ => $"wire{WireType}"
        };
    }
}

/// <summary>
///     Parse-pipeline profiling accumulators (opt-in at RUNTIME via <see cref="Profiling.Enabled" /> —
///     <c>CS2DEMOKIT_PROFILE=1</c> or <see cref="Profiling.Enabled" />). Populated by
///     the runtime-gated call-sites in <see cref="DemoParser" />; read once after a parse via
///     <see cref="ParseProfilingSnapshot.Read" />. Static because a parse is a single-shot per-process
///     operation (see <see cref="Profiling" />'s single-run contract); concurrent
///     profiling would need an instance threaded through the (protected) <c>Parse</c> signature, which is
///     out of scope.
/// </summary>
internal static class ParseProfiler
{
    // All three passes are timed by brackets placed OUTSIDE the (unmodified) loop bodies, so the parallel
    // pass-2 loop is never restructured. Passes 1 and 3 run sequentially on the parse thread, so their
    // alloc is the exact GetAllocatedBytesForCurrentThread delta. Pass 2 is parallel: there is no correct
    // outside-loop alloc figure (calling-thread under-counts the workers — the F-7 bug; process-wide
    // over-counts when e.g. a UI thread allocates concurrently), so pass-2 records WALL-CLOCK ONLY. For a
    // decompress-vs-proto-parse breakdown, take a one-off dotnet-trace CPU sample (Snappy.DecompressToArray
    // vs ParseFrame in the flamegraph). The accumulator fields exist unconditionally now (cheap statics);
    // the per-pass call-sites are guarded at runtime so a default parse touches none of them.
    private static long _pass1HeaderTicks, _pass1Alloc;
    private static long _pass2WallTicks;
    private static long _pass3EnrichTicks, _pass3Alloc;
    private static int _compressedFrames, _frameCount;

    // Whether THIS parse was profiled — captured from Profiling.Enabled at parse start (via Reset) and
    // reported as ParseProfilingSnapshot.Enabled. Decoupled from the LIVE flag so a Read() after the flag
    // is toggled (either direction) reflects what was actually captured, not the momentary flag state.
    private static bool _captured;

    /// <summary>
    ///     Zeroes every accumulator and records whether this parse is being profiled. Called at the start of
    ///     every parse (profiled or not) so a re-used process starts clean and the snapshot's
    ///     <c>Enabled</c> reflects the parse that produced the data.
    /// </summary>
    public static void Reset(bool captured)
    {
        _pass1HeaderTicks = _pass1Alloc = 0;
        _pass2WallTicks = 0;
        _pass3EnrichTicks = _pass3Alloc = 0;
        _compressedFrames = _frameCount = 0;
        _captured = captured;
    }

    /// <summary>Folds the sequential pass-1 header-scan ticks + (current-thread) allocation.</summary>
    public static void AddPass1(long ticks, long alloc)
    {
        _pass1HeaderTicks += ticks;
        _pass1Alloc += alloc;
    }

    /// <summary>Records the wall-clock of the whole pass-2 parallel decode loop (the parallel-efficiency denominator).</summary>
    public static void SetPass2Ticks(long ticks) => _pass2WallTicks = ticks;

    /// <summary>Folds the sequential pass-3 enrich ticks + (current-thread) allocation.</summary>
    public static void AddPass3(long ticks, long alloc)
    {
        _pass3EnrichTicks += ticks;
        _pass3Alloc += alloc;
    }

    /// <summary>Records the total and compressed frame counts.</summary>
    public static void SetCounts(int frames, int compressed)
    {
        _frameCount = frames;
        _compressedFrames = compressed;
    }

    /// <summary>
    ///     Materializes the current accumulators as an immutable snapshot. <c>Enabled</c> is the captured
    ///     flag from the last <see cref="Reset" /> — true only if the last parse was actually profiled.
    /// </summary>
    public static ParseProfilingSnapshot Snapshot() => new(
        _captured,
        _pass1HeaderTicks, _pass1Alloc,
        _pass2WallTicks,
        _pass3EnrichTicks, _pass3Alloc,
        _frameCount, _compressedFrames);
}

/// <summary>
///     Immutable snapshot of the parse-pipeline profiling accumulators, read once after a parse via
///     <see cref="Read" />. Tick fields are raw <c>Stopwatch</c> timestamps (convert with
///     <c>Stopwatch.GetElapsedTime</c> / <c>Stopwatch.Frequency</c>). The accumulators are populated at
///     runtime only when <see cref="Profiling.Enabled" /> was on at parse start; otherwise <see cref="Read" />
///     returns a snapshot whose <see cref="Enabled" /> is <c>false</c> and whose fields are zero — that is
///     the signal to callers that no profiling data was captured for the last parse.
///     <para>
///         <see cref="Pass2WallTicks" /> is wall-clock of the parallel decode; its workers' allocation is
///         deliberately NOT attributed here (no correct outside-loop figure — see <c>ParseProfiler</c>).
///         Passes 1 and 3 are sequential, so their <c>…Alloc</c> figures are exact.
///     </para>
/// </summary>
public readonly record struct ParseProfilingSnapshot(
    bool Enabled,
    long Pass1HeaderTicks,
    long Pass1Alloc,
    long Pass2WallTicks,
    long Pass3EnrichTicks,
    long Pass3Alloc,
    int FrameCount,
    int CompressedFrames)
{
    /// <summary>
    ///     Reads the parse-profiling accumulators captured by the last parse. <c>Enabled</c> is
    ///     <c>false</c> (and the fields zero) when the last parse ran with profiling off — see
    ///     <see cref="Profiling.Enabled" />.
    /// </summary>
    public static ParseProfilingSnapshot Read() => ParseProfiler.Snapshot();
}
