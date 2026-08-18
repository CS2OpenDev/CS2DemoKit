#region

using CS2DemoKit.Parser.EntityTracking;
using EtLensTransform = CS2DemoKit.Parser.EntityTracking.LensTransform;
using SlLensTransform = CS2DemoKit.Parser.Entities.SchemaLens.LensTransform;

#endregion

namespace CS2DemoKit.Parser.Entities.SchemaLens;

/// <summary>
///     Production-side bridge between the Entities-side <see cref="LensState" /> and the
///     EntityTracking-side <see cref="LensResolver" /> delegate. EntityTracking sits below
///     the Entities project in the dependency graph and cannot name <see cref="LensState" />
///     directly; the bridge captures the state in a closure and translates lookups on demand.
///     <para>
///         Bootstrap pattern (what <c>EntityTrackerFactory.CreateCurated</c> performs):
///         <code>
///         // LensState state = GeneratedLensRegistry.Load();
///         // tracker.BindLensResolver(LensResolverBridge.Build(state));
///         // (SDK wrapper factories, if wanted, register separately via TrackerEntityWorld.)
///         </code>
///     </para>
///     <para>
///         Mirrors the test-side helper <c>LensResolverBridgeTests.BridgeLensStateToResolver</c>
///         which now delegates here. Forwards <c>FieldRule.LensSlot</c> through into
///         <see cref="LensSlotRule" /> so the runtime allocator's pre-pass reservation
///         (<c>EntityTracker.PreReserveLensSlots</c> / <c>ClassShapeBuilder.ReserveLensSlot</c>)
///         pins each leaf to the exact slot the codegen wrapper expects.
///     </para>
/// </summary>
public static class LensResolverBridge
{
    /// <summary>
    ///     Builds a <see cref="LensResolver" /> closure around the given <paramref name="state" />.
    ///     The returned delegate is thread-safe for read; the captured state is treated as
    ///     immutable once <c>GeneratedLensRegistry.Load()</c> has produced it.
    /// </summary>
    public static LensResolver Build(LensState state) =>
        (serializerName, enginePath) =>
        {
            if (!state.AliasMap.TryGetValue(serializerName, out Dictionary<string, string>? aliases))
            {
                return null;
            }

            if (!aliases.TryGetValue(enginePath, out string? canonical))
            {
                return null;
            }

            if (!state.Fields.TryGetValue(serializerName, out Dictionary<string, FieldRule>? fields))
            {
                return null;
            }

            if (!fields.TryGetValue(canonical, out FieldRule? rule))
            {
                return null;
            }

            LaneKind lane = rule.WireType switch
            {
                WireType.IntLane => LaneKind.Int,
                WireType.FloatLane => LaneKind.Float,
                WireType.ObjectLane => LaneKind.Object,
                _ => LaneKind.Fallback
            };

            EtLensTransform transform = TranslateTransform(rule.Transform);

            return new LensSlotRule(lane, transform, FallbackDefault: null, rule.LensSlot);
        };

    /// <summary>
    ///     Translates the Entities-side transform enum to the EntityTracking-side one. The
    ///     Entities enum is the slim post-derivation vocabulary (None/HandleIndex); the
    ///     EntityTracking enum keeps extra members for decoder-internal use.
    /// </summary>
    public static EtLensTransform TranslateTransform(SlLensTransform t) =>
        t switch
        {
            SlLensTransform.HandleIndex => EtLensTransform.HandleIndex,
            _ => EtLensTransform.None
        };
}
