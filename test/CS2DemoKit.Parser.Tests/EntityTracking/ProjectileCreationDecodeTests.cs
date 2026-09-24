#region

using System.Globalization;
using System.Numerics;
using System.Text;
using CS2DemoKit.Parser.Entities;
using CS2DemoKit.Parser.EntityTracking;
using CS2DemoKit.TestSupport;

#endregion

namespace CS2DemoKit.Parser.Tests.EntityTracking;

/// <summary>
///     A grenade projectile's state on its creation frame matches its throw (#56). Through 0.12.0 a
///     smoke's instancebaseline (3,214 to 3,482 field paths, almost all <c>m_VoxelFrameData</c>)
///     was cut off at 2,048 paths, so every field the creation packet did not re-send kept a
///     garbage value: the cell (thousands of units from <c>m_vInitialPosition</c>), the team, the
///     bounce count and the thrower. These walk the committed sample, so they always run.
/// </summary>
[Category("Integration")]
[NotInParallel]
public class ProjectileCreationDecodeTests
{
    /// <summary>Projectile counts on the sample, one per class it carries.</summary>
    private static readonly Dictionary<string, int> SampleCounts = new(StringComparer.Ordinal)
    {
        ["CHEGrenadeProjectile"] = 4,
        ["CFlashbangProjectile"] = 5,
        ["CMolotovProjectile"] = 4,
        ["CSmokeGrenadeProjectile"] = 5
    };

    [Test]
    public async Task Sample_SmokeCreationStateMatchesItsThrow()
    {
        ProjectileTally tally = RetainedSampleTally();
        Console.WriteLine(tally.Describe());

        await Assert.That(tally.Error).IsNull();
        ProjectileClassTally smokes = tally.ByClass["CSmokeGrenadeProjectile"];
        await Assert.That(smokes.Created).IsEqualTo(5);
        await Assert.That(smokes.InFlight).IsEqualTo(5).Because("every sample smoke is thrown after the signon");
        await Assert.That(smokes.FarFromThrow).IsEqualTo(0);
        await Assert.That(smokes.BadTeam).IsEqualTo(0);
        await Assert.That(smokes.UnresolvedThrower).IsEqualTo(0);
        await Assert.That(tally.SmokeTickBeginWithoutEffect).IsEqualTo(0L);
    }

    [Test]
    public async Task Sample_EveryProjectileClass_HoldsTheSameInvariants()
    {
        ProjectileTally tally = RetainedSampleTally();
        Console.WriteLine(tally.Describe());

        await Assert.That(tally.Error).IsNull();
        foreach ((string cls, int count) in SampleCounts)
        {
            await Assert.That(tally.ByClass.ContainsKey(cls)).IsTrue().Because($"{cls} is on the sample");
            ProjectileClassTally t = tally.ByClass[cls];
            await Assert.That(t.Created).IsEqualTo(count).Because($"{cls} count");
            await Assert.That(t.InFlight).IsEqualTo(count).Because($"{cls} created in flight");
            await Assert.That(t.Violations).IsEqualTo(0).Because($"{cls} creation state");
        }
    }

    /// <summary>
    ///     The forward reader decodes through the same field-path loop as the retained parse, so a
    ///     reader-fed tally is the retained tally exactly, projectile by projectile.
    /// </summary>
    [Test]
    public async Task Sample_ForwardAndRetainedPathsAgree()
    {
        string path = DemoTestHelper.RequireDemo(DemoTestHelper.SampleDemoFileName);
        ProjectileTally retained = RetainedSampleTally();

        byte[] bytes = await File.ReadAllBytesAsync(path);
        ProjectileTally forward;
        using (DemoReader reader = DemoReader.Open(bytes.AsMemory(), new ParseOptions { Plan = DecodePlan.EntityReplay }))
        {
            forward = ProjectileTally.Walk(ProjectileTally.ReadAll(reader));
        }

        await Assert.That(forward.Error).IsNull();
        await Assert.That(forward.Describe()).IsEqualTo(retained.Describe());
    }

    private static ProjectileTally RetainedSampleTally()
    {
        string path = DemoTestHelper.RequireDemo(DemoTestHelper.SampleDemoFileName);
        ParsedDemo demo = DemoTestHelper.GetOrParse(path);
        return ProjectileTally.Walk(demo.Frames);
    }
}

/// <summary>Per-class creation-state checks over a walk.</summary>
internal sealed class ProjectileClassTally
{
    public int Created { get; set; }

    /// <summary>Created after the initial snapshot with <c>m_nBounces</c> 0: a fresh throw.</summary>
    public int InFlight { get; set; }

    public int FarFromThrow { get; set; }
    public int BadTeam { get; set; }
    public int UnresolvedThrower { get; set; }
    public int Violations => FarFromThrow + BadTeam + UnresolvedThrower;
}

/// <summary>
///     Replays frames through a curated tracker and reads every grenade projectile once its
///     creation frame has been processed. A fresh throw (created after the initial snapshot, with
///     <c>m_nBounces</c> 0) must sit within <see cref="MaxThrowOffset" /> of
///     <c>m_vInitialPosition</c>, carry team 2 or 3, and name a live <c>CCSPlayerPawn</c> as its
///     thrower. Projectiles already in flight when the demo starts legitimately fail the position
///     check, so they are counted but not checked. Every frame also counts smokes whose
///     <c>m_nSmokeEffectTickBegin</c> is set while <c>m_bDidSmokeEffect</c> is not, which is how a
///     garbage baseline made flying smokes count as clouds.
/// </summary>
internal sealed class ProjectileTally
{
    /// <summary>Measured 13 to 15 units on the sample: the throw offset from the eye.</summary>
    public const float MaxThrowOffset = 64f;

