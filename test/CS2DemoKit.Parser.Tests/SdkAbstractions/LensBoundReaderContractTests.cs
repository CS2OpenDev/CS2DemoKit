#region

using System.Numerics;
using CS2OpenDev.Sdk.Entities;
using CS2DemoKit.Parser.Entities.SdkAbstractions;
using CS2DemoKit.Parser.EntityTracking;

#endregion

namespace CS2DemoKit.Parser.SdkAbstractions.Tests;

/// <summary>
///     The upstream <c>ReadContractTests</c> (SDK#6 read-contract conformance) ported to run
///     against DVN's <see cref="LensBoundReader" /> over synthetically fabricated
///     <see cref="EntityState" /> instances — the same assertions the reference
///     <c>DictionaryEntityReader</c> satisfies, so the two implementations agree about what the
///     contract means. Divergences that are storage-inherent (not bugs) carry their own
///     documenting tests at the bottom and are called out in
///     <c>docs/upstream/sdk6-adapter-findings.md</c>.
/// </summary>
[Category("Unit")]
public class LensBoundReaderContractTests
{
    private const string Origin = SdkTestStates.Origin;

    private static LensBoundReader Reader(EntityState state)
        => new(state, SdkTestStates.PawnBinding());

    // ── Absent, received-null, and present are three different things ─────────

    /// <summary>A field never received reads as absent from every typed accessor — not as the type's default.</summary>
    [Test]
    public async Task NeverReceived_ReadsAsAbsentRatherThanDefault()
    {
        LensBoundReader reader = Reader(SdkTestStates.NewPawn());

        await Assert.That(reader.TryReadInt32(3, out int life)).IsFalse();
        await Assert.That(life).IsEqualTo(0); // the out value is default, but the return said so
        await Assert.That(reader.TryReadObject(3, out _)).IsFalse();
    }

    /// <summary>A received zero is reported as present, so a consumer can tell LIFE_ALIVE from silence.</summary>
    [Test]
    public async Task ReceivedZero_IsPresent()
    {
        EntityState state = SdkTestStates.NewPawn();
        SdkTestStates.Write(state, "m_lifeState", 0);
        LensBoundReader reader = Reader(state);

        await Assert.That(reader.TryReadInt32(3, out int life)).IsTrue();
        await Assert.That(life).IsEqualTo(0);
    }

    /// <summary>
    ///     An explicitly received null on the object lane is present to the boxed reader and
    ///     absent to the typed ones, which have no value to return.
    /// </summary>
    [Test]
    public async Task ReceivedNull_OnObjectLane_IsPresentToObjectReadAndAbsentToTypedReads()
    {
        EntityState state = SdkTestStates.NewPawn();
        SdkTestStates.Write(state, Origin, null); // object-lane slot, seen bit set, value null
        LensBoundReader reader = Reader(state);

        await Assert.That(reader.TryReadObject(1, out object? boxed)).IsTrue();
        await Assert.That(boxed).IsNull();
        await Assert.That(reader.TryReadVector3(1, out _)).IsFalse();
    }

    /// <summary>
    ///     The upstream case verbatim — a null for the int-typed <c>m_ArmorValue</c>. DVN's
    ///     int lane cannot hold null (and an int wire never delivers one), so this state only
    ///     arises through the fallback dictionary; there the asymmetry holds exactly.
    /// </summary>
    [Test]
    public async Task ReceivedNull_InFallbackDict_IsPresentToObjectReadAndAbsentToTypedReads()
    {
        EntityState state = SdkTestStates.NewShapelessPawn();
        state.SetFallback("m_ArmorValue", null);
        LensBoundReader reader = Reader(state);

        await Assert.That(reader.TryReadObject(0, out object? boxed)).IsTrue();
        await Assert.That(boxed).IsNull();
        await Assert.That(reader.TryReadInt32(0, out _)).IsFalse();
    }

    // ── Ordinal addressing ────────────────────────────────────────────────────

