#region

using CS2DemoKit.Parser.Entities.Generated;
using CS2DemoKit.Parser.Entities.SchemaLens;
using CS2DemoKit.Parser.EntityTracking;
using EtLensTransform = CS2DemoKit.Parser.EntityTracking.LensTransform;
using SlLensTransform = CS2DemoKit.Parser.Entities.SchemaLens.LensTransform;

#endregion

namespace CS2DemoKit.Parser.Entities.Tests;

/// <summary>
///     Tests for the <see cref="LensResolver" /> bridge between the
///     Entities-side <see cref="LensState" /> and the EntityTracking-side
///     <see cref="LensResolver" /> delegate. EntityTracker cannot reference
///     <see cref="LensState" /> directly (Entities → EntityTracking is load-bearing
///     on EntityTracking types like <c>EntityState</c>); the bridge captures
///     <see cref="LensState" /> in a closure and translates lookups on demand.
///     <para>
///         These tests assert:
///         <list type="bullet">
///             <item>
///                 Every active class in <see cref="GeneratedLensRegistry.Load" /> has at
///                 least one resolvable path through the bridge.
///             </item>
///             <item>
///                 Lens-mapped fields produce <see cref="LensSlotRule" /> values whose
///                 <see cref="LaneKind" /> matches the codegen-emitted <see cref="WireType" />.
///             </item>
///             <item>
///                 The two <c>LensTransform</c> enums (Entities-side and EntityTracking-side)
///                 round-trip 1:1 by name — guards against drift between the locally-mirrored
///                 enum and the canonical migration vocabulary.
///             </item>
///             <item>
///                 Unmapped paths return <c>null</c>, leaving the descriptor walk to the
///                 plain decoder-kind classification path.
///             </item>
///         </list>
///     </para>
/// </summary>
[Category("Unit")]
public class LensResolverBridgeTests
{
    /// <summary>
    ///     Builds a <see cref="LensResolver" /> closure around the given <see cref="LensState" />.
    ///     This is the canonical bridge any caller in a project that references both
    ///     <c>CS2DemoKit.Parser.Entities</c> and <c>CS2DemoKit.Parser.EntityTracking</c>
    ///     should reuse — the entity-factory bootstrap and the analysis layer follow
    ///     the same pattern.
    /// </summary>
    public static LensResolver BridgeLensStateToResolver(LensState state) =>
        // Delegates to the production-side bridge in CS2DemoKit.Parser.Entities.SchemaLens
        // so the test-side and production-side translation logic stay in lockstep.
        LensResolverBridge.Build(state);

    /// <summary>
    ///     Mirrors the closed-set canonical-form vocabulary. Drift here means the
    ///     EntityTracking-local enum has diverged from the Entities-side authoritative
    ///     one — caught by <see cref="TranslateTransform_CoversTheFullEntitiesVocabulary" />.
    /// </summary>
    public static EtLensTransform TranslateTransform(SlLensTransform t) =>
        LensResolverBridge.TranslateTransform(t);

    /// <summary>
    ///     The Entities-side enum is the slim post-derivation vocabulary (None/HandleIndex);
    ///     the EntityTracking-side enum keeps extra decoder-internal members. The bridge's
    ///     translation must cover every Entities-side member explicitly — a vocabulary
    ///     expansion that silently degrades to <c>None</c> would drop handle semantics.
    /// </summary>
    private static readonly string[] ExpectedTransformVocabulary = ["None", "HandleIndex"];

    [Test]
    [Category("Smoke")]
    public async Task TranslateTransform_CoversTheFullEntitiesVocabulary()
    {
        await Assert.That(Enum.GetNames<SlLensTransform>())
            .IsEquivalentTo(ExpectedTransformVocabulary);
        await Assert.That(TranslateTransform(SlLensTransform.None)).IsEqualTo(EtLensTransform.None);
        await Assert.That(TranslateTransform(SlLensTransform.HandleIndex))
            .IsEqualTo(EtLensTransform.HandleIndex);
    }

    /// <summary>
    ///     Bridge wiring: every Lens-mapped <c>(class, engine_field)</c> the codegen emitted
    ///     resolves to a non-null <see cref="LensSlotRule" /> through the bridge with the
    ///     correct lane.
    /// </summary>
    [Test]
    [Category("Smoke")]
    public async Task Bridge_ResolvesEveryLensField_WithCorrectLane()
    {
        LensState state = GeneratedLensRegistry.Load();
        LensResolver resolver = BridgeLensStateToResolver(state);

        foreach ((string className, Dictionary<string, FieldRule> fieldMap) in state.Fields)
        {
            foreach ((string canonical, FieldRule rule) in fieldMap)
            {
                LensSlotRule? resolved = resolver(className, canonical);

                await Assert.That(resolved).IsNotNull();

                LaneKind expectedLane = rule.WireType switch
                {
                    WireType.IntLane => LaneKind.Int,
                    WireType.FloatLane => LaneKind.Float,
                    WireType.ObjectLane => LaneKind.Object,
                    _ => LaneKind.Fallback
                };
                await Assert.That(resolved!.Value.Lane).IsEqualTo(expectedLane);

                EtLensTransform expectedTransform = TranslateTransform(rule.Transform);
                await Assert.That(resolved.Value.Transform).IsEqualTo(expectedTransform);
            }
        }
    }

