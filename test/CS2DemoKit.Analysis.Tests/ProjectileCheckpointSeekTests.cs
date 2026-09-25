#region

using System.Globalization;
using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Parser;
using CS2DemoKit.Parser.EntityTracking;
using CS2DemoKit.TestSupport;
using CS2OpenSchema.Protos;
using TUnit.Core.Exceptions;

#endregion

namespace CS2DemoKit.Analysis.Tests;

/// <summary>
///     A layer primed from a <c>DEM_FullPacket</c> checkpoint decodes a smoke's instancebaseline the
///     same way a sequential replay does (#56). Through 0.12.0 that baseline was cut off at 2,048 of
///     its ~3,200 field paths, so every smoke carried a garbage cell, team, bounce count and thrower
///     on either path. A chunk worker reaches a smoke two ways: a smoke thrown after the checkpoint
///     is created from the baseline the checkpoint loaded, and a smoke live at the checkpoint is
///     re-created from the snapshot itself. Both are compared against sequential replay at the same
///     frame.
/// </summary>
[NotInParallel]
[Category("Integration")]
public class ProjectileCheckpointSeekTests
{
    private const string Smoke = "CSmokeGrenadeProjectile";

    /// <summary>
    ///     The sample's full packets fall every 3,840 frames and none lands while a smoke is live, so
    ///     it primes at the last one before the first smoke and seeks forward until two are live.
    /// </summary>
    [Test]
    public async Task Sample_SmokesThrownAfterACheckpointMatchSequentialReplay()
    {
        string path = DemoTestHelper.RequireDemo(DemoTestHelper.SampleDemoFileName);
        IReadOnlyList<DemoFrame> frames = DemoTestHelper.GetOrParse(path).Frames;

        EntityStateLayer sequential = new(frames);
        int checkpoint = -1;
        int target = -1;
        for (int i = 0; i < frames.Count - 1; i++)
        {
            sequential.Apply(frames[i]);
            if (IsCheckpoint(frames, i))
            {
                checkpoint = i;
            }

            if (LiveSmokes(sequential.Tracker) >= 2)
            {
                target = i;
                break;
            }
        }

        await Assert.That(target).IsGreaterThan(0).Because("two smokes are live at once on the sample");
        await Assert.That(checkpoint).IsGreaterThan(0);

        EntityStateLayer primed = Prime(frames, checkpoint);
        primed.SeekBeforeFrame(target + 1);

        await AssertSameSmokes(sequential.Tracker, primed.Tracker, $"checkpoint={checkpoint} target={target}");
    }

    public static IEnumerable<string> CorpusDemos()
    {
        string? dir = DemoTestHelper.CorpusDirectory();
        if (dir is null)
        {
            yield return "(no " + DemoTestHelper.CorpusDirectoryVariable + ")";
            yield break;
        }

        foreach (string demo in Directory.GetFiles(dir, "*.dem", SearchOption.TopDirectoryOnly)
                     .Order(StringComparer.OrdinalIgnoreCase).Take(3))
        {
            yield return demo;
        }
    }

    /// <summary>
    ///     A full match has full packets while smokes are live, where the checkpoint snapshot itself
    ///     re-creates each smoke through ENTERPVS over its baseline. Runs on the first three demos in
    ///     <c>CS2DEMOKIT_CORPUS_DIR</c> and skips without it.
    /// </summary>
    [Test]
    [MethodDataSource(nameof(CorpusDemos))]
    public async Task Corpus_CheckpointSnapshotReproducesSmokeState(string path)
    {
        if (!File.Exists(path))
        {
            throw new SkipTestException($"Demo '{path}' is not present on this machine.");
        }

        IReadOnlyList<DemoFrame> frames = DemoTestHelper.GetOrParse(path).Frames;
        EntityStateLayer sequential = new(frames);
        int checkpoint = -1;
        for (int i = 0; i < frames.Count - 1; i++)
        {
            sequential.Apply(frames[i]);
            if (IsCheckpoint(frames, i) && LiveSmokes(sequential.Tracker) > 0)
            {
                checkpoint = i;
                break;
            }
        }

        if (checkpoint < 0)
        {
            throw new SkipTestException($"'{Path.GetFileName(path)}' has no full packet with a smoke live.");
        }

        EntityStateLayer primed = Prime(frames, checkpoint);
        await AssertSameSmokes(sequential.Tracker, primed.Tracker, $"{Path.GetFileName(path)} checkpoint={checkpoint}");
    }

    private static bool IsCheckpoint(IReadOnlyList<DemoFrame> frames, int i) =>
        i > 0
        && frames[i].CommandKind == EDemoCommands.DemFullPacket
        && frames[i + 1].ServerTick != frames[i].ServerTick;

    private static int LiveSmokes(EntityTracker tracker) =>
        tracker.CurrentEntities.All().Count(e => e.ClassName == Smoke);

    private static EntityStateLayer Prime(IReadOnlyList<DemoFrame> frames, int checkpoint)
    {
        FrameListSource source = new(frames, null);
        while (source.TryReadNext(out DemoFrame? frame) && frame.FrameNumber < checkpoint)
        {
        }

        EntityStateLayer primed = new(frames);
        primed.PrimeFromCheckpoint(source.SignonPrefix, source.LastInstanceBaselineFullPacket,
            frames[checkpoint], frames[checkpoint + 1]);
        return primed;
    }

    private static async Task AssertSameSmokes(EntityTracker sequential, EntityTracker primed, string where)
    {
        List<string> expected = SmokeStates(sequential);
        List<string> actual = SmokeStates(primed);
        Console.WriteLine(where);
        foreach (string line in expected)
        {
            Console.WriteLine(line);
        }

        await Assert.That(sequential.LastEntityError).IsNull();
        await Assert.That(primed.LastEntityError).IsNull();
        await Assert.That(expected.Count).IsGreaterThan(0);
        await Assert.That(actual).IsEquivalentTo(expected);
        foreach ((int _, EntityState smoke) in sequential.CurrentEntities.AllIndexed())
        {
            if (smoke.ClassName != Smoke)
            {
                continue;
            }

            long team = Convert.ToInt64(smoke["m_iTeamNum"], CultureInfo.InvariantCulture);
            await Assert.That(team is 2 or 3).IsTrue().Because($"smoke team {team}");
            EntityHandle thrower = EntityHandle.FromRaw(
                unchecked((int)Convert.ToInt64(smoke["m_hThrower"], CultureInfo.InvariantCulture)));
            await Assert.That(thrower.IsValid && sequential.CurrentEntities[thrower.Index] is { ClassName: "CCSPlayerPawn" })
                .IsTrue().Because($"smoke thrower {thrower}");
        }
    }

    private static List<string> SmokeStates(EntityTracker tracker)
    {
        List<string> lines = [];
        foreach ((int index, EntityState entity) in tracker.CurrentEntities.AllIndexed())
        {
            if (entity.ClassName != Smoke)
            {
                continue;
            }

            lines.Add(string.Create(CultureInfo.InvariantCulture,
                $"idx={index} cell=({entity["CBodyComponent.m_cellX"]},{entity["CBodyComponent.m_cellY"]},{entity["CBodyComponent.m_cellZ"]}) " +
                $"team={entity["m_iTeamNum"]} thrower={entity["m_hThrower"]} bounces={entity["m_nBounces"]} " +
                $"tickBegin={entity["m_nSmokeEffectTickBegin"]} did={entity["m_bDidSmokeEffect"]}"));
        }

        return lines;
    }
}
