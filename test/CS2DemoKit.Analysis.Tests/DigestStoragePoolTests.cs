#region

using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Analysis.Observation;
using CS2DemoKit.Analysis.Plugins;
using CS2DemoKit.Parser.EntityTracking;
using CS2OpenDev.Sdk.Entities;

#endregion

namespace CS2DemoKit.Analysis.Tests;

/// <summary>
///     Pins for the digest storage pool: a consumed digest hands its row storage and projectile
///     list back, the next rent reuses them empty, a recycled instance follows the layout it was
///     rented for, and a digest built without a pool keeps its rows when released, which is the
///     case every hand-built fixture digest is in.
/// </summary>
[Category("Unit")]
public class DigestStoragePoolTests
{
    private static readonly ProjectileRow Smoke = new(
        40, 7, "CSmokeGrenadeProjectile", 3, 2, null, null, null, 0, 0, null, null);

    private static DigestColumnLayout Layout(params (string Name, Type Type)[] columns) =>
        DigestColumnLayout.For(columns.Select(c => (IPerPlayerEntityValueProvider)new StubProvider(c.Name, c.Type)).ToList());

    [Test]
    public async Task ReleasedRows_ComeBackOnTheNextRent_Empty()
    {
        DigestColumnLayout layout = Layout(("a", typeof(int)), ("b", typeof(float)));
        DigestStoragePool pool = new();
        PerPawnColumns rows = pool.RentColumns(layout, 4);
        TypedSlotRow row = new(layout);
        row.Ints[0] = 5;
        row.Floats[0] = 1.5f;
        rows.AppendRow(3, row, [0b11UL]);

        EntityFrameDigest digest = new() { Pool = pool, PerPawn = rows };
        digest.ReleaseStorage();

        await Assert.That(ReferenceEquals(digest.PerPawn, PerPawnColumns.Empty)).IsTrue();
        PerPawnColumns again = pool.RentColumns(layout, 4);
        await Assert.That(ReferenceEquals(again, rows)).IsTrue();
        await Assert.That(again.Count).IsEqualTo(0);

        again.AppendRow(9, row, [0b01UL]);
        await Assert.That(again.Count).IsEqualTo(1);
        await Assert.That(again.SlotAt(0)).IsEqualTo(9);
        await Assert.That(again.IsPresent(0, 0)).IsTrue();
        await Assert.That(again.IsPresent(0, 1)).IsFalse();
        await Assert.That(again.GetInt(0, 0)).IsEqualTo(5);
    }

    [Test]
    public async Task ReleasedProjectileRows_ComeBackOnTheNextRent_Empty()
    {
        DigestStoragePool pool = new();
        EntityFrameDigest digest = new() { Pool = pool };
        digest.AddProjectile(Smoke);
        await Assert.That(digest.Projectiles.Length).IsEqualTo(1);

        digest.ReleaseStorage();
        await Assert.That(digest.Projectiles.Length).IsEqualTo(0);

        List<ProjectileRow> list = pool.RentProjectiles();
        await Assert.That(list.Count).IsEqualTo(0);
        list.Add(Smoke);
        pool.Return(list);
        await Assert.That(ReferenceEquals(pool.RentProjectiles(), list)).IsTrue();
        await Assert.That(list.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Release_IsIdempotent()
    {
        DigestColumnLayout layout = Layout(("a", typeof(int)));
        DigestStoragePool pool = new();
        EntityFrameDigest digest = new() { Pool = pool, PerPawn = pool.RentColumns(layout, 4) };
        digest.AddProjectile(Smoke);

        digest.ReleaseStorage();
        digest.ReleaseStorage();

        PerPawnColumns first = pool.RentColumns(layout, 4);
        PerPawnColumns second = pool.RentColumns(layout, 4);
        await Assert.That(ReferenceEquals(first, second)).IsFalse()
            .Because("a double release must not put the same storage in the pool twice");
        pool.RentProjectiles();
        await Assert.That(pool.RentProjectiles().Count).IsEqualTo(0);
    }

    [Test]
    public async Task WithoutAPool_ReleaseKeepsTheRows()
    {
        DigestColumnLayout layout = Layout(("hp", typeof(int)));
        EntityFrameDigest digest = new()
        {
            PerPawn = PerPawnColumns.FromBoxedRows(layout, [(3, new object?[] { 77 })])
        };
        digest.AddProjectile(Smoke);

        digest.ReleaseStorage();

        await Assert.That(digest.PerPawn.Count).IsEqualTo(1);
        await Assert.That(digest.PerPawn.GetInt(0, 0)).IsEqualTo(77);
        await Assert.That(digest.Projectiles.Length).IsEqualTo(1);
    }

    [Test]
    public async Task RecycledRows_FollowTheLayoutTheyAreRentedFor()
    {
        DigestColumnLayout first = Layout(("a", typeof(int)), ("b", typeof(string)));
        DigestColumnLayout same = Layout(("a", typeof(int)), ("b", typeof(string)));
        DigestColumnLayout other = Layout(("a", typeof(float)), ("b", typeof(string)));
        DigestStoragePool pool = new();

        PerPawnColumns rows = pool.RentColumns(first, 4);
        pool.Return(rows);
        PerPawnColumns rebound = pool.RentColumns(same, 4);
        await Assert.That(ReferenceEquals(rebound, rows)).IsTrue();
        await Assert.That(ReferenceEquals(rebound.Layout, same)).IsTrue();

        pool.Return(rebound);
        Assert.Throws<InvalidOperationException>(() => _ = pool.RentColumns(other, 4));
    }

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
