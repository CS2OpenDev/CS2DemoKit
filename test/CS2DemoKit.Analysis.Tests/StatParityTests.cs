#region

using CS2DemoKit.Analysis.GoldenStats;

#endregion

namespace CS2DemoKit.Analysis.Tests;

/// <summary>
///     Cross-provider parity tests. For each (demo, stat) pair,
///     loads two providers' <c>*.golden.json</c> files, iterates common players
///     (matched by display name), and asserts each stat is within the per-pair
///     tolerance. Two test methods, same underlying comparison:
///     <list type="bullet">
///         <item>
///             <c>OursVsLeetify_StatParity</c> — runs against Leetify's
///             converted reference. <b>Leetify is the current gold standard</b>
///             until hand-verified <c>expected.golden.json</c> data covers the
///             same stats. Uses the per-stat <see cref="_tolerances" /> table:
///             stats with achieved parity stay at <c>0.0</c>, and stats with
///             investigated-then-deferred gaps are pinned at the CURRENT max
///             observed |Δ| (so future drift past today's ceiling still
///             fails). Each non-zero tolerance is cross-referenced to its
///             entry in <c>/KNOWN-AND-SUSPECTED-ISSUES.md</c>. The closure
///             path is: tighten the parser, lower the ceiling, update the
///             issues doc — never widen the tolerance to absorb a regression.
///         </item>
///         <item>
///             <c>OursVsExpected_StatParity</c> — runs against
///             <c>expected.golden.json</c> with zero tolerance.
///             <para>
///                 <b>IMPORTANT:</b> today's <c>expected.golden.json</c> files
///                 are <b>agreement-based seeds</b> — they were written from
///                 the values where our parser and Leetify agreed at the time
///                 of seeding. They are NOT yet hand-verified ground truth.
///                 What this test actually catches today is "our parser
///                 drifted from a frozen snapshot of itself" — useful as
///                 regression detection, but not load-bearing in the sense
///                 the audit's F8 sunset plan requires.
///             </para>
///             <para>
///                 The semantics flip when expected is replaced with
///                 hand-verified values: same test, same zero tolerance, but
///                 now failures mean "our parser disagrees with what a human
///                 confirmed by watching the demo." Until then, treat
///                 Leetify-side comparisons as the source of truth and
///                 expected-side as a parser-regression tripwire.
///             </para>
///             <para>
///                 When the expected fixture is missing for a demo, the test
///                 skips cleanly via <c>SkipTestException</c>.
///             </para>
///         </item>
///     </list>
///     <para>
///         A stat that is <c>null</c> in either provider is silently skipped —
///         null means "this provider doesn't report this stat," not "the
///         provider reports zero."
///     </para>
/// </summary>
[Category("Oracle")]
public class StatParityTests
{
    // Per-stat tolerance for ours-vs-leetify comparisons.
    //
    // Tolerance rationale, in three groups:
    //
    //   1. STRICT (=0.0) — counts where we've achieved exact parity with
    //      Leetify across all 5 bench demos. A divergence here is a real
    //      regression. Keep at 0.0.
    //
    //   2. PROVIDER ROUNDING — small float headroom for legitimate display-
    //      precision differences between providers. Kd is computed by both
    //      sides and rounded; 0.02 covers observed jitter.
    //
    //   3. DOCUMENTED RESIDUAL — stats with known parity gaps that were
    //      investigated in the May 2026 correctness pass and deferred with
    //      written rationale in /KNOWN-AND-SUSPECTED-ISSUES.md. Each
    //      tolerance below is set to the CURRENT max |Δ| observed across
    //      the 5-demo bench suite (as of the May 2026 pass). Setting tolerance
    //      at exactly the current ceiling preserves the regression-tripwire
    //      property — any future drift that pushes |Δ| above today's
    //      ceiling fails the test. Closure path: tighten the parser, then
    //      tighten the tolerance here, then update the issues doc.
    //
    // The strict ground-truth gate is OursVsExpected_StatParity (zero
    // tolerance against hand-verified expected.golden.json). When expected
    // fixtures cover more demos, this Leetify-side comparison becomes
    // secondary signal — until then it's the primary parity check.
    private static readonly Dictionary<string, double> _tolerances =
        new(StringComparer.Ordinal)
        {
            // ── Group 1: STRICT (achieved exact parity) ─────────────────
            [CanonicalStatNames.Kills] = 0.0,
            [CanonicalStatNames.Deaths] = 0.0,
            [CanonicalStatNames.RoundsSurvived] = 0.0,
            [CanonicalStatNames.CtRoundsWon] = 0.0,
            [CanonicalStatNames.RoundsWon] = 0.0,
            [CanonicalStatNames.Multi2K] = 0.0,
            [CanonicalStatNames.Multi3K] = 0.0,
            [CanonicalStatNames.Multi4K] = 0.0,
            [CanonicalStatNames.Multi5K] = 0.0,

            // ── Group 2: PROVIDER ROUNDING (computed-and-rounded) ───────
            [CanonicalStatNames.Kd] = 0.02,

            // ── Group 3: DOCUMENTED RESIDUAL (see KNOWN-AND-SUSPECTED-ISSUES.md) ──
            //   Issue S1 (Shots overcount, +1 to +2 uniform direction):
            [CanonicalStatNames.ShotsFired] = 2.0,
            //   Issue S2 (EnemyDmg undercount, -1 to -3 uniform direction):
            [CanonicalStatNames.EnemyDamage] = 3.0,
            //   Issue S3 (TrdK undercount, max -2 per player):
            [CanonicalStatNames.TradeKills] = 2.0,
            //   Issue S4 (KAST% bound to S3; one missing T per affected player ~ 1/19 round):
            [CanonicalStatNames.KastPct] = 10.0,
            //   Issue S5 (HLTV formula choice, not a parser bug):
            [CanonicalStatNames.HltvRating] = 0.4,
            //   Issue S6 (ADR derived from EnemyDmg; rounding-precision residual):
            [CanonicalStatNames.Adr] = 0.2,
            //   Issue S6 (HS% derived from HSKills/Kills):
            [CanonicalStatNames.HsPct] = 3.0,
            //   Issue S7 (TotalA mixed-sign, ±1 per affected player):
            [CanonicalStatNames.Assists] = 1.0,
            //   Issue S8 (HitFoe mixed-sign, max ±4):
            [CanonicalStatNames.ShotsHitFoe] = 4.0
        };

