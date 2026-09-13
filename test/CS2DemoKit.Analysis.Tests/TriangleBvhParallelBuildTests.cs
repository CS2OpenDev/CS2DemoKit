#region

using System.Numerics;
using System.Runtime.CompilerServices;
using CS2DemoKit.Analysis.Visibility;

#endregion

namespace CS2DemoKit.Analysis.Tests;

/// <summary>
///     Holds the parallel builder to the serial tree at every degree of parallelism, through the
///     same structural digests <see cref="TriangleBvhBuildIdentityTests" /> pins. The builder's
///     determinism is argued by construction (see the builder's doc: node numbering is arithmetic
///     over segment sizes, and every fold is an IEEE minimum or maximum); these tests are the
///     empirical half, and vary the two things that could break the argument if it were wrong.
///     The degree of parallelism changes how many chunks a large node's sweep splits into, whether
///     small subtrees are deferred to the pool at all, and how many workers race to finish them;
///     the soups add what the pinned ones do not exercise at a size where the sweep is chunked
///     and the subtrees deferred: NaN vertices scattered through the soup, NaNs carrying distinct
///     payloads (a fold's NaN tie rule decides which survives), and mixed-sign zeros, which a
///     hardware minimum would order by operand position. Each degree builds several times, so a
///     lucky schedule has to be lucky every time, and the degree is observed as well as passed:
///     the build reports the most threads it ran on, which must never exceed the degree and must
///     be exactly one when the degree is one.
///     <para>
///         Every digest here anchors to a builder that predates the parallel one. The identity
///         tests' pins came from the two-pass builder; the four soups this class adds were
///         digested by the one-pass serial builder as it stood before the parallel build (a probe
///         test in a detached worktree of that build, run once), so a soup that reproduces its digest at
///         every degree reproduces the pre-change tree, not merely its own degree-one build.
///     </para>
///     <para>
///         Watched failing with the placement walk visiting a top node's pending children in
///         reverse collapse order (a different but still scheduling-independent numbering): every
///         soup large enough to have a top node failed at every degree above one, and no soup
///         below the deferral size moved. An earlier draft, which placed subtrees from recorded
///         positions instead of a walk, was watched failing the same way when its placement loop
///         stopped one node short and the trailing subtrees landed over the root in whichever
///         order their workers finished. The degree test was watched failing with the degree-one
///         guard removed from the chunking (a one-thread build forked and reported more).
///     </para>
/// </summary>
// Serial against the rest of the suite. Nineteen parameterised cases, each ~24 builds at degrees up
// to 64, is enough oversubscription on its own; sharing the pool with the other classes on top of
// that both slows everything down and makes the fork below unobservable, because Parallel.For runs
// its first range on the calling thread and only injects a worker when the pool has one to spare.
[NotInParallel]
[Category("Unit")]
public class TriangleBvhParallelBuildTests
{
    private static readonly int[] Degrees = [1, 2, 3, 4, 5, 8, 16, 32, 64];
    private const int Repeats = 3;

    // Cores needed before the default build's fork is reliably OBSERVABLE. This soup is 200,000
    // triangles, so a chunk's own work is small enough that the calling thread can finish every
    // chunk before a second worker steals one, and the peak then reads 1 on a machine that really
    // is forking. A four-core runner reports exactly that, while a real bake forks at a degree of
    // two: the bench build verb on de_nuke gives 129.3 ms on one thread, 80.8 on two and 56.5 on
    // four, same digest throughout. Eight is where this stops being a race.
    private const int ForkIsObservableAt = 8;

    // Builds the fork assertion may watch before it gives up. The peak is a property of the
    // SCHEDULE as much as of the build: Parallel.For runs its first range inline on the calling
    // thread and queues the rest, so a pass where the caller drains all six chunks before the pool
    // dispatches one reports 1 from a build that forks on the very next pass. One such pass is not
    // evidence of a build that never forks; five in a row, with the pool to this class alone, is.
    private const int ForkAttempts = 5;

