namespace CS2DemoKit.Parser.Tests;

/// <summary>
///     Pure-function tests for <see cref="BitBuffer" />, the bit-level
///     wire decoder hot in every entity decode. Tests cover the
///     individual read methods against hand-crafted bit patterns and the
///     critical hot-path branches (single-byte, 32-bit boundary,
///     cross-32-bit-word reads). Run in milliseconds against in-memory
///     byte arrays; would catch regressions that the existing demo-driven
///     integration tests can only surface as "entity decode failed."
///     <para>
///         <c>BitBuffer</c> is one of the protected core-parser files —
///         changes there need explicit sign-off. The test set is designed
///         to flag any breakage in the hot path immediately, not to
///         drive behaviour changes.
///     </para>
///     <para>
///         <b>Ref-struct constraint:</b> <see cref="BitBuffer" /> is a
///         <c>ref struct</c>, so test bodies can't <c>await</c> while
///         holding one. Pattern: read what you need, capture into
///         primitives, then assert on the primitives. See
///         <see cref="LEB128UtilsTests" /> for the same pattern.
///     </para>
/// </summary>
[Category("Unit")]
public class BitBufferTests
{
    // ── ReadByte ──────────────────────────────────────────────────────────────
    /// <summary>Read byte_reads sequential bytes.</summary>
    [Test]
    public async Task ReadByte_ReadsSequentialBytes()
    {
        byte[] data =
        {
            0xAA, 0xBB, 0xCC
        };
        byte b1, b2, b3;
        {
            BitBuffer bb = new(data);
            b1 = bb.ReadByte();
            b2 = bb.ReadByte();
            b3 = bb.ReadByte();
        }
        await Assert.That(b1).IsEqualTo((byte)0xAA);
        await Assert.That(b2).IsEqualTo((byte)0xBB);
        await Assert.That(b3).IsEqualTo((byte)0xCC);
    }

    // ── ReadOneBit ────────────────────────────────────────────────────────────
    /// <summary>Read one bit_reads lsb first from each byte.</summary>
    [Test]
    public async Task ReadOneBit_ReadsLsbFirstFromEachByte()
    {
        // 0b10110101 = 0xB5. ReadOneBit reads bits in least-significant order:
        // expected sequence: 1, 0, 1, 0, 1, 1, 0, 1
        byte[] data =
        {
            0xB5
        };
        bool b0, b1, b2, b3, b4, b5, b6, b7;
        int posAfter;
        {
            BitBuffer bb = new(data);
            b0 = bb.ReadOneBit();
            b1 = bb.ReadOneBit();
            b2 = bb.ReadOneBit();
            b3 = bb.ReadOneBit();
            b4 = bb.ReadOneBit();
            b5 = bb.ReadOneBit();
            b6 = bb.ReadOneBit();
            b7 = bb.ReadOneBit();
            posAfter = bb.TellBits;
        }

        await Assert.That(b0).IsTrue();
        await Assert.That(b1).IsFalse();
        await Assert.That(b2).IsTrue();
        await Assert.That(b3).IsFalse();
        await Assert.That(b4).IsTrue();
        await Assert.That(b5).IsTrue();
        await Assert.That(b6).IsFalse();
        await Assert.That(b7).IsTrue();
        await Assert.That(posAfter).IsEqualTo(8);
    }

    // ── ReadStringUtf8 ────────────────────────────────────────────────────────
    /// <summary>Read string utf8_reads until null terminator.</summary>
    [Test]
    public async Task ReadStringUtf8_ReadsUntilNullTerminator()
    {
        // "Hi\0Hi" — should read "Hi" and stop at the null.
        byte[] data =
        {
            0x48, 0x69, 0x00, 0x48, 0x69, 0x00, 0x00, 0x00
        };
        string s;
        int posAfter;
        {
            BitBuffer bb = new(data);
            s = bb.ReadStringUtf8();
            posAfter = bb.TellBits;
        }
        await Assert.That(s).IsEqualTo("Hi");
        // 3 bytes consumed (H + i + 0-terminator) = 24 bits.
        await Assert.That(posAfter).IsEqualTo(24);
    }

