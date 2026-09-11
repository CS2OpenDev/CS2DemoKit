#region

using System.Numerics;
using System.Runtime.Intrinsics;
using CS2DemoKit.Analysis.Visibility;

#endregion

namespace CS2DemoKit.Analysis.Tests;

/// <summary>
///     The tier reference corpus: a million random rays and the six-thousand-ray same-floor grid
///     over a real bake, evaluated on the scalar tier for the occlusion verdict, the hint written
///     back, the nearest-hit distance and the hinted re-run of every ray, and every other available
///     tier held to exact equality on all of them. This is what makes the general-availability claim true: a machine without a
///     given vector width runs the scalar tier and gets the same answers.
///     <para>
///         The comparison is bit identity, not tolerance: a verdict, a hint index or a distance
///         that differs in one bit is a mismatch. The same-floor grid is what gives the check its
///         teeth. Its rays have a direction component of exactly zero and origins on brush planes,
///         so the slab product for a face on the origin's plane is NaN, and a tier that handles
///         NaN any other way than the scalar compare-select (a library minimum, say, which
///         propagates it) flips those rays. It was watched failing with the Vector256 tier's
///         selects replaced by <c>Vector256.Max</c> and <c>Vector256.Min</c> and with the
///         Vector128 tier's two halves swapped: the first moved 48 and 311 same-floor rays on the
///         two maps (a different blocking triangle, and so a different hint, once the leaf on the
///         origin's plane is refused) and failed the negative-zero ray outright; the second sent
///         rays into the wrong children and flipped most verdicts to clear.
///     </para>
///     <para>
///         The plain entry points run <see cref="TriangleBvh.DefaultTier" />, so they are checked
///         against that tier rather than against scalar directly; with the tiers identical, both
///         checks say the same thing. The digest of the scalar answers per map is pinned so the
///         tiers can also be compared against the number in the previous phase's report; it covers
///         the verdict, the hint written back and the nearest distance, so it changes whenever the
///         tree changes, including a change to which of several blocking triangles is found first.
///         A new value is a re-pin and needs the differential harness log that adjudicates every
///         ray it moved.
///     </para>
///     <para>
///         The corpus skips segments shorter than the endpoint exclusion, as the engine does, and
///         holds no NaN or infinite input; it is evidence for real inputs. The degenerate classes
///         (NaN, negative zero, an origin on a face, empty lanes) are run through every tier by
///         <see cref="DegenerateRays_EveryTierMatchesScalar" /> with constructed rays.
///     </para>
///     <para>
///         Every ray is then run a second time with a hint in: the triangle the scalar traversal
///         found, or triangle 0 on a clear ray, so the hinted leaf test (each tier's own
///         <c>Lane</c>, with its own lane-bit extraction) runs on every tier for every real ray and
///         is held to the scalar verdict, hint and short-circuit flag. Two things are pinned there.
///         Every occluded ray must short-circuit on its own triangle: the traversal reached that
///         leaf through the same window the hinted test uses, so a traversal true is a hinted true
///         (the converse of the guarantee <c>TriangleBvh.AnyHit</c> documents), and a tier whose
///         leaf test reads the wrong lane bit fails this on most of the corpus. And every clear ray
///         hinted with triangle 0 must fall through to the traversal and stay clear. The pinned
///         digest covers the unhinted pass only, so it is unchanged by this pass.
///     </para>
///     <para>
///         Executed to date on x64 only, where the Vector128 tier runs as VEX-encoded SSE. The
///         Vector128 tier is also the one a NEON machine runs, and it has not been executed there;
///         the code uses only the cross-platform <c>Vector128</c> API with ordered comparisons and
///         no platform intrinsics, and this test adapts to whatever the machine accelerates, so a
///         run on ARM64 is the proof for that class of machine, not this log.
///     </para>
/// </summary>
[Category("RealAsset")]
public class TraversalTierTests
{
    private const float Eps = 0.1f;
    private const float NearEps = 1e-3f;

