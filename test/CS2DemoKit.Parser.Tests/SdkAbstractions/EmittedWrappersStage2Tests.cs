#region

using System.Numerics;
using System.Reflection;
using CS2OpenDev.Sdk.Entities;
using CS2DemoKit.Parser.Entities.SdkAbstractions;
using CS2DemoKit.Parser.EntityTracking;

#endregion

namespace CS2DemoKit.Parser.SdkAbstractions.Tests;

/// <summary>
///     SDK#25 (emitted-wrapper verification) stage 2: the UPSTREAM PACKAGE's emitted wrappers
///     (<c>CS2OpenDev.Sdk.Entities</c> 0.3.0 — the SDK#30 prefix-layout emit: wrapper
///     inheritance along the schema's real parent chains, base ordinal spaces carried verbatim
///     as binding prefixes) composed over DVN's real runtime pieces — a real
///     <see cref="EntityTracker" /> with fabricated <see cref="EntityState" /> instances, read
///     through <see cref="LensBoundReader" /> over the package's OWN
///     <see cref="EntityClassBinding" /> manifests (<see cref="EntityWrapperRegistry.Bindings" />),
///     never over <c>LensBindingBuilder</c>'s runtime-built ones.
///     <para>
///         That distinction is the whole point: the package's ordinal space is numbered from
///         the SDK's <c>state.json</c>, DVN's from our local Lens genesis, and the two have
///         already diverged (the SDK relocated the origin canonical to
///         <c>m_CBodyComponent.m_pSceneNode.m_vecOrigin</c> with alias <c>m_vecOrigin</c>;
///         our genesis still spells it <c>m_vecOrigin</c> canonically). The reader's
///         candidate-list resolution — canonical first, then alias spellings — is what bridges
///         their spelling to our wire-keyed storage, and
///         <see cref="OriginAliasBridge_SdkCanonicalReadsOurWireKeyedStorage" /> exercises
///         exactly that seam. No demo parsing anywhere in this file.
///     </para>
/// </summary>
[Category("Unit")]
public class EmittedWrappersStage2Tests
{
    /// <summary>The SDK's relocated origin canonical (our genesis still says <c>m_vecOrigin</c>).</summary>
    private const string SdkOriginCanonical = "m_CBodyComponent.m_pSceneNode.m_vecOrigin";

    // The two classes with a wrapper type but no Create case
    // (abstract bases that never appear as a live entity's class).
    private static readonly string[] ExpectedCreateExclusions = ["CBaseCSGrenade", "CCSWeaponBaseShotgun"];

    // The read-policy census's expected nullable value-typed properties: m_lifeState (carries
    // meaning at 0), the three seen-aware Origins 0.2.0 flipped to Vector3?, and —
    // since 1.1.0 answered our SDK#41 position ask — the six seen-aware cell/vec leaves on
    // each of the same three classes (cell 0 is a LEGAL world cell: a 0-default would
    // fabricate −16384 through the (cell − 32) × 512 arithmetic, so int?/float? is load-
    // bearing, not style). Absence is null, never a coordinate nobody is standing on.
    private static readonly string[] ExpectedNullableProperties = BuildExpectedNullableProperties();

    private static string[] BuildExpectedNullableProperties()
    {
        List<string> expected = ["CSPlayerPawn.LifeState"];
        foreach (string cls in new[] { "BaseCSGrenadeProjectile", "CSPlayerPawn", "PlantedC4" })
        {
            expected.Add($"{cls}.Origin");
            foreach (string axis in new[] { "X", "Y", "Z" })
            {
                expected.Add($"{cls}.OriginCell{axis}");
                expected.Add($"{cls}.OriginVec{axis}");
            }
        }

        return [.. expected.Order(StringComparer.Ordinal)];
    }

