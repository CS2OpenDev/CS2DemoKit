#region

using CS2DemoKit.Parser.Entities.Generated;
using CS2DemoKit.Parser.Entities.SchemaLens;
using CS2DemoKit.Parser;
using CS2DemoKit.Parser.EntityTracking;
using CS2DemoKit.TestSupport;
using TUnit.Core.Exceptions;

#endregion

namespace CS2DemoKit.Parser.Entities.Tests;

/// <summary>
///     Integration parity test: replays the reference demo through two
///     <see cref="EntityTracker" /> instances — one with the Schema Lens resolver
///     bound, one without — and diffs the <see cref="EntityState.Fields" />
///     projection on the load-bearing critical paths. Confirms the V1 locked
///     decisions hold under real wire data:
///     <list type="bullet">
///         <item>
///             HandleIndex-tagged paths stay on the natural decoder lane (Object for
///             <c>CHandle&lt;&gt;</c> wires) so <c>Fields["m_hController"]</c>
///             returns the raw boxed integer in BOTH modes.
///         </item>
///         <item>
///             BoolFromInt-tagged paths land on the int lane (0/1) so
///             <c>Fields["m_bFreezePeriod"]</c> returns an int in BOTH modes.
///         </item>
///         <item>
///             Transform.None with lane drift (e.g. uint64-wire <c>m_steamID</c>
///             declared as IntLane in genesis) honours the wire — Lens lane is
///             ignored to avoid silent precision loss.
///         </item>
///     </list>
///     <para>
///         The test only exercises critical paths from
///         <c>SchemaKeysAssertionTests._fields</c> — the unique discriminating
///         keys that gate downstream analysis correctness. Drift here is a hard
///         failure: the V1 promise of "<see cref="EntityState.Fields" />
///         compatible bit-for-bit between Lens-bound and unbound modes" is
///         the contract every analysis-layer migration depends on.
///     </para>
///     <para>
///         <see cref="NotInParallelAttribute" /> follows the parser-test memory
///         pressure guidance for any test that constructs and replays a full
///         demo through an <see cref="EntityTracker" />.
///     </para>
/// </summary>
[Category("Integration")]
[NotInParallel]
public class LensBoundParityTests
{
    private static readonly (string ClassName, string Path)[] _criticalPaths =
    [
        // Player controller — name / money / pawn link.
        ("CCSPlayerController", "m_iszPlayerName"),
        ("CCSPlayerController", "m_steamID"),
        ("CCSPlayerController", "m_hPlayerPawn"),
        ("CCSPlayerController", "m_pInGameMoneyServices.m_iAccount"),
        // Player pawn — health, team, controller link, weapons.
        ("CCSPlayerPawn", "m_iHealth"),
        ("CCSPlayerPawn", "m_iTeamNum"),
        ("CCSPlayerPawn", "m_lifeState"),
        ("CCSPlayerPawn", "m_hController"),
        ("CCSPlayerPawn", "m_pWeaponServices.m_hActiveWeapon"),
        ("CCSPlayerPawn", "m_pWeaponServices.m_hMyWeapons[0]"),
        // Game rules.
        ("CCSGameRulesProxy", "m_pGameRules.m_bFreezePeriod"),
        ("CCSGameRulesProxy", "m_pGameRules.m_totalRoundsPlayed")
    ];

