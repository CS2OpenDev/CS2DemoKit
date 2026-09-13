#region

using System.Numerics;
using CS2DemoKit.Analysis.Visibility;

#endregion

namespace CS2DemoKit.Analysis.Tests;

/// <summary>
///     The one property the differential suite structurally cannot check: whether the triangle test
///     is watertight.
///     <para>
///         <see cref="BruteForceOracle" /> carries a verbatim copy of the traversal's
///         Moller-Trumbore body, deliberately, so that a divergence can only ever be a traversal
///         decision. The cost is that a crack in the intersection predicate itself is invisible to
///         every oracle-adjudicated test in the suite: both sides reject the same ray at a seam,
///         both agree the surface is clear, the golden pins that answer, and a ray goes through a
///         wall with nothing to notice.
///     </para>
///     <para>
///         Closed convex geometry is the way out of that circle, because it supplies a ground truth
///         that owes nothing to any intersection code: a ray fired from a point inside a closed
///         convex surface crosses that surface exactly once, whatever the surface is made of. So the
///         census here counts crossings rather than comparing verdicts. Zero means the ray escaped
///         between two triangles; two or more means one seam was accepted by both of its triangles.
///     </para>
///     <para>
///         <b>What it finds, and it is a real defect.</b> Away from the seams the predicate is
///         exact: 48,000 random directions and 11,264 aimed at triangle interiors, across the three
///         fixtures, every one crossing exactly once with both trees and the soup agreeing. Aimed
///         straight at a shared edge or a shared vertex it is not exact at all. On the subdivided
///         sphere 416 of 14,088 seam-aimed rays (3.0%) cross NO triangle — they leave a closed
///         surface without touching it — and 3,251 (23%) cross two or more.
///     </para>
///     <para>
///         The cause is that Moller-Trumbore is not watertight. Two triangles either side of a seam
///         compute the same barycentric from their own <c>e1</c>, <c>e2</c> and their own
///         <c>f = 1/det</c>, so at the seam each evaluates a different float expression of the same
///         exact zero and their signs are independent. Both can reject, which is the leak, or both
///         can accept, which is the double count. The user-visible symptom of the leak is a false
///         CLEAR: a sightline that grazes the join between two brush faces reports no occluder, and
///         a visibility statistic counts a spotting the geometry does not allow. The fix is a
///         watertight formulation — Woop's, with a canonical per-edge ordering so both sides
///         evaluate the identical expression — which is a change to the intersection routine and
///         out of scope here.
///     </para>
///     <para>
///         <b>It predates this PR.</b> The census is taken with <see cref="BruteForceOracle" />'s
///         copy of the intersection body, which is verbatim the pre-PR one, and every ray is also
///         put through <see cref="LegacyTriangleBvh" />, the frozen copy of the tree as it stood
///         before this work. The leak and double-count numbers are the frozen tree's as much as the
///         live tree's, because neither tree's traversal is where the defect lives.
///         <see cref="KnownLimitation_TheTriangleTestLeaksAtSeams_AtThePinnedRate" /> pins both
///         trees' counts side by side so that claim stays checkable rather than remembered.
///     </para>
///     <para>
///         So the tests here divide the ground in two. Away from a seam, perfection is demanded:
///         <see cref="RandomDirectionsFromInside_CrossTheSurfaceExactlyOnce" /> and
///         <see cref="RaysAimedAtTriangleInteriors_CrossTheSurfaceExactlyOnce" /> fail on a single
///         lost ray, which is what a broken rewrite, a bad tolerance or a NaN would produce. At the
///         seam the known defect is measured rather than asserted away: its exact size is pinned, so
///         a future change to the intersection routine — a watertight one that should drive it to
///         zero, or an accidental one that widens it — moves a number and fails loudly. Needs no
///         bake, so all of it runs in CI.
///     </para>
/// </summary>
public class TriangleWatertightnessTests
{
    /// <summary>
    ///     How far from a shared edge, in world units, a lost or doubled ray may exit and still be
    ///     the seam's fault. The crack is ulp-scale: a seam-aimed direction is normalised in float,
    ///     which displaces the exit by a few parts in 1e7 of the cast distance, and the worst
    ///     measured across the three fixtures is 7e-5 units. A hundredth of a unit is two orders
    ///     above that and four below the smallest triangle edge in any fixture, so it separates
    ///     "lost at the seam" from "lost in open surface" without being a number anyone has to tune.
    /// </summary>
    public const double SeamTolerance = 0.01;

