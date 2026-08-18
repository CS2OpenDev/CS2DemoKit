namespace CS2DemoKit.Parser.Tests;

/// <summary>
///     Pure-function tests for <see cref="Leb128Utils" />, the ULEB128 varint
///     decoder that's hot in the parser path. No demo file required;
///     tests run in milliseconds against hand-crafted byte arrays.
///     <para>
///         Coverage: single-byte fast path, multi-byte continuation, every
///         7-bit boundary (0x7F → 0x80, 0x3FFF → 0x4000, …), uint32/uint64
///         maximums, malformed (truncated) input, empty input, TrySkip,
///         and cross-overload consistency.
///     </para>
///     <para>
///         These tests would have caught a regression in any of the
///         hot-path branches that the existing integration tests can only
///         detect by surfacing a demo-decode failure later in the pipeline.
///     </para>
/// </summary>
[Category("Unit")]
public class LEB128UtilsTests
{
    /// <summary>Parse frame header_decodes multibyte varints.</summary>
    [Test]
    public async Task ParseFrameHeader_DecodesMultiByteVarints()
    {
        // command=128 (0x80 0x01), tick=624_485 (0xE5 0x8E 0x26), size=16_383 (0xFF 0x7F).
        byte[] data =
        {
            0x80, 0x01, 0xE5, 0x8E, 0x26, 0xFF, 0x7F
        };
        int consumed = Leb128Utils.ParseFrameHeader(data, out FrameHeader header);

        int expectedConsumed = 7;
        await Assert.That(consumed).IsEqualTo(expectedConsumed);
        await Assert.That(header.Command).IsEqualTo(128u);
        await Assert.That(header.Tick).IsEqualTo(624_485); // Tick is int
        await Assert.That(header.Size).IsEqualTo(16_383u);
    }

    // ── Frame header — ParseFrameHeader ───────────────────────────────────────
    /// <summary>Parse frame header_decodes three varints.</summary>
    [Test]
    public async Task ParseFrameHeader_DecodesThreeVarints()
    {
        // Three single-byte varints: command=1, tick=2, size=3.
        byte[] data =
        {
            0x01, 0x02, 0x03, 0xFF, 0xFF
        };
        int consumed = Leb128Utils.ParseFrameHeader(data, out FrameHeader header);

        int expectedConsumed = 3;
        await Assert.That(consumed).IsEqualTo(expectedConsumed);
        await Assert.That(header.Command).IsEqualTo(1u);
        await Assert.That(header.Tick).IsEqualTo(2); // Tick is int
        await Assert.That(header.Size).IsEqualTo(3u);
    }

    /// <summary>Parse frame header_truncated returns negative.</summary>
    [Test]
    public async Task ParseFrameHeader_TruncatedReturnsNegative()
    {
        // 0x80 with no continuation byte — incomplete first varint.
        byte[] data =
        {
            0x80
        };
        int consumed = Leb128Utils.ParseFrameHeader(data, out _);
        int expected = -1;
        await Assert.That(consumed).IsEqualTo(expected);
    }

    // ── Cross-overload consistency ────────────────────────────────────────────
    // Span and byte-array overloads should produce identical (value, consumed)
    // for any input. Drift between them is the regression to catch.
    /// <summary>Test: SpanAndArrayOverloads Agree.</summary>
    [Test]
    [Arguments(new byte[]
    {
        0x00
    })]
    [Arguments(new byte[]
    {
        0x7F
    })]
    [Arguments(new byte[]
    {
        0x80, 0x01
    })]
    [Arguments(new byte[]
    {
        0xFF, 0x7F
    })]
    [Arguments(new byte[]
    {
        0xE5, 0x8E, 0x26
    })]
    [Arguments(new byte[]
    {
        0xFF, 0xFF, 0xFF, 0xFF, 0x0F
    })]
    public async Task SpanAndArrayOverloads_Agree(byte[] data)
    {
        uint spanValue;
        int spanConsumed;
        {
            ReadOnlySpan<byte> span = data;
            Leb128Utils.TryReadUInt32(ref span, out spanValue);
            spanConsumed = data.Length - span.Length;
        }

        int pos = 0;
        Leb128Utils.TryReadUInt32(data, ref pos, out uint arrValue);

        await Assert.That(spanValue).IsEqualTo(arrValue);
        await Assert.That(spanConsumed).IsEqualTo(pos);
    }

    // ── Byte-array + ref-int overload (DownstreamUtilities / DemoParser path) ─
    /// <summary>Test: TryReadUInt32 ByteArrayPos AdvancesPos.</summary>
    [Test]
    [Arguments(new byte[]
    {
        0x00
    }, 0u, 1)]
    [Arguments(new byte[]
    {
        0xE5, 0x8E, 0x26
    }, 624_485u, 3)]
    [Arguments(new byte[]
    {
        0xFF, 0xFF, 0xFF, 0xFF, 0x0F
    }, uint.MaxValue, 5)]
    public async Task TryReadUInt32_ByteArrayPos_AdvancesPos(
        byte[] data, uint expected, int expectedPos)
    {
        int pos = 0;
        await Assert.That(Leb128Utils.TryReadUInt32(data, ref pos, out uint value)).IsTrue();
        await Assert.That(value).IsEqualTo(expected);
        await Assert.That(pos).IsEqualTo(expectedPos);
    }

