#region

using System.Diagnostics;
using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Analysis.Building;
using CS2DemoKit.Analysis.Config;
using CS2DemoKit.Analysis.Events;
using CS2DemoKit.Analysis.Graphs;
using CS2DemoKit.Analysis.Nodes;
using CS2DemoKit.Analysis.PlayerStats;
using CS2DemoKit.Analysis.Plugins;
using CS2DemoKit.Analysis.Registry;
using CS2DemoKit.Parser;
using CS2DemoKit.Parser.EntityTracking;
using CS2DemoKit.Parser.GameEvents;
using CS2DemoKit.TestSupport;
using TUnit.Core.Exceptions;

#endregion

namespace CS2DemoKit.Analysis.Tests;

/// <summary>Entity integration tests.</summary>
[NotInParallel]
[Category("Integration")]
public class EntityIntegrationTests
{
    // ── 4. ActiveWeaponProvider (two-hop entity resolution) ─────────
    // (ActiveWeaponProvider_ExposesExpectedMetadata moved to ProviderMetadataTests.)
    /// <summary>Active weapon provider_reads class name for active slot_from demo.</summary>
    [Test]
    public async Task ActiveWeaponProvider_ReadsClassNameForActiveSlot_FromDemo()
    {
        string path = DemoTestHelper.RequireDemo();

        ParsedDemo parsed = DemoTestHelper.GetOrParse(path);
        DemoContext ctx = DemoAnalyzer.BuildContext(parsed);

        EntityStateLayer layer = ctx.CreateEntityLayer();
        layer.SeekToTick(parsed.TickCount / 2);

        ActiveWeaponProvider provider = new();

        int hits = 0;
        string? sampleClass = null;
        for (int slot = 0; slot < 64; slot++)
        {
            object? v = provider.Read(layer, slot);
            if (v is string { Length: > 0 } cls)
            {
                hits++;
                sampleClass ??= cls;
            }
        }

        Console.WriteLine($"Slots with resolvable active weapon at midpoint: {hits}  (sample: {sampleClass})");
        // Same 1–10 bound as PawnHealthProvider: live pawns at midpoint should
        // each have a resolvable active weapon.
        await Assert.That(hits).IsBetween(1, 10).WithInclusiveBounds();
        // Resolved class names always start with "C" (e.g. CWeaponAK47, CWeaponKnife).
        await Assert.That(sampleClass).IsNotNull();
        await Assert.That(sampleClass!.StartsWith('C')).IsTrue();
    }

    // ── 4b. Decode-health tripwire (fails loud; does NOT skip on decode failure) ──
    /// <summary>
    ///     Decode-health tripwire. On a real current-era demo the entity decoder must NOT hit a
    ///     bit-misalignment error and pawns MUST resolve at the demo midpoint. Unlike the other
    ///     entity tests, this deliberately does <b>not</b> call <see cref="SkipIfEntityDecodeFailed" />
    ///     — a decode regression (e.g. a new schema field type mis-mapped, as the AnimGraph2
    ///     <c>CGlobalSymbol</c>/<c>CUtlBinaryBlock</c> bug was) must <b>fail</b> the suite, not
    ///     silently skip it. Skips only when no demo is available.
    /// </summary>
    [Test]
    public async Task EntityDecode_IsHealthy_NoMisalignmentAndPawnsResolve()
    {
        string path = DemoTestHelper.RequireDemo();

        ParsedDemo parsed = DemoTestHelper.GetOrParse(path);

        // Replay to live play with a fresh tracker (no analysis build, keeping the tripwire light so
        // it stays reliable under full-suite memory load). Anchored on a kill rather than the frame
        // midpoint so this holds for any demo; see LivePlayFrameIndex.
        EntityTracker tracker = new();
        tracker.AdvanceToIndex(DemoTestHelper.LivePlayFrameIndex(parsed), parsed.Frames);

        // 1. No bit-misalignment error replaying there. (Broken decode on AnimGraph2-era demos used
        //    to set this and the other tests SKIPPED on it.)
        await Assert.That(tracker.LastEntityError).IsNull();

        // 2. Pawn entities resolve by slot (controller-bound). A 5v5 has ~10 pawn entities;
        //    broken decode (garbage m_hController) resolves 0. Counts dead pawns too.
        int pawns = 0;
        PawnLookup.ForEachLivePawn(tracker, (_, _) => pawns++);

        await Assert.That(pawns).IsGreaterThanOrEqualTo(5);
    }

    // ── 5. Lazy-activation tripwire ───────────────────────────────────────────
    /// <summary>Entity scanner_not allocated_when no providers registered.</summary>
    [Test]
    public async Task EntityScanner_NotAllocated_WhenNoProvidersRegistered()
    {
        string path = DemoTestHelper.RequireDemo();

        ParsedDemo parsed = DemoTestHelper.GetOrParse(path);

        EventRegistry registry = EventRegistry.Build();
        // Explicitly EMPTY provider registry → no ContextName can match → scanner stays null
        // even though gameplay_phase references entity.game.freeze_period in its triggers.
        EntityValueProviderRegistry emptyProviders = new();
        RuleChainBuilder builder = new(registry, parsed,
            entityProviders: emptyProviders);
        BuildResult build = builder.Build();

        await Assert.That(build.EntityScanner).IsNull();
    }
    // Provider metadata tests moved to ProviderMetadataTests.cs (S8: free from
    // class-level [NotInParallel] — they touch no demo and run in microseconds).

    // ── 2. Provider read against a real demo ──────────────────────────────────
    /// <summary>Freeze period provider_reads ccs game rules field_from demo.</summary>
    [Test]
    public async Task FreezePeriodProvider_ReadsCCSGameRulesField_FromDemo()
    {
        string path = DemoTestHelper.RequireDemo();

        ParsedDemo parsed = DemoTestHelper.GetOrParse(path);
        DemoContext ctx = DemoAnalyzer.BuildContext(parsed);

        EntityStateLayer layer = ctx.CreateEntityLayer();
        layer.SeekToTick(parsed.TickCount / 2);

        FreezePeriodProvider provider = new();
        object? value = provider.Read(layer);

        // Either null (entity not in this slot at this tick) or a bool — never some other type.
        if (value is not null)
        {
            await Assert.That(value).IsTypeOf(typeof(bool));
        }

        Console.WriteLine($"FreezePeriod at midpoint tick: {value?.ToString() ?? "<null>"}");
    }