    /// <param name="map">Map whose <c>collision.tris</c> to load.</param>
    /// <param name="expectedDigest">The digest <see cref="TriangleBvhBuildIdentityTests" /> pins for the map.</param>
    [Test]
    [Category("RealAsset")]
    [Arguments("de_ancient", "43AF9BBF399B09F9")]
    [Arguments("de_overpass", "652353CE415B180B")]
    [Arguments("de_dust2", "17A84C526631ED31")]
    [Arguments("de_vertigo", "A95DB5FA0487B30A")]
    [Arguments("de_nuke", "54882E14EFAA64DF")]
    [Arguments("de_mirage", "EF353601E7911349")]
    [Arguments("de_inferno", "2E58EC353584BB60")]
    [Arguments("de_anubis", "49D773EE4984FD86")]
    [Arguments("de_cache", "26AC3861F3664F6A")]
    [Arguments("de_train", "4AB268AA399704F1")]
    public async Task RealBake_EveryDegreeOfParallelism_GivesThePinnedTree(string map, string expectedDigest)
    {
        CollisionTris.Data bake = CollisionTris.Load(VisibilityReplay.RequireBakePath(map));
        await AssertEveryDegree(bake.Vertices, bake.TriangleCount, expectedDigest, map);
    }

    /// <param name="soup">Which pinned synthetic soup to build over.</param>
    /// <param name="expectedDigest">The digest <see cref="TriangleBvhBuildIdentityTests" /> pins for the soup.</param>
    [Test]
    [Arguments(TriangleBvhBuildIdentityTests.Soup.NaNVertex, "665F305D9EC52E5C")]
    [Arguments(TriangleBvhBuildIdentityTests.Soup.Random5000, "B4892CDEE9B0A25B")]
    [Arguments(TriangleBvhBuildIdentityTests.Soup.Random200000, "700A8DD157EA4EA6")]
    [Arguments(TriangleBvhBuildIdentityTests.Soup.DepthCapChain, "7E236F7C84028DB4")]
    [Arguments(TriangleBvhBuildIdentityTests.Soup.SixtyFourCoincident, "E5D492851EC7F3B7")]
    public async Task PinnedSoup_EveryDegreeOfParallelism_GivesThePinnedTree(TriangleBvhBuildIdentityTests.Soup soup, string expectedDigest)
    {
        (float[] vertices, int count) = TriangleBvhBuildIdentityTests.Make(soup);
        await AssertEveryDegree(vertices, count, expectedDigest, soup.ToString());
    }

    /// <summary>
    ///     The soups this class adds, each at a size that chunks the sweep and defers subtrees,
    ///     against the one-pass serial builder's digest (see the class doc). The payload soup's
    ///     pin is that builder's digest of the same soup with every NaN canonical: its digest of
    ///     the payload soup itself depended on the JIT tier its folds ran in, which is the defect
    ///     the canonicalisation removed, and the canonical soup is the tree it must now build.
    /// </summary>
    /// <param name="soup">Which soup to build over.</param>
    /// <param name="expectedDigest">The serial builder's digest for the soup.</param>
    [Test]
    [Arguments(Soup.NaNScattered, "3B29B6DFD98BA3EF")]
    [Arguments(Soup.SignedZeros, "B6368297C9848076")]
    [Arguments(Soup.CoincidentPastDeferral, "03CF5EADB33FA4B4")]
    [Arguments(Soup.NaNPayloads, "8B8D86397EBAB5F7")]
    public async Task AddedSoup_EveryDegreeOfParallelism_GivesTheSerialTree(Soup soup, string expectedDigest)
    {
        (float[] vertices, int count) = Make(soup);
        await AssertEveryDegree(vertices, count, expectedDigest, soup.ToString());
    }

