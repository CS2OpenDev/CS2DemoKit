#region

using CS2DemoKit.Analysis.Graphs;
using CS2DemoKit.Analysis.Nodes;
using CS2DemoKit.Parser;
using CS2DemoKit.Parser.GameEvents;
using CS2DemoKit.TestSupport;
using TUnit.Core.Exceptions;

#endregion

namespace CS2DemoKit.Analysis.Tests.RulesV2;

/// <summary>
///     <c>bucket:</c> vertical golden: the v2 <c>bucket:</c> kind, compiled by the planner and
///     evaluated on the reference demo, must produce per-player, per-weapon bucket counts
///     <b>identical</b> to an independent C# fold over <c>demo.AllGameEvents</c> replicating the
///     kill view's baked condition (killer == subject, killer != victim). The fold replaces the
///     retired v1 <c>keyed_counter</c> comparison arm as the oracle. Parses the demo, so
///     <see cref="NotInParallelAttribute" /> and the shared parse cache apply.
/// </summary>
[Category("Integration")]
[NotInParallel]
public class BucketKindGoldenTests
{
    private const string BucketId = "kills_by_weapon";

    // v2: the same per-weapon bucket via the new bucket: kind. `bucket: kill` lowers to the identical
    // event + condition (killer == player.slot ∧ killer ≠ victim, via the kill view's actor binding +
    // baked suicide exclusion); key: event.Weapon selects the same per-weapon bucket.
    private const string V2Yaml = """
                                  ruleset: bucket_v2
                                  for: each_player
                                  stats:
                                    kills_by_weapon:
                                      bucket: kill
                                      key: event.Weapon
                                      per: match
                                  """;

    /// <summary>v2 bucket == the independent event fold: identical per-player, per-weapon bucket maps.</summary>
    [Test]
    public async Task Bucket_MatchesEventFoldPerPlayer()
    {
        ParsedDemo demo = DemoTestHelper.GetOrParse(DemoTestHelper.RequireDemo());

        BuildResult v2Build = V2KindGoldenSupport.CompileV2(demo, V2Yaml);
        AnalysisRun v2Run = DemoAnalysis.Evaluate(demo, v2Build);

        Dictionary<int, Dictionary<string, double>> expected = FoldKillBuckets(demo);
        Dictionary<int, Dictionary<string, double>> v2 = ReadBuckets(v2Run);

        // The loop below walks v2 only, so a player the engine dropped entirely would never be
        // compared and the test would pass on the survivors. v2 may legitimately carry extra slots
        // (a materialized player with no kills folds to an empty bucket set), so this is a superset
        // check rather than equivalence.
        List<int> missing = expected.Keys.Where(slot => !v2.ContainsKey(slot)).OrderBy(s => s).ToList();
        await Assert.That(missing).IsEmpty()
            .Because("every slot with a kill in the event fold must appear in the engine's buckets");

        int totalBuckets = 0;
        double totalKills = 0;
        foreach ((int slot, Dictionary<string, double> b2) in v2)
        {
            Dictionary<string, double> b1 = expected.GetValueOrDefault(slot) ?? [];

            await Assert.That(b2.Keys.ToHashSet()).IsEquivalentTo(b1.Keys.ToHashSet())
                .Because($"slot{slot}: the weapon key sets must match the event fold");
            foreach ((string weapon, double count) in b1)
            {
                await Assert.That(b2.GetValueOrDefault(weapon)).IsEqualTo(count)
                    .Because($"slot{slot} {weapon}: v2 bucket must equal the event fold ({count})");
                totalKills += count;
            }

            totalBuckets += b1.Count;
        }

        Console.WriteLine($"[bucket] compared {v2.Count} players, {totalBuckets} weapon buckets, "
                          + $"{totalKills:F0} total kills");
        if (totalBuckets == 0)
        {
            throw new SkipTestException("no player got a weapon kill in this demo — bucket comparison is vacuous");
        }
    }

    /// <summary>
    ///     The independent oracle: fold every <see cref="PlayerDeathEvent" /> into slot →
    ///     (weapon → count), killer-attributed, excluding suicides — the same condition the kill
    ///     view bakes into the lowered <c>bucket: kill</c> edge.
    /// </summary>
    private static Dictionary<int, Dictionary<string, double>> FoldKillBuckets(ParsedDemo demo)
    {
        Dictionary<int, Dictionary<string, double>> bySlot = new();
        foreach (GameEvent ev in demo.AllGameEvents)
        {
            if (ev.Payload is not PlayerDeathEvent death || death.Attacker == death.UserId)
            {
                continue;
            }

            // A world kill carries the 0xFFFF no-player sentinel as its attacker. It is not any
            // player's kill, and the engine will not materialize a subject for it, so an oracle that
            // counted it would demand a bucket that can never exist.
            if (death.Attacker is < 0 or >= 64)
            {
                continue;
            }

            if (!bySlot.TryGetValue(death.Attacker, out Dictionary<string, double>? buckets))
            {
                bySlot[death.Attacker] = buckets = new Dictionary<string, double>(StringComparer.Ordinal);
            }

            buckets[death.Weapon] = buckets.GetValueOrDefault(death.Weapon) + 1;
        }

        return bySlot;
    }

    /// <summary>Reads each player's keyed weapon buckets into slot → (weapon → count).</summary>
    private static Dictionary<int, Dictionary<string, double>> ReadBuckets(AnalysisRun run)
    {
        EvaluationResult result = run.Snapshots
                                  ?? throw new InvalidOperationException("evaluation produced no snapshots");
        Dictionary<int, Dictionary<string, double>> byslot = new();
        foreach (PerPlayerNodeTemplate.MaterializedPlayer mp in result.MaterializedPlayers)
        {
            KeyedCounterNode? node = mp.Nodes
                .OfType<KeyedCounterNode>()
                .FirstOrDefault(n => string.Equals(n.RuleId, BucketId, StringComparison.Ordinal));
            byslot[mp.PlayerSlot] = node is null
                ? []
                : new Dictionary<string, double>(node.Buckets, StringComparer.Ordinal);
        }

        return byslot;
    }
}
