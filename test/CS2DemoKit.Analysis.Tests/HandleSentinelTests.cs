using CS2DemoKit.Analysis.Plugins;
using CS2DemoKit.Parser.EntityTracking;

namespace CS2DemoKit.Analysis.Tests;

/// <summary>
///     Invalid-handle folding in <see cref="PawnLookup" />. A networked ehandle carries index bits
///     plus serial bits and encodes invalid as all-ones at that serialized width, so the wire value
///     depends on the width and only the folded index is stable across all of them. CS2 sends 14
///     index + 10 serial, making <c>0xFFFFFF</c> the invalid a dead pawn's <c>m_hController</c>
///     actually reports.
///     <para>
///         Demo-independent by construction: every case is a handle value, so nothing here is
///         pinned to a match, a tick, or a build.
///     </para>
/// </summary>
[Category("Unit")]
public class HandleSentinelTests
{
    /// <summary>
    ///     Invalid handles at each serialized width that has plausibly shipped, plus the folded
    ///     index itself. Named so a failure says which encoding regressed.
    /// </summary>
    public static IEnumerable<(string Name, uint Handle)> InvalidHandles()
    {
        yield return ("zero", 0u);
        yield return ("32-bit all-ones", 0xFFFF_FFFFu);
        yield return ("24-bit all-ones (CS2: 14 index + 10 serial)", 0x00FF_FFFFu);
        yield return ("21-bit all-ones (Source 1: 11 index + 10 serial)", 0x001F_FFFFu);
        yield return ("bare folded index", 0x3FFFu);
    }

    [Test]
    [MethodDataSource(nameof(InvalidHandles))]
    public async Task IndexOf_InvalidHandle_IsNegative((string Name, uint Handle) c)
    {
        await Assert.That(PawnLookup.IndexOf(c.Handle)).IsEqualTo(-1)
            .Because($"{c.Name} points at no entity");
    }

    // The guard must not swallow real handles: a live index with a serial number in the high bits
    // is the ordinary case, and folding must return the index unchanged.
    [Test]
    [Arguments(1u, 1)]
    [Arguments(42u, 42)]
    [Arguments(16382u, 16382)]
    public async Task IndexOf_LiveHandle_ReturnsIndex(uint index, int expected)
    {
        uint withSerial = index | (7u << 14);

        await Assert.That(PawnLookup.IndexOf(withSerial)).IsEqualTo(expected)
            .Because("the serial number lives above the index bits and must not disturb the index");
    }

    // The regression that matters. Slot 16383 is empty in practice, so masking an invalid handle
    // raw still returned null and the bug stayed invisible. Occupying the slot removes that luck:
    // without the fold, this resolves the sentinel to a live entity.
    [Test]
    public async Task ResolveHandle_InvalidHandle_IsNullEvenWhenTheFoldedSlotIsOccupied()
    {
        EntityTracker tracker = new();
        EntityState planted = tracker.CurrentEntities.GetOrCreate(0x3FFF, "CCSPlayerController", 1);

        await Assert.That(tracker.CurrentEntities[0x3FFF]).IsSameReferenceAs(planted)
            .Because("the test is vacuous unless the slot really is occupied");

        foreach ((string name, uint handle) in InvalidHandles())
        {
            await Assert.That(PawnLookup.ResolveHandle(tracker, handle)).IsNull()
                .Because($"{name} must not resolve to whatever occupies slot 16383");
        }
    }

    [Test]
    public async Task ResolveHandle_LiveHandle_ResolvesToThatEntity()
    {
        EntityTracker tracker = new();
        EntityState pawn = tracker.CurrentEntities.GetOrCreate(42, "CCSPlayerPawn", 1);

        await Assert.That(PawnLookup.ResolveHandle(tracker, 42u | (7u << 14))).IsSameReferenceAs(pawn);
    }
}