    /// <summary>
    ///     The build never runs on more threads than its degree, and on exactly one when the
    ///     degree is one: the parallel sites are skipped then, not run with a single worker. At
    ///     the machine's own degree it must actually fork, or the bound is vacuous.
    /// </summary>
    [Test]
    public async Task Build_HonoursItsDegree()
    {
        (float[] vertices, int count) = TriangleBvhBuildIdentityTests.Make(TriangleBvhBuildIdentityTests.Soup.Random200000);
        foreach (int degree in Degrees)
        {
            TriangleBvh bvh = TriangleBvh.Build(vertices, count, degree);
            await Assert.That(bvh.PeakBuildWorkers).IsLessThanOrEqualTo(degree)
                .Because($"a build given {degree} threads ran on {bvh.PeakBuildWorkers} at once");
            if (degree == 1)
            {
                await Assert.That(bvh.PeakBuildWorkers).IsEqualTo(1)
                    .Because("a degree of one is the calling thread alone");
            }
        }

        int processors = Environment.ProcessorCount;
        if (processors >= ForkIsObservableAt)
        {
            int peak = 1;
            int attempts = 0;
            while (peak <= 1 && attempts < ForkAttempts)
            {
                attempts++;
                peak = Math.Max(peak, TriangleBvh.Build(vertices, count).PeakBuildWorkers);
            }

            Console.WriteLine($"[parallel-build] default degree {processors}: peak workers {peak} after {attempts} build(s)");
            await Assert.That(peak).IsGreaterThan(1)
                .Because($"the default degree with cores to spare must fork within {ForkAttempts} builds, "
                         + "or the bound above proves nothing");
        }
    }

    /// <summary>
    ///     Several builds at the full degree at once, contending for the same pool, each give the
    ///     pinned tree: nothing a build touches is shared between builds.
    /// </summary>
    [Test]
    public async Task ConcurrentBuilds_GiveThePinnedTree()
    {
        (float[] vertices, int count) = TriangleBvhBuildIdentityTests.Make(TriangleBvhBuildIdentityTests.Soup.Random200000);
        Task<string>[] builds = new Task<string>[4];
        for (int i = 0; i < builds.Length; i++)
        {
            builds[i] = Task.Run(() => $"{TriangleBvh.Build(vertices, count).StructuralDigest():X16}");
        }

        string[] digests = await Task.WhenAll(builds);
        foreach (string digest in digests)
        {
            await Assert.That(digest).IsEqualTo("700A8DD157EA4EA6")
                .Because("a build sharing the pool with three others must still be the serial tree");
        }
    }

    /// <summary>
    ///     The fold rule the chunked sweeps rest on, pinned by name: <see cref="Vector3.Min" /> and
    ///     <see cref="Vector3.Max" /> propagate a NaN from either side and order negative zero
    ///     below positive zero. This is the runtime's behaviour on the engine's target, not the
    ///     type's contract everywhere (the hardware minimum returns its second operand on NaN or
    ///     equal zeros), and under that other rule a chunked fold and the serial fold differ; a
    ///     retarget that changes it fails here rather than as a moved digest in a soup test. What
    ///     is deliberately not pinned is which of two NaNs survives: the runtime keeps the left
    ///     payload in unoptimised code and the right in optimised code, which is why the builder
    ///     canonicalises NaNs on entry and the next test holds it to that.
    ///     <para>
    ///         Read twice, in both JIT tiers. A test body is tier-0 code compiled once, while every
    ///         fold this rule protects runs under <see cref="MethodImplOptions.AggressiveOptimization" />,
    ///         and the tier is a known live variable here: the very defect the NaN canonicalisation
    ///         removed was a digest that differed between the tiers. A pin that only ever observed the
    ///         tier the folds do not run in would be the wrong half of the thing it certifies.
    ///     </para>
    /// </summary>
    [Test]
    public async Task VectorMinMax_AreTheIeeeMinimumAndMaximum()
    {
        Vector3 one = new(1f);
        Vector3 nan = new(float.NaN);
        await Assert.That(float.IsNaN(Vector3.Min(one, nan).X)).IsTrue().Because("Min must propagate a NaN on the right");
        await Assert.That(float.IsNaN(Vector3.Min(nan, one).X)).IsTrue().Because("Min must propagate a NaN on the left");
        await Assert.That(float.IsNaN(Vector3.Max(one, nan).X)).IsTrue().Because("Max must propagate a NaN on the right");
        await Assert.That(float.IsNaN(Vector3.Max(nan, one).X)).IsTrue().Because("Max must propagate a NaN on the left");

        int negativeZero = BitConverter.SingleToInt32Bits(-0f);
        int positiveZero = BitConverter.SingleToInt32Bits(0f);
        await Assert.That(BitConverter.SingleToInt32Bits(Vector3.Min(new Vector3(0f), new Vector3(-0f)).X)).IsEqualTo(negativeZero);
        await Assert.That(BitConverter.SingleToInt32Bits(Vector3.Min(new Vector3(-0f), new Vector3(0f)).X)).IsEqualTo(negativeZero);
        await Assert.That(BitConverter.SingleToInt32Bits(Vector3.Max(new Vector3(0f), new Vector3(-0f)).X)).IsEqualTo(positiveZero);
        await Assert.That(BitConverter.SingleToInt32Bits(Vector3.Max(new Vector3(-0f), new Vector3(0f)).X)).IsEqualTo(positiveZero);

        // The same eight observations out of a method compiled the way the folds are. The operands go
        // in as arguments across a non-inlined call so the JIT cannot fold the vectors at compile time
        // and hand back its own constant instead of the instruction's answer, which is what a fold over
        // a soup read from an array gets.
        FoldRule expected = new(true, true, true, true, negativeZero, negativeZero, positiveZero, positiveZero);
        await Assert.That(ObserveFoldRule(one, nan, new Vector3(0f), new Vector3(-0f))).IsEqualTo(expected)
            .Because("the optimised tier must fold the way the unoptimised one just did, or the builder's "
                     + "folds and this pin are reading different rules");
    }

