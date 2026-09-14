#region

using System.Numerics;
using CS2DemoKit.Analysis.Events;
using CS2DemoKit.Analysis.Visibility;

#endregion

namespace CS2DemoKit.Analysis.Tests;

/// <summary>
///     Frozen copies of the visibility pair loop and the transition scanner as they stood before the
///     ray-gating step, kept so the live code can be measured against them. Never edited: like
///     <see cref="LegacyTriangleBvh" />, these exist to stay exactly as they were, so that a parity
///     test comparing the live loop against them cannot degrade into the new loop testing itself.
///     <para>
///         They take no dependency on <c>VisibilityAnalyzer</c> beyond the pure primitives the
///         original code was built from: <see cref="VisibilityAnalyzer.Vantage" />,
///         <see cref="VisibilityAnalyzer.AreEnemies" />, <see cref="ViewFrustum" />,
///         <see cref="PlayerVantage.BuildAnchors" /> and <see cref="SmokeVolumes.SegmentBlocked" />.
///         The counter instrumentation is omitted; it never fed back into a result.
///     </para>
/// </summary>
internal static class ReferenceVisibility
{
    /// <summary>
    ///     Verbatim pre-gating <c>VisibilityAnalyzer.EvaluatePair</c>: every anchor is cast unless
    ///     the pair is already exposed and the anchor cannot newly could-see.
    /// </summary>
    /// <param name="engine">Collision geometry.</param>
    /// <param name="viewer">The viewer.</param>
    /// <param name="target">The target.</param>
    /// <param name="yawHalfDeg">Half the horizontal FOV in degrees.</param>
    /// <param name="pitchHalfDeg">Half the vertical FOV in degrees.</param>
    /// <param name="smokes">Active smoke spheres.</param>
    public static (bool Exposed, bool CouldSee) EvaluatePair(
        VisibilityEngine engine, in VisibilityAnalyzer.Vantage viewer, in VisibilityAnalyzer.Vantage target,
        float yawHalfDeg, float pitchHalfDeg, ReadOnlySpan<Vector4> smokes)
    {
        Span<Vector3> anchors = stackalloc Vector3[PlayerVantage.MaxAnchors];
        int n = PlayerVantage.BuildAnchors(target.Feet, target.Duck, viewer.Eye, anchors);
        ViewFrustum frustum = viewer.HasForward
            ? new ViewFrustum(viewer.Eye, viewer.Forward, yawHalfDeg, pitchHalfDeg)
            : default;

        bool exposed = false, couldSee = false;
        for (int i = 0; i < n; i++)
        {
            bool inFov = viewer.HasForward && frustum.Contains(anchors[i]);

            // Skip the ray if it can neither newly-expose nor newly-could-see.
            if (exposed && (!inFov || couldSee))
            {
                continue;
            }

            bool clear = engine.IsVisible(viewer.Eye, anchors[i]);
            if (clear)
            {
                exposed = true;
                // Vision additionally requires the sightline not to pass through active smoke.
                if (inFov && !SmokeVolumes.SegmentBlocked(viewer.Eye, anchors[i], smokes))
                {
                    couldSee = true;
                }
            }

            if (exposed && couldSee)
            {
                break;
            }
        }

        return (exposed, couldSee);
    }

    /// <summary>
    ///     Verbatim pre-gating <c>VisibilityTransitionScanner</c>: hash-set pair state, a dictionary
    ///     of on-target stamps, and the anchor set rebuilt for the chest-aim test. Feeds
    ///     <see cref="EvaluatePair" /> rather than the live analyzer.
    /// </summary>
    internal sealed class ReferenceScanner
    {
        private readonly VisibilityEngine _engine;
        private readonly Dictionary<int, int> _onTargetSince = [];
        private readonly HashSet<(int Viewer, int Target)> _onTarget = [];
        private readonly VisibilityTransitionScanner.Options _options;
        private readonly List<EnemySpottedEvent> _spots = new(4);
        private HashSet<(int Viewer, int Target)> _current = new();
        private int _lastSampledTick = int.MinValue;
        private HashSet<(int Viewer, int Target)> _visible = new();

        /// <param name="engine">The loaded collision geometry every sightline is cast against.</param>
        /// <param name="options">Sampling stride and frustum half-angles.</param>
        public ReferenceScanner(VisibilityEngine engine, VisibilityTransitionScanner.Options? options = null)
        {
            _engine = engine;
            _options = options ?? new VisibilityTransitionScanner.Options();
        }