    /// <summary>Ordinals outside the binding's space read as absent instead of throwing, so a stale wrapper degrades rather than crashing.</summary>
    [Test]
    [Arguments(-1)]
    [Arguments(7)]
    [Arguments(999)]
    public async Task OrdinalOutsideTheBinding_ReadsAsAbsent(int ordinal)
    {
        EntityState state = SdkTestStates.NewPawn();
        SdkTestStates.Write(state, "m_ArmorValue", 100);
        LensBoundReader reader = Reader(state);

        await Assert.That(reader.TryReadInt32(ordinal, out _)).IsFalse();
    }

    /// <summary>Each ordinal addresses the canonical path at that index.</summary>
    [Test]
    public async Task OrdinalsAddressTheirCanonicalPath()
    {
        EntityState state = SdkTestStates.NewPawn();
        SdkTestStates.Write(state, "m_ArmorValue", 100);
        SdkTestStates.Write(state, Origin, new Vector3(1, 2, 3));
        LensBoundReader reader = Reader(state);

        await Assert.That(reader.TryReadInt32(0, out int armor)).IsTrue();
        await Assert.That(armor).IsEqualTo(100);
        await Assert.That(reader.TryReadVector3(1, out Vector3 origin)).IsTrue();
        await Assert.That(origin).IsEqualTo(new Vector3(1, 2, 3));
    }

    // ── Typed reads ───────────────────────────────────────────────────────────

    /// <summary>Reads a 64-bit field without truncating it — DVN stores wide ints boxed as ulong on the object lane.</summary>
    [Test]
    public async Task UInt64_ReadsWideValuesIntact()
    {
        const ulong steamId = 76561198000000000UL;
        EntityState state = SdkTestStates.NewPawn();
        SdkTestStates.Write(state, "m_steamID", steamId);
        LensBoundReader reader = Reader(state);

        await Assert.That(reader.TryReadUInt64(5, out ulong actual)).IsTrue();
        await Assert.That(actual).IsEqualTo(steamId);
    }

    /// <summary>Accepts the engine's integer encoding of a boolean — DVN stores bool wires as int 0/1 on the int lane.</summary>
    [Test]
    [Arguments(1, true)]
    [Arguments(0, false)]
    public async Task Bool_AcceptsTheWiresIntegerEncoding(int wire, bool expected)
    {
        EntityState state = SdkTestStates.NewPawn();
        SdkTestStates.Write(state, "m_bSpotted", wire);
        LensBoundReader reader = Reader(state);

        await Assert.That(reader.TryReadBool(6, out bool actual)).IsTrue();
        await Assert.That(actual).IsEqualTo(expected);
    }

    /// <summary>
    ///     Reads a handle as its raw packed value, undecoded — no mask, no sentinel
    ///     interpretation. DVN's CHandle wires decode via the uint64 raw path, so the boxed
    ///     value is ulong and the adapter width-folds without touching the bits.
    /// </summary>
    [Test]
    public async Task EntityHandle_CrossesUndecoded()
    {
        const uint packed = 0x0004_2A1Fu;
        EntityState state = SdkTestStates.NewPawn();
        SdkTestStates.Write(state, "m_hOwnerEntity", (ulong)packed); // as the decoder boxes it
        LensBoundReader reader = Reader(state);

        await Assert.That(reader.TryReadEntityHandle(4, out uint raw)).IsTrue();
        await Assert.That(raw).IsEqualTo(packed);
    }

    /// <summary>
    ///     The 0xFFFFFFFF "invalid" sentinel also crosses undecoded, whatever width it was
    ///     boxed at — interpreting it is the world's job, not the reader's.
    /// </summary>
    [Test]
    public async Task EntityHandle_InvalidSentinelCrossesUndecoded()
    {
        EntityState state = SdkTestStates.NewPawn();
        SdkTestStates.Write(state, "m_hOwnerEntity", 0xFFFFFFFFUL);
        LensBoundReader reader = Reader(state);

        await Assert.That(reader.TryReadEntityHandle(4, out uint raw)).IsTrue();
        await Assert.That(raw).IsEqualTo(0xFFFFFFFFu);
    }

