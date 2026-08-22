#region

using System.Buffers.Binary;

#endregion

namespace CS2DemoKit.Parser;

/// <summary>
///     Storage for <c>svc_UserCmds</c> payloads. One partition of Pass 2 writes into one writer;
///     the blocks it allocates are kept alive by the <see cref="DemoFrame" />s that point into them.
///     <para>
///         <b>Why this exists.</b> svc_UserCmds is ~90% of the net messages in a demo (1.15M on a
///         290 MB file) and is read by almost nothing. A payload-per-message representation costs
///         one live object per message, and GC pause scales with the number of surviving objects,
///         not their bytes. Here the same bytes live in a few hundred large arrays instead.
///     </para>
///     <para>
///         <b>Shape.</b> Bump allocation into growable blocks, the arena/region pattern: a payload is
///         appended at a cursor and never freed on its own. It differs from a textbook arena in that
///         blocks are released piecemeal, each held by the frames pointing into it, rather than as one
///         region. It is not slab allocation: records are variable length, not fixed-size slots.
///     </para>
/// </summary>
internal sealed class UserCmdsWriter
{
    /// <summary>
    ///     Block size. Must stay above the 85,000-byte large-object threshold: an LOH array is never
    ///     copied during collection, which is the whole point. Page-size alignment buys nothing (the
    ///     array header pushes a page-multiple payload onto an extra page anyway); the real trade is
    ///     LOH residency against per-writer tail waste, which is bounded by one block per partition.
    /// </summary>
    private const int BlockBytes = 1 << 20;

    /// <summary>
    ///     Headroom a fresh frame wants before reusing the current block. Contiguity is enforced by
    ///     <see cref="Relocate" />, not by this: the constant only keeps relocation rare, since a
    ///     frame's payloads run far below it.
    /// </summary>
    private const int FrameHeadroom = 64 * 1024;

    private int _frameCount;
    private int _frameStart;
    private int _offset;
    private byte[]? _block;

    /// <summary>Opens a frame's run. Every <see cref="Append" /> lands in one block until <see cref="EndFrame" />.</summary>
    public void BeginFrame()
    {
        if (_block is null || _block.Length - _offset < FrameHeadroom)
        {
            _block = new byte[BlockBytes];
            _offset = 0;
        }

        _frameStart = _offset;
        _frameCount = 0;
    }

    /// <summary>
    ///     Appends one payload as a 4-byte little-endian length followed by the bytes, and reports
    ///     where the bytes landed so a caller can point at them without copying.
    /// </summary>
    public (byte[] Block, int Offset) Append(ReadOnlySpan<byte> payload, int ordinal)
    {
        int need = sizeof(int) + sizeof(int) + payload.Length;
        if (_block!.Length - _offset < need)
        {
            Relocate(need);
        }

        // Record: [ordinal][length][bytes]. The ordinal is this payload's position among the
        // frame's messages, so a composed view can put it back in wire order.
        BinaryPrimitives.WriteInt32LittleEndian(_block.AsSpan(_offset), ordinal);
        BinaryPrimitives.WriteInt32LittleEndian(_block.AsSpan(_offset + sizeof(int)), payload.Length);
        payload.CopyTo(_block.AsSpan(_offset + (2 * sizeof(int))));
        int payloadAt = _offset + (2 * sizeof(int));
        _offset += need;
        _frameCount++;
        return (_block, payloadAt);
    }

    /// <summary>Closes the frame's run. Returns nulls when the frame carried no subtick input.</summary>
    public (byte[]? Block, int Offset, int Count) EndFrame() =>
        _frameCount == 0 ? (null, 0, 0) : (_block, _frameStart, _frameCount);

    /// <summary>
    ///     Moves the open frame's run into a block large enough to finish it. Only reachable when one
    ///     frame's payloads exceed <see cref="FrameHeadroom" />. Blocks already handed to earlier
    ///     frames are untouched: those frames hold the reference and keep the old array alive.
    /// </summary>
    private void Relocate(int need)
    {
        int written = _offset - _frameStart;
        byte[] bigger = new byte[Math.Max(BlockBytes, written + need)];
        if (written > 0)
        {
            _block.AsSpan(_frameStart, written).CopyTo(bigger);
        }

        _block = bigger;
        _frameStart = 0;
        _offset = written;
    }
}

/// <summary>Reads back what <see cref="UserCmdsWriter" /> laid down.</summary>
internal static class UserCmdsStore
{
    /// <summary>
    ///     Reads the payload at <paramref name="offset" /> and advances it past the record. The
    ///     returned span points into the block; it is valid as long as the frame is reachable.
    /// </summary>
    public static ReadOnlySpan<byte> Read(byte[] block, scoped ref int offset)
    {
        offset += sizeof(int); // ordinal
        int length = BinaryPrimitives.ReadInt32LittleEndian(block.AsSpan(offset));
        offset += sizeof(int);
        ReadOnlySpan<byte> payload = block.AsSpan(offset, length);
        offset += length;
        return payload;
    }

    /// <summary>Reads one record's ordinal, length and payload start, advancing past the record.</summary>
    public static (int Ordinal, int PayloadAt, int Length) ReadHeader(byte[] block, scoped ref int offset)
    {
        int ordinal = BinaryPrimitives.ReadInt32LittleEndian(block.AsSpan(offset));
        int length = BinaryPrimitives.ReadInt32LittleEndian(block.AsSpan(offset + sizeof(int)));
        int at = offset + (2 * sizeof(int));
        offset = at + length;
        return (ordinal, at, length);
    }
}
