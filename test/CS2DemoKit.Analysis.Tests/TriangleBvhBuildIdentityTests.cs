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
///         The digests were taken from the two-pass builder (binary SAH tree, then a separate
///         collapse into eight-wide nodes) before the one-pass builder replaced it, and the one-pass
///         builder reproduces every one of them. The pins were watched failing under a deliberate
///         mutation of the one-pass collapse (largest-box tie broken toward the later child instead
///         of the earlier) on every soup here that reaches a tie, and under a changed lane-array
///         initial capacity on none, which is the point: capacity is not structure.
///     </para>
///     <para>
///         The soups cover what the real bakes do not: a root that is itself a leaf, fewer children
///         than lanes, coincident centroids past the leaf size (the half-split path), a NaN
///         vertex (NaN boxes through the collapse's largest-box pick and the lane assignment),
///         and a chain that reaches the binary depth cap with more than a leaf's worth left, so
///         the cap's half-split fires; that pin was watched failing with the cap raised to 128.
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

    private static (float[] Vertices, int Count) Make(Soup soup)
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
