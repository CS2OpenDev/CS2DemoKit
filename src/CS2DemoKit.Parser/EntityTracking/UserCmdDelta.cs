#region

using System.Buffers;
using System.Collections;
using Google.Protobuf;
using Google.Protobuf.Reflection;

#endregion

namespace CS2DemoKit.Parser.EntityTracking;

/// <summary>
///     Applies one <c>CMsgServerUserCmd.delta_data</c> blob to a user command in place.
///     <para>
///         The blob is not standard protobuf. It is the output of Valve's <c>codegen_delta_encoder</c>:
///         ordinary tags and values, plus two constructs a stock <c>MergeFrom</c> cannot read.
///     </para>
///     <list type="bullet">
///         <item>
///             Wire type 7 resets the field to its default. On a repeated field it clears the whole
///             list. Google.Protobuf's reader rejects wire type 7, so tags are read here by hand.
///         </item>
///         <item>
///             A repeated message field carries a list of operations addressed by index rather than
///             appended elements. Each key is a raw varint, <c>index &lt;&lt; 3 | wire</c>, so index 0
///             legitimately looks like field number 0. Wire 7 resizes the list to <c>index</c> entries
///             (growing with default elements or truncating), and wire 2 merges a nested delta into the
///             element at <c>index</c>.
///         </item>
///     </list>
///     <para>
///         Scalars, strings and bytes in one message are collected and merged in a single standard
///         <c>MergeFrom</c> pass, and nested messages recurse. Reflection keeps this working when the
///         packaged protos gain fields.
///     </para>
///     <para>
///         Unknown fields (numbers missing from the packaged descriptor) are skipped for every wire
///         type and counted, where demoinfocs rejects a reset of one. A field Valve adds before the
///         protos catch up would otherwise fail every command that touches it.
///     </para>
///     <para>
///         Adapted from markus-wa/demoinfocs-golang, <c>pkg/demoinfocs/s2_usercmd_delta.go</c>
///         (<c>mergeUserCmdDelta</c> / <c>mergeUserCmdRepeated</c>), MIT licensed. See
///         THIRD-PARTY-NOTICES.md.
///     </para>
/// </summary>
internal static class UserCmdDelta
{
    /// <summary>The <c>codegen_delta_encoder</c> reset marker. Invalid in standard protobuf.</summary>
    internal const int ResetWireType = 7;

    /// <summary>Largest list length a repeated resize may ask for (client.dll's cap).</summary>
    internal const int RepeatedLimit = 0x100;

    private const int VarintWireType = 0;
    private const int Fixed64WireType = 1;
    private const int LengthDelimitedWireType = 2;
    private const int Fixed32WireType = 5;

    /// <summary>
    ///     Merges <paramref name="delta" /> into <paramref name="target" />. On an exception the target
    ///     may be partly updated, so callers merge into a copy of their baseline.
    /// </summary>
    /// <exception cref="InvalidDataException">The blob is malformed.</exception>
    internal static void Merge(IMessage target, ReadOnlySpan<byte> delta)
    {
        long unknown = 0;
        Merge(target, delta, ref unknown);
    }

    /// <inheritdoc cref="Merge(IMessage, ReadOnlySpan{byte})" />
    /// <param name="target">The message to update in place.</param>
    /// <param name="delta">The <c>delta_data</c> bytes, or a nested message's slice of them.</param>
    /// <param name="unknownFieldsSkipped">Incremented once per field number the descriptor does not know.</param>
    internal static void Merge(IMessage target, ReadOnlySpan<byte> delta, ref long unknownFieldsSkipped)
    {
        MessageDescriptor descriptor = target.Descriptor;
        byte[]? scalars = null;
        int scalarLength = 0;

        try
        {
            int p = 0;
            while (p < delta.Length)
            {
                int start = p;
                ulong tag = ReadVarint(delta, ref p);
                ulong number = tag >> 3;
                int wire = (int)(tag & 7);
                if (number == 0 || number > int.MaxValue)
                {
                    throw new InvalidDataException($"delta field number {number} is out of range");
                }

                FieldDescriptor? field = descriptor.FindFieldByNumber((int)number);
                if (field is null)
                {
                    unknownFieldsSkipped++;
                }

                switch (wire)
                {
                    case ResetWireType:
                        field?.Accessor.Clear(target);
                        break;

                    case LengthDelimitedWireType:
                    {
                        ReadOnlySpan<byte> value = ReadLengthDelimited(delta, ref p);
                        if (field is null)
                        {
                            break;
                        }

                        if (field.FieldType is FieldType.Message or FieldType.Group)
                        {
                            if (field.IsRepeated)
                            {
                                MergeRepeated(target, field, value, ref unknownFieldsSkipped);
                            }
                            else
                            {
                                IMessage? sub = (IMessage?)field.Accessor.GetValue(target);
                                if (sub is null)
                                {
                                    sub = NewMessage(field.MessageType);
                                    field.Accessor.SetValue(target, sub);
                                }

                                Merge(sub, value, ref unknownFieldsSkipped);
                            }
                        }
                        else
                        {
                            // Strings, bytes and packed scalars are ordinary protobuf.
                            Append(ref scalars, ref scalarLength, delta[start..p]);
                        }

                        break;
                    }

                    case VarintWireType:
                    case Fixed64WireType:
                    case Fixed32WireType:
                        SkipValue(delta, ref p, wire);
                        // A repeated scalar sent unpacked would append rather than replace, and the
                        // encoder never sends one, so it is dropped as demoinfocs drops it.
                        if (field is { IsRepeated: false })
                        {
                            Append(ref scalars, ref scalarLength, delta[start..p]);
                        }

                        break;

                    default:
                        throw new InvalidDataException($"unsupported delta wire type {wire} on field {number}");
                }
            }

            if (scalarLength > 0)
            {
                try
                {
                    target.MergeFrom(new ReadOnlySpan<byte>(scalars, 0, scalarLength));
                }
                catch (InvalidProtocolBufferException ex)
                {
                    throw new InvalidDataException($"scalar delta fields of {descriptor.Name} did not parse", ex);
                }
            }
        }
        finally
        {
            if (scalars is not null)
            {
                ArrayPool<byte>.Shared.Return(scalars);
            }
        }
    }

