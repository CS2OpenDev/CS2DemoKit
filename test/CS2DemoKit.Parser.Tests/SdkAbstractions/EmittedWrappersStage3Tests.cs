#region

using System.Globalization;
using CS2OpenDev.Sdk.Entities;
using CS2DemoKit.Parser.Entities.SchemaLens;
using CS2DemoKit.Parser.Entities.SdkAbstractions;
using CS2DemoKit.Parser.EntityTracking;
using CS2DemoKit.TestSupport;
using TUnit.Core.Exceptions;

#endregion

namespace CS2DemoKit.Parser.SdkAbstractions.Tests;

/// <summary>
///     SDK#25 (emitted-wrapper verification) stage 3: the upstream package's emitted wrappers
///     A/B'd against DVN's own <see cref="EntityState.Fields" /> projection over ONE real demo.
///     A Lens-bound <see cref="EntityTracker" /> replays to three checkpoints (50%, 75%, 90% of
///     the frame list — stepped forward with <see cref="EntityTracker.AdvanceOneFrame" />, so
///     the demo is parsed once and replayed once); at each, for every property-carrying class
///     present, every ordinal of the PACKAGE's emitted <see cref="EntityClassBinding" /> is
///     read through <see cref="LensBoundReader" /> and joined against the string-keyed
///     projection <b>by canonical path through the binding's alias table</b> — never
///     ordinal-to-ordinal, because the SDK's ordinal space is numbered from their
///     <c>state.json</c> and ours from our Lens genesis, and the two diverged when the SDK
///     relocated the origin canonical. Presence must agree and values must agree.
///     Skips gracefully without a demo.
/// </summary>
[Category("Integration")]
[NotInParallel]
public class EmittedWrappersStage3Tests
{
    private static readonly string[] ExpectedCreateExclusions = ["CBaseCSGrenade", "CCSWeaponBaseShotgun"];

    private sealed class AbTotals
    {
        public readonly HashSet<string> ClassesChecked = new(StringComparer.Ordinal);
        public readonly List<string> Log = new();
        public readonly List<string> Mismatches = new();
        public int AliasBridgeReads;
        public int Comparisons;
        public int Entities;
        public int PresentComparisons;
    }

