#region

using System.Diagnostics;
using CS2OpenDev.Sdk.Entities;
using CS2DemoKit.Parser.Entities.SchemaLens;
using CS2DemoKit.Parser.Entities.SdkAbstractions;
using CS2DemoKit.Parser.EntityTracking;
using CS2DemoKit.TestSupport;
using TUnit.Core.Exceptions;

#endregion

namespace CS2DemoKit.Parser.SdkAbstractions.Tests;

/// <summary>
///     The battery's perf stage: prices the SDK seam on real data, so a new
///     <c>CS2OpenDev.Sdk.Entities</c> emit that regresses the read path fails here instead of
///     surfacing as an unexplained AnalysisBench drift. One Lens-bound replay to the 50%
///     checkpoint, then four read lanes are timed over the live pawns — the direct typed-lane
///     read (the floor every consumer could hand-write), the wrapper typed read, the wrapper
///     seen-aware nullable read, and the production position reconstruction — plus wrapper
///     bind and companion resolve, which allocate by design and get byte ceilings instead.
///     <para>
///     The pins are TRIPWIRES, not specs: wide headroom over values measured 2026-08-15 on M1
///     (Apple M2 Pro, Release). Re-pin deliberately from a fresh measured run when the SDK or
///     the seam changes shape — never loosen them to make a red run pass. Timing ratios are
///     only asserted in Release builds (Debug codegen distorts them); allocation pins hold in
///     both configurations. Skips gracefully without a demo.
///     </para>
/// </summary>
[Category("Integration")]
[NotInParallel]
public class EmittedWrappersPerfTests
{
    // ── Tripwire pins (see class doc) ─────────────────────────────────────────
    // Calibrated from the 2026-08-15 Release/M1 run (Sdk.Entities 1.1.0, 10 live pawns):
    //   direct 8.3 ns · Health 17.6 ns (2.1×) · LifeState 20.4 ns (2.5×) — all 0 B/op;
    //   bind 69 ns / 64 B · resolve 145 ns / 64 B. Pins sit ~3–4× above those ratios.
    private const double MaxTypedReadMultiple = 8;     // wrapper Health vs direct TryGetIntSlot
    private const double MaxNullableReadMultiple = 8;  // wrapper LifeState vs direct
    private const double MaxDirectReadNs = 50;         // sanity floor: the lane read itself
    private const double MaxZeroLaneBytesPerOp = 1.0;  // "zero-alloc" lanes, amortized slack
    private const double MaxBindBytesPerOp = 256;      // reader + wrapper per bind (meas. 64)
    private const double MaxResolveBytesPerOp = 256;   // companion wrapper per resolve (meas. 64)

    private const int ReadOps = 200_000;
    private const int BindOps = 20_000;
    private const int Batches = 7;

    private static long _sink;

    private readonly record struct PerfSample(double NsPerOp, double BytesPerOp);

    /// <summary>
    ///     Median-of-batches ns/op plus amortized bytes/op for one read lane. Synchronous on
    ///     purpose — <see cref="GC.GetAllocatedBytesForCurrentThread" /> is only meaningful
    ///     with no awaits inside the window. The delegate indirection (~1–2 ns) rides on every
    ///     lane equally, so ratios compress slightly toward 1 — conservative for a tripwire.
    /// </summary>
    private static PerfSample Measure(int ops, Action body)
    {
        for (int i = 0; i < ops / 10; i++)
        {
            body(); // JIT + lazy-init warmup, outside both windows
        }

        double nsPerTick = 1_000_000_000.0 / Stopwatch.Frequency;
        double[] perBatch = new double[Batches];
        long allocBefore = GC.GetAllocatedBytesForCurrentThread();
        for (int b = 0; b < Batches; b++)
        {
            long t0 = Stopwatch.GetTimestamp();
            for (int i = 0; i < ops; i++)
            {
                body();
            }

            perBatch[b] = (Stopwatch.GetTimestamp() - t0) * nsPerTick / ops;
        }

        long allocAfter = GC.GetAllocatedBytesForCurrentThread();
        Array.Sort(perBatch);
        return new PerfSample(perBatch[Batches / 2], (allocAfter - allocBefore) / (double)(Batches * ops));
    }

