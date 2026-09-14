#region

using System.Numerics;
using System.Runtime.InteropServices;
using CS2DemoKit.Analysis.Visibility;

#endregion

namespace CS2DemoKit.Analysis.Tests;

/// <summary>
///     Holds the gated pair loop to the frozen <see cref="ReferenceVisibility.EvaluatePair" />:
///     over hundreds of thousands of synthetic (viewer, target, smokes) triples, the live
///     <c>EvaluatePair</c> tuple must equal the reference tuple and the live <c>CouldSee</c> must
///     equal the reference tuple's could-see. The reference casts every anchor the old loop cast;
///     the live loop is allowed to cast fewer and is not allowed to answer differently.
///     <para>
///         The corpus is shaped so every branch is populated: viewers aimed at their target and
///         away from it, standing and crouched, smokes sitting on the sightline and off it, a
///         viewer with no view angle at all. The observation counts at the end are asserted so a
///         corpus that drifted into one regime would fail rather than pass vacuously.
///     </para>
/// </summary>
[Category("Unit")]
public class VisibilityPairParityTests
{
    [Test]
    public async Task SyntheticTriples_EvaluatePairAndCouldSee_MatchTheReference()
    {
        Random rng = new(20260910);
        VisibilityEngine engine = RandomSoup(rng, 3000, 2000f);
        Vector3 lo = new(-2000f, -2000f, -200f), hi = new(2000f, 2000f, 400f);

        Outcome outcome = RunCorpus(rng, engine, lo, hi, 200_000);

        Console.WriteLine($"[pair-parity] synthetic: {outcome.Describe()}");
        await Assert.That(outcome.Mismatches.Count).IsEqualTo(0)
            .Because(string.Join(Environment.NewLine, outcome.Mismatches.Take(10)));
        // Measured on this seed: exposed 27,945, could-see 9,682, smoke-separated 3,393, out of
        // frustum 14,720. The floors are set well under those so a seed change does not flake, and
        // well above zero so a corpus that collapsed into one regime cannot pass.
        await Assert.That(outcome.Exposed).IsGreaterThan(10_000);
        await Assert.That(outcome.NotExposed).IsGreaterThan(20_000);
        await Assert.That(outcome.CouldSee).IsGreaterThan(5_000);
        await Assert.That(outcome.ExposedNotSeenBySmoke).IsGreaterThan(1_000)
            .Because("smoke must be what separates exposed from could-see on a real share of pairs");
        await Assert.That(outcome.ExposedOutOfFrustum).IsGreaterThan(2_000)
            .Because("the frustum gate is only tested by pairs exposed but not on screen");
    }

    /// <summary>The same parity over the real de_nuke bake, with vantages sampled inside its bounds.</summary>
    [Test]
    [Category("RealAsset")]
    public async Task NukeBake_EvaluatePairAndCouldSee_MatchTheReference()
    {
        Random rng = new(20260911);
        VisibilityEngine engine = VisibilityReplay.RequireBake(VisibilityReplay.SampleDemoMap);

        Outcome outcome = RunCorpus(rng, engine, engine.Min, engine.Max, 50_000);

        Console.WriteLine($"[pair-parity] de_nuke: {outcome.Describe()}");
        await Assert.That(outcome.Mismatches.Count).IsEqualTo(0)
            .Because(string.Join(Environment.NewLine, outcome.Mismatches.Take(10)));
        await Assert.That(outcome.Exposed).IsGreaterThan(1_000);
        await Assert.That(outcome.NotExposed).IsGreaterThan(1_000);
        await Assert.That(outcome.CouldSee).IsGreaterThan(1_000);
    }

    private sealed class Outcome
    {
        public List<string> Mismatches { get; } = [];
        public int Exposed { get; set; }
        public int NotExposed { get; set; }
        public int CouldSee { get; set; }
        public int ExposedNotSeenBySmoke { get; set; }
        public int ExposedOutOfFrustum { get; set; }

        public string Describe() =>
            $"exposed={Exposed} notExposed={NotExposed} couldSee={CouldSee} "
            + $"exposedNotSeenBySmoke={ExposedNotSeenBySmoke} exposedOutOfFrustum={ExposedOutOfFrustum} "
            + $"mismatches={Mismatches.Count}";
    }

