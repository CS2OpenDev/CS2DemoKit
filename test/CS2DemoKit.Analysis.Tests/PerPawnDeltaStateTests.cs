#region

using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Analysis.Plugins;
using CS2DemoKit.Parser.EntityTracking;
using CS2OpenDev.Sdk.Entities;

#endregion

namespace CS2DemoKit.Analysis.Tests;

/// <summary>
///     Unit pins for the typed per-pawn digest: the delta state's change relation (the boxed
///     <c>Equals(object, object)</c> one restated on typed values, so NaN equals NaN and negative
///     zero equals zero), its recorded/was-null masks (a first null is a change, a null after a
///     null is not), the row storage's sizing contract (Empty until the first row, kind arrays
///     exactly as wide as the kind), the boxed-row test seam's kind check, the layout
///     compatibility relation, and the snapshot's fold. Pure in-memory; the real-demo pins are
///     <c>PerPawnFoldGoldenTests</c> and <c>PawnCellReaderParityTests</c>.
/// </summary>
[Category("Unit")]
public class PerPawnDeltaStateTests
{
    private static DigestColumnLayout Layout(params (string Name, Type Type)[] columns) =>
        DigestColumnLayout.For(columns.Select(c => (IPerPlayerEntityValueProvider)new StubProvider(c.Name, c.Type)).ToList());

    // ── Change relation ──────────────────────────────────────────────────────

    [Test]
    public async Task FirstRecord_IsAChange_EvenWhenNull()
    {
        DigestColumnLayout layout = Layout(("a", typeof(int)));
        PerPawnDeltaState delta = new(layout);
        TypedSlotRow row = delta.RowFor(3);

        await Assert.That(delta.RecordInt(row, 0, hasValue: false, 0)).IsTrue()
            .Because("never recorded is distinct from recorded null");
        await Assert.That(delta.RecordInt(row, 0, hasValue: false, 0)).IsFalse()
            .Because("null after null is the boxed Equals(null, null)");
        await Assert.That(delta.RecordInt(row, 0, hasValue: true, 7)).IsTrue();
        await Assert.That(delta.RecordInt(row, 0, hasValue: true, 7)).IsFalse();
        await Assert.That(delta.RecordInt(row, 0, hasValue: true, 8)).IsTrue();
        await Assert.That(delta.RecordInt(row, 0, hasValue: false, 0)).IsTrue()
            .Because("a value going to null is a change, exactly as the boxed digest recorded it");
        await Assert.That(delta.RecordInt(row, 0, hasValue: true, 8)).IsTrue()
            .Because("the same value returning after a null is a change");
    }

    [Test]
    public async Task FloatEquality_IsTheBoxedRelation_NaNEqualsNaN_NegativeZeroEqualsZero()
    {
        DigestColumnLayout layout = Layout(("f", typeof(float)));
        PerPawnDeltaState delta = new(layout);
        TypedSlotRow row = delta.RowFor(0);

        await Assert.That(delta.RecordFloat(row, 0, true, float.NaN)).IsTrue();
        await Assert.That(delta.RecordFloat(row, 0, true, float.NaN)).IsFalse()
            .Because("object.Equals on two boxed NaNs is true, so the boxed digest never re-emitted NaN");
        await Assert.That(delta.RecordFloat(row, 0, true, 0f)).IsTrue();
        await Assert.That(delta.RecordFloat(row, 0, true, -0f)).IsFalse()
            .Because("float.Equals(float) treats negative zero as zero, as the boxed digest did");
        await Assert.That(delta.RecordFloat(row, 0, true, float.Epsilon)).IsTrue();
    }

    [Test]
    public async Task StringEquality_IsOrdinal()
    {
        DigestColumnLayout layout = Layout(("s", typeof(string)));
        PerPawnDeltaState delta = new(layout);
        TypedSlotRow row = delta.RowFor(0);

        await Assert.That(delta.RecordString(row, 0, "CWeaponAK47")).IsTrue();
        await Assert.That(delta.RecordString(row, 0, new string("CWeaponAK47".AsSpan()))).IsFalse()
            .Because("equal contents, different instance: the boxed digest compared by value");
        await Assert.That(delta.RecordString(row, 0, "cweaponak47")).IsTrue()
            .Because("ordinal, not case-insensitive");
        await Assert.That(delta.RecordString(row, 0, null)).IsTrue();
        await Assert.That(delta.RecordString(row, 0, null)).IsFalse();
    }