    /// <summary>QAngle round-trips in the engine's component order — DVN stores angles as Vector3(pitch, yaw, roll).</summary>
    [Test]
    public async Task QAngle_RoundTripsPitchYawRoll()
    {
        EntityState state = SdkTestStates.NewPawn();
        SdkTestStates.Write(state, "m_angEyeAngles", new Vector3(10f, 20f, 30f));
        LensBoundReader reader = Reader(state);

        await Assert.That(reader.TryReadQAngle(2, out QAngle a)).IsTrue();
        await Assert.That(a.Pitch).IsEqualTo(10f);
        await Assert.That(a.Yaw).IsEqualTo(20f);
        await Assert.That(a.Roll).IsEqualTo(30f);
    }

    /// <summary>A value of the wrong shape reads as absent rather than being coerced into nonsense.</summary>
    [Test]
    public async Task WrongShape_ReadsAsAbsent()
    {
        EntityState state = SdkTestStates.NewPawn();
        SdkTestStates.Write(state, "m_angEyeAngles", "not an angle");
        LensBoundReader reader = Reader(state);

        await Assert.That(reader.TryReadQAngle(2, out _)).IsFalse();
        await Assert.That(reader.TryReadVector3(2, out _)).IsFalse();
    }

    // ── The engine-path escape hatch ──────────────────────────────────────────

    /// <summary>Reads a field by its canonical wire path, bypassing the ordinal space.</summary>
    [Test]
    public async Task ByEnginePath_ReadsTheCanonicalSpelling()
    {
        EntityState state = SdkTestStates.NewPawn();
        SdkTestStates.Write(state, Origin, new Vector3(4, 5, 6));
        LensBoundReader reader = Reader(state);

        await Assert.That(reader.TryReadByEnginePath(Origin, out object? v)).IsTrue();
        await Assert.That(v).IsEqualTo(new Vector3(4, 5, 6));
    }

    /// <summary>Resolves a historical spelling through the binding's alias table (current-demo storage, old-name query).</summary>
    [Test]
    public async Task ByEnginePath_ResolvesAHistoricalSpelling()
    {
        EntityState state = SdkTestStates.NewPawn();
        SdkTestStates.Write(state, Origin, new Vector3(7, 8, 9));
        LensBoundReader reader = Reader(state);

        await Assert.That(reader.TryReadByEnginePath("m_vecOrigin", out object? v)).IsTrue();
        await Assert.That(v).IsEqualTo(new Vector3(7, 8, 9));
    }

    /// <summary>
    ///     The reverse direction, which DVN needs and the reference reader does not: storage
    ///     keyed by the OLD wire spelling (a pre-rename demo), queried by the canonical
    ///     spelling. DVN's storage is wire-keyed, so the candidate walk bridges both ways.
    /// </summary>
    [Test]
    public async Task ByEnginePath_ResolvesCanonicalSpellingOverOldDemoStorage()
    {
        EntityState state = SdkTestStates.NewPawn(SdkTestStates.OldDemoPawnShape());
        SdkTestStates.Write(state, "m_vecOrigin", new Vector3(7, 8, 9));
        LensBoundReader reader = Reader(state);

        await Assert.That(reader.TryReadByEnginePath(Origin, out object? v)).IsTrue();
        await Assert.That(v).IsEqualTo(new Vector3(7, 8, 9));
    }

    /// <summary>Ordinal reads resolve over old-demo storage too — the alias candidates serve the ordinal space, not just the escape hatch.</summary>
    [Test]
    public async Task Ordinals_ResolveOverOldDemoStorage()
    {
        EntityState state = SdkTestStates.NewPawn(SdkTestStates.OldDemoPawnShape());
        SdkTestStates.Write(state, "m_vecOrigin", new Vector3(1, 2, 3));
        LensBoundReader reader = Reader(state);

        await Assert.That(reader.TryReadVector3(1, out Vector3 origin)).IsTrue();
        await Assert.That(origin).IsEqualTo(new Vector3(1, 2, 3));
    }