    /// <summary>Try read u int32_byte array pos_starting midway.</summary>
    [Test]
    public async Task TryReadUInt32_ByteArrayPos_StartingMidway()
    {
        // Buffer: filler 0xAA 0xBB, then varint 128 (0x80 0x01), then trailer 0xCC.
        byte[] data =
        {
            0xAA, 0xBB, 0x80, 0x01, 0xCC
        };
        int pos = 2;
        await Assert.That(Leb128Utils.TryReadUInt32(data, ref pos, out uint value)).IsTrue();
        await Assert.That(value).IsEqualTo(128u);
        await Assert.That(pos).IsEqualTo(4);
    }

    // ── Non-advancing overload — TryReadUInt32(span, out value, out bytesConsumed) ─
    /// <summary>Test: TryReadUInt32 NonAdvancing ReportsByteCount.</summary>
    [Test]
    [Arguments(new byte[]
    {
        0x00
    }, 0u, 1)]
    [Arguments(new byte[]
    {
        0x7F
    }, 127u, 1)]
    [Arguments(new byte[]
    {
        0x80, 0x01
    }, 128u, 2)]
    [Arguments(new byte[]
    {
        0xE5, 0x8E, 0x26
    }, 624_485u, 3)]
    [Arguments(new byte[]
    {
        0xFF, 0xFF, 0xFF, 0xFF, 0x0F
    }, 4_294_967_295u, 5)]
    public async Task TryReadUInt32_NonAdvancing_ReportsByteCount(
        byte[] bytes, uint expectedValue, int expectedConsumed)
    {
        bool ok = Leb128Utils.TryReadUInt32(bytes, out uint value, out int consumed);
        await Assert.That(ok).IsTrue();
        await Assert.That(value).IsEqualTo(expectedValue);
        await Assert.That(consumed).IsEqualTo(expectedConsumed);
    }

    /// <summary>Try read u int32_span_advances past varint only.</summary>
    [Test]
    public async Task TryReadUInt32_Span_AdvancesPastVarintOnly()
    {
        // Two consecutive varints: 128 (0x80 0x01) followed by 5 (0x05).
        byte[] bytes =
        {
            0x80, 0x01, 0x05
        };
        bool ok1, ok2;
        uint first, second;
        int afterFirst, afterSecond;
        {
            ReadOnlySpan<byte> span = bytes;
            ok1 = Leb128Utils.TryReadUInt32(ref span, out first);
            afterFirst = span.Length;
            ok2 = Leb128Utils.TryReadUInt32(ref span, out second);
            afterSecond = span.Length;
        }
        await Assert.That(ok1).IsTrue();
        await Assert.That(first).IsEqualTo(128u);
        await Assert.That(afterFirst).IsEqualTo(1);
        await Assert.That(ok2).IsTrue();
        await Assert.That(second).IsEqualTo(5u);
        await Assert.That(afterSecond).IsEqualTo(0);
    }

    // ── Span overload — TryReadUInt32(ref ReadOnlySpan<byte>, out uint) ───────
    /// <summary>Test: TryReadUInt32 Span DecodesEveryBoundary.</summary>
    [Test]
    [Arguments(new byte[]
    {
        0x00
    }, 0u)]
    [Arguments(new byte[]
    {
        0x01
    }, 1u)]
    [Arguments(new byte[]
    {
        0x7F
    }, 127u)]
    [Arguments(new byte[]
    {
        0x80, 0x01
    }, 128u)]
    [Arguments(new byte[]
    {
        0xFF, 0x7F
    }, 16_383u)]
    [Arguments(new byte[]
    {
        0xE5, 0x8E, 0x26
    }, 624_485u)]
    [Arguments(new byte[]
    {
        0x80, 0x80, 0x01
    }, 16_384u)]
    [Arguments(new byte[]
    {
        0xFF, 0xFF, 0x7F
    }, 2_097_151u)]
    [Arguments(new byte[]
    {
        0x80, 0x80, 0x80, 0x01
    }, 2_097_152u)]
    [Arguments(new byte[]
    {
        0xFF, 0xFF, 0xFF, 0x7F
    }, 268_435_455u)]
    [Arguments(new byte[]
    {
        0x80, 0x80, 0x80, 0x80, 0x01
    }, 268_435_456u)]
    [Arguments(new byte[]
    {
        0xFF, 0xFF, 0xFF, 0xFF, 0x0F
    }, 4_294_967_295u)] // uint32 max
    public async Task TryReadUInt32_Span_DecodesEveryBoundary(byte[] bytes, uint expected)
    {
        // Capture results BEFORE awaiting — ReadOnlySpan<byte> cannot cross
        // await boundaries (it's a ref struct).
        bool ok;
        uint value;
        int remaining;
        {
            ReadOnlySpan<byte> span = bytes;
            ok = Leb128Utils.TryReadUInt32(ref span, out value);
            remaining = span.Length;
        }
        await Assert.That(ok).IsTrue();
        await Assert.That(value).IsEqualTo(expected);
        await Assert.That(remaining).IsEqualTo(0);
    }