    [Test]
    public async Task DedupOff_EveryRecordIsAChange()
    {
        DigestColumnLayout layout = Layout(("a", typeof(int)));
        PerPawnDeltaState delta = new(layout, dedup: false);
        TypedSlotRow row = delta.RowFor(0);

        await Assert.That(delta.RecordInt(row, 0, true, 1)).IsTrue();
        await Assert.That(delta.RecordInt(row, 0, true, 1)).IsTrue();
        await Assert.That(delta.RecordInt(row, 0, false, 0)).IsTrue();
        await Assert.That(delta.RecordInt(row, 0, false, 0)).IsTrue();
    }

    [Test]
    public async Task Columns_AreIndependent_AcrossKindsAndSlots()
    {
        DigestColumnLayout layout = Layout(("a", typeof(int)), ("b", typeof(bool)), ("c", typeof(float)), ("d", typeof(string)));
        PerPawnDeltaState delta = new(layout);
        TypedSlotRow slot0 = delta.RowFor(0);
        TypedSlotRow slot9 = delta.RowFor(9);

        await Assert.That(delta.RecordInt(slot0, 0, true, 100)).IsTrue();
        await Assert.That(delta.RecordInt(slot0, 1, true, 1)).IsTrue();
        await Assert.That(delta.RecordFloat(slot0, 2, true, 2.5f)).IsTrue();
        await Assert.That(delta.RecordString(slot0, 3, "x")).IsTrue();

        await Assert.That(delta.RecordInt(slot9, 0, true, 100)).IsTrue()
            .Because("slot 9 has its own memory");
        await Assert.That(delta.RecordInt(slot0, 0, true, 100)).IsFalse();
        await Assert.That(delta.RecordInt(slot0, 1, true, 1)).IsFalse();
        await Assert.That(delta.RecordFloat(slot0, 2, true, 2.5f)).IsFalse();
        await Assert.That(delta.RecordString(slot0, 3, "x")).IsFalse();

        await Assert.That(slot0.Ints[layout.KindIndex[0]]).IsEqualTo(100);
        await Assert.That(slot0.Ints[layout.KindIndex[1]]).IsEqualTo(1);
        await Assert.That(slot0.Floats[layout.KindIndex[2]]).IsEqualTo(2.5f);
        await Assert.That(slot0.Strings[layout.KindIndex[3]]).IsEqualTo("x");
    }

    [Test]
    public async Task SlotsBeyondSixtyFour_GrowTheTable()
    {
        DigestColumnLayout layout = Layout(("a", typeof(int)));
        PerPawnDeltaState delta = new(layout);
        TypedSlotRow row = delta.RowFor(200);

        await Assert.That(delta.RecordInt(row, 0, true, 1)).IsTrue();
        await Assert.That(ReferenceEquals(delta.RowFor(200), row)).IsTrue();
    }

    // ── Row storage ──────────────────────────────────────────────────────────

    [Test]
    public async Task Digest_StartsOnTheSharedEmptyRows_WhichRefuseAppends()
    {
        EntityFrameDigest digest = new();
        await Assert.That(ReferenceEquals(digest.PerPawn, PerPawnColumns.Empty)).IsTrue();
        await Assert.That(digest.PerPawn.Count).IsEqualTo(0);
        await Assert.That(digest.Molotovs.Length).IsEqualTo(0);
        await Assert.That(digest.Smokes.Length).IsEqualTo(0);

        DigestColumnLayout layout = Layout(("a", typeof(int)));
        TypedSlotRow row = new(layout);
        Assert.Throws<InvalidOperationException>(() => PerPawnColumns.Empty.AppendRow(0, row, new ulong[1]));
    }

