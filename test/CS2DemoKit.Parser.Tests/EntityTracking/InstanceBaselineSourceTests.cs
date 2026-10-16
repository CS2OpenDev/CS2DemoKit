#region

using CS2DemoKit.Parser.EntityTracking;
using CS2DemoKit.TestSupport;
using TUnit.Core.Exceptions;

#endregion

namespace CS2DemoKit.Parser.Tests.EntityTracking;

/// <summary>
///     The full packet a resync reloads instance baselines from belongs to the tracker's current
///     walk. A pooled chunk worker re-primes one tracker many times, so a prime must replace the
///     source a previous chunk left behind, never inherit it.
/// </summary>
[Category("Integration")]
[NotInParallel]
public class InstanceBaselineSourceTests
{
    [Test]
    public async Task APrimeReplacesTheBaselineSourceAPreviousWalkLeft()
    {
        string path = DemoTestHelper.RequireDemo();
        byte[] bytes = await File.ReadAllBytesAsync(path);
        using DemoReader reader = DemoReader.Open(bytes.AsMemory(), new ParseOptions { Plan = DecodePlan.EntityReplay });

        EntityTracker walked = new();
        List<DemoFrame> carriers = [];
        while (reader.TryReadNext(out DemoFrame? frame))
        {
            walked.AdvanceOneFrame(frame);
            if (frame.CommandKind == EDemoCommands.DemFullPacket && DemoEnrichmentCursor.CarriesInstanceBaseline(frame))
            {
                carriers.Add(frame);
            }
        }

        if (carriers.Count == 0)
        {
            throw new SkipTestException("the reference demo has no full packet carrying instancebaseline");
        }

        await Assert.That(walked.InstanceBaselineFullPacketForTest).IsSameReferenceAs(carriers[^1]);

        walked.ResetEntitiesKeepSchema();
        await Assert.That(walked.InstanceBaselineFullPacketForTest).IsNull();

        await Assert.That(walked.LoadInstanceBaselineSnapshot(carriers[0])).IsTrue();
        await Assert.That(walked.InstanceBaselineFullPacketForTest).IsSameReferenceAs(carriers[0]);

        EntityTracker adopted = new();
        adopted.AdoptSchemaState(walked);
        await Assert.That(adopted.InstanceBaselineFullPacketForTest).IsNull();
    }
}