    // Near exclusion, matching the frame gate's. Every origin here sits far inside the surface, so
    // nothing is anywhere near it.
    private const float Eps = 1e-3f;

    /// <summary>
    ///     Rays aimed at nothing in particular: the control. A random direction misses every shared
    ///     edge by many orders of magnitude more than an ulp, so anything lost here is not a
    ///     watertightness crack at all — it is a broken intersection routine or a broken traversal.
    /// </summary>
    /// <param name="shape">Which closed convex mesh to fire through.</param>
    [Test]
    [Arguments("cube")]
    [Arguments("icosahedron")]
    [Arguments("sphere")]
    public async Task RandomDirectionsFromInside_CrossTheSurfaceExactlyOnce(string shape)
    {
        ClosedMesh mesh = ClosedMesh.Named(shape);
        Tally tally = Cast(mesh, RandomAims(mesh, 20261001));
        Console.WriteLine($"[watertight] {mesh.Name} random directions: {tally}");

        await Assert.That(tally.Rays).IsGreaterThan(10_000);
        await AssertNothingLost(tally);
    }

    /// <summary>
    ///     Rays aimed at the middle of a triangle, held a tenth of the way clear of every edge. This
    ///     is the other half of the control and the sharper half: the aim is exact, the way the seam
    ///     corpus's aim is exact, and only the seam is missing. Anything lost here cannot be blamed
    ///     on aiming precisely at geometry.
    /// </summary>
    /// <param name="shape">Which closed convex mesh to fire through.</param>
    [Test]
    [Arguments("cube")]
    [Arguments("icosahedron")]
    [Arguments("sphere")]
    public async Task RaysAimedAtTriangleInteriors_CrossTheSurfaceExactlyOnce(string shape)
    {
        ClosedMesh mesh = ClosedMesh.Named(shape);
        Tally tally = Cast(mesh, InteriorAims(mesh, 20261003));
        Console.WriteLine($"[watertight] {mesh.Name} triangle interiors: {tally}");

        await Assert.That(tally.Rays).IsGreaterThan(mesh.TriangleCount * 4);
        await AssertNothingLost(tally);
    }

    /// <summary>
    ///     The known limitation, measured. Rays aimed deliberately at the seams — every shared
    ///     vertex, every shared edge's midpoint, and seeded points along every shared edge, from
    ///     four interior points each — with the exact size of what the triangle test loses there
    ///     pinned per fixture.
    ///     <para>
    ///         This is a characterization, not a correctness check, and the counts are the point.
    ///         They are reproducible because the mesh, the seed and the ray construction are fixed
    ///         and every operation is IEEE, so a change in any of them means the intersection
    ///         routine or the traversal changed. A watertight rewrite should drive
    ///         <paramref name="leaks" /> and <paramref name="doubleCounts" /> to zero, and this test
    ///         is then the evidence that it worked; anything else that moves them wants explaining.
    ///     </para>
    ///     <para>
    ///         <paramref name="liveTreeDisagreements" /> and
    ///         <paramref name="frozenTreeDisagreements" /> are a second, separate defect wearing the
    ///         same symptom: not the triangle test disagreeing with itself, but a node box's float
    ///         slab test culling a leaf whose triangle the soup does hit. Both trees have it and
    ///         neither dominates — the wide tree loses three cube rays the frozen tree keeps, while
    ///         the frozen tree loses 26 more on the icosahedron and 130 more on the sphere — which
    ///         is what identifies it as the float boundary class the differential harness already
    ///         classifies, rather than anything this PR introduced.
    ///     </para>
    ///     <para>
    ///         Two things are asserted rather than pinned, because they are what make the pinned
    ///         numbers mean what they say. Every offending ray must exit within
    ///         <see cref="SeamTolerance" /> of a shared edge, computed in double from the convex
    ///         hull's own face planes and owing nothing to the float predicate under test: a ray
    ///         lost in open surface would be a different and far worse defect. And the several
    ///         crossings of a multi-crossing ray must span nearly nothing along the ray, which is
    ///         what makes them one crossing accepted repeatedly rather than a census that has
    ///         miscounted genuinely separate hits.
    ///     </para>
    /// </summary>
    /// <param name="shape">Which closed convex mesh to fire through.</param>
    /// <param name="rays">Rays the seam corpus casts.</param>
    /// <param name="leaks">Rays crossing no triangle at all: the watertightness leak.</param>
    /// <param name="doubleCounts">Rays crossing two or more: one seam accepted by both its triangles.</param>
    /// <param name="liveTreeDisagreements">Rays the live tree's node boxes cull though the soup hits them.</param>
    /// <param name="frozenTreeDisagreements">The same for the frozen pre-PR tree.</param>
    [Test]
    [Arguments("cube", 536, 5, 360, 3, 0)]
    [Arguments("icosahedron", 888, 15, 336, 0, 26)]
    [Arguments("sphere", 14088, 416, 3251, 46, 176)]
    public async Task KnownLimitation_TheTriangleTestLeaksAtSeams_AtThePinnedRate(
        string shape, int rays, int leaks, int doubleCounts, int liveTreeDisagreements, int frozenTreeDisagreements)
    {
        ClosedMesh mesh = ClosedMesh.Named(shape);
        Tally tally = Cast(mesh, SeamAims(mesh, 20261002));
        Console.WriteLine($"[watertight] {mesh.Name} seam-aimed: {tally.Report()}");

        await Assert.That(tally.Rays).IsEqualTo(rays)
            .Because("the corpus is fixed; a different ray count means the fixture moved, not the predicate");
        await Assert.That(tally.Leaks).IsEqualTo(leaks)
            .Because("the known non-watertightness of the shared Moller-Trumbore body, pinned. " + tally.Report());
        await Assert.That(tally.MultipleCrossings).IsEqualTo(doubleCounts)
            .Because("the other half of the same defect, pinned. " + tally.Report());
        await Assert.That(tally.TraversalDisagreements).IsEqualTo(liveTreeDisagreements)
            .Because("the live tree's float slab boundary class, pinned. " + tally.Report());
        await Assert.That(tally.LegacyTraversalDisagreements).IsEqualTo(frozenTreeDisagreements)
            .Because("and the frozen tree's, which is what says this is not new. " + tally.Report());

        await Assert.That(tally.WorstOffenderSeamDistance).IsLessThan(SeamTolerance)
            .Because("a ray lost or doubled away from a shared edge is not the seam's doing, it is a "
                     + "broken predicate. " + tally.Report());
        await Assert.That(tally.WorstMultiCrossingSpan).IsLessThan(SeamTolerance)
            .Because("the extra crossings must be one point accepted twice, not two real crossings a "
                     + "convex surface cannot have. " + tally.Report());
    }

