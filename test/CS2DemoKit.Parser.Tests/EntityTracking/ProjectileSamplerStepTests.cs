#region

using System.Numerics;
using CS2DemoKit.Parser.EntityTracking;

#endregion

namespace CS2DemoKit.Parser.Tests.EntityTracking;

/// <summary>
///     <see cref="ProjectileSampler" />'s per-frame step on hand-built entity sets, for the guards no
///     demo on hand reaches: the zero-cell <c>Position</c> guard (no projectile has a zero cell once
///     #56 is fixed) and the guard against following one projectile twice when
///     <see cref="EntityTracker.EntityCreated" /> fires for a slot already followed.
/// </summary>
[Category("Unit")]
public class ProjectileSamplerStepTests
{
    private const int Index = 300;

    private static EntityState Put(EntityTracker tracker, int serial, int cx, int cy, int cz)
    {
        EntityState e = tracker.CurrentEntities.GetOrCreate(Index, GrenadeProjectileClasses.HEGrenade, serial);
        e.Set("CBodyComponent.m_cellX", cx);
        e.Set("CBodyComponent.m_cellY", cy);
        e.Set("CBodyComponent.m_cellZ", cz);
        e.Set("CBodyComponent.m_vecX", 10f);
        e.Set("CBodyComponent.m_vecY", 20f);
        e.Set("CBodyComponent.m_vecZ", 30f);
        return e;
    }

    private static List<ProjectileSample> Step(ProjectileSampler.Slots slots, EntityTracker tracker, int frameIndex,
        params int[] created)
    {
        List<ProjectileSample> output = [];
        slots.Step(tracker, [.. created], frameIndex, 1000 + frameIndex, true, output);
        return output;
    }

    [Test]
    [Arguments(0, 30, 31)]
    [Arguments(30, 0, 31)]
    [Arguments(30, 31, 0)]
    public async Task Step_ZeroCell_YieldsNullPosition(int cx, int cy, int cz)
    {
        EntityTracker tracker = new();
        Put(tracker, 1, cx, cy, cz);

        List<ProjectileSample> samples = Step(new ProjectileSampler.Slots(), tracker, 0, Index);

        await Assert.That(samples.Count).IsEqualTo(1);
        await Assert.That(samples[0].Created).IsTrue();
        await Assert.That(samples[0].Position).IsNull();
    }

    [Test]
    public async Task Step_NonZeroCells_YieldTheReconstructedPosition()
    {
        EntityTracker tracker = new();
        Put(tracker, 1, 30, 31, 33);

        List<ProjectileSample> samples = Step(new ProjectileSampler.Slots(), tracker, 0, Index);

        await Assert.That(samples.Count).IsEqualTo(1);
        await Assert.That(samples[0].Position).IsEqualTo(
            new Vector3(PositionUtil.Axis(30, 10f), PositionUtil.Axis(31, 20f), PositionUtil.Axis(33, 30f)));
    }

    /// <summary>
    ///     A delete and a same-serial, same-class re-create in one frame raises EntityCreated for a
    ///     slot the removal pass still matches. That is the projectile already followed, so the frame
    ///     yields its one in-flight sample and no second Created.
    /// </summary>
    [Test]
    public async Task Step_CreatedAgainForAFollowedProjectile_IsNotANewLife()
    {
        EntityTracker tracker = new();
        ProjectileSampler.Slots slots = new();
        Put(tracker, 1, 30, 31, 33);
        await Assert.That(Step(slots, tracker, 0, Index).Count).IsEqualTo(1);

        List<ProjectileSample> samples = Step(slots, tracker, 1, Index);

        await Assert.That(samples.Count).IsEqualTo(1);
        await Assert.That(samples[0].Created).IsFalse();
        await Assert.That(samples[0].Removed).IsFalse();
        await Assert.That(samples[0].FrameIndex).IsEqualTo(1);
    }

    /// <summary>A new serial in a followed slot ends the old life before the new one starts.</summary>
    [Test]
    public async Task Step_NewSerialInAFollowedSlot_RemovesThenCreates()
    {
        EntityTracker tracker = new();
        ProjectileSampler.Slots slots = new();
        Put(tracker, 1, 30, 31, 33);
        Step(slots, tracker, 0, Index);

        tracker.CurrentEntities.Remove(Index);
        Put(tracker, 2, 30, 31, 34);
        List<ProjectileSample> samples = Step(slots, tracker, 1, Index);

        await Assert.That(samples.Count).IsEqualTo(2);
        await Assert.That(samples[0].Serial).IsEqualTo(1);
        await Assert.That(samples[0].Removed).IsTrue();
        await Assert.That(samples[1].Serial).IsEqualTo(2);
        await Assert.That(samples[1].Created).IsTrue();
    }
}