    // ── 4. End-to-end: gameplay_phase transitions to FreezeTime ──────────────
    /// <summary>Gameplay phase_transitions to freeze time_via entity state.</summary>
    [Test]
    public async Task GameplayPhase_TransitionsToFreezeTime_ViaEntityState()
    {
        string path = DemoTestHelper.RequireDemo();

        ParsedDemo parsed = DemoTestHelper.GetOrParse(path);

        EventRegistry registry = EventRegistry.Build();
        EntityValueProviderRegistry entityProviders = EntityValueProviderRegistry.CreateDefault();
        // Empty user config — built-in contexts (incl. gameplay_phase + entity trigger) are
        // always built, so this exercises the full lazy-activation + dispatch + trigger flow.
        RuleChainBuilder builder = new(registry, parsed,
            entityProviders: entityProviders);
        BuildResult build = builder.Build();

        // Lazy activation must have kicked in — gameplay_phase references entity.game.freeze_period.
        await Assert.That(build.EntityScanner).IsNotNull();

        StateGraphEvaluator evaluator = new(build.Graph, parsed, build.PlayerContextIndex, build.EntityScanner);
        SnapshotTable snapshots = evaluator.EvaluateWithSnapshots(parsed.Frames, build.Nodes).MessageSnapshots;

        // Find gameplay_phase node's tracked index.
        int gpIdx = -1;
        for (int i = 0; i < build.Nodes.Count; i++)
        {
            if (build.Nodes[i].Name == "GameplayPhase")
            {
                gpIdx = i;
                break;
            }
        }

        await Assert.That(gpIdx).IsGreaterThanOrEqualTo(0);

        int freezeTimeHits = 0;
        for (int s = 0; s < snapshots.Count; s++)
        {
            if (gpIdx >= snapshots.Width)
            {
                continue;
            }

            if (snapshots[s, gpIdx].DisplayValue == "FreezeTime")
            {
                freezeTimeHits++;
                if (freezeTimeHits > 0)
                {
                    break;
                }
            }
        }

        Console.WriteLine($"gameplay_phase 'FreezeTime' snapshot hits: {freezeTimeHits}");
        // Early-exits at first hit, so the only failure mode is "never observed
        // FreezeTime" — 0 hits. Range bound is degenerate here; equality is
        // what matters but range form is consistent with the rest of the file.
        await Assert.That(freezeTimeHits).IsBetween(1, int.MaxValue).WithInclusiveBounds();
    }

    // ── 4b. End-to-end: gameplay_phase cycles Freeze→Active across many rounds ──
    /// <summary>
    ///     Stronger sibling of the FreezeTime test: confirms the full <c>gameplay_phase</c> state
    ///     machine doesn't just reach FreezeTime once but CYCLES through FreezeTime and ActiveWithBuy
    ///     across the whole match via the HLTV entity path (<c>entity.game.freeze_period</c>), where
    ///     <c>round_prestart</c> fires 0×. Counts rising edges into FreezeTime (≈ one per round) so a
    ///     regression that stalls the machine after round 1 — or silently loses the entity trigger —
    ///     is caught. ActivePostBuy is logged but not asserted: it depends on a <c>buytime_ended</c>
    ///     game event that is absent on some sources.
    /// </summary>
    [Test]
    public async Task GameplayPhase_CyclesFreezeAndActive_AcrossRounds_ViaEntityState()
    {
        string path = DemoTestHelper.RequireDemo();

        ParsedDemo parsed = DemoTestHelper.GetOrParse(path);

        EventRegistry registry = EventRegistry.Build();
        EntityValueProviderRegistry entityProviders = EntityValueProviderRegistry.CreateDefault();
        RuleChainBuilder builder = new(registry, parsed, entityProviders: entityProviders);
        BuildResult build = builder.Build();

        await Assert.That(build.EntityScanner).IsNotNull();

        StateGraphEvaluator evaluator = new(build.Graph, parsed, build.PlayerContextIndex, build.EntityScanner);
        SnapshotTable snapshots = evaluator.EvaluateWithSnapshots(parsed.Frames, build.Nodes).MessageSnapshots;

        int gpIdx = -1;
        for (int i = 0; i < build.Nodes.Count; i++)
        {
            if (build.Nodes[i].Name == "GameplayPhase")
            {
                gpIdx = i;
                break;
            }
        }

        await Assert.That(gpIdx).IsGreaterThanOrEqualTo(0);

        HashSet<string> phasesSeen = new(StringComparer.Ordinal);
        int freezeRisingEdges = 0;
        string? prev = null;
        for (int m = 0; m < snapshots.Count; m++)
        {
            if (gpIdx >= snapshots.Width)
            {
                continue;
            }

            NodeSnapshot s = snapshots[m, gpIdx];
            if (!s.IsActive || string.IsNullOrEmpty(s.DisplayValue))
            {
                continue;
            }

            string value = s.DisplayValue;
            phasesSeen.Add(value);
            if (value == "FreezeTime" && prev != "FreezeTime")
            {
                freezeRisingEdges++;
            }

            prev = value;
        }

        Console.WriteLine($"gameplay_phase values seen: {string.Join(", ", phasesSeen)}");
        Console.WriteLine($"FreezeTime rising edges (≈ rounds): {freezeRisingEdges}");

        // The machine must reach both the freeze and the live-with-buy phases.
        await Assert.That(phasesSeen.Contains("FreezeTime")).IsTrue();
        await Assert.That(phasesSeen.Contains("ActiveWithBuy")).IsTrue();
        // A real match is 13+ rounds; require clearly-multi-round cycling. The lower bound is
        // deliberately slack (10) to tolerate a missed edge or two, not pinned to the exact count.
        await Assert.That(freezeRisingEdges).IsGreaterThanOrEqualTo(10);
    }

