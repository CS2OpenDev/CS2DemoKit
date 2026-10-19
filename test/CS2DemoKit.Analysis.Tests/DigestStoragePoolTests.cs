#region

using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Analysis.Observation;
using CS2DemoKit.Analysis.Plugins;
using CS2DemoKit.Parser.EntityTracking;
using CS2OpenDev.Sdk.Entities;

#endregion

namespace CS2DemoKit.Analysis.Tests;

/// <summary>
///     Pins for the digest storage pool: a consumed digest hands its row storage back, the next
///     rent reuses it empty, a recycled instance follows the layout it was rented for, projectile
///     rows share a block and stay contiguous across a block boundary, and a digest built without
///     a pool keeps its rows when released, which is the case every hand-built fixture digest is in.
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
    public async Task ProjectileRows_ShareABlock_AndOutliveAnotherDigestsRelease()
    {
        DigestStoragePool pool = new();
        EntityFrameDigest first = new() { Pool = pool };
        first.AddProjectile(Smoke);
        first.AddProjectile(Smoke with { EntityIndex = 41 });
        EntityFrameDigest second = new() { Pool = pool };
        second.AddProjectile(Smoke with { EntityIndex = 42, Serial = 9 });

        await Assert.That(first.Projectiles.Length).IsEqualTo(2);
        await Assert.That(first.Projectiles[1].EntityIndex).IsEqualTo(41);
        await Assert.That(second.Projectiles.Length).IsEqualTo(1);
        await Assert.That(second.Projectiles[0].Serial).IsEqualTo(9);

        first.ReleaseStorage();
        await Assert.That(first.Projectiles.Length).IsEqualTo(0);
        await Assert.That(second.Projectiles.Length).IsEqualTo(1);
        await Assert.That(second.Projectiles[0].EntityIndex).IsEqualTo(42);
    }

    [Test]
    public async Task ProjectileRows_EqualToTheLastDigests_ShareItsSegment()
    {
        DigestStoragePool pool = new();
        EntityFrameDigest first = new() { Pool = pool };
        first.AddProjectile(Smoke);
        first.AddProjectile(Smoke with { EntityIndex = 41 });
        first.CommitProjectiles();

        EntityFrameDigest same = new() { Pool = pool };
        same.AddProjectile(Smoke);
        same.AddProjectile(Smoke with { EntityIndex = 41 });
        same.CommitProjectiles();

        EntityFrameDigest moved = new() { Pool = pool };
        moved.AddProjectile(Smoke);
        moved.AddProjectile(Smoke with { EntityIndex = 41, Bounces = 1 });
        moved.CommitProjectiles();

        EntityFrameDigest fewer = new() { Pool = pool };
        fewer.AddProjectile(Smoke);
        fewer.CommitProjectiles();

        await Assert.That(same.Projectiles.Length).IsEqualTo(2);
        await Assert.That(same.Projectiles[1].EntityIndex).IsEqualTo(41);
        await Assert.That(same.Projectiles.Overlaps(first.Projectiles)).IsTrue()
            .Because("an unchanged frame reads the previous digest's rows, not a copy");
        await Assert.That(moved.Projectiles.Overlaps(first.Projectiles)).IsFalse();
        await Assert.That(moved.Projectiles[1].Bounces).IsEqualTo(1);
        await Assert.That(fewer.Projectiles.Length).IsEqualTo(1);
        await Assert.That(fewer.Projectiles.Overlaps(moved.Projectiles)).IsFalse();

        first.ReleaseStorage();
        await Assert.That(same.Projectiles.Length).IsEqualTo(2);
        await Assert.That(same.Projectiles[0].Serial).IsEqualTo(7);
    }

    [Test]
    public async Task ProjectileRows_StayContiguous_AcrossABlockBoundary()
    {
        DigestStoragePool pool = new();
        EntityFrameDigest filler = new() { Pool = pool };
        for (int i = 0; i < DigestStoragePool.ProjectileBlockRows - 1; i++)
        {
            filler.AddProjectile(Smoke with { EntityIndex = i });
        }

        filler.CommitProjectiles();
        EntityFrameDigest straddling = new() { Pool = pool };
        for (int i = 0; i < 3; i++)
        {
            straddling.AddProjectile(Smoke with { EntityIndex = 100 + i });
        }

        straddling.CommitProjectiles();

        await Assert.That(filler.Projectiles.Length).IsEqualTo(DigestStoragePool.ProjectileBlockRows - 1);
        await Assert.That(filler.Projectiles[^1].EntityIndex).IsEqualTo(DigestStoragePool.ProjectileBlockRows - 2);
        await Assert.That(straddling.Projectiles.Length).IsEqualTo(3);
        for (int i = 0; i < 3; i++)
        {
            await Assert.That(straddling.Projectiles[i].EntityIndex).IsEqualTo(100 + i);
        }

        EntityFrameDigest next = new() { Pool = pool };
        next.AddProjectile(Smoke with { EntityIndex = 200 });
        next.CommitProjectiles();
        await Assert.That(next.Projectiles[0].EntityIndex).IsEqualTo(200);
        await Assert.That(straddling.Projectiles[2].EntityIndex).IsEqualTo(102);
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
        await Assert.That(digest.Projectiles.Length).IsEqualTo(0);
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
