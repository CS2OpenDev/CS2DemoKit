#region

using CS2DemoKit.Analysis.Visibility;

#endregion

namespace CS2DemoKit.Analysis.Tests;

/// <summary>
///     Pins the tree the builder produces, bit for bit, through <see cref="TriangleBvh.StructuralDigest" />:
///     node count, depth, stack capacity, every lane's box and child, every slot's triangle and lane.
///     A builder change that keeps these digests has changed nothing a ray can observe, so every
///     verdict, hint and nearest distance is unchanged by construction and no differential run is
///     owed. A change that moves one is a tree change, and owes the differential harness against
///     the frozen binary tree with the oracle adjudicating, plus the real-demo counters.
///     <para>
///         What these digests are, and what they are not. This file was added by the one-pass
///         builder's own commit, so nothing in the repository distinguishes the numbers in it from
///         goldens regenerated against the code they check. The two-pass builder they are said to
///         come from (binary SAH tree, then a separate collapse into eight-wide nodes) ran in a
///         worktree that was never committed, and that run's log is not here either. So read them as
///         change detection, which they do give: a builder change that keeps every digest has changed
///         nothing a ray can observe, and the pins were watched failing under a deliberate mutation
///         of the one-pass collapse (largest-box tie broken toward the later child instead of the
///         earlier) on every soup here that reaches a tie, and under a changed lane-array initial
///         capacity on none, which is the point: capacity is not structure. Do not read them as
///         in-tree evidence that the one-pass builder reproduces the two-pass builder's tree; that
///         equivalence rests on the argument in <c>TriangleBvh.Builder</c>'s doc and on a review of
///         it, not on anything this file can run. Closing the gap means freezing the two-pass builder
///         into this assembly the way <c>LegacyTriangleBvh</c> freezes the pre-fix traversal and
///         asserting digest equality across these soups. It has not been done.
///     </para>
///     <para>
///         The soups cover what the real bakes do not: a root that is itself a leaf, fewer children
///         than lanes, coincident centroids past the leaf size (the half-split path), a NaN
///         vertex (NaN boxes through the collapse's largest-box pick and the lane assignment),
///         and a chain that reaches the binary depth cap with more than a leaf's worth left, so
///         the cap's half-split fires; that pin was watched failing with the cap raised to 128.
///     </para>
///     <para>
///         de_mirage, de_inferno, de_anubis, de_cache and de_train joined the real bakes in 2026-09
///         when the maps the app ships were finally given geometry across the board. They were not all
///         missing for the same reason, which is worth recording because the obvious explanation is
///         wrong: the 0.1 baker DID have a collision step and six of the nine bundles it wrote carry a
///         collisionMesh. It was skipped on mirage, inferno and anubis specifically; de_cache kept a
///         reference to a soup whose file was never committed, and the map has since changed under it
///         (1,622,923 triangles recorded against 1,632,062 today); de_train had never been baked at all.
///     </para>
///     <para>
///         de_inferno is the largest tree the suite builds by nearly a factor of three, at 2.73 million
///         triangles and 394,733 nodes, which makes it the most demanding determinism case the parallel
///         builder has.
///     </para>
/// </summary>
[Category("Unit")]
public class TriangleBvhBuildIdentityTests
{
    /// <param name="map">Map whose <c>collision.tris</c> to load.</param>
    /// <param name="expectedDigest">The structural digest pinned for the map; see the class doc before changing it.</param>
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
    public async Task RealBake_StructuralDigest_IsPinned(string map, string expectedDigest)
    {
        CollisionTris.Data bake = CollisionTris.Load(VisibilityReplay.RequireBakePath(map));
        TriangleBvh bvh = TriangleBvh.Build(bake.Vertices, bake.TriangleCount);
        Console.WriteLine($"[build-identity] {map}: {bake.TriangleCount} triangles, {bvh.NodeCount} nodes, depth {bvh.Depth}, "
                          + $"stack {bvh.StackCapacity}, digest {bvh.StructuralDigest():X16}");
        await Assert.That($"{bvh.StructuralDigest():X16}").IsEqualTo(expectedDigest)
            .Because("the tree over this bake is pinned; a new digest is a tree change that needs its harness log");
    }

