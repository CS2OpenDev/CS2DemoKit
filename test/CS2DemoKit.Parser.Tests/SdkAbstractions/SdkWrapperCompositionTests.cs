#region

using System.Numerics;
using CS2OpenDev.Sdk.Entities;
using CS2DemoKit.Parser.Entities.SdkAbstractions;
using CS2DemoKit.Parser.EntityTracking;

#endregion

namespace CS2DemoKit.Parser.SdkAbstractions.Tests;

/// <summary>
///     The upstream <c>WrapperCompositionTests</c> (SDK#6 wrapper-over-the-seam composition)
///     ported to DVN's real runtime pieces: hand-written wrappers in the emitter's future
///     shape, reading through <see cref="LensBoundReader" /> over fabricated
///     <see cref="EntityState" /> instances living in a real <see cref="EntityTracker" />'s
///     entity table, with handles resolved through <see cref="TrackerEntityWorld" /> — i.e.
///     the tracker's own sentinel checks, 14-bit index mask and factory dispatch, not a fake
///     handle table. No demo parsing anywhere.
/// </summary>
[Category("Unit")]
public class SdkWrapperCompositionTests
{
    private const string Origin = SdkTestStates.Origin;

    // Slots chosen so the tracker's index mask is genuinely exercised: the weapon handle
    // carries a serial in the high bits and the tracker must mask it off to find the slot.
    private const int WeaponSlot = 0x1234;
    private const uint WeaponHandle = 0x0002_1234u; // (serial 1 << 17) | slot 0x1234
    private const int PawnSlot = 50;

    // ── What the emitter would generate ───────────────────────────────────────

    private sealed class BasePlayerWeapon(IEntityFieldReader reader, IEntityWorld world)
        : EntityWrapper(reader, world)
    {
        [SchemaFieldVersion("genesis")]
        public int Clip1 => Reader.TryReadInt32(Ord.Clip1, out int v) ? v : 0;

        private static class Ord
        {
            internal const int Clip1 = 0;
        }
    }

    private sealed class CSPlayerPawn(IEntityFieldReader reader, IEntityWorld world)
        : EntityWrapper(reader, world)
    {
        /// <summary>0-default policy: absent reads as 0, which is harmless for health.</summary>
        [SchemaFieldVersion("genesis")]
        public int Health => Reader.TryReadInt32(Ord.Health, out int v) ? v : 0;

        /// <summary>Seen-aware policy: 0 means LIFE_ALIVE, so absent must not be 0.</summary>
        [SchemaFieldVersion("genesis")]
        public int? LifeState => Reader.TryReadInt32(Ord.LifeState, out int v) ? v : null;

        [SchemaFieldVersion("genesis")]
        public Vector3? Origin => Reader.TryReadVector3(Ord.Origin, out Vector3 v) ? v : null;

        [SchemaFieldVersion("genesis")]
        public uint ActiveWeaponHandle =>
            Reader.TryReadEntityHandle(Ord.ActiveWeapon, out uint h) ? h : 0u;

        public BasePlayerWeapon? ActiveWeapon => World.Resolve<BasePlayerWeapon>(ActiveWeaponHandle);

        private static class Ord
        {
            internal const int Health = 0;
            internal const int LifeState = 1;
            internal const int Origin = 2;
            internal const int ActiveWeapon = 3;
        }
    }

    // ── Bindings in the emitter's future shape ────────────────────────────────

    private static EntityClassBinding PawnBinding() => new(
        EngineClass: "CCSPlayerPawn",
        NetName: "CSPlayerPawn",
        CanonicalPaths: ["m_iHealth", "m_lifeState", Origin, "m_pWeaponServices.m_hActiveWeapon"],
        Aliases: new Dictionary<string, string> { ["m_vecOrigin"] = Origin },
        HandleOrdinals: [3]);

    private static EntityClassBinding WeaponBinding() => new(
        EngineClass: "CBasePlayerWeapon",
        NetName: "BasePlayerWeapon",
        CanonicalPaths: ["m_iClip1"],
        Aliases: new Dictionary<string, string>(),
        HandleOrdinals: []);

    // ── Fabricated runtime: a real tracker, no demo ───────────────────────────

    private static ClassShape PawnShape()
    {
        ClassShapeBuilder b = new("CCSPlayerPawn");
        b.Allocate(LaneKind.Int, "m_iHealth");
        b.Allocate(LaneKind.Int, "m_lifeState");
        b.Allocate(LaneKind.Object, Origin);
        b.Allocate(LaneKind.Object, "m_pWeaponServices.m_hActiveWeapon"); // handle → boxed ulong
        return b.Build();
    }

    private static ClassShape WeaponShape()
    {
        ClassShapeBuilder b = new("CBasePlayerWeapon");
        b.Allocate(LaneKind.Int, "m_iClip1");
        return b.Build();
    }

    private static (EntityTracker Tracker, TrackerEntityWorld World, EntityState Pawn) NewRuntime()
    {
        EntityTracker tracker = new();
        TrackerEntityWorld world = new(tracker);
        world.RegisterWrapper(PawnBinding(), (r, w) => new CSPlayerPawn(r, w));
        world.RegisterWrapper(WeaponBinding(), (r, w) => new BasePlayerWeapon(r, w));

        EntityState pawn = tracker.CurrentEntities.GetOrCreate(PawnSlot, "CCSPlayerPawn", serial: 1);
        pawn.BindShape(PawnShape());
        return (tracker, world, pawn);
    }