    [Test]
    public async Task Rows_GrowPastTheInitialCapacity_AndKeepEveryCell()
    {
        DigestColumnLayout layout = Layout(("a", typeof(int)), ("f", typeof(float)), ("s", typeof(string)));
        PerPawnColumns columns = new(layout, PerPawnColumns.InitialCapacity);
        TypedSlotRow scratch = new(layout);
        ulong[] present = new ulong[1];

        for (int r = 0; r < 37; r++)
        {
            scratch.Ints[0] = r;
            scratch.Floats[0] = r * 0.5f;
            scratch.Strings[0] = r % 3 == 0 ? null : $"row{r}";
            present[0] = scratch.Strings[0] is null ? 0b011UL : 0b111UL;
            columns.AppendRow(r + 1, scratch, present);
        }

        await Assert.That(columns.Count).IsEqualTo(37);
        for (int r = 0; r < 37; r++)
        {
            await Assert.That(columns.SlotAt(r)).IsEqualTo(r + 1);
            await Assert.That(columns.GetBoxed(r, 0)).IsEqualTo(r);
            await Assert.That(columns.GetBoxed(r, 1)).IsEqualTo(r * 0.5f);
            await Assert.That(columns.GetBoxed(r, 2)).IsEqualTo(r % 3 == 0 ? null : $"row{r}");
        }
    }

    [Test]
    public async Task FromBoxedRows_AcceptsDeclaredTypes_AndRejectsOthers()
    {
        DigestColumnLayout layout = Layout(("a", typeof(int)), ("b", typeof(bool)), ("f", typeof(float)), ("s", typeof(string)));

        PerPawnColumns ok = PerPawnColumns.FromBoxedRows(layout, [(4, new object?[] { 7, true, 1.5f, "x" }), (5, new object?[] { null, false, null, null })]);
        await Assert.That(ok.Count).IsEqualTo(2);
        await Assert.That(ok.GetBoxed(0, 0)).IsEqualTo(7);
        await Assert.That(ok.GetBoxed(0, 1)).IsEqualTo(true);
        await Assert.That(ok.GetBoxed(0, 2)).IsEqualTo(1.5f);
        await Assert.That(ok.GetBoxed(0, 3)).IsEqualTo("x");
        await Assert.That(ok.IsPresent(1, 0)).IsFalse();
        await Assert.That(ok.GetBoxed(1, 1)).IsEqualTo(false);
        await Assert.That(ok.GetBoxed(1, 2)).IsNull();

        // A double in a float column is the mistake the seam exists to catch.
        Assert.Throws<ArgumentException>(() => PerPawnColumns.FromBoxedRows(layout, [(4, new object?[] { 7, true, 1.5, "x" })]));
        // A row narrower than the layout.
        Assert.Throws<ArgumentException>(() => PerPawnColumns.FromBoxedRows(layout, [(4, new object?[] { 7, true })]));
        await Assert.That(ReferenceEquals(PerPawnColumns.FromBoxedRows(layout, []), PerPawnColumns.Empty)).IsTrue();
    }

    // ── Layout ───────────────────────────────────────────────────────────────

    [Test]
    public async Task Layout_RejectsTypesOutsideTheClosedSet()
    {
        Assert.Throws<NotSupportedException>(() => Layout(("d", typeof(double))));
        Assert.Throws<NotSupportedException>(() => Layout(("l", typeof(long))));
        await Task.CompletedTask;
    }

    [Test]
    public async Task Layout_CompatibilityIsKindsAndNames_ColumnForColumn()
    {
        DigestColumnLayout a = Layout(("x", typeof(int)), ("y", typeof(float)));
        DigestColumnLayout same = Layout(("x", typeof(int)), ("y", typeof(float)));
        DigestColumnLayout renamed = Layout(("x", typeof(int)), ("z", typeof(float)));
        DigestColumnLayout retyped = Layout(("x", typeof(bool)), ("y", typeof(float)));
        DigestColumnLayout reordered = Layout(("y", typeof(float)), ("x", typeof(int)));
        DigestColumnLayout shorter = Layout(("x", typeof(int)));

        await Assert.That(a.IsCompatibleWith(same)).IsTrue();
        await Assert.That(a.IsCompatibleWith(renamed)).IsFalse();
        await Assert.That(a.IsCompatibleWith(retyped)).IsFalse();
        await Assert.That(a.IsCompatibleWith(reordered)).IsFalse();
        await Assert.That(a.IsCompatibleWith(shorter)).IsFalse();
        await Assert.That(DigestColumnLayout.Empty.IsCompatibleWith(DigestColumnLayout.For([]))).IsTrue();
    }

