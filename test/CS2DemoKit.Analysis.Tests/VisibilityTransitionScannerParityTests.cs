#region

using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using CS2DemoKit.Analysis.Events;
using CS2DemoKit.Analysis.Visibility;
using CS2DemoKit.Parser;

#endregion

namespace CS2DemoKit.Analysis.Tests;

/// <summary>
///     Holds the live <see cref="VisibilityTransitionScanner" /> to the frozen
///     <see cref="ReferenceVisibility.ReferenceScanner" />: the same vantage and smoke stream into
///     both, and every spot, every per-viewer visible level and every on-target stamp has to agree
///     on every sample. The reference keeps the hash-set pair state and the dictionary of stamps the
///     scanner started with, so any re-expression of that state is measured against the original
///     rather than against itself.
///     <para>
///         Two corpora: a synthetic one that runs on a bare checkout and exercises the re-arm
///         paths deliberately (players leaving the set, crosshairs drifting on and off, ticks
///         repeating and going backward), and the committed sample demo over the real de_nuke bake.
///     </para>
/// </summary>
[Category("Unit")]
public class VisibilityTransitionScannerParityTests
{
    private const int SyntheticSlots = 8;

    [Test]
    public async Task SyntheticCorpus_LiveAndReferenceScannersAgreeOnEverySample()
    {
        Random rng = new(20260910);
        VisibilityEngine engine = RandomWalls(rng, 24);
        VisibilityTransitionScanner live = new(engine);
        ReferenceVisibility.ReferenceScanner reference = new(engine);

        // Persistent per-slot state random-walked between samples, so holds and drifts happen.
        Vector3[] feet = new Vector3[SyntheticSlots];
        float[] yaw = new float[SyntheticSlots];
        float[] pitch = new float[SyntheticSlots];
        for (int s = 0; s < SyntheticSlots; s++)
        {
            feet[s] = new Vector3(rng.Next(-1500, 1500), rng.Next(-1500, 1500), rng.Next(0, 3) * 64f);
            yaw[s] = rng.Next(0, 360);
            pitch[s] = rng.Next(-30, 30);
        }

        List<AimVantage> vantages = new(SyntheticSlots);
        List<Vector4> smokes = new(4);
        int spots = 0, samplesWithSmoke = 0, stamps = 0;
        int tick = 1000;
        for (int sample = 0; sample < 3000; sample++)
        {
            // Mostly forward by one, sometimes a repeat (inside the stride) and rarely a seek back.
            int roll = rng.Next(100);
            tick = roll < 3 ? tick - rng.Next(1, 20) : roll < 10 ? tick : tick + 1;

            vantages.Clear();
            for (int s = 0; s < SyntheticSlots; s++)
            {
                if (rng.Next(100) < 4)
                {
                    continue; // dead or dormant this sample: absent from the set
                }

                if (rng.Next(100) < 70)
                {
                    feet[s] += new Vector3(rng.Next(-8, 9), rng.Next(-8, 9), 0f);
                }

                float duck = rng.Next(100) < 15 ? (float)rng.NextDouble() : 0f;
                if (rng.Next(100) < 20)
                {
                    // Snap onto a random enemy's chest with a little jitter, so the on-target
                    // paths (arrival, hold, re-arm, a second enemy in a held crosshair) are all
                    // reached; a purely random yaw lands inside the tolerance almost never.
                    int enemy = (s + 1 + 2 * rng.Next(SyntheticSlots / 2)) % SyntheticSlots;
                    Vector3 chest = feet[enemy] + new Vector3(0f, 0f, 48f);
                    Vector3 to = chest - PlayerVantage.Eye(feet[s], duck);
                    yaw[s] = MathF.Atan2(to.Y, to.X) * 180f / MathF.PI + (float)(rng.NextDouble() * 4.0 - 2.0);
                    pitch[s] = -MathF.Atan2(to.Z, MathF.Sqrt(to.X * to.X + to.Y * to.Y)) * 180f / MathF.PI
                               + (float)(rng.NextDouble() * 2.0 - 1.0);
                }
                else if (rng.Next(100) < 30)
                {
                    yaw[s] += rng.Next(-15, 16);
                    pitch[s] = Math.Clamp(pitch[s] + rng.Next(-5, 6), -89f, 89f);
                }

                Vector3 eye = PlayerVantage.Eye(feet[s], duck);
                VisibilityAnalyzer.Vantage v = new(
                    s, s % 2 == 0 ? 2 : 3, feet[s], eye, PlayerVantage.Forward(pitch[s], yaw[s]), true, duck);
                vantages.Add(new AimVantage(v, 0f, pitch[s], yaw[s]));
            }

            smokes.Clear();
            if (rng.Next(100) < 20)
            {
                int count = rng.Next(1, 4);
                for (int i = 0; i < count; i++)
                {
                    smokes.Add(new Vector4(rng.Next(-1500, 1500), rng.Next(-1500, 1500), rng.Next(0, 3) * 64f,
                        SmokeVolumes.DefaultRadius));
                }

                samplesWithSmoke++;
            }

            ReadOnlySpan<Vector4> span = CollectionsMarshal.AsSpan(smokes);
            IReadOnlyList<EnemySpottedEvent> fromLive = live.Sample(tick, tick - 100, vantages, span);
            IReadOnlyList<EnemySpottedEvent> fromReference = reference.Sample(tick, tick - 100, vantages, span);

            await Assert.That(RenderSpots(fromLive)).IsEqualTo(RenderSpots(fromReference))
                .Because($"spots diverged on sample {sample} at tick {tick}");
            spots += fromLive.Count;

            for (int s = 0; s < SyntheticSlots; s++)
            {
                await Assert.That(live.IsAnyEnemyVisibleTo(s)).IsEqualTo(reference.IsAnyEnemyVisibleTo(s))
                    .Because($"visible level diverged for slot {s} on sample {sample} at tick {tick}");
                await Assert.That(live.OnTargetSince(s)).IsEqualTo(reference.OnTargetSince(s))
                    .Because($"on-target stamp diverged for slot {s} on sample {sample} at tick {tick}");
                if (live.OnTargetSince(s) >= 0)
                {
                    stamps++;
                }
            }
        }

        // The corpus has to have reached the interesting paths for the agreement to mean anything.
        await Assert.That(spots).IsGreaterThan(100).Because("a corpus with no rising edges pins nothing");
        await Assert.That(stamps).IsGreaterThan(100).Because("a corpus with no on-target holds pins no re-arm");
        await Assert.That(samplesWithSmoke).IsGreaterThan(100);
        Console.WriteLine($"[scanner-parity] synthetic: {spots} spots, {stamps} slot-samples on target, {samplesWithSmoke} smoked samples");
    }

