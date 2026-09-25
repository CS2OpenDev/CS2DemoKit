#region

using System.Numerics;
using CS2DemoKit.Parser.EntityTracking;
using CS2DemoKit.TestSupport;

#endregion

namespace CS2DemoKit.Parser.Tests.EntityTracking;

/// <summary>
///     Issue #59's projectile walk over two full matchmaking demos, the build-10231 de_nuke and the
///     build-10924 de_dust2 the issue's probes measured. Both live in the local corpus only (point
///     <c>CS2DEMOKIT_CORPUS_DIR</c> at the Steam replays folder), and the tests skip cleanly without
///     them. The pinned counts are those demos' own and would move with different bytes.
/// </summary>
[Category("Corpus")]
[NotInParallel]
public class ProjectileSamplerCorpusTests
{
    private const string Nuke = "match730_003731893271710924851_1024675027_129.dem";
    private const string Dust2 = "match730_003844470418295488702_1553410689_408.dem";

    public static IEnumerable<Func<Expected>> Demos()
    {
        yield return () => new Expected(Nuke, Smoke: 57, Flashbang: 54, Molotov: 62, HEGrenade: 54, Decoy: 2);
        yield return () => new Expected(Dust2, Smoke: 72, Flashbang: 109, Molotov: 72, HEGrenade: 91, Decoy: 8);
    }

    /// <summary>
    ///     Whole-match lifecycle: the per-class creation counts, one Created and one Removed per
    ///     projectile with a sample on every frame between, none still alive at the end, the thrower
    ///     resolved on every creation, and each thrown projectile's release point a throw's offset
    ///     from that player's pawn and from its own first position.
    /// </summary>
    [Test]
    [MethodDataSource(nameof(Demos))]
    public async Task Walk_HoldsTheLifecycleOverAWholeMatch(Expected expected)
    {
        ParsedDemo demo = DemoTestHelper.GetOrParse(DemoTestHelper.RequireDemo(expected.File));
        List<ProjectileSample> samples = ProjectileSampler.Walk(demo).ToList();

        Dictionary<string, int> created = ProjectileSamplerTests.CreatedByClass(samples);
        Console.WriteLine($"[projectiles] {expected.File}: "
                          + string.Join(", ", created.Select(kv => $"{kv.Key} {kv.Value}")));
        await Assert.That(created.GetValueOrDefault(GrenadeProjectileClasses.Smoke)).IsEqualTo(expected.Smoke);
        await Assert.That(created.GetValueOrDefault(GrenadeProjectileClasses.Flashbang)).IsEqualTo(expected.Flashbang);
        await Assert.That(created.GetValueOrDefault(GrenadeProjectileClasses.Molotov)).IsEqualTo(expected.Molotov);
        await Assert.That(created.GetValueOrDefault(GrenadeProjectileClasses.HEGrenade)).IsEqualTo(expected.HEGrenade);
        await Assert.That(created.GetValueOrDefault(GrenadeProjectileClasses.Decoy)).IsEqualTo(expected.Decoy);

        await ProjectileSamplerTests.AssertLifecycle(samples, true);

        List<ProjectileSample> creations = samples.Where(s => s.Created).ToList();
        await Assert.That(creations.Count(s => s.ThrowerSlot < 0)).IsEqualTo(0)
            .Because("the thrower resolves at creation on every class, smokes included since #56");

        int firstEntityFrame = ProjectileSamplerTests.FirstEntityFrame(demo.Frames);
        List<ProjectileSample> thrown = creations.Where(s => s.FrameIndex > firstEntityFrame).ToList();

        List<float> distances = ProjectileSamplerTests.ThrowerDistances(demo, thrown);
        Console.WriteLine($"[projectiles] thrower distance {distances.Min():F1} to {distances.Max():F1} "
                          + $"over {distances.Count} of {thrown.Count}");
        await Assert.That(distances.Count).IsEqualTo(thrown.Count);
        await Assert.That(distances.Max()).IsLessThan(ProjectileSamplerTests.ThrowerDistanceBound);

        float worst = 0;
        foreach (ProjectileSample s in thrown)
        {
            await Assert.That(s.Position is not null && s.InitialPosition is not null).IsTrue()
                .Because($"{s.ClassName} #{s.EntityIndex} at frame {s.FrameIndex}");
            float d = Vector3.Distance(s.Position!.Value, s.InitialPosition!.Value);
            worst = Math.Max(worst, d);
            await Assert.That(d).IsLessThan(ProjectileSamplerTests.FirstPositionBound)
                .Because($"{s.ClassName} #{s.EntityIndex} at frame {s.FrameIndex} starts {d:F1} units from its release point");
        }

        Console.WriteLine($"[projectiles] first position at most {worst:F1} from the release point");
    }

