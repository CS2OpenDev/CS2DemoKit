#region

using CS2DemoKit.Analysis.GoldenStats;

#endregion

namespace CS2DemoKit.Analysis.Tests;

/// <summary>
///     Golden-stats parity tests. For each (demo, stat) pair, loads the
///     <c>ours</c> and <c>expected</c> <c>*.golden.json</c> files, iterates
///     common players (matched by display name), and asserts each stat matches
///     with <b>zero tolerance</b>.
///     <para>
///         <c>ours.golden.json</c> is the engine's snapshot for the demo — the
///         thing being measured. <c>expected.golden.json</c> is the committed
///         reference, seeded from engine output that was verified during the
///         parity-hardening passes. A failure here means the engine drifted
///         from its verified output for that demo — a regression until proven
///         otherwise. The upgrade path for a reference value is
///         hand-verification (a human confirms the number by watching the
///         demo), recorded by moving the file's <c>provider_version</c> from
///         <c>null</c> to a dated hand-verified marker.
///     </para>
///     <para>
///         Never edit <c>expected.golden.json</c> to absorb a diff — fix the
///         engine, or hand-verify the value and re-pin deliberately.
///     </para>
///     <para>
///         A stat that is <c>null</c> in either provider is silently skipped —
///         null means "this provider doesn't report this stat," not "the
///         provider reports zero." When a fixture file is missing, the test
///         skips cleanly via <c>SkipTestException</c>.
///     </para>
/// </summary>
[Category("Oracle")]
public class StatParityTests
{
    // The canonical stat universe the parity gate covers. Every stat both
    // golden files can carry is compared; coverage lives in the fixtures, so
    // adding a stat here without reference values just null-skips until the
    // fixtures are re-pinned.
    private static readonly string[] _stats =
    {
        CanonicalStatNames.Kills,
        CanonicalStatNames.Deaths,
        CanonicalStatNames.Assists,
        CanonicalStatNames.RoundsSurvived,
        CanonicalStatNames.CtRoundsWon,
        CanonicalStatNames.RoundsWon,
        CanonicalStatNames.Multi2K,
        CanonicalStatNames.Multi3K,
        CanonicalStatNames.Multi4K,
        CanonicalStatNames.Multi5K,
        CanonicalStatNames.TradeKills,
        CanonicalStatNames.EnemyDamage,
        CanonicalStatNames.ShotsFired,
        CanonicalStatNames.ShotsHitFoe,
        CanonicalStatNames.Kd,
        CanonicalStatNames.Adr,
        CanonicalStatNames.HsPct,
        CanonicalStatNames.KastPct,
        CanonicalStatNames.HltvRating
    };

    /// <summary>
    ///     Compares the engine's snapshot to <c>expected.golden.json</c> with
    ///     zero tolerance. See the class summary for what a failure means and
    ///     how reference values get upgraded.
    /// </summary>
    [Test]
    [MethodDataSource(nameof(ParityCases))]
    public async Task OursVsExpected_StatParity((string DemoId, string Stat) c) =>
        await CompareStat(c.DemoId, c.Stat, "ours", "expected", 0.0);

    /// <summary>
    ///     Data source feeding the parity tests one (demo, stat) pair at a time.
    ///     Empty when no fixtures exist — no test cases get generated, which is
    ///     the right default until the bench has been run.
    /// </summary>
    public static IEnumerable<(string DemoId, string Stat)> ParityCases()
    {
        foreach (string demoId in GoldenStatsTestHelper.AllDemoIds())
        {
            foreach (string stat in _stats)
            {
                yield return (demoId, stat);
            }
        }
    }

    // ── Shared comparison ─────────────────────────────────────────────────────

    private static async Task CompareStat(
        string demoId, string stat,
        string lhsProvider, string rhsProvider, double tolerance)
    {
        GoldenStatsDocument lhs = GoldenStatsTestHelper.LoadGolden(demoId, lhsProvider);
        GoldenStatsDocument rhs = GoldenStatsTestHelper.LoadGolden(demoId, rhsProvider);

        List<string> divergences = new();
        int compared = 0;
        int nullSkipped = 0;

        foreach ((string player, PlayerStatsRecord lhsRec) in lhs.Players)
        {
            if (!rhs.Players.TryGetValue(player, out PlayerStatsRecord? rhsRec))
            {
                continue;
            }

            double? lhsVal = lhsRec.Stats.TryGetValue(stat, out double? lv) ? lv : null;
            double? rhsVal = rhsRec.Stats.TryGetValue(stat, out double? rv) ? rv : null;

            // Either side missing the stat → "provider doesn't report" → skip.
            if (lhsVal is null || rhsVal is null)
            {
                nullSkipped++;
                continue;
            }

            double delta = lhsVal.Value - rhsVal.Value;
            compared++;
            if (Math.Abs(delta) > tolerance)
            {
                string sign = delta >= 0 ? "+" : "";
                divergences.Add(
                    $"  {player,-32} {lhsProvider}={lhsVal,9:F2}  {rhsProvider}={rhsVal,9:F2}  delta={sign}{delta:F2}");
            }
        }

        Console.WriteLine(
            $"{demoId} | {stat,-18} | {lhsProvider} vs {rhsProvider} | compared={compared} divergences={divergences.Count} null-skipped={nullSkipped} tol=±{tolerance:F2}");
        if (divergences.Count > 0)
        {
            Console.WriteLine(string.Join('\n', divergences));
        }

        await Assert.That(divergences.Count).IsEqualTo(0);
    }
}