    /// <summary>
    ///     The premise every test above rests on: the meshes really are closed, really are convex,
    ///     and really do share their seam vertices rather than holding two copies a hair apart. A
    ///     subdivided mesh whose midpoints were computed once per adjacent triangle would leak for a
    ///     reason that is the fixture's fault and not the predicate's.
    /// </summary>
    /// <param name="shape">Which closed convex mesh to check.</param>
    [Test]
    [Arguments("cube")]
    [Arguments("icosahedron")]
    [Arguments("sphere")]
    public async Task EveryMeshIsClosed_AndItsSeamVerticesAreShared(string shape)
    {
        ClosedMesh mesh = ClosedMesh.Named(shape);

        // Closed: every undirected edge is used by exactly two triangles.
        Dictionary<(int A, int B), int> uses = [];
        for (int tri = 0; tri < mesh.TriangleCount; tri++)
        {
            for (int e = 0; e < 3; e++)
            {
                int i = mesh.Indices[(tri * 3) + e], j = mesh.Indices[(tri * 3) + ((e + 1) % 3)];
                (int, int) key = i < j ? (i, j) : (j, i);
                uses[key] = uses.GetValueOrDefault(key) + 1;
            }
        }

        await Assert.That(uses.Values.Count(c => c != 2)).IsEqualTo(0)
            .Because("a mesh with a boundary or a T-junction cannot support the one-crossing invariant");
        await Assert.That(uses.Count).IsEqualTo(mesh.Edges.Count);

        // Shared, not duplicated: no two distinct vertices sit within a hundredth of a unit.
        int coincident = 0;
        for (int i = 0; i < mesh.Positions.Length; i++)
        {
            for (int j = i + 1; j < mesh.Positions.Length; j++)
            {
                coincident += (mesh.Positions[i] - mesh.Positions[j]).Length() < 1e-2f ? 1 : 0;
            }
        }

        await Assert.That(coincident).IsEqualTo(0)
            .Because("two vertices at the same place means a seam was built twice, not shared");

        // Convex: every vertex is on the inner side of every face plane, which is what makes "one
        // crossing per ray from inside" a theorem rather than a hope.
        int nonConvex = 0;
        for (int tri = 0; tri < mesh.TriangleCount; tri++)
        {
            for (int v = 0; v < mesh.Positions.Length; v++)
            {
                nonConvex += mesh.OutwardDistance(tri, mesh.Positions[v]) > 1e-2 ? 1 : 0;
            }
        }

        await Assert.That(nonConvex).IsEqualTo(0).Because("the fixtures must be convex for the invariant to hold");
    }