    [Test]
    public async Task EmittedWrapperOrdinals_AgreeWithTheFieldsProjection_OnARealDemo()
    {
        string? demoPath = DemoTestHelper.FindDemoPath();
        if (demoPath is null)
        {
            throw new SkipTestException("No demo found — skipping emitted-wrapper stage 3 A/B.");
        }

        // ── One heavy parse, then ONE Lens-bound replay walked through checkpoints ─
        byte[] bytes = await File.ReadAllBytesAsync(demoPath);
        ParsedDemo parsed = DemoParser.Parse(bytes.AsMemory());

        LensState lens = Entities.Generated.GeneratedLensRegistry.Load();
        EntityTracker tracker = new();
        tracker.BindLensResolver(LensResolverBridge.Build(lens));

        // ── Register the package's own factories for every class Create covers ─
        // (needed so companion properties and World.Resolve produce PACKAGE wrappers).
        // The exclusion set is measured here, not assumed.
        TrackerEntityWorld world = new(tracker);
        List<string> excluded = new();
        foreach (EntityClassBinding binding in EntityWrapperRegistry.Bindings)
        {
            // Probe with a detached state (internal ctor — never enters the entity table).
            EntityState probe = new(binding.EngineClass, serial: 0);
            if (EntityWrapperRegistry.Create(binding.EngineClass, new LensBoundReader(probe, binding), world) is null)
            {
                excluded.Add(binding.EngineClass);
                continue;
            }

            string engineClass = binding.EngineClass;
            world.RegisterWrapper(binding, (r, w) => EntityWrapperRegistry.Create(engineClass, r, w)!);
        }

        await Assert.That(excluded.Order(StringComparer.Ordinal).ToArray())
            .IsEquivalentTo(ExpectedCreateExclusions);

        // Under 0.3.0's prefix layout EVERY binding is non-empty (a marker's binding is its
        // base's layout), so the A/B sweeps all 59 — the first time the emitted bindings meet
        // the wire at 674 paths, which is the measurement that decides 1.0.
        List<EntityClassBinding> propertyCarrying = EntityWrapperRegistry.Bindings
            .Where(b => b.CanonicalPaths.Count > 0)
            .ToList();
        await Assert.That(propertyCarrying.Count).IsEqualTo(61);

        // ── The A/B: one continuous replay walked to three checkpoints ────────
        // Full passes at 50% / 75% / 90% of the frame list; between frames, an opportunistic
        // sweep (every 64 frames, over the live-entity index only) catches transient classes —
        // CPlantedC4 and CFlashbangProjectile exist only while a bomb is planted or a flash is
        // in flight, and never coincide with a fixed checkpoint.
        AbTotals totals = new();
        HashSet<int> checkpoints =
        [
            parsed.Frames.Count / 2,
            parsed.Frames.Count * 3 / 4,
            parsed.Frames.Count * 9 / 10,
        ];
        int lastFrame = parsed.Frames.Count * 9 / 10;

        for (int i = 0; i <= lastFrame && i < parsed.Frames.Count; i++)
        {
            tracker.AdvanceOneFrame(parsed.Frames[i]);

            if (checkpoints.Contains(i))
            {
                await RunAbPass(tracker, world, propertyCarrying, totals, $"frame {i}");
                continue;
            }

            if ((i & 63) != 0)
            {
                continue;
            }

            List<EntityClassBinding> unseenNowLive = UnseenNowLive(tracker, propertyCarrying, totals);
            if (unseenNowLive.Count > 0)
            {
                await RunAbPass(tracker, world, unseenNowLive, totals, $"frame {i} (opportunistic)");
            }
        }

        Console.WriteLine("SDK#25 stage 3 A/B totals: "
            + $"classes={totals.ClassesChecked.Count}/{propertyCarrying.Count} property-carrying seen live, "
            + $"entity checks={totals.Entities}, ordinal comparisons={totals.Comparisons} "
            + $"({totals.PresentComparisons} value comparisons on present fields, "
            + $"{totals.AliasBridgeReads} resolved through the alias bridge), "
            + $"mismatches={totals.Mismatches.Count}");
        foreach (string line in totals.Log)
        {
            Console.WriteLine("  " + line);
        }

        List<string> neverSeen = propertyCarrying
            .Select(b => b.EngineClass)
            .Where(c => !totals.ClassesChecked.Contains(c))
            .ToList();
        Console.WriteLine("  never live at any checkpoint: "
            + (neverSeen.Count == 0 ? "(none)" : string.Join(", ", neverSeen)));

        await Assert.That(string.Join("\n", totals.Mismatches)).IsEqualTo("");
        await Assert.That(totals.Entities).IsGreaterThan(0);
        await Assert.That(totals.PresentComparisons).IsGreaterThan(0);
        await Assert.That(totals.ClassesChecked).Contains("CCSPlayerPawn");
        await Assert.That(totals.ClassesChecked).Contains("CCSPlayerController");

        // ── Typed spot checks on a live pawn (the paths analysis consumers lean on) ─
        EntityState? pawnState = tracker.CurrentEntities.OfClass("CCSPlayerPawn")
            .FirstOrDefault(s => s.Fields.ContainsKey("m_iHealth"));
        if (pawnState is null)
        {
            throw new SkipTestException("A/B passed, but no pawn with m_iHealth at the final checkpoint — typed spot checks skipped.");
        }

        EntityClassBinding pawnBinding = propertyCarrying.Single(b => b.EngineClass == "CCSPlayerPawn");
        CSPlayerPawn pawn = (CSPlayerPawn)EntityWrapperRegistry.Create(
            "CCSPlayerPawn", world.CreateReader(pawnBinding, pawnState), world)!;
        IReadOnlyDictionary<string, object?> pawnFields = pawnState.Fields;

        await Assert.That(pawn.Health)
            .IsEqualTo(Convert.ToInt32(pawnFields["m_iHealth"], CultureInfo.InvariantCulture));

        if (pawnFields.TryGetValue("m_lifeState", out object? lifeState))
        {
            await Assert.That(pawn.LifeState)
                .IsEqualTo(Convert.ToInt32(lifeState, CultureInfo.InvariantCulture));
        }
        else
        {
            await Assert.That(pawn.LifeState).IsNull();
        }

        if (pawnFields.TryGetValue("m_hController", out object? rawController))
        {
            ulong packed = Convert.ToUInt64(rawController, CultureInfo.InvariantCulture);
            await Assert.That((ulong)pawn.ControllerHandle).IsEqualTo(packed);

            // 0x00FFFFFF is the all-ones sentinel for a 24-bit handle field, encoding the same "no
            // entity" the 32-bit 0xFFFFFFFF does. The pawn picked here is the first with m_iHealth
            // at the FINAL checkpoint, and on a real match that is routinely a dead pawn carrying
            // exactly this: health 0, and a handle whose masked index is 0x3FFF, the reserved
            // invalid entity index. The committed four-round sample never produces one, so the
            // narrower guard held there while failing on every full match.
            if (pawn.ControllerHandle is not (0u or 0x00FF_FFFFu or 0xFFFF_FFFFu))
            {
                CSPlayerController? controller = world.Resolve<CSPlayerController>(pawn.ControllerHandle);
                await Assert.That(controller).IsNotNull();
                await Assert.That(controller!.EngineClassName).IsEqualTo("CCSPlayerController");
            }
        }

        if (pawnFields.TryGetValue("m_pMovementServices.m_nButtons", out object? rawButtons))
        {
            await Assert.That(pawn.Buttons)
                .IsEqualTo(Convert.ToUInt64(rawButtons, CultureInfo.InvariantCulture));
        }

        // ── The #29 companion round-trip + seen-aware Origin, measured on real data ──
        // The 0.1.1 run measured "companion flatness": ActiveWeapon (then typed
        // BasePlayerWeapon?) was structurally null whenever the live target's class was a
        // concrete weapon. 0.3.0 completes the arc: the hierarchy is emitted, the companions
        // are BasePlayerWeapon? again, and the typed fold SUCCEEDS for concrete weapons — a
        // resolved SmokeGrenade IS a BasePlayerWeapon now. This block measures:
        //   1. ActiveWeapon / LastWeapon resolve NON-null on live pawns, to the target's
        //      concrete wrapper type, and the INHERITED Clip1 read (base body, base ordinal,
        //      derived binding) round-trips against the Fields projection.
        //   2. CSPlayerController.PlayerPawn (once dropped by the C_-spelling matcher miss) resolves,
        //      and its Health round-trips against the joined pawn state.
        //   3. Origin reads null on every live pawn — and null is the NORMAL case: a real pawn
        //      carries cell leaves (CBodyComponent.m_cellX…), never either origin spelling, and
        //      the pre-0.2.0 Vector3 read manufactured (0,0,0) from exactly that absence. The
        //      cell reconstruction is measured alongside to prove the pawn is genuinely
        //      somewhere while the canonical-path read honestly says "never received".
        // A companion may legitimately resolve null with a valid-looking handle: the target
        // slot can be empty (the weapon was removed — a stale handle) or occupied by a class
        // the curated set never wrapped. Only "target live AND curated, still null" is a
        // defect; the other outcomes are classified and reported as measurements.
        HashSet<string> curated = EntityWrapperRegistry.Bindings
            .Select(b => b.EngineClass)
            .Where(c => !ExpectedCreateExclusions.Contains(c))
            .ToHashSet(StringComparer.Ordinal);
        int activeCandidates = 0, activeResolved = 0, activeConcreteClass = 0, clipRoundTrips = 0;
        int lastCandidates = 0, lastResolved = 0, lastStale = 0, lastUncurated = 0;
        int originReads = 0, originNull = 0, cellPositions = 0;
        string? firstResolveLine = null;
        List<string> lastNullLines = new();

        foreach (EntityState candidate in tracker.CurrentEntities.OfClass("CCSPlayerPawn"))
        {
            CSPlayerPawn holder = (CSPlayerPawn)EntityWrapperRegistry.Create(
                "CCSPlayerPawn", world.CreateReader(pawnBinding, candidate), world)!;

            originReads++;
            if (holder.Origin is null)
            {
                originNull++;
            }

            await Assert.That(candidate.Fields.ContainsKey("m_vecOrigin")).IsFalse();
            await Assert.That(candidate.Fields.ContainsKey("m_CBodyComponent.m_pSceneNode.m_vecOrigin"))
                .IsFalse();
            if (PositionUtil.CellToWorldVector(candidate) is not null)
            {
                cellPositions++;
            }

            if (holder.ActiveWeaponHandle is not (0u or 0xFFFF_FFFFu))
            {
                activeCandidates++;
                BasePlayerWeapon? active = holder.ActiveWeapon;
                EntityHandle activeHandle = new(holder.ActiveWeaponHandle);
                EntityState? target = activeHandle.IsValid ? tracker.CurrentEntities[activeHandle.Index] : null;
                if (target is not null && curated.Contains(target.ClassName))
                {
                    await Assert.That(active).IsNotNull();
                    await Assert.That(active!.EngineClassName).IsEqualTo(target.ClassName);
                    activeResolved++;

                    // Measurement 3, on live data: the inherited Clip1 — BasePlayerWeapon's
                    // body, the base's compile-time ordinal, the concrete target's own
                    // prefix-layout binding — against the Fields projection (0-default).
                    int expectedClip = target.Fields.TryGetValue("m_iClip1", out object? clip)
                        ? Convert.ToInt32(clip, CultureInfo.InvariantCulture)
                        : 0;
                    await Assert.That(active.Clip1).IsEqualTo(expectedClip);
                    clipRoundTrips++;

                    if (target.ClassName != "CBasePlayerWeapon")
                    {
                        activeConcreteClass++;
                        firstResolveLine ??= "  companions: ActiveWeapon target is "
                            + $"{target.ClassName} -> {active.GetType().Name} through the typed "
                            + $"BasePlayerWeapon? companion (inherited Clip1={active.Clip1} via the base ordinal)";
                    }
                }
            }

            if (holder.LastWeaponHandle is not (0u or 0xFFFF_FFFFu))
            {
                lastCandidates++;
                BasePlayerWeapon? last = holder.LastWeapon;
                EntityHandle lastHandle = new(holder.LastWeaponHandle);
                EntityState? target = lastHandle.IsValid ? tracker.CurrentEntities[lastHandle.Index] : null;
                if (target is null)
                {
                    lastStale++;
                    await Assert.That(last).IsNull();
                    lastNullLines.Add($"  #29 companion: LastWeapon handle 0x{holder.LastWeaponHandle:X8} "
                        + "-> target slot empty (stale handle), resolved null correctly");
                }
                else if (!curated.Contains(target.ClassName))
                {
                    lastUncurated++;
                    await Assert.That(last).IsNull();
                    lastNullLines.Add($"  #29 companion: LastWeapon target is {target.ClassName} "
                        + "(not in the curated 58), resolved null correctly");
                }
                else
                {
                    await Assert.That(last).IsNotNull();
                    await Assert.That(last!.EngineClassName).IsEqualTo(target.ClassName);
                    lastResolved++;
                }
            }
        }

        // Origin: null on every live pawn, while the cell leaves place at least one pawn in
        // the world — absence-as-null measured against position-exists-elsewhere.
        await Assert.That(originReads).IsGreaterThan(0);
        await Assert.That(originNull).IsEqualTo(originReads);
        await Assert.That(cellPositions).IsGreaterThan(0);

        // ActiveWeapon: at least one live+curated target resolved, and at least one of those
        // was a concrete weapon class — the exact shape that read structurally null on 0.1.1.
        await Assert.That(activeCandidates).IsGreaterThan(0);
        await Assert.That(activeResolved).IsGreaterThan(0);
        await Assert.That(activeConcreteClass).IsGreaterThan(0);
        // LastWeapon: every live+curated target resolved; stale/uncurated nulls are asserted
        // null above and reported below.
        await Assert.That(lastResolved + lastStale + lastUncurated).IsEqualTo(lastCandidates);

        // PlayerPawn on the controller — the typed fold, round-tripped through the live join.
        // Same classification as the weapons: a controller whose handle targets an empty slot
        // (the GOTV/HLTV controller, a disconnected player) resolves null CORRECTLY; every
        // live CCSPlayerPawn target must resolve and its Health must round-trip.
        EntityClassBinding controllerBinding = propertyCarrying.Single(b => b.EngineClass == "CCSPlayerController");
        int pawnLinkCandidates = 0, pawnLinkResolved = 0, pawnLinkStale = 0;
        foreach (EntityState candidate in tracker.CurrentEntities.OfClass("CCSPlayerController"))
        {
            CSPlayerController ctl = (CSPlayerController)EntityWrapperRegistry.Create(
                "CCSPlayerController", world.CreateReader(controllerBinding, candidate), world)!;
            if (ctl.PlayerPawnHandle is 0u or 0xFFFF_FFFFu)
            {
                continue;
            }

            pawnLinkCandidates++;
            CSPlayerPawn? linked = ctl.PlayerPawn;
            EntityHandle pawnHandle = new(ctl.PlayerPawnHandle);
            EntityState? linkedState = pawnHandle.IsValid ? tracker.CurrentEntities[pawnHandle.Index] : null;
            if (linkedState is null || linkedState.ClassName != "CCSPlayerPawn")
            {
                pawnLinkStale++;
                await Assert.That(linked).IsNull();
                Console.WriteLine($"  #29 companion: PlayerPawn handle 0x{ctl.PlayerPawnHandle:X8} "
                    + $"(IsHLTV={ctl.IsHLTV}) -> target {(linkedState is null ? "slot empty" : linkedState.ClassName)}, "
                    + "resolved null correctly");
                continue;
            }

            await Assert.That(linked).IsNotNull();
            await Assert.That(linked!.EngineClassName).IsEqualTo("CCSPlayerPawn");

            int expectedHealth = linkedState.Fields.TryGetValue("m_iHealth", out object? h)
                ? Convert.ToInt32(h, CultureInfo.InvariantCulture)
                : 0;
            await Assert.That(linked.Health).IsEqualTo(expectedHealth);
            pawnLinkResolved++;
        }

        await Assert.That(pawnLinkCandidates).IsGreaterThan(0);
        await Assert.That(pawnLinkResolved).IsGreaterThan(0);
        await Assert.That(pawnLinkResolved + pawnLinkStale).IsEqualTo(pawnLinkCandidates);

        Console.WriteLine(firstResolveLine ?? "  #29 companion: no concrete-class weapon target live at the final checkpoint");
        foreach (string line in lastNullLines)
        {
            Console.WriteLine(line);
        }

        Console.WriteLine($"  0.3.0 measurements at the final checkpoint: "
            + $"ActiveWeapon {activeResolved}/{activeCandidates} resolved ({activeConcreteClass} concrete-class targets, "
            + $"{clipRoundTrips} inherited Clip1 round-trips), "
            + $"LastWeapon {lastResolved}/{lastCandidates} resolved ({lastStale} stale, {lastUncurated} uncurated), "
            + $"PlayerPawn {pawnLinkResolved}/{pawnLinkCandidates} resolved with Health round-trip ({pawnLinkStale} stale/HLTV), "
            + $"Origin null on {originNull}/{originReads} pawns ({cellPositions} positioned via cell leaves)");
    }

