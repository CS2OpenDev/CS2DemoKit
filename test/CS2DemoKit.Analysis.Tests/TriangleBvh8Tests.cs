#region

using System.Numerics;
using CS2DemoKit.Analysis.Visibility;

#endregion

namespace CS2DemoKit.Analysis.Tests;

/// <summary>
///     Pins the eight-wide tree's structure: an empty mesh has no nodes and answers without a stack;
///     empty lanes are inverted boxes no ray can enter; lanes are laid out by octant and popped
///     nearest first; every triangle sits in exactly one leaf slot under boxes that contain it; the
///     build is deterministic; coincident centroids past the leaf size split by halves; and the
///     answers match the brute-force oracle on a soup. Each structural test was watched failing
///     under a deliberate mutation before the tree landed, in a Release build where the traversal's
///     Debug assertions are absent: zero bounds in the empty lanes, the min/max swap slab form (with
///     and without an empty-lane guard), a far-first push order, a strict final compare, and the
///     NaN guard removed (see the PR).
/// </summary>
[Category("Unit")]
public class TriangleBvh8Tests
{
    private const float Eps = 0.1f;

    /// <summary>
    ///     The old tree's empty root had a left child of -1 and a right child of 0 (itself), so a
    ///     ray whose slab window admitted the zero-size root box re-pushed the root until the
    ///     traversal stack overflowed. A ray through the world origin is such a ray; it needs no
    ///     float luck. The new tree has no nodes at all for an empty mesh.
    /// </summary>
    [Test]
    public async Task EmptyMesh_RayThroughTheOrigin_IsVisibleAndDoesNotOverflowTheStack()
    {
        VisibilityEngine engine = VisibilityEngine.FromTriangles([], 0);
        await Assert.That(engine.IsVisible(new Vector3(-1f, -1f, -1f), new Vector3(1f, 1f, 1f))).IsTrue()
            .Because("nothing can occlude a segment in an empty world");
        await Assert.That(engine.RayDownDistance(Vector3.Zero, 100f, out float drop)).IsFalse();
        await Assert.That(drop).IsEqualTo(float.MaxValue);

        int hint = 3;
        await Assert.That(engine.IsVisible(Vector3.Zero, new Vector3(0f, 0f, 500f), ref hint)).IsTrue();
        await Assert.That(hint).IsEqualTo(3).Because("a clear ray leaves the hint alone");

        // The frozen tree carries the defect: the same ray re-pushes its self-referencing empty
        // root until the traversal stack overflows. Asserted, not assumed, so the test is known to
        // observe the case it exists for.
        Vector3 o = new(-1f, -1f, -1f);
        Vector3 dir = Vector3.Normalize(new Vector3(1f, 1f, 1f));
        LegacyTriangleBvh frozen = LegacyTriangleBvh.Build([], 0);
        _ = Assert.Throws<IndexOutOfRangeException>(() => _ = frozen.AnyHit(o, dir, 3.4641f, 0.1f));

        TriangleBvh bvh = TriangleBvh.Build([], 0);
        await Assert.That(bvh.AnyHit(o, dir, 3.4641f, 0.1f)).IsFalse();
        await Assert.That(bvh.NodeCount).IsEqualTo(0);
        await Assert.That(bvh.TriangleCount).IsEqualTo(0);
        await Assert.That(bvh.Min).IsEqualTo(Vector3.Zero);
        await Assert.That(bvh.Max).IsEqualTo(Vector3.Zero);
    }