    private static async Task AssertNothingLost(Tally tally)
    {
        await Assert.That(tally.Leaks).IsEqualTo(0)
            .Because("a ray that leaves a closed surface without crossing it is a ray through a wall. "
                     + tally.Report());
        await Assert.That(tally.MultipleCrossings).IsEqualTo(0)
            .Because("a convex surface has one exit; two means one crossing was counted twice. " + tally.Report());
        await Assert.That(tally.TraversalDisagreements).IsEqualTo(0)
            .Because("the tree must find every crossing the soup finds. " + tally.Report());
        await Assert.That(tally.LegacyTraversalDisagreements).IsEqualTo(0)
            .Because("and so must the frozen one, which is what makes a difference between them "
                     + "attributable to this PR. " + tally.Report());
    }

    // Fires every aim and counts crossings two ways: the soup, triangle by triangle, with the same
    // Moller-Trumbore body the traversal uses, and the tree. The soup count is the census (zero is a
    // leak, two is a double count); the tree is held against it so a traversal that drops a crossing
    // the soup found cannot hide behind a clean census.
    private static Tally Cast(ClosedMesh mesh, List<(Vector3 Origin, Vector3 Direction)> aims)
    {
        TriangleBvh bvh = TriangleBvh.Build(mesh.Soup, mesh.TriangleCount);
        LegacyTriangleBvh legacy = LegacyTriangleBvh.Build(mesh.Soup, mesh.TriangleCount);
        Tally tally = new();
        for (int i = 0; i < aims.Count; i++)
        {
            (Vector3 origin, Vector3 dir) = aims[i];
            int crossings = BruteForceOracle.CountHits(
                mesh.Soup, mesh.TriangleCount, origin, dir, mesh.Far, Eps, out float nearest, out float farthest);
            bool traversed = bvh.NearestHit(origin, dir, mesh.Far, Eps, out _);
            bool frozen = legacy.NearestHit(origin, dir, mesh.Far, Eps, out _);
            tally.Add(mesh, origin, dir, crossings, nearest, farthest, traversed, frozen);
        }

        return tally;
    }

    // Uniform directions on the sphere from seeded interior points. Nothing is aimed at anything.
    private static List<(Vector3 Origin, Vector3 Direction)> RandomAims(ClosedMesh mesh, int seed)
    {
        Random rng = new(seed);
        List<(Vector3, Vector3)> aims = new(16_000);
        foreach (Vector3 origin in mesh.InteriorPoints(rng, 4))
        {
            for (int i = 0; i < 4000; i++)
            {
                aims.Add((origin, RandomDirection(rng)));
            }
        }

        return aims;
    }

    // Seeded barycentric points on every triangle, each coordinate kept above a tenth so the target
    // is well clear of all three edges.
    private static List<(Vector3 Origin, Vector3 Direction)> InteriorAims(ClosedMesh mesh, int seed)
    {
        Random rng = new(seed);
        List<(Vector3, Vector3)> aims = new(mesh.TriangleCount * 32);
        foreach (Vector3 origin in mesh.InteriorPoints(rng, 4))
        {
            for (int tri = 0; tri < mesh.TriangleCount; tri++)
            {
                Vector3 a = mesh.Positions[mesh.Indices[tri * 3]];
                Vector3 b = mesh.Positions[mesh.Indices[(tri * 3) + 1]];
                Vector3 c = mesh.Positions[mesh.Indices[(tri * 3) + 2]];
                for (int k = 0; k < 8; k++)
                {
                    float u = 0.1f + ((float)rng.NextDouble() * 0.7f);
                    float v = 0.1f + ((float)rng.NextDouble() * (0.8f - u));
                    aims.Add((origin, Vector3.Normalize(a + ((b - a) * u) + ((c - a) * v) - origin)));
                }
            }
        }

        return aims;
    }

