#region

using CS2DemoKit.Parser.EntityTracking;

#endregion

namespace CS2DemoKit.Parser.Entities.Tests;

/// <summary>
///     Contract test for the codegen-emitted <c>LensSlot</c> path through
///     <see cref="ClassShapeBuilder.Allocate" />. When the Lens rule supplies a
///     codegen-pinned slot index (>= 0), the allocator must place the field at
///     exactly that slot regardless of the auto-increment counter, and the next
///     auto-increment allocation must skip past the codegen-pinned slot rather
///     than collide with it.
///     <para>
///         This is the runtime side of the codegen-runtime slot-allocation
///         contract — the codegen-engineer's wrappers index into lanes with
///         compile-time slot constants, so the runtime allocator must honour
///         those constants for the wrappers to read the right value.
///     </para>
///     <para>
///         <see cref="ClassShapeBuilder" /> and its <c>Allocate</c> method are
///         <c>internal</c> in <c>CS2DemoKit.Parser.EntityTracking</c>; this
///         test project gets visibility via the <c>InternalsVisibleTo</c> grant
///         in that project's <c>AssemblyInfo.cs</c>.
///     </para>
/// </summary>
[Category("Unit")]
public class LensSlotHonoringTests
{
    [Test]
    public async Task ClassShapeBuilder_HonorsCodegenLensSlot()
    {
        ClassShapeBuilder builder = new("CCSPlayerPawn");

        // Codegen-pinned slot 42 for m_iHealth.
        SlotAddr pinned = builder.Allocate(LaneKind.Int, "m_iHealth", lensSlot: 42);
        await Assert.That(pinned.Lane).IsEqualTo(LaneKind.Int);
        await Assert.That(pinned.Slot).IsEqualTo(42);

        // Subsequent auto-increment allocation must NOT reuse slot 42 — it must
        // skip past the pinned slot. Today's behaviour without the fix would
        // hand out slot 1 (the previous slot count), colliding with slot 42 only
        // once a second pinned allocation hits 1 — making the bug latent. The
        // fix grows the parallel arrays past the pinned index so the next
        // auto-increment returns slot 43.
        SlotAddr autoNext = builder.Allocate(LaneKind.Int, "m_iArmor");
        await Assert.That(autoNext.Lane).IsEqualTo(LaneKind.Int);
        await Assert.That(autoNext.Slot).IsEqualTo(43);

        // Build the final shape and assert the PathToSlot map matches.
        ClassShape shape = builder.Build();
        await Assert.That(shape.PathToSlot["m_iHealth"]).IsEqualTo(new SlotAddr(LaneKind.Int, 42));
        await Assert.That(shape.PathToSlot["m_iArmor"]).IsEqualTo(new SlotAddr(LaneKind.Int, 43));
        // IntSlotPaths must be size >= 44 (indices 0..43 addressable).
        await Assert.That(shape.IntSlotPaths.Length).IsGreaterThanOrEqualTo(44);
        await Assert.That(shape.IntSlotPaths[42]).IsEqualTo("m_iHealth");
        await Assert.That(shape.IntSlotPaths[43]).IsEqualTo("m_iArmor");
    }

    [Test]
    public async Task ClassShapeBuilder_RejectsCodegenSlotCollision()
    {
        ClassShapeBuilder builder = new("CCSPlayerPawn");
        _ = builder.Allocate(LaneKind.Int, "m_iHealth", lensSlot: 5);

        // Second rule attempting the same (class, lane, slot) — must throw.
        InvalidOperationException? ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
        {
            builder.Allocate(LaneKind.Int, "m_iArmor", lensSlot: 5);
            return Task.CompletedTask;
        });
        await Assert.That(ex).IsNotNull();
        await Assert.That(ex!.Message).Contains("slot 5");
        await Assert.That(ex.Message).Contains("CCSPlayerPawn");
    }
}