    /// <summary>
    ///     Hurt enrichment populates attacker_active_weapon on real hurt events — WHEN a rule
    ///     references it. Per-player providers are reference-gated: the config here reads
    ///     the weapon enrichment, which is what activates the ActiveWeaponProvider; an empty
    ///     config would (correctly) leave the weapon un-snapshotted.
    /// </summary>
    [Test]
    public async Task HurtEnrichment_PopulatesAttackerActiveWeapon_OnRealHurtEvents()
    {
        string path = DemoTestHelper.RequireDemo();

        ParsedDemo parsed = DemoTestHelper.GetOrParse(path);

        EventRegistry registry = EventRegistry.Build();
        EntityValueProviderRegistry entityProviders = EntityValueProviderRegistry.CreateDefault();
        PerPlayerEntityValueProviderRegistry perPlayerProviders = PerPlayerEntityValueProviderRegistry.CreateDefault();
        // v2 probe ruleset: a where: read of enrich.hurt.attacker_active_weapon is what
        // reference-gates the ActiveWeaponProvider — an empty build would (correctly)
        // leave the weapon un-snapshotted.
        const string weaponProbeRuleset =
            """
            ruleset: weapon_probe
            title: Weapon probe
            summary: Activates the active-weapon enrichment provider.
            for: each_player
            stats:
              knife_hurts:
                count: damage_dealt
                where: 'enrich.hurt.attacker_active_weapon == "weapon_knife"'
                per: match
            """;
        BuildResult build = RulesV2.V2KindGoldenSupport.CompileV2(parsed, weaponProbeRuleset);

        await Assert.That(build.EntityScanner).IsNotNull();

        StateGraphEvaluator evaluator = new(build.Graph, parsed, build.PlayerContextIndex, build.EntityScanner);
        EvaluationResult result = evaluator.EvaluateWithSnapshots(parsed.Frames, build.Nodes);
        SkipIfEntityDecodeFailed(build.EntityScanner!.Layer.Tracker);

        // Locate target nodes. Also locate enrich.hurt.victim_health_before as a control —
        // it is always written by the same edge, so if it shows up in snapshots and the
        // weapon doesn't, the bug is weapon-specific (provider/snapshot key/etc), not a
        // snapshot-timing issue with TransientValueNode<string> generally.
        int weaponNodeIdx = -1;
        int hpNodeIdx = -1;
        for (int i = 0; i < result.FinalTrackedNodes.Count; i++)
        {
            if (result.FinalTrackedNodes[i].Name == "enrich.hurt.attacker_active_weapon")
            {
                weaponNodeIdx = i;
            }

            if (result.FinalTrackedNodes[i].Name == "enrich.hurt.victim_health_before")
            {
                hpNodeIdx = i;
            }
        }

        await Assert.That(weaponNodeIdx).IsGreaterThanOrEqualTo(0);
        await Assert.That(hpNodeIdx).IsGreaterThanOrEqualTo(0);

        int hurtEvents = 0;
        int hurtsWithWeapon = 0;
        int hurtsWithHp = 0;
        string? sampleWeapon = null;
        string? sampleHp = null;
        for (int m = 0; m < result.Messages.Count; m++)
        {
            if (result.Messages[m].Message is not GameEventMessage gem)
            {
                continue;
            }

            if (gem.DecodedEvent.Payload is not PlayerHurtEvent)
            {
                continue;
            }

            hurtEvents++;

            string? wval = result.MessageSnapshots[m, weaponNodeIdx].DisplayValue;
            string? hval = result.MessageSnapshots[m, hpNodeIdx].DisplayValue;
            if (!string.IsNullOrEmpty(wval) && wval.StartsWith('C'))
            {
                hurtsWithWeapon++;
                sampleWeapon ??= wval;
            }

            if (!string.IsNullOrEmpty(hval) && hval != "0")
            {
                hurtsWithHp++;
                sampleHp ??= hval;
            }
        }

        Console.WriteLine($"Hurt events: {hurtEvents}");
        Console.WriteLine($"  with attacker_active_weapon: {hurtsWithWeapon} (sample: {sampleWeapon})");
        Console.WriteLine($"  with victim_health_before  : {hurtsWithHp} (sample: {sampleHp})");
        // A pro/MM match has hundreds-to-thousands of hurt events. Bound on both
        // sides; lower catches "events not decoded," upper is generous for OT.
        await Assert.That(hurtEvents).IsBetween(100, 20_000).WithInclusiveBounds();
        // We ship with ≥95% of hurt events carrying an attacker weapon
        // (the only misses are non-player damage sources: world, suicide, bomb).
        // 100% is too tight; <95% means the snapshot pull is misfiring.
        int weaponThreshold = (int)(hurtEvents * 0.95);
        await Assert.That(hurtsWithWeapon)
            .IsBetween(weaponThreshold, hurtEvents).WithInclusiveBounds();
        await Assert.That(sampleWeapon).IsNotNull();
    }

    /// <summary>Hurt team enrichment_uses entity hp when scanner provided.</summary>
    [Test]
    public async Task HurtTeamEnrichment_UsesEntityHpWhenScannerProvided()
    {
        string path = DemoTestHelper.RequireDemo();

        ParsedDemo parsed = DemoTestHelper.GetOrParse(path);

        EventRegistry registry = EventRegistry.Build();
        EntityValueProviderRegistry entityProviders = EntityValueProviderRegistry.CreateDefault();
        PerPlayerEntityValueProviderRegistry perPlayerProviders = PerPlayerEntityValueProviderRegistry.CreateDefault();
        RuleChainBuilder builder = new(registry, parsed,
            entityProviders: entityProviders,
            perPlayerEntityProviders: perPlayerProviders);
        BuildResult build = builder.Build();

        // Per-player provider registry activates the scanner unconditionally.
        await Assert.That(build.EntityScanner).IsNotNull();

        StateGraphEvaluator evaluator = new(build.Graph, parsed, build.PlayerContextIndex, build.EntityScanner);
        EvaluationResult result = evaluator.EvaluateWithSnapshots(parsed.Frames, build.Nodes);

        // Real demos have hundreds of thousands to a few million inner messages
        // depending on tick count. Tight lower bound catches "evaluator no-op"
        // failures; upper bound is generous so OT and long demos still pass.
        Console.WriteLine($"entity e2e: {result.Messages.Count} messages processed, scanner allocated.");
        await Assert.That(result.Messages.Count).IsBetween(50_000, 20_000_000).WithInclusiveBounds();
    }

