#region

using CS2DemoKit.Analysis.Output;
using CS2DemoKit.Analysis.RulesetsV2.Model;
using CS2DemoKit.Parser;
using CS2DemoKit.Parser.GameEvents;
using CS2DemoKit.TestSupport;

#endregion

namespace CS2DemoKit.Analysis.Tests.RoundFacts;

/// <summary>
///     The <c>grenade_released</c> view: <c>weapon_fire</c> baked to the six grenade weapons, with
///     its own measured rate, so a <c>per: player_event</c> table on it validates where one on the
///     bare <c>weapon_fire</c> does not, and produces one row per release: the <c>weapon_fire</c>
///     events a grenade walker indexes as releases, by tick and slot, on the sample on both paths
///     and on the reference demo.
/// </summary>
[Category("Integration")]
[NotInParallel]
public class GrenadeReleasedViewTests
{
    private static readonly string[] _grenadeWeapons =
    [
        "weapon_hegrenade", "weapon_flashbang", "weapon_smokegrenade", "weapon_molotov", "weapon_incgrenade", "weapon_decoy"
    ];

    private const string Rules = """
        ruleset: releases
        for: each_player
        stats:
          weapon:  { capture: event.Weapon, on: grenade_released, per: match }
          smokes:  { count: grenade_released, match: { weapon: weapon_smokegrenade }, per: match }
        show:
          tables:
            releases:
              per: player_event
              on: grenade_released
              columns:
                - { stat: weapon, label: weapon }
        """;

    [Test]
    public async Task ReleasesTable_HasOneRowPerGrenadeWeaponFire_OnBothPaths()
    {
        string path = DemoTestHelper.RequireDemo(DemoTestHelper.SampleDemoFileName);
        ParsedDemo demo = RoundFactsTestSupport.Sample();
        IReadOnlyList<RulesetDoc> docs = RoundFactsTestSupport.Load(Rules);
        await Assert.That(DemoAnalysis.ValidateRulesets(docs).Diagnostics).IsEmpty();

        foreach ((string label, AnalysisRun run) in RoundFactsCorpusTests.RunBothPaths(path, docs))
        {
            await AssertReleases(run, demo, label);
        }
    }

    [Test]
    public async Task ReleasesTable_MatchesTheWalkersReleaseCount_OnTheReferenceDemo()
    {
        string path = DemoTestHelper.RequireDemo(DemoTestHelper.ReferenceDemoFileName);
        ParsedDemo demo = DemoTestHelper.GetOrParse(path);
        AnalysisRun run = DemoAnalysis.Run(demo, RoundFactsTestSupport.Load(Rules), new AnalysisOptions { CaptureSnapshots = false });
        int releases = await AssertReleases(run, demo, "retained");
        await Assert.That(releases).IsGreaterThan(100);
    }

    // The walker's release index: every weapon_fire whose weapon is a grenade, keyed by tick and slot.
    private static async Task<int> AssertReleases(AnalysisRun run, ParsedDemo demo, string label)
    {
        await Assert.That(run.Build.ExcludedRulesets).IsEmpty()
            .Because($"{label}: {string.Join("; ", run.Build.ExcludedRulesets.SelectMany(e => e.Diagnostics))}");

        List<(int Tick, int Slot, string Weapon)> expected = [];
        int shots = 0;
        foreach (GameEvent ev in demo.AllGameEvents)
        {
            if (ev.Payload is not WeaponFireEvent fire)
            {
                continue;
            }

            shots++;
            if (_grenadeWeapons.Contains(fire.Weapon, StringComparer.Ordinal))
            {
                // An event row's tick is its frame's, on the frame clock.
                DemoFrame frame = demo.Frames[ev.FrameNumber];
                expected.Add((frame.GameTick ?? frame.ServerTick, fire.UserId, fire.Weapon));
            }
        }

        MetricTable table = RoundFactsTestSupport.Table(run, "releases", demo);
        List<(int Tick, int Slot, string Weapon)> actual = table.Rows.Select(r => (
            RoundFactsTestSupport.Dim(r, "tick"),
            RoundFactsTestSupport.Dim(r, "player_slot"),
            (string)r.Values["weapon"]!)).ToList();

        Console.WriteLine($"{label}: {expected.Count} grenade releases among {shots} weapon_fire events, {actual.Count} rows");
        await Assert.That(actual.Count).IsEqualTo(expected.Count).Because(label);
        await Assert.That(actual.OrderBy(r => r.Tick).ThenBy(r => r.Slot).ToList())
            .IsEquivalentTo(expected.OrderBy(r => r.Tick).ThenBy(r => r.Slot).ToList()).Because(label);
        await Assert.That(shots).IsGreaterThan(expected.Count).Because("the baked filter drops the bullets");
        return expected.Count;
    }
}
