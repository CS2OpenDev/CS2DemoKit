#region

using CS2DemoKit.Analysis.Graphs;
using CS2DemoKit.Analysis.Yaml;
using CS2DemoKit.Parser;
using CS2DemoKit.TestSupport;

#endregion

namespace CS2DemoKit.Analysis.Tests;

/// <summary>
///     The bare scan and the snapshot run compute the same values; snapshots only add the
///     per-message rows. Both evaluations run over ONE build, so the second one also proves the
///     scanner starts each evaluation clean instead of from the prior run's terminal values.
/// </summary>
[NotInParallel]
[Category("Integration")]
public class BareScanParityTests
{
    [Test]
    public async Task BareScan_MatchesSnapshotRun_OnTheSameBuild()
    {
        string path = DemoTestHelper.RequireDemo(DemoTestHelper.SampleDemoFileName);
        ParsedDemo demo = DemoTestHelper.GetOrParse(path);
        RuleConfigLoadResult rules = YamlConfigLoader.LoadShippedEmbedded();
        BuildResult build = DemoAnalysis.Build(demo, rules.Rulesets);

        AnalysisRun bare = DemoAnalysis.Evaluate(demo, build, new AnalysisOptions { CaptureSnapshots = false });
        AnalysisRun snapshot = DemoAnalysis.Evaluate(demo, build, new AnalysisOptions { CaptureSnapshots = true });
        AnalysisRun bareAgain = DemoAnalysis.Evaluate(demo, build, new AnalysisOptions { CaptureSnapshots = false });

        await Assert.That(bare.Snapshots).IsNull();
        await Assert.That(snapshot.Snapshots).IsNotNull();
        await Assert.That(bare.Provenance.SnapshotsCaptured).IsFalse();
        await Assert.That(snapshot.Provenance.SnapshotsCaptured).IsTrue();

        string bareDigest = ForwardPathParityTests.RunDigest.Render(bare);
        string snapshotDigest = ForwardPathParityTests.RunDigest.Render(snapshot);
        string bareAgainDigest = ForwardPathParityTests.RunDigest.Render(bareAgain);

        // The snapshot rendering has a [tables] tail the bare one cannot; everything before it must match.
        int tables = snapshotDigest.IndexOf("[tables]", StringComparison.Ordinal);
        await Assert.That(tables).IsGreaterThan(0);
        await Assert.That(bareDigest).IsEqualTo(snapshotDigest[..tables]);
        await Assert.That(bareAgainDigest).IsEqualTo(bareDigest)
            .Because("a second evaluation over one build must not start from the first run's terminal state");

        // The final tracked nodes the snapshot table saw are exactly the bare run's final nodes.
        await Assert.That(snapshot.Snapshots!.FinalTrackedNodes.Count).IsEqualTo(bare.FinalNodes.Count);
        await Assert.That(snapshot.FinalNodes.Count).IsEqualTo(snapshot.Snapshots.FinalTrackedNodes.Count);
    }
}