    /// <summary>
    ///     A soup whose NaNs carry four distinct payloads builds the tree of the same soup with
    ///     every NaN replaced by <see cref="float.NaN" />, at one thread and at the machine's
    ///     degree: the payloads never reach a fold. Watched failing before the builder
    ///     canonicalised NaNs on entry, when the payload soup's digest depended on which JIT tier
    ///     the folds ran in (9A4F15785694945D with the hot methods optimised, E68E3065DD700583
    ///     with them not) and the two soups disagreed.
    /// </summary>
    [Test]
    public async Task NaNPayloads_DoNotReachTheTree()
    {
        (float[] payloads, int count) = Make(Soup.NaNPayloads);
        float[] canonical = (float[])payloads.Clone();
        for (int i = 0; i < canonical.Length; i++)
        {
            if (float.IsNaN(canonical[i]))
            {
                canonical[i] = float.NaN;
            }
        }

        foreach (int degree in new[] { 1, Environment.ProcessorCount })
        {
            string withPayloads = $"{TriangleBvh.Build(payloads, count, degree).StructuralDigest():X16}";
            string withCanonical = $"{TriangleBvh.Build(canonical, count, degree).StructuralDigest():X16}";
            await Assert.That(withPayloads).IsEqualTo(withCanonical)
                .Because($"on {degree} threads the NaN payloads changed the tree, so some fold's NaN tie rule reached it");
        }
    }

    /// <summary>Soups this class adds to the pinned ones.</summary>
    public enum Soup
    {
        /// <summary>200,000 random triangles with a NaN in one vertex of every 997th.</summary>
        NaNScattered,

        /// <summary>200,000 random triangles whose z coordinates are all negative or positive zero.</summary>
        SignedZeros,

        /// <summary>20,000 copies of one triangle: past the deferral size with no splittable centroid.</summary>
        CoincidentPastDeferral,

        /// <summary>
        ///     200,000 random triangles with a NaN in one vertex of every 991st, cycling through four
        ///     distinct payloads of both signs, so which NaN a fold keeps is visible in the digest.
        /// </summary>
        NaNPayloads
    }

    private static async Task AssertEveryDegree(float[] vertices, int count, string expectedDigest, string label)
    {
        foreach (int degree in Degrees)
        {
            for (int repeat = 0; repeat < Repeats; repeat++)
            {
                TriangleBvh bvh = TriangleBvh.Build(vertices, count, degree);
                string digest = $"{bvh.StructuralDigest():X16}";
                await Assert.That(digest).IsEqualTo(expectedDigest)
                    .Because($"{label} on {degree} threads (repeat {repeat}) must be the serial tree; a moved digest is a scheduling-dependent build");
                await Assert.That(bvh.PeakBuildWorkers).IsLessThanOrEqualTo(degree)
                    .Because($"{label} on {degree} threads ran on {bvh.PeakBuildWorkers} at once");
            }
        }
    }