    /// <summary>
    ///     Unmapped paths must return <c>null</c> — that's the signal to
    ///     <see cref="EntityTracker" /> that the leaf should follow the plain
    ///     decoder-kind classification path (no transform, no Lens-supplied default).
    /// </summary>
    [Test]
    [Category("Smoke")]
    public async Task Bridge_ReturnsNullForUnmappedPaths()
    {
        LensState state = GeneratedLensRegistry.Load();
        LensResolver resolver = BridgeLensStateToResolver(state);

        // Unknown class.
        await Assert.That(resolver("CTotallyMadeUpClass", "m_iHealth")).IsNull();

        // Known class, unknown field.
        await Assert.That(resolver("CCSPlayerPawn", "m_thisFieldDoesNotExistInGenesis")).IsNull();
    }

    /// <summary>
    ///     <see cref="EntityTracker.BindLensResolver" /> accepts the bridge closure
    ///     without throwing. The descriptor cache is only built when wire data flows
    ///     (a real demo / synthetic schema), so this is purely a wiring smoke test —
    ///     the production-style integration is exercised by the existing
    ///     Analysis test suite (which passes through the bound resolver as a no-op
    ///     when none of its paths happen to match — by design).
    /// </summary>
    [Test]
    [Category("Smoke")]
    public async Task EntityTracker_BindLensResolver_Accepts_BridgeClosure()
    {
        LensState state = GeneratedLensRegistry.Load();
        LensResolver resolver = BridgeLensStateToResolver(state);

        EntityTracker tracker = new();
        tracker.BindLensResolver(resolver);

        // Rebind to null is also legal — clears the resolver.
        tracker.BindLensResolver(null);

        await Assert.That(tracker).IsNotNull();
    }

    /// <summary>
    ///     Handle spot-check: <c>m_hController</c> resolves to the OBJECT lane (the honest
    ///     lane — the decoder boxes the raw wire handle there) and carries the
    ///     <see cref="EtLensTransform.HandleIndex" /> marker. The raw integer stays
    ///     undecoded on the lane; masking and sentinels belong to handle resolution.
    /// </summary>
    [Test]
    public async Task Bridge_HandleIndex_RoutesToObjectLane_WithHandleIndexTransform()
    {
        LensState state = GeneratedLensRegistry.Load();
        LensResolver resolver = BridgeLensStateToResolver(state);

        LensSlotRule? rule = resolver("CCSPlayerPawn", "m_hController");

        await Assert.That(rule).IsNotNull();
        await Assert.That(rule!.Value.Lane).IsEqualTo(LaneKind.Object);
        await Assert.That(rule.Value.Transform).IsEqualTo(EtLensTransform.HandleIndex);
    }

    /// <summary>
    ///     Bool spot-check: <c>CCSGameRules.m_bFreezePeriod</c> resolves to the int lane
    ///     (bools live as Int32 0/1 on the wire) with no transform — the wrapper-era
    ///     <c>BoolFromInt</c> vocabulary retired with the migration JSONs; readers compare
    ///     the lane value against zero.
    /// </summary>
    [Test]
    public async Task Bridge_Bool_RoutesToIntLane_NoTransform()
    {
        LensState state = GeneratedLensRegistry.Load();
        LensResolver resolver = BridgeLensStateToResolver(state);

        LensSlotRule? rule = resolver("CCSGameRules", "m_bFreezePeriod");

        await Assert.That(rule).IsNotNull();
        await Assert.That(rule!.Value.Lane).IsEqualTo(LaneKind.Int);
        await Assert.That(rule.Value.Transform).IsEqualTo(EtLensTransform.None);
    }

    /// <summary>
    ///     Sub-service fields (flattened under their parent path on
    ///     the wire) round-trip through the bridge with the
    ///     same lane the migration declared.
    /// </summary>
    [Test]
    public async Task Bridge_SubServiceField_RoutesToDeclaredLane()
    {
        LensState state = GeneratedLensRegistry.Load();
        LensResolver resolver = BridgeLensStateToResolver(state);

        LensSlotRule? rule = resolver("CCSPlayerController", "m_pInGameMoneyServices.m_iAccount");

        await Assert.That(rule).IsNotNull();
        await Assert.That(rule!.Value.Lane).IsEqualTo(LaneKind.Int);
        await Assert.That(rule.Value.Transform).IsEqualTo(EtLensTransform.None);
    }
}
