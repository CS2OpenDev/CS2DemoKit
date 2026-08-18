namespace CS2DemoKit.Parser;

/// <summary>
///     Describes a single occurrence of a net-message whose type ID landed in
///     <c>ParseNetMessage</c>'s default arm — i.e. the parser has no decoder registered for it.
///     Raised via <see cref="DemoParser.OnUnknownMessageType" /> once per occurrence so downstream
///     tooling (the UI's Output panel + reverse-engineering card view) can locate the raw bytes.
/// </summary>
/// <param name="FrameNumber">
///     The frame's <see cref="DemoFrame.FrameNumber" /> — equal to its index in
///     <c>ParsedDemo.Frames</c>, so it is directly seekable.
/// </param>
/// <param name="TypeId">The net-message type ID (UBitVar from the CDemoPacket bitstream).</param>
/// <param name="TypeName">
///     Resolved proto name, or <c>"unknown(N)"</c> when the ID is absent from the name cache.
/// </param>
/// <param name="DecompressedStart">
///     Byte-approximate (±1 byte) start offset of this message's payload within the
///     <em>decompressed</em> frame payload. The inner bitstream is bit-interleaved, so this is
///     suitable for highlighting a region but NOT for slicing exact bytes — see
///     <c>DownstreamUtilities.ExtractInnerMessageSlices</c> for an exact byte recovery.
/// </param>
/// <param name="Length">The message payload byte length (the bitstream's size varint).</param>
public readonly record struct UnknownMessageInfo(
    int FrameNumber,
    int TypeId,
    string TypeName,
    int DecompressedStart,
    int Length);