    /// <summary>
    ///     A root with fewer than eight children has empty lanes. Their boxes must be inverted and
    ///     their references <see cref="TriangleBvh.EmptyLane" />, and no ray, through the origin,
    ///     along an axis, or with its origin on a plane, may enter one. Under the mutation that
    ///     writes zero bounds into an empty lane, a ray through the origin enters it and reads the
    ///     empty reference; under the mutation that swaps min and max instead of selecting by sign,
    ///     every ray enters every empty lane. Both were watched failing here.
    /// </summary>
    /// <param name="count">Triangles in the soup, fewer than a full node.</param>
    [Test]
    [Arguments(1)]
    [Arguments(2)]
    [Arguments(3)]
    [Arguments(5)]
    [Arguments(7)]
    public async Task EmptyLanes_AreInvertedAndNeverEntered(int count)
    {
        float[] vertices = SeparatedTriangles(count);
        TriangleBvh bvh = TriangleBvh.Build(vertices, count);
        int empty = 0, used = 0;
        for (int lane = 0; lane < TriangleBvh.Width; lane++)
        {
            int child = bvh.LaneChild(lane);
            (Vector3 min, Vector3 max) = bvh.LaneBounds(lane);
            if (child == TriangleBvh.EmptyLane)
            {
                empty++;
                await Assert.That(min).IsEqualTo(new Vector3(float.PositiveInfinity));
                await Assert.That(max).IsEqualTo(new Vector3(float.NegativeInfinity));
            }
            else
            {
                used++;
                await Assert.That(min.X <= max.X && min.Y <= max.Y && min.Z <= max.Z).IsTrue();
            }
        }

        await Assert.That(used).IsGreaterThan(0);
        await Assert.That(used).IsLessThanOrEqualTo(count);
        await Assert.That(empty).IsEqualTo(TriangleBvh.Width - used);

        List<(Vector3 A, Vector3 B)> rays = [];
        // Through the origin from every one of the 26 lattice directions, at unit speed and
        // axis-aligned where the lattice says so, which makes inv infinite on that axis.
        for (int dx = -1; dx <= 1; dx++)
        {
            for (int dy = -1; dy <= 1; dy++)
            {
                for (int dz = -1; dz <= 1; dz++)
                {
                    if (dx == 0 && dy == 0 && dz == 0)
                    {
                        continue;
                    }

                    Vector3 d = new(dx, dy, dz);
                    rays.Add((-d * 500f, d * 500f));
                    rays.Add((Vector3.Zero, d * 500f)); // origin exactly on the mutated box
                }
            }
        }

        // Through every triangle, and along its base edge's plane with the origin on it.
        for (int i = 0; i < count; i++)
        {
            Vector3 c = new(vertices[i * 9] + 3f, 43f, 33f);
            rays.Add((c - new Vector3(50f, 0f, 0f), c + new Vector3(50f, 0f, 0f)));
            rays.Add((new Vector3(vertices[i * 9], 40f, 30f), new Vector3(vertices[i * 9] + 200f, 40f, 30f)));
            rays.Add((c + new Vector3(0f, 0f, 200f), c - new Vector3(0f, 0f, 200f)));
        }

        // Entry is observed, not inferred: every lane the traversal pushes is recorded, and none
        // may be empty. The oracle comparison alone would pass in a Release build under either
        // mutation, because a pushed empty reference decodes as a leaf of no slots and tests
        // nothing; only the Debug assertion in the traversal would catch it there.
        int occluded = 0, lanesEntered = 0;
        foreach ((Vector3 a, Vector3 b) in rays)
        {
            Vector3 delta = b - a;
            float len = delta.Length();
            Vector3 dir = delta / len;
            bool oracle = BruteForceOracle.AnyHit(vertices, count, a, dir, len, Eps);
            TraversalTrace trace = TraversalTrace.Create();
            int hint = -1;
            bool current = bvh.AnyHit(a, dir, len, Eps, ref hint, out _, ref trace);
            await Assert.That(current).IsEqualTo(oracle)
                .Because($"ray {a} -> {b} must match the oracle on a {count}-triangle mesh");
            bool nearest = bvh.NearestHit(a, dir, len, 1e-3f, out float t, ref trace);
            await Assert.That(nearest).IsEqualTo(BruteForceOracle.NearestHit(vertices, count, a, dir, len, 1e-3f, out float ot, out _));
            await Assert.That(t).IsEqualTo(ot);
            foreach (int lane in trace.Lanes)
            {
                await Assert.That(bvh.LaneChild(lane)).IsNotEqualTo(TriangleBvh.EmptyLane)
                    .Because($"ray {a} -> {b} entered empty lane {lane} of a {count}-triangle mesh");
            }

            lanesEntered += trace.Lanes.Count;
            if (oracle)
            {
                occluded++;
            }
        }

        await Assert.That(occluded).IsGreaterThan(0).Because("the per-triangle rays must hit something");
        await Assert.That(lanesEntered).IsGreaterThan(0).Because("the per-triangle rays enter the real lanes, so the trace is known to record");

        // The net under the guarantee: an empty reference is also the leaf of zero slots, so a
        // stray one popping off the stack tests nothing rather than indexing node -1.
        await Assert.That(TriangleBvh.IsLeaf(TriangleBvh.EmptyLane)).IsFalse();
        await Assert.That(TriangleBvh.DecodeLeaf(TriangleBvh.EmptyLane)).IsEqualTo((0, 0));
    }