    private static EntityState AddWeapon(EntityTracker tracker)
    {
        EntityState weapon = tracker.CurrentEntities.GetOrCreate(WeaponSlot, "CBasePlayerWeapon", serial: 1);
        weapon.BindShape(WeaponShape());
        return weapon;
    }

    // ── The tests ─────────────────────────────────────────────────────────────

    /// <summary>A wrapper reads its fields through the seam, constructed by the tracker's own factory dispatch.</summary>
    [Test]
    public async Task Wrapper_ReadsFieldsThroughTheSeam()
    {
        (EntityTracker tracker, _, EntityState state) = NewRuntime();
        SdkTestStates.Write(state, "m_iHealth", 87);
        SdkTestStates.Write(state, "m_lifeState", 0);
        SdkTestStates.Write(state, Origin, new Vector3(1, 2, 3));

        CSPlayerPawn? pawn = tracker.Get<CSPlayerPawn>(PawnSlot);

        await Assert.That(pawn).IsNotNull();
        await Assert.That(pawn!.Health).IsEqualTo(87);
        await Assert.That(pawn.LifeState).IsEqualTo(0);
        await Assert.That(pawn.Origin).IsEqualTo(new Vector3(1, 2, 3));
        await Assert.That(pawn.EngineClassName).IsEqualTo("CCSPlayerPawn");
    }

    /// <summary>The two read policies differ where it counts: an unsent lifeState is null, an unsent health is 0.</summary>
    [Test]
    public async Task ReadPolicies_DistinguishAbsentFromZero()
    {
        (EntityTracker tracker, _, _) = NewRuntime();

        CSPlayerPawn? pawn = tracker.Get<CSPlayerPawn>(PawnSlot);

        await Assert.That(pawn!.Health).IsEqualTo(0); // default-when-absent
        await Assert.That(pawn.LifeState).IsNull(); // null-when-absent
    }

    /// <summary>
    ///     A handle resolves to a live wrapper of the requested type — through the tracker's
    ///     own mask and slot lookup (the handle carries a serial in its high bits which the
    ///     tracker strips; nothing on the adapter side touches the bits).
    /// </summary>
    [Test]
    public async Task Handle_ResolvesToAnotherWrapper()
    {
        (EntityTracker tracker, _, EntityState pawnState) = NewRuntime();
        EntityState weaponState = AddWeapon(tracker);
        SdkTestStates.Write(weaponState, "m_iClip1", 30);
        SdkTestStates.Write(pawnState, "m_pWeaponServices.m_hActiveWeapon", (ulong)WeaponHandle);

        CSPlayerPawn? pawn = tracker.Get<CSPlayerPawn>(PawnSlot);

        await Assert.That(pawn!.ActiveWeaponHandle).IsEqualTo(WeaponHandle);
        await Assert.That(pawn.ActiveWeapon).IsNotNull();
        await Assert.That(pawn.ActiveWeapon!.Clip1).IsEqualTo(30);
    }

    /// <summary>An unresolvable handle yields null rather than throwing — empty slot, zero and invalid sentinels all collapse to the same answer.</summary>
    [Test]
    public async Task UnresolvableHandle_YieldsNull()
    {
        (_, TrackerEntityWorld world, EntityState pawnState) = NewRuntime();
        SdkTestStates.Write(pawnState, "m_pWeaponServices.m_hActiveWeapon", 0xDEAD_BEEFUL);

        // Slot 0x3EEF (0xDEADBEEF masked) is empty in this tracker.
        await Assert.That(world.Resolve<BasePlayerWeapon>(0xDEAD_BEEFu)).IsNull();
        // The two "no entity" sentinels.
        await Assert.That(world.Resolve<BasePlayerWeapon>(0u)).IsNull();
        await Assert.That(world.Resolve<BasePlayerWeapon>(0xFFFF_FFFFu)).IsNull();
    }

    /// <summary>A handle whose target is live but of another class resolves to null for the requested type.</summary>
    [Test]
    public async Task WrongTypeTarget_ResolvesToNull()
    {
        (EntityTracker tracker, TrackerEntityWorld world, _) = NewRuntime();
        AddWeapon(tracker);

        await Assert.That(world.Resolve<BasePlayerWeapon>(WeaponHandle)).IsNotNull();
        await Assert.That(world.Resolve<CSPlayerPawn>(WeaponHandle)).IsNull();
    }

    /// <summary>The string indexer reaches a field no property covers, including through an alias.</summary>
    [Test]
    public async Task Indexer_ReachesUncoveredFieldsAndHistoricalSpellings()
    {
        (EntityTracker tracker, _, EntityState state) = NewRuntime();
        SdkTestStates.Write(state, Origin, new Vector3(9, 9, 9));

        CSPlayerPawn? pawn = tracker.Get<CSPlayerPawn>(PawnSlot);

        await Assert.That(pawn![Origin]).IsEqualTo(new Vector3(9, 9, 9));
        await Assert.That(pawn["m_vecOrigin"]).IsEqualTo(new Vector3(9, 9, 9));
        await Assert.That(pawn["m_notAField"]).IsNull();
    }

    /// <summary>The bindings pass conformance, and the pawn's handle ordinal really is the handle field.</summary>
    [Test]
    public async Task EmittedShapeBindings_Conform()
    {
        EntityClassBinding pawn = PawnBinding();

        await Assert.That(BindingConformance.Validate(pawn).ToArray()).IsEmpty();
        await Assert.That(BindingConformance.Validate(WeaponBinding()).ToArray()).IsEmpty();

        await Assert.That(pawn.CanonicalPaths[pawn.HandleOrdinals.Single()])
            .IsEqualTo("m_pWeaponServices.m_hActiveWeapon");
    }
}