    private const string Smoke = "CSmokeGrenadeProjectile";

    public static readonly string[] Classes =
    [
        "CHEGrenadeProjectile", "CFlashbangProjectile", "CMolotovProjectile", Smoke, "CDecoyProjectile"
    ];

    private readonly List<string> _lines = [];

    public SortedDictionary<string, ProjectileClassTally> ByClass { get; } = new(StringComparer.Ordinal);
    public long SmokeTickBeginWithoutEffect { get; private set; }
    public string? Error { get; private set; }

    /// <summary>The largest per-update field-path count the tracker decoded.</summary>
    public int MaxFieldPathCount { get; private set; }

    public static ProjectileTally Walk(IEnumerable<DemoFrame> frames)
    {
        ProjectileTally tally = new();
        EntityTracker tracker = EntityTrackerFactory.CreateCurated();
        HashSet<string> classes = new(Classes, StringComparer.Ordinal);
        List<int> pending = [];
        tracker.EntityCreated += (index, state) =>
        {
            if (classes.Contains(state.ClassName))
            {
                pending.Add(index);
            }
        };

        bool seeded = false;
        foreach (DemoFrame frame in frames)
        {
            tracker.AdvanceOneFrame(frame);
            foreach (int index in pending)
            {
                if (tracker.CurrentEntities[index] is { } projectile && classes.Contains(projectile.ClassName))
                {
                    tally.Record(tracker, frame.FrameNumber, index, projectile, seeded);
                }
            }

            pending.Clear();
            seeded |= tracker.CurrentEntities.All().Any();

            foreach (EntityState entity in tracker.CurrentEntities.All())
            {
                if (entity.ClassName == Smoke
                    && Long(entity["m_nSmokeEffectTickBegin"]) > 0
                    && Long(entity["m_bDidSmokeEffect"]) == 0)
                {
                    tally.SmokeTickBeginWithoutEffect++;
                }
            }
        }

        tally.Error = tracker.LastEntityError;
        tally.MaxFieldPathCount = tracker.MaxFieldPathCountForTest;
        return tally;
    }

    public static IEnumerable<DemoFrame> ReadAll(DemoReader reader)
    {
        while (reader.TryReadNext(out DemoFrame? frame))
        {
            yield return frame;
        }
    }

    private void Record(EntityTracker tracker, int frame, int index, EntityState projectile, bool seeded)
    {
        if (!ByClass.TryGetValue(projectile.ClassName, out ProjectileClassTally? t))
        {
            t = new ProjectileClassTally();
            ByClass[projectile.ClassName] = t;
        }

        t.Created++;
        long bounces = Long(projectile["m_nBounces"]);
        long team = Long(projectile["m_iTeamNum"]);
        int thrower = unchecked((int)Long(projectile["m_hThrower"]));
        Vector3? world = PositionUtil.CellToWorld(projectile);
        Vector3? initial = projectile["m_vInitialPosition"] as Vector3?;
        float distance = world is { } w && initial is { } i ? Vector3.Distance(w, i) : float.NaN;
        EntityHandle handle = EntityHandle.FromRaw(thrower);
        EntityState? pawn = thrower != 0 && handle.IsValid ? tracker.CurrentEntities[handle.Index] : null;
        bool throwerOk = pawn is { ClassName: "CCSPlayerPawn" };

        bool inFlight = seeded && bounces == 0;
        if (inFlight)
        {
            t.InFlight++;
            if (!(distance <= MaxThrowOffset))
            {
                t.FarFromThrow++;
            }

            if (team is not (2 or 3))
            {
                t.BadTeam++;
            }

            if (!throwerOk)
            {
                t.UnresolvedThrower++;
            }
        }

        _lines.Add(string.Create(CultureInfo.InvariantCulture,
            $"{projectile.ClassName} frame={frame} idx={index} inFlight={inFlight} dist={distance:F1} " +
            $"team={team} bounces={bounces} thrower={(throwerOk ? handle.Index : -1)}"));
    }

    /// <summary>A stable rendering of the whole tally, one line per class then one per projectile.</summary>
    public string Describe()
    {
        StringBuilder sb = new();
        sb.Append("smokeTickBeginWithoutEffect=").Append(SmokeTickBeginWithoutEffect).AppendLine();
        foreach ((string cls, ProjectileClassTally t) in ByClass)
        {
            sb.Append(cls).Append(" created=").Append(t.Created).Append(" inFlight=").Append(t.InFlight)
                .Append(" far=").Append(t.FarFromThrow).Append(" badTeam=").Append(t.BadTeam)
                .Append(" unresolvedThrower=").Append(t.UnresolvedThrower).AppendLine();
        }

        foreach (string line in _lines)
        {
            sb.AppendLine(line);
        }

        return sb.ToString();
    }

    private static long Long(object? value) => value switch
    {
        null => 0,
        bool b => b ? 1 : 0,
        IConvertible c => c.ToInt64(CultureInfo.InvariantCulture),
        _ => 0
    };
}