    /// <summary>
    ///     The one verdict class where the sign-selected slab form and the swap form the binary tree
    ///     used differ: a direction component of negative zero with the origin on that axis's
    ///     minimum face. The box has its minimum x face at zero; the ray runs in the plane x = 0
    ///     from x = +0 to x = -0, so its direction's x is negative zero and its inverse negative
    ///     infinity, and it crosses the triangle's edge on that face at t = 5, which the oracle
    ///     reports as a hit. The swap form computes NaN for the minimum face and negative infinity
    ///     for the maximum, keeps them in that order, collapses the window and reports clear; the
    ///     selected form puts the NaN on the far side, ignores it and reports the hit. The positive
    ///     zero twin of the same ray is admitted by both forms, so the flip is the selected form
    ///     agreeing with itself across the sign of zero, and with the oracle. Watched failing under
    ///     the swap-form mutation.
    /// </summary>
    [Test]
    public async Task NegativeZeroDirection_OriginOnTheMinimumFace_IsOccluded()
    {
        float[] vertices = [0f, 0f, 0f, 0f, 0f, 10f, 10f, 10f, 0f];
        TriangleBvh bvh = TriangleBvh.Build(vertices, 1);
        (Vector3 min, Vector3 max) = bvh.LaneBounds(bvh.LaneOfTriangle(0));
        await Assert.That(min.X).IsEqualTo(0f).Because("the box's minimum face is the plane the ray runs in");

        Vector3 origin = new(0f, -5f, 5f);
        foreach (float endX in new[] { -0f, 0f })
        {
            Vector3 end = new(endX, 5f, 5f);
            Vector3 delta = end - origin;
            float len = delta.Length();
            Vector3 dir = delta / len;
            bool negativeZero = float.IsNegative(dir.X);
            await Assert.That(negativeZero).IsEqualTo(float.IsNegative(endX)).Because("the subtraction carries the sign of zero into the direction");
            await Assert.That(dir.X).IsEqualTo(0f);

            Vector3 inv = new(1f / dir.X, 1f / dir.Y, 1f / dir.Z);
            bool swapForm = SwapFormSlab(min, max, origin, inv, Eps, len - Eps);
            await Assert.That(swapForm).IsEqualTo(!negativeZero).Because("the swap form admits the box for +0 and rejects it for -0");

            bool oracle = BruteForceOracle.AnyHit(vertices, 1, origin, dir, len, Eps);
            await Assert.That(oracle).IsTrue().Because("the ray crosses the triangle's edge at t = 5, inside the window");
            await Assert.That(bvh.AnyHit(origin, dir, len, Eps)).IsTrue().Because($"dir.X = {(negativeZero ? "-0" : "+0")}: the selected form admits the box");
            int hint = 0;
            await Assert.That(bvh.AnyHit(origin, dir, len, Eps, ref hint, out bool shortCircuited)).IsTrue();
            await Assert.That(shortCircuited).IsTrue().Because("the hinted leaf test runs the same slab arithmetic");
            await Assert.That(bvh.NearestHit(origin, dir, len, 1e-3f, out float t)).IsTrue();
            await Assert.That(t).IsEqualTo(5f);
        }
    }

    /// <summary>
    ///     The same soup twice gives the same tree, bit for bit: every lane box, every reference,
    ///     the slot order and the stack capacity. The build has no sort and no comparer, and its
    ///     parallel parts are deterministic by construction (<c>TriangleBvhParallelBuildTests</c>
    ///     holds every degree of parallelism to the same digests), which is why; but the
    ///     differential and tier digests are only comparable between runs if it stays so, and
    ///     nothing else would notice if it stopped.
    /// </summary>
    [Test]
    public async Task Build_IsDeterministic_RunToRun()
    {
        const int triangles = 5000;
        float[] vertices = RandomSoup(new Random(20260916), triangles, 2000f);
        TriangleBvh first = TriangleBvh.Build(vertices, triangles);
        TriangleBvh second = TriangleBvh.Build((float[])vertices.Clone(), triangles);

        await Assert.That(second.NodeCount).IsEqualTo(first.NodeCount);
        await Assert.That(second.Depth).IsEqualTo(first.Depth);
        await Assert.That(second.StackCapacity).IsEqualTo(first.StackCapacity);
        await Assert.That(second.Min).IsEqualTo(first.Min);
        await Assert.That(second.Max).IsEqualTo(first.Max);
        int laneMismatches = 0, slotMismatches = 0;
        for (int lane = 0; lane < first.NodeCount * TriangleBvh.Width; lane++)
        {
            (Vector3 amin, Vector3 amax) = first.LaneBounds(lane);
            (Vector3 bmin, Vector3 bmax) = second.LaneBounds(lane);
            if (first.LaneChild(lane) != second.LaneChild(lane) || !BitsEqual(amin, bmin) || !BitsEqual(amax, bmax))
            {
                laneMismatches++;
            }
        }

        for (int slot = 0; slot < triangles; slot++)
        {
            if (first.TriangleOfSlot(slot) != second.TriangleOfSlot(slot) || first.LaneOfTriangle(slot) != second.LaneOfTriangle(slot))
            {
                slotMismatches++;
            }
        }

        await Assert.That(laneMismatches).IsEqualTo(0);
        await Assert.That(slotMismatches).IsEqualTo(0);
    }