    [Test]
    public async Task Layout_IndexesEachKindDensely_AndSharesIntStorageForBools()
    {
        DigestColumnLayout layout = Layout(
            ("i1", typeof(int)), ("f1", typeof(float)), ("b1", typeof(bool)), ("s1", typeof(string)), ("i2", typeof(int)));

        await Assert.That(layout.IntCount).IsEqualTo(3);
        await Assert.That(layout.FloatCount).IsEqualTo(1);
        await Assert.That(layout.StringCount).IsEqualTo(1);
        await Assert.That(layout.KindIndex).IsEquivalentTo([0, 0, 1, 0, 2]);
        await Assert.That(layout.Words).IsEqualTo(1);
        await Assert.That(DigestColumnLayout.Empty.Words).IsEqualTo(0);
    }

    // ── Scanner-side fold ────────────────────────────────────────────────────

    [Test]
    public async Task Scanner_RejectsADigestBuiltOnAnIncompatibleLayout_ButAcceptsEmptyRows()
    {
        PawnHealthProvider health = new();
        EntityChangeScanner scanner = new(new EntityStateLayer([]), providers: [], perPlayerProviders: [health]);

        DigestColumnLayout foreign = Layout(("entity.pawn.armor", typeof(int)));
        EntityFrameDigest wrongLayout = new()
        {
            PerPawn = PerPawnColumns.FromBoxedRows(foreign, [(3, new object?[] { 50 })])
        };
        EntityFrameDigest noRows = new()
        {
            PerPawn = PerPawnColumns.FromBoxedRows(foreign, [])
        };

        // The previous frame's digest is what gets folded, so the incompatible one is consumed at
        // frame 0 and rejected when frame 1 folds it.
        scanner.SetPrecomputedDigests([noRows, noRows, wrongLayout, noRows]);
        scanner.AdvanceAndPollAt(0, 10);
        scanner.AdvanceAndPollAt(1, 20);
        scanner.AdvanceAndPollAt(2, 30);
        Assert.Throws<InvalidOperationException>(() => scanner.AdvanceAndPollAt(3, 40));
        await Task.CompletedTask;
    }

    [Test]
    public async Task Snapshot_FoldsPresentCellsOnly_AndBoxesInDeclaredTypes()
    {
        DigestColumnLayout layout = Layout(("a", typeof(int)), ("b", typeof(bool)), ("f", typeof(float)), ("s", typeof(string)));
        PreFrameSnapshot snapshot = new(layout);

        snapshot.Fold(PerPawnColumns.FromBoxedRows(layout, [(2, new object?[] { 1, true, 0.25f, "one" })]));
        snapshot.Fold(PerPawnColumns.FromBoxedRows(layout, [(2, new object?[] { null, false, null, null }), (70, new object?[] { 9, null, null, null })]));

        await Assert.That(snapshot.GetBoxed(0, 2)).IsEqualTo(1).Because("an absent cell leaves the held value alone");
        await Assert.That(snapshot.GetBoxed(1, 2)).IsEqualTo(false);
        await Assert.That(snapshot.GetBoxed(2, 2)).IsEqualTo(0.25f);
        await Assert.That(snapshot.GetBoxed(3, 2)).IsEqualTo("one");
        await Assert.That(snapshot.GetBoxed(0, 70)).IsEqualTo(9).Because("slots past 63 grow the table");
        await Assert.That(snapshot.GetBoxed(1, 70)).IsNull().Because("never written");
        await Assert.That(snapshot.GetBoxed(0, 5)).IsNull();
        await Assert.That(snapshot.GetBoxed(0, -1)).IsNull();
        await Assert.That(ReferenceEquals(snapshot.GetBoxed(1, 2), snapshot.GetBoxed(1, 2))).IsTrue()
            .Because("bools box to the two shared instances");
    }

    /// <summary>A provider that exists only to give a layout a name and a type; never read.</summary>
    private sealed class StubProvider(string name, Type type) : IPerPlayerEntityValueProvider
    {
        public string EntityClass => "CCSPlayerPawn";

        public string FieldName => name;

        public string Name => name;

        public Type ValueType => type;

        public void CaptureAllSlots(EntityStateLayer layer, Action<int, object> emit) =>
            throw new NotSupportedException();

        public object? ReadForPawn(EntityTracker tracker, CSPlayerPawn pawn) => throw new NotSupportedException();

        public object? Read(EntityStateLayer layer, int playerSlot) => throw new NotSupportedException();
    }
}