        /// <summary>Is any enemy visible to <paramref name="viewerSlot" /> as of the most recent sample?</summary>
        /// <param name="viewerSlot">The player whose view is being asked about.</param>
        public bool IsAnyEnemyVisibleTo(int viewerSlot)
        {
            foreach ((int viewer, int _) in _visible)
            {
                if (viewer == viewerSlot)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>The tick the viewer's crosshair arrived on an enemy, or -1.</summary>
        /// <param name="viewerSlot">The player whose crosshair is being asked about.</param>
        public int OnTargetSince(int viewerSlot) => _onTargetSince.GetValueOrDefault(viewerSlot, -1);

        /// <summary>One sampled tick; see the live scanner for the contract.</summary>
        /// <param name="tick">The absolute server tick being sampled.</param>
        /// <param name="gameTick">The same instant on the frame clock.</param>
        /// <param name="vantages">This tick's vantage set.</param>
        /// <param name="smokes">Active smoke spheres.</param>
        public IReadOnlyList<EnemySpottedEvent> Sample(
            int tick, int gameTick, IReadOnlyList<AimVantage> vantages, ReadOnlySpan<Vector4> smokes)
        {
            _spots.Clear();

            if (_lastSampledTick != int.MinValue
                && tick >= _lastSampledTick
                && tick - _lastSampledTick < _options.SampleStrideTicks)
            {
                return _spots;
            }

            _lastSampledTick = tick;
            _current.Clear();
            _onTarget.Clear();

            for (int v = 0; v < vantages.Count; v++)
            {
                AimVantage viewerAim = vantages[v];
                VisibilityAnalyzer.Vantage viewer = viewerAim.Vantage;
                for (int t = 0; t < vantages.Count; t++)
                {
                    VisibilityAnalyzer.Vantage target = vantages[t].Vantage;
                    if (viewer.Slot == target.Slot || !VisibilityAnalyzer.AreEnemies(viewer, target))
                    {
                        continue;
                    }

                    (bool _, bool couldSee) = EvaluatePair(
                        _engine, viewer, target, _options.YawHalfDeg, _options.PitchHalfDeg, smokes);
                    if (!couldSee)
                    {
                        continue;
                    }

                    (int Viewer, int Target) pair = (viewer.Slot, target.Slot);
                    _current.Add(pair);

                    (float chest, float range) = ChestAim(viewer, target);
                    float halfWidth = range <= 1f
                        ? 90f
                        : (float)(Math.Atan2(VisibilityTransitionScanner.OnTargetHalfWidthUnits, range) * 180.0 / Math.PI);
                    if (chest <= halfWidth)
                    {
                        _onTarget.Add(pair);
                        if (!_onTargetSince.ContainsKey(viewer.Slot))
                        {
                            _onTargetSince[viewer.Slot] = tick;
                        }
                    }

                    if (_visible.Contains(pair))
                    {
                        continue;
                    }

                    _spots.Add(new EnemySpottedEvent(
                        tick, tick, gameTick, viewer.Slot, target.Slot,
                        chest,
                        viewerAim.EyePitchDeg, viewerAim.EyeYawDeg));
                }
            }

            foreach (int viewer in _onTargetSince.Keys.ToList())
            {
                bool stillOn = false;
                foreach ((int v, int _) in _onTarget)
                {
                    if (v == viewer)
                    {
                        stillOn = true;
                        break;
                    }
                }

                if (!stillOn)
                {
                    _onTargetSince.Remove(viewer);
                }
            }

            (_visible, _current) = (_current, _visible);
            return _spots;
        }

        // The pre-change scanner rebuilt the whole anchor set and read anchor 0. Anchor 0 is written
        // here as the literal expression BuildAnchors held at the time (height compression by
        // Lerp(1, crouched / standing, duck), chest at 48 units scaled) rather than through
        // BuildAnchors, so that a change to how the live code derives the chest point is measured
        // against the original arithmetic and not against itself.
        private static (float AngleDeg, float RangeUnits) ChestAim(
            in VisibilityAnalyzer.Vantage viewer, in VisibilityAnalyzer.Vantage target)
        {
            float t = Math.Clamp(target.Duck, 0f, 1f);
            float f = 1f + (PlayerVantage.EyeCrouched / PlayerVantage.EyeStanding - 1f) * t;
            Vector3 chest = new(target.Feet.X, target.Feet.Y, target.Feet.Z + 48f * f);
            return (
                PlayerVantage.AngleToPointDegrees(viewer.Eye, viewer.Forward, chest),
                Vector3.Distance(viewer.Eye, chest));
        }
    }
}