    /// <param name="map">Map whose <c>collision.tris</c> to load.</param>
    /// <param name="expectedDigest">The scalar digest pinned for the map; see the class doc before changing it.</param>
    [Test]
    [Arguments("de_nuke", "2460FD85B5737121")]
    [Arguments("de_dust2", "6F6C4395811AD5C4")]
    [Arguments("de_ancient", "487FBDC97754898D")]
    public async Task ReferenceCorpus_EveryTierMatchesScalar_VerdictHintAndDistance(string map, string expectedDigest)
    {
        CollisionTris.Data bake = CollisionTris.Load(VisibilityReplay.RequireBakePath(map));
        TriangleBvh bvh = TriangleBvh.Build(bake.Vertices, bake.TriangleCount);

        List<(Vector3 A, Vector3 B)> corpus = RayCorpora.Random(bvh.Min, bvh.Max, 1_000_000, 20260917);
        corpus.AddRange(RayCorpora.SameFloor(bvh.Min, bvh.Max, 0f, 20260910));
        int n = corpus.Count;

        Answers scalar = Evaluate(bvh, corpus, TraversalTier.Scalar);
        Hinted(bvh, corpus, TraversalTier.Scalar, scalar.Hint, scalar);
        Console.WriteLine($"[tier-corpus] {map}: {n} rays, {scalar.Occluded} occluded, {scalar.NearestHits} nearest hits, "
                          + $"{scalar.ShortCircuits} hinted short circuits, scalar digest {scalar.Digest:X16}");

        TraversalTier[] tiers = TriangleBvh.AvailableTiers.ToArray();
        Console.WriteLine($"[tier-corpus] {map}: tiers on this machine: {string.Join(", ", tiers)}; default {TriangleBvh.DefaultTier}");

        // The plain entry points against the tier they are documented to run.
        Answers plain = Evaluate(bvh, corpus, null);
        Hinted(bvh, corpus, null, scalar.Hint, plain);
        Answers byDefault = scalar;
        if (TriangleBvh.DefaultTier != TraversalTier.Scalar)
        {
            byDefault = Evaluate(bvh, corpus, TriangleBvh.DefaultTier);
            Hinted(bvh, corpus, TriangleBvh.DefaultTier, scalar.Hint, byDefault);
        }

        (int entryPointDisagreements, string? firstEntryPoint) = Compare(corpus, byDefault, plain, "plain");

        List<(TraversalTier Tier, int Mismatches, string? First)> compared = [];
        foreach (TraversalTier tier in tiers)
        {
            if (tier == TraversalTier.Scalar)
            {
                continue;
            }

            Answers answers = byDefault;
            if (tier != TriangleBvh.DefaultTier)
            {
                answers = Evaluate(bvh, corpus, tier);
                Hinted(bvh, corpus, tier, scalar.Hint, answers);
            }

            (int mismatches, string? first) = Compare(corpus, scalar, answers, tier.ToString());
            compared.Add((tier, mismatches, first));
            Console.WriteLine($"[tier-corpus] {map}: {tier} vs scalar: {mismatches} mismatches"
                              + (first is null ? string.Empty : $"; first: {first}"));
        }

        await Assert.That(tiers[0]).IsEqualTo(TraversalTier.Scalar);
        await Assert.That(tiers.Contains(TriangleBvh.DefaultTier)).IsTrue().Because("the default tier is one this machine accelerates");
        await Assert.That(tiers.Contains(TraversalTier.Vector256)).IsEqualTo(Vector256.IsHardwareAccelerated)
            .Because("a machine with 256-bit registers must compare that tier; a comparison that covers nothing proves nothing");
        await Assert.That(tiers.Contains(TraversalTier.Vector128)).IsEqualTo(Vector128.IsHardwareAccelerated);
        await Assert.That($"{scalar.Digest:X16}").IsEqualTo(expectedDigest)
            .Because("the scalar answers over the reference corpus are pinned; a new digest is a tree change that needs its harness log");
        await Assert.That(scalar.Occluded).IsGreaterThan(n / 2).Because("random segments across a map are mostly blocked");
        await Assert.That(n - scalar.Occluded).IsGreaterThan(1000).Because("and some are clear");
        await Assert.That(scalar.ShortCircuits).IsEqualTo(scalar.Occluded)
            .Because("every occluded ray hinted with its own triangle must short-circuit: the traversal reached that leaf through the same window");
        await Assert.That(scalar.HintedOccluded).IsEqualTo(scalar.Occluded)
            .Because("a clear ray hinted with triangle 0 must fall through to the traversal and stay clear");
        await Assert.That(entryPointDisagreements).IsEqualTo(0)
            .Because($"the plain entry points run the default tier; first: {firstEntryPoint}");
        foreach ((TraversalTier tier, int mismatches, string? first) in compared)
        {
            await Assert.That(mismatches).IsEqualTo(0)
                .Because($"tier {tier} must match the scalar reference on every verdict, hint and distance; first: {first}");
        }
    }

