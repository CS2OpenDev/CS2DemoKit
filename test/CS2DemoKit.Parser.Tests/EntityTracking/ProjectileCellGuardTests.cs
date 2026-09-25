#region

using System.Numerics;
using CS2DemoKit.Parser.EntityTracking;

#endregion

namespace CS2DemoKit.Parser.Tests.EntityTracking;

/// <summary>
///     The zero-cell guard behind <see cref="ProjectileSample.Position" />, on hand-built entities,
///     and proof that the public pawn reconstruction does not apply it.
/// </summary>
[Category("Unit")]
public class ProjectileCellGuardTests
{
    private static EntityState Projectile(int cx, int cy, int cz)
    {
        EntityTracker tracker = new();
        EntityState e = tracker.CurrentEntities.GetOrCreate(300, GrenadeProjectileClasses.HEGrenade, 1);
        e.Set("CBodyComponent.m_cellX", cx);
        e.Set("CBodyComponent.m_cellY", cy);
        e.Set("CBodyComponent.m_cellZ", cz);
        e.Set("CBodyComponent.m_vecX", 10f);
        e.Set("CBodyComponent.m_vecY", 20f);
        e.Set("CBodyComponent.m_vecZ", 30f);
        return e;
    }

    [Test]
    [Arguments(0, 30, 31)]
    [Arguments(30, 0, 31)]
    [Arguments(30, 31, 0)]
    public async Task Guarded_ZeroCell_IsNull(int cx, int cy, int cz) =>
        await Assert.That(PositionUtil.CellToWorldCore(Projectile(cx, cy, cz), true)).IsNull();

    [Test]
    public async Task Guarded_NonZeroCells_AreTheAxisValues() =>
        await Assert.That(PositionUtil.CellToWorldCore(Projectile(30, 31, 33), true))
            .IsEqualTo(new Vector3(PositionUtil.Axis(30, 10f), PositionUtil.Axis(31, 20f), PositionUtil.Axis(33, 30f)));

    [Test]
    public async Task Guarded_UnseenCell_IsNull()
    {
        EntityTracker tracker = new();
        EntityState e = tracker.CurrentEntities.GetOrCreate(300, GrenadeProjectileClasses.Decoy, 1);
        e.Set("CBodyComponent.m_cellX", 30);

        await Assert.That(PositionUtil.CellToWorldCore(e, true)).IsNull();
    }

    [Test]
    public async Task PublicCellToWorld_StillReconstructsAZeroCell() =>
        await Assert.That(PositionUtil.CellToWorld(Projectile(0, 31, 33)))
            .IsEqualTo(new Vector3(PositionUtil.Axis(0, 10f), PositionUtil.Axis(31, 20f), PositionUtil.Axis(33, 30f)));
}