    /// <summary>
    ///     Property-carrying bindings never yet A/B'd that have at least one live entity right
    ///     now — computed in one walk over the live-entity index (~250 entries), so it is cheap
    ///     enough to poll every 64 frames.
    /// </summary>
    private static List<EntityClassBinding> UnseenNowLive(
        EntityTracker tracker,
        List<EntityClassBinding> propertyCarrying,
        AbTotals totals)
    {
        if (totals.ClassesChecked.Count == propertyCarrying.Count)
        {
            return [];
        }

        HashSet<string> liveClasses = new(StringComparer.Ordinal);
        foreach (EntityState entity in tracker.CurrentEntities.All())
        {
            liveClasses.Add(entity.ClassName);
        }

        return propertyCarrying
            .Where(b => !totals.ClassesChecked.Contains(b.EngineClass) && liveClasses.Contains(b.EngineClass))
            .ToList();
    }

    /// <summary>
    ///     One A/B sweep over the tracker's CURRENT entity table: every ordinal of every
    ///     property-carrying class with at least one live entity, joined against the
    ///     <see cref="EntityState.Fields" /> projection by canonical path through the alias
    ///     table. Wrappers come from <see cref="EntityWrapperRegistry.Create" /> over readers
    ///     bound with the PACKAGE's manifests.
    /// </summary>
    private static async Task RunAbPass(
        EntityTracker tracker,
        TrackerEntityWorld world,
        List<EntityClassBinding> propertyCarrying,
        AbTotals totals,
        string checkpoint)
    {
        foreach (EntityClassBinding binding in propertyCarrying)
        {
            string[][] candidates = CandidatesByOrdinal(binding);
            List<EntityState> states = tracker.CurrentEntities.OfClass(binding.EngineClass).ToList();
            if (states.Count == 0)
            {
                continue;
            }

            totals.ClassesChecked.Add(binding.EngineClass);
            int classComparisons = 0;
            foreach (EntityState state in states)
            {
                totals.Entities++;
                LensBoundReader reader = world.CreateReader(binding, state);
                EntityWrapper? wrapper = EntityWrapperRegistry.Create(binding.EngineClass, reader, world);
                await Assert.That(wrapper).IsNotNull();
                await Assert.That(wrapper!.EngineClassName).IsEqualTo(state.ClassName);

                IReadOnlyDictionary<string, object?> fields = state.Fields;
                for (int ordinal = 0; ordinal < binding.CanonicalPaths.Count; ordinal++)
                {
                    // Join by canonical path THROUGH the alias table: the projection is keyed
                    // by the demo's wire spelling, so probe canonical first, then each alias.
                    bool projected = false;
                    object? projectedValue = null;
                    string wireKey = binding.CanonicalPaths[ordinal];
                    foreach (string candidate in candidates[ordinal])
                    {
                        if (fields.TryGetValue(candidate, out projectedValue))
                        {
                            projected = true;
                            wireKey = candidate;
                            break;
                        }
                    }

                    bool read = reader.TryReadObject(ordinal, out object? readValue);
                    totals.Comparisons++;
                    classComparisons++;

                    if (projected != read)
                    {
                        totals.Mismatches.Add(
                            $"{checkpoint} {binding.EngineClass}[{state.Serial}] ord {ordinal} "
                            + $"({binding.CanonicalPaths[ordinal]}): Fields[{wireKey}] present={projected}, "
                            + $"reader present={read}");
                        continue;
                    }

                    if (!projected)
                    {
                        continue;
                    }

                    totals.PresentComparisons++;
                    if (!string.Equals(wireKey, binding.CanonicalPaths[ordinal], StringComparison.Ordinal))
                    {
                        // The field surfaced under a historical spelling — the SDK-to-DVN
                        // ordinal divergence crossed the alias bridge on real demo data.
                        totals.AliasBridgeReads++;
                    }

                    if (!Equals(projectedValue, readValue))
                    {
                        totals.Mismatches.Add(
                            $"{checkpoint} {binding.EngineClass}[{state.Serial}] ord {ordinal} "
                            + $"({binding.CanonicalPaths[ordinal]}): Fields[{wireKey}]={projectedValue}, "
                            + $"reader={readValue}");
                    }
                }
            }

            totals.Log.Add(
                $"{checkpoint} {binding.EngineClass}: {states.Count} entities x {binding.CanonicalPaths.Count} ordinals = {classComparisons}");
        }
    }