    /// <summary>
    ///     More triangles than a leaf holds, all with the same centroid, so no bin sweep can split
    ///     them: the build halves the range by index instead. Every triangle must still land in
    ///     exactly one slot under a leaf of at most eight, and the answers must match the oracle,
    ///     including the nearest distance, which every copy shares. The triangle is horizontal, so
    ///     every box over it is flat in z and the slab window for the ray straight down is a single
    ///     point: the inclusive compare at the end of the slab test is what admits it, and this is
    ///     the one rejection class the differential harness cannot tell from rounding (a flat box
    ///     has a zero-width window in double precision too), so it is pinned here. Watched failing
    ///     under a strict compare.
    /// </summary>
    /// <param name="copies">Identical triangles in the soup.</param>
    [Test]
    [Arguments(9)]
    [Arguments(64)]
    public async Task CoincidentCentroids_PastTheLeafSize_AreSplitByHalves(int copies)
    {
        float[] one = [100f, 100f, 50f, 140f, 100f, 50f, 100f, 140f, 50f];
        float[] vertices = new float[copies * 9];
        for (int i = 0; i < copies; i++)
        {
            one.CopyTo(vertices, i * 9);
        }

        TriangleBvh bvh = TriangleBvh.Build(vertices, copies);
        await Assert.That(bvh.TriangleCount).IsEqualTo(copies);
        int[] seen = new int[copies];
        int leaves = 0;
        for (int lane = 0; lane < bvh.NodeCount * TriangleBvh.Width; lane++)
        {
            int child = bvh.LaneChild(lane);
            if (child == TriangleBvh.EmptyLane || !TriangleBvh.IsLeaf(child))
            {
                continue;
            }

            leaves++;
            (int start, int count) = TriangleBvh.DecodeLeaf(child);
            await Assert.That(count).IsGreaterThan(0);
            await Assert.That(count).IsLessThanOrEqualTo(8).Because("a leaf holds at most eight triangles, the build's leaf size");
            for (int s = start; s < start + count; s++)
            {
                seen[bvh.TriangleOfSlot(s)]++;
            }
        }

        await Assert.That(leaves).IsGreaterThanOrEqualTo((copies + 7) / 8);
        await Assert.That(seen.All(n => n == 1)).IsTrue().Because("every copy sits in exactly one slot");

        Vector3 a = new(110f, 110f, 100f), b = new(110f, 110f, 0f);
        Vector3 dir = new(0f, 0f, -1f);
        await Assert.That(bvh.AnyHit(a, dir, 100f, Eps)).IsTrue();
        await Assert.That(bvh.NearestHit(a, dir, 100f, 1e-3f, out float t)).IsTrue();
        await Assert.That(BruteForceOracle.NearestHit(vertices, copies, a, dir, 100f, 1e-3f, out float ot, out _)).IsTrue();
        await Assert.That(t).IsEqualTo(ot);
        await Assert.That(bvh.AnyHit(b + new Vector3(50f, 0f, 0f), dir, 100f, Eps)).IsFalse().Because("a ray beside the stack misses it");
    }

