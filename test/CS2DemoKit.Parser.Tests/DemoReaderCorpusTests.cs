#region

using CS2DemoKit.Parser.Entities;
using CS2DemoKit.Parser.EntityTracking;
using CS2DemoKit.Parser.GameEvents;
using CS2DemoKit.TestSupport;
using CS2OpenSchema.Protos;

#endregion

namespace CS2DemoKit.Parser.Tests;

/// <summary>
///     The reader-versus-parse and digest checks over every demo in the local corpus. Explicit
///     because it replays each demo three times; run it before claiming the two paths agree.
/// </summary>
[Explicit]
[NotInParallel]
[Category("Integration")]
public class DemoReaderCorpusTests
{
    public static IEnumerable<string> Demos()
    {
        string? sample = DemoTestHelper.FindDemoPath(DemoTestHelper.SampleDemoFileName);
        if (sample is not null)
        {
            yield return sample;
        }

        string? root = RepoRoot();
        if (root is null || !Directory.Exists(Path.Combine(root, "demos")))
        {
            yield break;
        }

        foreach (string path in Directory.EnumerateFiles(Path.Combine(root, "demos"), "*.dem", SearchOption.AllDirectories)
                     .OrderBy(p => p, StringComparer.Ordinal))
        {
            yield return path;
        }
    }

    [Test]
    [MethodDataSource(nameof(Demos))]
    public async Task Reader_MatchesParse_AndReplaysTheSameWorld(string path)
    {
        byte[] bytes = await File.ReadAllBytesAsync(path);
        ParsedDemo full = DemoParser.Parse(bytes.AsMemory());

        // Frames, messages, events, roster and warnings through the reader under the full plan.
        int index = 0;
        int events = 0;
        using (DemoReader reader = DemoReader.Open(bytes.AsMemory()))
        {
            while (reader.TryReadNext(out DemoFrame? frame))
            {
                DemoFrame reference = full.Frames[index];
                await Assert.That(frame.CommandKind).IsEqualTo(reference.CommandKind);
                await Assert.That(frame.ServerTick).IsEqualTo(reference.ServerTick);
                await Assert.That(frame.UserCmdsPayloadCount).IsEqualTo(reference.UserCmdsPayloadCount);
                await Assert.That(frame.DecodedMessages.Count).IsEqualTo(reference.DecodedMessages.Count);
                for (int m = 0; m < reference.DecodedMessages.Count; m++)
                {
                    await Assert.That(frame.DecodedMessages[m].MessageTypeName).IsEqualTo(reference.DecodedMessages[m].MessageTypeName);
                    if (frame.DecodedMessages[m] is GameEventMessage gem)
                    {
                        GameEvent expected = full.AllGameEvents[events++];
                        await Assert.That(gem.DecodedEvent.Name).IsEqualTo(expected.Name);
                        await Assert.That(gem.DecodedEvent.GameTick).IsEqualTo(expected.GameTick);
                    }
                }

                index++;
            }

            await Assert.That(index).IsEqualTo(full.Frames.Count);
            await Assert.That(events).IsEqualTo(full.AllGameEvents.Count);
            await Assert.That(reader.EndReason).IsEqualTo(ReadEndReason.Stop);
            await Assert.That(reader.Enrichment.Players.Count).IsEqualTo(full.Players.Count);
            foreach ((int slot, PlayerInfo p) in full.Players)
            {
                await Assert.That(reader.Enrichment.Players[slot]).IsEqualTo(p);
            }

            await Assert.That(reader.Enrichment.TickCount).IsEqualTo(full.TickCount);
            await Assert.That(reader.Enrichment.Warnings.Count).IsEqualTo(full.Warnings.Count);
            await Assert.That(reader.Provenance.MessagesDecoded).IsEqualTo(full.Provenance.MessagesDecoded);
            await Assert.That(reader.Provenance.UserCmdsStored).IsEqualTo(full.Provenance.UserCmdsStored);
        }

        // The world, through the narrowed entity-replay plan, at every full packet and at the end.
        EntityTracker referenceTracker = EntityTrackerFactory.CreateCurated();
        List<ulong> referenceDigests = [];
        foreach (DemoFrame frame in full.Frames)
        {
            referenceTracker.AdvanceOneFrame(frame);
            if (frame.CommandKind == EDemoCommands.DemFullPacket)
            {
                referenceDigests.Add(EntitySetDigest.Compute(referenceTracker.CurrentEntities));
            }
        }

        EntityTracker streamed = EntityTrackerFactory.CreateCurated();
        List<ulong> streamedDigests = [];
        using (DemoReader reader = DemoReader.Open(bytes.AsMemory(), new ParseOptions { Plan = DecodePlan.EntityReplay }))
        {
            while (reader.TryReadNext(out DemoFrame? frame))
            {
                streamed.AdvanceOneFrame(frame);
                if (frame.CommandKind == EDemoCommands.DemFullPacket)
                {
                    streamedDigests.Add(EntitySetDigest.Compute(streamed.CurrentEntities));
                }
            }
        }

        await Assert.That(streamed.LastEntityError).IsEqualTo(referenceTracker.LastEntityError);
        await Assert.That(streamedDigests.Count).IsEqualTo(referenceDigests.Count);
        for (int i = 0; i < referenceDigests.Count; i++)
        {
            await Assert.That(streamedDigests[i]).IsEqualTo(referenceDigests[i]).Because($"full packet #{i} of {Path.GetFileName(path)}");
        }

        await Assert.That(EntitySetDigest.Compute(streamed.CurrentEntities))
            .IsEqualTo(EntitySetDigest.Compute(referenceTracker.CurrentEntities));
        Console.WriteLine($"{Path.GetFileName(path)}: frames={index} events={events} fullPackets={referenceDigests.Count} digest={EntitySetDigest.Compute(streamed.CurrentEntities):x16}");
    }

    private static string? RepoRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "CS2DemoKit.slnx")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        return null;
    }
}
