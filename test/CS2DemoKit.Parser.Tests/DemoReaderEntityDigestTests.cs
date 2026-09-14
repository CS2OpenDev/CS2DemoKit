#region

using CS2DemoKit.Parser.Entities;
using CS2DemoKit.Parser.EntityTracking;
using CS2DemoKit.TestSupport;
using CS2OpenSchema.Protos;

#endregion

namespace CS2DemoKit.Parser.Tests;

/// <summary>
///     Entity replay through the forward reader must build the same world as replay over the
///     retained frame list, checked at every full packet and at the end with the entity digest,
///     under the full plan and under the entity-replay plan that leaves the rest undecoded.
/// </summary>
[NotInParallel]
[Category("Integration")]
public class DemoReaderEntityDigestTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Replay_OverReader_MatchesReplayOverList(bool narrowed)
    {
        string path = DemoTestHelper.RequireDemo();
        byte[] bytes = await File.ReadAllBytesAsync(path);
        ParsedDemo full = DemoTestHelper.GetOrParse(path);

        EntityTracker reference = EntityTrackerFactory.CreateCurated();
        List<ulong> referenceDigests = [];
        foreach (DemoFrame frame in full.Frames)
        {
            reference.AdvanceOneFrame(frame);
            if (frame.CommandKind == EDemoCommands.DemFullPacket)
            {
                referenceDigests.Add(EntitySetDigest.Compute(reference.CurrentEntities));
            }
        }

        await Assert.That(reference.LastEntityError).IsNull();
        await Assert.That(referenceDigests.Count).IsGreaterThan(0);

        ParseOptions options = new() { Plan = narrowed ? DecodePlan.EntityReplay : DecodePlan.Everything };
        using DemoReader reader = DemoReader.Open(bytes.AsMemory(), options);
        EntityTracker streamed = EntityTrackerFactory.CreateCurated();
        List<ulong> streamedDigests = [];
        while (reader.TryReadNext(out DemoFrame? frame))
        {
            streamed.AdvanceOneFrame(frame);
            if (frame.CommandKind == EDemoCommands.DemFullPacket)
            {
                streamedDigests.Add(EntitySetDigest.Compute(streamed.CurrentEntities));
            }
        }

        await Assert.That(streamed.LastEntityError).IsNull();
        await Assert.That(streamed.CurrentTick).IsEqualTo(reference.CurrentTick);
        await Assert.That(streamed.CurrentFrameIndex).IsEqualTo(reference.CurrentFrameIndex);
        await Assert.That(streamedDigests.Count).IsEqualTo(referenceDigests.Count);
        for (int i = 0; i < referenceDigests.Count; i++)
        {
            await Assert.That(streamedDigests[i]).IsEqualTo(referenceDigests[i]).Because($"full packet #{i}");
        }

        await Assert.That(EntitySetDigest.Compute(streamed.CurrentEntities))
            .IsEqualTo(EntitySetDigest.Compute(reference.CurrentEntities));

        // Belt and braces: the digest is one number, so also walk the final fields directly.
        Dictionary<int, EntityState> a = reference.CurrentEntities.AllIndexed().ToDictionary(t => t.Index, t => t.Entity);
        Dictionary<int, EntityState> b = streamed.CurrentEntities.AllIndexed().ToDictionary(t => t.Index, t => t.Entity);
        await Assert.That(b.Count).IsEqualTo(a.Count);
        foreach ((int index, EntityState ea) in a)
        {
            EntityState eb = b[index];
            await Assert.That(eb.ClassName).IsEqualTo(ea.ClassName);
            await Assert.That(eb.Serial).IsEqualTo(ea.Serial);
            await Assert.That(eb.IsInPvs).IsEqualTo(ea.IsInPvs);
            IReadOnlyDictionary<string, object?> fa = ea.Fields;
            IReadOnlyDictionary<string, object?> fb = eb.Fields;
            await Assert.That(fb.Count).IsEqualTo(fa.Count);
            foreach ((string key, object? va) in fa)
            {
                await Assert.That(fb.ContainsKey(key)).IsTrue();
                await Assert.That(EntitySetDigest.Canonical(fb[key])).IsEqualTo(EntitySetDigest.Canonical(va));
            }
        }
    }

    [Test]
    public async Task Digest_IsSensitiveToOneField()
    {
        string path = DemoTestHelper.RequireDemo();
        ParsedDemo full = DemoTestHelper.GetOrParse(path);
        EntityTracker tracker = EntityTrackerFactory.CreateCurated();
        int stopAt = Math.Min(full.Frames.Count - 1, DemoTestHelper.LivePlayFrameIndex(full));
        for (int i = 0; i <= stopAt; i++)
        {
            tracker.AdvanceOneFrame(full.Frames[i]);
        }

        ulong before = EntitySetDigest.Compute(tracker.CurrentEntities);
        ulong again = EntitySetDigest.Compute(tracker.CurrentEntities);
        await Assert.That(again).IsEqualTo(before);

        tracker.AdvanceOneFrame(full.Frames[stopAt + 1]);
        ulong after = EntitySetDigest.Compute(tracker.CurrentEntities);
        await Assert.That(after).IsNotEqualTo(before).Because("a live frame changes at least one cell");
    }
}
