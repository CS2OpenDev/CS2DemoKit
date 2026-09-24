#region

using CS2DemoKit.Parser.Entities;
using CS2DemoKit.Parser.EntityTracking;

#endregion

namespace CS2DemoKit.Parser.Tests.EntityTracking;

/// <summary>
///     The name-keyed serializer overrides in <see cref="FieldDecoderFactory" />: fields whose
///     <c>MNetworkSerializer</c> attribute the flattened-serializer proto never sends, so the factory
///     fills the encoder in by name. <c>m_iClip1</c> and <c>m_iClip2</c> are networked with
///     <c>minusone</c> (clip + 1 as an unsigned varint) and were once read as zigzag, which made the
///     clip alternate in sign and read one high (#49). Hand-built varints, no demo.
///     <para>
///         <see cref="BitBuffer" /> is a ref struct, so each test decodes into locals first and
///         asserts after.
///     </para>
/// </summary>
[Category("Unit")]
public class FieldDecoderSerializerOverrideTests
{
    private static readonly uint[] ClipRaw = [0, 1, 8, 31, 36, 151];
    private static readonly int[] ClipExpected = [-1, 0, 7, 30, 35, 150];

    private static RuntimeField Field(string name, string type, string? encoder = null) =>
        new(name, type, encoder, 0, null, null, 0, null, 0, [], [], null);

    private static byte[] Leb128(params uint[] values)
    {
        List<byte> bytes = [];
        foreach (uint value in values)
        {
            uint v = value;
            while (v >= 0x80)
            {
                bytes.Add((byte)(v | 0x80));
                v >>= 7;
            }

            bytes.Add((byte)v);
        }

        return bytes.ToArray();
    }

    private static uint ZigZag(int value) => (uint)(value << 1 ^ value >> 31);

    private static int[] DecodeInt(RuntimeField field, byte[] data, int count)
    {
        IntDecoder decoder = FieldDecoderFactory.TryCreateInt(field)
                             ?? throw new InvalidOperationException("no int-lane decoder");
        int[] values = new int[count];
        BitBuffer b = new(data);
        for (int i = 0; i < count; i++)
        {
            values[i] = decoder(ref b);
        }

        return values;
    }

    private static object?[] DecodeBoxed(RuntimeField field, byte[] data, int count, out int tellBits)
    {
        FieldDecoder decoder = FieldDecoderFactory.Create(field);
        object?[] values = new object?[count];
        BitBuffer b = new(data);
        for (int i = 0; i < count; i++)
        {
            values[i] = decoder(ref b);
        }

        tellBits = b.TellBits;
        return values;
    }

    /// <summary>
    ///     A raw unsigned varint n decodes to n - 1 on both lanes: 0 is the no-magazine -1, and a
    ///     full Galil magazine (35) arrives as 36.
    /// </summary>
    [Test]
    [Arguments("m_iClip1")]
    [Arguments("m_iClip2")]
    public async Task Clip_DecodesMinusOne_OnBothLanes(string name)
    {
        RuntimeField field = Field(name, "int32");
        byte[] data = Leb128(ClipRaw);

        int[] typed = DecodeInt(field, data, ClipRaw.Length);
        object?[] boxed = DecodeBoxed(field, data, ClipRaw.Length, out _);

        await Assert.That(typed).IsEquivalentTo(ClipExpected);
        await Assert.That(boxed.Select(v => (int)v!).ToArray()).IsEquivalentTo(ClipExpected);
    }

    /// <summary>
    ///     The minusone read consumes exactly one varint per value, the same bits zigzag consumed,
    ///     so the cursor ends where the bytes end and nothing after the field moves.
    /// </summary>
    [Test]
    [Arguments("m_iClip1")]
    [Arguments("m_iClip2")]
    public async Task Clip_ConsumesOneVarint(string name)
    {
        RuntimeField field = Field(name, "int32");
        byte[] data = Leb128(ClipRaw);

        DecodeBoxed(field, data, ClipRaw.Length, out int tellBits);

        await Assert.That(tellBits).IsEqualTo(data.Length * 8);
    }

