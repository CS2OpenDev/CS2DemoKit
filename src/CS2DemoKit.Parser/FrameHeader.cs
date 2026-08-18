namespace CS2DemoKit.Parser;

/// <summary>
///     The decoded header fields that precede every frame payload in a CS2 demo file.
/// </summary>
/// <remarks>
///     The raw wire format encodes three consecutive ULEB128 uint32 varints:
///     <c>rawCommand</c> (EDemoCommands | optional compressed flag), <c>tick</c>, <c>size</c>.
///     <see cref="Leb128Utils.ParseFrameHeader" /> splits the compressed flag out of the command
///     varint at decode time so callers always receive the clean <c>EDemoCommands</c> value
///     and a separate <see cref="IsCompressed" /> bool — no masking required downstream.
///     <para>
///         Maximum encoded header size is 15 bytes (3 × 5-byte ULEB128 ceiling).
///         Typical mid-match frames consume 4–7 bytes.
///     </para>
/// </remarks>
public readonly struct FrameHeader
{
    // DEM_IsCompressed is bit 6 (0x40) of the raw command varint.
    // Defined here rather than referencing EDemoCommands to keep LEB128Utils free of
    // generated-proto dependencies.
    private const uint CompressedFlag = 0x40u;

    /// <summary>
    ///     The <c>EDemoCommands</c> value with the compressed flag already stripped.
    ///     Cast directly to <c>EDemoCommands</c> without additional masking.
    /// </summary>
    public uint Command { get; }

    /// <summary>
    ///     Server tick at which this frame was recorded.
    ///     Pre-recording frames (e.g. <c>DEM_FileHeader</c>, <c>DEM_SyncTick</c>) use
    ///     <c>-1</c>, which the wire format encodes as <c>0xFFFFFFFF</c>.
    /// </summary>
    public int Tick { get; }

    /// <summary>
    ///     Payload byte length as written in the file.
    ///     When <see cref="IsCompressed" /> is <see langword="true" /> this is the Snappy-compressed
    ///     size; the decompressed size is only known after inflating the payload.
    /// </summary>
    public uint Size { get; }

    /// <summary>
    ///     <see langword="true" /> when the payload is Snappy-compressed
    ///     (<c>DEM_IsCompressed</c> flag was set in the raw command varint).
    /// </summary>
    public bool IsCompressed { get; }

    /// <summary>
    ///     Only <see cref="Leb128Utils.ParseFrameHeader" /> should construct this.
    /// </summary>
    internal FrameHeader(uint rawCommand, uint tick, uint size)
    {
        IsCompressed = (rawCommand & CompressedFlag) != 0;
        Command = rawCommand & ~CompressedFlag; // strip flag so Command == clean EDemoCommands value
        Tick = (int)tick; // 0xFFFFFFFF on wire = -1 for pre-recording frames
        Size = size;
    }
}
