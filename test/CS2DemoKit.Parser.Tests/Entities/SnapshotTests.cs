#region

using CS2OpenDev.Sdk.Entities;
using CS2DemoKit.Parser;
using CS2DemoKit.Parser.Entities.SdkAbstractions;
using CS2DemoKit.Parser.EntityTracking;
using CS2DemoKit.TestSupport;
using TUnit.Core.Exceptions;

#endregion

namespace CS2DemoKit.Parser.Entities.Tests;

/// <summary>
///     Tests for the frozen-snapshot machinery: <see cref="EntityState.FreezeCopy" /> (the
///     load-bearing primitive), <see cref="EntityTracker.Snapshot{T}" /> and
///     <see cref="EntityTracker.SnapshotNode" />. The returned wrapper / node is <b>frozen</b> —
///     it holds no live <see cref="EntityState" /> and does not change when the live entity is
///     later mutated. Typed snapshots dispatch through registered factories; since the SDK
///     cutover those are the SDK-emitted wrappers registered via
///     <see cref="TrackerEntityWorld" /> (the local generated layer and its nested-freeze
///     machinery were removed with the cutover).
/// </summary>
[Category("Unit")]
public class SnapshotTests
{
    // ── EntityState.FreezeCopy detachment (pure, no demo) ─────────────────────

    /// <summary>
    ///     A frozen copy of an <see cref="EntityState" /> is fully detached: mutating the
    ///     original's lanes / fallback after the freeze does not change the copy. This is the
    ///     load-bearing primitive behind <see cref="EntityTracker.Snapshot{T}" />.
    /// </summary>
    [Test]
    [Category("Smoke")]
    public async Task FreezeCopy_IsDetachedFromLaterMutation()
    {
        // A minimal shape: one int slot (Health-like), one object slot (a handle-like).
        ClassShape shape = new(
            "CTestEntity",
            new Dictionary<string, SlotAddr>
            {
                ["m_iHealth"] = new(LaneKind.Int, 0),
                ["m_hThing"] = new(LaneKind.Object, 0)
            },
            ["m_iHealth"],
            [],
            ["m_hThing"]);

        EntityState live = new("CTestEntity", 7);
        live.BindShape(shape);
        live.SetIntSlot(0, 100);
        live.SetObjectSlot(0, 1234UL);
        live.SetFallback("m_szName", "before");

        EntityState frozen = live.FreezeCopy();

        // Mutate the live state AFTER the freeze.
        live.SetIntSlot(0, 5);
        live.SetObjectSlot(0, 9999UL);
        live.SetFallback("m_szName", "after");

        // The frozen copy must still report the pre-mutation values.
        await Assert.That(frozen["m_iHealth"]).IsEqualTo(100);
        await Assert.That(frozen["m_hThing"]).IsEqualTo(1234UL);
        await Assert.That(frozen["m_szName"]).IsEqualTo("before");

        // Sanity: the live state did move.
        await Assert.That(live["m_iHealth"]).IsEqualTo(5);
        await Assert.That(live["m_szName"]).IsEqualTo("after");
    }

    /// <summary>
    ///     The frozen <see cref="EntityState.Fields" /> projection preserves the <c>_seen</c>
    ///     distinction: an unwritten slot stays absent from the copy, exactly as on the live
    ///     state.
    /// </summary>
    [Test]
    public async Task FreezeCopy_PreservesSeenSemantics()
    {
        ClassShape shape = new(
            "CTestEntity",
            new Dictionary<string, SlotAddr>
            {
                ["m_iSeen"] = new(LaneKind.Int, 0),
                ["m_iUnseen"] = new(LaneKind.Int, 1)
            },
            ["m_iSeen", "m_iUnseen"],
            [],
            []);

        EntityState live = new("CTestEntity", 1);
        live.BindShape(shape);
        live.SetIntSlot(0, 0); // received default-0
        // slot 1 never written.

        EntityState frozen = live.FreezeCopy();

        await Assert.That(frozen.Fields.ContainsKey("m_iSeen")).IsTrue();
        await Assert.That(frozen.Fields.ContainsKey("m_iUnseen")).IsFalse();
    }

    // ── Snapshot<T> over a live replay (demo-backed) ──────────────────────────

