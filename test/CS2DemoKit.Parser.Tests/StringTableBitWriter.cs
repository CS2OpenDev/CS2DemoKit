namespace CS2DemoKit.Parser.Tests;

/// <summary>
///     The one bit writer the string-table tests build fixtures with, LSB-first to match
///     <see cref="BitBuffer" />'s read order.
///     <para>
///         <b>Shared on purpose.</b> This type exists so the wire encodings are written down in
///         exactly ONE place. Each string-table test file used to carry its own private copy, and
///         every copy encoded a variable user-data length as a protobuf varint — the same mistake
///         the decoder was making — so the fixtures agreed with the bug and the suite stayed green
///         while no real demo could get through <c>userinfo</c> at all. Keep new fixtures on this
///         writer rather than growing a fourth private one.
///     </para>
/// </summary>
internal sealed class StringTableBitWriter
{
    private readonly List<byte> _bytes = [];
    private int _bitPos; // 0-7 within the current (last) byte

    /// <summary>Appends a single bit.</summary>
    public StringTableBitWriter One(bool value = true)
    {
        if (_bitPos == 0)
        {
            _bytes.Add(0);
        }

        if (value)
        {
            _bytes[^1] |= (byte)(1 << _bitPos);
        }

        _bitPos = (_bitPos + 1) % 8;
        return this;
    }

    /// <summary>Appends a single zero bit.</summary>
    public StringTableBitWriter Zero() => One(false);

    /// <summary>Writes <paramref name="count" /> low bits of <paramref name="value" />, LSB first.</summary>
    public StringTableBitWriter Raw(uint value, int count)
    {
        for (int i = 0; i < count; i++)
        {
            One((value & (1u << i)) != 0);
        }

        return this;
    }

    /// <summary>
    ///     Writes a protobuf-style unsigned LEB128 varint (8 bits at a time, byte-aligned reads
    ///     notwithstanding). Under <c>using_varint_bitcounts</c> this is the encoding of a
    ///     non-sequential entry INDEX — and of nothing else in this format.
    /// </summary>
    public StringTableBitWriter VarInt(uint value)
    {
        while (value >= 0x80)
        {
            Raw((value & 0x7F) | 0x80, 8);
            value >>= 7;
        }

        return Raw(value, 8);
    }

    /// <summary>
    ///     Writes Source's <c>UBitVar</c>: a 6-bit seed whose top two bits select how many further
    ///     bits carry the rest of the value. Under <c>using_varint_bitcounts</c> this is the encoding
    ///     of a variable user-data LENGTH.
    ///     <para>
    ///         Deliberately NOT the same as <see cref="VarInt" />, despite one flag switching both
    ///         fields: the index is a protobuf varint and the length is a UBitVar. See
    ///         <c>StringTableWireFormatTests</c>, which pins both directions so neither can be
    ///         "unified" with the other.
    ///     </para>
    /// </summary>
    public StringTableBitWriter UBitVar(uint value) => value switch
    {
        < 1u << 4 => Raw(value, 6),
        < 1u << 8 => Raw((value & 15) | 0x10, 6).Raw(value >> 4, 4),
        < 1u << 12 => Raw((value & 15) | 0x20, 6).Raw(value >> 4, 8),
        _ => Raw((value & 15) | 0x30, 6).Raw(value >> 4, 28)
    };

    /// <summary>Returns the buffer, zero-padded to the next byte boundary.</summary>
    public byte[] ToArray() => _bytes.ToArray();
}
