#region

using Google.Protobuf;
using Google.Protobuf.Reflection;

#endregion

namespace CS2DemoKit.Parser;

/// <summary>
///     A net-message payload that points at bytes already held in a <see cref="UserCmdsWriter" />
///     block, decoding only when a consumer asks for the typed view.
///     <para>
///         The distinction from a lazily-computed <c>Payload</c> property is what makes this usable:
///         <c>EntityTracker</c> and the parser's enrichment pass both do
///         <c>switch (msg.Payload)</c>, and a type test against a real object falls through without
///         decoding anything. A lazy property would be forced by those same switches, on every frame
///         of every replay.
///     </para>
///     <para>
///         Holds no bytes of its own. The block is shared with every other payload written by the
///         same partition and is kept alive by the frames pointing into it.
///     </para>
/// </summary>
internal sealed class BlockMessage : IMessage
{
    private readonly byte[] _block;
    private readonly int _length;
    private readonly int _offset;
    private readonly MessageParser _parser;
    private IMessage? _decoded;

    private BlockMessage(MessageParser parser, byte[] block, int offset, int length)
    {
        _parser = parser;
        _block = block;
        _offset = offset;
        _length = length;
    }

    /// <inheritdoc />
    public MessageDescriptor Descriptor => Decode().Descriptor;

    /// <summary>Wraps a range of <paramref name="block" /> without copying it.</summary>
    public static BlockMessage Over(MessageParser parser, byte[] block, int offset, int length) =>
        new(parser, block, offset, length);

    /// <summary>Decodes once and caches. Idempotent.</summary>
    public IMessage Decode() =>
        _decoded ??= _parser.ParseFrom(_block.AsSpan(_offset, _length));

    /// <summary>Decodes and returns the payload as <typeparamref name="T" />, or null if it is not that type.</summary>
    public T? TryDecode<T>() where T : class, IMessage => Decode() as T;

    /// <inheritdoc />
    public void WriteTo(CodedOutputStream output) => Decode().WriteTo(output);

    /// <inheritdoc />
    public int CalculateSize() => Decode().CalculateSize();

    /// <inheritdoc />
    public void MergeFrom(CodedInputStream input) => Decode().MergeFrom(input);
}
