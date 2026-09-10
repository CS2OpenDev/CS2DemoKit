#region

using System.Numerics;
using CS2DemoKit.Analysis.Visibility;
using CS2DemoKit.Parser;
using CS2DemoKit.Parser.EntityTracking;
using CS2DemoKit.TestSupport;
using TUnit.Core.Exceptions;

#endregion

namespace CS2DemoKit.Analysis.Tests;

/// <summary>
///     Drives the visibility primitives over a real demo the way the analysis pipeline does, without
///     the pipeline: one sequential entity replay, one vantage per live pawn per frame, the active
///     smokes alongside. Shared by the trace golden and the scanner parity test so both judge the
///     same (vantage, smoke) stream.
///     <para>
///         The sample demo is resolved by exact filename and the bake by
///         <see cref="CollisionAssetLocator.EnvVar" />; either being absent is a loud skip naming
///         what was looked for, never a silent pass.
///     </para>
/// </summary>
internal static class VisibilityReplay
{
    /// <summary>Frame count of the committed four-round de_nuke sample, pinned so a swapped file cannot masquerade.</summary>
    public const int SampleDemoFrameCount = 19238;

    /// <summary>The map the sample demo was played on.</summary>
    public const string SampleDemoMap = "de_nuke";

    /// <summary>Callback per replayed frame with the frame's server tick, live vantages and active smokes.</summary>
    /// <param name="tick">The frame's server tick.</param>
    /// <param name="vantages">One entry per live pawn with both a position and a view angle.</param>
    /// <param name="smokes">Active smoke spheres this frame.</param>
    public delegate void FrameCallback(int tick, IReadOnlyList<AimVantage> vantages, List<Vector4> smokes);

    /// <summary>The committed sample demo, parsed once per process.</summary>
    public static ParsedDemo RequireSampleDemo() =>
        DemoTestHelper.GetOrParse(DemoTestHelper.RequireDemo(DemoTestHelper.SampleDemoFileName));

    /// <summary>Loads <c>&lt;map&gt;/collision.tris</c> from the collision directory, or skips naming the path.</summary>
    /// <param name="map">Map whose bake to load.</param>
    public static VisibilityEngine RequireBake(string map) => VisibilityEngine.Load(RequireBakePath(map));

    /// <summary>Resolves <c>&lt;map&gt;/collision.tris</c> under the collision directory, or skips naming the path.</summary>
    /// <param name="map">Map whose bake to resolve.</param>
    public static string RequireBakePath(string map)
    {
        string? dir = Environment.GetEnvironmentVariable(CollisionAssetLocator.EnvVar);
        if (string.IsNullOrWhiteSpace(dir))
        {
            throw new SkipTestException(
                $"Set {CollisionAssetLocator.EnvVar} to a directory holding <map>/collision.tris "
                + "(the app checkout's assets/ directory is one) to run against real geometry.");
        }

        string path = Path.Combine(dir, map, "collision.tris");
        if (!File.Exists(path))
        {
            throw new SkipTestException($"No bake for {map}: looked for {path}");
        }

        return path;
    }

    /// <summary>
    ///     Replays every frame in order and hands each one's live vantages and smokes to
    ///     <paramref name="onFrame" />. Vantage comes from <see cref="VisibilityAnalyzer.TryVantage" />
    ///     with the pawn's networked eye angles carried alongside, which is the shape the digest-fed
    ///     <see cref="AimVantageScanner" /> produces in the pipeline.
    /// </summary>
    /// <param name="frames">The demo's frames.</param>
    /// <param name="onFrame">Invoked once per frame, including frames the scanner's stride gate will reject.</param>
    public static void ForEachFrame(IReadOnlyList<DemoFrame> frames, FrameCallback onFrame)
    {
        EntityTracker tracker = new();
        List<AimVantage> vantages = new(12);
        List<Vector4> smokes = new(4);

        for (int i = 0; i < frames.Count; i++)
        {
            if (i == 0)
            {
                tracker.ReplayToIndex(0, frames);
            }
            else
            {
                tracker.AdvanceOneFrame(frames[i]);
            }

            vantages.Clear();
            PawnLookup.ForEachLivePawn(tracker, (slot, pawn) =>
            {
                if (VisibilityAnalyzer.TryVantage(slot, pawn, PositionUtil.CellToWorld) is not { HasForward: true } v)
                {
                    return;
                }

                Vector3 angles = pawn.TryGet<Vector3>("m_angEyeAngles") ?? default;
                vantages.Add(new AimVantage(v, 0f, angles.X, angles.Y));
            });

            VisibilityAnalyzer.CollectActiveSmokes(tracker, smokes);
            onFrame(frames[i].ServerTick, vantages, smokes);
        }
    }
}
