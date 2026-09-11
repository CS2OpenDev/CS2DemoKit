#region

using CS2DemoKit.Analysis.Visibility;

#endregion

namespace CS2DemoKit.Analysis.Tests;

/// <summary>
///     Holds a build to the serial builder's allocation budget. The budget is what the build has
///     to allocate and nothing else: three int arrays of the triangle count (slot order, slot to
///     lane, triangle to slot), the triangle records and the leaf storage (36 bytes per triangle
///     each), the lane arrays at their initial guess (seven arrays of eight entries per guessed
///     node, one node per six triangles, doubled each time the tree outgrows them, as the random
///     soup's does once) and the final exact-size lane arrays, plus a fixed allowance for the
///     builder's own objects. A degree of one runs on the calling thread alone, so it is read
///     from the thread's own allocation counter, exact whatever else the test run is doing; the
///     full degree is read from the process counter, which is why the class runs in parallel
///     with nothing, and it is allowed a few percent for the segments' own bins, lists and their
///     own growth.
///     <para>
///         Allocation is a stable instrument (the bench's build verb reports it to the tenth of a
///         megabyte run after run) and it caught nothing until this test existed: an earlier form
///         of the parallel builder allocated 148 percent of the serial budget at every degree, from
///         a closure in the decision path costing a heap object per decision. Watched
///         failing with that closure put back: 156 percent of budget on de_nuke at a degree of
///         one, and 209 percent on the random soup at both degrees.
///     </para>
/// </summary>
[NotInParallel]
[Category("Unit")]
public class TriangleBvhBuildAllocationTests
{
    // The builder, its bin sets, the lists, the parallel options and a few closures.
    private const long Allowance = 256 * 1024;

    // What the full degree may add over the serial budget: a bin set and a pending list per
    // segment, growth of a subtree segment's lane arrays past the guess, and the loop machinery.
    private const double ParallelSlack = 0.05;

    /// <summary>
    ///     The random soup, at a degree of one: the thread's own counter against the budget.
    /// </summary>
    [Test]
    public async Task SyntheticSoup_SerialBuild_AllocatesTheBudget()
    {
        (float[] vertices, int count) = TriangleBvhBuildIdentityTests.Make(TriangleBvhBuildIdentityTests.Soup.Random200000);
        await AssertSerialBudget(vertices, count, "Random200000");
    }

    /// <summary>
    ///     The random soup, at the machine's degree: the process counter against the budget plus
    ///     the parallel slack.
    /// </summary>
    [Test]
    public async Task SyntheticSoup_ParallelBuild_AllocatesTheBudget()
    {
        (float[] vertices, int count) = TriangleBvhBuildIdentityTests.Make(TriangleBvhBuildIdentityTests.Soup.Random200000);
        await AssertParallelBudget(vertices, count, "Random200000");
    }

    /// <summary>A real bake at both degrees.</summary>
    /// <param name="map">Map whose <c>collision.tris</c> to load.</param>
    [Test]
    [Category("RealAsset")]
    [Arguments("de_nuke")]
    public async Task RealBake_Build_AllocatesTheBudget(string map)
    {
        CollisionTris.Data bake = CollisionTris.Load(VisibilityReplay.RequireBakePath(map));
        await AssertSerialBudget(bake.Vertices, bake.TriangleCount, map);
        await AssertParallelBudget(bake.Vertices, bake.TriangleCount, map);
    }

    private static async Task AssertSerialBudget(float[] vertices, int count, string label)
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        TriangleBvh bvh = TriangleBvh.Build(vertices, count, 1);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        long budget = Budget(count, bvh.NodeCount);
        Console.WriteLine($"[build-allocation] {label} serial: {allocated / 1048576.0:F2} MiB allocated on the thread, budget {budget / 1048576.0:F2} MiB ({100.0 * allocated / budget:F1} percent)");
        await Assert.That(bvh.PeakBuildWorkers).IsEqualTo(1).Because("the thread counter is only the whole build when the build stayed on the thread");
        await Assert.That(allocated).IsLessThanOrEqualTo(budget + Allowance)
            .Because($"{label} at a degree of one allocated {allocated} bytes against a budget of {budget}: something allocates per node or per triangle that the serial build did not");
    }

    private static async Task AssertParallelBudget(float[] vertices, int count, string label)
    {
        int degree = Environment.ProcessorCount;
        long before = GC.GetTotalAllocatedBytes(true);
        TriangleBvh bvh = TriangleBvh.Build(vertices, count, degree);
        long allocated = GC.GetTotalAllocatedBytes(true) - before;
        long budget = Budget(count, bvh.NodeCount);
        long limit = (long)(budget * (1 + ParallelSlack)) + Allowance;
        Console.WriteLine($"[build-allocation] {label} on {degree} threads: {allocated / 1048576.0:F2} MiB allocated, serial budget {budget / 1048576.0:F2} MiB ({100.0 * allocated / budget:F1} percent), limit {limit / 1048576.0:F2} MiB");
        await Assert.That(allocated).IsLessThanOrEqualTo(limit)
            .Because($"{label} on {degree} threads allocated {allocated} bytes against a serial budget of {budget}: the parallel build may add its segments' bins and a little growth, not more");
    }

    // The arrays the build cannot avoid, with the runtime's 24-byte array header each. The guess
    // of one wide node per six triangles is the builder's TrianglesPerWideNodeGuess, and the
    // doubling is its Grow; change them together.
    private static long Budget(int count, int nodeCount)
    {
        const long header = 24;
        long intArray = (4L * count) + header;
        long perTriangle = (36L * count) + header;
        long capacity = Math.Max(1, count / 6);
        long guessLanes = (32L * capacity) + header;
        while (nodeCount > capacity)
        {
            capacity *= 2;
            guessLanes += (32L * capacity) + header;
        }

        long exactLanes = (32L * nodeCount) + header;
        return (3 * intArray) + (2 * perTriangle) + (7 * guessLanes) + (7 * exactLanes);
    }
}