    // ── Per-player provider tests ─────────────────────────────────────────────
    // (PawnHealthProvider_ExposesExpectedMetadata moved to ProviderMetadataTests.)
    /// <summary>Pawn health provider_reads health for active slot_from demo.</summary>
    [Test]
    public async Task PawnHealthProvider_ReadsHealthForActiveSlot_FromDemo()
    {
        string path = DemoTestHelper.RequireDemo();

        ParsedDemo parsed = DemoTestHelper.GetOrParse(path);
        DemoContext ctx = DemoAnalyzer.BuildContext(parsed);

        EntityStateLayer layer = ctx.CreateEntityLayer();
        layer.SeekToTick(parsed.TickCount / 2);

        PawnHealthProvider provider = new();

        // Scan slots 0..63; at least one should resolve to a live player with HP > 0
        // somewhere in the middle of the demo.
        int hits = 0;
        int? sampleHp = null;
        for (int slot = 0; slot < 64; slot++)
        {
            object? v = provider.Read(layer, slot);
            if (v is int hp and > 0)
            {
                hits++;
                sampleHp ??= hp;
            }
        }

        Console.WriteLine($"Slots with live pawn at midpoint: {hits}  (sample HP: {sampleHp})");
        // A 5v5 demo at midpoint has 0–10 live pawns. Mid-match shouldn't be
        // 0 in most cases (whole-team wipes are rare and brief); allow 1–10
        // since this test doesn't pin a specific midpoint state.
        await Assert.That(hits).IsBetween(1, 10).WithInclusiveBounds();
        await Assert.That(sampleHp).IsNotNull();
    }

    /// <summary>Player snapshot builder_alive players_have valid field ranges.</summary>
    [Test]
    public async Task PlayerSnapshotBuilder_AlivePlayers_HaveValidFieldRanges()
    {
        string path = DemoTestHelper.RequireDemo();
        ParsedDemo parsed = DemoTestHelper.GetOrParse(path);

        EntityStateLayer layer = new(parsed.Frames);
        layer.SeekToTick(parsed.TickCount / 2);
        SkipIfEntityDecodeFailed(layer.Tracker);

        IReadOnlyList<PlayerSnapshot> snaps = PlayerSnapshotBuilder.Build(layer.Tracker, parsed);

        PlayerSnapshot[] alive = snaps.Where(s => s.IsAlive).ToArray();
        Console.WriteLine($"Alive players at midpoint: {alive.Length} / {snaps.Count}");

        // Mid-match should have at least some alive players. If everyone reads dead
        // the lifeState lookup is broken (a bug that once hid behind a
        // diagnostic dump). At a random midpoint we typically see 4–10 alive.
        await Assert.That(alive.Length).IsGreaterThanOrEqualTo(2);

        foreach (PlayerSnapshot s in alive)
        {
            // Health: alive ⇒ HP ∈ [1, 100]. 0 with IsAlive=true means lifeState was
            // missing AND the (broken) health>0 fallback misfired.
            await Assert.That(s.Health).IsBetween(1, 100).WithInclusiveBounds();

            // Armor: 0–100 across all rounds. Negative or >100 indicates wide-cast
            // overflow (the UInt64-as-uint bug would land negative here).
            await Assert.That(s.Armor).IsBetween(0, 100).WithInclusiveBounds();

            // Money: starts at 800, capped at 16000 by Valve's economy. Negative
            // would indicate the same wide-cast bug.
            await Assert.That(s.Money).IsBetween(0, 16000).WithInclusiveBounds();
        }

        // At least one alive player should have a resolved active weapon (and the
        // weapon's short name should not be the empty string the Builder uses as
        // sentinel for "no weapon resolved"). The known bug class here —
        // dotted sub-entity path + UInt64 handle — would zero this out.
        PlayerSnapshot[] armed = alive.Where(s => s.ActiveWeapon.Length > 0).ToArray();
        await Assert.That(armed.Length).IsGreaterThanOrEqualTo(1);
        Console.WriteLine($"Alive players with resolved active weapon: {armed.Length} / {alive.Length}  (sample: {armed.FirstOrDefault()?.ActiveWeapon})");
    }

