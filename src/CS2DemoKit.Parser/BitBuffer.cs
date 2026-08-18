#region

using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

#endregion

namespace CS2DemoKit.Parser;

// Copied from demofile-net (MIT): https://github.com/saul/demofile-net
// Changes: namespace; Read3BitNormal returns System.Numerics.Vector3 instead of SDK Vector;
//          added ReadBytes(int) overload that allocates a new array (used in ProcessSendTables).

/// <summary>
///     Provides a bit-level reader over a byte span, with methods for reading various data types used in Source Engine
///     demo files.
/// </summary>
public ref struct BitBuffer
{
    private static readonly uint[] _bitMask;

    private int _bitsAvail = 0;
    private uint _buf = 0;
    private readonly ReadOnlySpan<byte> _original;
    private ReadOnlySpan<byte> _spanPointer;

    static BitBuffer()
    {
        _bitMask = new uint[33];
        for (int i = 1; i < _bitMask.Length - 1; ++i)
        {
            _bitMask[i] = (1u << i) - 1;
        }

        _bitMask[^1] = uint.MaxValue;
    }

    /// <param name="spanPointer">Source byte span to read from; not copied — the reader holds a reference.</param>
    public BitBuffer(ReadOnlySpan<byte> spanPointer)
    {
        _original = spanPointer;
        _spanPointer = spanPointer;
        FetchNext();
    }

    /// <summary>
    ///     Returns an independent <see cref="BitBuffer" /> positioned at the current bit offset.
    ///     Useful for speculative reads that should not advance the caller's cursor.
    /// </summary>
    public BitBuffer Clone()
    {
        (int fromByte, int skipBits) = Math.DivRem(TellBits, 8);
        BitBuffer cloned = new(_original[fromByte..]);
        cloned.ReadUBits(skipBits);
        return cloned;
    }

    /// <summary>Total bits consumed from the source span since construction.</summary>
    public int TellBits { get; private set; } = 0;

    /// <summary>Bits still available to read (buffered + unread span).</summary>
    public int RemainingBits => _bitsAvail + _spanPointer.Length * 8;

    /// <summary>Bytes still available to read, including the partially-consumed buffered word.</summary>
    public int RemainingBytes => _spanPointer.Length + _bitsAvail / 8;

    private void FetchNext()
    {
        _bitsAvail = _spanPointer.Length >= 4 ? 32 : _spanPointer.Length * 8;
        UpdateBuffer();
    }

    /// <summary>Reads <paramref name="numBits" /> bits (≤ 32) and returns them as an unsigned integer, LSB-first.</summary>
    public uint ReadUBits(int numBits)
    {
        TellBits += numBits;

        if (_bitsAvail >= numBits)
        {
            uint ret = _buf & _bitMask[numBits];
            _bitsAvail -= numBits;
            if (_bitsAvail != 0)
            {
                _buf >>= numBits;
            }
            else
            {
                FetchNext();
            }

            return ret;
        }
        else
        {
            uint ret = _buf;
            numBits -= _bitsAvail;
            UpdateBuffer();
            ret |= (_buf & _bitMask[numBits]) << _bitsAvail;
            _bitsAvail = 32 - numBits;
            _buf >>= numBits;
            return ret;
        }
    }

    /// <summary>Reads 8 bits as a byte (LSB-first within the buffer).</summary>
    public byte ReadByte() => (byte)ReadUBits(8);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private unsafe void UpdateBuffer()
    {
        if (_spanPointer.Length < 4)
        {
            // .NET 8/PGO optimisation issue (https://github.com/dotnet/runtime/issues/95056)
            fixed (uint* bufPtr = &_buf)
            {
                byte* bufBytes = (byte*)bufPtr;
                for (int i = 0; i < 4; ++i)
                {
                    bufBytes[i] = i < _spanPointer.Length ? _spanPointer[i] : default;
                }
            }

            _spanPointer = default;
        }
        else
        {
            _buf = MemoryMarshal.Read<uint>(_spanPointer[..4]);
            _spanPointer = _spanPointer[4..];
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool ReadOneBit()
    {
        TellBits += 1;
        uint ret = _buf & 1;
        if (--_bitsAvail == 0)
        {
            FetchNext();
        }
        else
        {
            _buf >>= 1;
        }

        return ret != 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public float ReadFloat()
    {
        uint bits = ReadUBits(32);
        unsafe
        {
            return *(float*)&bits;
        }
    }

    /// <summary>
    ///     Reads Source's variable-width unsigned int (UBitVar) — 6 bits seed that determines
    ///     whether 0, 4, 8, or 28 additional bits follow. Used pervasively on the wire.
    /// </summary>
    public uint ReadUBitVar()
    {
        uint ret = ReadUBits(6);
        switch (ret & (16 | 32))
        {
            case 16: ret = ret & 15 | ReadUBits(4) << 4; break;
            case 32: ret = ret & 15 | ReadUBits(8) << 4; break;
            case 48: ret = ret & 15 | ReadUBits(32 - 4) << 4; break;
        }

        return ret;
    }

    /// <summary>Reads a protobuf-style unsigned LEB128 varint (up to 5 bytes) and returns it as a <see cref="uint" />.</summary>
    public uint ReadUVarInt32()
    {
        uint result = 0;
        int shift = 0;
        byte b;
        do
        {
            b = ReadByte();
            result |= (uint)(b & 0x7F) << shift;
            shift += 7;
        } while ((b & 0x80) != 0);

        return result;
    }

    /// <summary>Reads a protobuf-style ZigZag-encoded signed varint and returns it as an <see cref="int" />.</summary>
    public int ReadVarInt32()
    {
        uint result = ReadUVarInt32();
        return (int)(result >> 1) ^ -(int)(result & 1);
    }

    /// <summary>Fills <paramref name="output" /> with bytes read from the buffer, one byte per slot.</summary>
    public void ReadBytes(scoped Span<byte> output)
    {
        for (int i = 0; i < output.Length; ++i)
        {
            output[i] = ReadByte();
        }
    }

    /// <summary>Allocates a new <c>byte[]</c> and reads <paramref name="count" /> bytes into it.</summary>
    public byte[] ReadBytes(int count)
    {
        byte[] buf = new byte[count];
        ReadBytes(buf.AsSpan());
        return buf;
    }

    /// <summary>
    ///     Reads <paramref name="bits" /> bits into <paramref name="output" />, packing 8 bits per
    ///     destination byte. Any trailing remainder bits land in the final byte's low bits.
    /// </summary>
    public void ReadBitsAsBytes(scoped Span<byte> output, int bits)
    {
        int bytes = bits / 8;
        int remainder = bits % 8;
        for (int i = 0; i < bytes; ++i)
        {
            output[i] = ReadByte();
        }

        if (remainder != 0)
        {
            output[bytes] = (byte)ReadUBits(remainder);
        }
    }

    /// <summary>
    ///     Reads Source's field-path-specific variable-width integer: a cascading 1-bit prefix
    ///     gate that selects between 2-, 4-, 10-, 17-, or 31-bit payload widths.
    /// </summary>
    public int ReadUBitVarFieldPath()
    {
        if (ReadOneBit())
        {
            return (int)ReadUBits(2);
        }

        if (ReadOneBit())
        {
            return (int)ReadUBits(4);
        }

        if (ReadOneBit())
        {
            return (int)ReadUBits(10);
        }

        if (ReadOneBit())
        {
            return (int)ReadUBits(17);
        }

        return (int)ReadUBits(31);
    }

    /// <summary>Reads a protobuf-style unsigned LEB128 varint (up to 10 bytes) and returns it as a <see cref="ulong" />.</summary>
    public ulong ReadUVarInt64()
    {
        int c = 0;
        ulong result = 0UL;
        byte b;
        do
        {
            b = ReadByte();
            if (c < 10)
            {
                result |= (ulong)(b & 0x7f) << 7 * c;
            }

            c += 1;
        } while ((b & 0x80) != 0);

        return result;
    }

    /// <summary>Reads a protobuf-style ZigZag-encoded signed varint and returns it as a <see cref="long" />.</summary>
    public long ReadVarInt64()
    {
        ulong result = ReadUVarInt64();
        return (long)(result >> 1) ^ -(long)(result & 1);
    }

    /// <summary>Reads a quantised angle in degrees [0, 360) over <paramref name="bits" /> bits.</summary>
    public float ReadAngle(int bits)
    {
        float max = (1UL << bits) - 1;
        return 360.0f * (ReadUBits(bits) / max);
    }

    /// <summary>
    ///     Reads Source's variable-length world coordinate: 1 bit has-integer, 1 bit has-fraction,
    ///     1 bit sign, optional 14-bit integer part, optional 5-bit fractional part.
    /// </summary>
    public float ReadCoord()
    {
        const int FractBits = 5;
        bool hasInt = ReadOneBit();
        bool hasFract = ReadOneBit();

        if (hasInt || hasFract)
        {
            bool signBit = ReadOneBit();
            float intval = hasInt ? ReadUBits(14) + 1.0f : 0.0f;
            float fractval = hasFract ? ReadUBits(FractBits) : 0.0f;
            float value = intval + fractval * (1.0f / (1 << FractBits));
            return signBit ? -value : value;
        }

        return 0.0f;
    }

    /// <summary>Reads a 20-bit high-precision coordinate, decoded into the range [-180, 180).</summary>
    public float ReadCoordPrecise() =>
        ReadUBits(20) * (360.0f / (1 << 20)) - 180.0f;

    /// <summary>Reads a NUL-terminated UTF-8 string. Grows a stack-allocated buffer to the heap if needed.</summary>
    public string ReadStringUtf8()
    {
        Span<byte> buf = stackalloc byte[260];
        int i = 0;
        byte b;
        while ((b = ReadByte()) != 0)
        {
            if (i == buf.Length)
            {
                byte[] newBuf = new byte[buf.Length * 2];
                buf.CopyTo(newBuf);
                buf = newBuf;
            }

            buf[i++] = b;
        }

        return Encoding.UTF8.GetString(buf[..i]);
    }

    /// <summary>Reads a single normal-vector component: 1 sign bit + 11-bit magnitude in [-1, 1].</summary>
    public float ReadNormal()
    {
        bool isNeg = ReadOneBit();
        uint len = ReadUBits(11);
        float ret = len * (1.0f / ((1 << 11) - 1));
        return isNeg ? -ret : ret;
    }

    /// <summary>
    ///     Reads a compressed 3-component unit normal: has-x bit + has-y bit + optional X / Y
    ///     components (each <see cref="ReadNormal" />) + sign-z bit. The Z component is derived
    ///     from <c>sqrt(1 - x² - y²)</c> so only its sign needs to be transmitted.
    /// </summary>
    public Vector3 Read3BitNormal()
    {
        float x = 0.0f, y = 0.0f;
        bool hasX = ReadOneBit();
        bool hasY = ReadOneBit();
        if (hasX)
        {
            x = ReadNormal();
        }

        if (hasY)
        {
            y = ReadNormal();
        }

        bool negZ = ReadOneBit();
        float sumSqr = x * x + y * y;
        float z = sumSqr < 1.0f ? (float)Math.Sqrt(1.0 - sumSqr) : 0.0f;
        return new Vector3(x, y, negZ ? -z : z);
    }
}