    private static (float[] Vertices, int Count) Make(Soup soup)
    {
        switch (soup)
        {
            case Soup.NaNScattered:
            {
                const int count = 200_000;
                float[] v = TriangleBvh8Tests.RandomSoup(new Random(20260911), count, 8000f);
                for (int t = 500; t < count; t += 997)
                {
                    v[(t * 9) + (t % 9)] = float.NaN;
                }

                return (v, count);
            }

            case Soup.SignedZeros:
            {
                const int count = 200_000;
                float[] v = TriangleBvh8Tests.RandomSoup(new Random(20260912), count, 8000f);
                Random rng = new(7);
                for (int t = 0; t < count; t++)
                {
                    for (int k = 2; k < 9; k += 3)
                    {
                        v[(t * 9) + k] = rng.Next(2) == 0 ? 0f : -0f;
                    }
                }

                return (v, count);
            }

            case Soup.CoincidentPastDeferral:
            {
                const int count = 20_000;
                float[] one = [100f, 100f, 50f, 140f, 100f, 50f, 100f, 140f, 50f];
                float[] v = new float[count * 9];
                for (int i = 0; i < count; i++)
                {
                    one.CopyTo(v, i * 9);
                }

                return (v, count);
            }

            case Soup.NaNPayloads:
            {
                const int count = 200_000;
                float[] v = TriangleBvh8Tests.RandomSoup(new Random(20260913), count, 8000f);
                int[] payloads = [0x7FC00001, 0x7FC0BEEF, unchecked((int)0xFFC00000), unchecked((int)0xFFC01234)];
                int i = 0;
                for (int t = 250; t < count; t += 991, i++)
                {
                    v[(t * 9) + (t % 9)] = BitConverter.Int32BitsToSingle(payloads[i % payloads.Length]);
                }

                return (v, count);
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(soup), soup, "no such soup");
        }
    }

    /// <summary>
    ///     The fold rule read in the tier the builder's folds run in. Compiled with the attribute they
    ///     carry, and not inlined, so the operands arrive unknown and the vector folds happen at run
    ///     time rather than in the JIT's constant folder.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization | MethodImplOptions.NoInlining)]
    private static FoldRule ObserveFoldRule(Vector3 one, Vector3 nan, Vector3 zero, Vector3 negativeZero) =>
        new(float.IsNaN(Vector3.Min(one, nan).X),
            float.IsNaN(Vector3.Min(nan, one).X),
            float.IsNaN(Vector3.Max(one, nan).X),
            float.IsNaN(Vector3.Max(nan, one).X),
            BitConverter.SingleToInt32Bits(Vector3.Min(zero, negativeZero).X),
            BitConverter.SingleToInt32Bits(Vector3.Min(negativeZero, zero).X),
            BitConverter.SingleToInt32Bits(Vector3.Max(zero, negativeZero).X),
            BitConverter.SingleToInt32Bits(Vector3.Max(negativeZero, zero).X));

    /// <summary>One reading of the eight observations that pin the fold rule.</summary>
    /// <param name="MinPropagatesNaNOnTheRight">Min of a number and a NaN, NaN second.</param>
    /// <param name="MinPropagatesNaNOnTheLeft">Min of a NaN and a number, NaN first.</param>
    /// <param name="MaxPropagatesNaNOnTheRight">Max of a number and a NaN, NaN second.</param>
    /// <param name="MaxPropagatesNaNOnTheLeft">Max of a NaN and a number, NaN first.</param>
    /// <param name="MinOfZerosNegativeSecond">Bits of Min(+0, -0).</param>
    /// <param name="MinOfZerosNegativeFirst">Bits of Min(-0, +0).</param>
    /// <param name="MaxOfZerosNegativeSecond">Bits of Max(+0, -0).</param>
    /// <param name="MaxOfZerosNegativeFirst">Bits of Max(-0, +0).</param>
    private readonly record struct FoldRule(
        bool MinPropagatesNaNOnTheRight,
        bool MinPropagatesNaNOnTheLeft,
        bool MaxPropagatesNaNOnTheRight,
        bool MaxPropagatesNaNOnTheLeft,
        int MinOfZerosNegativeSecond,
        int MinOfZerosNegativeFirst,
        int MaxOfZerosNegativeSecond,
        int MaxOfZerosNegativeFirst);
}
