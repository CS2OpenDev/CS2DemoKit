#region

using System.Runtime.CompilerServices;

#endregion

namespace CS2DemoKit.Parser;

// ── LEB128Utils ───────────────────────────────────────────────────────────────
//
// ULEB128 (Unsigned Little-Endian Base 128) encoding used throughout CS2 demos:
//   • Each byte carries 7 data bits (bits 0–6).
//   • Bit 7 (0x80) is the continuation flag — set on every byte except the last.
//   • Bytes are ordered least-significant group first (little-endian bit groups).
//
//   Examples:
//     0x00             →          0
//     0x7F             →        127
//     0x80 0x01        →        128
//     0xFF 0x7F        →     16 383
//     0xE5 0x8E 0x26   →    624 485
//
//   uint32 fits in at most 5 bytes (ceil(32/7) = 5).
//   uint64 fits in at most 10 bytes (ceil(64/7) = 10).
//
// Active use cases in this codebase:
//   • Frame headers:     ParseFrameHeader — fully-unrolled 3-varint decode from span.
//   • Proto wire format: TryReadUInt32 / TryReadUInt64 / TrySkip — used by
//                        DemoParser.FindBytesField and DownstreamUtilities.Scan.

/// <summary>
///     Span-based synchronous ULEB128 decoding utilities.
///     All methods are allocation-free and operate directly on
///     <see cref="ReadOnlySpan{Byte}" /> or <see cref="byte" />[] data.
/// </summary>
/// <remarks>
///     Hot-path methods use <see cref="MethodImplOptions.AggressiveInlining" /> so
///     the single-byte fast-path (values 0–127, the most common case) compiles
///     down to a branch + two assignments at the call site.  The multibyte path
///     is kept in a <see cref="MethodImplOptions.NoInlining" /> helper so it doesn't
///     bloat every call site that takes the fast path.
/// </remarks>
public static class Leb128Utils
{
    // Maximum number of bytes a ULEB128-encoded integer can occupy.
    // Used as loop bounds to avoid reading past a malformed/truncated varint.
    private const int MaxBytesUInt32 = 5; // ceil(32 / 7)
    private const int MaxBytesUInt64 = 10; // ceil(64 / 7)

    // ── Frame header parsing ──────────────────────────────────────────────────
    //
    // Decodes the three-varint frame header (Command, Tick, Size) in one pass.
    // Each varint is fully unrolled (no loop) so the JIT sees every branch as a
    // simple compare + fall-through, making the 1–3-byte common cases near-zero cost.
    //
    // Hot/cold split on bounds checking:
    //   When ≥ MaxBytesUInt32 bytes remain before a varint, a single "room check"
    //   at the top of DecodeVarint proves all five subsequent byte reads are safe.
    //   The JIT then eliminates the per-read bounds checks, yielding fewer branches
    //   than a loop that checks the bound on every iteration.
    //   When fewer bytes remain (the tail), DecodeVarintSlow checks each byte.

    /// <summary>
    ///     Decodes a CS2 demo frame header — three consecutive ULEB128 uint32 varints
    ///     (Command, Tick, Size) — from the head of <paramref name="data" />.
    /// </summary>
    /// <param name="data">Source span; must begin at the first byte of the frame header.</param>
    /// <param name="header">The decoded header on success; <see langword="default" /> on failure.</param>
    /// <returns>
    ///     Number of bytes consumed (1–15) on success, or <c>-1</c> if <paramref name="data" />
    ///     is too short to contain a complete header.
    /// </returns>
    /// <remarks>
    ///     All three fields are decoded with a shared index and no intermediate span slicing,
    ///     avoiding slice-struct overhead at each field boundary.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ParseFrameHeader(ReadOnlySpan<byte> data, out FrameHeader header)
    {
        int offset = 0;

        if (!DecodeVarint(data, ref offset, out uint command) ||
            !DecodeVarint(data, ref offset, out uint tick) ||
            !DecodeVarint(data, ref offset, out uint size))
        {
            header = default;
            return -1;
        }

        header = new FrameHeader(command, tick, size);
        return offset; // total bytes consumed by all three varints
    }

    // ── uint32 ───────────────────────────────────────────────────────────────

    /// <summary>
    ///     Attempts to decode a ULEB128 uint32 from the head of <paramref name="span" />,
    ///     advancing the span past the consumed bytes on success.
    /// </summary>
    /// <param name="span">
    ///     Source bytes.  Advanced past the varint on success; unchanged on failure.
    /// </param>
    /// <param name="value">The decoded value, or 0 on failure.</param>
    /// <returns>
    ///     <see langword="true" /> if a complete, non-truncated varint was decoded.
    ///     <see langword="false" /> if the span is empty or ends mid-varint.
    /// </returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryReadUInt32(ref ReadOnlySpan<byte> span, out uint value)
    {
        // Fast path: single-byte varint (values 0–127).  The majority of proto
        // field tags and small integers hit this branch.
        if (span.Length > 0 && (span[0] & 0x80) == 0)
        {
            value = span[0];
            span = span[1..];
            return true;
        }

        // Delegate to the cold multibyte path so this method stays small enough
        // to inline efficiently.
        return TryReadUInt32Core(ref span, out value);
    }

    /// <summary>
    ///     Non-advancing overload: decodes from <paramref name="span" /> without
    ///     modifying it, reporting <paramref name="bytesConsumed" /> instead.
    ///     Useful when byte-offset accounting is needed alongside decoding
    ///     (e.g. computing field positions for hex-view annotation).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryReadUInt32(ReadOnlySpan<byte> span, out uint value, out int bytesConsumed)
    {
        ReadOnlySpan<byte> remaining = span;
        if (!TryReadUInt32(ref remaining, out value))
        {
            bytesConsumed = 0;
            return false;
        }

        bytesConsumed = span.Length - remaining.Length;
        return true;
    }

