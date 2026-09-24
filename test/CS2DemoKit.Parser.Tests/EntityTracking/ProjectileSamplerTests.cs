#region

using System.Numerics;
using CS2DemoKit.Parser.Entities;
using CS2DemoKit.Parser.EntityTracking;
using CS2DemoKit.TestSupport;

#endregion

namespace CS2DemoKit.Parser.Tests.EntityTracking;

/// <summary>
///     Issue #59: <see cref="ProjectileSampler" /> on the committed sample demo. The walk must match
///     a hand-rolled oracle that scans the whole entity set every frame, keep one Created and one
///     Removed per projectile, give the same stream through the forward reader, and land each
///     projectile's first position and thrower where the geometry says they are.
/// </summary>
[Category("Integration")]
[NotInParallel]
public class ProjectileSamplerTests
{
    /// <summary>
    ///     Throw offset bound: <c>m_vInitialPosition</c> sits 35 to 80 units from the thrower's pawn
    ///     origin on every demo measured, so 128 separates a right thrower from a wrong one.
    /// </summary>
    internal const float ThrowerDistanceBound = 128f;

    /// <summary>
    ///     First-cell bound: a projectile's first reconstructed position sits within 17 units of
    ///     <c>m_vInitialPosition</c> on every class once the smoke creation decode is right (#56).
    /// </summary>
    internal const float FirstPositionBound = 32f;

    [Test]
    public async Task Walk_CountsOneLifePerProjectile()
    {
        List<ProjectileSample> samples = ProjectileSampler.Walk(Demo()).ToList();

        Dictionary<string, int> created = CreatedByClass(samples);
        Console.WriteLine("[projectiles] created " + string.Join(", ", created.Select(kv => $"{kv.Key} {kv.Value}")));

        await Assert.That(created.GetValueOrDefault(GrenadeProjectileClasses.Molotov)).IsEqualTo(4);
        await Assert.That(created.GetValueOrDefault(GrenadeProjectileClasses.HEGrenade)).IsEqualTo(4);
        await Assert.That(created.GetValueOrDefault(GrenadeProjectileClasses.Flashbang)).IsEqualTo(5);
        await Assert.That(created.GetValueOrDefault(GrenadeProjectileClasses.Smoke)).IsEqualTo(5);
        await Assert.That(created.GetValueOrDefault(GrenadeProjectileClasses.Decoy)).IsEqualTo(0);

        await AssertLifecycle(samples, true);
    }

    /// <summary>
    ///     Stride thins in-flight samples only. Lifecycle samples bypass it, and values are read on
    ///     every frame, so the strided walk is exactly the full walk filtered.
    /// </summary>
    [Test]
    public async Task Walk_WithStride_IsTheFullWalkFilteredKeepingLifecycle()
    {
        ParsedDemo demo = Demo();
        const int stride = 8;

        List<ProjectileSample> full = ProjectileSampler.Walk(demo).ToList();
        List<ProjectileSample> strided = ProjectileSampler.Walk(demo, stride).ToList();
        List<ProjectileSample> expected = full
            .Where(s => s.FrameIndex % stride == 0 || s.Created || s.Removed)
            .ToList();

        await Assert.That(strided.Count).IsEqualTo(expected.Count);
        await Assert.That(strided.SequenceEqual(expected)).IsTrue();
        await Assert.That(strided.Count).IsLessThan(full.Count);
    }

