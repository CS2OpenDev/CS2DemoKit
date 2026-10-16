#region

using System.Numerics;
using CS2DemoKit.Analysis.Observation;
using CS2DemoKit.Parser.EntityTracking;

#endregion

namespace CS2DemoKit.Analysis.Tests;

/// <summary>
///     The frame diff behind the projectile lifecycle, on hand-built rows: a slot that changes
///     hands within one frame removes the old projectile before it creates the new one, a thrower
///     resolved once is held even when a later frame cannot resolve it, and the removed row is the
///     last one seen. Hand-built because the slot hand-over needs two projectiles in one slot on
///     consecutive frames, which no bundled demo is guaranteed to contain.
/// </summary>
[Category("Unit")]
public class ProjectileLifecycleTests
{
    // Order is part of the contract: every list is ascending by entity index.
    private static bool Same(ReadOnlySpan<ProjectileRow> actual, params ProjectileRow[] expected) =>
        actual.SequenceEqual(expected);

    private static ProjectileRow Row(int index, int serial, int thrower, string className = GrenadeProjectileClasses.HEGrenade,
        int bounces = 0) =>
        new(index, serial, className, thrower, 2, null, null, null, bounces, 0, null, null);

    [Test]
    public async Task FirstSighting_IsCreated_AndAnEmptyFrame_RemovesEverything()
    {
        ProjectileLifecycle life = new();

        life.Step([Row(70, 3, 5), Row(72, 1, 8)]);
        await Assert.That(Same(life.Created, Row(70, 3, 5), Row(72, 1, 8))).IsTrue();
        await Assert.That(life.Removed.Length).IsEqualTo(0);
        await Assert.That(Same(life.Live, Row(70, 3, 5), Row(72, 1, 8))).IsTrue();

        life.Step([]);
        await Assert.That(life.Created.Length).IsEqualTo(0);
        await Assert.That(Same(life.Removed, Row(70, 3, 5), Row(72, 1, 8))).IsTrue();
        await Assert.That(life.Live.Length).IsEqualTo(0);
    }

    [Test]
    public async Task ASlotThatChangesHands_RemovesBeforeItCreates_AndKeepsAscendingOrder()
    {
        ProjectileLifecycle life = new();
        life.Step([Row(70, 3, 5, bounces: 1), Row(71, 1, 2)]);

        // 70 is re-created with a new serial; 71 is gone; 69 and 73 are new.
        life.Step([Row(69, 9, 1), Row(70, 4, 6), Row(73, 2, 3)]);

        await Assert.That(Same(life.Removed, Row(70, 3, 5, bounces: 1), Row(71, 1, 2))).IsTrue()
            .Because("the removed row is the last one seen, ascending by entity index");
        await Assert.That(Same(life.Created, Row(69, 9, 1), Row(70, 4, 6), Row(73, 2, 3))).IsTrue();
        await Assert.That(Same(life.Live, Row(69, 9, 1), Row(70, 4, 6), Row(73, 2, 3))).IsTrue();
    }

    [Test]
    public async Task ASerialKeptByAnotherClass_IsANewProjectile()
    {
        ProjectileLifecycle life = new();
        life.Step([Row(70, 3, 5)]);
        life.Step([Row(70, 3, 5, GrenadeProjectileClasses.Flashbang)]);

        await Assert.That(Same(life.Removed, Row(70, 3, 5))).IsTrue();
        await Assert.That(Same(life.Created, Row(70, 3, 5, GrenadeProjectileClasses.Flashbang))).IsTrue();
    }

    [Test]
    public async Task TheThrower_IsHeldFromItsFirstResolution()
    {
        ProjectileLifecycle life = new();

        life.Step([Row(70, 3, -1)]);
        await Assert.That(life.Live[0].ThrowerSlot).IsEqualTo(-1);

        life.Step([Row(70, 3, 5)]);
        await Assert.That(life.Live[0].ThrowerSlot).IsEqualTo(5);

        // The chain breaks (the thrower died) and later re-resolves elsewhere: both are ignored.
        life.Step([Row(70, 3, -1, bounces: 2)]);
        await Assert.That(life.Live[0]).IsEqualTo(Row(70, 3, 5, bounces: 2));
        life.Step([Row(70, 3, 9, bounces: 3)]);
        await Assert.That(life.Live[0]).IsEqualTo(Row(70, 3, 5, bounces: 3));

        life.Step([]);
        await Assert.That(life.Removed[0]).IsEqualTo(Row(70, 3, 5, bounces: 3))
            .Because("the removed row carries the held thrower and the last-seen fields");
    }

    [Test]
    public async Task Reset_ForgetsTheLiveSet()
    {
        ProjectileLifecycle life = new();
        life.Step([Row(70, 3, 5)]);
        life.Reset();

        await Assert.That(life.Live.Length).IsEqualTo(0);
        life.Step([Row(70, 3, 5)]);
        await Assert.That(life.Created.Length).IsEqualTo(1).Because("after a reset the same projectile is created again");
        await Assert.That(life.Removed.Length).IsEqualTo(0);
    }

    private static ProjectileRow At(int index, int serial, float x, float y = 0, float z = 0) =>
        Row(index, serial, 5) with { Position = new Vector3(x, y, z) };

    [Test]
    public async Task LastMoved_IsTheSpawnUntilThePositionChangesByMoreThanATenth()
    {
        ProjectileLifecycle life = new() { TrackMovement = true };

        life.Step([At(70, 3, 100f)], 10);
        await Assert.That(life.LiveLastMoved.ToArray()).IsEquivalentTo([10]);

        // Jitter under 0.1 units is rest; a step of exactly 0.1 is rest too.
        life.Step([At(70, 3, 100.05f)], 11);
        life.Step([At(70, 3, 100.1f)], 12);
        await Assert.That(life.LiveLastMoved.ToArray()).IsEquivalentTo([10]);

        life.Step([At(70, 3, 100.5f), At(72, 1, 0f)], 13);
        await Assert.That(life.LiveLastMoved.ToArray()).IsEquivalentTo([13, 13]);

        // An unreadable position on either side is not a move.
        life.Step([Row(70, 3, 5), At(72, 1, 0f)], 14);
        life.Step([At(70, 3, 200f), At(72, 1, 0f)], 15);
        await Assert.That(life.LiveLastMoved.ToArray()).IsEquivalentTo([13, 13]);

        life.Step([At(70, 3, 201f), At(72, 1, 0f)], 16);
        await Assert.That(life.LiveLastMoved.ToArray()).IsEquivalentTo([16, 13]);

        // A removed row carries the tick it last moved on, aligned with Removed.
        life.Step([At(72, 1, 0f)], 17);
        await Assert.That(life.RemovedLastMoved.ToArray()).IsEquivalentTo([16]);
        await Assert.That(life.LiveLastMoved.ToArray()).IsEquivalentTo([13]);
    }

    [Test]
    public async Task LastMoved_IsNotKept_WhenMovementIsNotTracked()
    {
        ProjectileLifecycle life = new();
        life.Step([At(70, 3, 100f)], 10);
        life.Step([At(70, 3, 200f)], 11);
        await Assert.That(life.LiveLastMoved.Length).IsEqualTo(0);
        life.Step([], 12);
        await Assert.That(life.RemovedLastMoved.Length).IsEqualTo(0);
        await Assert.That(life.Removed.Length).IsEqualTo(1);
    }
}