    /// <param name="soup">Which synthetic soup to build over.</param>
    /// <param name="expectedDigest">The structural digest pinned for the soup.</param>
    [Test]
    [Arguments(Soup.OneTriangle, "498D21D7836E5FF1")]
    [Arguments(Soup.ThreeSeparated, "675595A31042C778")]
    [Arguments(Soup.NineCoincident, "C71B0547691CE6CD")]
    [Arguments(Soup.SixtyFourCoincident, "E5D492851EC7F3B7")]
    [Arguments(Soup.NaNVertex, "665F305D9EC52E5C")]
    [Arguments(Soup.Random5000, "B4892CDEE9B0A25B")]
    [Arguments(Soup.Random200000, "700A8DD157EA4EA6")]
    [Arguments(Soup.DepthCapChain, "7E236F7C84028DB4")]
    public async Task SyntheticSoup_StructuralDigest_IsPinned(Soup soup, string expectedDigest)
    {
        (float[] vertices, int count) = Make(soup);
        TriangleBvh bvh = TriangleBvh.Build(vertices, count);
        Console.WriteLine($"[build-identity] {soup}: {count} triangles, {bvh.NodeCount} nodes, depth {bvh.Depth}, "
                          + $"stack {bvh.StackCapacity}, digest {bvh.StructuralDigest():X16}");
        await Assert.That($"{bvh.StructuralDigest():X16}").IsEqualTo(expectedDigest)
            .Because("the tree over this soup is pinned; a new digest is a tree change that needs its harness log");
    }

    /// <summary>Synthetic soups the identity pins cover.</summary>
    public enum Soup
    {
        /// <summary>A single triangle: the root is a leaf and the one wide node has one lane.</summary>
        OneTriangle,

        /// <summary>Three separated triangles: a root with five empty lanes.</summary>
        ThreeSeparated,

        /// <summary>Nine copies of one triangle: past the leaf size with no splittable centroid.</summary>
        NineCoincident,

        /// <summary>Sixty-four copies: several half-split levels.</summary>
        SixtyFourCoincident,

        /// <summary>A random soup with one NaN vertex, which reaches the all-NaN lane fallback.</summary>
        NaNVertex,

        /// <summary>The determinism test's soup.</summary>
        Random5000,

        /// <summary>A soup the size of a small bake.</summary>
        Random200000,

        /// <summary>
        ///     A thousand triangles whose group boxes all have the same area in float, so every
        ///     candidate split costs the same and the SAH keeps its first: the lowest bin. That peels
        ///     a sixteenth off the bottom at every level, a chain 64 binary levels deep with eleven
        ///     triangles still unsplit at the cap, where the depth cap's half-split takes over.
        /// </summary>
        DepthCapChain
    }

    internal static (float[] Vertices, int Count) Make(Soup soup)
    {
        switch (soup)
        {
            case Soup.OneTriangle:
                return (TriangleBvh8Tests.SeparatedTriangles(1), 1);
            case Soup.ThreeSeparated:
                return (TriangleBvh8Tests.SeparatedTriangles(3), 3);
            case Soup.NineCoincident:
                return (Coincident(9), 9);
            case Soup.SixtyFourCoincident:
                return (Coincident(64), 64);
            case Soup.NaNVertex:
            {
                float[] v = TriangleBvh8Tests.RandomSoup(new Random(20260918), 3000, 2000f);
                v[(1234 * 9) + 4] = float.NaN;
                return (v, 3000);
            }

            case Soup.Random5000:
                return (TriangleBvh8Tests.RandomSoup(new Random(20260916), 5000, 2000f), 5000);
            case Soup.Random200000:
                return (TriangleBvh8Tests.RandomSoup(new Random(20260919), 200_000, 8000f), 200_000);
            case Soup.DepthCapChain:
                return (Chain(1000), 1000);
            default:
                throw new ArgumentOutOfRangeException(nameof(soup), soup, "no such soup");
        }
    }

    // Triangle i spans y and z by 2^40 and sits at x = i, so its centroid is distinct on x alone
    // while every group's box area is 2^81 exactly: the x term of the area, at most 2^50, is below
    // the ulp of 2^80 and rounds away. Equal costs leave the SAH its first candidate, bin 0.
    private static float[] Chain(int count)
    {
        const float span = 1099511627776f; // 2^40
        float[] vertices = new float[count * 9];
        for (int i = 0; i < count; i++)
        {
            int b = i * 9;
            vertices[b] = i;
            vertices[b + 3] = i;
            vertices[b + 4] = span;
            vertices[b + 6] = i;
            vertices[b + 8] = span;
        }

        return vertices;
    }

    private static float[] Coincident(int copies)
    {
        float[] one = [100f, 100f, 50f, 140f, 100f, 50f, 100f, 140f, 50f];
        float[] vertices = new float[copies * 9];
        for (int i = 0; i < copies; i++)
        {
            one.CopyTo(vertices, i * 9);
        }

        return vertices;
    }
}