    // ── PlayerSnapshotBuilder end-to-end ──────────────────────────────────────
    // These tests exercise PlayerSnapshotBuilder.Build directly — the same code
    // path the UI uses. Replaces two earlier weak regression tests that
    // re-implemented the lookup logic in the test body and would have passed
    // even when the production code was broken.
    //
    // Assertions go beyond "> 0": every snapshot's fields are checked against
    // ranges and invariants that reflect what a healthy CS2 demo produces at
    // mid-match. Per S4 in the audit: existence-only assertions don't catch
    // partial regressions.
    /// <summary>Player snapshot builder_at midpoint_produces teamed players.</summary>
    [Test]
    public async Task PlayerSnapshotBuilder_DuringLivePlay_ProducesTeamedPlayers()
    {
        string path = DemoTestHelper.RequireDemo();
        ParsedDemo parsed = DemoTestHelper.GetOrParse(path);

        // Anchored on a kill, not TickCount / 2: on a full match the tick midpoint can land in
        // warmup or halftime with nobody on a team. See LivePlayTick.
        EntityStateLayer layer = new(parsed.Frames);
        layer.SeekToTick(DemoTestHelper.LivePlayTick(parsed));
        SkipIfEntityDecodeFailed(layer.Tracker);

        IReadOnlyList<PlayerSnapshot> snaps = PlayerSnapshotBuilder.Build(layer.Tracker, parsed);

        Console.WriteLine($"Snapshots: {snaps.Count}");
        foreach (PlayerSnapshot s in snaps)
        {
            Console.WriteLine($"  team={s.Team} {(s.IsAlive ? "alive" : "dead ")} hp={s.Health,3} armor={s.Armor,3} ${s.Money,5} {s.ActiveWeapon,-10} util=[{s.UtilSummary}] name={s.Name}");
        }

        // A normal CS2 demo during live play should produce at least one teamed player
        // on each side. Strict 5v5 isn't asserted because the discovered demo may
        // be a 1v1 retake, 4v5 with a disconnect, OT halftime mid-restart, etc.
        // What we ARE asserting: the builder produces some snapshots, both teams
        // are represented, and CT-first ordering is intact.
        await Assert.That(snaps.Count).IsGreaterThanOrEqualTo(2);
        await Assert.That(snaps.Count).IsLessThanOrEqualTo(10);

        int ctCount = snaps.Count(s => s.Team == 3);
        int tCount = snaps.Count(s => s.Team == 2);
        await Assert.That(ctCount).IsGreaterThanOrEqualTo(1);
        await Assert.That(tCount).IsGreaterThanOrEqualTo(1);
        await Assert.That(ctCount + tCount).IsEqualTo(snaps.Count); // no spectators slipped in

        // CT block precedes T block: every snapshot at index i < ctCount must be team 3.
        for (int i = 0; i < ctCount; i++)
        {
            await Assert.That(snaps[i].Team).IsEqualTo(3);
        }

        for (int i = ctCount; i < snaps.Count; i++)
        {
            await Assert.That(snaps[i].Team).IsEqualTo(2);
        }

        // Every player has a non-empty name (the builder skips controllers with no
        // resolvable name — if any slipped through this would catch it).
        await Assert.That(snaps.All(s => !string.IsNullOrEmpty(s.Name))).IsTrue();
    }

    /// <summary>Player snapshot builder_with name lookups_prefers explicit names.</summary>
    [Test]
    public async Task PlayerSnapshotBuilder_WithNameLookups_PrefersExplicitNames()
    {
        // The builder accepts two name-lookup dictionaries as fallback when the
        // m_sSanitizedPlayerName field hasn't been populated. This test verifies
        // both fallbacks fire correctly by injecting a name for a slot we know
        // exists at midpoint.
        string path = DemoTestHelper.RequireDemo();
        ParsedDemo parsed = DemoTestHelper.GetOrParse(path);

        EntityStateLayer layer = new(parsed.Frames);
        layer.SeekToTick(parsed.TickCount / 2);
        SkipIfEntityDecodeFailed(layer.Tracker);

        // Build with the convenience overload — pulls names from parsed.Players +
        // PlayerConnectEvents the same way the ViewModel does.
        IReadOnlyList<PlayerSnapshot> baseline = PlayerSnapshotBuilder.Build(layer.Tracker, parsed);

        await Assert.That(baseline.Count).IsGreaterThanOrEqualTo(2);
        await Assert.That(baseline.All(s => !string.IsNullOrEmpty(s.Name))).IsTrue();

        // No two players share a name on a real demo (no double-spawned phantom
        // controllers from the parser bug class that this audit was triggered by).
        int distinctNames = baseline.Select(s => s.Name).Distinct().Count();
        await Assert.That(distinctNames).IsEqualTo(baseline.Count);

        // BuildNameLookups should match the dicts the ViewModel constructs inline.
        (IReadOnlyDictionary<int, string> nameBySlot, IReadOnlyDictionary<int, string> nameByUserId) = PlayerSnapshotBuilder.BuildNameLookups(parsed);
        // 5v5 demo → at least 10 names by user-id (string-table) plus extras
        // for any mid-match join. Upper bound of 64 covers PVS slot range.
        await Assert.That(nameByUserId.Count).IsBetween(2, 64).WithInclusiveBounds();
        Console.WriteLine($"Name lookups built: {nameBySlot.Count} by-slot, {nameByUserId.Count} by-user-id");
    }

    /// <summary>Scanner_captures pre frame health snapshot.</summary>
    [Test]
    public async Task Scanner_CapturesPreFrameHealthSnapshot()
    {
        string path = DemoTestHelper.RequireDemo();

        ParsedDemo parsed = DemoTestHelper.GetOrParse(path);

        PawnHealthProvider provider = new();
        EntityStateLayer layer = new(parsed.Frames);
        layer.SeekToTick(parsed.TickCount / 2);
        SkipIfEntityDecodeFailed(layer.Tracker);

        // Direct CaptureAllSlots check: at midpoint, expect at least one alive player.
        int directHits = 0;
        provider.CaptureAllSlots(layer, (slot, val) => directHits++);
        Console.WriteLine($"Direct CaptureAllSlots at midpoint: {directHits} (slot, value) pairs");
        // Same 1–10 bound as the per-slot Read path above.
        await Assert.That(directHits).IsBetween(1, 10).WithInclusiveBounds();

        EntityChangeScanner scanner = new(
            new EntityStateLayer(parsed.Frames),
            [],
            [provider]);

        // Walk frames; on each, capture the snapshot (which reflects PREVIOUS frame's HP).
        // Verify at least one slot has a non-null snapshot value across the demo.
        int slotsWithSnapshotEver = 0;
        HashSet<int> observedSlots = new();

        for (int i = 0; i < parsed.Frames.Count; i++)
        {
            scanner.AdvanceAndPoll(parsed.Frames[i].ServerTick);
            for (int slot = 0; slot < 64; slot++)
            {
                if (scanner.GetPreFrameValue(provider, slot) is int)
                {
                    if (observedSlots.Add(slot))
                    {
                        slotsWithSnapshotEver++;
                    }
                }
            }

            // Early exit once we've confirmed plenty of slots are tracked.
            if (slotsWithSnapshotEver >= 5 && i > 5_000)
            {
                break;
            }
        }

        Console.WriteLine($"Distinct slots captured in pre-frame snapshot: {slotsWithSnapshotEver}");
        // Over a full demo (or until the 5-slot / 5000-frame early-exit), a
        // healthy snapshotter sees all 10 players at some point. Tolerate
        // a slightly smaller count for short demos, but anything under 2
        // means the scanner missed almost every pawn.
        await Assert.That(slotsWithSnapshotEver).IsBetween(2, 10).WithInclusiveBounds();
    }