    /// <summary>
    ///     <see cref="EntityTracker.Snapshot{T}" /> returns a typed wrapper whose scalar getter
    ///     equals the live wrapper at snapshot time AND stays stable after the live tracker is
    ///     re-seeked to a different tick (detachment). <see cref="EntityTracker.SnapshotNode" />
    ///     returns a generic frozen node carrying the entity's <c>ClassName</c> + a clone of the
    ///     <c>Fields</c> projection. Typed dispatch goes through SDK-wrapper factories
    ///     registered via <see cref="TrackerEntityWorld" /> — the factory hands the wrapper a
    ///     reader over the FROZEN state, which is what makes the typed snapshot frozen too.
    /// </summary>
    [Test]
    [Category("Integration")]
    [NotInParallel]
    public async Task SnapshotPawn_IsFrozenAndStableAcrossReseek()
    {
        string? demoPath = DemoTestHelper.FindDemoPath();
        if (demoPath is null)
        {
            throw new SkipTestException("No demo found — skipping snapshot freeze test.");
        }

        byte[] bytes = await File.ReadAllBytesAsync(demoPath);
        ParsedDemo parsed = DemoParser.Parse(bytes.AsMemory());

        EntityTracker tracker = EntityTrackerFactory.CreateCurated();
        TrackerEntityWorld world = new(tracker);
        world.RegisterWrapper(
            EntityWrapperRegistry.Bindings.Single(b => b.EngineClass == "CCSPlayerPawn"),
            (r, w) => EntityWrapperRegistry.Create("CCSPlayerPawn", r, w)!);

        // Seek to a mid-demo frame so pawns are populated.
        int midFrame = parsed.Frames.Count / 2;
        tracker.AdvanceToIndex(midFrame, parsed.Frames);

        (int slot, EntityState? pawn) = FirstSlotOfClass(tracker, "CCSPlayerPawn");
        if (pawn is null)
        {
            throw new SkipTestException("No CCSPlayerPawn populated at mid-demo — skipping.");
        }

        // Snapshot the pawn as a typed frozen wrapper.
        CSPlayerPawn? frozenWrapper = tracker.Snapshot<CSPlayerPawn>(slot);
        await Assert.That(frozenWrapper).IsNotNull();

        // The live wrapper at the same instant agrees with the frozen one.
        CSPlayerPawn? liveWrapper = tracker.Get<CSPlayerPawn>(slot);
        await Assert.That(liveWrapper).IsNotNull();
        int frozenHealthAtSnapshot = frozenWrapper!.Health;
        await Assert.That(frozenHealthAtSnapshot).IsEqualTo(liveWrapper!.Health);

        // Generic-node form carries ClassName + a Fields clone. (The nested-freeze tree and
        // its ISnapshotable machinery were removed outright in the cutover cleanup — the only
        // producers were the retired local wrappers' SnapshotInto overrides.)
        EntitySnapshot? node = tracker.SnapshotNode(slot);
        await Assert.That(node).IsNotNull();
        await Assert.That(node!.ClassName).IsEqualTo("CCSPlayerPawn");
        // The node's Fields clone matches the live projection at snapshot time.
        bool nodeHadHealth = node.Fields.TryGetValue("m_iHealth", out object? nodeHpAtSnapshot);
        if (pawn.Fields.TryGetValue("m_iHealth", out object? liveHp))
        {
            await Assert.That(nodeHadHealth).IsTrue();
            await Assert.That(nodeHpAtSnapshot).IsEqualTo(liveHp);
        }

        // Re-seek the live tracker to the very end of the demo. The frozen wrapper +
        // node must NOT change (no live aliasing), even though the live entity's
        // fields may have moved.
        tracker.AdvanceToIndex(parsed.Frames.Count - 1, parsed.Frames);

        await Assert.That(frozenWrapper.Health).IsEqualTo(frozenHealthAtSnapshot);
        // The node's captured value is the snapshot-time clone, unchanged by the re-seek.
        if (nodeHadHealth)
        {
            await Assert.That(node.Fields.TryGetValue("m_iHealth", out object? stillHp)).IsTrue();
            await Assert.That(stillHp).IsEqualTo(nodeHpAtSnapshot);
        }
    }

    private static (int Slot, EntityState? Entity) FirstSlotOfClass(EntityTracker tracker, string className)
    {
        foreach ((int index, EntityState entity) in tracker.CurrentEntities.AllIndexed())
        {
            if (string.Equals(entity.ClassName, className, StringComparison.Ordinal))
            {
                return (index, entity);
            }
        }

        return (-1, null);
    }
}
