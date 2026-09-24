#region

using CS2DemoKit.TestSupport;
using TUnit.Core.Exceptions;

#endregion

namespace CS2DemoKit.Parser.Tests.EntityTracking;

/// <summary>
///     The projectile creation-state invariants from <see cref="ProjectileCreationDecodeTests" /> over
///     every demo this machine has: the sample, <c>&lt;repo-root&gt;/demos/**</c>, <c>DEMO_PATH</c>,
///     and the top-level demos in <c>CS2DEMOKIT_CORPUS_DIR</c>. Explicit because it replays each demo
///     in full. A build whose smoke baseline outgrew the field-path cap would fail here with a decode
///     error; one that cut it off silently again would fail the position, team and thrower checks.
///     <para>
///         Only fresh throws are checked (created after the initial snapshot with
///         <c>m_nBounces</c> 0). Projectiles already in flight when the demo starts are legitimately
///         far from <c>m_vInitialPosition</c>, such as the five bouncing decoys at frame 15 on one
///         build-10231 demo.
///     </para>
/// </summary>
[Explicit]
[NotInParallel]
[Category("Integration")]
public class ProjectileCreationCorpusTests
{
    /// <summary>Above this many projectiles a demo is a real match and must hold a smoke.</summary>
    private const int FullMatchProjectiles = 50;

    public static IEnumerable<string> Demos()
    {
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach (string path in DemoReaderCorpusTests.Demos())
        {
            if (seen.Add(Path.GetFullPath(path)))
            {
                yield return path;
            }
        }

        string? fromEnv = Environment.GetEnvironmentVariable("DEMO_PATH");
        if (!string.IsNullOrWhiteSpace(fromEnv) && File.Exists(fromEnv) && seen.Add(Path.GetFullPath(fromEnv)))
        {
            yield return fromEnv;
        }

        if (DemoTestHelper.CorpusDirectory() is { } dir)
        {
            foreach (string path in Directory.GetFiles(dir, "*.dem", SearchOption.TopDirectoryOnly)
                         .Order(StringComparer.OrdinalIgnoreCase))
            {
                if (seen.Add(Path.GetFullPath(path)))
                {
                    yield return path;
                }
            }
        }
    }

    [Test]
    [MethodDataSource(nameof(Demos))]
    public async Task Corpus_ProjectileCreationInvariants(string path)
    {
        if (!File.Exists(path))
        {
            throw new SkipTestException($"Demo '{path}' is not present on this machine.");
        }

        byte[] bytes = await File.ReadAllBytesAsync(path);
        ProjectileTally tally;
        using (DemoReader reader = DemoReader.Open(bytes.AsMemory(), new ParseOptions { Plan = DecodePlan.EntityReplay }))
        {
            tally = ProjectileTally.Walk(ProjectileTally.ReadAll(reader));
        }

        Console.WriteLine($"{Path.GetFileName(path)} maxFieldPaths={tally.MaxFieldPathCount} " +
                          $"smokeTickBeginWithoutEffect={tally.SmokeTickBeginWithoutEffect}");
        foreach ((string cls, ProjectileClassTally t) in tally.ByClass)
        {
            Console.WriteLine($"  {cls} created={t.Created} inFlight={t.InFlight} far={t.FarFromThrow} " +
                              $"badTeam={t.BadTeam} unresolvedThrower={t.UnresolvedThrower}");
        }

        await Assert.That(tally.Error).IsNull();
        await Assert.That(tally.SmokeTickBeginWithoutEffect).IsEqualTo(0L);
        foreach ((string cls, ProjectileClassTally t) in tally.ByClass)
        {
            await Assert.That(t.FarFromThrow).IsEqualTo(0).Because($"{cls} position at creation");
            await Assert.That(t.BadTeam).IsEqualTo(0).Because($"{cls} team at creation");
            await Assert.That(t.UnresolvedThrower).IsEqualTo(0).Because($"{cls} thrower at creation");
        }

        if (tally.ByClass.Values.Sum(t => t.Created) >= FullMatchProjectiles)
        {
            await Assert.That(tally.ByClass.TryGetValue("CSmokeGrenadeProjectile", out ProjectileClassTally? smokes)
                              && smokes.InFlight > 0).IsTrue().Because("a full match throws smokes");
        }
    }
}
