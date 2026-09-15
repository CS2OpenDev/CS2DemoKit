#region

using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Analysis.Graphs;
using CS2DemoKit.Analysis.Profiles;
using CS2DemoKit.Analysis.Yaml;
using CS2DemoKit.Parser;
using CS2DemoKit.TestSupport;

#endregion

namespace CS2DemoKit.Analysis.Tests;

/// <summary>
///     The sample demo is a tournament recording whose header classifies as matchmaking: only the
///     game-event vocabulary tells the two dialects apart. Both paths must reach the same profile,
///     the header-only fallback must be visible as such, and a forced wrong dialect must show up
///     in the run's dialect check instead of silently zeroing every round-end effect.
/// </summary>
[NotInParallel]
[Category("Integration")]
public class ProfileResolutionTests
{
    [Test]
    public async Task Sample_ResolvesToThePreRestartDialect_OnBothPaths()
    {
        string path = DemoTestHelper.RequireDemo(DemoTestHelper.SampleDemoFileName);
        ParsedDemo demo = DemoTestHelper.GetOrParse(path);

        await Assert.That(DemoAnalysis.ResolveProfile(demo).GetType()).IsEqualTo(typeof(Cs2GotvPreRestartProfile));

        using DemoReader reader = DemoReader.OpenFile(path);
        await Assert.That(DemoAnalysis.ResolveProfile(reader).GetType()).IsEqualTo(typeof(Cs2GotvPreRestartProfile));
        await Assert.That(reader.Started).IsFalse().Because("the probe is a rewind, not a read");

        BuildResult build = DemoAnalysis.Build(reader, []);
        await Assert.That(build.Profile.GetType()).IsEqualTo(typeof(Cs2GotvPreRestartProfile));
        await Assert.That(build.ProfileResolution).IsEqualTo(ProfileResolutionKind.HeaderAndVocabulary);
    }

    /// <summary>
    ///     A matchmaking demo fires <c>cs_pre_restart</c> before the first freeze and then both
    ///     markers per round; the early-stopping probe must still land on the plain GOTV profile,
    ///     the same one the retained demo resolves from every event it carries.
    /// </summary>
    [Test]
    public async Task MatchmakingDemo_ResolvesToGotv_OnBothPaths_WithTheEarlyProbe()
    {
        string path = DemoTestHelper.RequireDemo();
        ParsedDemo demo = DemoTestHelper.GetOrParse(path);
        await Assert.That(DemoAnalysis.ResolveProfile(demo).GetType()).IsEqualTo(typeof(Cs2GotvProfile));

        using DemoReader reader = DemoReader.OpenFile(path);
        await Assert.That(DemoAnalysis.ResolveProfile(reader).GetType()).IsEqualTo(typeof(Cs2GotvProfile));

        // The rule itself, on the marker sequence the corpus shows: a pre-freeze restart decides
        // nothing, an in-match one decides tournament only once the window passes without the
        // matchmaking marker, and that marker decides matchmaking at once.
        Func<string, int, bool> stop = DialectProbe.Stop();
        await Assert.That(stop("cs_pre_restart", 100)).IsFalse();
        await Assert.That(stop("round_freeze_end", 2000)).IsFalse();
        await Assert.That(stop("cs_pre_restart", 9000)).IsFalse();
        await Assert.That(stop("player_death", 9000 + DialectProbe.WindowTicks)).IsFalse();
        await Assert.That(stop("round_officially_ended", 9020)).IsTrue();

        Func<string, int, bool> tournament = DialectProbe.Stop();
        await Assert.That(tournament("cs_pre_restart", 100)).IsFalse();
        await Assert.That(tournament("player_death", 100 + 2 * DialectProbe.WindowTicks)).IsFalse();
        await Assert.That(tournament("round_freeze_end", 2000)).IsFalse();
        await Assert.That(tournament("cs_pre_restart", 9000)).IsFalse();
        await Assert.That(tournament("player_death", 9000 + DialectProbe.WindowTicks)).IsFalse();
        await Assert.That(tournament("player_death", 9001 + DialectProbe.WindowTicks)).IsTrue();
    }

    [Test]
    public async Task HeaderOnly_WhenTheProbeIsOff_AndItIsReported()
    {
        string path = DemoTestHelper.RequireDemo(DemoTestHelper.SampleDemoFileName);
        AnalysisOptions options = new() { ProbeDialect = false };

        using DemoReader reader = DemoReader.OpenFile(path);
        DemoSourceProfile profile = DemoAnalysis.ResolveProfile(reader, options);
        await Assert.That(profile.GetType()).IsEqualTo(typeof(Cs2GotvProfile));

        BuildResult build = DemoAnalysis.Build(reader, [], options);
        await Assert.That(build.ProfileResolution).IsEqualTo(ProfileResolutionKind.HeaderOnly);
    }

    [Test]
    public async Task Explicit_Wins_AndIsReported()
    {
        string path = DemoTestHelper.RequireDemo(DemoTestHelper.SampleDemoFileName);
        ParsedDemo demo = DemoTestHelper.GetOrParse(path);
        Cs2GotvProfile forced = new();

        BuildResult build = DemoAnalysis.Build(demo, [], new AnalysisOptions { Profile = forced });
        await Assert.That(build.Profile).IsSameReferenceAs(forced);
        await Assert.That(build.ProfileResolution).IsEqualTo(ProfileResolutionKind.Explicit);

        using DemoReader reader = DemoReader.OpenFile(path);
        await Assert.That(DemoAnalysis.ResolveProfile(reader, new AnalysisOptions { Profile = forced }))
            .IsSameReferenceAs(forced);
    }

    [Test]
    public async Task DialectCheck_FlagsAForcedWrongDialect()
    {
        string path = DemoTestHelper.RequireDemo(DemoTestHelper.SampleDemoFileName);
        RuleConfigLoadResult rules = YamlConfigLoader.LoadShippedEmbedded();

        AnalysisRun resolved = DemoAnalysis.Run(path, rules.Rulesets);
        await Assert.That(resolved.Provenance.ProfileResolution).IsEqualTo(ProfileResolutionKind.HeaderAndVocabulary);
        await Assert.That(resolved.Provenance.Dialect.CsPreRestartSeen).IsGreaterThan(0);
        await Assert.That(resolved.Provenance.Dialect.RoundOfficiallyEndedSeen).IsEqualTo(0);
        await Assert.That(resolved.Provenance.Dialect.BoundMarkerNeverSeen).IsFalse();

        AnalysisRun forced = DemoAnalysis.Run(path, rules.Rulesets, new AnalysisOptions { Profile = new Cs2GotvProfile() });
        await Assert.That(forced.Provenance.ProfileResolution).IsEqualTo(ProfileResolutionKind.Explicit);
        await Assert.That(forced.Provenance.Dialect.CsPreRestartSeen).IsEqualTo(resolved.Provenance.Dialect.CsPreRestartSeen);
        await Assert.That(forced.Provenance.Dialect.BoundMarkerNeverSeen).IsTrue();
    }
}