    // Every shared vertex, every shared edge's midpoint, and seeded points along every shared edge,
    // from each interior point. The direction is normalised from the interior point to the target
    // the way any caller would compute it, so the ray lands within an ulp or two of the seam rather
    // than exactly on it — which is the realistic case and the one the predicate has to survive.
    private static List<(Vector3 Origin, Vector3 Direction)> SeamAims(ClosedMesh mesh, int seed)
    {
        Random rng = new(seed);
        List<(Vector3, Vector3)> aims = new(mesh.Edges.Count * 32);
        foreach (Vector3 origin in mesh.InteriorPoints(rng, 4))
        {
            for (int v = 0; v < mesh.Positions.Length; v++)
            {
                aims.Add((origin, Vector3.Normalize(mesh.Positions[v] - origin)));
            }

            for (int e = 0; e < mesh.Edges.Count; e++)
            {
                (int a, int b) = mesh.Edges[e];
                Vector3 pa = mesh.Positions[a], pb = mesh.Positions[b];
                aims.Add((origin, Vector3.Normalize(((pa + pb) * 0.5f) - origin)));
                for (int k = 0; k < 6; k++)
                {
                    float s = (float)rng.NextDouble();
                    aims.Add((origin, Vector3.Normalize(pa + ((pb - pa) * s) - origin)));
                }
            }
        }

        return aims;
    }

    private static Vector3 RandomDirection(Random rng)
    {
        // Uniform on the sphere: z uniform in [-1, 1], azimuth uniform in [0, 2pi).
        double z = (rng.NextDouble() * 2.0) - 1.0;
        double a = rng.NextDouble() * Math.Tau;
        double r = Math.Sqrt(Math.Max(0.0, 1.0 - (z * z)));
        return Vector3.Normalize(new Vector3((float)(r * Math.Cos(a)), (float)(r * Math.Sin(a)), (float)z));
    }

    // Counts by class, for the live tree and for the frozen pre-PR one side by side, and for every
    // offending ray measures how far from a shared edge it leaves the surface and how far apart its
    // several "crossings" actually are. The first few are kept verbatim so a failure is reproducible
    // from the message alone.
    private sealed class Tally
    {
        private readonly List<string> _offenders = [];
        private readonly List<string> _disagreements = [];

        public int Rays { get; private set; }

        public int Leaks { get; private set; }

        public int MultipleCrossings { get; private set; }

        public int TraversalDisagreements { get; private set; }

        /// <summary>The same count for <see cref="LegacyTriangleBvh" />, the frozen pre-PR tree.</summary>
        public int LegacyTraversalDisagreements { get; private set; }

        /// <summary>Furthest from a shared edge, in world units, that any offending ray left the surface.</summary>
        public double WorstOffenderSeamDistance { get; private set; }

        /// <summary>
        ///     Widest span, in world units, between the nearest and farthest "crossing" of a ray that
        ///     crossed more than once. A span of nothing says the extra crossings are one point
        ///     accepted several times — a seam counted twice — rather than the ray really entering
        ///     and leaving the body again, which on a convex surface it cannot do.
        /// </summary>
        public double WorstMultiCrossingSpan { get; private set; }

        public void Add(
            ClosedMesh mesh, Vector3 origin, Vector3 dir, int crossings, float nearest, float farthest,
            bool traversed, bool frozen)
        {
            Rays++;
            bool disagrees = traversed != crossings > 0;
            if (disagrees)
            {
                TraversalDisagreements++;
            }

            if (frozen != crossings > 0)
            {
                LegacyTraversalDisagreements++;
            }

            if (crossings == 0)
            {
                Leaks++;
            }
            else if (crossings > 1)
            {
                MultipleCrossings++;
                WorstMultiCrossingSpan = Math.Max(WorstMultiCrossingSpan, farthest - (double)nearest);
            }
            else if (!disagrees)
            {
                return;
            }

            (double exit, double seam) = mesh.ExitAndSeamDistance(origin, dir);
            WorstOffenderSeamDistance = Math.Max(WorstOffenderSeamDistance, seam);
            string line =
                $"{mesh.Name}: origin=({origin.X:R},{origin.Y:R},{origin.Z:R}) "
                + $"dir=({dir.X:R},{dir.Y:R},{dir.Z:R}) crossings={crossings} "
                + $"tree={(traversed ? "hit" : "miss")} frozen={(frozen ? "hit" : "miss")} "
                + $"exit={exit:F4} seam-distance={seam:E3}"
                + (crossings > 1 ? $" span={farthest - (double)nearest:E3}" : string.Empty);

            // Kept apart because they are different defects wearing the same symptom: a census of 0
            // or 2 is the triangle test disagreeing with itself across a seam, while a disagreement
            // is the node box's float slab test culling a leaf whose triangle the soup does hit.
            // Reporting only the first eight of everything would bury the rarer one.
            if (disagrees && _disagreements.Count < 4)
            {
                _disagreements.Add(line);
            }
            else if (_offenders.Count < 8)
            {
                _offenders.Add(line);
            }
        }

