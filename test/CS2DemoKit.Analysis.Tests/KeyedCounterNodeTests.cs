#region

using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Analysis.Nodes;

#endregion

namespace CS2DemoKit.Analysis.Tests;

/// <summary>
///     Unit tests for <see cref="KeyedCounterNode" /> (F3, per-weapon dimensions): bucket
///     accumulation, activation semantics, the invariant-culture total display, and the
///     snapshot-exclusion marker.
/// </summary>
[Category("Unit")]
public class KeyedCounterNodeTests
{
    /// <summary>Fresh node: inactive, no buckets, null display/numeric values.</summary>
    [Test]
    public async Task FreshNode_IsInactive()
    {
        KeyedCounterNode node = new("kills_by_weapon", "kills_by_weapon");

        await Assert.That(node.IsActive).IsFalse();
        await Assert.That(node.Buckets).IsEmpty();
        await Assert.That(node.Total).IsEqualTo(0.0);
        await Assert.That(node.GetDisplayValue()).IsNull();
        await Assert.That(node.GetNumericValue()).IsNull();
    }

    /// <summary>Add accumulates per key and across the total; first add activates the node.</summary>
    [Test]
    public async Task Add_AccumulatesPerKeyAndTotal()
    {
        KeyedCounterNode node = new("kills_by_weapon", "kills_by_weapon");

        node.Add("ak47", 1);
        await Assert.That(node.IsActive).IsTrue();

        node.Add("ak47", 1);
        node.Add("awp", 1);

        await Assert.That(node.Buckets.Count).IsEqualTo(2);
        await Assert.That(node.Buckets["ak47"]).IsEqualTo(2.0);
        await Assert.That(node.Buckets["awp"]).IsEqualTo(1.0);
        await Assert.That(node.Total).IsEqualTo(3.0);
        await Assert.That(node.GetNumericValue()).IsEqualTo(3f);
    }

    /// <summary>Display value is the cross-bucket total in invariant "0.##" (no trailing zeros, dot decimal).</summary>
    [Test]
    public async Task DisplayValue_IsInvariantTotal()
    {
        KeyedCounterNode node = new("damage_by_weapon", "damage_by_weapon");

        node.Add("ak47", 27);
        node.Add("awp", 108);
        await Assert.That(node.GetDisplayValue()).IsEqualTo("135");

        node.Add("hegrenade", 0.5);
        await Assert.That(node.GetDisplayValue()).IsEqualTo("135.5");
    }

    /// <summary>
    ///     Keyed nodes are snapshot-excluded (dictionary values don't fit the scalar NodeSnapshot
    ///     model) but are NOT transient — a per-dispatch reset would wipe the per-game totals.
    /// </summary>
    [Test]
    public async Task Node_IsSnapshotExcluded_ButNotTransient()
    {
        StateNode node = new KeyedCounterNode("kills_by_weapon", "kills_by_weapon");

        await Assert.That(node is ISnapshotExcludedNode).IsTrue();
        await Assert.That(node is ITransientNode).IsFalse();
        await Assert.That(node is IRoundScopedNode).IsFalse();
    }

    // ── C8 named reducers (Combine) ─────────────────────────────────────────────

    /// <summary>Default mode is Add; Combine on an Add node folds exactly like Add (v1 byte-identity).</summary>
    [Test]
    public async Task Combine_Add_MatchesAdd()
    {
        KeyedCounterNode node = new("dmg", "dmg");
        await Assert.That(node.ReduceMode).IsEqualTo(KeyedReduceMode.Add);

        node.Combine("ak47", 30);
        node.Combine("ak47", 20);
        node.Combine("awp", 100);

        await Assert.That(node.Buckets["ak47"]).IsEqualTo(50.0);
        await Assert.That(node.Buckets["awp"]).IsEqualTo(100.0);
        await Assert.That(node.Total).IsEqualTo(150.0);
    }

    /// <summary>Min keeps the smallest per key; an unseen key takes the first value (never min-against-0).</summary>
    [Test]
    public async Task Combine_Min_KeepsSmallest_FirstValueOnUnseen()
    {
        KeyedCounterNode node = new("min_hp", "min_hp", null, KeyedReduceMode.Min);

        node.Combine("s1mple", 80); // unseen → 80 (not min(0, 80) = 0)
        await Assert.That(node.Buckets["s1mple"]).IsEqualTo(80.0);
        node.Combine("s1mple", 40); // smaller → 40
        node.Combine("s1mple", 60); // larger → unchanged
        node.Combine("zywoo", 100); // separate key, unseen → 100

        await Assert.That(node.Buckets["s1mple"]).IsEqualTo(40.0);
        await Assert.That(node.Buckets["zywoo"]).IsEqualTo(100.0);
        await Assert.That(node.Total).IsEqualTo(140.0); // 40 + 100
    }

    /// <summary>Max keeps the largest per key; an unseen key takes the first value (never max-against-0 for negatives).</summary>
    [Test]
    public async Task Combine_Max_KeepsLargest_FirstValueOnUnseen()
    {
        KeyedCounterNode node = new("max_dist", "max_dist", null, KeyedReduceMode.Max);

        node.Combine("s1mple", -50); // unseen → -50 (not max(0, -50) = 0)
        await Assert.That(node.Buckets["s1mple"]).IsEqualTo(-50.0);
        node.Combine("s1mple", -10); // larger → -10
        node.Combine("s1mple", -30); // smaller → unchanged

        await Assert.That(node.Buckets["s1mple"]).IsEqualTo(-10.0);
    }

    /// <summary>Last overwrites with the most recent value per key.</summary>
    [Test]
    public async Task Combine_Last_Overwrites()
    {
        KeyedCounterNode node = new("last_hp", "last_hp", null, KeyedReduceMode.Last);

        node.Combine("s1mple", 100);
        node.Combine("s1mple", 55);
        node.Combine("s1mple", 12);

        await Assert.That(node.Buckets["s1mple"]).IsEqualTo(12.0);
        await Assert.That(node.Total).IsEqualTo(12.0);
    }

    /// <summary>First writes once per key (the first value), keeping it against later writes.</summary>
    [Test]
    public async Task Combine_First_WritesOnce()
    {
        KeyedCounterNode node = new("first_hp", "first_hp", null, KeyedReduceMode.First);

        node.Combine("s1mple", 100);
        node.Combine("s1mple", 55); // key seen → kept
        node.Combine("zywoo", 90);

        await Assert.That(node.Buckets["s1mple"]).IsEqualTo(100.0);
        await Assert.That(node.Buckets["zywoo"]).IsEqualTo(90.0);
        await Assert.That(node.Total).IsEqualTo(190.0);
    }

    /// <summary>RuleId (table identity) is carried independently of the display name.</summary>
    [Test]
    public async Task RuleId_IndependentOfDisplayName()
    {
        KeyedCounterNode node = new("kills_by_weapon", "Kills by Weapon", "s1mple");

        await Assert.That(node.RuleId).IsEqualTo("kills_by_weapon");
        await Assert.That(node.Name).IsEqualTo("Kills by Weapon");
        await Assert.That(node.Subtitle).IsEqualTo("s1mple");
    }
}
