#region

using CS2DemoKit.Parser.Entities.Generated;
using CS2DemoKit.Parser.Entities.SchemaLens;
using CS2DemoKit.Parser.EntityTracking;

#endregion

namespace CS2DemoKit.Parser.Entities;

/// <summary>
///     Constructs <see cref="EntityTracker" /> instances with the codegen-emitted Schema Lens
///     resolver bound — the lane-mapping step whose omission does not throw: the tracker decodes
///     happily and lane-routed reads land on the fallback dict instead.
///     <para>
///         Historical note: until the SDK cutover this also registered the local generated
///         wrapper factories (<c>EntityFactoryRegistry</c>). The local wrapper layer is retired —
///         typed reads go through the SDK-emitted wrappers (<c>CS2OpenDev.Sdk.Entities</c>) bound
///         via the SdkAbstractions seam; consumers wire those factories per tracker (the Analysis
///         layer's <c>SdkEntityWorlds</c> is the production wiring point).
///     </para>
/// </summary>
public static class EntityTrackerFactory
{
    /// <summary>
    ///     Returns a fresh <see cref="EntityTracker" /> with the codegen-emitted Schema Lens
    ///     resolver bound. Nothing has been replayed yet: feed it frames with
    ///     <see cref="EntityTracker.Replay" />, <see cref="EntityTracker.ReplayToIndex" /> or
    ///     <see cref="EntityTracker.AdvanceOneFrame" /> exactly as you would a hand-built tracker.
    ///     <para>
    ///         Post-construction configuration still works — <c>StoreClassFilter</c>,
    ///         <see cref="EntityTracker.DecodeErrorRaised" /> and
    ///         <see cref="EntityTracker.DecodeDiagnosticSink" /> can all be set on the returned
    ///         instance before the first frame.
    ///     </para>
    ///     <para>
    ///         Not cheap enough to call per frame: the lens state is rebuilt per call. Call it
    ///         once per tracker, which is once per replay.
    ///     </para>
    /// </summary>
    public static EntityTracker CreateCurated()
    {
        EntityTracker tracker = new();
        tracker.BindLensResolver(LensResolverBridge.Build(GeneratedLensRegistry.Load()));
        return tracker;
    }
}
