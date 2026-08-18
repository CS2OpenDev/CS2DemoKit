#region

using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Analysis.Nodes;

#endregion

namespace CS2DemoKit.Analysis.Tests;

/// <summary>
///     The windowed multi-kill pulse node. Exercises the four semantic rules the
///     design research called out, driving the node exactly as the evaluator does: the per-dispatch
///     transient <c>Reset()</c> runs BEFORE each <c>Observe</c> (mirroring
///     StateGraphEvaluator's transient-reset loop), so each pulse is an isolated rising edge.
/// </summary>
[Category("Unit")]
public class WindowedHighlightPulseNodeTests
{
    /// <summary>Runs a tick sequence through a fresh node and returns the ticks at which it pulsed active.</summary>
    private static List<int> PulseTicks(int window, int minCount, params int[] ticks)
    {
        WindowedHighlightPulseNode node = new("burst", null, window, minCount);
        List<int> fired = [];
        foreach (int t in ticks)
        {
            ((ITransientNode)node).Reset(); // evaluator clears the transient pulse before each dispatch
            node.Observe(t);
            if (node.IsActive)
            {
                fired.Add(t);
            }
        }

        return fired;
    }

    [Test]
    public async Task Fires_OnTheNthKillWithinWindow()
    {
        // 3 kills at ticks 0,1,2 — span 2 <= window 5 → pulses on the 3rd.
        await Assert.That(PulseTicks(5, 3, 0, 1, 2)).IsEquivalentTo(new List<int> { 2 });
        // A double (min 2) pulses on the 2nd.
        await Assert.That(PulseTicks(5, 2, 0, 1)).IsEquivalentTo(new List<int> { 1 });
    }

    [Test]
    public async Task SlidingWindow_RejectsKillsSpreadBeyondWindow()
    {
        // Kills at 0,4,8: each consecutive gap is 4 (< window 5), but the first-to-last span is 8 > 5.
        // A consecutive-gap streak WOULD fire here; a true sliding window must NOT.
        await Assert.That(PulseTicks(5, 3, 0, 4, 8)).IsEmpty();
    }

    [Test]
    public async Task FiresOnce_AcrossOneSustainedSpray()
    {
        // A 5-kill spray all inside the rolling window fires the triple exactly ONCE (at the 3rd kill),
        // not again at the 4th/5th — the burst latch holds until the window breaks.
        await Assert.That(PulseTicks(5, 3, 0, 1, 2, 3, 4)).IsEquivalentTo(new List<int> { 2 });
    }

    [Test]
    public async Task ReArms_AfterAGapBreaksTheWindow()
    {
        // Two separate triples: 0,1,2 then (gap) 20,21,22. The gap pushes the span out of window,
        // re-arming the latch, so the second burst fires too.
        await Assert.That(PulseTicks(5, 3, 0, 1, 2, 20, 21, 22)).IsEquivalentTo(new List<int> { 2, 22 });
    }

    [Test]
    public async Task RoundReset_ClearsStateAndNeverEmits()
    {
        WindowedHighlightPulseNode node = new("burst", null, 5, 3);
        foreach (int t in new[] { 0, 1, 2 })
        {
            ((ITransientNode)node).Reset();
            node.Observe(t);
        }

        await Assert.That(node.IsActive).IsTrue().Because("the third kill completed a burst");

        // Round boundary: clears the ring + latch, and must NOT leave the node active (round end is not a highlight).
        ((IRoundScopedNode)node).Reset();
        await Assert.That(node.IsActive).IsFalse().Because("round reset clears the pulse and never emits");

        // A fresh sequence in the new round fires again (state was fully cleared).
        List<int> after = [];
        foreach (int t in new[] { 0, 1, 2 })
        {
            ((ITransientNode)node).Reset();
            node.Observe(t);
            if (node.IsActive)
            {
                after.Add(t);
            }
        }

        await Assert.That(after).IsEquivalentTo(new List<int> { 2 });
    }

    [Test]
    public async Task TransientReset_ClearsThePulseBetweenDispatches()
    {
        WindowedHighlightPulseNode node = new("burst", null, 5, 2);
        ((ITransientNode)node).Reset();
        node.Observe(0);
        ((ITransientNode)node).Reset();
        node.Observe(1);
        await Assert.That(node.IsActive).IsTrue();

        // The evaluator's next transient reset (before the next dispatch) drops the pulse to false.
        ((ITransientNode)node).Reset();
        await Assert.That(node.IsActive).IsFalse();
    }
}