    /// <summary>
    ///     Control: the override is keyed on the field, not on int32. Health still decodes zigzag.
    /// </summary>
    [Test]
    public async Task PlainInt32_StaysZigzag()
    {
        RuntimeField field = Field("m_iHealth", "int32");
        byte[] data = Leb128(200, 1, ZigZag(37));

        int[] typed = DecodeInt(field, data, 3);
        object?[] boxed = DecodeBoxed(field, data, 3, out _);

        int[] expected = [100, -1, 37];
        await Assert.That(typed).IsEquivalentTo(expected);
        await Assert.That(boxed.Select(v => (int)v!).ToArray()).IsEquivalentTo(expected);
    }

    /// <summary>A proto that does declare <c>minusone</c> is honored on any int32 field.</summary>
    [Test]
    public async Task ProtoMinusOne_IsHonored()
    {
        RuntimeField field = Field("m_iSomethingElse", "int32", "minusone");
        byte[] data = Leb128(ClipRaw);

        int[] typed = DecodeInt(field, data, ClipRaw.Length);
        object?[] boxed = DecodeBoxed(field, data, ClipRaw.Length, out _);

        await Assert.That(typed).IsEquivalentTo(ClipExpected);
        await Assert.That(boxed.Select(v => (int)v!).ToArray()).IsEquivalentTo(ClipExpected);
    }

    /// <summary>
    ///     The name table only fills a null encoder. A proto-declared encoder wins, so a named field
    ///     that arrives with one keeps it: m_flSimulationTime declared <c>coord</c> is not read as
    ///     simtime, and m_iClip1 declared with an unrelated encoder stays zigzag.
    /// </summary>
    [Test]
    public async Task ProtoEncoder_WinsOverNameTable()
    {
        // A simtime read of 0x40 (64 ticks) consumes one byte and returns 1.0; a coord read of
        // the same byte consumes two flag bits and returns 0 when both are clear.
        byte[] simData = [0x40, 0x00, 0x00, 0x00];
        float coordRead;
        int coordBits;
        {
            FloatDecoder decoder = FieldDecoderFactory.TryCreateFloat(Field("m_flSimulationTime", "float32", "coord"))!;
            BitBuffer b = new(simData);
            coordRead = decoder(ref b);
            coordBits = b.TellBits;
        }

        int[] clip = DecodeInt(Field("m_iClip1", "int32", "something"), Leb128(1, 200), 2);

        await Assert.That(coordRead).IsEqualTo(0f);
        await Assert.That(coordBits).IsEqualTo(2);
        await Assert.That(clip).IsEquivalentTo(new[] { -1, 100 });
    }

    /// <summary>
    ///     Regression guard for folding the simtime override into one place: both time fields still
    ///     decode as a tick count at 64 ticks per second through the typed and the boxed lanes.
    /// </summary>
    [Test]
    [Arguments("m_flSimulationTime")]
    [Arguments("m_flAnimTime")]
    public async Task SimTimeOverride_StillApplies(string name)
    {
        RuntimeField field = Field(name, "float32");
        byte[] data = Leb128(64, 6400);

        float[] typed = new float[2];
        {
            FloatDecoder decoder = FieldDecoderFactory.TryCreateFloat(field)!;
            BitBuffer b = new(data);
            typed[0] = decoder(ref b);
            typed[1] = decoder(ref b);
        }

        object?[] boxed = DecodeBoxed(field, data, 2, out int tellBits);

        float[] expected = [1f, 100f];
        await Assert.That(typed).IsEquivalentTo(expected);
        await Assert.That(boxed.Select(v => (float)v!).ToArray()).IsEquivalentTo(expected);
        await Assert.That(tellBits).IsEqualTo(data.Length * 8);
    }
}