    [Test]
    public async Task SeamReadCosts_StayWithinPinnedTripwires_OnARealDemo()
    {
        string? demoPath = DemoTestHelper.FindDemoPath();
        if (demoPath is null)
        {
            throw new SkipTestException("No demo found — skipping the battery's perf stage.");
        }

        // ── Stage-3-shaped setup: one parse, one Lens-bound replay to the 50% mark ─
        byte[] bytes = await File.ReadAllBytesAsync(demoPath);
        ParsedDemo parsed = DemoParser.Parse(bytes.AsMemory());

        EntityTracker tracker = new();
        tracker.BindLensResolver(LensResolverBridge.Build(Entities.Generated.GeneratedLensRegistry.Load()));

        TrackerEntityWorld world = new(tracker);
        foreach (EntityClassBinding b in EntityWrapperRegistry.Bindings)
        {
            EntityState probe = new(b.EngineClass, serial: 0);
            if (EntityWrapperRegistry.Create(b.EngineClass, new LensBoundReader(probe, b), world) is null)
            {
                continue; // the two abstract bases — measured and asserted in stage 2
            }

            string engineClass = b.EngineClass;
            world.RegisterWrapper(b, (r, w) => EntityWrapperRegistry.Create(engineClass, r, w)!);
        }

        int checkpoint = parsed.Frames.Count / 2;
        for (int i = 0; i <= checkpoint; i++)
        {
            tracker.AdvanceOneFrame(parsed.Frames[i]);
        }

        EntityClassBinding pawnBinding = EntityWrapperRegistry.Bindings
            .Single(b => b.EngineClass == "CCSPlayerPawn");
        EntityState[] pawns = tracker.CurrentEntities.OfClass("CCSPlayerPawn")
            .Where(s => s.Fields.ContainsKey("m_iHealth"))
            .ToArray();
        if (pawns.Length == 0)
        {
            throw new SkipTestException("No live pawn with m_iHealth at the 50% checkpoint — perf stage skipped.");
        }

        CSPlayerPawn[] wrappers = pawns
            .Select(s => (CSPlayerPawn)EntityWrapperRegistry.Create(
                "CCSPlayerPawn", world.CreateReader(pawnBinding, s), world)!)
            .ToArray();

        // Pre-resolve the direct-lane address exactly the way a hand-tuned consumer would.
        if (pawns[0].Shape is not { } shape
            || !shape.PathToSlot.TryGetValue("m_iHealth", out SlotAddr healthAddr)
            || healthAddr.Lane != LaneKind.Int)
        {
            throw new SkipTestException("m_iHealth is not Lens-mapped to the int lane — perf floor unavailable.");
        }

        int n = pawns.Length;

        // ── The lanes. Every body rotates its pawn and pays the same % + delegate tax. ─
        int j0 = 0;
        PerfSample direct = Measure(ReadOps, () =>
        {
            EntityState s = pawns[j0++ % n];
            if (s.TryGetIntSlot(healthAddr.Slot, out int v))
            {
                _sink += v;
            }
        });

        int j1 = 0;
        PerfSample wrapperHealth = Measure(ReadOps, () => _sink += wrappers[j1++ % n].Health);

        int j2 = 0;
        PerfSample wrapperLifeState = Measure(ReadOps, () =>
            _sink += wrappers[j2++ % n].LifeState is { } ls ? ls : -1);

        int j3 = 0;
        PerfSample cellPosition = Measure(ReadOps, () =>
            _sink += PositionUtil.CellToWorldVector(pawns[j3++ % n]) is null ? 0 : 1);

        int j4 = 0;
        PerfSample bind = Measure(BindOps, () =>
        {
            EntityState s = pawns[j4++ % n];
            _sink += EntityWrapperRegistry.Create(
                "CCSPlayerPawn", world.CreateReader(pawnBinding, s), world) is null ? 0 : 1;
        });

        // Companion resolve wants a pawn whose ActiveWeapon target is live + curated;
        // without one this lane reports n/a instead of failing the stage.
        CSPlayerPawn? resolveHolder = wrappers.FirstOrDefault(w =>
            w.ActiveWeaponHandle is not (0u or 0xFFFF_FFFFu) && w.ActiveWeapon is not null);
        PerfSample? resolve = resolveHolder is null
            ? null
            : Measure(BindOps, () => _sink += resolveHolder.ActiveWeapon is null ? 0 : 1);

        // ── Report (battery style — these numbers feed the round's upstream report) ─
        Console.WriteLine($"── Seam read costs @ frame {checkpoint}, {n} live pawns ──");
        Console.WriteLine($"  {"lane",-28} {"ns/op",8} {"B/op",8} {"vs direct",10}");
        void Row(string lane, PerfSample? s) => Console.WriteLine(s is { } v
            ? $"  {lane,-28} {v.NsPerOp,8:F1} {v.BytesPerOp,8:F2} {v.NsPerOp / direct.NsPerOp,9:F1}×"
            : $"  {lane,-28} {"n/a",8}");
        Row("direct TryGetIntSlot", direct);
        Row("wrapper Health", wrapperHealth);
        Row("wrapper LifeState (null.)", wrapperLifeState);
        Row("CellToWorldVector", cellPosition);
        Row("bind (reader + Create)", bind);
        Row("resolve ActiveWeapon", resolve);
        Console.WriteLine($"  sink={_sink}"); // defeats dead-code elimination; value meaningless

        // ── Allocation tripwires (hold in Debug and Release) ──────────────────
        await Assert.That(direct.BytesPerOp).IsLessThan(MaxZeroLaneBytesPerOp);
        await Assert.That(wrapperHealth.BytesPerOp).IsLessThan(MaxZeroLaneBytesPerOp);
        await Assert.That(wrapperLifeState.BytesPerOp).IsLessThan(MaxZeroLaneBytesPerOp);
        await Assert.That(cellPosition.BytesPerOp).IsLessThan(MaxZeroLaneBytesPerOp);
        await Assert.That(bind.BytesPerOp).IsLessThan(MaxBindBytesPerOp);
        if (resolve is { } r)
        {
            await Assert.That(r.BytesPerOp).IsLessThan(MaxResolveBytesPerOp);
        }

#if !DEBUG
        // ── Timing tripwires (Release only — Debug codegen distorts the ratios) ─
        await Assert.That(direct.NsPerOp).IsLessThan(MaxDirectReadNs);
        await Assert.That(wrapperHealth.NsPerOp).IsLessThan(direct.NsPerOp * MaxTypedReadMultiple);
        await Assert.That(wrapperLifeState.NsPerOp).IsLessThan(direct.NsPerOp * MaxNullableReadMultiple);
#endif
    }
}