    // Slots chosen so the tracker's 14-bit index mask is genuinely exercised: the weapon
    // handle carries serial 1 in its high bits (0x0002_1234 = (1 << 17) | 0x1234) and the
    // tracker must mask it off to find slot 0x1234 (< 0x4000, inside the mask).
    private const int WeaponSlot = 0x1234;
    private const uint WeaponHandle = 0x0002_1234u;
    private const int PawnSlot = 50;

    // ── Package surface helpers ───────────────────────────────────────────────

    private static EntityClassBinding SdkBinding(string engineClass)
        => EntityWrapperRegistry.Bindings.Single(b
            => string.Equals(b.EngineClass, engineClass, StringComparison.Ordinal));

    private static Type[] WrapperTypes()
        => typeof(EntityWrapperRegistry).Assembly
            .GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && t.IsSubclassOf(typeof(EntityWrapper)))
            .ToArray();

    private static PropertyInfo[] DeclaredProperties(Type wrapper)
        => wrapper.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);

    // ── Fabricated runtime: OUR wire-keyed storage under THEIR bindings ───────

    /// <summary>
    ///     The pawn shape as OUR Lens genesis would key it — origin under the <c>m_vecOrigin</c>
    ///     spelling (which is the SDK binding's ALIAS, not its canonical), everything else on
    ///     the lane DVN's decoder would really choose (ints on the int lane; handles and wide
    ///     ints boxed on the object lane per the honour-the-wire rule).
    /// </summary>
    private static ClassShape GenesisSpelledPawnShape()
    {
        ClassShapeBuilder b = new("CCSPlayerPawn");
        b.Allocate(LaneKind.Int, "m_iHealth");
        b.Allocate(LaneKind.Int, "m_lifeState");
        b.Allocate(LaneKind.Object, "m_vecOrigin"); // OUR canonical == the SDK's alias
        b.Allocate(LaneKind.Object, "m_pWeaponServices.m_hActiveWeapon"); // CHandle → boxed ulong
        b.Allocate(LaneKind.Object, "m_pMovementServices.m_nButtons"); // CInButtonState → boxed ulong
        b.Allocate(LaneKind.Object, "m_hOwnerEntity"); // CHandle → boxed ulong
        return b.Build();
    }

    private static ClassShape WeaponShape()
    {
        ClassShapeBuilder b = new("CBasePlayerWeapon");
        b.Allocate(LaneKind.Int, "m_iClip1");
        return b.Build();
    }

    /// <summary>
    ///     A real tracker whose factory dispatch constructs the PACKAGE's wrappers via
    ///     <see cref="EntityWrapperRegistry.Create" /> over readers bound with the PACKAGE's
    ///     manifests. This is the exact composition a consuming runtime would run.
    /// </summary>
    private static (EntityTracker Tracker, TrackerEntityWorld World, EntityState Pawn) NewRuntime()
    {
        EntityTracker tracker = new();
        TrackerEntityWorld world = new(tracker);
        world.RegisterWrapper(SdkBinding("CCSPlayerPawn"),
            (r, w) => EntityWrapperRegistry.Create("CCSPlayerPawn", r, w)!);
        world.RegisterWrapper(SdkBinding("CBasePlayerWeapon"),
            (r, w) => EntityWrapperRegistry.Create("CBasePlayerWeapon", r, w)!);

        EntityState pawn = tracker.CurrentEntities.GetOrCreate(PawnSlot, "CCSPlayerPawn", serial: 1);
        pawn.BindShape(GenesisSpelledPawnShape());
        return (tracker, world, pawn);
    }

    private static EntityState AddWeapon(EntityTracker tracker)
    {
        EntityState weapon = tracker.CurrentEntities.GetOrCreate(WeaponSlot, "CBasePlayerWeapon", serial: 1);
        weapon.BindShape(WeaponShape());
        return weapon;
    }

    // ── 2.1 Structural assertions on the package ──────────────────────────────

    /// <summary>
    ///     Wrapper census on the 0.3.0 inheritance emit: 59 concrete EntityWrapper subclasses
    ///     (CIncendiaryGrenade curated — our SDK#34 footnote), 13 with DECLARED properties, 46
    ///     markers that declare nothing but now INHERIT their base's surface. The 0.1.1/0.2.0
    ///     pin "property-carrying == non-empty bindings" died with the flat emission: under
    ///     prefix layout a marker's binding IS its base's layout, so all 59 are non-empty
    ///     (674 paths total).
    /// </summary>
    [Test]
    public async Task Package_Census_SixtyOneWrappers_FifteenDeclaring_AllBindingsNonEmpty()
    {
        Type[] wrappers = WrapperTypes();
        List<Type> withProperties = wrappers.Where(t => DeclaredProperties(t).Length > 0).ToList();

        await Assert.That(wrappers.Length).IsEqualTo(61);
        await Assert.That(withProperties.Count).IsEqualTo(15);
        await Assert.That(wrappers.Length - withProperties.Count).IsEqualTo(46);

        // Wrapper types and binding manifests are two views of one emitted set: they must
        // agree name-for-name, and under prefix layout every binding has ordinals.
        HashSet<string> typeNames = wrappers.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        HashSet<string> netNames = EntityWrapperRegistry.Bindings.Select(b => b.NetName)
            .ToHashSet(StringComparer.Ordinal);
        await Assert.That(typeNames.SetEquals(netNames)).IsTrue();

        await Assert.That(EntityWrapperRegistry.Bindings.Count(b => b.CanonicalPaths.Count == 0))
            .IsEqualTo(0);
        await Assert.That(EntityWrapperRegistry.Bindings.Sum(b => b.CanonicalPaths.Count))
            .IsEqualTo(735);
    }

    /// <summary>
    ///     THE structural law of the 0.3.0 emit, measured over the shipped manifests rather
    ///     than trusted from the design doc: for every wrapper whose C# base is another
    ///     wrapper, the base's binding is a VERBATIM PREFIX of the derived binding (paths,
    ///     aliases and handle ordinals all carried), so every base ordinal constant is valid
    ///     in every descendant binding by construction. Markers add nothing: their binding
    ///     equals their base's exactly. The shotgun negative case is pinned by name —
    ///     WeaponNOVA/XM1014/Sawedoff derive CSWeaponBaseShotgun and carry 8 paths, NOT the
    ///     gun's 11: the emitter followed the schema's real parent chain, not the tidy shape.
    /// </summary>
    [Test]
    public async Task Package_PrefixLayout_BaseBindingIsVerbatimPrefix_ShotgunsAreNotGuns()
    {
        Dictionary<string, EntityClassBinding> byNetName = EntityWrapperRegistry.Bindings
            .ToDictionary(b => b.NetName, StringComparer.Ordinal);
        int derivedCount = 0, markerCount = 0;

        foreach (Type wrapper in WrapperTypes())
        {
            if (wrapper.BaseType == typeof(EntityWrapper))
            {
                continue;
            }

            derivedCount++;
            EntityClassBinding derived = byNetName[wrapper.Name];
            EntityClassBinding baseBinding = byNetName[wrapper.BaseType!.Name];

            // Base paths verbatim at the front, in order.
            await Assert.That(derived.CanonicalPaths.Count >= baseBinding.CanonicalPaths.Count).IsTrue();
            for (int i = 0; i < baseBinding.CanonicalPaths.Count; i++)
            {
                await Assert.That(derived.CanonicalPaths[i]).IsEqualTo(baseBinding.CanonicalPaths[i]);
            }

            // Inherited aliases and handle ordinals survive into the descendant.
            foreach (KeyValuePair<string, string> alias in baseBinding.Aliases)
            {
                await Assert.That(derived.Aliases.TryGetValue(alias.Key, out string? target)).IsTrue();
                await Assert.That(target).IsEqualTo(alias.Value);
            }

            foreach (int handleOrdinal in baseBinding.HandleOrdinals)
            {
                await Assert.That(derived.HandleOrdinals).Contains(handleOrdinal);
            }

            if (DeclaredProperties(wrapper).Length == 0
                && wrapper.BaseType != typeof(EntityWrapper)
                && derived.CanonicalPaths.Count == baseBinding.CanonicalPaths.Count)
            {
                markerCount++;
            }
        }

        // 52 of 59 have a curated ancestor (the design doc's 51-of-58, plus the incendiary).
        await Assert.That(derivedCount).IsEqualTo(52);

        // The shotgun negative case, by name: 8 base paths, no gun ordinals.
        foreach (string shotgun in new[] { "WeaponNOVA", "WeaponXM1014", "WeaponSawedoff" })
        {
            Type type = WrapperTypes().Single(t => t.Name == shotgun);
            await Assert.That(type.BaseType!.Name).IsEqualTo("CSWeaponBaseShotgun");
            await Assert.That(byNetName[shotgun].CanonicalPaths.Count).IsEqualTo(8);
            await Assert.That(byNetName[shotgun].CanonicalPaths).DoesNotContain("m_zoomLevel");
        }

        // The AK47 exemplar from the design doc: the gun's 11 paths verbatim, m_iClip1 at the
        // base's ordinal 2 (BasePlayerWeapon.Ord.Clip1), declaring nothing of its own.
        EntityClassBinding ak = byNetName["AK47"];
        await Assert.That(ak.CanonicalPaths.Count).IsEqualTo(11);
        await Assert.That(ak.CanonicalPaths[2]).IsEqualTo("m_iClip1");
        await Assert.That(ak.CanonicalPaths.SequenceEqual(byNetName["CSWeaponBaseGun"].CanonicalPaths))
            .IsTrue();

        // The MolotovGrenade promotion — the 0.3.0 surprise upstream flagged: incendiary's
        // schema parent is the molotov, not CBaseCSGrenade.
        await Assert.That(WrapperTypes().Single(t => t.Name == "IncendiaryGrenade").BaseType!.Name)
            .IsEqualTo("MolotovGrenade");
    }

    /// <summary>
    ///     Upstream ran BindingConformance in their CI (stage 1); we run it again over the
    ///     shipped manifests so OUR report stands on its own evidence.
    /// </summary>
    [Test]
    public async Task Package_EmittedBindings_PassConformanceHere()
    {
        await Assert.That(EntityWrapperRegistry.Bindings.Count).IsEqualTo(61);

        BindingConformance.ThrowIfInvalid(EntityWrapperRegistry.Bindings);

        foreach (EntityClassBinding binding in EntityWrapperRegistry.Bindings)
        {
            await Assert.That(BindingConformance.Validate(binding).ToArray()).IsEmpty();
        }
    }

    /// <summary>
    ///     Registry factory contract, measured rather than trusted: probing Create for every
    ///     binding must yield exactly the two claimed exclusions and a correctly-typed wrapper
    ///     (type name == NetName, EngineClassName flowing from the reader) for the other 56.
    /// </summary>
    [Test]
    public async Task Registry_Create_CoversFiftyNine_ExcludesTheTwoAbstractBases()
    {
        EntityTracker tracker = new();
        TrackerEntityWorld world = new(tracker);

        List<string> excluded = new();
        int created = 0;
        foreach (EntityClassBinding binding in EntityWrapperRegistry.Bindings)
        {
            EntityState state = tracker.CurrentEntities.GetOrCreate(created + excluded.Count + 1,
                binding.EngineClass, serial: 1);
            LensBoundReader reader = world.CreateReader(binding, state);
            EntityWrapper? wrapper = EntityWrapperRegistry.Create(binding.EngineClass, reader, world);

            if (wrapper is null)
            {
                excluded.Add(binding.EngineClass);
                continue;
            }

            created++;
            await Assert.That(wrapper.GetType().Name).IsEqualTo(binding.NetName);
            await Assert.That(wrapper.EngineClassName).IsEqualTo(binding.EngineClass);
        }

        await Assert.That(created).IsEqualTo(59);
        await Assert.That(excluded.Order(StringComparer.Ordinal).ToArray())
            .IsEquivalentTo(ExpectedCreateExclusions);

        // An engine class the package never wrapped is a null, not a throw.
        LensBoundReader probe = world.CreateReader(SdkBinding("CCSPlayerPawn"),
            tracker.CurrentEntities.GetOrCreate(PawnSlot, "CCSPlayerPawn", serial: 1));
        await Assert.That(EntityWrapperRegistry.Create("CNotAClass", probe, world)).IsNull();
    }

    /// <summary>
    ///     The read-policy census, re-measured on 0.3.0: the nullable value-typed set is exactly
    ///     <c>m_lifeState</c> plus the three seen-aware <c>Origin</c>s, unchanged by the
    ///     inheritance emit because the census is DeclaredOnly and inherited properties declare
    ///     nowhere new; every other schema property is 0-default. Also pins <c>Buttons</c> as
    ///     <see cref="ulong" /> (the width derived through CInButtonState), the handle properties
    ///     as raw <see cref="uint" />, and the companion types: <c>ActiveWeapon</c>/
    ///     <c>LastWeapon</c> are <see cref="BasePlayerWeapon" />? again now that the hierarchy
    ///     makes the typed fold succeed, while <c>CSPlayerController.PlayerPawn</c> — the
    ///     property an earlier emit silently dropped via the <c>C_</c>-spelling matcher miss —
    ///     stays <see cref="CSPlayerPawn" />?.
    /// </summary>
    [Test]
    public async Task Package_ReadPolicies_NullableSetIsLifeStatePlusThreeOrigins_ButtonsIsUlong()
    {
        List<string> nullableValueProps = new();
        foreach (Type wrapper in WrapperTypes())
        {
            foreach (PropertyInfo prop in DeclaredProperties(wrapper))
            {
                if (Nullable.GetUnderlyingType(prop.PropertyType) is not null)
                {
                    nullableValueProps.Add($"{wrapper.Name}.{prop.Name}");
                }
            }
        }

        await Assert.That(nullableValueProps.Order(StringComparer.Ordinal).ToArray())
            .IsEquivalentTo(ExpectedNullableProperties);
        await Assert.That(typeof(CSPlayerPawn).GetProperty("LifeState")!.PropertyType)
            .IsEqualTo(typeof(int?));
        await Assert.That(typeof(CSPlayerPawn).GetProperty("Origin")!.PropertyType)
            .IsEqualTo(typeof(Vector3?));
        await Assert.That(typeof(CSPlayerPawn).GetProperty("Buttons")!.PropertyType)
            .IsEqualTo(typeof(ulong));
        await Assert.That(typeof(CSPlayerPawn).GetProperty("ControllerHandle")!.PropertyType)
            .IsEqualTo(typeof(uint));

        // The companion types, pinned by identity: with 0.3.0's inheritance the weapon
        // companions are BasePlayerWeapon? AGAIN (the #29 reversal —
        // a resolved SmokeGrenade now IS a BasePlayerWeapon), PlayerPawn is
        // the typed CSPlayerPawn? (its handle only ever targets exactly CCSPlayerPawn), and
        // m_hOwnerEntity (CBaseEntity is not curated) stays raw-only.
        await Assert.That(typeof(CSPlayerPawn).GetProperty("ActiveWeapon")!.PropertyType)
            .IsEqualTo(typeof(BasePlayerWeapon));
        await Assert.That(typeof(CSPlayerPawn).GetProperty("LastWeapon")!.PropertyType)
            .IsEqualTo(typeof(BasePlayerWeapon));
        await Assert.That(typeof(CSPlayerController).GetProperty("PlayerPawn")!.PropertyType)
            .IsEqualTo(typeof(CSPlayerPawn));
        await Assert.That(typeof(CSPlayerPawn).GetProperty("OwnerEntityHandle")).IsNotNull();
        await Assert.That(typeof(CSPlayerPawn).GetProperty("OwnerEntity")).IsNull();
    }

    // ── 2.2 Composition semantics over a real tracker ─────────────────────────

    /// <summary>Seen-aware policy: an unsent m_lifeState is null; a received 0 is 0 (LIFE_ALIVE).</summary>
    [Test]
    public async Task LifeState_AbsentIsNull_ReceivedZeroIsZero()
    {
        (EntityTracker tracker, _, EntityState state) = NewRuntime();

        CSPlayerPawn? pawn = tracker.Get<CSPlayerPawn>(PawnSlot);
        await Assert.That(pawn).IsNotNull();
        await Assert.That(pawn!.LifeState).IsNull();

        SdkTestStates.Write(state, "m_lifeState", 0);
        await Assert.That(pawn.LifeState).IsEqualTo(0);
    }

    /// <summary>0-default policy: an unsent m_iHealth reads 0; a received value reads through.</summary>
    [Test]
    public async Task Health_AbsentIsZero_ReceivedValueReadsThrough()
    {
        (EntityTracker tracker, _, EntityState state) = NewRuntime();

        CSPlayerPawn? pawn = tracker.Get<CSPlayerPawn>(PawnSlot);
        await Assert.That(pawn!.Health).IsEqualTo(0);

        SdkTestStates.Write(state, "m_iHealth", 87);
        await Assert.That(pawn.Health).IsEqualTo(87);
    }

    /// <summary>
    ///     The wide read: CInButtonState decodes onto our object lane as a boxed ulong, and the
    ///     wrapper's ulong property must carry bits above the 32-bit line intact.
    /// </summary>
    [Test]
    public async Task Buttons_UlongWideRead_CarriesBitsAboveThirtyTwo()
    {
        (EntityTracker tracker, _, EntityState state) = NewRuntime();
        const ulong buttons = 0x0000_0001_8000_0001ul; // needs all 33 low bits

        SdkTestStates.Write(state, "m_pMovementServices.m_nButtons", buttons);

        CSPlayerPawn? pawn = tracker.Get<CSPlayerPawn>(PawnSlot);
        await Assert.That(pawn!.Buttons).IsEqualTo(buttons);
    }

    /// <summary>
    ///     Handle seam: the raw property returns the packed value bit-for-bit (serial included,
    ///     no mask, no decode), while the companion resolves through TrackerEntityWorld — the
    ///     tracker's own sentinel checks and 14-bit index mask — to the right wrapper instance.
    /// </summary>
    [Test]
    public async Task Handles_RawCrossesUndecoded_CompanionResolvesThroughTheTracker()
    {
        (EntityTracker tracker, _, EntityState pawnState) = NewRuntime();
        EntityState weaponState = AddWeapon(tracker);
        SdkTestStates.Write(weaponState, "m_iClip1", 30);
        SdkTestStates.Write(pawnState, "m_pWeaponServices.m_hActiveWeapon", (ulong)WeaponHandle);

        CSPlayerPawn? pawn = tracker.Get<CSPlayerPawn>(PawnSlot);

        await Assert.That(pawn!.ActiveWeaponHandle).IsEqualTo(WeaponHandle);

        // 0.3.0 types the companion BasePlayerWeapon? again — with the hierarchy emitted, the
        // typed fold succeeds and the 0.2.0-era cast is gone. The typed surface reads directly.
        BasePlayerWeapon? weapon = pawn.ActiveWeapon;
        await Assert.That(weapon).IsNotNull();
        await Assert.That(weapon!.Clip1).IsEqualTo(30);
        await Assert.That(weapon.EngineClassName).IsEqualTo("CBasePlayerWeapon");
    }

    /// <summary>The 0xFFFFFFFF invalid sentinel crosses raw exactly; the companion resolves null.</summary>
    [Test]
    public async Task Handles_InvalidSentinel_RawExact_CompanionNull()
    {
        (EntityTracker tracker, _, EntityState pawnState) = NewRuntime();
        SdkTestStates.Write(pawnState, "m_pWeaponServices.m_hActiveWeapon", 0xFFFF_FFFFul);

        CSPlayerPawn? pawn = tracker.Get<CSPlayerPawn>(PawnSlot);

        await Assert.That(pawn!.ActiveWeaponHandle).IsEqualTo(0xFFFF_FFFFu);
        await Assert.That(pawn.ActiveWeapon).IsNull();

        // Absent handle: 0-default raw, null companion (0 is the "uninitialised" sentinel).
        await Assert.That(pawn.LastWeaponHandle).IsEqualTo(0u);
        await Assert.That(pawn.LastWeapon).IsNull();
    }

    // ── 2.3 The origin divergence, exercised deliberately ─────────────────────

    /// <summary>
    ///     THE load-bearing test of this battery. The SDK's emitted pawn binding spells the
    ///     origin canonical <c>m_CBodyComponent.m_pSceneNode.m_vecOrigin</c>; the WIRE still
    ///     spells it <c>m_vecOrigin</c>. Since the lens became DERIVED from the SDK package
    ///     (2026-08-15) the two rule spaces share the canonical — the divergence moved from
    ///     rule-key-vs-rule-key to wire-vs-canonical, and the alias table remains the only
    ///     thing bridging a wire write to a canonical wrapper read.
    /// </summary>
    [Test]
    public async Task OriginAliasBridge_SdkCanonicalReadsOurWireKeyedStorage()
    {
        // Pin the spelling split so this test cannot silently pass by identity: the
        // package's canonical is the relocated path, m_vecOrigin is only an alias …
        EntityClassBinding sdkPawn = SdkBinding("CCSPlayerPawn");
        await Assert.That(sdkPawn.CanonicalPaths).Contains(SdkOriginCanonical);
        await Assert.That(sdkPawn.CanonicalPaths).DoesNotContain("m_vecOrigin");
        await Assert.That(sdkPawn.Aliases["m_vecOrigin"]).IsEqualTo(SdkOriginCanonical);

        // … and the DERIVED lens mirrors the package: rule keyed by the SDK canonical,
        // the wire spelling resolving to it through the AliasMap (never a rule key itself).
        Entities.SchemaLens.LensState ourLens = Entities.Generated.GeneratedLensRegistry.Load();
        await Assert.That(ourLens.Fields["CCSPlayerPawn"].ContainsKey(SdkOriginCanonical)).IsTrue();
        await Assert.That(ourLens.Fields["CCSPlayerPawn"].ContainsKey("m_vecOrigin")).IsFalse();
        await Assert.That(ourLens.AliasMap["CCSPlayerPawn"].GetValueOrDefault("m_vecOrigin"))
            .IsEqualTo(SdkOriginCanonical);

        // Same shape on the other two relocated classes the SDK state moved.
        foreach (string relocated in new[] { "CBaseCSGrenadeProjectile", "CPlantedC4" })
        {
            EntityClassBinding b = SdkBinding(relocated);
            await Assert.That(b.Aliases["m_vecOrigin"]).IsEqualTo(SdkOriginCanonical);
            await Assert.That(ourLens.Fields[relocated].ContainsKey(SdkOriginCanonical)).IsTrue();
            await Assert.That(ourLens.AliasMap[relocated].GetValueOrDefault("m_vecOrigin"))
                .IsEqualTo(SdkOriginCanonical);
        }

        // Now the bridge itself: write under our wire spelling, read their canonical ordinal.
        // Before the write, the seen-aware Vector3? (0.2.0) must read null — absence is
        // null, never the (0,0,0) nobody is standing on.
        (EntityTracker tracker, _, EntityState state) = NewRuntime();
        CSPlayerPawn? pawn = tracker.Get<CSPlayerPawn>(PawnSlot);
        await Assert.That(pawn!.Origin).IsNull();

        Vector3 origin = new(123.5f, -456.25f, 78f);
        SdkTestStates.Write(state, "m_vecOrigin", origin);
        await Assert.That(pawn.Origin).IsEqualTo(origin);

        // The by-path escape hatch resolves BOTH spellings against our storage.
        await Assert.That(pawn["m_vecOrigin"]).IsEqualTo(origin);
        await Assert.That(pawn[SdkOriginCanonical]).IsEqualTo(origin);
    }

    // ── 2.4 The 0.3.0 inheritance seams, fabricated ───────────────────────────

    /// <summary>
    ///     The two reads SDK#30 exists to make correct, run over OUR runtime rather than the
    ///     reference reader: (a) an inherited read through a MARKER's own binding — AK47
    ///     declares nothing, so <c>Clip1</c> executes BasePlayerWeapon's body with the BASE's
    ///     compile-time ordinal (2) against AK47's prefix-layout binding; (b) the
    ///     inherited-alias bridge — SmokeGrenadeProjectile declares neither <c>Origin</c> nor
    ///     the <c>m_vecOrigin</c> alias (both arrive from BaseCSGrenadeProjectile through the
    ///     binding prefix), and the alias still bridges the SDK's relocated canonical to our
    ///     wire-keyed storage, seen-aware null included.
    /// </summary>
    [Test]
    public async Task Inheritance_MarkerBindingReadsBaseOrdinals_InheritedAliasBridges()
    {
        EntityTracker tracker = new();
        TrackerEntityWorld world = new(tracker);
        world.RegisterWrapper(SdkBinding("CAK47"),
            (r, w) => EntityWrapperRegistry.Create("CAK47", r, w)!);
        world.RegisterWrapper(SdkBinding("CSmokeGrenadeProjectile"),
            (r, w) => EntityWrapperRegistry.Create("CSmokeGrenadeProjectile", r, w)!);

        // (a) Marker binding, base ordinal. AK47's binding is CSWeaponBaseGun's 11 paths
        // verbatim; m_iClip1 sits at the base's ordinal 2 and nothing about AK47 declares it.
        ClassShapeBuilder ak = new("CAK47");
        ak.Allocate(LaneKind.Int, "m_iClip1");
        EntityState akState = tracker.CurrentEntities.GetOrCreate(70, "CAK47", serial: 1);
        akState.BindShape(ak.Build());
        SdkTestStates.Write(akState, "m_iClip1", 30);

        AK47? rifle = tracker.Get<AK47>(70);
        await Assert.That(rifle).IsNotNull();
        await Assert.That(rifle!.Clip1).IsEqualTo(30);

        // The hierarchy is real C# inheritance: the same read through a base-typed reference
        // (exactly what the restored BasePlayerWeapon? companions hand out).
        BasePlayerWeapon baseTyped = rifle;
        await Assert.That(baseTyped.Clip1).IsEqualTo(30);
        await Assert.That(typeof(AK47).GetProperty("Clip1",
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)).IsNull();

        // (b) Inherited alias. The smoke projectile's binding carries the m_vecOrigin →
        // relocated-canonical alias it INHERITED; our storage keys the wire spelling.
        await Assert.That(SdkBinding("CSmokeGrenadeProjectile").Aliases["m_vecOrigin"])
            .IsEqualTo(SdkOriginCanonical);
        await Assert.That(typeof(SmokeGrenadeProjectile).GetProperty("Origin",
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)).IsNull();

        ClassShapeBuilder smoke = new("CSmokeGrenadeProjectile");
        smoke.Allocate(LaneKind.Object, "m_vecOrigin");
        EntityState smokeState = tracker.CurrentEntities.GetOrCreate(71, "CSmokeGrenadeProjectile", serial: 1);
        smokeState.BindShape(smoke.Build());

        SmokeGrenadeProjectile? projectile = tracker.Get<SmokeGrenadeProjectile>(71);
        await Assert.That(projectile).IsNotNull();
        await Assert.That(projectile!.Origin).IsNull();

        Vector3 origin = new(64f, -128.5f, 32f);
        SdkTestStates.Write(smokeState, "m_vecOrigin", origin);
        await Assert.That(projectile.Origin).IsEqualTo(origin);
    }
}