    /// <summary>Try read u int32_span_empty returns false.</summary>
    [Test]
    public async Task TryReadUInt32_Span_EmptyReturnsFalse()
    {
        bool ok;
        uint value;
        {
            ReadOnlySpan<byte> span = ReadOnlySpan<byte>.Empty;
            ok = Leb128Utils.TryReadUInt32(ref span, out value);
        }
        await Assert.That(ok).IsFalse();
        await Assert.That(value).IsEqualTo(0u);
    }

    /// <summary>Test: TryReadUInt32 Span TruncatedReturnsFalse.</summary>
    [Test]
    [Arguments(new byte[]
    {
        0x80
    })] // one-byte truncation
    [Arguments(new byte[]
    {
        0x80, 0x80
    })] // two-byte truncation
    [Arguments(new byte[]
    {
        0xFF, 0xFF, 0xFF, 0xFF
    })] // four-byte continuation, no terminator
    [Arguments(new byte[]
    {
        0x80, 0x80, 0x80, 0x80, 0x80
    })] // five-byte all-continuation (uint32 max length but never terminates)
    public async Task TryReadUInt32_Span_TruncatedReturnsFalse(byte[] bytes)
    {
        bool ok;
        {
            ReadOnlySpan<byte> span = bytes;
            ok = Leb128Utils.TryReadUInt32(ref span, out _);
        }
        await Assert.That(ok).IsFalse();
    }

    // ── uint64 overload ───────────────────────────────────────────────────────
    /// <summary>Test: TryReadUInt64 DecodesEveryBoundary.</summary>
    [Test]
    [Arguments(new byte[]
    {
        0x00
    }, 0ul, 1)]
    [Arguments(new byte[]
    {
        0x7F
    }, 127ul, 1)]
    [Arguments(new byte[]
    {
        0x80, 0x01
    }, 128ul, 2)]
    [Arguments(new byte[]
    {
        0xFF, 0xFF, 0xFF, 0xFF, 0x0F
    }, 4_294_967_295ul, 5)]
    [Arguments(new byte[]
    {
        0x80, 0x80, 0x80, 0x80, 0x10
    }, 4_294_967_296ul, 5)] // 2^32
    [Arguments(new byte[]
    {
        0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x01
    }, ulong.MaxValue, 10)]
    public async Task TryReadUInt64_DecodesEveryBoundary(byte[] data, ulong expected, int expectedPos)
    {
        int pos = 0;
        await Assert.That(Leb128Utils.TryReadUInt64(data, ref pos, out ulong value)).IsTrue();
        await Assert.That(value).IsEqualTo(expected);
        await Assert.That(pos).IsEqualTo(expectedPos);
    }

    /// <summary>Try read u int64_truncated returns false.</summary>
    [Test]
    public async Task TryReadUInt64_TruncatedReturnsFalse()
    {
        // 10-byte continuation with no terminator.
        byte[] data =
        {
            0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80
        };
        int pos = 0;
        await Assert.That(Leb128Utils.TryReadUInt64(data, ref pos, out ulong _)).IsFalse();
    }

    // ── TrySkip ───────────────────────────────────────────────────────────────
    /// <summary>Test: TrySkip AdvancesPastVarint.</summary>
    [Test]
    [Arguments(new byte[]
    {
        0x00
    }, 1)]
    [Arguments(new byte[]
    {
        0x7F
    }, 1)]
    [Arguments(new byte[]
    {
        0x80, 0x01
    }, 2)]
    [Arguments(new byte[]
    {
        0xE5, 0x8E, 0x26
    }, 3)]
    [Arguments(new byte[]
    {
        0xFF, 0xFF, 0xFF, 0xFF, 0x0F
    }, 5)]
    public async Task TrySkip_AdvancesPastVarint(byte[] data, int expectedPos)
    {
        int pos = 0;
        await Assert.That(Leb128Utils.TrySkip(data, ref pos)).IsTrue();
        await Assert.That(pos).IsEqualTo(expectedPos);
    }

    /// <summary>Try skip_truncated returns false.</summary>
    [Test]
    public async Task TrySkip_TruncatedReturnsFalse()
    {
        byte[] data =
        {
            0x80, 0x80, 0x80
        }; // all continuation, no terminator
        int pos = 0;
        await Assert.That(Leb128Utils.TrySkip(data, ref pos)).IsFalse();
    }
}