    // ── byte[] + ref int pos overloads (DownstreamUtilities / DemoParser compat) ─
    //
    // These mirror the TryReadUInt32 / TryReadUInt64 / TrySkip signatures above
    // but accept a byte[] array and a ref int position counter rather than an
    // advancing span.  Provided so callers that already carry an index can use
    // LEB128Utils without refactoring to spans.

    /// <summary>
    ///     Decodes a ULEB128 uint32 from <paramref name="data" /> starting at
    ///     <paramref name="pos" />, advancing <paramref name="pos" /> past the consumed bytes.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryReadUInt32(byte[] data, ref int pos, out uint value)
    {
        // Single-byte fast path.
        if ((uint)pos < (uint)data.Length && (data[pos] & 0x80) == 0)
        {
            value = data[pos++];
            return true;
        }

        return TryReadUInt32ArrayCore(data, ref pos, out value);
    }

    /// <summary>
    ///     Decodes a ULEB128 uint64 from <paramref name="data" /> starting at
    ///     <paramref name="pos" />, advancing <paramref name="pos" /> past the consumed bytes.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryReadUInt64(byte[] data, ref int pos, out ulong value)
    {
        if ((uint)pos < (uint)data.Length && (data[pos] & 0x80) == 0)
        {
            value = data[pos++];
            return true;
        }

        return TryReadUInt64ArrayCore(data, ref pos, out value);
    }

    /// <summary>
    ///     Skips the next ULEB128 varint in <paramref name="data" /> without decoding it,
    ///     advancing <paramref name="pos" /> past the consumed bytes.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TrySkip(byte[] data, ref int pos)
    {
        while ((uint)pos < (uint)data.Length)
        {
            if ((data[pos++] & 0x80) == 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    ///     Fully-unrolled ULEB128 uint32 decoder using a shared index.
    ///     Returns <see langword="false" /> when the span ends before the varint completes.
    /// </summary>
    /// <remarks>
    ///     Hot path: when ≥ <see cref="MaxBytesUInt32" /> bytes remain from <paramref name="offset" />,
    ///     a single bounds check before the first byte proves all five reads safe — the JIT
    ///     eliminates the redundant per-read checks.
    ///     Cold path: falls back to <see cref="DecodeVarintSlow" /> near the end of the span.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool DecodeVarint(ReadOnlySpan<byte> data, ref int offset, out uint value)
    {
        // Hot path: ≥5 bytes remain — one check unlocks all five reads.
        if (data.Length - offset >= MaxBytesUInt32)
        {
            uint b = data[offset++];
            if (b < 0x80)
            {
                value = b;
                return true;
            }

            uint result = b & 0x7F;
            b = data[offset++];
            result |= (b & 0x7F) << 7;
            if (b < 0x80)
            {
                value = result;
                return true;
            }

            b = data[offset++];
            result |= (b & 0x7F) << 14;
            if (b < 0x80)
            {
                value = result;
                return true;
            }

            b = data[offset++];
            result |= (b & 0x7F) << 21;
            if (b < 0x80)
            {
                value = result;
                return true;
            }

            // Fifth byte: contributes bits 28–31.  The continuation bit and any
            // higher bits are silently truncated — matches the protobuf uint32 spec.
            b = data[offset++];
            value = result | b << 28;
            return true;
        }

        // Cold path: near the end of the span — check each byte individually.
        return DecodeVarintSlow(data, ref offset, out value);
    }

    /// <summary>
    ///     Bounds-checked fallback for <see cref="DecodeVarint" /> when fewer than
    ///     <see cref="MaxBytesUInt32" /> bytes remain.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool DecodeVarintSlow(ReadOnlySpan<byte> data, ref int offset, out uint value)
    {
        uint result = 0;
        for (int shift = 0; shift < 32; shift += 7)
        {
            if ((uint)offset >= (uint)data.Length)
            {
                value = 0;
                return false;
            }

            uint b = data[offset++];
            result |= (b & 0x7F) << shift;
            if (b < 0x80)
            {
                value = result;
                return true;
            }
        }

        value = 0;
        return false; // over-long encoding (>5 bytes for uint32)
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool TryReadUInt32ArrayCore(byte[] data, ref int pos, out uint value)
    {
        value = 0;
        int shift = 0;
        int limit = Math.Min(data.Length, pos + MaxBytesUInt32);
        while (pos < limit)
        {
            byte b = data[pos++];
            value |= (uint)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                return true;
            }

            shift += 7;
        }

        value = 0;
        return false;
    }

    // ── Cold multibyte cores (NoInlining keeps hot call sites compact) ───────

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool TryReadUInt32Core(ref ReadOnlySpan<byte> span, out uint value)
    {
        value = 0;
        int shift = 0;
        // Cap at MaxBytesUInt32 to reject malformed over-long encodings.
        int limit = Math.Min(span.Length, MaxBytesUInt32);
        for (int i = 0; i < limit; i++)
        {
            byte b = span[i];
            value |= (uint)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                span = span[(i + 1)..];
                return true;
            }

            shift += 7;
        }

        value = 0;
        return false; // truncated or overflowed uint32
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool TryReadUInt64ArrayCore(byte[] data, ref int pos, out ulong value)
    {
        value = 0;
        int shift = 0;
        int limit = Math.Min(data.Length, pos + MaxBytesUInt64);
        while (pos < limit)
        {
            byte b = data[pos++];
            value |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                return true;
            }

            shift += 7;
        }

        value = 0;
        return false;
    }
}
