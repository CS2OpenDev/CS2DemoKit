#region

using System.Buffers.Binary;

#endregion

namespace CS2DemoKit.Parser;

/// <summary>
///     Arena for <c>svc_UserCmds</c> payloads. One partition of Pass 2 writes into one writer;
///     the slabs it allocates are kept alive by the <see cref="DemoFrame" />s that point into them.
///     <para>
///         <b>Why an arena.</b> Subtick input is ~90% of the net messages in a demo (2.3M on a
///         290 MB file) and is read by almost nothing. A payload-per-message representation costs
///         one live object per message, and GC pause scales with the number of surviving objects,
///         not their bytes. Here the same bytes live in a few hundred large arrays instead.
///     </para>
/// </summary>
internal sealed class SubtickWriter
{
    /// <summary>
    ///     Slab size. Must stay above the 85,000-byte large-object threshold: an LOH array is never
    ///     copied during collection, which is the whole point. Page-size alignment buys nothing (the
    ///     array header pushes a page-multiple payload onto an extra page anyway); the real trade is
    ///     LOH residency against per-writer tail waste, which is bounded by one slab per partition.
    /// </summary>
    private const int SlabBytes = 1 << 20;

    /// <summary>
    ///     Headroom a fresh frame wants before reusing the current slab. Contiguity is enforced by
    ///     <see cref="Relocate" />, not by this: the constant only keeps relocation rare, since a
    ///     frame's payloads run far below it.
    /// </summary>
    private const int FrameHeadroom = 64 * 1024;

    private int _frameCount;
    private int _frameStart;
    private int _offset;
    private byte[]? _slab;

    /// <summary>Opens a frame's run. Every <see cref="Append" /> lands in one slab until <see cref="EndFrame" />.</summary>
    public void BeginFrame()
    {
        if (_slab is null || _slab.Length - _offset < FrameHeadroom)
        {
            _slab = new byte[SlabBytes];
            _offset = 0;
        }

        _frameStart = _offset;
        _frameCount = 0;
    }

    /// <summary>Appends one payload as a 4-byte little-endian length followed by the bytes.</summary>
    public void Append(ReadOnlySpan<byte> payload)
    {
        int need = sizeof(int) + payload.Length;
        if (_slab!.Length - _offset < need)
        {
            Relocate(need);
        }

        BinaryPrimitives.WriteInt32LittleEndian(_slab.AsSpan(_offset), payload.Length);
        payload.CopyTo(_slab.AsSpan(_offset + sizeof(int)));
        _offset += need;
        _frameCount++;
    }

    /// <summary>Closes the frame's run. Returns nulls when the frame carried no subtick input.</summary>
    public (byte[]? Slab, int Offset, int Count) EndFrame() =>
        _frameCount == 0 ? (null, 0, 0) : (_slab, _frameStart, _frameCount);

    /// <summary>
    ///     Moves the open frame's run into a slab large enough to finish it. Only reachable when one
    ///     frame's payloads exceed <see cref="FrameHeadroom" />. Slabs already handed to earlier
    ///     frames are untouched: those frames hold the reference and keep the old array alive.
    /// </summary>
    private void Relocate(int need)
    {
        int written = _offset - _frameStart;
        byte[] bigger = new byte[Math.Max(SlabBytes, written + need)];
        if (written > 0)
        {
            _slab.AsSpan(_frameStart, written).CopyTo(bigger);
        }

        _slab = bigger;
        _frameStart = 0;
        _offset = written;
    }
}

/// <summary>Reads back what <see cref="SubtickWriter" /> laid down.</summary>
internal static class SubtickArena
{
    /// <summary>
    ///     Reads the payload at <paramref name="offset" /> and advances it past the record. The
    ///     returned span points into the slab; it is valid as long as the frame is reachable.
    /// </summary>
    public static ReadOnlySpan<byte> Read(byte[] slab, scoped ref int offset)
    {
        int length = BinaryPrimitives.ReadInt32LittleEndian(slab.AsSpan(offset));
        offset += sizeof(int);
        ReadOnlySpan<byte> payload = slab.AsSpan(offset, length);
        offset += length;
        return payload;
    }
}
