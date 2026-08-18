namespace CS2DemoKit.Parser;

/// <summary>
///     One top-level demo command (EDemoCommands entry) parsed from the .dem file.
/// </summary>
public sealed class DemoFrame
{
    /// <summary>
    ///     Name of the demo command, e.g. "DEM_Packet", "DEM_SyncTick", etc.
    /// </summary>
    public required string Command { get; init; }

    /// <summary>Zero-based sequential index of this frame in <see cref="ParsedDemo.Frames" />.</summary>
    public required int FrameNumber { get; init; }

    /// <summary>
    ///     Alias for <see cref="ServerTick" />. In CS2 demos the frame header tick IS the game tick,
    ///     so this always equals <see cref="ServerTick" />.
    /// </summary>
    public int? GameTick { get; internal set; }

    /// <summary>
    ///     Byte length of the three ULEB128-encoded header fields (cmd, tick, size) that precede the payload.
    ///     <c>RawStart + HeaderLength</c> is the first byte of the payload in the raw .dem file.
    /// </summary>
    public required int HeaderLength { get; init; }

    /// <summary>
    ///     The sub-components of this frame.
    ///     <list type="bullet">
    ///         <item>Empty frames (DEM_SyncTick, DEM_Stop) → empty list.</item>
    ///         <item>
    ///             Direct-payload frames (DEM_FileHeader, DEM_SendTables, …) → one entry whose
    ///             <see cref="NetMessage.MessageTypeName" /> matches <see cref="Command" />.
    ///         </item>
    ///         <item>DEM_Packet / DEM_SignonPacket → the multiplexed net messages.</item>
    ///         <item>
    ///             DEM_FullPacket → entry[0] is the CDemoStringTables snapshot,
    ///             followed by the net messages from the nested CDemoPacket.
    ///         </item>
    ///     </list>
    /// </summary>
    public IReadOnlyList<NetMessage> InnerMessages => MessageList;

    /// <summary>
    ///     True when the payload was Snappy-compressed in the file.
    ///     The compressed bytes occupy <see cref="PayloadLength" /> bytes starting at <see cref="PayloadStart" />.
    ///     Use <see cref="DownstreamUtilities.GetDecompressedPayload(DemoFrame,byte[])" /> (or its
    ///     <c>ReadOnlySpan&lt;byte&gt;</c> overload, for a memory-mapped source) to obtain the uncompressed content.
    /// </summary>
    public required bool IsCompressed { get; init; }

    /// <summary>
    ///     Internal mutable backing for <see cref="InnerMessages" />.
    ///     The parser's enrichment pass (pass 3) replaces individual slots in this list
    ///     (e.g. to promote a raw <c>CMsgSource1LegacyGameEvent</c> to a
    ///     <c>GameEventMessage</c>) without reallocating the list.
    ///     External callers use <see cref="InnerMessages" /> (read-only view).
    /// </summary>
    internal List<NetMessage> MessageList { get; init; } = [];

    /// <summary>Byte length of the (possibly compressed) payload within the raw .dem file.</summary>
    public int PayloadLength => RawLength - HeaderLength;

    /// <summary>Byte offset of the (possibly compressed) payload within the raw .dem file.</summary>
    public int PayloadStart => RawStart + HeaderLength;

    /// <summary>
    ///     Total byte length of this frame in the raw .dem file, including header varints and the
    ///     (possibly Snappy-compressed) payload.  <c>RawStart + RawLength</c> is the start of the next frame.
    /// </summary>
    public required int RawLength { get; init; }

    /// <summary>
    ///     Byte offset of this frame's first header byte (cmd varint) within the raw .dem file.
    ///     Slice <c>demoBytes[RawStart .. RawStart + RawLength]</c> to get the complete frame bytes
    ///     (header varints + payload) for hex display or on-demand decompression.
    /// </summary>
    public required int RawStart { get; init; }

    /// <summary>
    ///     Tick value from the demo frame header varint. In CS2 demos this is the game tick
    ///     (gameplay starts at 1), not the absolute server tick. Pre-game frames use a negative sentinel.
    /// </summary>
    public required int ServerTick { get; init; }

    public override string ToString() =>
        InnerMessages.Count > 0
            ? $"[{ServerTick}] {Command}  ({InnerMessages.Count} inner msgs)"
            : $"[{ServerTick}] {Command}";
}