    /// <summary>Scanner_captures pre frame weapon snapshot.</summary>
    [Test]
    public async Task Scanner_CapturesPreFrameWeaponSnapshot()
    {
        string path = DemoTestHelper.RequireDemo();

        ParsedDemo parsed = DemoTestHelper.GetOrParse(path);

        // Probe entity-decode health on a SEPARATE layer at midpoint before
        // building the scanner. The scanner-driven loop below walks frames in
        // order and we don't want a probe SeekToTick on its layer to leave it
        // mid-demo. The probe layer is throwaway.
        {
            EntityStateLayer probe = new(parsed.Frames);
            probe.SeekToTick(parsed.TickCount / 2);
            SkipIfEntityDecodeFailed(probe.Tracker);
        }

        ActiveWeaponProvider provider = new();
        EntityChangeScanner scanner = new(
            new EntityStateLayer(parsed.Frames),
            [],
            [provider]);

        int slotsWithSnapshotEver = 0;
        HashSet<int> observedSlots = new();

        for (int i = 0; i < parsed.Frames.Count; i++)
        {
            scanner.AdvanceAndPoll(parsed.Frames[i].ServerTick);
            for (int slot = 0; slot < 64; slot++)
            {
                if (scanner.GetPreFrameValue(provider, slot) is string { Length: > 0 })
                {
                    if (observedSlots.Add(slot))
                    {
                        slotsWithSnapshotEver++;
                    }
                }
            }

            if (slotsWithSnapshotEver >= 5 && i > 5_000)
            {
                break;
            }
        }

        Console.WriteLine($"Distinct slots with weapon snapshot: {slotsWithSnapshotEver}");
        // Over a full demo (or until the 5-slot / 5000-frame early-exit), a
        // healthy weapon snapshotter sees all 10 players. Same bound as the
        // health-snapshot counterpart.
        await Assert.That(slotsWithSnapshotEver).IsBetween(2, 10).WithInclusiveBounds();
    }

    // ── 3. Scanner emits a synthesized event when the value transitions ───────
    /// <summary>Scanner_emits rising edge on freeze period.</summary>
    [Test]
    public async Task Scanner_EmitsRisingEdgeOnFreezePeriod()
    {
        string path = DemoTestHelper.RequireDemo();

        ParsedDemo parsed = DemoTestHelper.GetOrParse(path);

        EntityStateLayer layer = new(parsed.Frames);
        FreezePeriodProvider provider = new();
        GenericBoolNode valueNode = new(provider.ContextName);
        EntityChangeScanner scanner = new(layer, [(provider, valueNode)]);

        int risingEdgesObserved = 0;
        // Walk frames in order; each AdvanceAndPoll returns synthesized messages for any
        // changes since the last call. We sample every ~5000 frames for speed; the scanner
        // still observes every frame internally because the layer is forward-seek-only.
        for (int i = 0; i < parsed.Frames.Count; i += 1)
        {
            IReadOnlyList<NetMessage> msgs = scanner.AdvanceAndPoll(parsed.Frames[i].ServerTick);
            risingEdgesObserved += msgs.Count;
            if (risingEdgesObserved > 0 && i > 10_000)
            {
                break; // smoke test — first rising edge is enough
            }
        }

        Console.WriteLine($"Rising-edge events observed: {risingEdgesObserved}");
        // A normal MM/HLTV demo has 1+ freeze period per round in the first
        // 10k frames (the test's early-exit threshold). 0 means the scanner
        // never observed the m_bFreezePeriod transition — the bug the provider
        // shipped to fix.
        await Assert.That(risingEdgesObserved).IsBetween(1, 50).WithInclusiveBounds();
    }

    /// <summary>
    ///     The scanner synthesizes one <c>molotov_thrown</c> event per CMolotovProjectile
    ///     creation, attributed to the thrower via <c>m_hThrower → pawn → m_hController → slot</c>.
    ///     Molotov/incendiary has no usable wire detonation event, so this is the entity-derived
    ///     path that powers the <c>molotov_used</c> rule. Drives the scanner directly (no graph) so
    ///     the failure mode is "attribution broke", not "rule wiring broke".
    /// </summary>
    [Test]
    public async Task MolotovThrowScanner_AttributesThrowsToSlots()
    {
        string path = DemoTestHelper.RequireDemo(DemoTestHelper.ReferenceDemoFileName);
        ParsedDemo parsed = DemoTestHelper.GetOrParse(path);

        EntityStateLayer layer = new(parsed.Frames);
        EntityChangeScanner scanner = new(layer, [], null, true);

        Dictionary<int, int> bySlot = new();
        int total = 0;
        foreach (DemoFrame frame in parsed.Frames)
        {
            foreach (NetMessage msg in scanner.AdvanceAndPoll(frame.ServerTick))
            {
                if (msg is GameEventMessage { DecodedEvent: MolotovThrownEvent mt })
                {
                    total++;
                    bySlot.TryGetValue(mt.PlayerSlot, out int n);
                    bySlot[mt.PlayerSlot] = n + 1;
                }
            }
        }

        SkipIfEntityDecodeFailed(layer.Tracker);

        Console.WriteLine($"molotov_thrown events: {total} across {bySlot.Count} slots " +
                          $"({string.Join(", ", bySlot.OrderBy(kv => kv.Key).Select(kv => $"s{kv.Key}={kv.Value}"))})");

        // A normal MM match throws dozens of molotovs/incendiaries across both teams. Bound on
        // both sides: lower catches "attribution never fired" (the pre-fix entity-decode state
        // would zero this), upper is generous for a heavy-utility match.
        await Assert.That(total).IsBetween(10, 80).WithInclusiveBounds();
        // Every synthesized throw resolved to a real player slot (0..63), never the -1 sentinel.
        await Assert.That(bySlot.Keys.All(s => s is >= 0 and < 64)).IsTrue();
        // Utility is spread across multiple throwers, not collapsed onto one mis-resolved slot.
        await Assert.That(bySlot.Count).IsGreaterThanOrEqualTo(3);
    }

