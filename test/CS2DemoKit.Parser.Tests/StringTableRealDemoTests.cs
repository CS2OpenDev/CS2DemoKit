#region

using CS2DemoKit.TestSupport;

#endregion

namespace CS2DemoKit.Parser.Tests;

/// <summary>
///     The <c>userinfo</c> string table must decode from a REAL demo's bitstream, not merely from a
///     fixture this repository encoded itself.
///     <para>
///         <b>Why this test exists as a separate, demo-backed file.</b> The variable user-data length
///         under <c>UsingVarintBitcounts</c> was read as a protobuf <c>UVarInt32</c> when the wire
///         writes Source's <c>UBitVar</c> — 8 bits consumed where the wire spent 6. Every CS2 demo
///         ever produced tripped the resulting bounds check and had its <c>userinfo</c> table thrown
///         away. <see cref="StringTableBoundsTests" /> stayed green throughout, because its bit-writer
///         emitted lengths with the same wrong encoding the decoder read them with: a synthetic
///         fixture authored against the implementation's belief can only ever confirm that belief.
///         Only real bytes can falsify it, so the guard against a regression here has to be a real
///         demo.
///     </para>
///     <para>
///         The assertion is on <see cref="ParseWarningCodes" />-coded warnings rather than on the
///         roster, and that is deliberate: the roster SURVIVED the bug. <c>CDemoStringTables</c>
///         snapshots repopulate <c>userinfo</c> through an entirely different code path, so names and
///         SteamIDs stayed correct while the bitstream decoder was failing on every single message.
///         Any assertion about players would have passed on the broken decoder too — the warning
///         channel was the only place the damage was visible.
///     </para>
/// </summary>
[Category("Integration")]
public class StringTableRealDemoTests
{
    /// <summary>
    ///     A healthy demo produces no string-table warnings at all. Any <c>string-table-*</c> code on
    ///     a known-good file means the bitstream decoder disagrees with the wire — which is a decoder
    ///     defect, not a damaged demo, however the warning is phrased.
    /// </summary>
    [Test]
    public async Task HealthyDemo_ProducesNoStringTableWarnings()
    {
        string path = DemoTestHelper.RequireDemo();
        ParsedDemo demo = DemoTestHelper.GetOrParse(path);

        string[] stringTableWarnings = demo.Warnings
            .Where(w => w.Code.StartsWith("string-table-", StringComparison.Ordinal))
            .Select(w => w.Message)
            .ToArray();

        await Assert.That(stringTableWarnings).IsEmpty()
            .Because("a known-good demo's string tables must decode; a warning here is the decoder's fault");
    }
}