    /// <summary>
    ///     Compares our parser output to <c>expected.golden.json</c> with
    ///     <b>zero tolerance</b>. See the class summary for the full posture
    ///     — today's expected files are agreement-based seeds, not yet
    ///     hand-verified, so this test functions as a parser-regression
    ///     tripwire rather than a ground-truth check. Leetify
    ///     (<see cref="OursVsLeetify_StatParity" />) is the current gold
    ///     standard. Skips cleanly when expected.golden.json is missing.
    /// </summary>
    [Test]
    [MethodDataSource(nameof(ParityCases))]
    public async Task OursVsExpected_StatParity((string DemoId, string Stat) c) =>
        await CompareStat(c.DemoId, c.Stat, "ours", "expected", 0.0);

    /// <summary>
    ///     Compares our parser output to Leetify's API response for each
    ///     (demo, stat) pair, using the per-stat tolerance table. Failures
    ///     here populate the parity-gap backlog.
    /// </summary>
    [Test]
    [MethodDataSource(nameof(ParityCases))]
    public async Task OursVsLeetify_StatParity((string DemoId, string Stat) c) =>
        await CompareStat(c.DemoId, c.Stat, "ours", "leetify", _tolerances[c.Stat]);

    /// <summary>
    ///     Data source feeding the parity tests one (demo, stat) pair at a time.
    ///     Empty when no fixtures exist — no test cases get generated, which is
    ///     the right default until the bench has been run.
    /// </summary>
    public static IEnumerable<(string DemoId, string Stat)> ParityCases()
    {
        foreach (string demoId in GoldenStatsTestHelper.AllDemoIds())
        {
            foreach (string stat in _tolerances.Keys)
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
