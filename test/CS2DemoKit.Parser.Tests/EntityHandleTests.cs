using CS2DemoKit.Parser.EntityTracking;

namespace CS2DemoKit.Parser.Tests;

/// <summary>
///     The wire rule <see cref="EntityHandle" /> exists to hold in one place: invalid is all-ones
///     at the serialized width, so every width folds to the reserved index.
///     <para>
///         Demo-independent by construction. Before this type the same rule was re-derived at
///         three call sites and got it wrong at all three.
///     </para>
/// </summary>
[Category("Unit")]
public class EntityHandleTests
{
    /// <summary>
    ///     All-ones at each serialized width that has plausibly shipped. The point of the type is
    ///     that it does not need to know which width produced the value.
    /// </summary>
    public static IEnumerable<(string Name, uint Value)> InvalidHandles()
    {
        yield return ("32-bit all-ones", 0xFFFF_FFFFu);
        yield return ("24-bit all-ones (CS2: 14 index + 10 serial)", 0x00FF_FFFFu);
        yield return ("21-bit all-ones (Source 1: 11 index + 10 serial)", 0x001F_FFFFu);
        yield return ("bare reserved index", EntityHandle.IndexMask);
    }

    [Test]
    [MethodDataSource(nameof(InvalidHandles))]
    public async Task IsValid_AllOnesAtAnyWidth_IsFalse((string Name, uint Value) c)
    {
        await Assert.That(new EntityHandle(c.Value).IsValid).IsFalse()
            .Because($"{c.Name} names no entity");
    }

    // The zero convention deliberately lives with callers, not the wire primitive.
    [Test]
    public async Task IsValid_ZeroHandle_IsTrue()
    {
        await Assert.That(new EntityHandle(0).IsValid).IsTrue()
            .Because("zero is index 0, which is a real slot on the wire; 'zero means unset' is a "
                     + "caller convention and folding it in here would change it for every caller");
    }

    [Test]
    [Arguments(0u, 0u)]
    [Arguments(1u, 0u)]
    [Arguments(42u, 7u)]
    [Arguments(16382u, 1023u)]
    public async Task IndexAndSerial_RoundTripThroughThePackedValue(uint index, uint serial)
    {
        EntityHandle h = new(index | (serial << EntityHandle.IndexBits));

        await Assert.That(h.Index).IsEqualTo((int)index);
        await Assert.That(h.Serial).IsEqualTo(serial);
        await Assert.That(h.IsValid).IsTrue();
    }

    // The serial occupies the bits above the index, so a large serial must not bleed downward.
    [Test]
    public async Task Index_IsUnaffectedByTheSerial()
    {
        const uint index = 300;
        List<uint> disturbed = [];

        for (uint serial = 0; serial < 1024; serial++)
        {
            EntityHandle h = new(index | (serial << EntityHandle.IndexBits));
            if (h.Index != (int)index)
            {
                disturbed.Add(serial);
            }
        }

        await Assert.That(disturbed).IsEmpty()
            .Because("the serial occupies the bits above the index and must not bleed downward");
    }

    [Test]
    public async Task FromRaw_NegativeWireValue_FoldsToTheUnsignedEncoding()
    {
        await Assert.That(EntityHandle.FromRaw(-1)).IsEqualTo(new EntityHandle(0xFFFF_FFFFu));
        await Assert.That(EntityHandle.FromRaw(-1).IsValid).IsFalse()
            .Because("-1 is how 0xFFFFFFFF arrives on the int lane");
    }

    // Pins the width behaviourally rather than by restating the constants. Widening the index
    // without widening the reserved marker would break the fold, and this is what would catch it.
    [Test]
    public async Task ReservedIndex_IsTheSlotAboveTheLastAddressableOne()
    {
        EntityHandle lastAddressable = new(EntityHandle.IndexMask - 1);
        EntityHandle reserved = new(EntityHandle.IndexMask);

        await Assert.That(lastAddressable.Index).IsEqualTo(16382);
        await Assert.That(lastAddressable.IsValid).IsTrue();
        await Assert.That(reserved.Index).IsEqualTo(16383);
        await Assert.That(reserved.IsValid).IsFalse()
            .Because("the top index is the invalid marker, which is what every width folds to");
    }
}