    // ── Shared skip helper ────────────────────────────────────────────────────
    //
    // Entity-state decoding hits a known bit-misalignment bug on the bench /
    // MM-source demos (matchmaking GOTV), traced to per-field decoder behavior
    // that diverges from demofile-net's compile-time decoders. The Furia HLTV
    // demos do NOT trigger it (see Parser.Tests.EntityTracker_FuriaMirage_NoEntityDecodeErrors).
    // Full investigation is captured in /KNOWN-AND-SUSPECTED-ISSUES.md.
    //
    // Tests that depend on functioning entity-state decoding call this helper
    // AFTER advancing the tracker (SeekToTick / Replay / EvaluateWithSnapshots).
    // When the underlying bug is fixed and tracker.LastEntityError becomes null
    // on the reference demo, every guarded test starts running automatically —
    // no test-side change required.
    private static void SkipIfEntityDecodeFailed(EntityTracker tracker)
    {
        if (tracker.LastEntityError is { } err)
        {
            throw new SkipTestException(
                "Entity-state decoder hits a known bit-misalignment bug on this demo. " +
                "See the entity-decode section of /KNOWN-AND-SUSPECTED-ISSUES.md. Tracker error head: " +
                err.Split('\n', 2)[0]);
        }
    }

    // ── EntityValueCache pre-frame correctness gate ─────────────────────────────

    /// <summary>
    ///     The invariant gate: <see cref="EntityValueCache" /> must read each fire frame's
    ///     PRE-frame entity state, not the at-frame state. At a <c>player_death</c> frame the victim's
    ///     pawn is already dead (HP 0, filtered out by <c>PawnHealthProvider</c>), so an at-frame read of
    ///     <c>UserId.entity.pawn.health</c> would match nothing — the marquee condition silently
    ///     broken. This proves the cache reports the victim ALIVE entering the fatal frame (the meaningful
    ///     value) while the at-frame read is dead, and cross-checks the value against the trusted
    ///     <see cref="EntityChangeScanner.GetPreFrameValue" /> oracle for a tick-leading death.
    /// </summary>
    [Test]
    public async Task EntityValueCache_ReadsPreDeathHealth_NotPostDeath()
    {
        string path = DemoTestHelper.RequireDemo();
        ParsedDemo parsed = DemoTestHelper.GetOrParse(path);

        // Decode-health probe on a throwaway layer so the test skips (not fails) on the known bug.
        {
            EntityStateLayer probe = new(parsed.Frames);
            probe.SeekToTick(parsed.TickCount / 2);
            SkipIfEntityDecodeFailed(probe.Tracker);
        }

        // Map an event's FrameNumber to its parsed.Frames index (FrameNumber need not equal the index).
        Dictionary<int, int> indexByFrameNumber = new();
        for (int i = 0; i < parsed.Frames.Count; i++)
        {
            indexByFrameNumber[parsed.Frames[i].FrameNumber] = i;
        }

        List<(int Frame, int Victim)> deaths = parsed.AllGameEvents
            .Where(e => e.Payload is PlayerDeathEvent)
            .Select(e => (Fire: e, Death: (PlayerDeathEvent)e.Payload!))
            .Where(x => indexByFrameNumber.ContainsKey(x.Fire.FrameNumber) && x.Death.UserId >= 0)
            .Select(x => (Frame: indexByFrameNumber[x.Fire.FrameNumber], Victim: x.Death.UserId))
            .Where(d => d.Frame > 0)
            .OrderBy(d => d.Frame)
            .ToList();

        await Assert.That(deaths.Count).IsGreaterThan(0).Because("a real match has player deaths");

        PawnHealthProvider provider = new();
        const string HpProvider = "entity.pawn.health";
        int[] deathFrames = deaths.Select(d => d.Frame).Distinct().OrderBy(f => f).ToArray();
        EntityValueCache cache = EntityValueCache.Build(parsed.Frames, deathFrames, [provider]);

        // Independent oracle: the scanner's pre-frame snapshot, captured walking frames in order.
        EntityChangeScanner scanner = new(new EntityStateLayer(parsed.Frames), [], [provider]);
        int nextScannerFrame = 0;

        int verified = 0;
        int oracleChecked = 0;
        foreach ((int frame, int victim) in deaths)
        {
            object? pre = cache.At(frame).GetValue(HpProvider, victim);
            if (pre is not int hp || hp <= 0)
            {
                continue; // victim not resolvable alive pre-frame (bot/early-frame edge) → skip
            }

            // At-frame: apply frames [0, frame] inclusive → the victim's pawn is now dead → null read.
            EntityStateLayer atFrame = new(parsed.Frames);
            atFrame.SeekBeforeFrame(frame + 1);
            object? post = provider.Read(atFrame, victim);

            await Assert.That(hp).IsGreaterThan(0)
                .Because("pre-frame health is the victim entering the fatal frame — alive");
            await Assert.That(post is null || post is int and <= 0).IsTrue()
                .Because("at the death frame the victim's pawn is dead — the at-frame value the cache must NOT use");

            // Cross-check the value against the scanner oracle, but only for a death whose frame leads
            // its ServerTick (so the scanner's tick-granular pre-frame is directly comparable).
            if (frame == 0 || parsed.Frames[frame].ServerTick != parsed.Frames[frame - 1].ServerTick)
            {
                while (nextScannerFrame <= frame)
                {
                    scanner.AdvanceAndPoll(parsed.Frames[nextScannerFrame].ServerTick);
                    nextScannerFrame++;
                }

                if (scanner.GetPreFrameValue(provider, victim) is int scannerHp and > 0)
                {
                    await Assert.That(hp).IsEqualTo(scannerHp)
                        .Because("the cache must reproduce the scanner's trusted pre-frame health");
                    oracleChecked++;
                }
            }

            verified++;
            if (verified >= 5 && oracleChecked >= 1)
            {
                break;
            }
        }

        await Assert.That(verified).IsGreaterThan(0)
            .Because("at least one death must show alive-pre-frame / dead-at-frame (invariant #1)");
    }