    // ── ReadUBitVarFieldPath ──────────────────────────────────────────────────
    // Branching width based on a leading 1-bit per branch:
    //   bit0=1                       -> 2-bit value
    //   bit0=0, bit1=1               -> 4-bit value
    //   bit0=0, bit1=0, bit2=1       -> 10-bit value
    //   bit0..bit2=0, bit3=1         -> 17-bit value
    //   bit0..bit3=0                 -> 31-bit value
    /// <summary>Read u bit var field path_takes first branch_for2 bit values.</summary>
    [Test]
    public async Task ReadUBitVarFieldPath_TakesFirstBranch_For2BitValues()
    {
        // bit 0 = 1 (take branch 1), bits 1-2 = value 0b11 = 3.
        // LSB→MSB: 1, 1, 1, 0, 0, 0, 0, 0  → 0b00000111 = 0x07
        byte[] data =
        {
            0x07, 0x00
        };
        int value;
        int posAfter;
        {
            BitBuffer bb = new(data);
            value = bb.ReadUBitVarFieldPath();
            posAfter = bb.TellBits;
        }
        await Assert.That(value).IsEqualTo(3);
        await Assert.That(posAfter).IsEqualTo(3); // 1 branch bit + 2 value bits
    }

    /// <summary>Read u bit var field path_takes second branch_for4 bit values.</summary>
    [Test]
    public async Task ReadUBitVarFieldPath_TakesSecondBranch_For4BitValues()
    {
        // bit 0 = 0, bit 1 = 1 (take branch 2), bits 2-5 = value 0b1111 = 15.
        // LSB→MSB: 0, 1, 1, 1, 1, 1, 0, 0  → 0b00111110 = 0x3E
        byte[] data =
        {
            0x3E, 0x00
        };
        int value;
        int posAfter;
        {
            BitBuffer bb = new(data);
            value = bb.ReadUBitVarFieldPath();
            posAfter = bb.TellBits;
        }
        await Assert.That(value).IsEqualTo(15);
        await Assert.That(posAfter).IsEqualTo(6); // 2 branch bits + 4 value bits
    }

    // ── ReadUBitVar ───────────────────────────────────────────────────────────
    // ReadUBitVar reads 6 bits. The top two bits (16, 32) decide how many MORE
    // bits to read for the high nibble:
    //   case 00: just the 6 bits (4 low bits)
    //   case 16: 4 more bits, total 8-bit value
    //   case 32: 8 more bits, total 12-bit value
    //   case 48: 28 more bits, total 32-bit value
    /// <summary>Read u bit var_small value_takes no extra bits.</summary>
    [Test]
    public async Task ReadUBitVar_SmallValue_TakesNoExtraBits()
    {
        // Encode value 0x05 in the low 4 bits of a 6-bit ReadUBits result with the
        // top two bits both clear (so no extra bits are read).
        // Bit layout (LSB first): 1 0 1 0 0 0 = ReadUBits(6) returns 0b000101 = 5.
        byte[] data =
        {
            0b00000101, 0x00
        };
        uint value;
        int posAfter;
        {
            BitBuffer bb = new(data);
            value = bb.ReadUBitVar();
            posAfter = bb.TellBits;
        }
        await Assert.That(value).IsEqualTo(5u);
        await Assert.That(posAfter).IsEqualTo(6);
    }

    /// <summary>Read u bits_crosses word boundary.</summary>
    [Test]
    public async Task ReadUBits_CrossesWordBoundary()
    {
        // Read 24 bits then 24 bits — the second read crosses the 32-bit word
        // boundary inside BitBuffer's internal _buf. Verify the value comes out
        // correctly via the cross-word fast path.
        //
        // Bytes: 0x01 0x02 0x03  0x04 0x05 0x06  (little-endian within bytes for ReadUBits)
        // First 24 bits  = 0x030201
        // Next 24 bits   = 0x060504
        byte[] data =
        {
            0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08
        };
        uint v1, v2;
        {
            BitBuffer bb = new(data);
            v1 = bb.ReadUBits(24);
            v2 = bb.ReadUBits(24);
        }
        await Assert.That(v1).IsEqualTo(0x030201u);
        await Assert.That(v2).IsEqualTo(0x060504u);
    }

    // ── ReadUBits ─────────────────────────────────────────────────────────────
    /// <summary>Test: ReadUBits DecodesKnownWidths.</summary>
    [Test]
    [Arguments(new byte[]
    {
        0x00
    }, 1, 0u)]
    [Arguments(new byte[]
    {
        0x01
    }, 1, 1u)]
    [Arguments(new byte[]
    {
        0xFF
    }, 8, 0xFFu)]
    [Arguments(new byte[]
    {
        0xAB, 0xCD, 0xEF, 0x12
    }, 32, 0x12EFCDABu)]
    public async Task ReadUBits_DecodesKnownWidths(byte[] data, int bits, uint expected)
    {
        uint value;
        int posAfter;
        {
            BitBuffer bb = new(data);
            value = bb.ReadUBits(bits);
            posAfter = bb.TellBits;
        }
        await Assert.That(value).IsEqualTo(expected);
        await Assert.That(posAfter).IsEqualTo(bits);
    }

