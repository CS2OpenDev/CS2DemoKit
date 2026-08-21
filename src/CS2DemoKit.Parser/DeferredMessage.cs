#region

using Google.Protobuf;
using Google.Protobuf.Reflection;

#endregion

namespace CS2DemoKit.Parser;

/// <summary>
///     A net-message payload kept in its raw wire form and not yet expanded into its protobuf object
///     graph. It IS a valid on-the-wire message; it simply defers the (sometimes very expensive)
///     deserialization until a consumer actually needs the typed view.
///     <para>
///         <b>No longer produced by the parser.</b> This existed for <c>svc_UserCmds</c>, and it only
///         solved half that problem: deferring the decode removed the protobuf object graph but still
///         cost a private byte copy and two live objects per message, and at ~90% of a demo's net
///         messages that object count was what made parse GC-bound. Those payloads now go to the
///         subtick arena instead (<c>SubtickWriter</c>), which holds the same bytes in a few hundred
///         large slabs and allocates nothing per message.
///     </para>
///     <para>
///         Kept because it is public API and still works: a hand-built frame may carry one, and
///         <see cref="Models.SubTickExtractor" /> still reads it. Nothing in the parse path creates one.
///     </para>
///     <para>
///         <b>Why implementing <see cref="IMessage" /> is the right layer</b> (rather than changing the
///         <see cref="NetMessage" /> contract). The four <see cref="IMessage" /> members all delegate to
///         <see cref="Materialize" />, so the payload behaves exactly like the real message the instant
///         anyone invokes one. The win does not come from faking those members; it comes from the fact
///         that <b>the hot path never invokes them</b>. Entity tracking and the analysis engine only ask
///         <c>Payload is CConcrete</c>, a pure type-identity test that calls no interface member and
///         correctly evaluates <c>false</c> for a deferred payload (those layers do not read subtick data
///         anyway). Only the two real consumers, plus per-message display of a selected frame, ever touch
///         a member and thereby materialize: exactly when the typed view is genuinely needed. This keeps
///         the load-bearing <see cref="NetMessage" /> / <see cref="DemoFrame" /> contract untouched and
///         its ~20 consumers working unchanged.
///     </para>
///     <para>
///         <b>The one seam.</b> <c>Payload is CSVCMsg_UserCommands</c> is <c>false</c> until
///         <see cref="Materialize" /> runs. Consumers that genuinely need the typed message call
///         <see cref="TryMaterialize{T}" /> (or <see cref="Materialize" />); everything else is
///         intentionally blind to it. Materialization is cached and idempotent.
///     </para>
/// </summary>
public sealed class DeferredMessage : IMessage
{
    private readonly byte[] _bytes;
    private readonly MessageParser _parser;
    private IMessage? _materialized;

    private DeferredMessage(MessageParser parser, byte[] bytes)
    {
        _parser = parser;
        _bytes = bytes;
    }

    /// <inheritdoc />
    /// <remarks>Materializes: reading the descriptor requires the real message. Off the hot path.</remarks>
    public MessageDescriptor Descriptor => Materialize().Descriptor;

    /// <summary>
    ///     Creates a deferred payload over <paramref name="data" />, taking a private copy of the bytes
    ///     so it is independent of the caller's (typically pooled) buffer.
    /// </summary>
    /// <param name="parser">The parser that will materialize the real message on demand.</param>
    /// <param name="data">The raw wire bytes of this single message.</param>
    public static DeferredMessage Defer(MessageParser parser, ReadOnlyMemory<byte> data) =>
        new(parser, data.ToArray());

    /// <summary>Deserializes (once, cached) and returns the real protobuf message. Idempotent.</summary>
    public IMessage Materialize() => _materialized ??= _parser.ParseFrom(_bytes);

    /// <summary>
    ///     Materializes and returns the payload as <typeparamref name="T" />, or <c>null</c> if it is
    ///     not that type. The typed convenience for the two real consumers.
    /// </summary>
    public T? TryMaterialize<T>() where T : class, IMessage => Materialize() as T;

    /// <inheritdoc />
    public void WriteTo(CodedOutputStream output) => Materialize().WriteTo(output);

    /// <inheritdoc />
    public int CalculateSize() => Materialize().CalculateSize();

    /// <inheritdoc />
    public void MergeFrom(CodedInputStream input) => Materialize().MergeFrom(input);
}