    /// <summary>
    ///     The degenerate classes the real-bake corpus does not reach, through every tier: rays
    ///     through the origin along the 26 lattice directions on a mesh with empty lanes (an
    ///     infinite inverse component against an inverted box), a direction of negative zero with
    ///     the origin on the minimum face, an origin exactly on a face of a zero-direction axis, NaN
    ///     origins and directions, and a random soup for the general case. Each tier must answer
    ///     as scalar does on the verdict, the short-circuit flag, the hint and the nearest distance.
    /// </summary>
    [Test]
    [Category("Unit")]
    public async Task DegenerateRays_EveryTierMatchesScalar()
    {
        List<(float[] Vertices, int Count, List<(Vector3 A, Vector3 B)> Rays)> cases = [];

        foreach (int count in new[] { 1, 3, 7, 8, 40 })
        {
            float[] vertices = SeparatedTriangles(count);
            List<(Vector3, Vector3)> rays = [];
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
                        rays.Add((Vector3.Zero, d * 500f));
                    }
                }
            }

            for (int i = 0; i < count; i++)
            {
                float x = vertices[i * 9];
                Vector3 c = new(x + 3f, 43f, 33f);
                rays.Add((c - new Vector3(50f, 0f, 0f), c + new Vector3(50f, 0f, 0f)));
                // Origin on the leaf box's minimum y face (y = 40) with dir.Y = 0: the NaN product class.
                rays.Add((new Vector3(x - 100f, 40f, 33f), new Vector3(x + 100f, 40f, 33f)));
                rays.Add((new Vector3(x, 40f, 30f), new Vector3(x + 200f, 40f, 30f)));
                rays.Add((c + new Vector3(0f, 0f, 200f), c - new Vector3(0f, 0f, 200f)));
            }

            cases.Add((vertices, count, rays));
        }

        {
            // The negative-zero construction from TriangleBvh8Tests, both signs.
            float[] vertices = [0f, 0f, 0f, 0f, 0f, 10f, 10f, 10f, 0f];
            cases.Add((vertices, 1, [(new Vector3(0f, -5f, 5f), new Vector3(-0f, 5f, 5f)), (new Vector3(0f, -5f, 5f), new Vector3(0f, 5f, 5f))]));
        }

        {
            Random rng = new(20260923);
            float[] vertices = RandomSoup(rng, 3000, 2000f);
            List<(Vector3, Vector3)> rays = RayCorpora.Random(new Vector3(-2200f), new Vector3(2200f), 20_000, 20260924);
            rays.AddRange(RayCorpora.SameFloor(new Vector3(-2200f), new Vector3(2200f), 0f, 20260925));
            cases.Add((vertices, 3000, rays));
        }

        int compared = 0, occluded = 0, shortCircuits = 0;
        foreach ((float[] vertices, int count, List<(Vector3 A, Vector3 B)> rays) in cases)
        {
            TriangleBvh bvh = TriangleBvh.Build(vertices, count);
            foreach ((Vector3 a, Vector3 b) in rays)
            {
                Vector3 delta = b - a;
                float len = delta.Length();
                Vector3 dir = delta / len;
                int found = -1;
                for (int pass = 0; pass < 2; pass++)
                {
                    // Pass 0 unhinted; pass 1 hints the triangle scalar found (or triangle 0 on a
                    // clear ray), so the hinted leaf test runs on every tier for every ray.
                    int hintIn = pass == 0 ? -1 : Math.Max(found, 0);
                    int scalarHint = hintIn;
                    bool scalarVerdict = bvh.AnyHit(a, dir, len, Eps, ref scalarHint, out bool scalarShort, TraversalTier.Scalar);
                    found = scalarVerdict ? scalarHint : found;
                    bool scalarNear = bvh.NearestHit(a, dir, len, NearEps, out float scalarT, TraversalTier.Scalar);
                    occluded += scalarVerdict ? 1 : 0;
                    shortCircuits += scalarShort ? 1 : 0;
                    foreach (TraversalTier tier in TriangleBvh.AvailableTiers.ToArray())
                    {
                        int hint = hintIn;
                        bool verdict = bvh.AnyHit(a, dir, len, Eps, ref hint, out bool shortCircuited, tier);
                        bool near = bvh.NearestHit(a, dir, len, NearEps, out float t, tier);
                        compared++;
                        string ray = $"{tier} on {count} triangles, ray ({a.X:R},{a.Y:R},{a.Z:R}) -> ({b.X:R},{b.Y:R},{b.Z:R}) pass {pass}";
                        await Assert.That(verdict).IsEqualTo(scalarVerdict).Because($"{ray}: verdict");
                        await Assert.That(shortCircuited).IsEqualTo(scalarShort).Because($"{ray}: short circuit");
                        await Assert.That(hint).IsEqualTo(scalarHint).Because($"{ray}: hint");
                        await Assert.That(near).IsEqualTo(scalarNear).Because($"{ray}: nearest verdict");
                        await Assert.That(BitConverter.SingleToInt32Bits(t)).IsEqualTo(BitConverter.SingleToInt32Bits(scalarT))
                            .Because($"{ray}: nearest distance {t:R} vs {scalarT:R}");
                    }
                }
            }

            // NaN input on every tier: a miss that leaves the hint alone.
            foreach (TraversalTier tier in TriangleBvh.AvailableTiers.ToArray())
            {
                int hint = 0;
                await Assert.That(bvh.AnyHit(new Vector3(float.NaN, 40f, 33f), Vector3.UnitX, 100f, Eps, ref hint, out _, tier)).IsFalse();
                await Assert.That(bvh.AnyHit(new Vector3(0f, 40f, 33f), new Vector3(float.NaN, 0f, 0f), 100f, Eps, ref hint, out _, tier)).IsFalse();
                await Assert.That(hint).IsEqualTo(0);
                await Assert.That(bvh.NearestHit(new Vector3(0f, 40f, 33f), Vector3.Normalize(Vector3.Zero), 100f, NearEps, out float t, tier)).IsFalse();
                await Assert.That(t).IsEqualTo(float.MaxValue);
            }
        }

        Console.WriteLine($"[tier-degenerate] {compared} tier answers compared, {occluded} scalar occlusions, {shortCircuits} hinted short circuits");
        await Assert.That(occluded).IsGreaterThan(0).Because("the constructed rays must hit something");
        await Assert.That(shortCircuits).IsGreaterThan(0).Because("the hinted pass must exercise the leaf test");
    }

    private static Answers Evaluate(TriangleBvh bvh, List<(Vector3 A, Vector3 B)> corpus, TraversalTier? tier)
    {
        int n = corpus.Count;
        Answers answers = new(n);
        ulong digest = 14695981039346656037UL;
        for (int i = 0; i < n; i++)
        {
            (Vector3 a, Vector3 b) = corpus[i];
            Vector3 delta = b - a;
            float len = delta.Length();
            if (len <= 2f * Eps)
            {
                continue;
            }

            Vector3 dir = delta / len;
            int h = -1;
            bool v;
            bool nh;
            float t;
            if (tier is { } named)
            {
                v = bvh.AnyHit(a, dir, len, Eps, ref h, out _, named);
                nh = bvh.NearestHit(a, dir, len, NearEps, out t, named);
            }
            else
            {
                v = bvh.AnyHit(a, dir, len, Eps, ref h);
                nh = bvh.NearestHit(a, dir, len, NearEps, out t);
                if (bvh.AnyHit(a, dir, len, Eps) != v)
                {
                    h = int.MinValue; // the unhinted plain overload disagreed; surfaces as a hint mismatch
                }
            }

            answers.Verdict[i] = v;
            answers.Hint[i] = h;
            answers.NearHit[i] = nh;
            answers.DistanceBits[i] = BitConverter.SingleToInt32Bits(t);
            answers.Occluded += v ? 1 : 0;
            answers.NearestHits += nh ? 1 : 0;
            digest = Fnv(digest, v ? 1UL : 0UL);
            digest = Fnv(digest, (ulong)(uint)h);
            digest = Fnv(digest, (ulong)(uint)answers.DistanceBits[i]);
        }

        answers.Digest = digest;
        return answers;
    }

    // The hinted pass: every ray again with the scalar tier's found triangle as the hint in (or
    // triangle 0 on a ray it found clear), through the plain entry point when tier is null.
    private static void Hinted(TriangleBvh bvh, List<(Vector3 A, Vector3 B)> corpus, TraversalTier? tier, int[] hintsIn, Answers into)
    {
        int n = corpus.Count;
        for (int i = 0; i < n; i++)
        {
            (Vector3 a, Vector3 b) = corpus[i];
            Vector3 delta = b - a;
            float len = delta.Length();
            if (len <= 2f * Eps)
            {
                continue;
            }

            Vector3 dir = delta / len;
            int h = Math.Max(hintsIn[i], 0);
            bool v;
            bool shortCircuited;
            if (tier is { } named)
            {
                v = bvh.AnyHit(a, dir, len, Eps, ref h, out shortCircuited, named);
            }
            else
            {
                v = bvh.AnyHit(a, dir, len, Eps, ref h, out shortCircuited);
            }

            into.HintedVerdict[i] = v;
            into.HintedHint[i] = h;
            into.HintedShort[i] = shortCircuited;
            into.HintedOccluded += v ? 1 : 0;
            into.ShortCircuits += shortCircuited ? 1 : 0;
        }
    }

    private static (int Mismatches, string? First) Compare(List<(Vector3 A, Vector3 B)> corpus, Answers reference, Answers other, string label)
    {
        int mismatches = 0;
        string? first = null;
        for (int i = 0; i < corpus.Count; i++)
        {
            if (reference.Verdict[i] == other.Verdict[i] && reference.Hint[i] == other.Hint[i]
                && reference.NearHit[i] == other.NearHit[i] && reference.DistanceBits[i] == other.DistanceBits[i]
                && reference.HintedVerdict[i] == other.HintedVerdict[i] && reference.HintedHint[i] == other.HintedHint[i]
                && reference.HintedShort[i] == other.HintedShort[i])
            {
                continue;
            }

            mismatches++;
            if (first is null)
            {
                (Vector3 a, Vector3 b) = corpus[i];
                first = $"ray {i}: ({a.X:R},{a.Y:R},{a.Z:R}) -> ({b.X:R},{b.Y:R},{b.Z:R}) "
                        + $"reference=({reference.Verdict[i]},{reference.Hint[i]},{reference.NearHit[i]},{BitConverter.Int32BitsToSingle(reference.DistanceBits[i]):R}; "
                        + $"hinted {reference.HintedVerdict[i]},{reference.HintedHint[i]},{reference.HintedShort[i]}) "
                        + $"{label}=({other.Verdict[i]},{other.Hint[i]},{other.NearHit[i]},{BitConverter.Int32BitsToSingle(other.DistanceBits[i]):R}; "
                        + $"hinted {other.HintedVerdict[i]},{other.HintedHint[i]},{other.HintedShort[i]})";
            }
        }

        return (mismatches, first);
    }

    private static ulong Fnv(ulong h, ulong value)
    {
        for (int i = 0; i < 8; i++)
        {
            h ^= (value >> (i * 8)) & 0xFF;
            h *= 1099511628211UL;
        }

        return h;
    }

    private static float[] SeparatedTriangles(int count)
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

    private static float[] RandomSoup(Random rng, int count, float half)
    {
        float[] v = new float[count * 9];
        for (int i = 0; i < count; i++)
        {
            Vector3 a = new(
                (float)((rng.NextDouble() * 2.0 * half) - half),
                (float)((rng.NextDouble() * 2.0 * half) - half),
                (float)((rng.NextDouble() * 2.0 * half) - half));
            Vector3 b = a + new Vector3((float)(rng.NextDouble() * 80.0), (float)(rng.NextDouble() * 80.0), (float)(rng.NextDouble() * 80.0));
            Vector3 c = a + new Vector3((float)(rng.NextDouble() * 80.0), (float)(rng.NextDouble() * 80.0), (float)(rng.NextDouble() * 80.0));
            float[] tri = [a.X, a.Y, a.Z, b.X, b.Y, b.Z, c.X, c.Y, c.Z];
            tri.CopyTo(v, i * 9);
        }

        return v;
    }

    private sealed class Answers(int n)
    {
        public readonly bool[] Verdict = new bool[n];
        public readonly int[] Hint = new int[n];
        public readonly bool[] NearHit = new bool[n];
        public readonly int[] DistanceBits = new int[n];
        public readonly bool[] HintedVerdict = new bool[n];
        public readonly int[] HintedHint = new int[n];
        public readonly bool[] HintedShort = new bool[n];
        public int Occluded;
        public int NearestHits;
        public int HintedOccluded;
        public int ShortCircuits;
        public ulong Digest;
    }
}