    /// <summary>
    ///     The walk matches the full-scan oracle over a whole match, including the smoke slots a
    ///     prop or particle system takes on the frame the smoke goes: each gives the smoke's Removed
    ///     sample and nothing else for that slot.
    /// </summary>
    [Test]
    public async Task Walk_MatchesTheOracleThroughSameFrameSlotReuse()
    {
        ParsedDemo demo = DemoTestHelper.GetOrParse(DemoTestHelper.RequireDemo(Nuke));

        List<ProjectileSample> packaged = ProjectileSampler.Walk(demo).ToList();
        List<ProjectileSample> oracle = ProjectileSamplerTests.Oracle(demo.Frames, out int reoccupied);

        Console.WriteLine($"[projectiles] {reoccupied} removals into an occupied slot");
        await Assert.That(reoccupied).IsEqualTo(9);
        await Assert.That(packaged.Count).IsEqualTo(oracle.Count);
        await Assert.That(packaged.SequenceEqual(oracle)).IsTrue();
    }

    [Test]
    public async Task Walk_OverTheForwardReader_MatchesTheRetainedWalk()
    {
        string path = DemoTestHelper.RequireDemo(Nuke);
        byte[] bytes = await File.ReadAllBytesAsync(path);

        List<ProjectileSample> retained = ProjectileSampler.Walk(DemoTestHelper.GetOrParse(path)).ToList();

        using DemoReader reader = DemoReader.Open(bytes.AsMemory(), new ParseOptions { Plan = DecodePlan.EntityReplay });
        List<ProjectileSample> forward = ProjectileSampler.Walk(reader).ToList();

        await Assert.That(forward.Count).IsEqualTo(retained.Count);
        await Assert.That(forward.SequenceEqual(retained)).IsTrue();
    }

    /// <summary>
    ///     The dust2 recording starts with five decoys already in the air. They come out as Created
    ///     on the first entity frame, far from where they were thrown, which is why the geometry
    ///     checks above skip that frame and why a consumer must not read every Created as a throw.
    /// </summary>
    [Test]
    public async Task Walk_ProjectilesInFlightAtRecordingStartAreCreatedFarFromTheirRelease()
    {
        ParsedDemo demo = DemoTestHelper.GetOrParse(DemoTestHelper.RequireDemo(Dust2));
        int firstEntityFrame = ProjectileSamplerTests.FirstEntityFrame(demo.Frames);

        List<ProjectileSample> atStart = ProjectileSampler.Walk(demo, maxFrames: firstEntityFrame + 1)
            .Where(s => s.Created)
            .ToList();

        Console.WriteLine($"[projectiles] first entity frame {firstEntityFrame}: " + string.Join(", ",
            atStart.Select(s => $"{s.ClassName} {Vector3.Distance(s.Position!.Value, s.InitialPosition!.Value):F0}")));
        await Assert.That(firstEntityFrame).IsEqualTo(15);
        await Assert.That(atStart.Count).IsEqualTo(5);
        await Assert.That(atStart.All(s => s.ClassName == GrenadeProjectileClasses.Decoy)).IsTrue();
        await Assert.That(atStart.All(s => s.FrameIndex == firstEntityFrame)).IsTrue();
        await Assert.That(atStart.All(s => Vector3.Distance(s.Position!.Value, s.InitialPosition!.Value) > 1000f)).IsTrue()
            .Because("these were thrown before the recording began");
    }

    public sealed record Expected(string File, int Smoke, int Flashbang, int Molotov, int HEGrenade, int Decoy)
    {
        public override string ToString() => File;
    }
}
