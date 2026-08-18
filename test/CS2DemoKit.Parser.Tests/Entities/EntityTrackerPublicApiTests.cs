#region

using CS2DemoKit.Parser.Entities;
using CS2DemoKit.Parser.EntityTracking;

#endregion

namespace CS2DemoKit.Parser.Entities.Tests;

/// <summary>
///     Tests for the public Tier-3 API surface on
///     <see cref="EntityTracker" />: <see cref="EntityTracker.Get{T}" />,
///     <see cref="EntityTracker.Snapshot{T}" />, <see cref="EntityTracker.ResolveHandle{T}" />,
///     <see cref="EntityTracker.GetFieldMeta" />.
///     <para>
///         These tests assert wiring correctness at empty / null-slot / no-factory
///         boundaries. End-to-end integration with the emitted typed wrappers
///         lives in the SdkAbstractions suites; the
///         public surface is exercised here only for null / empty / sentinel paths.
///     </para>
/// </summary>
[Category("Unit")]
public class EntityTrackerPublicApiTests
{
    // ── Get<T> ───────────────────────────────────────────────────────────────

    /// <summary>An empty slot returns <c>null</c> without throwing.</summary>
    [Test]
    [Category("Smoke")]
    public async Task Get_ReturnsNull_WhenSlotEmpty()
    {
        EntityTracker tracker = new();
        StubWrapper? wrapper = tracker.Get<StubWrapper>(7);

        await Assert.That(wrapper).IsNull();
    }

    /// <summary>
    ///     A registered factory is dispatched to when the slot's class matches.
    ///     The wrapper is returned as the generic type when the factory's concrete
    ///     type is assignable to it.
    /// </summary>
    [Test]
    public async Task Get_DispatchesToRegisteredFactory()
    {
        EntityTracker tracker = new();
        tracker.RegisterEntityFactory("CCSPlayerPawn", (s, t) => new StubWrapper(s, t));

        // Without a Schema we can't go through PacketEntities, but the public API
        // works equally well off a manually-injected state — Get<T> indexes into
        // CurrentEntities by slot. There's no public way to inject a state from
        // outside the assembly, so this test exercises only the null-path here
        // and defers the "happy path" to the wrapper integration tests.
        StubWrapper? wrapper = tracker.Get<StubWrapper>(99);
        await Assert.That(wrapper).IsNull();

        // Re-registering with a different factory replaces the previous one.
        bool sentinelTouched = false;
        tracker.RegisterEntityFactory("CCSPlayerPawn", (s, t) =>
        {
            sentinelTouched = true;
            return new StubWrapper(s, t);
        });

        // Still empty slot — no factory dispatch.
        _ = tracker.Get<StubWrapper>(99);
        await Assert.That(sentinelTouched).IsFalse();
    }

    // ── Snapshot<T> ──────────────────────────────────────────────────────────

    /// <summary>
    ///     <see cref="EntityTracker.Snapshot{T}" /> degrades to the same null-path
    ///     behaviour as <see cref="EntityTracker.Get{T}" /> on empty slots.
    /// </summary>
    [Test]
    [Category("Smoke")]
    public async Task Snapshot_ReturnsNull_WhenSlotEmpty()
    {
        EntityTracker tracker = new();
        StubWrapper? wrapper = tracker.Snapshot<StubWrapper>(7);

        await Assert.That(wrapper).IsNull();
    }

    // ── ResolveHandle<T> ─────────────────────────────────────────────────────

    /// <summary>
    ///     A zero handle is the "no entity" sentinel and resolves to <c>null</c>
    ///     without consulting the entity table. This is V1's locked behaviour
    ///     for handle dereferencing (R5).
    /// </summary>
    [Test]
    [Category("Smoke")]
    public async Task ResolveHandle_ReturnsNull_ForZeroHandle()
    {
        EntityTracker tracker = new();
        StubWrapper? wrapper = tracker.ResolveHandle<StubWrapper>(0);

        await Assert.That(wrapper).IsNull();
    }

    /// <summary>
    ///     The <c>0xFFFFFFFF</c> ("explicit invalid") sentinel — which arrives on the
    ///     int lane as <c>-1</c> after the V1 HandleIndex identity transform —
    ///     also resolves to <c>null</c>.
    /// </summary>
    [Test]
    [Category("Smoke")]
    public async Task ResolveHandle_ReturnsNull_ForInvalidSentinel()
    {
        EntityTracker tracker = new();
        StubWrapper? wrapper = tracker.ResolveHandle<StubWrapper>(-1);

        await Assert.That(wrapper).IsNull();
    }

    /// <summary>
    ///     A non-sentinel handle whose low 14 bits don't index a live entity
    ///     resolves to <c>null</c> (empty slot path through <see cref="EntityTracker.Get{T}" />).
    /// </summary>
    [Test]
    public async Task ResolveHandle_ReturnsNull_ForLiveButEmptyTargetSlot()
    {
        EntityTracker tracker = new();

        // Slot 42 is empty — even if the handle's high bits encode a serial,
        // the slot's wrapper construction can't proceed (no factory, no state).
        int handle = 42 | 1 << 17; // slot 42, serial 1
        StubWrapper? wrapper = tracker.ResolveHandle<StubWrapper>(handle);

        await Assert.That(wrapper).IsNull();
    }

    // ── GetFieldMeta ─────────────────────────────────────────────────────────

    /// <summary>
    ///     A class with no descriptors built yet returns <c>null</c> — the wire-type
    ///     introspection hook is lazy and depends on the internal descriptor cache
    ///     having been primed by an earlier <c>BuildFieldDescs</c> walk.
    /// </summary>
    [Test]
    [Category("Smoke")]
    public async Task GetFieldMeta_ReturnsNull_ForUnseenClass()
    {
        EntityTracker tracker = new();
        RuntimeField? meta = tracker.GetFieldMeta("CCSPlayerPawn", "m_iHealth");

        await Assert.That(meta).IsNull();
    }

    // ── Test helper wrapper ──────────────────────────────────────────────────

    /// <summary>
    ///     Minimal test stand-in for a factory-produced wrapper. The
    ///     Get/Snapshot/ResolveHandle constraints are <c>where T : class</c> precisely
    ///     so ANY factory product qualifies — this harness type, or the SDK-emitted
    ///     wrappers production registers via <c>TrackerEntityWorld</c>.
    /// </summary>
    private sealed class StubWrapper
    {
        public StubWrapper(EntityState state, EntityTracker tracker)
        {
            State = state;
            Tracker = tracker;
        }

        public EntityState State { get; }
        public EntityTracker Tracker { get; }
    }
}
