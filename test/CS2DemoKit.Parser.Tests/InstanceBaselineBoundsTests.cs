using CS2DemoKit.Parser.EntityTracking;

namespace CS2DemoKit.Parser.Tests;

/// <summary>
///     Hostile-input bounds on the instancebaseline decoder. This is the second decoder over the
///     same untrusted string-table bitstream, and unlike <c>StringTableProcessor</c> it had no
///     bounds at all: the create path decompressed with no size ceiling, and the declared entry
///     count sized an allocation with nothing checking it against the payload.
///     <para>
///         These drive <c>EntityTracker.DecompressBounded</c> directly (internal, see its remarks).
///         Its only caller swallows per-update by design, so a test going through that path could
///         never observe the guard firing.
///     </para>
/// </summary>
[Category("Unit")]
public class InstanceBaselineBoundsTests
{
    /// <summary>A Snappy length header declaring <paramref name="declared" /> bytes, then filler.</summary>
    private static byte[] Declaring(uint declared)
    {
        List<byte> bytes = [];
        for (uint v = declared; ; v >>= 7)
        {
            if (v < 0x80)
            {
                bytes.Add((byte)v);
                break;
            }

            bytes.Add((byte)(v | 0x80));
        }

        bytes.AddRange([0xFF, 0xFF, 0xFF, 0xFF]);
        return [.. bytes];
    }

    // The decompression bomb the create path used to run uncapped: a few bytes declaring a huge
    // output, which drives the allocation before anything looks at the real payload.
    [Test]
    public async Task DecompressBounded_DeclaredLengthAboveTheCap_Throws()
    {
        InvalidDataException? ex = null;
        try
        {
            EntityTracker.DecompressBounded(Declaring((16 * 1024 * 1024) + 1));
        }
        catch (InvalidDataException e)
        {
            ex = e;
        }

        await Assert.That(ex).IsNotNull();
        await Assert.That(ex!.Message).Contains("16777217")
            .Because("the message names what was declared, so a real demo tripping it is diagnosable");
    }

    // GetUncompressedLength returns int, so 2^31 wraps negative and would pass an upper-bound-only
    // check, reaching Snappier to fail there instead of naming the real problem.
    [Test]
    public async Task DecompressBounded_DeclaredLengthWrappingNegative_Throws()
    {
        InvalidDataException? ex = null;
        try
        {
            EntityTracker.DecompressBounded(Declaring(2147483648u));
        }
        catch (InvalidDataException e)
        {
            ex = e;
        }

        await Assert.That(ex).IsNotNull();
        await Assert.That(ex!.Message).Contains("maximum")
            .Because("the wrapped length is rejected by our guard, not passed through to Snappier");
    }

    // The guard must not refuse a legitimate blob: anything under the ceiling proceeds to Snappier,
    // which is then free to reject it on its own terms.
    [Test]
    public async Task DecompressBounded_PlausibleDeclaredLength_ReachesTheDecoder()
    {
        Exception? ex = null;
        try
        {
            EntityTracker.DecompressBounded(Declaring(64));
        }
        catch (Exception e)
        {
            ex = e;
        }

        await Assert.That(ex).IsNotNull().Because("the payload is not a decodable stream");
        await Assert.That(ex!.Message).DoesNotContain("maximum")
            .Because("it failed inside Snappy, not at the size guard, so the guard let it through");
    }
}
