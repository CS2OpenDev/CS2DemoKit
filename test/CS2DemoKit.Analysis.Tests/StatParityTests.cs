#region

using CS2DemoKit.Analysis.GoldenStats;
using CS2DemoKit.Parser;
using CS2DemoKit.TestSupport;
using TUnit.Core.Exceptions;

#endregion

namespace CS2DemoKit.Analysis.Tests;

/// <summary>
///     Runs the engine over a demo and compares every canonical stat to that demo's committed
///     <c>expected.golden.json</c>, at <b>zero tolerance</b>, matching players by display name.
///     <para>
///         Only <c>expected</c> is committed. It is the assertion: values pinned from engine output
///         verified during the parity-hardening passes. <c>ours</c> is what the code currently
///         produces, so it is never stored, only derived (<see cref="LiveGoldenStats" />). A failure
///         means the engine drifted from its pinned output, which is a regression until proven
///         otherwise.
///     </para>
///     <para>
///         This used to load <c>ours</c> from disk too, so it compared two committed files that
///         agreed by construction and could not fail. Because it never opened a <c>.dem</c> it did
///         not even skip, and reported green while asserting nothing about the engine. Deriving
///         <c>ours</c> is what makes the gate real; the cost is that a demo which is not on this
///         machine now skips instead of silently passing.
///     </para>
///     <para>
///         Never edit <c>expected.golden.json</c> to absorb a diff. Fix the engine, or hand-verify
///         the value and re-pin deliberately, recording it by moving the file's
///         <c>provider_version</c> from <c>null</c> to a dated hand-verified marker.
///     </para>
///     <para>
///         A stat that is <c>null</c> on either side is skipped: null means "not reported", not
///         zero.
///     </para>
/// </summary>
[Category("Oracle")]
public class StatParityTests
{
    // Fixed so a re-pin diffs only on values; the converter stamps DateTime.UtcNow otherwise.
    private const string PinnedTimestamp = "2026-08-19T00:00:00.0000000Z";

    // The canonical stat universe the gate covers. A stat absent from the pinned file null-skips
    // until that file is re-pinned.
    private static readonly string[] _stats =
    [
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
    ];

    /// <summary>
    ///     One case per demo, because the engine run is the expensive part: all stats of all players
    ///     are compared from a single evaluation.
    /// </summary>
    /// <param name="demoId">The fixture directory name.</param>
    [Test]
    [MethodDataSource(nameof(DemoIds))]
    public async Task OursVsExpected_StatParity(string demoId)
    {
        // A fixture directory is named for its demo, so the demo file follows from the id.
        string demoFileName = demoId + ".dem";
        string expectedPath = Path.Combine(GoldenStatsTestHelper.FindFixtureDir(demoId), "expected.golden.json");
        bool pinning = Environment.GetEnvironmentVariable("PIN_EXPECTED") == "1";

        // Checked before parsing: a directory holding other artifacts (an entity-field snapshot,
        // say) is not a parity fixture and must not cost a multi-GB parse to discover that.
        if (!pinning && !File.Exists(expectedPath))
        {
            throw new SkipTestException($"No parity fixture for '{demoId}' ({expectedPath}).");
        }

        // Skips when this machine does not have the demo. The bench demos are gitignored; the
        // committed sample resolves everywhere, so a bare clone still exercises the gate.
        string demoPath = DemoTestHelper.RequireDemo(demoFileName);
        string demoSha = DemoTestHelper.Sha256OfFile(demoPath);
        ParsedDemo demo = DemoTestHelper.GetOrParse(demoPath);

        GoldenStatsDocument ours = LiveGoldenStats.Derive(demoFileName, demo, demoSha, PinnedTimestamp);

        if (pinning)
        {
            GoldenStatsSerializer.WriteToFile(ours with { Provider = "expected" }, expectedPath);

            // Skip, not return. A re-pin asserts nothing, and reporting it as passed is exactly the
            // green-but-empty result this gate exists to remove.
            throw new SkipTestException($"Re-pinned {expectedPath}. Review the diff before committing.");
        }

        GoldenStatsDocument expected = GoldenStatsSerializer.ReadFromFile(expectedPath);
        await AssertSameDemo(expected, demoPath, demoSha);

        List<string> divergences = [];
        int compared = 0;
        int nullSkipped = 0;

        foreach ((string player, PlayerStatsRecord expectedRecord) in expected.Players)
        {
            if (!ours.Players.TryGetValue(player, out PlayerStatsRecord? oursRecord))
            {
                divergences.Add($"  {player,-32} missing from the live run entirely");
                continue;
            }

            foreach (string stat in _stats)
            {
                double? want = expectedRecord.Stats.TryGetValue(stat, out double? e) ? e : null;
                double? got = oursRecord.Stats.TryGetValue(stat, out double? o) ? o : null;
                if (want is null || got is null)
                {
                    nullSkipped++;
                    continue;
                }

                compared++;
                double delta = got.Value - want.Value;
                if (delta != 0.0)
                {
                    divergences.Add(
                        $"  {player,-24} {stat,-14} ours={got,9:F2}  expected={want,9:F2}  delta={(delta >= 0 ? "+" : "")}{delta:F2}");
                }
            }
        }

        Console.WriteLine(
            $"{demoId} | live vs expected | compared={compared} divergences={divergences.Count} null-skipped={nullSkipped}");
        if (divergences.Count > 0)
        {
            Console.WriteLine(string.Join('\n', divergences));
        }

        await Assert.That(compared).IsGreaterThan(0)
            .Because("a run that compares nothing is the failure mode this gate was rebuilt to remove");
        await Assert.That(divergences).IsEmpty();
    }

    /// <summary>One case per fixture directory; empty when there are no fixtures.</summary>
    /// <returns>The demo ids.</returns>
    public static IEnumerable<string> DemoIds() => GoldenStatsTestHelper.AllDemoIds();

    /// <summary>
    ///     Refuses to compare a live run against a reference pinned from different bytes. Two files
    ///     can hold the same match and still differ, and the resulting divergences read exactly like
    ///     engine regressions, which is the most expensive kind of wrong answer this suite can give.
    ///     <para>
    ///         A committed demo failing this is a repo inconsistency, so it fails. A demo from
    ///         outside the repo is simply not the one the reference describes, which is the same
    ///         situation as not having it at all, so it skips.
    ///     </para>
    /// </summary>
    private static async Task AssertSameDemo(GoldenStatsDocument expected, string demoPath, string demoSha)
    {
        if (expected.DemoSha256 is not { } pinned || string.Equals(pinned, demoSha, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        string message =
            $"'{Path.GetFileName(demoPath)}' hashes to {demoSha}, but the fixture was pinned from "
            + $"{pinned}. The reference does not describe this file.";

        bool committed = demoPath.Contains(Path.Combine("tests", "assets"), StringComparison.Ordinal);
        if (!committed)
        {
            throw new SkipTestException(message + " Supply the demo it was pinned from, or re-pin.");
        }

        await Assert.That(demoSha).IsEqualTo(pinned)
            .Because(message + " This demo is committed, so the two are out of sync in the repo.");
    }
}