    /// <summary>An unknown path reads as absent.</summary>
    [Test]
    public async Task ByEnginePath_UnknownPathIsAbsent()
    {
        LensBoundReader reader = Reader(SdkTestStates.NewPawn());

        await Assert.That(reader.TryReadByEnginePath("m_nowhere", out _)).IsFalse();
    }

    /// <summary>The escape hatch reaches fields the binding never curated — the contract's stated purpose for it.</summary>
    [Test]
    public async Task ByEnginePath_ReachesUncuratedFields()
    {
        EntityState state = SdkTestStates.NewPawn();
        state.SetFallback("m_pSomeService.m_nUncurated", 42);
        LensBoundReader reader = Reader(state);

        await Assert.That(reader.TryReadByEnginePath("m_pSomeService.m_nUncurated", out object? v)).IsTrue();
        await Assert.That(v).IsEqualTo(42);
    }

    // ── Identity ──────────────────────────────────────────────────────────────

    /// <summary>
    ///     Reports the state's own class. The reference reader's override parameter models a
    ///     subclass read through a base binding; in DVN that case is inherent — the state
    ///     carries its real class name regardless of which binding addresses it.
    /// </summary>
    [Test]
    public async Task EngineClassName_ReportsTheStatesOwnClass()
    {
        await Assert.That(Reader(SdkTestStates.NewPawn()).EngineClassName).IsEqualTo("CCSPlayerPawn");

        EntityState subclass = new("CCSPlayerPawnBase", serial: 1);
        subclass.BindShape(SdkTestStates.PawnShape());
        await Assert.That(Reader(subclass).EngineClassName).IsEqualTo("CCSPlayerPawnBase");
    }

    // ── DVN-specific coverage ─────────────────────────────────────────────────

    /// <summary>
    ///     All-fallback mode (no shape bound):
    ///     every contract semantic holds with storage routed entirely through the fallback
    ///     dictionary.
    /// </summary>
    [Test]
    public async Task ShapelessState_HonorsTheContractThroughTheFallbackDict()
    {
        EntityState state = SdkTestStates.NewShapelessPawn();
        state.SetFallback("m_lifeState", 0);
        state.SetFallback("m_steamID", 76561198000000000UL);
        LensBoundReader reader = Reader(state);

        await Assert.That(reader.TryReadInt32(3, out int life)).IsTrue(); // received zero is present
        await Assert.That(life).IsEqualTo(0);
        await Assert.That(reader.TryReadUInt64(5, out ulong steamId)).IsTrue();
        await Assert.That(steamId).IsEqualTo(76561198000000000UL);
        await Assert.That(reader.TryReadInt32(0, out _)).IsFalse(); // never received is absent
    }

    /// <summary>
    ///     DOCUMENTED DIVERGENCE from the reference reader (findings-report item): DVN stores
    ///     angles and positions in one shape (boxed Vector3), so the storage cannot refuse a
    ///     cross-shape read the way the reference's CLR-type discrimination does —
    ///     TryReadVector3 on an angle field yields the raw component triple, and TryReadQAngle
    ///     on a position reinterprets it. The emitted wrapper choosing the right accessor per
    ///     field is what carries the type discrimination under DVN.
    /// </summary>
    [Test]
    public async Task AngleAndVectorShareOneStorageShape_SoCrossReadsSucceed()
    {
        EntityState state = SdkTestStates.NewPawn();
        SdkTestStates.Write(state, "m_angEyeAngles", new Vector3(10f, 20f, 30f));
        SdkTestStates.Write(state, Origin, new Vector3(1, 2, 3));
        LensBoundReader reader = Reader(state);

        await Assert.That(reader.TryReadVector3(2, out Vector3 asVector)).IsTrue();
        await Assert.That(asVector).IsEqualTo(new Vector3(10f, 20f, 30f));
        await Assert.That(reader.TryReadQAngle(1, out QAngle asAngle)).IsTrue();
        await Assert.That(asAngle).IsEqualTo(new QAngle(1, 2, 3));
    }
}
