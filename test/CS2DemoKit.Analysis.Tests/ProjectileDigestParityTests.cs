#region

using System.Numerics;
using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Analysis.Observation;
using CS2DemoKit.Parser;
using CS2DemoKit.Parser.EntityTracking;
using CS2DemoKit.TestSupport;

#endregion

namespace CS2DemoKit.Analysis.Tests;

/// <summary>
///     Pins the projectile lifecycle the scanner derives from its digests to
///     <see cref="ProjectileSampler.Walk(ParsedDemo, int, int)" /> at stride 1, the public walk the
///     grenade consumers were built on: the same projectiles created and removed on the same
///     frames, the same live set with the same position and held thrower on every frame between.
///     At the sequential cap and at the default one, since the pipelined producer re-primes a
///     worker per chunk and the lifecycle is what hides that from the consumer.
/// </summary>
[Category("Integration")]
[NotInParallel]
public class ProjectileDigestParityTests
{
    private static readonly HashSet<string> AllClasses = [.. GrenadeProjectileClasses.All];

    /// <summary>One projectile on one frame, the fields both sides read.</summary>
    private readonly record struct Row(
        int FrameIndex, int EntityIndex, int Serial, string ClassName, int ThrowerSlot,
        Vector3? Position, Vector3? InitialPosition, Vector3? InitialVelocity, int Bounces);

    private sealed class Stream
    {
        public readonly List<Row> Created = [];
        public readonly List<Row> Removed = [];
        public readonly List<Row> Live = [];
    }

    [Test]
    public async Task Lifecycle_AtTheSequentialCap_MatchesTheSamplerWalk() => await AssertParity(1);

    [Test]
    public async Task Lifecycle_AtTheDefaultCap_MatchesTheSamplerWalk() => await AssertParity(null);

    private static async Task AssertParity(int? maxDegreeOfParallelism)
    {
        ParsedDemo demo = DemoTestHelper.GetOrParse(DemoTestHelper.RequireDemo(DemoTestHelper.SampleDemoFileName));

        Stream expected = FromSampler(demo);
        Stream actual = FromScanner(demo, maxDegreeOfParallelism);

        Console.WriteLine($"sampler: created={expected.Created.Count} removed={expected.Removed.Count} live rows={expected.Live.Count}; "
                          + $"scanner: created={actual.Created.Count} removed={actual.Removed.Count} live rows={actual.Live.Count}");

        await Assert.That(expected.Created.Count).IsGreaterThan(0)
            .Because("a demo with no projectile would make this parity pass while checking nothing");
        await Assert.That(expected.Created.Select(r => r.ClassName).Distinct().Count()).IsGreaterThan(2)
            .Because("the sample carries molotovs, smokes, flashes and HE grenades; fewer classes means the walk missed some");
        await Assert.That(Diff(expected.Created, actual.Created)).IsNull();
        await Assert.That(Diff(expected.Removed, actual.Removed)).IsNull();
        await Assert.That(Diff(expected.Live, actual.Live)).IsNull();
    }

    private static Stream FromSampler(ParsedDemo demo)
    {
        Stream s = new();
        foreach (ProjectileSample sample in ProjectileSampler.Walk(demo))
        {
            Row row = new(sample.FrameIndex, sample.EntityIndex, sample.Serial, sample.ClassName, sample.ThrowerSlot,
                sample.Position, sample.InitialPosition, sample.InitialVelocity, sample.Bounces);
            if (sample.Removed)
            {
                s.Removed.Add(row);
                continue;
            }

            if (sample.Created)
            {
                s.Created.Add(row);
            }

            s.Live.Add(row);
        }

        return s;
    }

    private static Stream FromScanner(ParsedDemo demo, int? maxDegreeOfParallelism)
    {
        EntityChangeScanner scanner = new(new EntityStateLayer { StoreUnlensedFields = false }, [], null, false, null, null, null,
            AllClasses);
        Stream s = new();
        foreach ((int n, DemoFrame _, IReadOnlyList<NetMessage> _) in SequentialScan.Frames(scanner, demo, maxDegreeOfParallelism))
        {
            ProjectileLifecycle lifecycle = scanner.Projectiles;
            foreach (ProjectileRow row in lifecycle.Created)
            {
                s.Created.Add(ToRow(n, row));
            }

            foreach (ProjectileRow row in lifecycle.Removed)
            {
                s.Removed.Add(ToRow(n, row));
            }

            foreach (ProjectileRow row in lifecycle.Live)
            {
                s.Live.Add(ToRow(n, row));
            }
        }

        return s;
    }

    private static Row ToRow(int frameIndex, in ProjectileRow row) =>
        new(frameIndex, row.EntityIndex, row.Serial, row.ClassName, row.ThrowerSlot,
            row.Position, row.InitialPosition, row.InitialVelocity, row.Bounces);

    private static string? Diff(List<Row> expected, List<Row> actual)
    {
        int n = Math.Min(expected.Count, actual.Count);
        for (int i = 0; i < n; i++)
        {
            if (!expected[i].Equals(actual[i]))
            {
                return $"row {i}: sampler {expected[i]} vs scanner {actual[i]}";
            }
        }

        return expected.Count == actual.Count ? null : $"count: sampler {expected.Count} vs scanner {actual.Count}";
    }
}