    [Test]
    [Category("RealAsset")]
    public async Task SampleDemo_LiveAndReferenceScannersAgreeOnEveryFrame()
    {
        ParsedDemo demo = VisibilityReplay.RequireSampleDemo();
        await Assert.That(demo.Frames.Count).IsEqualTo(VisibilityReplay.SampleDemoFrameCount);
        VisibilityEngine engine = VisibilityReplay.RequireBake(VisibilityReplay.SampleDemoMap);

        VisibilityTransitionScanner live = new(engine);
        ReferenceVisibility.ReferenceScanner reference = new(engine);
        int spots = 0;
        List<string> divergences = [];

        VisibilityReplay.ForEachFrame(demo.Frames, (tick, vantages, smokes) =>
        {
            ReadOnlySpan<Vector4> span = CollectionsMarshal.AsSpan(smokes);
            IReadOnlyList<EnemySpottedEvent> liveSpots = live.Sample(tick, tick, vantages, span);
            spots += liveSpots.Count;
            string fromLive = RenderSpots(liveSpots);
            string fromReference = RenderSpots(reference.Sample(tick, tick, vantages, span));
            if (!string.Equals(fromLive, fromReference, StringComparison.Ordinal))
            {
                divergences.Add($"tick {tick}: live [{fromLive}] reference [{fromReference}]");
            }

            for (int i = 0; i < vantages.Count; i++)
            {
                int slot = vantages[i].Vantage.Slot;
                if (live.IsAnyEnemyVisibleTo(slot) != reference.IsAnyEnemyVisibleTo(slot))
                {
                    divergences.Add($"tick {tick}: visible level for slot {slot}");
                }

                if (live.OnTargetSince(slot) != reference.OnTargetSince(slot))
                {
                    divergences.Add(
                        $"tick {tick}: on-target stamp for slot {slot}: live {live.OnTargetSince(slot)} reference {reference.OnTargetSince(slot)}");
                }
            }
        });

        await Assert.That(divergences.Count).IsEqualTo(0)
            .Because(string.Join(Environment.NewLine, divergences.Take(10)));
        await Assert.That(spots).IsGreaterThan(0).Because("the sample has engagements, so it has rising edges");
        Console.WriteLine($"[scanner-parity] sample demo: {spots} spots, 0 divergences");
    }

    private static string RenderSpots(IReadOnlyList<EnemySpottedEvent> spots)
    {
        if (spots.Count == 0)
        {
            return string.Empty;
        }

        CultureInfo inv = CultureInfo.InvariantCulture;
        return string.Join('\n', spots.Select(s =>
            $"{s.FrameNumber}|{s.ServerTick}|{s.GameTick}|{s.ViewerSlot}>{s.TargetSlot}|"
            + $"{s.AngleToChestDeg.ToString("R", inv)}|{s.ViewerPitchDeg.ToString("R", inv)}|{s.ViewerYawDeg.ToString("R", inv)}"));
    }

    // A handful of large random triangles in the play box, so some sightlines are blocked and most are
    // not; enough structure for edges to appear and disappear as players walk.
    private static VisibilityEngine RandomWalls(Random rng, int count)
    {
        float[] v = new float[count * 9];
        for (int i = 0; i < count; i++)
        {
            float x = rng.Next(-1500, 1500), y = rng.Next(-1500, 1500);
            float dx = rng.Next(-600, 600), dy = rng.Next(-600, 600);
            v[i * 9 + 0] = x;
            v[i * 9 + 1] = y;
            v[i * 9 + 2] = -10f;
            v[i * 9 + 3] = x + dx;
            v[i * 9 + 4] = y + dy;
            v[i * 9 + 5] = -10f;
            v[i * 9 + 6] = x + dx * 0.5f;
            v[i * 9 + 7] = y + dy * 0.5f;
            v[i * 9 + 8] = 300f;
        }

        return VisibilityEngine.FromTriangles(v, count);
    }
}