    // ── ReadUVarInt32 ─────────────────────────────────────────────────────────
    /// <summary>Test: ReadUVarInt32 DecodesEveryBoundary.</summary>
    [Test]
    [Arguments(new byte[]
    {
        0x00
    }, 0u, 8)]
    [Arguments(new byte[]
    {
        0x01
    }, 1u, 8)]
    [Arguments(new byte[]
    {
        0x7F
    }, 127u, 8)]
    [Arguments(new byte[]
    {
        0x80, 0x01
    }, 128u, 16)]
    [Arguments(new byte[]
    {
        0xFF, 0x7F
    }, 16_383u, 16)]
    [Arguments(new byte[]
    {
        0xE5, 0x8E, 0x26
    }, 624_485u, 24)]
    [Arguments(new byte[]
    {
        0xFF, 0xFF, 0xFF, 0xFF, 0x0F
    }, 4_294_967_295u, 40)]
    public async Task ReadUVarInt32_DecodesEveryBoundary(byte[] data, uint expected, int bitsExpected)
    {
        uint value;
        int posAfter;
        {
            // Pad with trailing zero so BitBuffer doesn't trip the underflow
            // path when reading near the end of the buffer.
            byte[] padded = data.Concat(new byte[]
            {
                0x00, 0x00, 0x00, 0x00
            }).ToArray();
            BitBuffer bb = new(padded);
            value = bb.ReadUVarInt32();
            posAfter = bb.TellBits;
        }
        await Assert.That(value).IsEqualTo(expected);
        await Assert.That(posAfter).IsEqualTo(bitsExpected);
    }

    // ── ReadUVarInt64 ─────────────────────────────────────────────────────────
    /// <summary>Read u var int64_decodes ulong max value.</summary>
    [Test]
    public async Task ReadUVarInt64_DecodesUlongMaxValue()
    {
        // ulong.MaxValue (2^64 - 1) encodes as 10 bytes, all 0xFF except last 0x01.
        byte[] data =
        {
            0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x01, 0x00, 0x00, 0x00, 0x00
        }; // padding
        ulong value;
        {
            BitBuffer bb = new(data);
            value = bb.ReadUVarInt64();
        }
        await Assert.That(value).IsEqualTo(ulong.MaxValue);
    }

    // ── ReadVarInt32 (zigzag) ─────────────────────────────────────────────────
    /// <summary>Test: ReadVarInt32 ZigzagDecode.</summary>
    [Test]
    [Arguments(new byte[]
    {
        0x00
    }, 0)]
    [Arguments(new byte[]
    {
        0x02
    }, 1)]
    [Arguments(new byte[]
    {
        0x01
    }, -1)]
    [Arguments(new byte[]
    {
        0x04
    }, 2)]
    [Arguments(new byte[]
    {
        0x03
    }, -2)]
    public async Task ReadVarInt32_ZigzagDecode(byte[] data, int expected)
    {
        int value;
        {
            byte[] padded = data.Concat(new byte[]
            {
                0x00, 0x00, 0x00, 0x00
            }).ToArray();
            BitBuffer bb = new(padded);
            value = bb.ReadVarInt32();
        }
        await Assert.That(value).IsEqualTo(expected);
    }

    // ── TellBits ──────────────────────────────────────────────────────────────
    /// <summary>Tell bits_tracks consumed bits accurately.</summary>
    [Test]
    public async Task TellBits_TracksConsumedBitsAccurately()
    {
        byte[] data =
        {
            0xFF, 0xFF, 0xFF, 0xFF
        };
        int afterOne, afterByte, afterUBits;
        {
            BitBuffer bb = new(data);
            bb.ReadOneBit();
            afterOne = bb.TellBits;
            bb.ReadByte();
            afterByte = bb.TellBits;
            bb.ReadUBits(11);
            afterUBits = bb.TellBits;
        }
        await Assert.That(afterOne).IsEqualTo(1);
        await Assert.That(afterByte).IsEqualTo(9);
        await Assert.That(afterUBits).IsEqualTo(20);
    }
}