        public override string ToString() =>
            $"{Rays} rays, {Leaks} leaks, {MultipleCrossings} double counts (widest span "
            + $"{WorstMultiCrossingSpan:E3}), {TraversalDisagreements} where the live tree and the soup "
            + $"disagree, {LegacyTraversalDisagreements} where the frozen tree does, "
            + $"worst offender {WorstOffenderSeamDistance:E3} from a shared edge";

        public string Report() => _offenders.Count + _disagreements.Count == 0
            ? ToString()
            : ToString() + Environment.NewLine
              + string.Join(Environment.NewLine, _disagreements.Concat(_offenders));
    }

    // A closed convex triangle mesh with its seam topology and its face planes kept alongside, so a
    // ray can be aimed at a shared edge rather than found near one by luck, and a lost ray can be
    // told where it should have left the surface.
    //
    // Positions are shared by index, so a seam's two triangles read bit-identical vertices; that is
    // the precondition for a leak here being the predicate's fault rather than the fixture's, and
    // EveryMeshIsClosed_AndItsSeamVerticesAreShared asserts it.
    private sealed class ClosedMesh
    {
        // One outward unit normal and its plane offset per triangle, in double. Double because it is
        // the ground truth the float predicate is measured against: computing it in float would put
        // the measurement in the same precision as the thing being measured.
        private readonly double[] _planes;

        private ClosedMesh(string name, Vector3[] positions, int[] indices, Vector3 centre, float radius)
        {
            Name = name;
            Positions = positions;
            Indices = indices;
            Centre = centre;
            Radius = radius;
            Far = radius * 8f;

            float[] soup = new float[indices.Length * 3];
            for (int i = 0; i < indices.Length; i++)
            {
                Vector3 p = positions[indices[i]];
                soup[(i * 3) + 0] = p.X;
                soup[(i * 3) + 1] = p.Y;
                soup[(i * 3) + 2] = p.Z;
            }

            Soup = soup;

            HashSet<(int, int)> edges = [];
            for (int tri = 0; tri < indices.Length / 3; tri++)
            {
                for (int e = 0; e < 3; e++)
                {
                    int i = indices[(tri * 3) + e], j = indices[(tri * 3) + ((e + 1) % 3)];
                    edges.Add(i < j ? (i, j) : (j, i));
                }
            }

            Edges = [.. edges.OrderBy(x => x.Item1).ThenBy(x => x.Item2)];

            _planes = new double[indices.Length / 3 * 4];
            for (int tri = 0; tri < indices.Length / 3; tri++)
            {
                Vector3 a = positions[indices[tri * 3]];
                double ux = positions[indices[(tri * 3) + 1]].X - (double)a.X;
                double uy = positions[indices[(tri * 3) + 1]].Y - (double)a.Y;
                double uz = positions[indices[(tri * 3) + 1]].Z - (double)a.Z;
                double vx = positions[indices[(tri * 3) + 2]].X - (double)a.X;
                double vy = positions[indices[(tri * 3) + 2]].Y - (double)a.Y;
                double vz = positions[indices[(tri * 3) + 2]].Z - (double)a.Z;
                double nx = (uy * vz) - (uz * vy), ny = (uz * vx) - (ux * vz), nz = (ux * vy) - (uy * vx);
                double len = Math.Sqrt((nx * nx) + (ny * ny) + (nz * nz));
                nx /= len;
                ny /= len;
                nz /= len;
                double offset = (nx * a.X) + (ny * a.Y) + (nz * a.Z);
                // Orient outward: the centre must be on the negative side.
                if ((nx * centre.X) + (ny * centre.Y) + (nz * centre.Z) > offset)
                {
                    (nx, ny, nz, offset) = (-nx, -ny, -nz, -offset);
                }

                _planes[(tri * 4) + 0] = nx;
                _planes[(tri * 4) + 1] = ny;
                _planes[(tri * 4) + 2] = nz;
                _planes[(tri * 4) + 3] = offset;
            }
        }

        public string Name { get; }

        public Vector3[] Positions { get; }

        public int[] Indices { get; }

        public IReadOnlyList<(int A, int B)> Edges { get; }

