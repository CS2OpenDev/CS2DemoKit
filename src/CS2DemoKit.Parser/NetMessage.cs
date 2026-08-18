#region

using System.Diagnostics.CodeAnalysis;
using Google.Protobuf;

#endregion

namespace CS2DemoKit.Parser;

/// <summary>
///     One message within a <see cref="DemoFrame" />.
///     Not sealed — <see cref="CS2DemoKit.Parser.GameEvents.GameEventMessage" /> extends this
///     to carry the decoded <see cref="CS2DemoKit.Parser.GameEvents.GameEvent" />.
/// </summary>
public class NetMessage
{
    /// <summary>
    ///     Protected constructor for derived types (e.g. <c>GameEventMessage</c>).
    ///     Satisfies all required members so subclasses can call base(...) without object-init syntax.
    /// </summary>
    [SetsRequiredMembers]
    protected NetMessage(string messageTypeName, IMessage? payload,
        int? decompressedStart = null, int? decompressedLength = null)
    {
        MessageTypeName = messageTypeName;
        Payload = payload!;
        DecompressedStart = decompressedStart;
        DecompressedLength = decompressedLength;
    }

    /// <summary>
    ///     Parameterless constructor retained for object-initializer syntax used in the parser.
    /// </summary>
    public NetMessage()
    {
    }

    /// <summary>
    ///     Byte length of this message's payload within the decompressed frame payload.
    /// </summary>
    public int? DecompressedLength { get; init; }

    /// <summary>
    ///     Byte-approximate offset of this message's payload within the <em>decompressed</em> frame payload.
    ///     <list type="bullet">
    ///         <item>Direct-payload frames: 0 (the message IS the full decompressed frame payload).</item>
    ///         <item>
    ///             DEM_Packet / DEM_FullPacket inner messages: byte-aligned start within the
    ///             CDemoPacket.data bitstream, shifted by the data field's position in the frame proto.
    ///             Note: the inner bitstream is bit-interleaved, so this is approximate (±1 byte).
    ///         </item>
    ///         <item>DEM_FullPacket string-table entry: position of CDemoStringTables field in frame proto.</item>
    ///         <item>Null only when the position could not be determined (e.g. parse failure).</item>
    ///     </list>
    ///     Use together with <see cref="DecompressedLength" /> to highlight the byte range in a
    ///     drilldown hex view of the decompressed frame bytes.
    /// </summary>
    public int? DecompressedStart { get; init; }

    /// <summary>
    ///     The proto-style name, e.g. "svc_PacketEntities" or "DEM_FileHeader".
    ///     Uses the <c>OriginalNameAttribute</c> value (lowercase snake_case for net messages)
    ///     rather than <c>Descriptor.Name</c> because the UI accent-brush logic matches on the
    ///     lowercase proto prefix (svc_, net_, cs_, …).
    /// </summary>
    public required string MessageTypeName { get; init; }

    /// <summary>
    ///     The deserialized protobuf message payload.
    ///     Typed as non-generic <see cref="Google.Protobuf.IMessage" /> because the list holds
    ///     heterogeneous message types. Use <c>Payload.Descriptor.Name</c> to recover the type
    ///     name when needed.
    /// </summary>
    public required IMessage Payload { get; init; }

    /// <inheritdoc />
    public override string ToString() => MessageTypeName;
}