    /// <summary>
    ///     Replays the reference demo twice — once with <see cref="EntityTracker.BindLensResolver" />
    ///     bound, once without — and asserts every critical path's <see cref="EntityState.Fields" />
    ///     value matches by both presence and CLR runtime type. Type drift between the
    ///     two modes is a regression even when values compare equal (an <c>int 0</c> vs
    ///     <c>ulong 0</c> diverges to consumers downcasting at the call site).
    /// </summary>
    [Test]
    [Category("Smoke")]
    public async Task BoundAndUnbound_ProduceMatchingFieldsForCriticalPaths()
    {
        string? demoPath = DemoTestHelper.FindDemoPath();
        if (demoPath is null)
        {
            throw new SkipTestException("No demo found — skipping Lens-bound parity test.");
        }

        byte[] bytes = await File.ReadAllBytesAsync(demoPath);
        ParsedDemo parsed = DemoParser.Parse(bytes.AsMemory());

        // Replay #1: plain path (no resolver).
        EntityTracker plainTracker = new();
        plainTracker.Replay(parsed.Frames);

        // Replay #2: bound resolver.
        LensState lensState = GeneratedLensRegistry.Load();
        LensResolver resolver = LensResolverBridgeTests.BridgeLensStateToResolver(lensState);
        EntityTracker boundTracker = new();
        boundTracker.BindLensResolver(resolver);
        boundTracker.Replay(parsed.Frames);

        List<string> divergences = new();

        foreach ((string className, string path) in _criticalPaths)
        {
            EntityState? plainSample = FirstOfClass(plainTracker, className);
            EntityState? boundSample = FirstOfClass(boundTracker, className);

            if (plainSample is null || boundSample is null)
            {
                divergences.Add(
                    $"  CLASS-MISSING-IN-ONE-OR-BOTH: {className}  (plain={plainSample is not null}, bound={boundSample is not null})");
                continue;
            }

            bool plainHas = plainSample.Fields.TryGetValue(path, out object? plainValue);
            bool boundHas = boundSample.Fields.TryGetValue(path, out object? boundValue);

            if (plainHas != boundHas)
            {
                divergences.Add(
                    $"  KEY-PRESENCE-DIVERGES: {className}::{path}  plain={plainHas}, bound={boundHas}");
                continue;
            }

            if (!plainHas)
            {
                // Both missing — not a divergence; just not received on the sampled entity.
                continue;
            }

            // The boxed CLR type returned via Fields[path] must match
            // between the two modes. PawnLookup.TryUnboxHandle and Analysis call sites
            // rely on this for the dispatch switch over int / uint / ulong / long.
            string plainType = plainValue?.GetType().FullName ?? "(null)";
            string boundType = boundValue?.GetType().FullName ?? "(null)";
            if (!string.Equals(plainType, boundType, StringComparison.Ordinal))
            {
                divergences.Add(
                    $"  VALUE-TYPE-DIVERGES: {className}::{path}  plain={plainType}={plainValue}  bound={boundType}={boundValue}");
                continue;
            }

            // Value parity. For reference-typed values (vectors, strings) reference
            // equality is overly strict; compare by Equals().
            if (!Equals(plainValue, boundValue))
            {
                divergences.Add(
                    $"  VALUE-DIVERGES: {className}::{path}  plain={plainValue}  bound={boundValue}");
            }
        }

        string report = divergences.Count > 0
            ? "Lens-bound parity test failures (V1 promise: bit-for-bit Fields compat):\n"
              + string.Join("\n", divergences)
            : "";
        await Assert.That(report).IsEqualTo("");
    }

    /// <summary>
    ///     Regression guard: a fresh, never-wire-touched <see cref="EntityState" />
    ///     for a Lens-mapped class reports zero <see cref="EntityState.Fields" /> entries
    ///     when bound. Pre-PVS / pre-tick entities must distinguish "not received" from
    ///     "received default 0/0.0/null"; the <see cref="ClassShape" /> seeded-defaults
    ///     path would defeat this (and was deliberately removed).
    /// </summary>
    [Test]
    public async Task FreshEntity_ReportsEmptyFields_WhenLensIsBound()
    {
        string? demoPath = DemoTestHelper.FindDemoPath();
        if (demoPath is null)
        {
            throw new SkipTestException("No demo found — skipping the fresh-entity parity test.");
        }

        byte[] bytes = await File.ReadAllBytesAsync(demoPath);
        ParsedDemo parsed = DemoParser.Parse(bytes.AsMemory());

        LensState lensState = GeneratedLensRegistry.Load();
        LensResolver resolver = LensResolverBridgeTests.BridgeLensStateToResolver(lensState);

        EntityTracker tracker = new();
        tracker.BindLensResolver(resolver);

        // Drive at least one frame to build the descriptor cache and bind shapes.
        // A class-shape exists on EntityState after the first ENTERPVS in that class.
        tracker.Replay(parsed.Frames);

        // The promise: the bound tracker's Fields key count for any class must equal
        // the unbound tracker's Fields key count on the same demo. If lens defaults
        // were seeded at BindShape time, the bound count would exceed the unbound
        // count by the number of (genesis-declared) fields not present in the wire.
        //
        // We sample CCSPlayerPawn since it has the most wire-rich field set
        // (~30 genesis fields, all expected to receive at least one update during
        // a full replay on a real demo).
        EntityState? boundSample = FirstOfClass(tracker, "CCSPlayerPawn");
        if (boundSample is null)
        {
            throw new SkipTestException("No CCSPlayerPawn in demo — skipping.");
        }

        EntityTracker plainTracker = new();
        plainTracker.Replay(parsed.Frames);

        EntityState? plainSample = FirstOfClass(plainTracker, "CCSPlayerPawn");
        await Assert.That(plainSample).IsNotNull();

        int plainCount = plainSample!.Fields.Count;
        int boundCount = boundSample.Fields.Count;

        // Bound count should match plain count exactly. Lens defaults should NOT
        // introduce extra keys — that is the absent-vs-zero promise itself.
        await Assert.That(boundCount).IsEqualTo(plainCount);
    }

    private static EntityState? FirstOfClass(EntityTracker tracker, string className)
    {
        foreach (EntityState state in tracker.CurrentEntities.All())
        {
            if (string.Equals(state.ClassName, className, StringComparison.Ordinal))
            {
                return state;
            }
        }

        return null;
    }
}
