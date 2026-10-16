#region

using CS2DemoKit.Parser.EntityTracking;
using CS2DemoKit.TestSupport;

#endregion

namespace CS2DemoKit.Parser.Tests.EntityTracking;

/// <summary>
///     Sequential playback across a dropped snapshot, on the tournament demos that drop one (each
///     loses the <c>cs_intermission</c> tick's <c>PacketEntities</c>). At the first full packet after
///     the break the sequential tracker must hold exactly what a tracker primed from that full packet
///     holds, the way a parallel chunk starting there is primed, and the two must stay equal to the
///     end with no delta landing on an unknown entity. Inferno and the fut mirage drop their snapshot
///     after the last full packet, so nothing can resync them and the test checks only that no
///     resync is invented. Skips when the demo is not under <c>CS2DEMOKIT_CORPUS_DIR</c>.
/// </summary>
[NotInParallel]
[Category("Corpus")]
public class SnapshotResyncCorpusTests
{
    [Test]
    [Arguments("furia-vs-vitality-m1-mirage.dem", 249643, 13)]
    [Arguments("furia-vs-vitality-m2-inferno.dem", -1, -1)]
    [Arguments("furia-vs-vitality-m3-nuke.dem", 123087, 13)]
    [Arguments("furia-vs-vitality-m4-overpass.dem", 217395, 13)]
    [Arguments("vitality-vs-fut-m1-mirage.dem", -1, -1)]
    [Arguments("vitality-vs-fut-m2-dust2.dem", 215127, 13)]
    [Arguments("vitality-vs-fut-m3-nuke.dem", 176722, 13)]
    public async Task SequentialResync_MatchesAPrimeFromTheSameFullPacket(string file, int resyncFrame, int leadingScore)
    {
        string path = DemoTestHelper.RequireDemo(file);
        byte[] bytes = await File.ReadAllBytesAsync(path);
        using DemoReader reader = DemoReader.Open(bytes.AsMemory(), new ParseOptions { Plan = DecodePlan.EntityReplay });

        EntityTracker sequential = new();
        EntityTracker? primed = null;
        List<DemoFrame> signonPrefix = [];
        bool prefixDone = false;
        int resyncedAt = -1;
        int unknownAtResync = 0;
        while (reader.TryReadNext(out DemoFrame? frame))
        {
            prefixDone |= frame.CommandKind == EDemoCommands.DemPacket;
            if (!prefixDone)
            {
                signonPrefix.Add(frame);
            }

            int resyncsBefore = sequential.SnapshotResyncCount;
            sequential.AdvanceOneFrame(frame);
            primed?.AdvanceOneFrame(frame);
            if (sequential.SnapshotResyncCount == resyncsBefore)
            {
                continue;
            }

            await Assert.That(resyncedAt).IsEqualTo(-1).Because("these demos drop one snapshot each");
            resyncedAt = frame.FrameNumber;
            unknownAtResync = sequential.DeltaUnknownCount;

            primed = new EntityTracker();
            foreach (DemoFrame signon in signonPrefix)
            {
                primed.AdvanceOneFrame(signon);
            }

            primed.ResetEntitiesKeepSchema();
            if (reader.LastInstanceBaselineFullPacket is { } baselines)
            {
                primed.LoadInstanceBaselineSnapshot(baselines);
            }

            primed.ProcessFullPacketCheckpoint(frame);
            await Assert.That(Diff(sequential, primed)).IsNull().Because($"at the resync, frame {frame.FrameNumber}");

            (int t, int ct) = Scores(sequential);
            Console.WriteLine($"{file}: resync at frame {frame.FrameNumber} (tick {frame.ServerTick}), "
                              + $"unknown-entity deltas before it {unknownAtResync}, score T {t} CT {ct}");
            if (leadingScore >= 0)
            {
                await Assert.That(Math.Max(t, ct)).IsEqualTo(leadingScore);
            }
        }

        (int endT, int endCt) = Scores(sequential);
        Console.WriteLine($"{file}: end score T {endT} CT {endCt}, unknown-entity deltas {sequential.DeltaUnknownCount}");
        await Assert.That(resyncedAt).IsEqualTo(resyncFrame);
        if (primed is not null)
        {
            await Assert.That(sequential.DeltaUnknownCount).IsEqualTo(unknownAtResync)
                .Because("no delta after the resync may land on an entity the tracker does not hold");
            await Assert.That(primed.DeltaUnknownCount).IsEqualTo(0);
            await Assert.That(Diff(sequential, primed)).IsNull().Because("at the last frame");
        }
    }

    private static (int T, int Ct) Scores(EntityTracker tracker)
    {
        int t = -1, ct = -1;
        foreach (EntityState team in tracker.CurrentEntities.All())
        {
            if (team.ClassName != "CCSTeam" || team["m_iScore"] is not { } score)
            {
                continue;
            }

            switch (Convert.ToInt32(team["m_iTeamNum"], System.Globalization.CultureInfo.InvariantCulture))
            {
                case 2:
                    t = Convert.ToInt32(score, System.Globalization.CultureInfo.InvariantCulture);
                    break;
                case 3:
                    ct = Convert.ToInt32(score, System.Globalization.CultureInfo.InvariantCulture);
                    break;
            }
        }

        return (t, ct);
    }

    private static string? Diff(EntityTracker a, EntityTracker b)
    {
        Dictionary<int, Dictionary<string, object?>> left = a.SnapshotCurrentFields();
        Dictionary<int, Dictionary<string, object?>> right = b.SnapshotCurrentFields();
        if (left.Count != right.Count)
        {
            return $"entity count {left.Count} vs {right.Count}";
        }

        foreach ((int index, Dictionary<string, object?> fields) in left)
        {
            if (!right.TryGetValue(index, out Dictionary<string, object?>? other))
            {
                return $"entity {index} only on the sequential tracker";
            }

            if (a.CurrentEntities[index]!.ClassName != b.CurrentEntities[index]!.ClassName)
            {
                return $"entity {index} class {a.CurrentEntities[index]!.ClassName} vs {b.CurrentEntities[index]!.ClassName}";
            }

            if (fields.Count != other.Count)
            {
                return $"entity {index} field count {fields.Count} vs {other.Count}";
            }

            foreach ((string key, object? value) in fields)
            {
                if (!other.TryGetValue(key, out object? otherValue) || !SameValue(value, otherValue))
                {
                    return $"entity {index} {key}: {value} vs {otherValue}";
                }
            }
        }

        return null;
    }

    private static bool SameValue(object? a, object? b) =>
        Equals(a, b)
        || (a is System.Collections.IEnumerable x && b is System.Collections.IEnumerable y && a is not string
            && x.Cast<object?>().SequenceEqual(y.Cast<object?>()));
}