    /// <summary>Applies the indexed operations of one repeated message field.</summary>
    private static void MergeRepeated(IMessage parent, FieldDescriptor field, ReadOnlySpan<byte> ops,
        ref long unknownFieldsSkipped)
    {
        IList list = (IList)field.Accessor.GetValue(parent);
        int p = 0;
        while (p < ops.Length)
        {
            ulong key = ReadVarint(ops, ref p);
            ulong index = key >> 3;
            int wire = (int)(key & 7);

            switch (wire)
            {
                case ResetWireType:
                {
                    if (index > RepeatedLimit)
                    {
                        throw new InvalidDataException(
                            $"{field.Name} resize to {index} exceeds the limit of {RepeatedLimit}");
                    }

                    int length = (int)index;
                    while (list.Count < length)
                    {
                        list.Add(NewMessage(field.MessageType));
                    }

                    while (list.Count > length)
                    {
                        list.RemoveAt(list.Count - 1);
                    }

                    break;
                }

                case LengthDelimitedWireType:
                {
                    ReadOnlySpan<byte> value = ReadLengthDelimited(ops, ref p);
                    if (index >= (ulong)list.Count)
                    {
                        throw new InvalidDataException(
                            $"{field.Name}[{index}] is out of bounds for a list of {list.Count}");
                    }

                    Merge((IMessage)list[(int)index]!, value, ref unknownFieldsSkipped);
                    break;
                }

                default:
                    throw new InvalidDataException($"unsupported repeated delta wire type {wire} on {field.Name}");
            }
        }
    }

    private static IMessage NewMessage(MessageDescriptor type) => type.Parser.ParseFrom(ByteString.Empty);

    private static ulong ReadVarint(ReadOnlySpan<byte> data, ref int p)
    {
        ulong result = 0;
        for (int shift = 0; shift < 64; shift += 7)
        {
            if (p >= data.Length)
            {
                throw new InvalidDataException("truncated varint in user-command delta");
            }

            byte b = data[p++];
            result |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                return result;
            }
        }

        throw new InvalidDataException("varint longer than 10 bytes in user-command delta");
    }

    private static ReadOnlySpan<byte> ReadLengthDelimited(ReadOnlySpan<byte> data, ref int p)
    {
        ulong length = ReadVarint(data, ref p);
        if (length > (ulong)(data.Length - p))
        {
            throw new InvalidDataException(
                $"length {length} runs past the end of the user-command delta ({data.Length - p} bytes left)");
        }

        ReadOnlySpan<byte> value = data.Slice(p, (int)length);
        p += (int)length;
        return value;
    }

    private static void SkipValue(ReadOnlySpan<byte> data, ref int p, int wire)
    {
        int size = wire switch
        {
            Fixed64WireType => 8,
            Fixed32WireType => 4,
            _ => 0
        };

        if (size == 0)
        {
            ReadVarint(data, ref p);
            return;
        }

        if (size > data.Length - p)
        {
            throw new InvalidDataException("truncated fixed-width value in user-command delta");
        }

        p += size;
    }

    private static void Append(ref byte[]? buffer, ref int length, ReadOnlySpan<byte> bytes)
    {
        if (buffer is null)
        {
            buffer = ArrayPool<byte>.Shared.Rent(Math.Max(64, bytes.Length));
        }
        else if (length + bytes.Length > buffer.Length)
        {
            byte[] grown = ArrayPool<byte>.Shared.Rent(Math.Max(buffer.Length * 2, length + bytes.Length));
            buffer.AsSpan(0, length).CopyTo(grown);
            ArrayPool<byte>.Shared.Return(buffer);
            buffer = grown;
        }

        bytes.CopyTo(buffer.AsSpan(length));
        length += bytes.Length;
    }
}