        public float[] Soup { get; }

        public int TriangleCount => Indices.Length / 3;

        public Vector3 Centre { get; }

        public float Radius { get; }

        /// <summary>Far limit for a cast: past the surface from anywhere inside it.</summary>
        public float Far { get; }

        /// <summary>
        ///     The named fixture. All three sit at map-like coordinates rather than at the origin:
        ///     float spacing at a thousand units is a thousand times coarser than at one, and the
        ///     geometry this ships against is map geometry.
        /// </summary>
        /// <param name="shape">"cube", "icosahedron" or "sphere".</param>
        public static ClosedMesh Named(string shape) => shape switch
        {
            "cube" => Cube(new Vector3(1024f, -512f, 128f), 256f),
            "icosahedron" => Icosahedron(new Vector3(-1536f, 768f, 64f), 384f, 0),
            "sphere" => Icosahedron(new Vector3(512f, 2048f, -320f), 448f, 2),
            _ => throw new ArgumentOutOfRangeException(nameof(shape), shape, "unknown fixture"),
        };

        /// <summary>Seeded points strictly inside the surface, the centre first.</summary>
        /// <param name="rng">Seeded generator.</param>
        /// <param name="count">How many points, including the centre.</param>
        public List<Vector3> InteriorPoints(Random rng, int count)
        {
            List<Vector3> points = new(count) { Centre };
            while (points.Count < count)
            {
                // A quarter of the circumradius is inside the smallest inradius any of the three
                // fixtures has (the cube's, at Radius / sqrt 3).
                float reach = Radius * 0.25f;
                points.Add(Centre + new Vector3(
                    (float)((rng.NextDouble() * 2.0) - 1.0) * reach,
                    (float)((rng.NextDouble() * 2.0) - 1.0) * reach,
                    (float)((rng.NextDouble() * 2.0) - 1.0) * reach));
            }

            return points;
        }

        /// <summary>How far outside triangle <paramref name="tri" />'s plane a point lies, in double.</summary>
        /// <param name="tri">Triangle index.</param>
        /// <param name="p">The point.</param>
        public double OutwardDistance(int tri, Vector3 p) =>
            (_planes[(tri * 4) + 0] * p.X) + (_planes[(tri * 4) + 1] * p.Y) + (_planes[(tri * 4) + 2] * p.Z)
            - _planes[(tri * 4) + 3];

        /// <summary>
        ///     Where the ray leaves the surface, and how far that point is from the nearest shared
        ///     edge. Both in double, from the convex hull's face planes: the exit of a ray from
        ///     inside a convex body is the nearest positive crossing of a plane the ray is leaving
        ///     through, which needs no triangle test at all and so owes nothing to the predicate
        ///     being measured.
        /// </summary>
        /// <param name="origin">Ray origin, inside the surface.</param>
        /// <param name="dir">Unit direction.</param>
        public (double Exit, double SeamDistance) ExitAndSeamDistance(Vector3 origin, Vector3 dir)
        {
            double best = double.PositiveInfinity;
            for (int tri = 0; tri < TriangleCount; tri++)
            {
                double nd = (_planes[(tri * 4) + 0] * dir.X) + (_planes[(tri * 4) + 1] * dir.Y)
                            + (_planes[(tri * 4) + 2] * dir.Z);
                if (nd <= 0.0)
                {
                    continue;
                }

                double t = -OutwardDistance(tri, origin) / nd;
                if (t > 0.0 && t < best)
                {
                    best = t;
                }
            }

            double px = origin.X + (best * dir.X), py = origin.Y + (best * dir.Y), pz = origin.Z + (best * dir.Z);
            double seam = double.PositiveInfinity;
            for (int e = 0; e < Edges.Count; e++)
            {
                (int a, int b) = Edges[e];
                seam = Math.Min(seam, PointSegmentDistance(px, py, pz, Positions[a], Positions[b]));
            }

            return (best, seam);
        }

        private static double PointSegmentDistance(double px, double py, double pz, Vector3 a, Vector3 b)
        {
            double vx = b.X - (double)a.X, vy = b.Y - (double)a.Y, vz = b.Z - (double)a.Z;
            double wx = px - a.X, wy = py - a.Y, wz = pz - a.Z;
            double vv = (vx * vx) + (vy * vy) + (vz * vz);
            double s = vv > 0.0 ? Math.Clamp(((wx * vx) + (wy * vy) + (wz * vz)) / vv, 0.0, 1.0) : 0.0;
            double dx = wx - (s * vx), dy = wy - (s * vy), dz = wz - (s * vz);
            return Math.Sqrt((dx * dx) + (dy * dy) + (dz * dz));
        }