    /// <summary>
    ///     The walk tracks slots through <see cref="EntityTracker.EntityCreated" /> and never scans
    ///     the entity set. An oracle that does scan it every frame must see the same stream, which
    ///     is what shows no projectile slips past the event.
    /// </summary>
    [Test]
    public async Task Walk_MatchesAFullScanOracle()
    {
        ParsedDemo demo = Demo();

        List<ProjectileSample> packaged = ProjectileSampler.Walk(demo).ToList();
        List<ProjectileSample> oracle = Oracle(demo.Frames);

        await Assert.That(packaged.Count).IsEqualTo(oracle.Count);
        for (int i = 0; i < oracle.Count; i++)
        {
            await Assert.That(packaged[i]).IsEqualTo(oracle[i]).Because($"sample #{i}");
        }
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task Walk_OverTheForwardReader_MatchesTheRetainedWalk(bool narrowed)
    {
        string path = DemoTestHelper.RequireDemo(DemoTestHelper.SampleDemoFileName);
        byte[] bytes = await File.ReadAllBytesAsync(path);

        List<ProjectileSample> retained = ProjectileSampler.Walk(DemoTestHelper.GetOrParse(path)).ToList();

        ParseOptions options = new() { Plan = narrowed ? DecodePlan.EntityReplay : DecodePlan.Everything };
        using DemoReader reader = DemoReader.Open(bytes.AsMemory(), options);
        List<ProjectileSample> forward = ProjectileSampler.Walk(reader).ToList();

        await Assert.That(forward.Count).IsEqualTo(retained.Count);
        await Assert.That(forward.SequenceEqual(retained)).IsTrue();
    }

    /// <summary>
    ///     Every projectile's thrower resolves on its Created sample, and the release point sits a
    ///     throw's offset from that player's pawn at the same frame.
    /// </summary>
    [Test]
    public async Task Walk_ResolvesTheThrowerAtCreation()
    {
        ParsedDemo demo = Demo();
        List<ProjectileSample> created = ProjectileSampler.Walk(demo).Where(s => s.Created).ToList();

        await Assert.That(created.All(s => s.ThrowerSlot is >= 0 and < 64)).IsTrue()
            .Because("the thrower chain resolves at creation on every class once #56 is fixed");

        List<float> distances = ThrowerDistances(demo, created);
        Console.WriteLine($"[projectiles] thrower distance {distances.Min():F1} to {distances.Max():F1}");
        await Assert.That(distances.Count).IsEqualTo(created.Count);
        await Assert.That(distances.Max()).IsLessThan(ThrowerDistanceBound);
    }

    /// <summary>
    ///     A projectile's first position is its release point. Smokes pass only because their
    ///     creation packet now decodes (#56); before that their first cell was thousands of units off.
    /// </summary>
    [Test]
    public async Task Walk_FirstPositionIsTheReleasePoint()
    {
        ParsedDemo demo = Demo();
        int firstEntityFrame = FirstEntityFrame(demo.Frames);
        List<ProjectileSample> created = ProjectileSampler.Walk(demo)
            .Where(s => s.Created && s.FrameIndex > firstEntityFrame)
            .ToList();

        await Assert.That(created.Count).IsGreaterThan(0);
        foreach (ProjectileSample s in created)
        {
            await Assert.That(s.Position).IsNotNull().Because($"{s.ClassName} #{s.EntityIndex}");
            await Assert.That(s.InitialPosition).IsNotNull().Because($"{s.ClassName} #{s.EntityIndex}");
            float d = Vector3.Distance(s.Position!.Value, s.InitialPosition!.Value);
            await Assert.That(d).IsLessThan(FirstPositionBound)
                .Because($"{s.ClassName} #{s.EntityIndex} at frame {s.FrameIndex} starts {d:F1} units from its release point");
        }
    }

    [Test]
    public void Walk_ValidatesArgumentsAtTheCall()
    {
        ParsedDemo demo = Demo();

        // Never enumerated: validation belongs at the call, not at the first MoveNext.
        Assert.Throws<ArgumentOutOfRangeException>(() => ProjectileSampler.Walk(demo, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => ProjectileSampler.Walk(demo, 1, -1));
        Assert.Throws<ArgumentNullException>(() => ProjectileSampler.Walk((ParsedDemo)null!));
    }

    [Test]
    public async Task Walk_RejectsAReaderThatHasAlreadyRead()
    {
        string path = DemoTestHelper.RequireDemo(DemoTestHelper.SampleDemoFileName);
        byte[] bytes = await File.ReadAllBytesAsync(path);

        using DemoReader fresh = DemoReader.Open(bytes.AsMemory());
        Assert.Throws<ArgumentOutOfRangeException>(() => ProjectileSampler.Walk(fresh, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => ProjectileSampler.Walk(fresh, 1, -1));
        await Assert.That(fresh.Started).IsFalse().Because("a rejected call must not read the source");

        using DemoReader started = DemoReader.Open(bytes.AsMemory());
        await Assert.That(started.TryReadNext(out _)).IsTrue();
        Assert.Throws<InvalidOperationException>(() => ProjectileSampler.Walk(started));
    }

    [Test]
    public async Task Walk_StopsAtMaxFramesWithoutReadingPastIt()
    {
        string path = DemoTestHelper.RequireDemo(DemoTestHelper.SampleDemoFileName);
        byte[] bytes = await File.ReadAllBytesAsync(path);
        ParsedDemo demo = DemoTestHelper.GetOrParse(path);

        List<ProjectileSample> full = ProjectileSampler.Walk(demo).ToList();
        int cut = full.First(s => !s.Created && !s.Removed).FrameIndex + 1;
        List<ProjectileSample> head = ProjectileSampler.Walk(demo, maxFrames: cut).ToList();

        await Assert.That(head.SequenceEqual(full.TakeWhile(s => s.FrameIndex < cut))).IsTrue();

        using DemoReader reader = DemoReader.Open(bytes.AsMemory());
        await Assert.That(ProjectileSampler.Walk(reader, maxFrames: 0).Any()).IsFalse();
        await Assert.That(reader.Started).IsFalse().Because("maxFrames 0 must not read a frame");
    }

    // ---- shared with the corpus tests ----

    internal static Dictionary<string, int> CreatedByClass(IEnumerable<ProjectileSample> samples) =>
        samples.Where(s => s.Created)
            .GroupBy(s => s.ClassName)
            .ToDictionary(g => g.Key, g => g.Count());

    /// <summary>
    ///     One Created and at most one Removed per <c>(EntityIndex, Serial)</c>, Created first,
    ///     Removed last, and (at stride 1) a sample on every frame in between.
    /// </summary>
    internal static async Task AssertLifecycle(List<ProjectileSample> samples, bool allRemoved)
    {
        foreach (IGrouping<(int, int), ProjectileSample> life in samples.GroupBy(s => (s.EntityIndex, s.Serial)))
        {
            List<ProjectileSample> l = life.ToList();
            await Assert.That(l.Count(s => s.Created)).IsEqualTo(1).Because($"{life.Key} is created once");
            await Assert.That(l[0].Created).IsTrue().Because($"{life.Key} starts with its Created sample");
            int removed = l.Count(s => s.Removed);
            await Assert.That(removed).IsLessThanOrEqualTo(1);
            if (allRemoved)
            {
                await Assert.That(removed).IsEqualTo(1).Because($"{life.Key} is removed before the demo ends");
            }

            if (removed == 1)
            {
                await Assert.That(l[^1].Removed).IsTrue().Because($"{life.Key} ends with its Removed sample");
            }

            for (int i = 1; i < l.Count; i++)
            {
                await Assert.That(l[i].FrameIndex).IsEqualTo(l[i - 1].FrameIndex + 1)
                    .Because($"{life.Key} is sampled on every frame of its life");
            }
        }
    }

    /// <summary>
    ///     The index of the first frame after which the tracker holds any entity: where projectiles
    ///     already in flight when the recording began first appear.
    /// </summary>
    internal static int FirstEntityFrame(IReadOnlyList<DemoFrame> frames)
    {
        EntityTracker tracker = EntityTrackerFactory.CreateCurated();
        for (int i = 0; i < frames.Count; i++)
        {
            tracker.AdvanceOneFrame(frames[i]);
            if (tracker.CurrentEntities.AllIndexed().Any())
            {
                return i;
            }
        }

        return frames.Count;
    }

    /// <summary>
    ///     For each Created sample with a resolved thrower, the distance from its release point to
    ///     that player's pawn, both at the creation frame.
    /// </summary>
    internal static List<float> ThrowerDistances(ParsedDemo demo, IReadOnlyCollection<ProjectileSample> created)
    {
        HashSet<int> frames = created.Select(s => s.FrameIndex).ToHashSet();
        Dictionary<(int, int), Vector3> pawns = PositionSampler.Walk(demo)
            .Where(p => frames.Contains(p.FrameIndex))
            .ToDictionary(p => (p.FrameIndex, p.PlayerSlot), p => p.Position);

        List<float> distances = [];
        foreach (ProjectileSample s in created)
        {
            if (s.ThrowerSlot >= 0 && s.InitialPosition is { } init
                                   && pawns.TryGetValue((s.FrameIndex, s.ThrowerSlot), out Vector3 pawn))
            {
                distances.Add(Vector3.Distance(init, pawn));
            }
        }

        return distances;
    }

    /// <summary>
    ///     The walk written out the slow way: scan every live entity each frame, match projectiles
    ///     by <c>(index, serial, class)</c>, and emit Removed from the previous frame's values.
    /// </summary>
    internal static List<ProjectileSample> Oracle(IReadOnlyList<DemoFrame> frames)
    {
        EntityTracker tracker = EntityTrackerFactory.CreateCurated();
        Dictionary<int, (int Serial, string Class, int Thrower, ProjectileSample Last)> live = [];
        List<ProjectileSample> output = [];

        for (int i = 0; i < frames.Count; i++)
        {
            tracker.AdvanceOneFrame(frames[i]);
            int tick = frames[i].ServerTick;
            List<ProjectileSample> frame = [];

            Dictionary<int, EntityState> now = tracker.CurrentEntities.AllIndexed()
                .Where(t => GrenadeProjectileClasses.Contains(t.Entity.ClassName))
                .ToDictionary(t => t.Index, t => t.Entity);

            foreach ((int index, (int serial, string cls, int _, ProjectileSample last)) in live.ToList())
            {
                if (!now.TryGetValue(index, out EntityState? e) || e.Serial != serial || e.ClassName != cls)
                {
                    frame.Add(last with { FrameIndex = i, Tick = tick, Created = false, Removed = true });
                    live.Remove(index);
                }
            }

            foreach ((int index, EntityState e) in now)
            {
                bool created = !live.TryGetValue(index, out var entry);
                int thrower = created ? -1 : entry.Thrower;
                if (thrower < 0)
                {
                    thrower = PawnLookup.ResolveThrowerSlot(tracker, e);
                }

                Vector3? position = null;
                if (PositionUtil.CellToWorld(e) is { } p
                    && e.TryGet<int>("CBodyComponent.m_cellX") is not 0
                    && e.TryGet<int>("CBodyComponent.m_cellY") is not 0
                    && e.TryGet<int>("CBodyComponent.m_cellZ") is not 0)
                {
                    position = p;
                }

                ProjectileSample s = new(i, tick, index, e.Serial, e.ClassName, thrower, position,
                    e.TryGet<Vector3>("m_vInitialPosition"), e.TryGet<Vector3>("m_vInitialVelocity"),
                    e.TryGet<int>("m_nBounces") ?? 0, created, false);
                live[index] = (e.Serial, e.ClassName, thrower, s);
                frame.Add(s);
            }

            output.AddRange(frame.OrderBy(s => s.EntityIndex).ThenBy(s => s.Removed ? 0 : 1));
        }

        return output;
    }

    private static ParsedDemo Demo() =>
        DemoTestHelper.GetOrParse(DemoTestHelper.RequireDemo(DemoTestHelper.SampleDemoFileName));
}
