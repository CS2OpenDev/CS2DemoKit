#region

using CS2DemoKit.Analysis.Events;
using CS2DemoKit.Analysis.RulesetsV2.Model;

#endregion

namespace CS2DemoKit.Analysis.Tests.RoundFacts;

/// <summary>The scheduler's due-tick arithmetic, without a demo.</summary>
[Category("Unit")]
public class CadenceSchedulerTests
{
    private static CadenceScheduler Scheduler(int ticks) =>
        new([
            new CadencePlan(
                new CadenceDef("c", ticks, null, CadenceAnchor.RoundFreezeEnd, CadenceStop.None, default),
                new HashSet<Type> { typeof(CadenceSchedulerTests) },
                new HashSet<Type>())
        ]);

    [Test]
    public async Task Drain_FiresOncePerPeriod()
    {
        CadenceScheduler scheduler = Scheduler(64);
        List<CadenceTickEvent> fired = [];
        scheduler.Observe(typeof(CadenceSchedulerTests), 1000);
        scheduler.Drain(1000, fired);
        scheduler.Drain(1063, fired);
        scheduler.Drain(1130, fired);

        await Assert.That(fired.Select(e => (e.FrameNumber, e.Step)).ToList())
            .IsEquivalentTo(new List<(int, int)> { (1000, 0), (1130, 1), (1130, 2) });
    }

    [Test]
    public async Task Drain_PeriodPastInt32FromAnchor_FiresStepZeroOnly()
    {
        CadenceScheduler scheduler = Scheduler(int.MaxValue);
        List<CadenceTickEvent> fired = [];
        scheduler.Observe(typeof(CadenceSchedulerTests), 200_000);
        scheduler.Drain(200_000, fired);
        scheduler.Drain(int.MaxValue, fired);

        await Assert.That(fired.Select(e => e.Step).ToList()).IsEquivalentTo(new List<int> { 0 });
    }
}