    /// <summary>
    ///     Eight compact clusters, one per octant of a cube, so the root's eight lanes are the eight
    ///     clusters. A segment from the centre of cluster <c>k</c> to the centre of the opposite
    ///     cluster passes through exactly those two boxes, and the near one has to be tested first
    ///     whichever way the ray points: eight directions, every sign combination, plus two
    ///     axis-aligned rays whose zero components make the inverse direction infinite. The recorded
    ///     order of node visits is the proof: the near cluster's subtree root must pop right after
    ///     the tree root. Under a far-first push order the far cluster pops first on every ray,
    ///     which was watched failing.
    /// </summary>
    [Test]
    public async Task PopOrder_NearestClusterIsTestedFirst_ForEveryOctant()
    {
        const float D = 1000f;
        const int perCluster = 9;
        float[] vertices = new float[8 * perCluster * 9];
        Vector3[] centres = new Vector3[8];
        Vector3[] offsets =
        [
            new(6f, 0f, 0f), new(-6f, 0f, 0f), new(0f, 6f, 0f), new(0f, -6f, 0f), new(0f, 0f, 6f), new(0f, 0f, -6f),
            new(4.2f, -4.2f, 0f), new(-4.2f, 4.2f, 0f), new(4.2f, 0f, -4.2f)
        ];
        for (int k = 0; k < 8; k++)
        {
            centres[k] = new Vector3((k & 1) != 0 ? D : -D, (k & 2) != 0 ? D : -D, (k & 4) != 0 ? D : -D);
            for (int j = 0; j < perCluster; j++)
            {
                Vector3 p = centres[k] + offsets[j];
                // A triangle of radius about half a unit around p, in a plane that faces the offset.
                Vector3 n = Vector3.Normalize(offsets[j]);
                Vector3 u = Vector3.Normalize(Vector3.Cross(n, MathF.Abs(n.Z) < 0.9f ? Vector3.UnitZ : Vector3.UnitX));
                Vector3 w = Vector3.Cross(n, u);
                Vector3 v0 = p + (u * 0.5f);
                Vector3 v1 = p - (u * 0.25f) + (w * 0.45f);
                Vector3 v2 = p - (u * 0.25f) - (w * 0.45f);
                int b = ((k * perCluster) + j) * 9;
                vertices[b] = v0.X;
                vertices[b + 1] = v0.Y;
                vertices[b + 2] = v0.Z;
                vertices[b + 3] = v1.X;
                vertices[b + 4] = v1.Y;
                vertices[b + 5] = v1.Z;
                vertices[b + 6] = v2.X;
                vertices[b + 7] = v2.Y;
                vertices[b + 8] = v2.Z;
            }
        }

        TriangleBvh bvh = TriangleBvh.Build(vertices, 8 * perCluster);
        int rootLanesUsed = 0;
        for (int lane = 0; lane < TriangleBvh.Width; lane++)
        {
            if (bvh.LaneChild(lane) != TriangleBvh.EmptyLane)
            {
                rootLanesUsed++;
            }
        }

        await Assert.That(rootLanesUsed).IsEqualTo(8).Because("eight separated clusters fill the root");

        // The line through two opposite centres is shifted a little so it passes through both
        // cluster boxes without touching any of the tiny triangles.
        Vector3 shift = new(0f, 1.5f, 1.5f);
        for (int k = 0; k < 8; k++)
        {
            await AssertOrder(bvh, centres[k] + shift, centres[7 - k] + shift, k, 7 - k, centres);
        }

        // Axis-aligned: cluster 0 (-,-,-) to cluster 1 (+,-,-) along +X and back along -X, so the
        // y and z direction components are exactly zero.
        await AssertOrder(bvh, centres[0] + shift, centres[1] + shift, 0, 1, centres);
        await AssertOrder(bvh, centres[1] + shift, centres[0] + shift, 1, 0, centres);
        await AssertOrder(bvh, centres[2] + shift, centres[6] + shift, 2, 6, centres);
        await AssertOrder(bvh, centres[6] + shift, centres[2] + shift, 6, 2, centres);
    }