    /// <summary>
    ///     Per-ordinal candidate spellings in the reader's own probe order: the canonical path
    ///     first, then every historical spelling aliased to it (ordinal-sorted). This mirrors
    ///     <c>LensOrdinalMap</c>'s resolution exactly so the A/B joins the way the seam reads.
    /// </summary>
    private static string[][] CandidatesByOrdinal(EntityClassBinding binding)
    {
        Dictionary<string, List<string>> aliasesByTarget = new(StringComparer.Ordinal);
        foreach (KeyValuePair<string, string> alias in binding.Aliases)
        {
            if (!aliasesByTarget.TryGetValue(alias.Value, out List<string>? spellings))
            {
                aliasesByTarget[alias.Value] = spellings = new List<string>();
            }

            spellings.Add(alias.Key);
        }

        string[][] result = new string[binding.CanonicalPaths.Count][];
        for (int i = 0; i < binding.CanonicalPaths.Count; i++)
        {
            string canonical = binding.CanonicalPaths[i];
            if (aliasesByTarget.TryGetValue(canonical, out List<string>? spellings))
            {
                spellings.Sort(StringComparer.Ordinal);
                result[i] = [canonical, .. spellings];
            }
            else
            {
                result[i] = [canonical];
            }
        }

        return result;
    }
}