        // Axis-aligned box, two triangles per face. Vertex i carries X in bit 0, Y in bit 1 and Z in
        // bit 2, so the quads below read as the four corners of one face.
        private static ClosedMesh Cube(Vector3 centre, float half)
        {
            Vector3[] p = new Vector3[8];
            for (int i = 0; i < 8; i++)
            {
                p[i] = centre + new Vector3(
                    (i & 1) == 0 ? -half : half,
                    (i & 2) == 0 ? -half : half,
                    (i & 4) == 0 ? -half : half);
            }

            int[] indices =
            [
                0, 2, 6, 0, 6, 4, // X = -half
                1, 3, 7, 1, 7, 5, // X = +half
                0, 1, 5, 0, 5, 4, // Y = -half
                2, 3, 7, 2, 7, 6, // Y = +half
                0, 1, 3, 0, 3, 2, // Z = -half
                4, 5, 7, 4, 7, 6, // Z = +half
            ];

            return new ClosedMesh("cube", p, indices, centre, half * MathF.Sqrt(3f));
        }

        // Regular icosahedron, optionally subdivided `levels` times with every midpoint projected
        // back onto the sphere. Midpoints are memoised on the ordered index pair, so the two
        // triangles either side of a seam get the SAME vertex rather than two computed twice.
        private static ClosedMesh Icosahedron(Vector3 centre, float radius, int levels)
        {
            float t = (1f + MathF.Sqrt(5f)) / 2f;
            List<Vector3> unit =
            [
                Vector3.Normalize(new Vector3(-1f, t, 0f)), Vector3.Normalize(new Vector3(1f, t, 0f)),
                Vector3.Normalize(new Vector3(-1f, -t, 0f)), Vector3.Normalize(new Vector3(1f, -t, 0f)),
                Vector3.Normalize(new Vector3(0f, -1f, t)), Vector3.Normalize(new Vector3(0f, 1f, t)),
                Vector3.Normalize(new Vector3(0f, -1f, -t)), Vector3.Normalize(new Vector3(0f, 1f, -t)),
                Vector3.Normalize(new Vector3(t, 0f, -1f)), Vector3.Normalize(new Vector3(t, 0f, 1f)),
                Vector3.Normalize(new Vector3(-t, 0f, -1f)), Vector3.Normalize(new Vector3(-t, 0f, 1f)),
            ];

            int[] faces =
            [
                0, 11, 5, 0, 5, 1, 0, 1, 7, 0, 7, 10, 0, 10, 11,
                1, 5, 9, 5, 11, 4, 11, 10, 2, 10, 7, 6, 7, 1, 8,
                3, 9, 4, 3, 4, 2, 3, 2, 6, 3, 6, 8, 3, 8, 9,
                4, 9, 5, 2, 4, 11, 6, 2, 10, 8, 6, 7, 9, 8, 1,
            ];

            for (int level = 0; level < levels; level++)
            {
                Dictionary<(int, int), int> midpoints = [];
                List<int> next = new(faces.Length * 4);
                for (int tri = 0; tri < faces.Length / 3; tri++)
                {
                    int a = faces[tri * 3], b = faces[(tri * 3) + 1], c = faces[(tri * 3) + 2];
                    int ab = Midpoint(unit, midpoints, a, b);
                    int bc = Midpoint(unit, midpoints, b, c);
                    int ca = Midpoint(unit, midpoints, c, a);
                    next.AddRange([a, ab, ca, b, bc, ab, c, ca, bc, ab, bc, ca]);
                }

                faces = [.. next];
            }

            Vector3[] positions = new Vector3[unit.Count];
            for (int i = 0; i < unit.Count; i++)
            {
                positions[i] = centre + (unit[i] * radius);
            }

            return new ClosedMesh(levels == 0 ? "icosahedron" : $"sphere-{levels}", positions, faces, centre, radius);
        }

        private static int Midpoint(List<Vector3> unit, Dictionary<(int, int), int> cache, int i, int j)
        {
            (int, int) key = i < j ? (i, j) : (j, i);
            if (cache.TryGetValue(key, out int existing))
            {
                return existing;
            }

            unit.Add(Vector3.Normalize((unit[i] + unit[j]) * 0.5f));
            cache[key] = unit.Count - 1;
            return unit.Count - 1;
        }
    }
}