    /// <summary>
    ///     Over a random soup: every triangle occupies exactly one leaf slot, every leaf lane's box
    ///     holds its triangles, every inner lane's box holds its child lanes' boxes, the root bounds
    ///     are the union, and the hinted leaf lane of a triangle is the leaf holding it. Then ten
    ///     thousand rays agree with the oracle on <c>AnyHit</c> and on <c>NearestHit</c>'s distance,
    ///     bit for bit.
    /// </summary>
    [Test]
    public async Task RandomSoup_StructureIsConsistent_AndAnswersMatchTheOracle()
    {
        Random rng = new(20260916);
        const int triangles = 5000;
        float[] vertices = RandomSoup(rng, triangles, 2000f);
        TriangleBvh bvh = TriangleBvh.Build(vertices, triangles);

        await Assert.That(bvh.TriangleCount).IsEqualTo(triangles);
        await Assert.That(bvh.NodeCount).IsGreaterThan(1);
        await Assert.That(bvh.StackCapacity).IsGreaterThan(1);

        int[] seen = new int[triangles];
        int leafLanes = 0, innerLanes = 0, emptyLanes = 0;
        for (int lane = 0; lane < bvh.NodeCount * TriangleBvh.Width; lane++)
        {
            int child = bvh.LaneChild(lane);
            (Vector3 min, Vector3 max) = bvh.LaneBounds(lane);
            if (child == TriangleBvh.EmptyLane)
            {
                emptyLanes++;
                continue;
            }

            if (TriangleBvh.IsLeaf(child))
            {
                leafLanes++;
                (int start, int count) = TriangleBvh.DecodeLeaf(child);
                await Assert.That(count).IsGreaterThan(0);
                for (int s = start; s < start + count; s++)
                {
                    int t = bvh.TriangleOfSlot(s);
                    seen[t]++;
                    await Assert.That(bvh.LaneOfTriangle(t)).IsEqualTo(lane).Because("the hint path must find the leaf holding the triangle");
                    for (int v = 0; v < 3; v++)
                    {
                        Vector3 p = new(vertices[(t * 9) + (v * 3)], vertices[(t * 9) + (v * 3) + 1], vertices[(t * 9) + (v * 3) + 2]);
                        await Assert.That(p.X >= min.X && p.X <= max.X && p.Y >= min.Y && p.Y <= max.Y && p.Z >= min.Z && p.Z <= max.Z)
                            .IsTrue().Because($"triangle {t} vertex {v} must lie in its leaf box");
                    }
                }
            }
            else
            {
                innerLanes++;
                for (int l = 0; l < TriangleBvh.Width; l++)
                {
                    int sub = (child * TriangleBvh.Width) + l;
                    if (bvh.LaneChild(sub) == TriangleBvh.EmptyLane)
                    {
                        continue;
                    }

                    (Vector3 cmin, Vector3 cmax) = bvh.LaneBounds(sub);
                    await Assert.That(cmin.X >= min.X && cmin.Y >= min.Y && cmin.Z >= min.Z && cmax.X <= max.X && cmax.Y <= max.Y && cmax.Z <= max.Z)
                        .IsTrue().Because("a child lane's box must lie inside its parent lane's box");
                }
            }
        }

        await Assert.That(seen.All(n => n == 1)).IsTrue().Because("every triangle sits in exactly one leaf slot");
        await Assert.That(leafLanes).IsGreaterThan(0);
        await Assert.That(innerLanes).IsGreaterThan(0);
        Console.WriteLine($"[bvh8] soup: {bvh.NodeCount} nodes, {leafLanes} leaf lanes, {innerLanes} inner lanes, "
                          + $"{emptyLanes} empty lanes, depth {bvh.Depth}, stack {bvh.StackCapacity}");

        Vector3 lo = new(-2500f, -2500f, -300f), hi = new(2500f, 2500f, 500f);
        int occluded = 0, anyMismatch = 0, nearestMismatch = 0;
        for (int i = 0; i < 10_000; i++)
        {
            Vector3 a = RandomPoint(rng, lo, hi);
            Vector3 b = RandomPoint(rng, lo, hi);
            Vector3 delta = b - a;
            float len = delta.Length();
            Vector3 dir = delta / len;
            bool oracle = BruteForceOracle.AnyHit(vertices, triangles, a, dir, len, Eps);
            if (bvh.AnyHit(a, dir, len, Eps) != oracle)
            {
                anyMismatch++;
            }

            bool oracleNear = BruteForceOracle.NearestHit(vertices, triangles, a, dir, len, 1e-3f, out float ot, out _);
            bool near = bvh.NearestHit(a, dir, len, 1e-3f, out float t);
            if (near != oracleNear || BitConverter.SingleToInt32Bits(t) != BitConverter.SingleToInt32Bits(ot))
            {
                nearestMismatch++;
            }

            if (oracle)
            {
                occluded++;
            }
        }

        Console.WriteLine($"[bvh8] soup rays: {occluded} of 10000 occluded, {anyMismatch} AnyHit mismatches, {nearestMismatch} NearestHit mismatches");
        await Assert.That(occluded).IsGreaterThan(1000);
        await Assert.That(10_000 - occluded).IsGreaterThan(1000);
        await Assert.That(anyMismatch).IsEqualTo(0);
        await Assert.That(nearestMismatch).IsEqualTo(0);
    }

