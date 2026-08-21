using CS2DemoKit.Parser;
using CS2DemoKit.TestSupport;

namespace CS2DemoKit.Parser.Tests;

/// <summary>
///     The malformed-demo policy (issue #12): what the parser refuses to continue on, and how a
///     consumer tells a damaged demo from a fine one.
///     <para>
///         The rule these pin: the parser throws only when the input is not a CS2 demo at all.
///         Damage inside a real demo degrades to a partial parse plus a graded warning, because a
///         corrupt byte late in a match should not cost the whole parse.
///     </para>
/// </summary>
[Category("Unit")]
public class MalformedDemoPolicyTests
{
    /// <summary>A 16-byte header good enough to pass the magic check, then <paramref name="body" />.</summary>
    private static byte[] DemoWith(params byte[] body)
    {
        byte[] file = new byte[16 + body.Length];
        "PBDEMS2\0"u8.CopyTo(file);
        body.CopyTo(file, 16);
        return file;
    }

    /// <summary>True when the parser refused the input outright rather than returning a partial.</summary>
    private static bool Refused(byte[] file)
    {
        try
        {
            DemoParser.Parse(file.AsMemory());
            return false;
        }
        catch (InvalidDataException)
        {
            return true;
        }
    }

    // ── The one thing that still throws ───────────────────────────────────────

    [Test]
    public async Task Parse_NotADemoFile_Throws()
    {
        byte[] notADemo = new byte[64];
        "NOTADEMO"u8.CopyTo(notADemo);

        await Assert.That(Refused(notADemo)).IsTrue()
            .Because("there is no partial result to offer when the input is not a demo at all");
    }

    [Test]
    public async Task Parse_TooShortForAHeader_Throws()
    {
        await Assert.That(Refused(new byte[8])).IsTrue();
    }

    // ── Everything else degrades ──────────────────────────────────────────────

    // Was a throw before this policy landed: a nonsense frame size discarded every frame already
    // scanned. Offsets chain, so the scan still cannot continue, but what it has is worth keeping.
    [Test]
    public async Task Parse_FrameSizeThatCannotBeReal_DegradesInsteadOfThrowing()
    {
        // cmd=1, tick=0, size = 0x80000000 as a 5-byte varint: negative once cast to int.
        byte[] file = DemoWith(0x01, 0x00, 0x80, 0x80, 0x80, 0x80, 0x08);

        ParsedDemo demo = DemoParser.Parse(file.AsMemory());

        await Assert.That(demo.Warnings.Any(w => w.Code == ParseWarningCodes.FrameStreamCorrupt)).IsTrue()
            .Because("the consumer has to be told the stream was abandoned, not just handed short output");
        await Assert.That(demo.Health).IsEqualTo(ParseHealth.Damaged);
    }

    [Test]
    public async Task Parse_TruncatedMidPayload_KeepsWhatItScannedAndSaysSo()
    {
        string path = DemoTestHelper.RequireDemo();
        byte[] whole = File.ReadAllBytes(path);

        ParsedDemo full = DemoParser.Parse(whole.AsMemory());
        byte[] cut = whole[..(whole.Length / 2)];

        ParsedDemo partial = DemoParser.Parse(cut.AsMemory());

        await Assert.That(partial.Frames.Count).IsGreaterThan(0)
            .Because("half a demo is still worth returning");
        await Assert.That(partial.Frames.Count).IsLessThan(full.Frames.Count);
        await Assert.That(partial.Warnings.Any(w => w.Code == ParseWarningCodes.DemoTruncated)).IsTrue()
            .Because("a silent partial parse is the exact failure this policy exists to remove");
        await Assert.That(partial.Health).IsEqualTo(ParseHealth.Damaged);
    }

    // ── Health is not Warnings.Count > 0 ──────────────────────────────────────

    // The discriminating case. A demo from a build newer than this parser drops message types it
    // has no case for; that is this library being behind, not the demo being damaged. Grading the
    // two the same would fire a "damaged" banner on every new-build demo.
    [Test]
    public async Task SeverityOf_SeparatesParserLimitationFromDemoDamage()
    {
        await Assert.That(ParseWarningCodes.SeverityOf(ParseWarningCodes.NetMessageDropped))
            .IsEqualTo(ParseHealth.Degraded);
        await Assert.That(ParseWarningCodes.SeverityOf(ParseWarningCodes.WarningsTruncated))
            .IsEqualTo(ParseHealth.Degraded);

        await Assert.That(ParseWarningCodes.SeverityOf(ParseWarningCodes.StringTableCreateFailed))
            .IsEqualTo(ParseHealth.Damaged);
        await Assert.That(ParseWarningCodes.SeverityOf(ParseWarningCodes.DemoTruncated))
            .IsEqualTo(ParseHealth.Damaged);
    }

    // A code added without a severity must not read as clean.
    [Test]
    public async Task SeverityOf_UnknownCode_IsPessimistic()
    {
        await Assert.That(ParseWarningCodes.SeverityOf("code-nobody-graded")).IsEqualTo(ParseHealth.Damaged);
    }

    [Test]
    public async Task Health_OnAHealthyDemo_IsNotDamaged()
    {
        string path = DemoTestHelper.RequireDemo();
        ParsedDemo demo = DemoParser.Parse(File.ReadAllBytes(path).AsMemory());

        await Assert.That(demo.Health).IsNotEqualTo(ParseHealth.Damaged);
    }
}