    private static Outcome RunCorpus(Random rng, VisibilityEngine engine, Vector3 lo, Vector3 hi, int triples)
    {
        Outcome outcome = new();
        List<Vector4> smokes = new(4);
        Vector3[] anchors = new Vector3[PlayerVantage.MaxAnchors];
        const float yaw = 53f, pitch = 37f;

        for (int i = 0; i < triples; i++)
        {
            VisibilityAnalyzer.Vantage target = RandomVantage(rng, lo, hi, 3, null);
            VisibilityAnalyzer.Vantage viewer = RandomVantage(rng, lo, hi, 2, target.Feet);

            smokes.Clear();
            int roll = rng.Next(100);
            if (roll < 25)
            {
                // On the sightline, somewhere between eye and target, so it actually blocks.
                float t = (float)rng.NextDouble();
                Vector3 on = Vector3.Lerp(viewer.Eye, target.Feet + new Vector3(0f, 0f, 48f), t);
                smokes.Add(new Vector4(on.X, on.Y, on.Z, SmokeVolumes.DefaultRadius));
            }
            else if (roll < 40)
            {
                int count = rng.Next(1, 4);
                for (int k = 0; k < count; k++)
                {
                    Vector3 p = RandomPoint(rng, lo, hi);
                    smokes.Add(new Vector4(p.X, p.Y, p.Z, SmokeVolumes.DefaultRadius));
                }
            }

            ReadOnlySpan<Vector4> span = CollectionsMarshal.AsSpan(smokes);
            (bool refExposed, bool refCouldSee) = ReferenceVisibility.EvaluatePair(engine, viewer, target, yaw, pitch, span);
            (bool liveExposed, bool liveCouldSee) = VisibilityAnalyzer.EvaluatePair(engine, viewer, target, yaw, pitch, span);
            bool couldSeeOnly = VisibilityAnalyzer.CouldSee(engine, viewer, target, yaw, pitch, span);

            if (liveExposed != refExposed || liveCouldSee != refCouldSee || couldSeeOnly != refCouldSee)
            {
                outcome.Mismatches.Add(
                    $"triple {i}: reference=({refExposed},{refCouldSee}) live=({liveExposed},{liveCouldSee}) "
                    + $"couldSee={couldSeeOnly} viewer.Eye={viewer.Eye} fwd={viewer.Forward} target.Feet={target.Feet} smokes={smokes.Count}");
                continue;
            }

            if (refExposed)
            {
                outcome.Exposed++;
                if (!refCouldSee)
                {
                    // Separate the two reasons an exposed pair is not seen, using the same
                    // primitives the loop uses, so both gates are known to have been exercised.
                    int n = PlayerVantage.BuildAnchors(target.Feet, target.Duck, viewer.Eye, anchors);
                    ViewFrustum frustum = new(viewer.Eye, viewer.Forward, yaw, pitch);
                    bool anyInFov = false;
                    for (int a = 0; a < n; a++)
                    {
                        anyInFov |= viewer.HasForward && frustum.Contains(anchors[a]);
                    }

                    if (anyInFov && smokes.Count > 0)
                    {
                        outcome.ExposedNotSeenBySmoke++;
                    }
                    else if (!anyInFov)
                    {
                        outcome.ExposedOutOfFrustum++;
                    }
                }
            }
            else
            {
                outcome.NotExposed++;
            }

            if (refCouldSee)
            {
                outcome.CouldSee++;
            }
        }

        return outcome;
    }

    // Half the viewers aim at the target (with jitter that leaves some anchors on the frustum
    // edge), the rest look anywhere; a few have no view angle at all.
    private static VisibilityAnalyzer.Vantage RandomVantage(Random rng, Vector3 lo, Vector3 hi, int team, Vector3? aimAt)
    {
        Vector3 feet = RandomPoint(rng, lo, hi);
        float duck = rng.Next(100) < 30 ? (float)rng.NextDouble() : 0f;
        Vector3 eye = PlayerVantage.Eye(feet, duck);
        bool hasForward = rng.Next(100) >= 3;

        float yawDeg, pitchDeg;
        if (aimAt is { } at && rng.Next(100) < 50)
        {
            Vector3 to = at + new Vector3(0f, 0f, 48f) - eye;
            yawDeg = MathF.Atan2(to.Y, to.X) * 180f / MathF.PI + (float)(rng.NextDouble() * 120.0 - 60.0);
            pitchDeg = -MathF.Atan2(to.Z, MathF.Sqrt(to.X * to.X + to.Y * to.Y)) * 180f / MathF.PI
                       + (float)(rng.NextDouble() * 80.0 - 40.0);
        }
        else
        {
            yawDeg = (float)(rng.NextDouble() * 360.0 - 180.0);
            pitchDeg = (float)(rng.NextDouble() * 178.0 - 89.0);
        }

        return new VisibilityAnalyzer.Vantage(
            rng.Next(0, 64), team, feet, eye, hasForward ? PlayerVantage.Forward(pitchDeg, yawDeg) : default, hasForward, duck);
    }

    private static Vector3 RandomPoint(Random rng, Vector3 lo, Vector3 hi) => new(
        lo.X + (hi.X - lo.X) * (float)rng.NextDouble(),
        lo.Y + (hi.Y - lo.Y) * (float)rng.NextDouble(),
        lo.Z + (hi.Z - lo.Z) * (float)rng.NextDouble());

    // Random triangles up to 400 units across, so a 4000-unit box is partly occluded: sightlines
    // of a few hundred units are usually clear and long ones usually blocked.
    private static VisibilityEngine RandomSoup(Random rng, int count, float half)
    {
        float[] v = new float[count * 9];
        for (int i = 0; i < count; i++)
        {
            Vector3 c = new(
                (float)(rng.NextDouble() * 2.0 - 1.0) * half,
                (float)(rng.NextDouble() * 2.0 - 1.0) * half,
                (float)(rng.NextDouble() * 400.0 - 100.0));
            for (int k = 0; k < 3; k++)
            {
                v[i * 9 + k * 3 + 0] = c.X + (float)(rng.NextDouble() * 400.0 - 200.0);
                v[i * 9 + k * 3 + 1] = c.Y + (float)(rng.NextDouble() * 400.0 - 200.0);
                v[i * 9 + k * 3 + 2] = c.Z + (float)(rng.NextDouble() * 200.0 - 100.0);
            }
        }

        return VisibilityEngine.FromTriangles(v, count);
    }
}