    /// <summary>
    ///     A NaN in the origin or the direction makes every slab comparison false, so without a guard
    ///     every lane passes, empty ones included, and the traversal pushes eight references per node
    ///     into a stack sized for the real lanes only: an <see cref="IndexOutOfRangeException" /> as
    ///     soon as a node has fewer real lanes than the eight pushed, which a root with three
    ///     children guarantees at the first pop. The binary tree answered false on the same input
    ///     (its stack was a fixed 64 and its depth about 20), so false is the verdict the guard has
    ///     to keep. Reachable from <see cref="VisibilityEngine.Raycast" /> through a zero direction,
    ///     which <see cref="Vector3.Normalize" /> turns into NaN. Watched failing before the guard
    ///     landed: the empty-lane assertion in the Debug build, the exception in Release.
    /// </summary>
    /// <param name="triangles">Soup size: three separated triangles (a three-lane root) or a random soup with depth.</param>
    [Test]
    [Arguments(3)]
    [Arguments(5000)]
    public async Task NaNOriginOrDirection_IsAMiss_AndDoesNotOverflowTheStack(int triangles)
    {
        Random rng = new(20260922);
        float[] vertices = triangles == 3 ? SeparatedTriangles(3) : RandomSoup(rng, triangles, 2000f);
        TriangleBvh bvh = TriangleBvh.Build(vertices, triangles);
        Vector3 dir = Vector3.Normalize(new Vector3(1f, 2f, 3f));
        Vector3 origin = new(10f, 20f, 30f);
        Vector3[] origins = [new(float.NaN, 20f, 30f), new(10f, float.NaN, 30f), new(10f, 20f, float.NaN), origin, origin];
        Vector3[] dirs = [dir, dir, dir, new(float.NaN, dir.Y, dir.Z), Vector3.Normalize(Vector3.Zero)];
        for (int i = 0; i < origins.Length; i++)
        {
            Vector3 o = origins[i], d = dirs[i];
            await Assert.That(bvh.AnyHit(o, d, 3000f, Eps)).IsFalse().Because($"case {i}: a NaN ray hits nothing");
            int hint = 7;
            await Assert.That(bvh.AnyHit(o, d, 3000f, Eps, ref hint)).IsFalse().Because($"case {i}: hinted");
            await Assert.That(hint).IsEqualTo(7).Because($"case {i}: a miss leaves the hint alone");
            await Assert.That(bvh.NearestHit(o, d, 3000f, 1e-3f, out float t)).IsFalse().Because($"case {i}: nearest");
            await Assert.That(t).IsEqualTo(float.MaxValue);
        }

        VisibilityEngine engine = VisibilityEngine.FromTriangles(vertices, triangles);
        await Assert.That(engine.Raycast(origin, Vector3.Zero, 3000f, out float distance)).IsFalse()
            .Because("a zero direction normalises to NaN and must read as a miss, not a crash");
        await Assert.That(distance).IsEqualTo(float.MaxValue);
    }

    [Test]
    public async Task Build_RejectsASoupShorterThanItsCount()
    {
        ArgumentOutOfRangeException e = Assert.Throws<ArgumentOutOfRangeException>(() => _ = TriangleBvh.Build(new float[9], 2));
        await Assert.That(e.ParamName).IsEqualTo("count");
    }

    // The near cluster's subtree root must be the first node popped after the tree root, the far
    // cluster's must be popped later, and no other cluster's may be popped at all. Node visits are
    // the evidence rather than triangle tests: the clusters' triangles are tiny, so the ray crosses
    // each cluster's box without crossing any leaf box inside it.
    private static async Task AssertOrder(TriangleBvh bvh, Vector3 a, Vector3 b, int nearCluster, int farCluster, Vector3[] centres)
    {
        Vector3 delta = b - a;
        float len = delta.Length();
        Vector3 dir = delta / len;
        TraversalTrace trace = TraversalTrace.Create();
        int hint = -1;
        bool hit = bvh.AnyHit(a, dir, len, Eps, ref hint, out _, ref trace);
        await Assert.That(hit).IsFalse().Because($"the ray from cluster {nearCluster} to {farCluster} must miss every triangle so both clusters are traversed");

        int[] subtreeOf = new int[8];
        for (int k = 0; k < 8; k++)
        {
            subtreeOf[k] = -1;
            for (int lane = 0; lane < TriangleBvh.Width; lane++)
            {
                (Vector3 min, Vector3 max) = bvh.LaneBounds(lane);
                Vector3 c = centres[k];
                if (c.X >= min.X && c.X <= max.X && c.Y >= min.Y && c.Y <= max.Y && c.Z >= min.Z && c.Z <= max.Z)
                {
                    subtreeOf[k] = bvh.LaneChild(lane);
                }
            }

            await Assert.That(subtreeOf[k]).IsGreaterThanOrEqualTo(0).Because($"cluster {k} is an inner lane of the root");
        }

        await Assert.That(trace.Nodes.Count).IsGreaterThanOrEqualTo(3).Because("the root and both clusters' subtree roots are popped");
        await Assert.That(trace.Nodes[0]).IsEqualTo(0);
        await Assert.That(trace.Nodes[1]).IsEqualTo(subtreeOf[nearCluster])
            .Because($"dir=({dir.X:F2},{dir.Y:F2},{dir.Z:F2}): the near cluster {nearCluster} pops first, not {Array.IndexOf(subtreeOf, trace.Nodes[1])}");
        await Assert.That(trace.Nodes.Contains(subtreeOf[farCluster])).IsTrue().Because("the far cluster is reached too");
        for (int k = 0; k < 8; k++)
        {
            if (k != nearCluster && k != farCluster)
            {
                await Assert.That(trace.Nodes.Contains(subtreeOf[k])).IsFalse().Because($"cluster {k} is off the line");
            }
        }
    }

