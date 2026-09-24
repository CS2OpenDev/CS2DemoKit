#region

using CS2DemoKit.Parser.EntityTracking;

#endregion

namespace CS2DemoKit.Parser.Tests.EntityTracking;

/// <summary>
///     <see cref="PawnLookup.ResolveThrowerSlot" /> on hand-built chains, and the class list the
///     projectile walk filters on. Demo-independent: every case is a handle value or a class name.
/// </summary>
[Category("Unit")]
public class PawnLookupThrowerTests
{
    /// <summary>
    ///     Builds <c>projectile.m_hThrower -> pawn.m_hController</c>, the chain
    ///     <see cref="PawnLookup.ResolveThrowerSlot" /> walks, and returns the projectile.
    /// </summary>
    private static (EntityTracker Tracker, EntityState Projectile) ThrowerChain(uint controllerHandle)
    {
        EntityTracker tracker = new();

        EntityState pawn = tracker.CurrentEntities.GetOrCreate(5, "CCSPlayerPawn", 1);
        pawn.Set("m_hController", controllerHandle);

        EntityState projectile = tracker.CurrentEntities.GetOrCreate(700, GrenadeProjectileClasses.Molotov, 1);
        projectile.Set("m_hThrower", 5u | (1u << 14));

        return (tracker, projectile);
    }

    // This is the site with no empty slot to absorb a bad fold: it returns a player slot outright,
    // so an unfolded 0x00FFFFFF became slot 16382 in the digest rather than the documented -1.
    [Test]
    public async Task ResolveThrowerSlot_DeadThrower_IsMinusOne()
    {
        (EntityTracker tracker, EntityState projectile) = ThrowerChain(0x00FF_FFFFu);

        await Assert.That(PawnLookup.ResolveThrowerSlot(tracker, projectile)).IsEqualTo(-1)
            .Because("a dead pawn's controller handle names no player, and 16382 is not a slot");
    }

    [Test]
    public async Task ResolveThrowerSlot_LiveThrower_IsControllerIndexMinusOne()
    {
        (EntityTracker tracker, EntityState projectile) = ThrowerChain(3u | (1u << 14));

        await Assert.That(PawnLookup.ResolveThrowerSlot(tracker, projectile)).IsEqualTo(2)
            .Because("slot is controller index minus one, and the guard must not eat the live case");
    }

    [Test]
    public async Task ResolveThrowerSlot_NoThrowerHandle_IsMinusOne()
    {
        EntityTracker tracker = new();
        EntityState projectile = tracker.CurrentEntities.GetOrCreate(700, GrenadeProjectileClasses.HEGrenade, 1);

        await Assert.That(PawnLookup.ResolveThrowerSlot(tracker, projectile)).IsEqualTo(-1);
    }

    [Test]
    public async Task ResolveThrowerSlot_ThrowerSlotEmpty_IsMinusOne()
    {
        EntityTracker tracker = new();
        EntityState projectile = tracker.CurrentEntities.GetOrCreate(700, GrenadeProjectileClasses.Flashbang, 1);
        projectile.Set("m_hThrower", 9u | (1u << 14));

        await Assert.That(PawnLookup.ResolveThrowerSlot(tracker, projectile)).IsEqualTo(-1)
            .Because("the handle names an index nothing occupies");
    }

    [Test]
    [Arguments("CSmokeGrenadeProjectile")]
    [Arguments("CMolotovProjectile")]
    [Arguments("CHEGrenadeProjectile")]
    [Arguments("CFlashbangProjectile")]
    [Arguments("CDecoyProjectile")]
    public async Task GrenadeProjectileClasses_ContainsTheFive(string className) =>
        await Assert.That(GrenadeProjectileClasses.Contains(className)).IsTrue();

    // The held weapons and the props a smoke slot is reused by in the same frame must not pass.
    [Test]
    [Arguments("CWeaponHEGrenade")]
    [Arguments("CHEGrenade")]
    [Arguments("CSmokeGrenade")]
    [Arguments("CPhysicsPropMultiplayer")]
    [Arguments("cmolotovprojectile")]
    [Arguments("")]
    [Arguments(null)]
    public async Task GrenadeProjectileClasses_RejectsOtherClasses(string? className) =>
        await Assert.That(GrenadeProjectileClasses.Contains(className)).IsFalse();

    [Test]
    public async Task GrenadeProjectileClasses_AllListsFiveDistinctClasses()
    {
        await Assert.That(GrenadeProjectileClasses.All.Count).IsEqualTo(5);
        await Assert.That(GrenadeProjectileClasses.All.Distinct(StringComparer.Ordinal).Count()).IsEqualTo(5);
        await Assert.That(GrenadeProjectileClasses.All.All(GrenadeProjectileClasses.Contains)).IsTrue();
    }
}