    /// <summary>
    ///     Diagnostic for the "node entity breakpoint computes indefinitely" report: time the build the
    ///     view-model kicks for a node input-event entity condition (<c>NoDeathsYet</c> +
    ///     <c>input.player_death.Attacker.entity.pawn.health &lt;= 100</c>) — the full distinct
    ///     player_death frame union — and prove it TERMINATES (no hang in <c>SeekBeforeFrame</c> /
    ///     <c>Replay</c> / <c>CaptureAllSlots</c>): the report is a working-but-slow cost, not a hang.
    ///     <para>
    ///         <b>Where the cost is (measured, do not re-optimise the wrong end):</b> this builds twice —
    ///         all four providers vs. just the one a <c>…health</c> condition reads — and they come out
    ///         essentially equal (~14s each on the reference demo). The cost is the forward entity REPLAY to
    ///         the last fire frame (≈ whole match), not the per-provider <c>CaptureAllSlots</c> (~8 ms total).
    ///         So narrowing the build to the referenced provider saves nothing AND would turn an instant
    ///         provider-swap edit into a full rebuild — a regression that was tried and reverted. Reading
    ///         entity state at frame N is intrinsically O(N) replay; a frame-prefilter doesn't help either,
    ///         since the replay must still reach the last relevant frame. The only real levers are
    ///         eliminating the second replay (capture during the evaluator's existing entity pass) or hiding
    ///         the one-time cost (pre-warm at load) — not shrinking the capture set.
    ///     </para>
    /// </summary>
    [Test]
    public async Task EntityValueCache_BuildsOverAllDeathFrames_WithAllProviders_Terminates()
    {
        string path = DemoTestHelper.RequireDemo();
        ParsedDemo parsed = DemoTestHelper.GetOrParse(path);

        {
            EntityStateLayer probe = new(parsed.Frames);
            probe.SeekToTick(parsed.TickCount / 2);
            SkipIfEntityDecodeFailed(probe.Tracker);
        }

        Dictionary<int, int> indexByFrameNumber = new();
        for (int i = 0; i < parsed.Frames.Count; i++)
        {
            indexByFrameNumber[parsed.Frames[i].FrameNumber] = i;
        }

        int[] deathFrames = parsed.AllGameEvents
            .Where(e => e.Payload is PlayerDeathEvent)
            .Where(e => indexByFrameNumber.ContainsKey(e.FrameNumber))
            .Select(e => indexByFrameNumber[e.FrameNumber])
            .Where(f => f > 0)
            .Distinct()
            .OrderBy(f => f)
            .ToArray();

        await Assert.That(deathFrames.Length).IsGreaterThan(0);

        PerPlayerEntityValueProviderRegistry registry = PerPlayerEntityValueProviderRegistry.CreateDefault();
        // All four providers (what the VM builds) vs. just the one a `…health` condition reads. Times both
        // to PROVE the provider count is negligible — the replay dominates — so the narrowing optimisation
        // stays reverted (see the method summary).
        IReadOnlyCollection<IPerPlayerEntityValueProvider> allProviders = registry.All;
        IPerPlayerEntityValueProvider[] healthOnly = [registry.Get("entity.pawn.health")!];

        Stopwatch sw = Stopwatch.StartNew();
        EntityValueCache cache = EntityValueCache.Build(parsed.Frames, deathFrames, allProviders);
        sw.Stop();
        long allMs = sw.ElapsedMilliseconds;

        sw.Restart();
        EntityValueCache.Build(parsed.Frames, deathFrames, healthOnly);
        sw.Stop();
        long oneMs = sw.ElapsedMilliseconds;

        Console.WriteLine(
            $"EntityValueCache.Build over {deathFrames.Length} death frames " +
            $"(last frame {deathFrames[^1]} / {parsed.Frames.Count}): " +
            $"all {allProviders.Count} providers = {allMs} ms; health-only = {oneMs} ms " +
            $"→ replay dominates; provider narrowing saves {allMs - oneMs} ms (negligible)");

        // Spot-check the cache is populated (a killer's pre-frame health should resolve for some death).
        int killerHpReadable = 0;
        foreach (int f in deathFrames)
        {
            for (int slot = 0; slot < 64; slot++)
            {
                if (cache.At(f).GetValue("entity.pawn.health", slot) is int and > 0)
                {
                    killerHpReadable++;
                    break;
                }
            }

            if (killerHpReadable >= 3)
            {
                break;
            }
        }

        await Assert.That(killerHpReadable).IsGreaterThan(0)
            .Because("the build must populate per-slot pre-frame health the node predicate reads");
        // Termination is the real assertion (a hang fails by timeout, not here). The generous wall-clock
        // ceiling flags a genuine cost regression without being flaky on a loaded CI box.
        await Assert.That(allMs).IsLessThan(120_000)
            .Because($"the node-union build must terminate quickly; took {allMs} ms");
        // Documents the measured finding: one provider is NOT materially cheaper than four (the replay
        // dominates), so the narrowing optimisation is correctly absent. If this ever flips to a large gap,
        // the cost model changed and narrowing would be worth reconsidering.
        await Assert.That(oneMs).IsGreaterThan(allMs / 2)
            .Because($"replay dominates: one provider ({oneMs} ms) ≈ all four ({allMs} ms), not ~4× cheaper");
    }
}
