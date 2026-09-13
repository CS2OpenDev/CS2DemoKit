#region

using CS2DemoKit.Parser.EntityTracking;

#endregion

namespace CS2DemoKit.Parser.Tests.EntityTracking;

/// <summary>
///     The range contract of <see cref="EntityState.TryGetIntSlot" />,
///     <see cref="EntityState.TryGetFloatSlot" /> and <see cref="EntityState.TryGetObjectSlot" />:
///     a slot outside the bound shape's lane reads as absent, it does not throw.
///     <para>
///         These three went from <c>internal</c> to <c>public</c> alongside
///         <see cref="EntityState.Shape" />, so <c>CS2DemoKit.Parser</c> now offers slot-indexed
///         reads to anyone resolving a path through <see cref="ClassShape.PathToSlot" />. Reachable
///         only internally they could assume a slot the shape itself produced; published, they can
///         be handed a slot resolved against a DIFFERENT class's shape, a -1 "not mapped" sentinel,
///         or a slot from a shape the demo's schema no longer allocates that wide. The lane read
///         bottoms out in a <c>_seen</c> bitvector index — <c>slot &gt;&gt;&gt; 6</c> turns -1 into
///         67,108,863 — so before the guard those all produced an
///         <see cref="IndexOutOfRangeException" />, which is not a failure a caller of a
///         <c>Try</c> method has any reason to catch.
///     </para>
///     <para>
///         Both flavours of out-of-range are covered because they failed differently: a slot past
///         the lane but inside the <c>_seen</c> word (the bitvector is allocated in whole 64-bit
///         words, so a one-slot lane carries 64 bits) already answered <c>false</c> on a clear bit,
///         while a slot past the word count threw. Only the second was a crash, and only the first
///         will keep working if the guard is ever removed — so a test for one is not a test for the
///         other.
///     </para>
/// </summary>
[Category("Unit")]
public class EntityStateSlotBoundsTests
{
    /// <summary>
    ///     Exactly one slot in each lane, so slot 0 is valid everywhere, slot 1 is past the lane on
    ///     every lane, and slot 64 is past the <c>_seen</c> bitvector's single word on every lane.
    /// </summary>
    private static EntityState OneSlotPerLane()
    {
        ClassShapeBuilder shape = new("CTestProjectile");
        shape.Allocate(LaneKind.Int, "m_iTestInt");
        shape.Allocate(LaneKind.Float, "m_flTestFloat");
        shape.Allocate(LaneKind.Object, "m_vecTestObject");

        EntityState state = new("CTestProjectile", serial: 1);
        state.BindShape(shape.Build());
        return state;
    }

    [Test]
    public async Task NegativeSlot_ReadsAsAbsent_OnEveryLane()
    {
        EntityState state = OneSlotPerLane();

        foreach (int slot in (int[]) [-1, -64, int.MinValue])
        {
            await Assert.That(state.TryGetIntSlot(slot, out int i)).IsFalse()
                .Because($"slot {slot} is not in the int lane");
            await Assert.That(i).IsEqualTo(0);

            await Assert.That(state.TryGetFloatSlot(slot, out float f)).IsFalse()
                .Because($"slot {slot} is not in the float lane");
            await Assert.That(f).IsEqualTo(0f);

            await Assert.That(state.TryGetObjectSlot(slot, out object? o)).IsFalse()
                .Because($"slot {slot} is not in the object lane");
            await Assert.That(o).IsNull();
        }
    }

    [Test]
    public async Task PastTheEndSlot_ReadsAsAbsent_OnEveryLane()
    {
        EntityState state = OneSlotPerLane();

        // 1: past the one-entry lane but inside the _seen word. 64: past the word too. int.MaxValue:
        // the far end, which also pins that the guard is an unsigned compare and not a sign test.
        foreach (int slot in (int[]) [1, 64, int.MaxValue])
        {
            await Assert.That(state.TryGetIntSlot(slot, out int i)).IsFalse()
                .Because($"slot {slot} is past the end of the int lane");
            await Assert.That(i).IsEqualTo(0);

            await Assert.That(state.TryGetFloatSlot(slot, out float f)).IsFalse()
                .Because($"slot {slot} is past the end of the float lane");
            await Assert.That(f).IsEqualTo(0f);

            await Assert.That(state.TryGetObjectSlot(slot, out object? o)).IsFalse()
                .Because($"slot {slot} is past the end of the object lane");
            await Assert.That(o).IsNull();
        }
    }

    /// <summary>
    ///     All-fallback mode (no shape bound) has no lanes at all, so every slot is out of range —
    ///     the shape that would have named one has not been bound yet.
    /// </summary>
    [Test]
    public async Task ShapelessEntity_ReadsEverySlotAsAbsent()
    {
        EntityState state = new("CTestProjectile", serial: 1);

        await Assert.That(state.Shape).IsNull();
        await Assert.That(state.TryGetIntSlot(0, out int i)).IsFalse();
        await Assert.That(i).IsEqualTo(0);
        await Assert.That(state.TryGetFloatSlot(0, out float f)).IsFalse();
        await Assert.That(f).IsEqualTo(0f);
        await Assert.That(state.TryGetObjectSlot(0, out object? o)).IsFalse();
        await Assert.That(o).IsNull();
    }

    /// <summary>
    ///     The other half of the guard: an in-range slot still answers exactly as it did — <c>false</c>
    ///     before its first wire update (the absent-vs-received-zero distinction these readers exist
    ///     for), then the written value. This is the per-frame read path, so a guard that narrowed it
    ///     would be worse than the crash it prevents.
    /// </summary>
    [Test]
    public async Task ValidSlot_KeepsTheAbsentThenReceivedAnswers()
    {
        EntityState state = OneSlotPerLane();

        await Assert.That(state.TryGetIntSlot(0, out int i)).IsFalse()
            .Because("slot 0 is in range but has received no wire update yet");
        await Assert.That(state.TryGetFloatSlot(0, out float f)).IsFalse();
        await Assert.That(state.TryGetObjectSlot(0, out object? o)).IsFalse();

        // Zero and null deliberately: a received default must still read as received.
        state.SetIntSlot(0, 0);
        state.SetFloatSlot(0, 0f);
        state.SetObjectSlot(0, null);

        await Assert.That(state.TryGetIntSlot(0, out i)).IsTrue();
        await Assert.That(i).IsEqualTo(0);
        await Assert.That(state.TryGetFloatSlot(0, out f)).IsTrue();
        await Assert.That(f).IsEqualTo(0f);
        await Assert.That(state.TryGetObjectSlot(0, out o)).IsTrue();
        await Assert.That(o).IsNull();

        state.SetIntSlot(0, 42);
        state.SetFloatSlot(0, 1.5f);
        state.SetObjectSlot(0, "seen");

        await Assert.That(state.TryGetIntSlot(0, out i)).IsTrue();
        await Assert.That(i).IsEqualTo(42);
        await Assert.That(state.TryGetFloatSlot(0, out f)).IsTrue();
        await Assert.That(f).IsEqualTo(1.5f);
        await Assert.That(state.TryGetObjectSlot(0, out o)).IsTrue();
        await Assert.That(o).IsEqualTo("seen");
    }
}