    private static bool BitsEqual(Vector3 a, Vector3 b) =>
        BitConverter.SingleToInt32Bits(a.X) == BitConverter.SingleToInt32Bits(b.X)
        && BitConverter.SingleToInt32Bits(a.Y) == BitConverter.SingleToInt32Bits(b.Y)
        && BitConverter.SingleToInt32Bits(a.Z) == BitConverter.SingleToInt32Bits(b.Z);

    // The slab form the binary tree ran until the eight-wide rebuild: both crossings from the
    // minimum and maximum faces, swapped into order when the first is the larger, then the
    // NaN-ignoring compare-select. Kept here only to name which way the negative-zero verdict flips.
    private static bool SwapFormSlab(Vector3 min, Vector3 max, Vector3 o, Vector3 inv, float lo, float hi)
    {
        SwapFormAxis(min.X, max.X, o.X, inv.X, ref lo, ref hi);
        SwapFormAxis(min.Y, max.Y, o.Y, inv.Y, ref lo, ref hi);
        SwapFormAxis(min.Z, max.Z, o.Z, inv.Z, ref lo, ref hi);
        return lo <= hi;
    }

    private static void SwapFormAxis(float min, float max, float o, float inv, ref float lo, ref float hi)
    {
        float t0 = (min - o) * inv, t1 = (max - o) * inv;
        if (t0 > t1)
        {
            (t0, t1) = (t1, t0);
        }

        lo = t0 > lo ? t0 : lo;
        hi = t1 < hi ? t1 : hi;
    }

    // Small separated triangles spaced along x, well away from the origin, so the origin is in no
    // real box and a soup of fewer than eight leaves a root with empty lanes.
    internal static float[] SeparatedTriangles(int count)
    {
        float[] vertices = new float[count * 9];
        for (int i = 0; i < count; i++)
        {
            float x = 300f + (250f * i);
            float[] tri = [x, 40f, 30f, x + 10f, 40f, 30f, x, 50f, 42f];
            tri.CopyTo(vertices, i * 9);
        }

        return vertices;
    }

    private static Vector3 RandomPoint(Random rng, Vector3 lo, Vector3 hi) => new(
        lo.X + ((hi.X - lo.X) * (float)rng.NextDouble()),
        lo.Y + ((hi.Y - lo.Y) * (float)rng.NextDouble()),
        lo.Z + ((hi.Z - lo.Z) * (float)rng.NextDouble()));

    internal static float[] RandomSoup(Random rng, int count, float half)
    {
        float[] v = new float[count * 9];
        for (int i = 0; i < count; i++)
        {
            Vector3 c = new(
                (float)((rng.NextDouble() * 2.0) - 1.0) * half,
                (float)((rng.NextDouble() * 2.0) - 1.0) * half,
                (float)((rng.NextDouble() * 400.0) - 100.0));
            for (int k = 0; k < 3; k++)
            {
                v[(i * 9) + (k * 3)] = c.X + (float)((rng.NextDouble() * 400.0) - 200.0);
                v[(i * 9) + (k * 3) + 1] = c.Y + (float)((rng.NextDouble() * 400.0) - 200.0);
                v[(i * 9) + (k * 3) + 2] = c.Z + (float)((rng.NextDouble() * 200.0) - 100.0);
            }
        }

        return v;
    }
}
