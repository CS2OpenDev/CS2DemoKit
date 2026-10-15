#region

using CS2DemoKit.Analysis.Graphs;
using CS2DemoKit.Analysis.Output;
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

        // The configured tables included: the bare run projects them from what it recorded at the
        // round boundaries, the snapshot run from its rows, and they must agree.
        await Assert.That(snapshotDigest).Contains("[tables]");
        await Assert.That(bareDigest).IsEqualTo(snapshotDigest);
        await Assert.That(bareAgainDigest).IsEqualTo(bareDigest)
            .Because("a second evaluation over one build must not start from the first run's terminal state");

        // The scoreboard needs no snapshots either: the bare run reads it from its final nodes.
        PlayerGameStatsProjector scoreboard = new();
        string snapshotBoard = PlayerGameStatsProjectorTests.RenderTable(scoreboard.Project(snapshot, snapshot.Demo).Single());
        await Assert.That(PlayerGameStatsProjectorTests.RenderTable(scoreboard.Project(bare, bare.Demo).Single()))
            .IsEqualTo(snapshotBoard);
        await Assert.That(PlayerGameStatsProjectorTests.RenderTable(scoreboard.Project(bareAgain, bareAgain.Demo).Single()))
            .IsEqualTo(snapshotBoard);

        // The final tracked nodes the snapshot table saw are exactly the bare run's final nodes.
        await Assert.That(snapshot.Snapshots!.FinalTrackedNodes.Count).IsEqualTo(bare.FinalNodes.Count);
        await Assert.That(snapshot.FinalNodes.Count).IsEqualTo(snapshot.Snapshots.FinalTrackedNodes.Count);
    }

    /// <summary>
    ///     Event tables agree too: the bare run records a row per firing as it goes, the snapshot run
    ///     finds the same firings in its rows, and a second bare run over the build starts clean.
    /// </summary>
    [Test]
    public async Task BareScan_EventTables_MatchSnapshotRun_OnTheSameBuild()
    {
        string path = DemoTestHelper.RequireDemo(DemoTestHelper.SampleDemoFileName);
        ParsedDemo demo = DemoTestHelper.GetOrParse(path);
        RuleConfigLoadResult extra = YamlConfigLoader.LoadDocuments([("parity.rules.yaml", ForwardPathParityTests.TableRulesets)]);
        await Assert.That(extra.Errors.Count).IsEqualTo(0);
        BuildResult build = DemoAnalysis.Build(demo, [.. YamlConfigLoader.LoadShippedEmbedded().Rulesets, .. extra.Rulesets]);

        AnalysisRun bare = DemoAnalysis.Evaluate(demo, build, new AnalysisOptions { CaptureSnapshots = false });
        AnalysisRun snapshot = DemoAnalysis.Evaluate(demo, build, new AnalysisOptions { CaptureSnapshots = true });
        AnalysisRun bareAgain = DemoAnalysis.Evaluate(demo, build, new AnalysisOptions { CaptureSnapshots = false });

        MetricTable positions = snapshot.ProjectConfiguredOutputs().Single(t => t.Name == "parity_positions");
        await Assert.That(positions.Rows.Count).IsGreaterThan(0);
        await Assert.That(positions.ColumnClocks["tick"]).IsEqualTo(MetricTable.FrameClock);

        string expected = ForwardPathParityTests.RunDigest.Render(snapshot);
        await Assert.That(ForwardPathParityTests.RunDigest.Render(bare)).IsEqualTo(expected);
        await Assert.That(ForwardPathParityTests.RunDigest.Render(bareAgain)).IsEqualTo(expected);
    }

    // Same table name and on:, so the two sequence stats hash alike and share one node. Loaded
    // separately, since one load rejects the duplicate table name.
    private const string SharedSequenceRulesets = """
        ruleset: shared_seq_a
        for: each_player
        stats:
          kills:
            count: kill
            per: match
        show:
          tables:
            kill_log:
              per: player_event
              on: kill
              columns:
                - { stat: kills, label: K }
        ---
        ruleset: shared_seq_b
        for: each_player
        stats:
          deaths:
            count: death
            per: match
        show:
          tables:
            kill_log:
              per: player_event
              on: kill
              columns:
                - { stat: deaths, label: D }
        """;

    /// <summary>
    ///     Two event tables on one shared sequence node still get one row per firing each, every row
    ///     carrying that table's own columns, on both paths.
    /// </summary>
    [Test]
    public async Task BareScan_EventTablesSharingASequence_OneRowPerFiringEach()
    {
        string path = DemoTestHelper.RequireDemo(DemoTestHelper.SampleDemoFileName);
        ParsedDemo demo = DemoTestHelper.GetOrParse(path);
        string[] docs = SharedSequenceRulesets.Split("---\n");
        RuleConfigLoadResult a = YamlConfigLoader.LoadDocuments([("a.rules.yaml", docs[0])]);
        RuleConfigLoadResult b = YamlConfigLoader.LoadDocuments([("b.rules.yaml", docs[1])]);
        await Assert.That(string.Join("; ", [.. a.Errors, .. b.Errors])).IsEqualTo("");
        BuildResult build = DemoAnalysis.Build(demo,
            [.. YamlConfigLoader.LoadShippedEmbedded().Rulesets, .. a.Rulesets, .. b.Rulesets]);

        AnalysisRun bare = DemoAnalysis.Evaluate(demo, build, new AnalysisOptions { CaptureSnapshots = false });
        AnalysisRun snapshot = DemoAnalysis.Evaluate(demo, build, new AnalysisOptions { CaptureSnapshots = true });

        foreach (AnalysisRun run in (AnalysisRun[])[snapshot, bare])
        {
            List<MetricTable> logs = run.ProjectConfiguredOutputs().Where(t => t.Name == "kill_log").ToList();
            await Assert.That(logs.Count).IsEqualTo(2);
            await Assert.That(logs[0].Rows.Count).IsGreaterThan(0);
            await Assert.That(logs[1].Rows.Count).IsEqualTo(logs[0].Rows.Count);
            foreach (MetricTable log in logs)
            {
                await Assert.That(log.Rows.Count(r => r.Values.Values.All(v => v is null))).IsEqualTo(0)
                    .Because($"{log.ValueColumns.Single()} must be read on every row");
            }
        }

        await Assert.That(ForwardPathParityTests.RunDigest.Render(bare))
            .IsEqualTo(ForwardPathParityTests.RunDigest.Render(snapshot));
    }
}
