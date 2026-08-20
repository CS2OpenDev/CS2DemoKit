namespace CS2DemoKit.Parser.Tests;

/// <summary>
///     Pins the two variable-width encodings the string-table bitstream uses under
///     <c>using_varint_bitcounts</c>, and pins them as DIFFERENT from each other.
///     <para>
///         One protobuf field switches both the non-sequential entry INDEX and the variable
///         user-data LENGTH off their fixed bit widths, which reads as an invitation to give them the
///         same encoding. It is not one: the index becomes a protobuf <c>UVarInt32</c>, the length
///         becomes Source's <c>UBitVar</c>. Reading the length as a varint consumed 8 bits where the
///         wire spent 6, desynced the entry, and threw away <c>userinfo</c> on every CS2 demo ever
///         produced. Reading the index as a UBitVar fails the other way and is worse: it consumes 6
///         bits where the wire spent 8, yields a plausible-looking small index, and drops a player
///         slot with NO exception and NO warning.
///     </para>
///     <para>
///         Each test below therefore chooses a value whose two encodings disagree in both width and
///         result, so decoding with the wrong one cannot coincidentally pass. Values that happen to
///         encode identically (anything under 16 spends 6 bits either way for the seed, but differs
///         in total width) would make these tests vacuous.
///     </para>
/// </summary>
[Category("Unit")]
public class StringTableWireFormatTests
{
    /// <summary>
    ///     Warnings here are raised without ever constructing a <see cref="ParsedDemo" />, which would
    ///     otherwise strand them on this pool thread — see the same hook on
    ///     <see cref="StringTableBoundsTests" />.
    /// </summary>
    [After(Test)]
    public void DrainStrandedWarnings() => ParseDiagnostics.Drain();

    private static StringTableProcessor.TableState VarintTable() =>
        new("userinfo")
        {
            UsingVarintBitcounts = true
        };

    /// <summary>
    ///     The entry index is a protobuf varint. Index 200 encodes as two bytes (<c>0xC8 0x01</c>);
    ///     a UBitVar read of those same bits stops after 6 and yields 8. Asserting the entry lands at
    ///     200 — and that nothing lands at 8 — is what fails if the index is ever "unified" with the
    ///     length's encoding.
    /// </summary>
    [Test]
    public async Task EntryIndex_IsProtobufVarint_NotUBitVar()
    {
        StringTableProcessor.TableState state = VarintTable();
        byte[] data = new StringTableBitWriter()
            .Zero().VarInt(200) // explicit (non-sequential) index
            .One() // hasString
            .Zero() // not a history suffix
            .Raw('h', 8).Raw('i', 8).Raw(0, 8) // "hi\0"
            .Zero() // hasUserData = 0
            .ToArray();

        StringTableProcessor.DecodeEntries(data, 1, state);

        await Assert.That(state.Entries.ContainsKey(200)).IsTrue()
            .Because("the index is a UVarInt32; a UBitVar read of these bits would have yielded 8");
        await Assert.That(state.Entries.ContainsKey(8)).IsFalse()
            .Because("8 is precisely the wrong answer a UBitVar read produces here — it must not appear");
        await Assert.That(state.Entries[200].Key).IsEqualTo("hi")
            .Because("the name decodes only if the index consumed exactly the bits the wire spent");
    }

    /// <summary>
    ///     The user-data length is a UBitVar. 35 encodes as a 6-bit seed plus 4 bits (10 bits total);
    ///     a varint read of those same bits consumes 8, sees the continuation bit set, and runs away
    ///     into a length no message could satisfy. Asserting the exact blob round-trips is what fails
    ///     if the length is ever switched back to the index's encoding.
    /// </summary>
    [Test]
    public async Task UserDataLength_IsUBitVar_NotProtobufVarint()
    {
        byte[] blob = new byte[35];
        for (int i = 0; i < blob.Length; i++)
        {
            blob[i] = (byte)(i + 1);
        }

        StringTableProcessor.TableState state = VarintTable();
        StringTableBitWriter bits = new StringTableBitWriter()
            .One() // sequential index → 0
            .Zero() // hasString = 0
            .One() // hasUserData = 1
            .UBitVar((uint)blob.Length);
        foreach (byte b in blob)
        {
            bits.Raw(b, 8);
        }

        StringTableProcessor.DecodeEntries(bits.ToArray(), 1, state);

        await Assert.That(state.Entries[0].Value).IsEquivalentTo(blob)
            .Because("the blob round-trips only if the length was read as a UBitVar, spending 6 bits not 8");
    }

    /// <summary>
    ///     The guard that makes the two tests above non-vacuous: the chosen values really do encode
    ///     differently. If a future change made these encodings agree, the tests above would keep
    ///     passing while proving nothing — this one would fail and say so.
    /// </summary>
    [Test]
    public async Task TheTwoEncodings_ActuallyDiffer_ForTheValuesPinnedAbove()
    {
        byte[] asVarint = new StringTableBitWriter().VarInt(200).ToArray();
        byte[] asUBitVar = new StringTableBitWriter().UBitVar(200).ToArray();

        await Assert.That(asVarint).IsNotEquivalentTo(asUBitVar)
            .Because("if these ever coincide, the encoding-pinning tests above stop discriminating");
    }
}
